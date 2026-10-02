# ZeroLag — a server-side patch set for OpenRA

ZeroLag is an unofficial fork of [OpenRA](https://github.com/OpenRA/OpenRA) that changes
**only the dedicated server**, so that one player's slow connection or slow computer no
longer freezes the game for everyone else.

- **Players need nothing.** The normal `release-20250330` client connects and plays as
  usual; the network protocol is unchanged. ZeroLag servers appear in the ordinary server
  list.
- **It is not a mod.** Red Alert, Tiberian Dawn and Dune 2000 are untouched. The changes
  are in the server's networking code (`OpenRA.Game/Server/`), one settings file and one
  test file.
- **Not affiliated with the OpenRA project.** Licensed under the GPLv3, like OpenRA.

The branch `zerolag` is the `release-20250330` tag plus the ZeroLag commits, so the whole
change can be read as a short series with `git log release-20250330..zerolag`.

## What changes for players

In stock OpenRA every player waits for the slowest player's orders before the game can
advance, so one bad connection produces game-wide freezes. On a ZeroLag server:

- Your input delay follows **your own** connection, not the worst one in the game. A player
  on a bad connection feels their own delay; nobody else does.
- A player whose **connection drops out** gets a buffer sized to their dropouts (up to
  1.5 s), so their game keeps running through them instead of freezing and fast-forwarding.
  Only their own commands take longer; nobody else is affected. They are told, and can turn
  it off or set it themselves with **`!buffer`**. A dropout never slows the game for anyone.
- A player whose **computer** can't keep up (long games, many units) slows the game down
  **smoothly** instead of the stock stop-start stutter, to what that computer was measured
  managing, and the game then sits just under that ceiling and edges back up as the load
  passes, rather than speeding up and slowing down every minute. Never below 30% by
  default: a computer that needs less is left behind, like a spectator, and catches back up
  at turbo speed if its load drops. Above that it is the players' call. The chat says so —
  *"Slowing the game to 78% so that the slowest computer can keep up."* — and says when it
  is back to full speed. The player concerned is told privately, by name, with the two
  display settings to change, and given the all-clear when they are keeping up again; the
  others are not told who it is unless the host enables that.
- Type **`!speed`** in the chat to ask the current game speed, **`!quiet`** to hide the speed
  messages for yourself.
- If the host enables it, type **`!kickslow`** (or just `kickslow`) to vote to kick whichever player the game
  is currently being slowed down for, without needing to know who it is. A majority of the
  players still in the game is needed; votes stand for as long as that player keeps the
  game slow, so they can be typed whenever each player gets fed up.
- Spectators and defeated players never slow the game or make it wait.

The trade-off: players on good connections get *lower* delay than before (about 280 ms
instead of 400 ms at normal speed); a player on a bad connection feels more of their own
delay. A connection that stops responding entirely pauses the game for everyone for at most
three seconds (after a few seconds of silence), then the game continues without that player;
the stock "connection problems" dialog and 60 s drop are unchanged, and the player catches
up if they come back.

## For server hosts

**Requirements:** an OpenRA `release-20250330` dedicated server (any OS). Nothing changes
for your players.

**Install:** download `OpenRA.Game.dll` from the [Releases](../../releases) page, check its
SHA-256 against the one on the release, and replace the file of the same name in your
server's `lib/openra` directory (on the Linux AppImage layout that is
`usr/lib/openra/OpenRA.Game.dll`). Keep a copy of the original to roll back. Restart the
server. See [SERVER-INSTALL.md](SERVER-INSTALL.md) for a step-by-step version with backup and rollback.

**Settings** (all optional; pass them like any other server setting, e.g.
`Server.VoteKickSlowest=True`):

