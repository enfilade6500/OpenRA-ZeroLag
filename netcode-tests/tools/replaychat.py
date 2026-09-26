#!/usr/bin/env python3
"""Search OpenRA replays (.orarep) for chat about lag, slowness, the server, etc.

Reads the replay files directly (no OpenRA needed), so it works on the server:

    python3 replaychat.py /var/lib/openra/1234/Replays          # default word list
    python3 replaychat.py replays/ -a desync crash              # default list plus these
    python3 replaychat.py replays/ -w "gg" "well played"        # only these words/phrases
    python3 replaychat.py replays/ --all                        # every chat line
    python3 replaychat.py replays/ -C 2                         # 2 chat lines of context around each hit
    python3 replaychat.py replays/ --players-only               # ignore the server's own messages

Matching is case-insensitive and matches the start of a word, so "lag" also finds
"laggy" / "lagging" but not "flag"; "slow" finds "slower" and "slowing".
The time shown is the game clock (minutes:seconds at normal speed) when the line was
sent, worked out from the last game frame recorded before it, so it is approximate.
"""
import argparse
import io
import os
import re
import struct
import sys
from datetime import datetime

DEFAULT_WORDS = [
    "lag", "slow", "fast", "server", "speed", "freez", "froze", "stutter", "chop",
    "delay", "ping", "desync", "disconnect", "dc", "kick", "rubber", "connection",
    "internet", "wifi", "fps", "smooth", "pause", "stuck", "!speed", "!kickslow",
]

ORDER_FIELDS = 0xFF
ORDER_HANDSHAKE = 0xFE
SYNC_HASH = 0x65
DISCONNECT = 0xBF
F_TARGET, F_EXTRA_ACTORS, F_TARGET_STRING, F_QUEUED = 0x01, 0x02, 0x04, 0x08
F_EXTRA_LOCATION, F_EXTRA_DATA, F_TARGET_IS_CELL, F_SUBJECT, F_GROUPED = 0x10, 0x20, 0x40, 0x80, 0x100
TICKS_PER_NET_FRAME = 3
TIMESTEP_MS = 40  # "normal" game speed


class Reader:
    """The subset of .NET BinaryReader that orders use."""

    def __init__(self, data):
        self.b = io.BytesIO(data)

    def left(self):
        return self.b.getbuffer().nbytes - self.b.tell()

    def u8(self):
        return struct.unpack("<B", self.b.read(1))[0]

    def i16(self):
        return struct.unpack("<h", self.b.read(2))[0]

    def i32(self):
        return struct.unpack("<i", self.b.read(4))[0]

    def u32(self):
        return struct.unpack("<I", self.b.read(4))[0]

    def skip(self, n):
        self.b.seek(n, io.SEEK_CUR)

    def string(self):
        # 7-bit encoded length prefix, then UTF-8
        length, shift = 0, 0
        while True:
            byte = self.u8()
            length |= (byte & 0x7F) << shift
            if not byte & 0x80:
                break
            shift += 7
        return self.b.read(length).decode("utf-8", errors="replace")


def parse_orders(payload):
    """Yield (order_name, target_string, extra_data) for each order in an order packet."""
    r = Reader(payload)
    while r.left() > 0:
        kind = r.u8()
        if kind == ORDER_HANDSHAKE:
            name = r.string()
            yield name, r.string(), 0
            continue

        if kind != ORDER_FIELDS:
            return  # not an order stream we understand

        name = r.string()
        flags = r.i16()
        if flags & F_SUBJECT:
            r.u32()

        if flags & F_TARGET:
            target_type = r.u8()
            if target_type == 1:      # Actor
                r.skip(8)
            elif target_type == 2:    # FrozenActor
                r.skip(8)
            elif target_type == 3:    # Terrain
                if flags & F_TARGET_IS_CELL:
                    r.skip(5)
                else:
                    r.skip(12)
                    count = r.i16()
                    if count != -1:
                        r.skip(12 * count)

        target_string = r.string() if flags & F_TARGET_STRING else None
        if flags & F_EXTRA_ACTORS:
            r.skip(4 * r.i32())

        if flags & F_EXTRA_LOCATION:
            r.skip(4)

        extra_data = r.u32() if flags & F_EXTRA_DATA else 0
        if flags & F_GROUPED:
            r.skip(4 * r.i32())

        yield name, target_string, extra_data


def read_metadata(data):
    """Map title, start time and player names from the trailer, if present."""
    info = {"map": None, "start": None, "players": {}}
    if len(data) < 20:
        return info

    data_length, end_marker = struct.unpack("<ii", data[-8:])
    if end_marker != -2:
        return info

    start = len(data) - 8 - data_length - 8
    if start < 0 or struct.unpack("<ii", data[start:start + 8]) != (-1, 1):
        return info

    yaml_length = struct.unpack("<i", data[start + 8:start + 12])[0]
    yaml = data[start + 12:start + 12 + yaml_length].decode("utf-8", errors="replace")
    m = re.search(r"^\tMapTitle: (.*)$", yaml, re.M)
    if m:
        info["map"] = m.group(1).strip()

    m = re.search(r"^\tStartTimeUtc: (.*)$", yaml, re.M)
    if m:
        # OpenRA writes "2026-09-26 23-03-26"
        info["start"] = re.sub(r" (\d\d)-(\d\d)-(\d\d)$", r" \1:\2:\3", m.group(1).strip())

    for block in re.split(r"^Player@\d+:", yaml, flags=re.M)[1:]:
        idx = re.search(r"^\tClientIndex: (-?\d+)", block, re.M)
        name = re.search(r"^\tName: (.*)$", block, re.M)
        if idx and name:
            info["players"][int(idx.group(1))] = name.group(1).strip()

    return info


