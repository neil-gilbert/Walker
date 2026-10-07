#!/usr/bin/env python3
"""Measure Walker against a pinned, real FluentValidation production diff."""
import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import platform
import signal
import shutil
import statistics
import subprocess
import sys
import tempfile
import time
from benchmark_commands import walker_command
from benchmark_memory import ProcessTreeMemory

URL = 'https://github.com/FluentValidation/FluentValidation.git'
COMMIT = 'f0d51d2109b04aa35f0119a715ef6ab413765263'
PROJECT = 'src/FluentValidation/FluentValidation.csproj'
TESTS = 'src/FluentValidation.Tests/FluentValidation.Tests.csproj'
FILTER = 'FullyQualifiedName~FluentValidation.Tests.ScalePrecisionValidatorTests'


def run(command, cwd, stdout, stderr, timeout, measure_memory=False):
    start = time.perf_counter()
    interrupted = False
    with stdout.open('w') as out, stderr.open('w') as err:
        with subprocess.Popen(command, cwd=cwd, stdout=out, stderr=err,
                              start_new_session=os.name == 'posix') as process, ProcessTreeMemory(process.pid, measure_memory) as memory:
            try:
                process.wait(timeout=timeout)
            except (subprocess.TimeoutExpired, KeyboardInterrupt):
                interrupted = True
                # Walker gets a chance to stop its children and restore source.
                if os.name == 'posix':
                    os.killpg(process.pid, signal.SIGINT)
                else:
                    process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    if os.name == 'posix':
                        os.killpg(process.pid, signal.SIGKILL)
                    else:
                        process.kill()
                    process.wait()
    return {'exitCode': process.returncode,
            'wallMs': round((time.perf_counter() - start) * 1000),
            'interrupted': interrupted, 'memory': memory.report}


def git(work, *arguments):
    return subprocess.check_output(['git', *arguments], cwd=work).decode().strip()


