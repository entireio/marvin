#!/usr/bin/env python3
"""Fetch a pinned Hey Marvin model for private microWakeWord evaluation.
Model redistribution/licensing review is unresolved; this is not a release asset.
"""
from pathlib import Path
import argparse,hashlib,urllib.request
variants={
 'v1':('https://raw.githubusercontent.com/TaterTotterson/Tater-Wake-Words/740bd31af8f28b1700daf49c36444d278715d298/microWakeWordsV1/hey_marvin.tflite','f8297bc0e1d42e173e4a47e09f073d93aeddc5fb3aec9791cc5afeac5c431f50',63536),
 'v3':('https://raw.githubusercontent.com/TaterTotterson/microWakeWords/e2e4f5ad41b7c944016d95350fc9d6fa17f3fa8f/microWakeWordsV3/hey_marvin.tflite','32d550a8dae155ceb407981df189e6380f0abaf4545534b132821ef97036584a',63520),
}
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--variant',choices=variants,default='v1');a=p.parse_args()
url,expected,size=variants[a.variant]
dest=Path(__file__).resolve().parents[1]/'afe/hey_marvin.tflite'
data=dest.read_bytes() if dest.exists() else b''
current=hashlib.sha256(data).hexdigest()
if data and current not in {v[1] for v in variants.values()}:raise SystemExit('Existing model is not a known evaluation artifact; refusing to overwrite it')
if current!=expected:data=urllib.request.urlopen(url,timeout=30).read(size+1)
if len(data)!=size or hashlib.sha256(data).hexdigest()!=expected:raise SystemExit('Wake model does not match the pinned evaluation artifact')
if current!=expected:
 tmp=dest.with_suffix('.tflite.tmp');tmp.write_bytes(data);tmp.replace(dest)
print('Pinned evaluation model verified:',a.variant,expected)
