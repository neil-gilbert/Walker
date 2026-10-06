#!/usr/bin/env python3
"""Compare BenchmarkDotNet JSON exports and paired Walker verification runs."""
import argparse
import json
import math
from pathlib import Path


def load_benchmarks(directory):
    reports = sorted((directory / 'results').glob('*-report-full-compressed.json'))
    if not reports:
        raise ValueError(f'No BenchmarkDotNet JSON reports in {directory}')
    results = {}
    for report in reports:
        for benchmark in json.loads(report.read_text())['Benchmarks']:
            key = (benchmark['Type'], benchmark['Method'], benchmark['Parameters'])
            stats = benchmark.get('Statistics')
            if not stats or not math.isfinite(stats['Mean']) or stats['Mean'] <= 0:
                raise ValueError(f'Missing or invalid measurements for {key}')
            if key in results:
                raise ValueError(f'Duplicate benchmark: {key}')
            results[key] = (stats['Mean'], benchmark.get('Memory', {}).get('BytesAllocatedPerOperation'))
    if not results:
        raise ValueError('No measured benchmarks')
    return results


def delta(before, after):
    return f'{(after / before - 1) * 100:+.1f}%' if before else 'n/a'


def compare(root):
    before = load_benchmarks(root / 'base')
    after = load_benchmarks(root / 'head')
    if before.keys() != after.keys():
        raise ValueError('Benchmark cases differ between base and head')
    lines = ['## Performance comparison', '',
             f"Base: `{(root / 'base-sha.txt').read_text().strip()}`  ",
             f"Head: `{(root / 'head-sha.txt').read_text().strip()}`", '',
             'Negative time changes indicate faster execution. Hosted-runner timings are informational; rerun small differences.', '',
             '| Benchmark | Base mean (ms) | Head mean (ms) | Time change | Base bytes/op | Head bytes/op |',
             '| --- | ---: | ---: | ---: | ---: | ---: |']
    for key in sorted(before):
        base_time, base_bytes = before[key]
        head_time, head_bytes = after[key]
        label = f'{key[0]}.{key[1]} ({key[2]})'.replace('|', '\\|')
        lines.append(f'| {label} | {base_time / 1e6:.4f} | {head_time / 1e6:.4f} | '
                     f'{delta(base_time, head_time)} | {base_bytes} | {head_bytes} |')
    e2e = json.loads((root / 'end-to-end/summary.json').read_text())
    base_time = e2e['before']['medianWallMs']
    head_time = e2e['walker']['medianWallMs']
    lines.extend(['', '### Completed verification', '',
                  f'Payments fixture, {e2e["environment"]["repetitions"]} paired runs, equivalent mutant IDs and outcomes.', '',
                  '| Metric | Base | Head | Change |', '| --- | ---: | ---: | ---: |',
                  f'| Median wall time (ms) | {base_time} | {head_time} | {delta(base_time, head_time)} |', '',
                  'Full reports, environment details and individual runs are in the benchmark artifact.'])
    return '\n'.join(lines) + '\n'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('artifacts', type=Path)
    parser.add_argument('--summary', type=Path, required=True)
    args = parser.parse_args()
    try:
        report = compare(args.artifacts)
    except (ValueError, KeyError, OSError, TypeError) as error:
        with args.summary.open('a') as summary:
            summary.write(f'## Performance comparison unavailable\n\n{error}\n')
        raise SystemExit(str(error))
    with args.summary.open('a') as summary:
        summary.write(report)
    print(report)
