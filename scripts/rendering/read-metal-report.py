#!/usr/bin/env python3
"""Summarize Apple's raw HUD frame intervals, never its cached FPS summary.

This is report analysis, not complete game acceptance. The report's encoder
tables do not provide a paired GPU timing ledger for every presented frame.
"""
import argparse
import hashlib
from html.parser import HTMLParser
import json
import math
from pathlib import Path


class IntervalData(HTMLParser):
    def __init__(self):
        super().__init__()
        self.active = False
        self.matches = 0
        self.payload = []

    def handle_starttag(self, tag, attrs):
        if tag == 'script' and dict(attrs).get('id') == 'frameintervaldata':
            self.active = True
            self.matches += 1

    def handle_endtag(self, tag):
        if tag == 'script':
            self.active = False

    def handle_data(self, data):
        if self.active:
            self.payload.append(data)


def summarize(source):
    parser = IntervalData()
    parser.feed(source)
    if parser.matches != 1:
        raise ValueError('Expected exactly one Apple raw frame-interval payload')
    values = json.loads(''.join(parser.payload))['frameintervals']
    if not isinstance(values, list) or not values or not all(
        type(v) in (int, float) and math.isfinite(v) and v > 0 for v in values
    ):
        raise ValueError('Missing or invalid frame intervals')
    seconds = sum(values) / 1e9
    ordered = sorted(values)
    def percentile(fraction):
        return ordered[min(len(ordered)-1, int((len(ordered)-1)*fraction))] / 1e6
    return {
        'metricKind': 'appleHUDRawFrameIntervals',
        'sourceSHA256': hashlib.sha256(source.encode()).hexdigest(),
        'frames': len(values),
        'intervalDurationSeconds': seconds,
        'meanFPS': len(values) / seconds,
        'p50MS': percentile(.5), 'p95MS': percentile(.95), 'p99MS': percentile(.99),
        'maxMS': max(values) / 1e6,
        'intervalsOver25MS': sum(v > 25_000_000 for v in values),
        'extraRefreshIntervalsAt60Hz': sum(max(0, round(v/(1e9/60))-1) for v in values),
        'overallComplete': False,
        'limitations': 'Report-only cadence. General FPS/Missed Frames fields are not used. GPU headroom, run identity, complete ten-minute coverage, motion and visual quality require separate validation.'
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    try:
        result = summarize(args.report.read_text())
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.exit(1, f'Invalid Apple report: {error}\n')
    text = json.dumps(result, indent=2)+'\n'
    if args.output:
        args.output.write_text(text)
    print(text, end='')


if __name__ == '__main__':
    main()
