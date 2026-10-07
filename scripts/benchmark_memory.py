"""Optional sampled process-tree RSS for paired benchmarks (not an OS peak)."""
import os
import subprocess
import threading


def tree_rss(snapshot, root):
    rows = []
    for line in snapshot.splitlines():
        fields = line.split()
        if len(fields) == 3 and all(field.isdecimal() for field in fields):
            rows.append(tuple(map(int, fields)))
    descendants = {root}
    while True:
        children = {pid for pid, parent, _ in rows if parent in descendants}
        expanded = descendants | children
        if expanded == descendants:
            return sum(rss * 1024 for pid, _, rss in rows if pid in descendants)
        descendants = expanded


class ProcessTreeMemory:
    def __init__(self, pid, enabled=False):
        self.pid = pid
        self.enabled = enabled
        self.stop = threading.Event()
        self.thread = None
        self.report = {'peakTreeRssBytes': None, 'samples': 0,
                       'method': 'ps process-tree RSS sampled every 100ms; excludes reparented/shared daemons'} if enabled else None

    def __enter__(self):
        if self.enabled:
            self.thread = threading.Thread(target=self.sample, daemon=True)
            self.thread.start()
        return self

    def sample(self):
        try:
            if os.name != 'posix':
                raise RuntimeError('Process-tree RSS sampling requires POSIX ps')
            while not self.stop.is_set():
                snapshot = subprocess.check_output(['ps', '-axo', 'pid,ppid,rss'], text=True, timeout=2)
                rss = tree_rss(snapshot, self.pid)
                self.report['peakTreeRssBytes'] = max(self.report['peakTreeRssBytes'] or 0, rss)
                self.report['samples'] += 1
                self.stop.wait(0.1)
        except (OSError, RuntimeError, subprocess.SubprocessError) as error:
            self.report['error'] = str(error)

    def __exit__(self, *_):
        self.stop.set()
        if self.thread:
            self.thread.join(timeout=3)
