import importlib.util
import os
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('commands', Path(__file__).parents[1] / 'benchmark_commands.py')
commands = importlib.util.module_from_spec(spec)
spec.loader.exec_module(commands)


class CommandTests(unittest.TestCase):
    def test_control_arguments_are_independent_from_candidate_arguments(self):
        candidate = ['--mutant-mode', 'switch', '--workers', '2']
        control = ['--mutant-mode', 'switch', '--workers', '1']
        self.assertEqual(['dotnet', 'Walker.dll', 'verify', *control],
                         commands.walker_command('before', 'Walker.dll', ['verify'], candidate, control_arguments=control))
        self.assertEqual(['dotnet', 'Walker.dll', 'verify', *candidate],
                         commands.walker_command('walker', 'Walker.dll', ['verify'], candidate, control_arguments=control))

    def test_candidate_switch_does_not_change_control_or_requested_scope(self):
        scope = ['verify', '--tests', 'Tests.csproj', '--filter', 'FullyQualifiedName~Boundary']
        extra = ['--mutant-mode', 'switch']
        self.assertEqual(['dotnet', 'Walker.dll', *scope],
                         commands.walker_command('before', Path('Walker.dll'), scope, extra))
        self.assertEqual(['dotnet', 'Walker.dll', *scope, *extra],
                         commands.walker_command('walker', Path('Walker.dll'), scope, extra))
        self.assertEqual(scope[-1], 'FullyQualifiedName~Boundary')
        self.assertEqual(extra, ['--mutant-mode', 'switch'])

    def test_separate_walker_host_applies_to_both_labels(self):
        for label in ['before', 'walker']:
            self.assertEqual(['/host/dotnet', 'Walker.dll', 'verify'],
                             commands.walker_command(label, 'Walker.dll', ['verify'], [], '/host/dotnet'))

    def test_apphost_uses_each_snapshot_without_passing_dll_as_an_argument(self):
        for label in ['before', 'walker']:
            self.assertEqual(['/snapshot/Walker.Cli' + ('.exe' if os.name == 'nt' else ''), 'verify'],
                             commands.walker_command(label, '/snapshot/Walker.Cli.dll', ['verify'], [], '/host/dotnet', True))


if __name__ == '__main__':
    unittest.main()