def client_names_from_lobby(yaml):
    """Client index -> name from a SyncInfo / SyncLobbyClients order (covers spectators too)."""
    names = {}
    for block in re.split(r"^\s*Client@\d+:", yaml, flags=re.M)[1:]:
        idx = re.search(r"^\s*Index: (\d+)", block, re.M)
        name = re.search(r"^\s*Name: (.*)$", block, re.M)
        if idx and name:
            names[int(idx.group(1))] = name.group(1).strip()

    return names


def fluent_to_text(yaml):
    """A FluentMessage carries a message key and named arguments; render something readable."""
    head = yaml.split("Arguments:", 1)[0]
    key = re.search(r"^\s*Key: (.*)$", head, re.M)
    text = key.group(1).strip() if key else " ".join(yaml.split())
    text = text.replace("notification-", "").replace("-", " ")
    args = []
    for block in re.split(r"^\s*Argument@\d+:", yaml, flags=re.M)[1:]:
        k = re.search(r"^\s*Key: (.*)$", block, re.M)
        v = re.search(r"^\s*Value: (.*)$", block, re.M)
        if k and v:
            args.append(f"{k.group(1).strip()}={v.group(1).strip()}")

    return f"[{text}{': ' + ', '.join(args) if args else ''}]"


def read_chat(path):
    """Return (metadata, [(seconds, client_index, speaker, text, is_server)]) for one replay."""
    with open(path, "rb") as f:
        data = f.read()

    meta = read_metadata(data)
    names = dict(meta["players"])
    lines = []
    last_frame = 0
    pos = 0
    total = len(data)
    while pos + 8 <= total:
        client, length = struct.unpack("<ii", data[pos:pos + 8])
        pos += 8
        if client == -1:  # metadata trailer begins
            break

        if length < 4 or pos + length > total:
            break

        packet = data[pos:pos + length]
        pos += length
        frame = struct.unpack("<i", packet[:4])[0]
        payload = packet[4:]
        if frame > 0:
            last_frame = max(last_frame, frame)

        if not payload or payload[0] in (SYNC_HASH, DISCONNECT):
            continue

        try:
            orders = list(parse_orders(payload))
        except Exception:
            continue

        seconds = last_frame * TICKS_PER_NET_FRAME * TIMESTEP_MS / 1000
        for name, target, extra in orders:
            if name in ("SyncInfo", "SyncLobbyClients") and target:
                names.update(client_names_from_lobby(target))
            elif name == "Chat" and target is not None:
                speaker = names.get(client, f"client {client}")
                if extra:
                    speaker += " (team)"
                lines.append((seconds, client, speaker, target, False))
            elif name == "Message" and target:
                lines.append((seconds, client, "server", target, True))
            elif name == "FluentMessage" and target:
                lines.append((seconds, client, "server", fluent_to_text(target), True))

    return meta, lines


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("paths", nargs="*", help="replay files or directories (searched recursively)")
    ap.add_argument("-w", "--words", nargs="+", metavar="WORD", help="search for these words/phrases instead of the default list")
    ap.add_argument("-a", "--add", nargs="+", metavar="WORD", default=[], help="add these to the default list")
    ap.add_argument("--all", action="store_true", help="print every chat line")
    ap.add_argument("-C", "--context", type=int, default=0, metavar="N", help="also print N chat lines before and after each match")
    ap.add_argument("--players-only", action="store_true", help="ignore the server's own messages")
    ap.add_argument("--list-words", action="store_true", help="print the default word list and exit")
    args = ap.parse_args()

    if args.list_words:
        print(" ".join(DEFAULT_WORDS))
        return 0

    if not args.paths:
        ap.error("give at least one replay file or directory")

    words = (args.words or DEFAULT_WORDS) + args.add
    pattern = re.compile(r"(?<!\w)(?:" + "|".join(re.escape(w) for w in words) + ")", re.I)

    files = []
    for p in args.paths:
        if os.path.isdir(p):
            for root, _, filenames in os.walk(p):
                files.extend(os.path.join(root, fn) for fn in filenames if fn.endswith(".orarep"))
        elif os.path.isfile(p):
            files.append(p)
        else:
            print(f"not found: {p}", file=sys.stderr)

    files.sort()
    if not files:
        print("no .orarep files found", file=sys.stderr)
        return 1

    total_hits = 0
    replays_with_hits = 0
    for path in files:
        try:
            meta, lines = read_chat(path)
        except Exception as e:
            print(f"== {path}: could not read ({e})", file=sys.stderr)
            continue

        if args.players_only:
            lines = [l for l in lines if not l[4]]

        hits = [i for i, l in enumerate(lines) if args.all or pattern.search(l[3])]
        if not hits:
            continue

        replays_with_hits += 1
        total_hits += len(hits)
        show = set()
        for i in hits:
            show.update(range(max(0, i - args.context), min(len(lines), i + args.context + 1)))

        when = meta["start"] or datetime.utcfromtimestamp(os.path.getmtime(path)).strftime("%Y-%m-%d %H:%M:%S") + " (file time; no end-of-game data)"
        players = ", ".join(meta["players"].get(k, str(k)) for k in sorted(meta["players"])) or "?"
        print(f"== {os.path.basename(path)}  ({when} UTC, {meta['map'] or 'unknown map'}; {players})")
        previous = -1
        for i in sorted(show):
            if previous >= 0 and i != previous + 1:
                print("   ...")
            previous = i
            seconds, _, speaker, text, is_server = lines[i]
            mark = "*" if i in hits else " "
            print(f" {mark} [{int(seconds // 60):02d}:{int(seconds % 60):02d}] {speaker}: {text}")

        print()

    print(f"{total_hits} matching line(s) in {replays_with_hits} of {len(files)} replay(s); words: {' '.join(words)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
