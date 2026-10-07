#!/usr/bin/env python3
"""Compare completed Walker/Stryker reports in separate warmed disposable repos."""
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import signal
import statistics
import subprocess
import tempfile
import time

from benchmark_memory import ProcessTreeMemory


def run(command, cwd, env, timeout, measure_memory=False):
    start = time.perf_counter()
    timed_out = False
    with subprocess.Popen(command, cwd=cwd, env=env, text=True,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          start_new_session=os.name == 'posix') as process, \
            ProcessTreeMemory(process.pid, measure_memory) as memory:
        try:
            output, _ = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            if os.name == 'posix':
                os.killpg(process.pid, signal.SIGINT)
            else:
                process.terminate()
            try:
                output, _ = process.communicate(timeout=15)
            except subprocess.TimeoutExpired:
                if os.name == 'posix':
                    os.killpg(process.pid, signal.SIGKILL)
                else:
                    process.kill()
                output, _ = process.communicate()
        wall_ms = round((time.perf_counter() - start) * 1000)
    return {'command': list(map(str, command)), 'exitCode': process.returncode,
            'wallMs': wall_ms, 'timedOut': timed_out, 'memory': memory.report}, output


def checked(command, cwd, env, timeout):
    result, output = run(command, cwd, env, timeout)
    if result['exitCode'] != 0 or result['timedOut']:
        raise RuntimeError(f'Setup failed: {command}\n{output}')
    return result, output


