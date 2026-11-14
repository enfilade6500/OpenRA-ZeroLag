#!/usr/bin/env python3
"""
replay-digest: the few kilobytes that matter from an OpenRA server-side replay.

A dedicated server records every game as Replays/<mod>/<version>/<mod>-Server-<time>.orarep: every
order packet it relayed, plus (if the game ended normally) a metadata footer. Those files are
megabytes each and most of their bulk is unit orders. For reviewing how a server has been doing
over weeks, what is wanted is the rest: who played what map for how long, what they said in chat,
what the server announced, who was defeated or left and when. This script pulls that out as JSON
(or readable text) so that the replays themselves can stay on the server.

    replay-digest.py GAME.orarep [...]            one JSON object per replay, on stdout
    replay-digest.py --text GAME.orarep           readable
    replay-digest.py --since 48h -o digests/ /var/lib/openra/*/Replays
                                                  every replay newer than 48 hours under those
                                                  directories, one <name>.json each in digests/

Only the standard library is used (Python 3.8+). It reads the file format of release-20250330
(OpenRA.Game/Network/Order.cs, FileFormats/ReplayMetadata.cs); unit orders are counted per player
but not interpreted. No IP addresses are included unless --ips is given.

A replay without a footer is one the server did not finish writing: the process was killed or
died mid-game. The digest says so and still reports everything up to the last packet.
"""

import argparse
import datetime as dt
import glob
import io
import json
import os
import re
import struct
import sys

# ---------------------------------------------------------------------------------------------
# Wire format

ORDER_ACK = 0x10
ORDER_PING = 0x20
ORDER_SYNCHASH = 0x65
ORDER_TICKSCALE = 0x76
ORDER_DISCONNECT = 0xBF
ORDER_HANDSHAKE = 0xFE
ORDER_FIELDS = 0xFF

F_TARGET, F_EXTRAACTORS, F_TARGETSTRING, F_QUEUED, F_EXTRALOCATION, F_EXTRADATA, F_TARGETISCELL, F_SUBJECT, F_GROUPED = \
    0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x100

TT_INVALID, TT_ACTOR, TT_TERRAIN, TT_FROZENACTOR = 0, 1, 2, 3

# Timesteps of the RA/TD/D2k game speeds (mods/*/mod.yaml GameSpeeds); a net frame is
# NetFrameInterval (3) of these. Unknown speeds fall back to normal.
TIMESTEP_MS = {"slowest": 80, "slower": 50, "default": 40, "normal": 40, "fast": 35, "faster": 30, "fastest": 20}


class Reader:
    """BinaryReader semantics over bytes."""

    def __init__(self, data, pos=0):
        self.data = data
        self.pos = pos

    def left(self):
        return len(self.data) - self.pos

    def u8(self):
        v = self.data[self.pos]
        self.pos += 1
        return v

    def i16(self):
        v = struct.unpack_from("<h", self.data, self.pos)[0]
        self.pos += 2
        return v

    def i32(self):
        v = struct.unpack_from("<i", self.data, self.pos)[0]
        self.pos += 4
        return v

    def u32(self):
        v = struct.unpack_from("<I", self.data, self.pos)[0]
        self.pos += 4
        return v

    def u64(self):
        v = struct.unpack_from("<Q", self.data, self.pos)[0]
        self.pos += 8
        return v

    def skip(self, n):
        if n < 0 or self.pos + n > len(self.data):
            raise ValueError("skip past end")
        self.pos += n

    def string(self):
        """BinaryReader.ReadString: 7-bit encoded length, then UTF-8."""
        length = 0
        shift = 0
        while True:
            b = self.u8()
            length |= (b & 0x7F) << shift
            if not b & 0x80:
                break
            shift += 7
            if shift > 35:
                raise ValueError("bad string length")
        if length > self.left():
            raise ValueError("string past end")
        s = self.data[self.pos:self.pos + length].decode("utf-8", "replace")
        self.pos += length
        return s