@contextmanager
def workspace():
    directory = Path(tempfile.mkdtemp(prefix='walker-real-world-'))
    try:
        yield directory
    except BaseException:
        print('Benchmark workspace retained for inspection: ' + str(directory), file=sys.stderr)
        raise
    else:
        shutil.rmtree(directory)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=Path('artifacts/real-world-benchmark'))
    parser.add_argument('--cli', type=Path, help='Defaults to the Release build of Walker')
    parser.add_argument('--compare-cli', type=Path, help='Preserved CLI output directory DLL for paired comparisons')
    parser.add_argument('--walker-dotnet', type=Path, help='Host Walker with this installation; target builds/tests still use dotnet on PATH')
    parser.add_argument('--walker-apphost', action='store_true', help='Use each CLI snapshot apphost and --walker-dotnet runtime; avoids executable-directory SDK lookup on macOS')
    parser.add_argument('--walker-arg', action='append', default=[], help='Candidate-only argument; use --walker-arg=--compiled-tests')
    parser.add_argument('--compare-walker-arg', action='append', default=[], help='Control-only argument; repeat for mode/worker comparisons')
    parser.add_argument('--measure-memory', action='store_true', help='Sample process-tree RSS for both Walker labels')
    parser.add_argument('--checkout', type=Path, help='Reuse a clean checkout at the pinned commit')
    parser.add_argument('--repetitions', type=int, default=3)
    parser.add_argument('--timeout', type=int, default=120)
    parser.add_argument('--max-mutants', type=int, default=20)
    parser.add_argument('--profiles', nargs='+', choices=['full', 'focused'], default=['full', 'focused'])
    args = parser.parse_args()
    if args.walker_apphost:
        if not args.walker_dotnet:
            parser.error('--walker-apphost requires --walker-dotnet to locate its runtime')
        os.environ['DOTNET_ROOT'] = str(args.walker_dotnet.resolve().parent)
    if min(args.repetitions, args.timeout, args.max_mutants) < 1:
        parser.error('repetitions, timeout and max-mutants must be positive')
    repo = Path(__file__).resolve().parents[1]
    cli = (args.cli or repo / 'src/Walker.Cli/bin/Release/net10.0/Walker.Cli.dll').resolve()
    if not cli.is_file():
        parser.error('Build Walker first: dotnet build src/Walker.Cli -c Release')
    executables = [('walker', cli)]
    if args.compare_cli:
        before = args.compare_cli.resolve()
        if not before.is_file():
            parser.error('--compare-cli DLL does not exist')
        executables.insert(0, ('before', before))
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    summary = {'repository': URL, 'commit': COMMIT, 'base': None,
               'project': PROJECT, 'tests': TESTS,
               'environment': {'platform': platform.platform(), 'machine': platform.machine(),
                               'walkerDotnet': str(args.walker_dotnet.resolve()) if args.walker_dotnet else 'dotnet on PATH',
                               'walkerApphost': args.walker_apphost,
                               'runtimeRollForward': os.environ.get('DOTNET_ROLL_FORWARD'),
                               'repetitions': args.repetitions, 'restoreAndBaselineWarmed': True},
               'timeoutSeconds': args.timeout, 'maxMutants': args.max_mutants,
               'setup': {}, 'profiles': {}, 'walkerArguments': args.walker_arg, 'compareWalkerArguments': args.compare_walker_arg,
               'cliHashes': {label: {assembly.name: hashlib.sha256(assembly.read_bytes()).hexdigest()
                                    for assembly in path.parent.glob('Walker.*.dll')} for label, path in executables}}
    # A supplied checkout is never reset, cleaned, staged or committed.
    with workspace() as temporary:
        source = args.checkout.resolve() if args.checkout else temporary / 'source'
        if not args.checkout:
            source.mkdir()
            for name, command in [
                ('init', ['git', 'init', str(source)]),
                ('fetch', ['git', 'fetch', '--depth', '2', URL, COMMIT]),
                ('checkout', ['git', 'checkout', '--detach', 'FETCH_HEAD'])
            ]:
                result = run(command, source, output / (name + '.log'), output / (name + '.stderr.log'), 180)
                summary['setup'][name] = result
                if result['exitCode'] != 0:
                    raise SystemExit('Setup failed; inspect ' + str(output / (name + '.stderr.log')))
        if git(source, 'rev-parse', 'HEAD') != COMMIT or git(source, 'status', '--porcelain'):
            raise SystemExit('Benchmark requires a clean checkout at ' + COMMIT)
        summary['environment']['sdk'] = subprocess.check_output(['dotnet', '--version'], cwd=source).decode().strip()
        summary['base'] = git(source, 'rev-parse', 'HEAD~1')
        sources = [source / p for p in git(source, 'ls-files', '*.cs').splitlines()]
        original = {p: hashlib.sha256(p.read_bytes()).hexdigest() for p in sources}
        production = [p for p in sources if p.is_relative_to(source / 'src/FluentValidation')]
        summary['size'] = {'trackedCSharpFiles': len(sources), 'coreCSharpFiles': len(production),
                           'coreLines': sum(len(p.read_bytes().splitlines()) for p in production)}
        expected = {}
        for repetition in range(1, args.repetitions + 1):
            # Alternate scope order to reduce compiler-server/order bias.
            profiles = args.profiles if repetition % 2 else list(reversed(args.profiles))
            for profile in profiles:
                for label, executable in executables if repetition % 2 else reversed(executables):
                    stem = f'{profile}-{repetition}' if label == 'walker' else f'before-{profile}-{repetition}'
                    work = temporary / stem
                    setup = run(['git', 'worktree', 'add', '--detach', str(work), COMMIT], source,
                                output / (stem + '.worktree.log'), output / (stem + '.worktree.stderr.log'), 60)
                    if setup['exitCode'] != 0:
                        raise SystemExit('Could not create isolated worktree: ' + str(work))
                    warm = run(['dotnet', 'test', TESTS, '--nologo', '--verbosity', 'minimal'], work,
                               output / (stem + '.setup.log'), output / (stem + '.setup.stderr.log'), 180)
                    if warm['exitCode'] != 0:
                        raise SystemExit('Ordinary tests failed; inspect ' + str(output / (stem + '.setup.log')))
                    arguments = ['verify', '--base', summary['base'],
                           '--project', PROJECT, '--tests', TESTS, '--timeout', str(args.timeout),
                           '--max-mutants', str(args.max_mutants), '--format', 'json']
                    if profile == 'focused':
                        arguments += ['--filter', FILTER]
                    command = walker_command(label, executable, arguments, args.walker_arg,
                                             args.walker_dotnet.resolve() if args.walker_dotnet else 'dotnet', args.walker_apphost, args.compare_walker_arg)
                    result = run(command, work, output / (stem + '.json'), output / (stem + '.stderr.log'), args.timeout + 10, args.measure_memory)
                    result['setup'] = {'worktree': setup, 'buildAndTests': warm}
                    result['sourceRestored'] = all((work / p.relative_to(source)).exists()
                        and hashlib.sha256((work / p.relative_to(source)).read_bytes()).hexdigest() == h for p, h in original.items())
                    result['sourceCheckoutUnchanged'] = all(p.exists() and hashlib.sha256(p.read_bytes()).hexdigest() == h
                                                           for p, h in original.items()) and not git(source, 'status', '--porcelain')
                    if not result['sourceRestored'] or not result['sourceCheckoutUnchanged'] or git(work, 'diff', 'HEAD'):
                        raise SystemExit('Source restoration failed in ' + str(work))
                    report = json.loads((output / (stem + '.json')).read_text())
                    if report['schemaVersion'] != 1:
                        raise SystemExit('Unsupported Walker report schema')
                    result.update({key: report[key] for key in [
                        'status', 'mutantsDiscovered', 'mutantsSelected', 'mutantsExecuted', 'killed',
                        'survived', 'hung', 'compileErrors', 'testErrors', 'timedOut', 'skipped',
                        'timings', 'unresolvedArithmetic', 'unresolvedBoolean', 'error']})
                    result['mutationBuildMs'] = sum(r['buildMs'] for r in report['results'])
                    result['workersUsed'] = report.get('workersUsed', 1)
                    result['mutationTestMs'] = sum(r['testMs'] for r in report['results'])
                    result['preparation'] = report.get('preparation')
                    result['completeUnderBudget'] = (report['status'] in ['passed', 'failed']
                                                     and not result['interrupted']
                                                     and result['wallMs'] < args.timeout * 1000)
                    outcomes = sorted((r['mutant']['id'], r['outcome']) for r in report['results'])
                    if profile not in expected:
                        expected[profile] = outcomes
                    result['sameOutcomesAsFirstRun'] = outcomes == expected[profile]
                    group = summary['profiles'].setdefault(profile, {'filter': report['testFilter'], 'runs': []})
                    run_key = 'runs' if label == 'walker' else 'beforeRuns'
                    median_key = 'medianWallMs' if label == 'walker' else 'beforeMedianWallMs'
                    group.setdefault(run_key, []).append(result)
                    group[median_key] = statistics.median(r['wallMs'] for r in group[run_key])
                    if args.measure_memory:
                        peaks = [r['memory']['peakTreeRssBytes'] for r in group[run_key] if r['memory']['peakTreeRssBytes'] is not None]
                        group['medianPeakTreeRssBytes' if label == 'walker' else 'beforeMedianPeakTreeRssBytes'] = statistics.median(peaks) if peaks else None
                    if 'medianWallMs' in group and 'beforeMedianWallMs' in group:
                        group['improvementPercent'] = round((1 - group['medianWallMs'] / group['beforeMedianWallMs']) * 100, 1)
                    (output / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
                    print(f"{stem}: {result['wallMs']/1000:.3f}s; {report['status']}; "
                          f"{report['mutantsExecuted']}/{report['mutantsSelected']} executed", flush=True)
                    cleanup = run(['git', 'worktree', 'remove', str(work)], source,
                                  output / (stem + '.cleanup.log'), output / (stem + '.cleanup.stderr.log'), 60)
                    if cleanup['exitCode'] != 0:
                        raise SystemExit('Could not remove disposable worktree; retained at ' + str(work))
                    if result['interrupted']:
                        raise SystemExit('Benchmark interrupted; evidence saved, no further runs started')
                    if args.compare_cli and (report['status'] not in ['passed', 'failed'] or not result['sameOutcomesAsFirstRun']):
                        raise SystemExit('Comparison rejected: incomplete/error report or different mutant IDs/outcomes')
        print('Evidence: ' + str(output / 'summary.json'))


if __name__ == '__main__':
    main()
