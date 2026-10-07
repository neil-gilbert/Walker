import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('memory', Path(__file__).parents[1] / 'benchmark_memory.py')
memory = importlib.util.module_from_spec(spec)
spec.loader.exec_module(memory)


class MemoryTests(unittest.TestCase):
    def test_tree_sum_includes_descendants_and_excludes_unrelated_processes(self):
        snapshot = 'PID PPID RSS\n100 1 10\n101 100 20\n102 101 30\n103 1 1000\n'
        self.assertEqual(60 * 1024, memory.tree_rss(snapshot, 100))
        self.assertEqual(0, memory.tree_rss(snapshot, 999))


if __name__ == '__main__':
    unittest.main()
