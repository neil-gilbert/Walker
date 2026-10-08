#!/usr/bin/env python3
"""Check the packed CLI's baseline observer from outside the source checkout."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape


def main():
    repo = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path, default=Path(shutil.which('dotnet') or 'dotnet'))
    parser.add_argument('--target-dotnet', type=Path, help='Separate dotnet installation for target builds/tests')
    parser.add_argument('--target-sdk', default='10.0.100', help='Minimum target SDK; rolls forward within its major version')
    parser.add_argument('--target-framework', default='net10.0', help='Target fixture framework')
    parser.add_argument('--compiled-tests', action='store_true', help='Also exercise verified DLL probes from the installed tool')
    parser.add_argument('--mutant-mode', choices=['source', 'switch'], default='source', help='Exercise compile-once switching through installed-tool configuration')
    parser.add_argument('--workers', type=int, choices=[1, 2], default=1, help='Exercise installed isolated worker execution')
    parser.add_argument('--isolate', action='store_true', help='Verify a native isolated snapshot')
    parser.add_argument('--focused-rerun', action='store_true', help='Add an equality assertion and rerun the reported survivor ID')
    parser.add_argument('--agent-workflows', action='store_true', help='Exercise progress, investigation and paired explicit-fault/test-patch verification')
    parser.add_argument('--evidence-directory', type=Path, help='Retain reports and fixture source hashes')
    args = parser.parse_args()
    if args.compiled_tests and args.mutant_mode == 'switch':
        parser.error('Switch mode includes DLL checks; omit --compiled-tests')
    if args.workers == 2 and args.mutant_mode != 'switch': parser.error('Two workers require switch mode')
    if args.agent_workflows and (not args.isolate or args.mutant_mode != 'source' or args.compiled_tests):
        parser.error('Agent workflow smoke requires --isolate and ordinary source mode')
    sdk = args.dotnet.resolve()
    target_sdk = args.target_dotnet.resolve() if args.target_dotnet else sdk
    env = dict(os.environ)
    env['DOTNET_ROOT'] = str(sdk.parent)
    env['PATH'] = str(sdk.parent) + os.pathsep + env['PATH']
    with tempfile.TemporaryDirectory(prefix='walker-packed-smoke-') as temporary:
        root = Path(temporary)
        installed = root / 'installed'
        with zipfile.ZipFile(args.package) as package:
            assert 'tools/net10.0/any/Walker.BuildLogger.dll' in package.namelist(), 'Logger missing from tool package'
            assert not any(name.endswith('/Microsoft.Build.Framework.dll') for name in package.namelist()), 'MSBuild must supply its own framework assembly'
            specification = ET.fromstring(package.read(next(name for name in package.namelist() if name.endswith('.nuspec'))))
            identity = next(e.text for e in specification.iter() if e.tag.split('}')[-1] == 'id')
            version = next(e.text for e in specification.iter() if e.tag.split('}')[-1] == 'version')
        config = root / 'tool-install.config'
        config.write_text('<configuration><packageSources><clear /><add key="local" value="'
                          + escape(str(args.package.resolve().parent), {'"': '&quot;'}) + '" /></packageSources></configuration>')
        install_env = dict(env)
        install_env['NUGET_PACKAGES'] = str(root / 'packages')
        installation = subprocess.run([str(sdk), 'tool', 'install', identity, '--version', version,
            '--tool-path', str(installed), '--configfile', str(config), '--no-cache'],
            env=install_env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=120)
        assert installation.returncode == 0, installation.stdout
        assert list(installed.rglob('Walker.BuildLogger.dll')), 'Installed tool lost its observer'
        env['PATH'] = str(target_sdk.parent) + os.pathsep + env['PATH']
        trace = root / 'commands.jsonl'
        # The public executor launches dotnet by name. Trace its commands while forwarding to the
        # chosen SDK so the package must prove that it removed both metadata-only builds.
        if os.name == 'posix':
            wrapper = root / 'wrapper'
            wrapper.mkdir()
            shim = wrapper / 'dotnet'
            shim.write_text('#!/usr/bin/env python3\nimport json, os, sys\n'
                            f'with open({str(trace)!r}, "a") as output: output.write(json.dumps(sys.argv[1:])+"\\n")\n'
                            f'os.execv({str(target_sdk)!r}, [{str(target_sdk)!r}, *sys.argv[1:]])\n')
            shim.chmod(0o755)
            env['PATH'] = str(wrapper) + os.pathsep + env['PATH']
        work = root / 'fixture'
        shutil.copytree(repo / 'examples/Payments', work,
                        ignore=shutil.ignore_patterns('bin', 'obj', 'TestResults'))
        properties = (repo / 'Directory.Build.props').read_text().replace('net10.0', args.target_framework)
        (work / 'Directory.Build.props').write_text(properties)
        (work / 'global.json').write_text(json.dumps({'sdk': {
            'version': args.target_sdk, 'rollForward': 'latestFeature', 'allowPrerelease': False}}))
        project = work / 'Payments.Tests/Payments.Tests.csproj'
        project.write_text(project.read_text().replace('<PropertyGroup>',
            '<PropertyGroup><TargetFramework></TargetFramework>'
            f'<TargetFrameworks>{args.target_framework};{args.target_framework}-windows</TargetFrameworks>', 1))
        source = work / 'Payments/PaymentService.cs'
        if args.mutant_mode == 'switch':
            # This smoke checks a supported boundary; the normal benchmark retains decimal fallback.
            source.write_text(source.read_text().replace('decimal balance, decimal price', 'int balance, int price'))
            if args.workers == 2:
                source.write_text(source.read_text().replace('    public bool CanAuthorise',
                    '    public bool AnotherBoundary(int balance, int price) => balance >= price;\n    public bool CanAuthorise'))
        if args.compiled_tests:
            (work / 'walker.json').write_text('{"compiledTests": true}')
        elif args.mutant_mode == 'switch':
            (work / 'walker.json').write_text(json.dumps({'mutantMode': 'switch', 'workers': args.workers}))
        original = source.read_bytes()
        source.write_text(source.read_text().replace('balance >= price', 'balance > price'))
        (work / '.gitignore').write_text('bin/\nobj/\n')

        def run(*command):
            return subprocess.run(command, cwd=work, env=env, text=True, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, timeout=150)

        for command in [('git', 'init'), ('git', 'config', 'user.email', 'test@example.invalid'),
                        ('git', 'config', 'user.name', 'Test'), ('git', 'add', '.'),
                        ('git', 'commit', '-m', 'before')]:
            result = run(*command)
            assert result.returncode == 0, result.stdout
        source.write_bytes(original)
        for command in [('git', 'add', '.'), ('git', 'commit', '-m', 'after')]:
            result = run(*command)
            assert result.returncode == 0, result.stdout
        executable = str(installed / ('walker.exe' if os.name == 'nt' else 'walker'))
        isolation_flags = ['--isolate'] if args.isolate else []
        result = run(executable, 'verify', *isolation_flags, *(['--progress', '--investigate'] if args.agent_workflows else []), '--base', 'HEAD~1',
                     '--project', 'Payments/Payments.csproj', '--tests', 'Payments.Tests/Payments.Tests.csproj',
                     '--max-mutants', str(args.workers), '--timeout', '120', '--format', 'json')
        assert result.returncode == 1, result.stdout + result.stderr
        report = json.loads(result.stdout)
        assert report['mutantsExecuted'] == args.workers and report['survived'] == args.workers, report
        assert report['workersRequested'] == args.workers and report['workersUsed'] == args.workers, report
        assert source.read_bytes() == original, 'Source bytes were not restored'
        if args.isolate:
            assert report['isolation']['cleanupState'] == 'removed', report
            assert Path(report['isolation']['reportPath']).is_file(), report
            assert not Path(report['isolation']['worktreePath']).exists(), report
        if args.mutant_mode == 'switch':
            assert report['preparation']['supported'] == args.workers and report['preparation']['fallback'] == 0, report
            assert all(r['buildMs'] == 0 for r in report['results']), report
        if os.name == 'posix':
            commands = [json.loads(line) for line in trace.read_text().splitlines()]
            assert sum(command[0] == 'build' for command in commands) == (3 if args.mutant_mode == 'switch' else 2), commands
            assert sum(any(arg.startswith('-logger:') for arg in command) for command in commands) == 1, commands
            if args.compiled_tests:
                assert sum(command[0] == 'test' and command[1].endswith('.dll') for command in commands) == 2, commands
                assert sum(command[0] == 'msbuild' and any('TargetPath' in arg for arg in command) for command in commands) == 2, commands
            elif args.mutant_mode == 'switch':
                assert sum(command[0] == 'test' and command[1].endswith('.dll') for command in commands) == (14 if args.workers == 2 else 4), commands
        evidence = {'initial': report, 'sourceSha256': hashlib.sha256(original).hexdigest()}
        if args.agent_workflows:
            assert '[walker progress]' in result.stderr, result.stderr
            assert report['investigation']['gaps'][0]['mutantIds'] == [report['survivors'][0]['id']], report
            fault = report['survivors'][0]
            faults_path = root / 'faults.json'
            faults_path.write_text(json.dumps({'schemaVersion': 1, 'challenges': [{
                key: fault[key] for key in ('file', 'sourceHash', 'spanStart', 'original', 'replacement')
            } | {'concern': 'Rejecting exact balance', 'expectedBehaviour': 'Exact balance permits purchase'}]}))
            patch_path = root / 'tests.json'
            patch_path.write_text(json.dumps({'schemaVersion': 1, 'contract': 'Exact balance permits purchase', 'files': [{
                'file': 'Payments.Tests/BoundaryTests.cs', 'originalHash': None,
                'content': 'using Xunit; namespace Payments.Tests; public class BoundaryTests { [Fact] public void ExactBalance() => Assert.True(new PaymentService().CanPurchase(10, 10)); }'
            }]}))
            tests_before = (work / 'Payments.Tests/PaymentTests.cs').read_bytes()
            paired = run(executable, 'verify', '--isolate', '--progress', '--investigate', '--base', 'HEAD',
                '--project', 'Payments/Payments.csproj', '--tests', 'Payments.Tests/Payments.Tests.csproj',
                '--challenge', str(faults_path), '--test-patch', str(patch_path), '--max-mutants', '1', '--timeout', '120', '--format', 'json')
            assert paired.returncode == 0, paired.stdout + paired.stderr
            improvement = json.loads(paired.stdout)
            assert improvement['testImprovement']['status'] == 'verified', improvement
            assert improvement['testImprovement']['before']['survived'] == 1, improvement
            assert improvement['results'][0]['mutant']['operator'] == 'CustomFault', improvement
            assert improvement['results'][0]['killConfirmed'] is True, improvement
            assert improvement['isolation']['cleanupState'] == 'removed', improvement
            assert source.read_bytes() == original, 'Paired workflow changed production source'
            assert (work / 'Payments.Tests/PaymentTests.cs').read_bytes() == tests_before, 'Paired workflow changed original tests'
            assert not (work / 'Payments.Tests/BoundaryTests.cs').exists(), 'Proposed test escaped isolation'
            evidence['testImprovement'] = improvement
        if args.focused_rerun:
            assert args.workers == 1, 'Focused smoke expects one boundary survivor'
            tests = work / 'Payments.Tests/PaymentTests.cs'
            tests.write_text(tests.read_text().replace('public sealed class PaymentTests\n{',
                'public sealed class PaymentTests\n{\n    [Fact] public void EqualityCanPurchase() => Assert.True(new PaymentService().CanPurchase(10, 10));'))
            mutant_id = report['survivors'][0]['id']
            focused = run(executable, 'verify', *isolation_flags, '--base', 'HEAD~1',
                '--project', 'Payments/Payments.csproj', '--tests', 'Payments.Tests/Payments.Tests.csproj',
                '--mutant', mutant_id, '--mutant', mutant_id, '--max-mutants', '1', '--timeout', '120',
                '--confirm-kills', '--format', 'json')
            assert focused.returncode == 0, focused.stdout + focused.stderr
            rerun = json.loads(focused.stdout)
            assert rerun['selection'] == {'kind': 'explicit', 'requestedIds': [mutant_id]}, rerun
            assert rerun['mutantsExecuted'] == 1 and rerun['killed'] == 1, rerun
            assert rerun['results'][0]['killConfirmed'] is True, rerun
            assert source.read_bytes() == original, 'Focused rerun changed original source'
            if args.isolate: assert rerun['isolation']['cleanupState'] == 'removed', rerun
            evidence['focused'] = rerun
        if args.evidence_directory:
            args.evidence_directory.mkdir(parents=True, exist_ok=True)
            (args.evidence_directory / 'packed-agent-loop.json').write_text(json.dumps(evidence, indent=2))
        print('Packed CLI passed the two-framework baseline, observer deployment and source-restoration checks'
              + (' plus optional agent workflows.' if args.agent_workflows else '.'))


if __name__ == '__main__':
    main()
