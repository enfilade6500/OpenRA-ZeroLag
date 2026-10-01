# OpenRA netcode test harness

Runs a real OpenRA dedicated server (stock or patched) against headless fake
clients over simulated bad network connections, and measures what players would
experience.

## What the fake clients do

`src/FakeClient.cs` speaks the release network protocol using the release's own
`OpenRA.Game.dll` for every packet it builds or parses, and reproduces the
release client's pacing logic line by line:

- `Game.Loop` / `Game.InnerLogicTick` (logic ticks, the 250ms "don't catch up" rule)
- `OrderManager.TryTick` / `ProcessOrders` (lockstep gating, net frames, the
  "Attempted to process orders ... for frame N on frame M" crash check)
- `TickTime` (the release class itself) and TickScale handling
- `NetworkConnection.Receive` (acks, sync packets, pings with queue length)

The game world is replaced by a configurable per-tick CPU cost (with optional
random hitches), and the world sync hash is replaced by a hash of the order stream.
The clients exchange these hashes through the server exactly like real sync
hashes, so if the server ever delivers different orders to different clients, or
delivers them out of order, the run reports a desync or the release client's
crash.

Players issue orders at random (default 180 APM); each order carries a timestamp so
input delay can be measured.

## Simulated networks

`src/Link.cs` puts a userspace TCP proxy between each client and the server with
separate settings per direction: fixed delay, jitter, and random "freezes" that
hold back all traffic for a while (what a TCP retransmit after packet loss, or a
Wi-Fi hiccup, looks like to the game). Byte order is always preserved, like TCP.

## Scenarios

| name | what it simulates |
|---|---|
| clean | 4 players, all ~30ms round trip |
| slowlink | p4 has ~300ms round trip with +/-30ms jitter |
| spikes | p4 has ~80ms round trip plus 300ms freezes about every 3s each way |
| hitch | p4's PC stalls for 400ms about every 5s |
| slowcpu | p4's PC needs 44ms per 40ms tick (~90% speed at best) |
| chaos | every player has a different problem, 600 APM |
| leave | p4 quits mid-game; a 5th client never readies and is kicked at start |
| holetrain, decline, lossload, outage8, deadlink, plateau, floorreturn | patterns measured in real games (see NETCODE.md, v1.1) |
| melo | the 14-player game from v1.1's first day: p4's PC fine, then ~60% for two minutes, then ~85%, then fine (run 420s) |
| wander | a PC whose ceiling wanders between ~66% and ~92% for eight minutes (run 480s) |
| nextslowest | p4 at ~60% and p3 at ~80%; p4 leaves after two minutes and p3's ceiling must be found gently (run 240s) |
| offsethold | the stuck hold of v1.2 (NETCODE.md, v1.3): p4's PC is at ~70% for a minute and then fine, but hands frames to its game 600ms late while answering pings at once, so its lateness reads 600ms high (run 240s) |

`tools/score12.py v11,v12,v13` scores these (and the regression suite) per server build;
`tools/policysim.py` is the one-second model of the control loop the v1.2 and v1.3 policies
were chosen with (`--sweep` and `--trend-sweep` run the variants, `--trace dip v1.3` prints a
timeline). `CpuSpec.ReceiveDelayMs` is the harness knob behind `offsethold`.

## Metrics (per client, after a 5s warm-up)

- **speed%**: game time simulated / wall time. 100% = the game runs at full speed.
- **freezes/min**: gaps of 100ms or more between world ticks (a visible freeze).
- **frozen-ms/min**: total time lost to those freezes.
- **max-gap**: the longest freeze.
- **own-lat / remote-lat**: time from an order being issued to it taking effect,
  for the player's own orders and for other players' orders.
- **scale**: range of TickScale values the server sent.
- **queue**: average buffered frames reported in ping replies.

## Running

Needs the .NET 8 SDK (to build the harness) and an extracted release AppImage.
The paths at the top of `build.sh` and the `tools/*.sh` scripts point at this
machine's layout; adjust them for yours.

```sh
./build.sh
tools/run-scenario.sh <server-variant> <scenario> <seconds> <seed> <port>
python3 tools/compare.py stock,fork 1,2,3
```

`tools/run-server.sh` starts a dedicated server from `servers/<variant>`, a copy of
the release's `usr/lib/openra` directory with the `OpenRA.Game.dll` under test.
`tools/testhost/` builds a copy of `OpenRA.Server.dll` that listens on IPv4 only,
because the test container has no IPv6. It isn't needed on a normal machine.

`--crashtest 1` makes the lobby admin start the game before anyone has reported
ready. That crashes the stock release dedicated server.

`--defeatbit N --defeatframe F` makes every client report world player N as defeated
from net frame F on (scenario `defeated`), to test that a defeated player stops
slowing the game.

`--votekick 1` with scenario `votekick` (and the server started with
`Server.VoteKickSlowest=True`) scripts the chat: once the server has announced a
slowdown, p1 sends `!speed`, then p1, p2 and p3 send `!kickslow`. The run passes only if
the slow player receives the kick message and is disconnected while the others keep
playing in lockstep; the chat every client saw is printed in the checks.

## Also in this directory

- `src/SchedulerTests.cs` + `tools/run-tests.sh` — the FrameScheduler / announcer unit
  tests as a standalone program, for machines without NUnit. The same assertions live in
  `OpenRA.Test/FrameSchedulerTest.cs` and run under `make tests`.
- `tools/build-game.sh` — compiles `OpenRA.Game.dll` with `csc` directly against the
  release's dependency DLLs and the .NET 6 reference assemblies, for a machine without
  NuGet access. Prefer `make` when you can.
- `tools/replaychat.py` — searches `.orarep` replays for chat about lag, slowness, the
  server, and the ZeroLag chat messages, so complaints can be matched against the log.