| Setting | Default | Meaning |
|---|---|---|
| `Server.Netcode` | `dynamic` | `dynamic` = ZeroLag scheduling; `classic` = the original fixed-latency relay, for A/B testing or rollback without swapping files. |
| `Server.MaxPlayerLag` | `3000` | Milliseconds a slow-PC player may fall behind (absorbing the lag alone) before the whole game is slowed for them. 3 s means temporary dips such as big battles usually never touch the other players; `0` slows everyone as soon as anyone falls behind. |
| `Server.MinGameSpeed` | `30` | Never slow the game below this percentage for a slow computer; a computer that needs less is left to fall behind on its own, and catches up at turbo speed if it recovers. Above the floor it is the players' call (`!speed`, `!kickslow`): maps played with huge armies routinely run at 50–70% and their players accept that. Not applied with fewer than three players. `0` = no floor, as stock. |
| `Server.MaxPlayerBuffer` | `1500` | Largest buffer (ms) built for a player whose connection drops out; costs only that player's own input delay. `0` disables it. |
| `Server.MaxCatchUpSpeed` | `400` | Fastest speed (percent) a player far behind is asked to run at to catch up; players slightly behind are asked for much less. |
| `Server.MaxWaitForStalledPlayer` | `3000` | Longest the game pauses (ms) for a player who stops responding, before continuing without them. `0` never pauses. |
| `Server.StartDelay` | `2000` | Wait (ms) after the last player has finished loading before the game starts, so that player's graphics warm up like everyone else's did while they waited for them. `0` starts at once. |
| `Server.AnnounceGameSpeed` | `True` | Chat messages when the game slows down and when it is back to full speed (at most one every 30 s); players can `!quiet` them. |
| `Server.NameSlowestPlayer` | `False` | Name the player the game is slowed down for in those messages. |
| `Server.VoteKickSlowest` | `False` | Enable the `!kickslow` vote. Also requires the stock `Server.EnableVoteKick`. |
| `Server.ZeroLagNotice` | `False` | One line to each player on joining the lobby: that this is a ZeroLag server and which commands exist. Sent after your `motd.txt`. |

**The server log** (`Logs/dedicated-server.log`) records every slowdown with the player's
name and what their computer managed, every dropout that was turned into buffer, lists
players who are behind every 10 s (and why), and writes a one-line summary for each player
when they leave, including their connection's dropouts. `grep -E "Slowing|full speed|behind|dropped out|Summary for"` is a good start.

## How it works

[NETCODE.md](NETCODE.md) describes the design: the server closes one network frame per
period on its own clock and forwards whatever each client has sent by then (nothing if a
client is late, several packets merged if it is catching up), keeps each client a small,
steady distance behind with the existing per-client tick-scale message — a distance that
grows for a client whose connection drops out — tells a client that has fallen behind to
run faster in proportion to how far behind it is, and slows the whole game only when a
computer genuinely cannot keep up, telling a dropout from a slow computer by the shape of
the client's packet arrivals. Release clients support all of this unchanged; the invariants
they rely on (strictly consecutive frames per sender, acknowledgement counts, replay layout)
are preserved and tested.

## Testing

- `OpenRA.Test/FrameSchedulerTest.cs` — unit tests for the scheduler and the chat
  announcer, run by `make tests` and by the CI on this repository.
- `netcode-tests/` — a harness of headless clients that speak the release protocol over
  simulated bad connections (delay, jitter, drop-outs, slow computers, spectators, defeated
  players, a scripted `!kickslow` vote). Every scenario checks lockstep across all clients
  and that recorded replays play back identically. See `netcode-tests/README.md`.

These tests model the client's networking and timing, not real rendering or simulation
load. ZeroLag has been run on public servers, but treat it as experimental and read the
log after your first games.

## Building from source

Same as OpenRA (see [INSTALL.md](INSTALL.md) for the dependencies): clone this repository, check out `zerolag`, and run `make` (or
`make check` for the analyzers and `make tests` for the unit tests). The dedicated server
is `bin/OpenRA.Server.dll`; the file that differs from the release is
`bin/OpenRA.Game.dll`.

## Status and contributing

Issues and pull requests are welcome on this repository. The OpenRA project itself is not
responsible for this fork; please don't report ZeroLag problems to them. If you run a
ZeroLag server, the server log from a game that went badly is the most useful thing you
can attach to an issue.
