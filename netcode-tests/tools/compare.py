#!/usr/bin/env python3
# Summarise results/<label>-<scenario>-<seed>.json into a comparison table (mean over seeds).
# usage: compare.py labels(comma) seeds(comma) [results-dir]
import json, os, sys, statistics as st, pathlib
R = sys.argv[3] if len(sys.argv) > 3 else '/home/claude/results'
labels = sys.argv[1].split(',') if len(sys.argv) > 1 else ['stock', 'final2', 'stage2']
seeds = sys.argv[2].split(',') if len(sys.argv) > 2 else ['1', '2', '3']
scen = ['clean', 'slowlink', 'spikes', 'downjitter', 'dropout', 'hitch', 'slowcpu', 'battles', 'potato', 'outage', 'chaos', 'leave']
def load(l, s, seed):
    p = f'{R}/{l}-{s}-{seed}.json'
    return json.load(open(p)) if os.path.exists(p) else None
print(f"{'scenario':10} {'server':15} {'runs':>4} {'safe':>4} | {'HEALTHY: speed':>14} {'frz/min':>7} {'lost s/min':>10} {'input ms':>8} | {'PROBLEM: frz/min':>16} {'lost s/min':>10} {'input ms':>8}")
for s in scen:
    for l in labels:
        runs = [r for r in (load(l, s, sd) for sd in seeds) if r]
        if not runs: continue
        healthy = (lambda c: c['Name'] == 'p1') if s == 'chaos' else (lambda c: c['Name'] != 'p4')
        prob = (lambda c: c['Name'] != 'p1') if s == 'chaos' else (lambda c: c['Name'] == 'p4')
        def agg(sel, key):
            vals = [c[key] for r in runs for c in r['Clients'] if sel(c) and c[key] == c[key]]
            return st.mean(vals) if vals else float('nan')
        ok = all(r['LockstepOk'] for r in runs)
        print(f"{s:10} {l:15} {len(runs):4} {'OK' if ok else 'FAIL':>4} | {agg(healthy,'SpeedPct'):13.1f}% {agg(healthy,'FreezesPerMin'):7.1f} {agg(healthy,'FrozenMsPerMin')/1000:10.2f} {agg(healthy,'OwnLatencyP50'):8.0f} | {agg(prob,'FreezesPerMin'):16.1f} {agg(prob,'FrozenMsPerMin')/1000:10.2f} {agg(prob,'OwnLatencyP50'):8.0f}")
print("healthy = players without a problem (chaos: p1 only); problem = p4 (chaos: p2-p4). input ms = median time from issuing an order to it taking effect for that player.")
