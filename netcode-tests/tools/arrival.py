"""Reconstruct each client's arrival pattern from a server replay.

The server writes a client's sync packet into the replay the moment it arrives (with the
client's game frame F), and writes the dispatched frame N for every client when it closes N.
So for each sync packet, the last closed frame before it in the file tells us, to one frame
period, *when* the client's report for frame F arrived. lag = closed - F.

  compute-limited client : lag grows smoothly (slope = 1 - speed)
  network holes          : lag flat, then a step of k frames, then falls at the catch-up rate
  burst                  : several consecutive F arrive between the same two closings
"""
import struct, sys, collections
sys.path.insert(0, '/home/claude/deliver')
from replaychat import parse_orders, client_names_from_lobby

def read(path):
    data = open(path, 'rb').read()
    pos, total = 0, len(data)
    names = {}
    closed = 0
    arrivals = collections.defaultdict(list)   # client -> [(closed_frame, F)]
    while pos + 8 <= total:
        client, length = struct.unpack('<ii', data[pos:pos+8]); pos += 8
        if client == -1 or length < 4 or pos + length > total:
            break
        pkt = data[pos:pos+length]; pos += length
        frame = struct.unpack('<i', pkt[:4])[0]
        payload = pkt[4:]
        if payload and payload[0] == 0x65 and len(payload) == 13:
            arrivals[client].append((closed, frame))
            continue
        if payload and payload[0] == 0xBF:
            continue
        if frame == 0:
            try:
                for n, t, x in parse_orders(payload):
                    if n in ('SyncInfo', 'SyncLobbyClients') and t:
                        names.update(client_names_from_lobby(t))
            except Exception:
                pass
            continue
        closed = max(closed, frame)
    return names, arrivals

def analyse(path, period_ms=120, bucket_s=60):
    names, arrivals = read(path)
    per_bucket = period_ms and int(bucket_s * 1000 / period_ms)
    print(f"== {path.split('/logs2/')[-1]}")
    for client, seq in sorted(arrivals.items(), key=lambda kv: kv[0]):
        if len(seq) < 50:
            continue
        name = names.get(client, str(client))
        lags = [(c, f, c - f) for c, f in seq]
        # steps: increase of lag between consecutive reports
        holes = [(c, f, l - pl) for (c, f, l), (pc, pf, pl) in zip(lags[1:], lags[:-1]) if l - pl >= 3]
        # bursts: consecutive F arriving between the same two closings
        bursts = sum(1 for (c, f, l), (pc, pf, pl) in zip(lags[1:], lags[:-1]) if c == pc)
        # time (in frames) spent in holes ~ sum of step sizes
        hole_frames = sum(s for _, _, s in holes)
        last_frame = lags[-1][1]
        print(f"  {name:22} reports {len(seq):6}  lag median {sorted(l for _,_,l in lags)[len(lags)//2]:3}  max {max(l for _,_,l in lags):4}"
              f"  holes>=3fr {len(holes):4} ({hole_frames*period_ms/1000:.0f}s = {100*hole_frames/max(1,last_frame):.1f}% of game)  bursts {bursts}")
        # timeline: per bucket, median lag / max lag / holes / hole frames
        buckets = collections.defaultdict(list)
        hb = collections.Counter(); hbf = collections.Counter()
        for c, f, l in lags:
            buckets[f // per_bucket].append(l)
        for c, f, s in holes:
            hb[f // per_bucket] += 1; hbf[f // per_bucket] += s
        row = []
        for b in sorted(buckets):
            ls = buckets[b]
            row.append(f"{b*bucket_s//60:>3}m:{sorted(ls)[len(ls)//2]:>3}/{max(ls):>3}/{hb[b]:>2}h{hbf[b]*period_ms//1000:>3}s")
        for i in range(0, len(row), 6):
            print("      " + "  ".join(row[i:i+6]))
    print()

if __name__ == '__main__':
    for p in sys.argv[1:]:
        analyse(p)
