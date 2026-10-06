#!/usr/bin/env python3
"""Run a small reproducible comparison; no user working tree is modified."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import statistics
import platform
import os
import signal
import sys

parser = argparse.ArgumentParser()
parser.add_argument('--stryker', default='dotnet-stryker', help='Path to an installed Stryker.NET tool')
parser.add_argument('--timeout', type=int, default=120, help='Per-tool wall-time safety limit')
parser.add_argument('--output', default='artifacts/benchmark')
parser.add_argument('--compare-cli', type=Path, help='Optional pre-change CLI DLL for paired measurements')
parser.add_argument('--repetitions', type=int, default=1)
parser.add_argument('--skip-stryker', action='store_true')
parser.add_argument('--sample-text', action='store_true', help='Also capture a real human-readable sample run')
args = parser.parse_args()
if args.repetitions < 1 or args.timeout < 1: parser.error('repetitions and timeout must be positive')
if args.compare_cli: args.compare_cli = args.compare_cli.resolve()
repo = Path(__file__).resolve().parents[1]
output = Path(args.output).resolve()
output.mkdir(parents=True, exist_ok=True)
cli = repo / 'src/Walker.Cli/bin/Debug/net8.0/Walker.Cli.dll'
if not cli.exists():
    raise SystemExit('Build the verifier first: dotnet build Walker.sln')

def run(command, cwd, timeout=None):
    start = time.perf_counter()
    # Keep timed-out benchmark processes inside their disposable workspace and
    # give Walker an opportunity to cancel gracefully and restore source.
    with subprocess.Popen(command, cwd=cwd, text=True, stdout=subprocess.PIPE,
                          stderr=subprocess.STDOUT, start_new_session=os.name == 'posix') as process:
        try:
            text, _ = process.communicate(timeout=timeout)
            return process.returncode, text, round((time.perf_counter() - start) * 1000)
        except subprocess.TimeoutExpired:
            if os.name == 'posix': os.killpg(process.pid, signal.SIGINT)
            else: process.terminate()
            try:
                text, _ = process.communicate(timeout=15)
            except subprocess.TimeoutExpired:
                if os.name == 'posix': os.killpg(process.pid, signal.SIGKILL)
                else: process.kill()
                text, _ = process.communicate()
            return 3, text, round((time.perf_counter() - start) * 1000)

with tempfile.TemporaryDirectory(prefix='walker-benchmark-') as directory:
    work = Path(directory)
    shutil.copytree(repo / 'examples/Payments', work, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns('bin', 'obj', 'TestResults', 'stryker-output', 'StrykerOutput'))
    shutil.copy(repo / 'Directory.Build.props', work)
    (work / '.gitignore').write_text('bin/\nobj/\nTestResults/\nstryker-output/\n')
    source = work / 'Payments/PaymentService.cs'
    original = source.read_text()
    baseline = original.replace('balance >= price', 'balance > price').replace('active && approved', 'active || approved')
    baseline = baseline.replace('customer is not null', 'customer is null').replace('amount * 2', 'amount + 2')
    source.write_text(baseline)
    for command in [ ['git', 'init'], ['git', 'config', 'user.email', 'benchmark@example.invalid'],
                     ['git', 'config', 'user.name', 'Benchmark'], ['git', 'add', '.'], ['git', 'commit', '-m', 'before'] ]:
        code, text, _ = run(command, work)
        if code != 0: raise SystemExit(text)
    source.write_text(original)
    expected_source_bytes = source.read_bytes()
    for command in [['git', 'add', '.'], ['git', 'commit', '-m', 'agent change']]:
        code, text, _ = run(command, work)
        if code != 0: raise SystemExit(text)
    # Warm restores and validate baseline before either timing measurement.
    code, text, _ = run(['dotnet', 'test', 'Payments.Tests/Payments.Tests.csproj', '--nologo', '-p:UseSharedCompilation=false'], work, args.timeout)
    if code != 0: raise SystemExit(text)
    verify_args = ['verify', '--base', 'HEAD~1', '--project', 'Payments/Payments.csproj',
                   '--tests', 'Payments.Tests/Payments.Tests.csproj', '--max-mutants', '20',
                   '--timeout', str(args.timeout)]
    summary = {'fixture': 'Payments: four changed expressions; weak equality boundary and fee assertion',
               'environment': {'platform': platform.platform(), 'sdk': run(['dotnet', '--version'], work)[1].strip(),
                               'repetitions': args.repetitions, 'restoreAndBaselineWarmed': True},
               'walker': {'runs': []}}
    tools = [('walker', cli)]
    if args.compare_cli:
        tools.append(('before', args.compare_cli))
        summary['before'] = {'runs': []}
    expected_outcomes = None
    for repetition in range(args.repetitions):
        # Alternate order to reduce warm-cache/order bias. All runs share the same unchanged fixture.
        for label, executable in tools if repetition % 2 == 0 else reversed(tools):
            code, text, elapsed = run(['dotnet', str(executable), *verify_args, '--format', 'json'], work, args.timeout + 15)
            print(f'{label} run {repetition + 1}: {elapsed} ms (exit {code})', file=sys.stderr, flush=True)
            (output / f'{label}-{repetition + 1}.json').write_text(text)
            report = json.loads(text)
            outcomes = sorted((r['mutant']['id'], r['outcome']) for r in report['results'])
            if report['status'] not in ['failed', 'passed']: raise SystemExit(f'{label} incomplete or errored: {text}')
            if expected_outcomes is None: expected_outcomes = outcomes
            if outcomes != expected_outcomes: raise SystemExit('Outcomes or IDs differ between compared executions')
            summary[label]['runs'].append({'exitCode': code, 'wallMs': elapsed, 'status': report['status'],
                                          'mutantsExecuted': report['mutantsExecuted'], 'killed': report['killed'],
                                          'survived': report['survived'], 'timings': report['timings'],
                                          'buildMs': sum(r['buildMs'] for r in report['results']),
                                          'testMs': sum(r['testMs'] for r in report['results'])})
            summary[label]['survivors'] = report['survivors']
            if source.read_bytes() != expected_source_bytes: raise SystemExit('Verifier did not restore source')
    for label, _ in tools:
        summary[label]['medianWallMs'] = statistics.median(r['wallMs'] for r in summary[label]['runs'])
    if args.sample_text:
        code, text, _ = run(['dotnet', str(cli), *verify_args, '--format', 'text'], work, args.timeout + 15)
        (output / 'sample.txt').write_text(text)
        if code != 1: raise SystemExit('Sample should report surviving mutants')
    if args.skip_stryker:
        (output / 'summary.json').write_text(json.dumps(summary, indent=2))
        print(json.dumps(summary, indent=2))
        raise SystemExit(0)
    config = {'stryker-config': {'project': 'Payments.csproj', 'reporters': ['json'], 'concurrency': 1}}
    (work / 'Payments.Tests/stryker-config.json').write_text(json.dumps(config))
    code, text, elapsed = run([args.stryker], work / 'Payments.Tests', args.timeout)
    (output / 'stryker.log').write_text(text)
    summary['stryker'] = {'exitCode': code, 'wallMs': elapsed}
    reports = list((work / 'Payments.Tests').rglob('mutation-report.json'))
    if reports:
        stryker = json.loads(reports[-1].read_text())
        (output / 'stryker.json').write_text(json.dumps(stryker, indent=2))
        mutants = [m for file in stryker['files'].values() for m in file['mutants']]
        summary['stryker'].update({'mutantsExecuted': sum(m['status'] in ['Killed', 'Survived', 'Timeout', 'RuntimeError'] for m in mutants),
                                   'killed': sum(m['status'] == 'Killed' for m in mutants),
                                   'survived': sum(m['status'] == 'Survived' for m in mutants),
                                   'statuses': {status: sum(m['status'] == status for m in mutants) for status in sorted({m['status'] for m in mutants})},
                                   'survivors': [m for m in mutants if m['status'] == 'Survived']})
    else:
        summary['stryker']['error'] = 'No completed JSON report; inspect stryker.log. Do not infer performance from an incomplete run.'
    (output / 'summary.json').write_text(json.dumps(summary, indent=2))
    print(json.dumps(summary, indent=2))
