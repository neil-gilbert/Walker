import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('comparison', Path(__file__).parents[1] / 'compare-benchmarks.py')
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)


class ComparisonTests(unittest.TestCase):
    def setUp(self):
        self.workspace = tempfile.TemporaryDirectory()
        self.addCleanup(self.workspace.cleanup)
        self.root = Path(self.workspace.name)
        for label, mean in [('base', 2_000_000), ('head', 1_000_000)]:
            directory = self.root / label / 'results'
            directory.mkdir(parents=True)
            (directory / 'Fixture-report-full-compressed.json').write_text(json.dumps({
                'Benchmarks': [{'Type': 'Fixture', 'Method': 'Select', 'Parameters': 'Count=100',
                                'Statistics': {'Mean': mean},
                                'Memory': {'BytesAllocatedPerOperation': 64}}]}))
            (self.root / f'{label}-sha.txt').write_text(label)
        (self.root / 'end-to-end').mkdir()
        (self.root / 'end-to-end/summary.json').write_text(json.dumps({
            'before': {'medianWallMs': 200}, 'walker': {'medianWallMs': 250},
            'environment': {'repetitions': 3}}))

    def test_reports_correct_units_and_direction(self):
        report = comparison.compare(self.root)
        self.assertIn('| 2.0000 | 1.0000 | -50.0% | 64 | 64 |', report)
        self.assertIn('| 200 | 250 | +25.0% |', report)

    def test_rejects_non_equivalent_cases(self):
        path = self.root / 'head/results/Fixture-report-full-compressed.json'
        content = json.loads(path.read_text())
        content['Benchmarks'][0]['Parameters'] = 'Count=1000'
        path.write_text(json.dumps(content))
        with self.assertRaisesRegex(ValueError, 'cases differ'):
            comparison.compare(self.root)

    def test_rejects_failed_measurement(self):
        path = self.root / 'head/results/Fixture-report-full-compressed.json'
        content = json.loads(path.read_text())
        content['Benchmarks'][0]['Statistics'] = None
        path.write_text(json.dumps(content))
        with self.assertRaisesRegex(ValueError, 'invalid measurements'):
            comparison.compare(self.root)

    def test_rejects_missing_report(self):
        with self.assertRaisesRegex(ValueError, 'No BenchmarkDotNet'):
            comparison.load_benchmarks(self.root / 'missing')


if __name__ == '__main__':
    unittest.main()
