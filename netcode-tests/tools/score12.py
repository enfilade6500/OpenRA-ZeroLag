#!/usr/bin/env python3
"""Score the v1.2 scenarios (and the regression suite) per server build.

usage: score12.py <label>[,<label>...] [seed] [results-dir] [scenario ...]

One line per scenario and build:
  others speed%  the healthy players' average game speed (what the majority got)
  lost ms/min    the healthy players' time lost to freezes of 100 ms or more, per minute
  p4 stalled%    share of the slow player's time lost to such freezes
  p4 input p95   the slow player's own input delay, 95th percentile (ms)
  slowdowns      "Slowing the game" lines in the server log, and the lowest speed reached
  failed probes  "Speeding the game up to ... was too much" lines
  changes/m      slowdowns + failed probes + returns to full speed, per minute
  wait s         total time the whole game was paused for a stopped player
"""
import json, os, re, sys

labels = sys.argv[1].split(',') if len(sys.argv) > 1 else ['v11', 'v12']
seed = sys.argv[2] if len(sys.argv) > 2 else '1'
R = sys.argv[3] if len(sys.argv) > 3 else '/home/claude/results'
scenarios = sys.argv[4:] or ['melo', 'wander', 'nextslowest', 'holetrain', 'plateau', 'decline', 'lossload', 'floorreturn', 'deadlink', 'outage8']

def log_stats(path):
    if not os.path.exists(path):
        return None
    slow, fails, full, wait = [], 0, 0, 0.0
    for line in open(path, encoding='utf-8', errors='replace'):
        m = re.search(r'Slowing the game to (\d+)%', line)
        if m: slow.append(int(m.group(1)))
        if 'was too much for' in line: fails += 1
        if 'back to full speed' in line: full += 1
        m = re.search(r'after waiting ([\d.]+)s|Everyone waited ([\d.]+)s|briefly \(([\d.]+)s in total', line)
        if m: wait += float(m.group(1) or m.group(2) or m.group(3))
    return dict(slow=slow, fails=fails, full=full, wait=wait)

print(f"{'scenario':12} {'build':6} {'ok':>4} | {'others speed%':>13} {'lost ms/min':>11} | {'p4 stalled%':>11} {'p4 input p95':>12} | {'slowdowns':>9} {'min%':>4} {'fails':>5} {'changes/m':>9} {'wait s':>6}")
for s in scenarios:
    for l in labels:
        jp = f'{R}/{l}-{s}-{seed}.json'
        if not os.path.exists(jp):
            continue
        r = json.load(open(jp))
        st = log_stats(f'{R}/serverlog-{l}-{s}-{seed}.log') or {}
        slow_names = {'p4'} if s != 'nextslowest' else {'p3', 'p4'}
        p4 = next((c for c in r['Clients'] if c['Name'] == 'p4'), None)
        others = [c for c in r['Clients'] if c['Name'] not in slow_names]
        mins = r['WindowSeconds'] / 60
        changes = (len(st.get('slow', [])) + st.get('fails', 0) + st.get('full', 0)) / mins if st else float('nan')
        print(f"{s:12} {l:6} {'OK' if r['LockstepOk'] else 'FAIL':>4} | "
              f"{sum(c['SpeedPct'] for c in others) / len(others):13.1f} {sum(c['FrozenMsPerMin'] for c in others) / len(others):11.0f} | "
              f"{p4['FrozenMsPerMin'] / 600:11.1f} {p4['OwnLatencyP95']:12.0f} | "
              f"{len(st.get('slow', [])):9} {min(st['slow']) if st.get('slow') else 100:4} {st.get('fails', 0):5} {changes:9.1f} {st.get('wait', 0):6.1f}")
        if s == 'nextslowest':
            p3 = next((c for c in r['Clients'] if c['Name'] == 'p3'), None)
            if p3:
                print(f"{'':12} {'':6} {'':4} |   p3 (the next-slowest, 80%): speed {p3['SpeedPct']:.1f}%, stalled {p3['FrozenMsPerMin'] / 600:.1f}%, input p95 {p3['OwnLatencyP95']:.0f} ms, longest gap {p3['MaxGapMs']:.0f} ms")
print("p4 = the player with the problem. stalled% and lost ms/min count gaps of 100 ms or more between ticks, so they also count a game running below 40% speed.")
