#!/usr/bin/env python3
"""Score the scenarios built from real games, per server build.

usage: score.py <label>[,<label>...] [seed] [results-dir]

For each scenario and build, one line: what the player with the problem (p4) experienced,
what the game did about it (from the server log), and whether anyone else noticed.

  stalled%   share of p4's time lost to freezes of 100 ms or more
  input      p4's own input delay, median / 95th percentile (ms)
  slowdowns  "Slowing the game" lines in the server log, and the lowest speed reached
  changes/m  speed changes per minute (slowdowns + returns to full speed): the sawtooth
  wait s     total time the whole game was paused for a stopped player
  others     the healthy players' average speed and time lost to freezes per minute
"""
import json, os, re, sys

labels = sys.argv[1].split(',') if len(sys.argv) > 1 else ['base', 'v11']
seed = sys.argv[2] if len(sys.argv) > 2 else '1'
R = sys.argv[3] if len(sys.argv) > 3 else '/home/claude/results'
scenarios = ['holetrain', 'decline', 'plateau', 'lossload', 'outage8', 'deadlink', 'floorreturn', 'potato']

def log_stats(path):
    if not os.path.exists(path):
        return None
    slow = []; full = 0; wait = 0.0; cont = 0; buffered = 0.0; notslowed = 0
    for line in open(path, encoding='utf-8', errors='replace'):
        m = re.search(r'Slowing the game to (\d+)%', line)
        if m: slow.append(int(m.group(1)))
        if 'back to full speed' in line: full += 1
        m = re.search(r'after waiting ([\d.]+)s|Everyone waited ([\d.]+)s|briefly \(([\d.]+)s in total', line)
        if m: wait += float(m.group(1) or m.group(2) or m.group(3))
        if 'continues without them' in line: cont += 1
        m = re.search(r'buffering ([\d.]+)s|buffered up to ([\d.]+)s', line)
        if m: buffered = max(buffered, float(m.group(1) or m.group(2)))
        if 'not slowed down for them' in line: notslowed += 1
    return dict(slow=slow, full=full, wait=wait, cont=cont, buffered=buffered, notslowed=notslowed)

print(f"{'scenario':11} {'build':7} {'ok':>4} | {'stalled%':>8} {'input p50/p95':>14} | {'slowdowns':>9} {'min%':>4} {'changes/m':>9} {'wait s':>6} {'buffer':>6} | {'others speed%':>13} {'lost ms/min':>11}")
for s in scenarios:
    for l in labels:
        jp = f'{R}/{l}-{s}-{seed}.json'
        if not os.path.exists(jp):
            continue
        r = json.load(open(jp))
        st = log_stats(f'{R}/serverlog-{l}-{s}-{seed}.log') or {}
        p4 = next((c for c in r['Clients'] if c['Name'] == 'p4'), None)
        others = [c for c in r['Clients'] if c['Name'] != 'p4']
        mins = r['WindowSeconds'] / 60
        changes = (len(st.get('slow', [])) + st.get('full', 0)) / mins if st else float('nan')
        print(f"{s:11} {l:7} {'OK' if r['LockstepOk'] else 'FAIL':>4} | "
              f"{p4['FrozenMsPerMin'] / 600:8.1f} {p4['OwnLatencyP50']:6.0f}/{p4['OwnLatencyP95']:<7.0f} | "
              f"{len(st.get('slow', [])):9} {min(st['slow']) if st.get('slow') else 100:4} {changes:9.1f} {st.get('wait', 0):6.1f} {st.get('buffered', 0):6.1f} | "
              f"{sum(c['SpeedPct'] for c in others) / len(others):13.1f} {sum(c['FrozenMsPerMin'] for c in others) / len(others):11.0f}")
print("p4 = the player with the problem. stalled% and lost ms/min count gaps of 100 ms or more between ticks, so they also count a game running below 40% speed.")