def source_hashes(work):
    return {str(path.relative_to(work)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(work.rglob('*.cs'))
            if not {'bin', 'obj', 'StrykerOutput', 'stryker-output'}.intersection(path.parts)}


def prepare(repo, work, fixture, env, timeout):
    payments = fixture == 'payments'
    shutil.copytree(repo / 'examples' / ('Payments' if payments else 'PreparedMutants'), work,
                    ignore=shutil.ignore_patterns('bin', 'obj', 'TestResults',
                                                 'StrykerOutput', 'stryker-output'))
    for name in ('Directory.Build.props', 'global.json'):
        shutil.copy(repo / name, work)
    (work / '.gitignore').write_text('bin/\nobj/\nTestResults/\nStrykerOutput/\n.walker.lock\n')
    tests = 'Payments.Tests/Payments.Tests.csproj' if payments else 'Tests/Tests.csproj'
    project = 'Payments/Payments.csproj' if payments else 'Code/Code.csproj'
    source = work / ('Payments/PaymentService.cs' if payments else 'Code/Rules.cs')
    if not payments:
        test_file = work / 'Tests/BoundaryTests.cs'
        # This Walker-specific lifecycle assertion is not production behaviour.
        # Remove it in BOTH copies so Stryker may use its normal host lifecycle.
        test_file.write_text(test_file.read_text().replace(
            '    private static int runs;\n'
            '    [Fact] public void FreshHost() => Assert.Equal(1, ++runs);\n', ''))
    config = {'stryker-config': {'project': Path(project).name,
                                'reporters': ['json']}}
    (work / Path(tests).parent / 'stryker-config.json').write_text(json.dumps(config, indent=2))
    original = source.read_text()
    baseline = original.replace('balance >= price', 'balance > price') \
        .replace('active && approved', 'active || approved') \
        .replace('customer is not null', 'customer is null') \
        .replace('amount * 2', 'amount + 2') \
        .replace('v >= 0', 'v > 0')
    source.write_text(baseline)
    for command in (['git', 'init', '-q'], ['git', 'config', 'user.email', 'benchmark@example.invalid'],
                    ['git', 'config', 'user.name', 'Benchmark'], ['git', 'add', '.'],
                    ['git', '-c', 'commit.gpgsign=false', 'commit', '-qm', 'before']):
        checked(command, work, env, timeout)
    source.write_text(original)
    for command in (['git', 'add', '.'],
                    ['git', '-c', 'commit.gpgsign=false', 'commit', '-qm', 'changed expressions']):
        checked(command, work, env, timeout)
    hashes = source_hashes(work)
    warm, output = checked([env['WALKER_BENCHMARK_DOTNET'], 'test', tests, '--nologo',
                            '-p:UseSharedCompilation=false'], work, env, timeout)
    return project, tests, hashes, warm, output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, default=Path(shutil.which('dotnet') or 'dotnet'))
    parser.add_argument('--cli', type=Path, required=True)
    parser.add_argument('--stryker', type=Path, required=True)
    parser.add_argument('--fixture', choices=['payments', 'boundaries'], default='payments')
    parser.add_argument('--mutant-mode', choices=['source', 'switch'], default='source')
    parser.add_argument('--workers', type=int, choices=[1, 2], default=1)
    parser.add_argument('--stryker-concurrency', type=int, default=1)
    parser.add_argument('--repetitions', type=int, default=3)
    parser.add_argument('--timeout', type=int, default=120)
    parser.add_argument('--measure-memory', action='store_true')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if min(args.repetitions, args.timeout, args.stryker_concurrency) < 1:
        parser.error('repetitions, timeout and concurrency must be positive')
    if args.workers == 2 and args.mutant_mode != 'switch':
        parser.error('two Walker workers require switch mode')
    repo = Path(__file__).resolve().parents[1]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    # Never accept stale reports or append a new benchmark to old results.
    if (output / 'summary.json').exists() or any(output.glob('pair-*')):
        parser.error('output already contains results; choose a fresh directory')
    dotnet, cli, stryker = (path.resolve() for path in (args.dotnet, args.cli, args.stryker))
    for path in (dotnet, cli, stryker):
        if not path.is_file():
            parser.error(f'Missing executable: {path}')
    env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent),
               PATH=str(dotnet.parent) + os.pathsep + os.environ.get('PATH', ''),
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1',
               WALKER_BENCHMARK_DOTNET=str(dotnet))
    summary = {'createdUtc': datetime.now(timezone.utc).isoformat(),
               'fixture': args.fixture, 'repetitions': args.repetitions,
               'policy': {'separateFreshReposPerToolAndPair': True,
                          'restoreAndBaselineWarmedOutsideTiming': True,
                          'toolBaselineAndPreparationIncluded': True,
                          'bothUseDiff': 'HEAD~1 (resolved to parent SHA for Stryker)',
                          'mutationSetsIdentical': False,
                          'lifecycleAssertionRemovedForBoth': args.fixture == 'boundaries',
                          'timeoutSeconds': args.timeout},
               'environment': {'platform': platform.platform(), 'processor': platform.processor(),
                               'logicalCpus': os.cpu_count(),
                               'sdk': checked([str(dotnet), '--version'], repo, env, 30)[1].strip(),
                               'runtimes': checked([str(dotnet), '--list-runtimes'], repo, env, 30)[1].strip(),
                               'walkerCommit': checked(['git', 'rev-parse', 'HEAD'], repo, env, 30)[1].strip(),
                               'walkerCliSha256': hashlib.sha256(cli.read_bytes()).hexdigest(),
                               'strykerTool': checked([str(dotnet), 'tool', 'list', '--tool-path',
                                                      str(stryker.parent)], repo, env, 30)[1].strip()},
               'walker': {'mutantMode': args.mutant_mode, 'workersRequested': args.workers, 'runs': []},
               'stryker': {'concurrency': args.stryker_concurrency, 'runs': []}}
    signatures = {}
    for repetition in range(1, args.repetitions + 1):
        order = ['walker', 'stryker'] if repetition % 2 else ['stryker', 'walker']
        pair_output = output / f'pair-{repetition}'
        pair_output.mkdir()
        with tempfile.TemporaryDirectory(prefix='walker-stryker-') as directory:
            prepared = {}
            completed_reports = {}
            for label in order:
                # macOS exposes /var through /private/var; Stryker canonicalises
                # report paths, so use canonical roots for both tools as well.
                work = Path(directory).resolve() / label
                prepared[label] = (work, *prepare(repo, work, args.fixture, env, args.timeout))
                (pair_output / f'{label}-warm.log').write_text(prepared[label][-1])
            if prepared['walker'][3] != prepared['stryker'][3]:
                raise RuntimeError('Tool copies have different source/test bytes')
            for label in order:
                work, project, tests, hashes, warm, _ = prepared[label]
                if label == 'walker':
                    command = [str(dotnet), str(cli), 'verify', '--base', 'HEAD~1',
                               '--project', project, '--tests', tests, '--max-mutants', '20',
                               '--timeout', str(args.timeout), '--mutant-mode', args.mutant_mode,
                               '--workers', str(args.workers), '--format', 'json']
                    cwd = work
                else:
                    parent = checked(['git', 'rev-parse', 'HEAD~1'], work, env, 30)[1].strip()
                    command = [str(stryker), f'--since:{parent}', '--concurrency',
                               str(args.stryker_concurrency), '--skip-version-check',
                               '--output', str(pair_output / 'stryker-output')]
                    cwd = work / Path(tests).parent
                result, text = run(command, cwd, env, args.timeout + 15, args.measure_memory)
                (pair_output / f'{label}.log').write_text(text)
                result.update({'orderInPair': order.index(label) + 1,
                               'warmSetup': warm, 'sourceHashes': hashes})
                if result['timedOut']:
                    raise RuntimeError(f'{label} timed out; inspect {pair_output}')
                if source_hashes(work) != hashes:
                    raise RuntimeError(f'{label} did not restore source/test bytes')
                if label == 'walker':
                    report = json.loads(text)
                    (pair_output / 'walker.json').write_text(json.dumps(report, indent=2))
                    expected = (4, 2, 2) if args.fixture == 'payments' else (20, 20, 0)
                    if report['status'] not in ('passed', 'failed') or result['exitCode'] != (1 if expected[2] else 0):
                        raise RuntimeError(f'Walker incomplete/errored: {pair_output}')
                    actual = tuple(report[key] for key in ('mutantsExecuted', 'killed', 'survived'))
                    if actual != expected:
                        raise RuntimeError(f'Walker outcomes {actual} != {expected}')
                    if args.fixture == 'boundaries' and args.mutant_mode == 'switch':
                        preparation = report.get('preparation') or {}
                        if (preparation.get('supported'), preparation.get('fallback'),
                                report.get('workersUsed')) != (20, 0, args.workers):
                            raise RuntimeError('Boundary switching/pool was not actually used')
                    result.update({key: report.get(key) for key in
                                   ('status', 'mutantsExecuted', 'killed', 'survived',
                                    'timings', 'preparation', 'workersUsed')})
                    signature = sorted((row['mutant']['id'], row['outcome']) for row in report['results'])
                else:
                    reports = list((pair_output / 'stryker-output').rglob('mutation-report.json'))
                    if result['exitCode'] != 0 or len(reports) != 1:
                        raise RuntimeError(f'Stryker incomplete/errored: {pair_output}')
                    report = json.loads(reports[0].read_text())
                    (pair_output / 'stryker.json').write_text(json.dumps(report, indent=2))
                    mutants = [m for file in report['files'].values() for m in file['mutants']]
                    statuses = dict(sorted(Counter(m['status'] for m in mutants).items()))
                    if not mutants or set(statuses) - {'Killed', 'Survived', 'NoCoverage', 'Ignored', 'CompileError'}:
                        raise RuntimeError(f'Stryker has incomplete/error outcomes: {statuses}')
                    result.update({'mutantsGenerated': len(mutants), 'statuses': statuses,
                                   'mutantsExecuted': sum(m['status'] in ('Killed', 'Survived') for m in mutants),
                                   'killed': statuses.get('Killed', 0), 'survived': statuses.get('Survived', 0)})
                    if result['mutantsExecuted'] == 0:
                        raise RuntimeError('Stryker executed no mutants')
                    signature = sorted((str(Path(file_name).relative_to(work)) if Path(file_name).is_absolute() else file_name,
                                        m['id'], m['status']) for file_name, file in report['files'].items()
                                       for m in file['mutants'])
                if label in signatures and signatures[label] != signature:
                    raise RuntimeError(f'{label} mutant IDs/outcomes changed between pairs')
                signatures[label] = signature
                completed_reports[label] = report
                summary[label]['runs'].append(result)
                print(f"{args.fixture} {label} pair {repetition}: {result['wallMs']} ms; "
                      f"{result['mutantsExecuted']} executed, {result['killed']} killed, "
                      f"{result['survived']} survived", flush=True)
                (output / 'summary.json').write_text(json.dumps(summary, indent=2))
            # IDs differ across tools: compare shared mutations by file, line and
            # replacement text. Do not silently present different behaviours as equal.
            stryker_work = prepared['stryker'][0]
            stryker_outcomes = {}
            for file_name, file in completed_reports['stryker']['files'].items():
                relative = str(Path(file_name).relative_to(stryker_work)) if Path(file_name).is_absolute() else file_name
                for mutant in file['mutants']:
                    key = (relative, mutant['location']['start']['line'], ' '.join(mutant['replacement'].split()))
                    stryker_outcomes[key] = mutant['status']
            common = []
            for row in completed_reports['walker']['results']:
                mutant = row['mutant']
                key = (mutant['file'], mutant['line'], ' '.join(mutant['replacement'].split()))
                if stryker_outcomes.get(key) != row['outcome']:
                    raise RuntimeError(f'Shared mutation missing or different in Stryker: {key}')
                common.append({'file': key[0], 'line': key[1], 'replacement': key[2], 'outcome': row['outcome']})
            (pair_output / 'common-mutations.json').write_text(json.dumps(common, indent=2))
            summary['commonMutations'] = common
    for label in ('walker', 'stryker'):
        walls = [result['wallMs'] for result in summary[label]['runs']]
        summary[label].update({'medianWallMs': statistics.median(walls), 'minWallMs': min(walls),
                               'maxWallMs': max(walls)})
    summary['walkerWallRelativeToStryker'] = summary['walker']['medianWallMs'] / summary['stryker']['medianWallMs']
    summary['completed'] = True
    (output / 'summary.json').write_text(json.dumps(summary, indent=2))
    print(f"Medians: Walker {summary['walker']['medianWallMs']} ms; "
          f"Stryker {summary['stryker']['medianWallMs']} ms", flush=True)


if __name__ == '__main__':
    main()
