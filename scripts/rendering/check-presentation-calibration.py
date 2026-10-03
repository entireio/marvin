#!/usr/bin/env python3
"""Compare an Instruments interval export with PresentationCalibration.swift.

This validates the export's clock units and held-frame detection on one layer;
it cannot certify simulator performance or GPU headroom.
"""
import argparse
import importlib.util
import json
import math
from pathlib import Path

spec=importlib.util.spec_from_file_location('overview',Path(__file__).with_name('read-game-overview.py'))
overview=importlib.util.module_from_spec(spec)
spec.loader.exec_module(overview)


def check(intervals,native):
    groups=overview.read_intervals(intervals)
    display=[rows for key,rows in groups.items() if key[-1]=='2.On Display']
    if len(display)!=1:
        raise ValueError('Calibration requires exactly one process/layer display stream')
    rows=sorted(display[0],key=lambda row:row['start'])
    frames=[row['frame'] for row in rows]
    if frames!=list(range(frames[0],frames[0]+len(frames))):
        raise ValueError('Missing, duplicate or unordered layer-local frames')
    records=json.loads(native.read_text())['records']
    if not all(type(row.get('presentedTime')) in (int,float) and math.isfinite(row['presentedTime']) and row['presentedTime']>=0 for row in records):
        raise ValueError('Invalid native presentation timestamps')
    # Metal documents zero for a drawable that has not been presented.
    times=[row['presentedTime'] for row in records if row['presentedTime']>0]
    if not all(math.isfinite(t) for t in times) or any(b<=a for a,b in zip(times,times[1:])):
        raise ValueError('Invalid native presentation timestamps')
    if len(times)!=len(rows)+1:
        raise ValueError('Native/exported coverage mismatch')
    delta=[b-a for a,b in zip(times,times[1:])]
    error=max(abs(row['duration']/1e9-dt) for row,dt in zip(rows,delta))
    offsets=[t-row['start']/1e9 for t,row in zip(times,rows)]
    native_long=[i for i,dt in enumerate(delta) if dt>.025]
    exported_long=[i for i,row in enumerate(rows) if row['duration']>25_000_000]
    return {'passed':error<1e-7 and max(offsets)-min(offsets)<1e-7 and native_long==exported_long and bool(native_long),
            'intervalCount':len(rows),'maximumIntervalErrorSeconds':error,
            'clockOffsetSpreadSeconds':max(offsets)-min(offsets),
            'nativeLongFrameIndices':native_long,'exportedLongFrameIndices':exported_long,
            'overallComplete':False,'scope':'Single-layer calibration, not simulator acceptance'}


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('intervals',type=Path)
    parser.add_argument('native',type=Path)
    parser.add_argument('--output',type=Path)
    args=parser.parse_args()
    report=check(args.intervals,args.native)
    text=json.dumps(report,indent=2)+'\n'
    if args.output:args.output.write_text(text)
    print(text,end='')
    raise SystemExit(0 if report['passed'] else 1)