def parse_order(r):
    """One order from the reader. Returns a dict with 'type' and, for Fields orders, 'order', 'target_string',
    'extra_data', 'has_subject'. Raises on malformed data."""
    t = r.u8()
    if t == ORDER_FIELDS:
        name = r.string()
        flags = r.i16()
        has_subject = bool(flags & F_SUBJECT)
        if has_subject:
            r.u32()
        if flags & F_TARGET:
            tt = r.u8()
            if tt == TT_ACTOR:
                r.u32()
                r.i32()
            elif tt == TT_FROZENACTOR:
                r.u32()
                r.u32()
            elif tt == TT_TERRAIN:
                if flags & F_TARGETISCELL:
                    r.i32()
                    r.u8()
                else:
                    r.skip(12)
                    n = r.i16()
                    if n != -1:
                        r.skip(12 * n)
            elif tt == TT_INVALID:
                pass
            else:
                raise ValueError(f"bad target type {tt}")
        target_string = r.string() if flags & F_TARGETSTRING else None
        if flags & F_EXTRAACTORS:
            r.skip(4 * r.i32())
        if flags & F_EXTRALOCATION:
            r.i32()
        extra_data = r.u32() if flags & F_EXTRADATA else 0
        if flags & F_GROUPED:
            r.skip(4 * r.i32())
        return {"type": "fields", "order": name, "target_string": target_string, "extra_data": extra_data,
                "has_subject": has_subject}
    if t == ORDER_HANDSHAKE:
        name = r.string()
        target_string = r.string()
        return {"type": "handshake", "order": name, "target_string": target_string}
    if t == ORDER_SYNCHASH:
        h = r.i32()
        defeat = r.u64()
        return {"type": "sync", "hash": h, "defeat": defeat}
    if t == ORDER_DISCONNECT:
        r.i32()
        return {"type": "disconnect"}
    if t == ORDER_ACK:
        r.i32()
        return {"type": "ack"}
    if t == ORDER_PING:
        r.i32()
        return {"type": "ping"}
    if t == ORDER_TICKSCALE:
        r.i32()
        return {"type": "tickscale"}
    raise ValueError(f"unknown order type 0x{t:02x}")


def packets(data, end, state):
    """Yield (client, frame, payload) for each recorded packet in data[:end]. A file cut off mid-packet (the
    server was killed while writing) ends the walk with state["truncated"] set; a length that cannot be
    right ends it with state["corrupt"] set."""
    pos = 0
    while pos + 8 <= end:
        client, length = struct.unpack_from("<ii", data, pos)
        if length < 4 or length > 64 * 1024 * 1024 or client < 0 or client > 100000:
            state["corrupt"] = pos
            return
        if pos + 8 + length > end:
            state["truncated"] = pos
            return
        pos += 8
        frame = struct.unpack_from("<i", data, pos)[0]
        yield client, frame, data[pos + 4:pos + length]
        pos += length
    if pos != end:
        state["truncated"] = pos


def read_footer(data):
    """(yaml text, body end offset) if the metadata footer is present and sane, else (None, len(data))."""
    if len(data) < 20:
        return None, len(data)
    data_length, end_marker = struct.unpack_from("<ii", data, len(data) - 8)
    if end_marker != -2 or data_length < 4 or data_length + 16 > len(data):
        return None, len(data)
    start = len(data) - 8 - data_length - 8
    start_marker, version = struct.unpack_from("<ii", data, start)
    if start_marker != -1 or version != 1:
        return None, len(data)
    str_len = struct.unpack_from("<i", data, start + 8)[0]
    if str_len != data_length - 4:
        return None, len(data)
    text = data[start + 12:start + 12 + str_len].decode("utf-8", "replace")
    return text, start


# ---------------------------------------------------------------------------------------------
# MiniYaml (the subset OpenRA writes: tab indentation, "Key: Value", "Key@Suffix", # comments)

class Node:
    __slots__ = ("key", "value", "children")

    def __init__(self, key, value):
        self.key = key
        self.value = value
        self.children = []

    def child(self, key):
        for c in self.children:
            if c.key == key:
                return c
        return None

    def get(self, key, default=None):
        c = self.child(key)
        return c.value if c is not None and c.value is not None else default


def parse_miniyaml(text):
    root = Node(None, None)
    stack = [(-1, root)]
    for raw in text.splitlines():
        line = raw.rstrip("\r")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        indent = len(line) - len(line.lstrip("\t"))
        body = line.strip()
        if ":" in body:
            key, value = body.split(":", 1)
            value = value.strip()
            if value == "":
                value = None
        else:
            key, value = body, None
        node = Node(key.strip(), value)
        while stack and stack[-1][0] >= indent:
            stack.pop()
        stack[-1][1].children.append(node)
        stack.append((indent, node))
    return root


# ---------------------------------------------------------------------------------------------
# The digest

def iso_utc(s):
    """OpenRA writes DateTime as 'yyyy-MM-dd HH-mm-ss'; give it back as ISO 8601 UTC."""
    if not s:
        return s
    m = re.fullmatch(r"(\d{4}-\d{2}-\d{2}) (\d{2})-(\d{2})-(\d{2})", s.strip())
    return f"{m.group(1)}T{m.group(2)}:{m.group(3)}:{m.group(4)}Z" if m else s


def mmss(seconds):
    seconds = int(round(seconds))
    if seconds >= 3600:
        return "%d:%02d:%02d" % (seconds // 3600, seconds % 3600 // 60, seconds % 60)
    return "%d:%02d" % (seconds // 60, seconds % 60)


def digest(path, include_ips=False, chat_limit=None):
    with open(path, "rb") as f:
        data = f.read()

    out = {
        "file": os.path.basename(path),
        "bytes": len(data),
        "complete": False,
        "mod": None, "version": None,
        "map": {"uid": None, "title": None},
        "server_name": None,
        "start_utc": None, "end_utc": None,
        "game": {"speed": None, "frame_ms": None, "net_frame_interval": 3, "last_frame": 0, "duration_s": None,
                 "started": False, "out_of_sync": False},
        "players": [],
        "chat": [],
        "server_messages": [],
        "events": [],
        "parse_errors": 0,
    }

    # Time of recording from the file name, as a fallback for a replay without footer
    m = re.search(r"(\d{4})-(\d{2})-(\d{2})T(\d{2})(\d{2})(\d{2})Z", os.path.basename(path))
    file_time = None
    if m:
        file_time = dt.datetime(*(int(x) for x in m.groups()), tzinfo=dt.timezone.utc)

    footer_text, body_end = read_footer(data)
    footer_players = []
    if footer_text is not None:
        out["complete"] = True
        root = parse_miniyaml(footer_text)
        meta = root.child("Root")
        if meta is not None:
            out["mod"] = meta.get("Mod")
            out["version"] = meta.get("Version")
            out["map"]["uid"] = meta.get("MapUid")
            out["map"]["title"] = meta.get("MapTitle")
            out["start_utc"] = iso_utc(meta.get("StartTimeUtc"))
            out["end_utc"] = iso_utc(meta.get("EndTimeUtc"))
            try:
                out["game"]["last_frame"] = int(meta.get("FinalGameTick", "0"))
            except ValueError:
                pass
        for n in root.children:
            if n.key and n.key.startswith("Player@"):
                footer_players.append({
                    "client": int(n.get("ClientIndex", "-1")),
                    "name": n.get("Name"),
                    "human": n.get("IsHuman") == "True",
                    "bot": n.get("BotType") if n.get("IsBot") == "True" else None,
                    "faction": n.get("FactionId") or n.get("FactionName"),
                    "team": int(n.get("Team", "0")),
                    "spawn": int(n.get("SpawnPoint", "0")),
                    "outcome": n.get("Outcome", "Undefined"),
                    "outcome_utc": iso_utc(n.get("OutcomeTimestampUtc")),
                    "disconnect_frame": int(n.get("DisconnectFrame", "0")),
                })

    # Lobby state as of game start, from the server's sync orders
    clients = {}          # index -> dict
    globals_node = None
    frame_ms = None

    def absorb_clients(nodes):
        for n in nodes:
            if not (n.key and n.key.startswith("Client@")):
                continue
            try:
                idx = int(n.get("Index", n.key.split("@", 1)[1]))
            except ValueError:
                continue
            c = {
                "client": idx,
                "name": n.get("Name"),
                "slot": n.get("Slot"),
                "observer": n.get("Slot") is None,
                "team": int(n.get("Team", "0") or 0),
                "faction": n.get("Faction"),
                "spawn": int(n.get("SpawnPoint", "0") or 0),
                "admin": n.get("IsAdmin") == "True",
                "bot": n.get("Bot"),
                "state": n.get("State"),
            }
            if include_ips:
                c["ip"] = n.get("IPAddress") or n.get("AnonymizedIPAddress")
                c["location"] = n.get("Location")
            clients[idx] = c

    def absorb_globals(node):
        nonlocal globals_node, frame_ms
        if node is None:
            return
        globals_node = node
        out["server_name"] = node.get("ServerName") or out["server_name"]
        out["map"]["uid"] = out["map"]["uid"] or node.get("Map")
        try:
            out["game"]["net_frame_interval"] = int(node.get("NetFrameInterval", "3"))
        except ValueError:
            pass
        opts = node.child("Options")
        speed = None
        if opts is not None:
            gs = opts.child("gamespeed")
            if gs is not None:
                speed = gs.get("Value")
        out["game"]["speed"] = speed or out["game"]["speed"]
        frame_ms = TIMESTEP_MS.get((out["game"]["speed"] or "normal").lower(), 40) * out["game"]["net_frame_interval"]
        out["game"]["frame_ms"] = frame_ms

    # Walk the packets
    current_frame = 0
    started = False
    orders_by_client = {}
    last_defeat = 0
    defeat_frames = {}        # world player index -> frame first seen defeated
    last_sync_frame = -1
    handshake_names = {}
    fluent_re = re.compile(r"^\s*(\S+?)\s*:\s*(.*)$")

    def t_of(frame):
        return mmss(frame * (frame_ms or 120) / 1000.0)

    def add_event(frame, kind, who=None, detail=None):
        e = {"frame": frame, "t": t_of(frame), "type": kind}
        if who is not None:
            e["who"] = who
        if detail is not None:
            e["detail"] = detail
        out["events"].append(e)

    def name_of(client):
        c = clients.get(client)
        if c and c.get("name"):
            return c["name"]
        return handshake_names.get(client) or f"client {client}"

    walk = {}
    for client, frame, payload in packets(data, body_end, walk):
            if frame > current_frame:
                current_frame = frame
            r = Reader(payload)
            try:
                while r.left() > 0:
                    o = parse_order(r)
                    kind = o["type"]
                    if kind == "sync":
                        if frame > last_sync_frame:
                            last_sync_frame = frame
                            d = o["defeat"]
                            new = d & ~last_defeat
                            if new:
                                for i in range(64):
                                    if new & (1 << i) and i not in defeat_frames:
                                        defeat_frames[i] = frame
                                last_defeat |= new
                        continue
                    if kind == "disconnect":
                        # a client's own goodbye; from client 0 it is the server dropping an unvalidated connection
                        if started and client != 0:
                            add_event(current_frame, "quit", name_of(client))
                        continue
                    if kind in ("ack", "ping", "tickscale"):
                        continue
                    name = o["order"]
                    ts = o.get("target_string")
                    if kind == "handshake":
                        if name == "HandshakeResponse" and ts:
                            hs = parse_miniyaml(ts).child("Handshake")
                            if hs is not None and hs.get("Name"):
                                handshake_names[client] = hs.get("Name")
                        continue

                    # Server (client 0) orders
                    if client == 0:
                        if name == "SyncInfo" and ts:
                            root = parse_miniyaml(ts)
                            clients.clear()
                            absorb_clients(root.children)
                            absorb_globals(root.child("GlobalSettings"))
                        elif name == "SyncLobbyClients" and ts:
                            clients.clear()
                            absorb_clients(parse_miniyaml(ts).children)
                        elif name == "SyncLobbyGlobalSettings" and ts:
                            absorb_globals(parse_miniyaml(ts).child("GlobalSettings"))
                        elif name == "StartGame":
                            started = True
                            out["game"]["started"] = True
                            add_event(0, "start")
                        elif name == "Message" and ts is not None:
                            if started:
                                out["server_messages"].append({"frame": current_frame, "t": t_of(current_frame), "text": ts})
                        elif name == "FluentMessage" and ts is not None and started:
                            # "FluentMessage:" with Protocol, Key and Arguments -> Argument@i {Key, Value, Type}
                            root = parse_miniyaml(ts)
                            fm = root.child("FluentMessage") or (root.children[0] if root.children else None)
                            key = (fm.get("Key") if fm is not None else None) or ts
                            args = {}
                            if fm is not None and fm.child("Arguments") is not None:
                                for a in fm.child("Arguments").children:
                                    if a.get("Key"):
                                        args[a.get("Key")] = a.get("Value")
                            who = args.get("player")
                            if key.endswith("disconnected"):
                                add_event(current_frame, "left", who, key.replace("notification-", ""))
                            elif "kick" in key or "vote" in key:
                                add_event(current_frame, key.replace("notification-", ""), who, args or None)
                            else:
                                out["server_messages"].append({"frame": current_frame, "t": t_of(current_frame),
                                                               "key": key.replace("notification-", ""), "args": args or None})
                        continue

                    # Client orders
                    if name == "Chat":
                        if ts is None:
                            continue
                        entry = {"frame": current_frame, "t": t_of(current_frame), "from": name_of(client),
                                 "client": client, "text": ts}
                        if o.get("extra_data"):
                            entry["team"] = o["extra_data"]
                        if not started:
                            entry["lobby"] = True
                        out["chat"].append(entry)
                    elif not started:
                        continue
                    elif name == "PauseGame":
                        add_event(current_frame, "pause" if (ts or "").lower() == "pause" else "unpause", name_of(client))
                    elif name == "Surrender":
                        add_event(current_frame, "surrender", name_of(client))
                    elif o.get("has_subject"):
                        # a unit or building order: counted, not read
                        orders_by_client[client] = orders_by_client.get(client, 0) + 1
            except (ValueError, IndexError, struct.error):
                out["parse_errors"] += 1
    if "truncated" in walk:
        out["truncated"] = True
    if "corrupt" in walk:
        out["corrupt_at"] = walk["corrupt"]

    if frame_ms is None:
        absorb_globals(globals_node)
    if current_frame > out["game"]["last_frame"]:
        out["game"]["last_frame"] = current_frame
    out["game"]["duration_s"] = round(out["game"]["last_frame"] * (frame_ms or 120) / 1000.0, 1)
    if out["start_utc"] is None and file_time is not None:
        out["start_utc"] = file_time.strftime("%Y-%m-%dT%H:%M:%SZ")
        out["start_utc_source"] = "file name"

    # Players: lobby clients, with the footer's outcomes where it exists
    by_client = {p["client"]: p for p in footer_players}
    players = []
    for idx in sorted(clients):
        c = clients[idx]
        p = {
            "client": idx, "name": c["name"], "observer": c["observer"], "team": c["team"],
            "faction": c["faction"], "spawn": c["spawn"], "admin": c["admin"], "bot": c["bot"],
            "orders": orders_by_client.get(idx, 0),
        }
        if include_ips:
            p["ip"] = c.get("ip")
            p["location"] = c.get("location")
        fp = by_client.get(idx)
        if fp:
            p["outcome"] = fp["outcome"]
            p["outcome_utc"] = fp["outcome_utc"]
            if fp["disconnect_frame"]:
                p["disconnect_frame"] = fp["disconnect_frame"]
            if fp["faction"] and (not p["faction"] or p["faction"] == "Random"):
                p["faction"] = fp["faction"]
        minutes = out["game"]["duration_s"] / 60.0 if out["game"]["duration_s"] else 0
        p["orders_per_min"] = round(p["orders"] / minutes, 1) if minutes > 0.5 else None
        players.append(p)
    # footer players the lobby snapshot missed (should not happen; kept for completeness)
    for fp in footer_players:
        if fp["client"] not in clients:
            players.append({"client": fp["client"], "name": fp["name"], "observer": False, "team": fp["team"],
                            "faction": fp["faction"], "spawn": fp["spawn"], "admin": False, "bot": fp["bot"],
                            "orders": orders_by_client.get(fp["client"], 0), "outcome": fp["outcome"],
                            "outcome_utc": fp["outcome_utc"]})
    out["players"] = players

    # Defeats: the sync packets carry a bitmask over the map's player list, which includes the
    # non-playable players (Neutral, Creeps) first. With a footer, the losers are known by name and
    # the bits are matched to them in order; without one, the bits are reported as they are.
    if defeat_frames:
        losers = [p for p in footer_players if p["outcome"] == "Lost"]
        bits = sorted(defeat_frames)
        losers_sorted = sorted(losers, key=lambda p: p["outcome_utc"] or "")
        names = {}
        if losers and len(losers) == len(bits):
            # bits in frame order against losers in wall-clock order
            for b, p in zip(sorted(bits, key=lambda b: defeat_frames[b]), losers_sorted):
                names[b] = p["name"]
        for b in sorted(bits, key=lambda b: defeat_frames[b]):
            add_event(defeat_frames[b], "defeated", names.get(b), {"world_player": b})

    out["events"].sort(key=lambda e: (e["frame"], 0 if e["type"] == "start" else 1))
    if chat_limit is not None and len(out["chat"]) > chat_limit:
        out["chat"] = out["chat"][:chat_limit]
        out["chat_truncated"] = True
    return out


# ---------------------------------------------------------------------------------------------
# Text rendering

def render_text(d):
    lines = []
    g = d["game"]
    size = "%.1f MB" % (d["bytes"] / 1048576.0) if d["bytes"] >= 1048576 else "%d KB" % (d["bytes"] // 1024)
    status = "complete" if d["complete"] else "NO FOOTER: the server did not finish writing it (killed or died mid-game)"
    if d.get("truncated"):
        status += ", cut off mid-packet"
    if d.get("corrupt_at") is not None:
        status += ", unreadable from byte %d" % d["corrupt_at"]
    lines.append(f"{d['file']}  ({size}, {status})")
    lines.append("  map: %s  [%s]" % (d["map"]["title"] or "?", (d["map"]["uid"] or "?")[:12]))
    lines.append("  server: %s   mod: %s %s" % (d["server_name"] or "?", d["mod"] or "?", d["version"] or ""))
    lines.append("  start: %s%s   end: %s" % (d["start_utc"] or "?", " (from the file name)" if d.get("start_utc_source") else "",
                                                  d["end_utc"] or "?"))
    lines.append("  speed: %s (%s ms/frame)   frames: %d = %s%s" % (
        g["speed"] or "?", g["frame_ms"] or "?", g["last_frame"], mmss(g["duration_s"] or 0),
        "" if g["started"] else "   (game never started)"))
    players = [p for p in d["players"] if not p["observer"]]
    specs = [p for p in d["players"] if p["observer"]]
    lines.append("  players: %d, spectators: %d" % (len(players), len(specs)))
    for p in sorted(players, key=lambda p: (p["team"], p["client"])):
        bits = []
        if p["team"]:
            bits.append("team %d" % p["team"])
        if p["faction"]:
            bits.append(p["faction"])
        bits.append("spawn %d" % p["spawn"])
        if p["bot"]:
            bits.append("bot " + p["bot"])
        if p["admin"]:
            bits.append("admin")
        if p.get("outcome") and p["outcome"] != "Undefined":
            bits.append(p["outcome"].lower() + (" at " + p["outcome_utc"][11:19] + "Z" if p.get("outcome_utc") else ""))
        if p.get("orders_per_min") is not None:
            bits.append("%s orders/min" % p["orders_per_min"])
        lines.append("    %-24s %s" % ((p["name"] or "?")[:24], ", ".join(bits)))
    for p in specs:
        lines.append("    %-24s spectator%s" % ((p["name"] or "?")[:24], ", admin" if p["admin"] else ""))
    if d["events"]:
        lines.append("  events:")
        for e in d["events"]:
            who = e.get("who")
            det = e.get("detail")
            s = "    [%7s] %s" % (e["t"], e["type"])
            if who:
                s += ": " + who
            if det and not who:
                s += "  %s" % (det if isinstance(det, str) else json.dumps(det))
            lines.append(s)
    if d["server_messages"]:
        lines.append("  server said (%d):" % len(d["server_messages"]))
        for m in d["server_messages"]:
            text = m.get("text") if m.get("text") is not None else "%s %s" % (m.get("key"), json.dumps(m.get("args")))
            lines.append("    [%7s] %s" % (m["t"], text))
    lines.append("  chat (%d lines%s):" % (len(d["chat"]), ", truncated" if d.get("chat_truncated") else ""))
    for c in d["chat"]:
        team = " (team %d)" % c["team"] if c.get("team") else ""
        lines.append("    [%7s] %s%s: %s" % (c["t"], c["from"], team, c["text"]))
    if d["parse_errors"]:
        lines.append("  orders that could not be read: %d" % d["parse_errors"])
    return "\n".join(lines)


# ---------------------------------------------------------------------------------------------

def parse_since(s):
    m = re.fullmatch(r"(\d+)([hdm]?)", s.strip())
    if not m:
        raise argparse.ArgumentTypeError("--since wants a number with h, d or m (e.g. 48h, 7d, 90m)")
    n, unit = int(m.group(1)), m.group(2) or "h"
    return dt.timedelta(**{{"h": "hours", "d": "days", "m": "minutes"}[unit]: n})


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[1], formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("paths", nargs="+", help=".orarep files, or directories to search recursively")
    ap.add_argument("--text", action="store_true", help="readable text instead of JSON")
    ap.add_argument("--both", action="store_true", help="with -o: write both <name>.json and <name>.txt")
    ap.add_argument("--since", type=parse_since, default=None, help="only replays modified within this long (48h, 7d, 90m)")
    ap.add_argument("-o", "--out-dir", default=None, help="write one file per replay here (<name>.json or .txt) instead of stdout")
    ap.add_argument("--ips", action="store_true", help="include the players' IP addresses and locations")
    ap.add_argument("--chat-limit", type=int, default=None, help="keep at most this many chat lines per game")
    ap.add_argument("--quiet", action="store_true", help="no per-file progress on stderr")
    args = ap.parse_args(argv)

    files = []
    for p in args.paths:
        if os.path.isdir(p):
            files.extend(glob.glob(os.path.join(glob.escape(p), "**", "*.orarep"), recursive=True))
        else:
            files.extend(glob.glob(p) or [p])
    files = sorted(set(files))
    if args.since is not None:
        cutoff = dt.datetime.now().timestamp() - args.since.total_seconds()
        files = [f for f in files if os.path.exists(f) and os.path.getmtime(f) >= cutoff]
    if not files:
        print("no replays found", file=sys.stderr)
        return 1

    if args.out_dir:
        os.makedirs(args.out_dir, exist_ok=True)

    failures = 0
    for f in files:
        try:
            d = digest(f, include_ips=args.ips, chat_limit=args.chat_limit)
        except Exception as e:  # a corrupt file must not stop the batch
            failures += 1
            print(f"{f}: {e}", file=sys.stderr)
            continue
        text = render_text(d) if args.text else json.dumps(d, ensure_ascii=False)
        if args.out_dir:
            base = os.path.splitext(os.path.basename(f))[0]
            outputs = [(".txt", render_text(d)), (".json", json.dumps(d, ensure_ascii=False))] if args.both \
                else [((".txt" if args.text else ".json"), text)]
            for ext, body in outputs:
                with open(os.path.join(args.out_dir, base + ext), "w", encoding="utf-8") as w:
                    w.write(body + "\n")
            name = base + ("" if args.both else outputs[0][0])
            if not args.quiet:
                print(f"{f} -> {name} ({len(d['chat'])} chat lines, {d['game']['last_frame']} frames)", file=sys.stderr)
        else:
            print(text)
            if args.text:
                print()
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
