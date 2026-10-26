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
- A player whose **connection drops out** freezes only their own game for the length of the
  dropout, then catches up; nobody else notices. A dropout never slows the game for anyone.
  A player who would rather have a smooth game than a prompt one can ask the server to buffer
  the game for them with **`!buffer 1.5`** (their own commands then take that much longer),
  and a host can have that happen by itself with `Server.MaxPlayerBuffer`.
- A player whose **computer** can't keep up (long games, many units) slows the game down
  **smoothly** instead of the stock stop-start stutter, to what that computer was measured
  managing, and the game then sits just under that ceiling and edges back up as the load
  passes, rather than speeding up and slowing down every minute. Never below the server's
  **minimum game speed** (by default 85%, 80% from 10 players, 75% from 12 and 72% from
  14): a computer that needs less is left behind, like a spectator, and catches back up at
  turbo speed if its load drops. Each player is told the minimum when they join the lobby
  and everyone when the map changes; the lobby admin can set it for one game with
  **`!minspeed`**. Above the minimum it is the players' call. The player
  concerned is told privately, once — *"Name: the game is slowed to 78% because your
  computer is not keeping up (it is managing about 80%). Close other programs; on a
  laptop, plug in the charger and pick the performance power mode."* — and given the
  all-clear when they are keeping up again. The others are not told, unless the host
  enables the speed messages for everyone or they ask for them (`!speed on`); a player
  left behind by the floor, and one the game has stopped waiting for, are announced.
- Type **`!help`** in the chat (lobby or game) for what the server is, its commands and its
  settings; **`!speed`** to ask the current game speed (`!speed on` to be told of every
  change); **`!quiet`** to hide the server's messages for yourself; **`!map`** to read the
  map's description; **`!minspeed`** for the minimum game speed (the admin: `!minspeed 77`
  sets it for the next game, `!minspeed auto` goes back to the server's table).
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
| `Server.MinGameSpeed` | `85` | Never slow the game below this percentage for a slow computer; a computer that needs less is left to fall behind on its own, and catches up at turbo speed if it recovers. A computer measured only a little under the floor gets the game at the floor first and is left behind only once it has fallen well behind there (one reading wobbles by ten points). Above the floor it is the players' call (`!speed`, `!kickslow`). Not applied with fewer than three players. `0` = no floor, as stock. |
| `Server.MinGameSpeedByPlayers` | `10:80,12:75,14:72` | Lower floors for games with more players, as `players:percent` pairs: the floor for a game is the entry with the most players not above the number playing, or `MinGameSpeed` below the smallest. Bigger games mean bigger maps, more computers, and more lost by leaving players behind. The lobby admin can override both for one game with `!minspeed <percent>` (`0`: none; `auto`: back to the table); the override lasts until the game ends or the lobby empties. Empty: `MinGameSpeed` for every game. |
| `Server.MaxPlayerBuffer` | `0` | Largest buffer (ms) built automatically for a player whose connection drops out repeatedly; costs only that player's own input delay. `0`: none unless the player asks (`!buffer`). Players who had it turned it off: recency beats smoothness. |
| `Server.MaxCatchUpSpeed` | `400` | Fastest speed (percent) a player far behind is asked to run at to catch up; players slightly behind are asked for much less. |
| `Server.MaxWaitForStalledPlayer` | `3000` | Longest the game pauses (ms) for a player who stops responding, before continuing without them. `0` never pauses. |
| `Server.StartDelay` | `2000` | Wait (ms) after the last player has finished loading before the game starts, so that player's graphics warm up like everyone else's did while they waited for them. `0` starts at once. |
| `Server.AnnounceGameSpeed` | `True` | ZeroLag's chat lines: the slow player told privately, once; everyone told of a player left behind by the floor or one the game stopped waiting for. Players can `!quiet` them. `False`: none (log only). |
| `Server.AnnounceSpeedChanges` | `False` | Also tell everyone each time the game slows down and when it is back to full speed (at most one line every 30 s). `False`: only players who type `!speed on`. |
| `Server.NameSlowestPlayer` | `False` | Name the player the game is slowed down for in the public messages. |
| `Server.VoteKickSlowest` | `False` | Enable the `!kickslow` vote. Also requires the stock `Server.EnableVoteKick`. |
| `Server.ZeroLagNotice` | `True` | One line to each player on joining the lobby: "server: type !help for special server info and commands." Sent after your `motd.txt`. |
| `Server.ShowMapBriefing` | `False` | Print the map's briefing text (`MissionData` → `Briefing`) in the lobby chat when the map is chosen and to each player who joins, as stock does. Off by default because maps use it for long change logs that push the chat off the screen: players get one line saying the map has a description and that `!map` shows it. |
| `Server.LobbyOptions` | `explored=True,reusable-engineers=True,bounty=True` | Lobby options the server prefers, as `id=value` pairs: applied to every map that offers the option and does not lock it, in place of the map's default, as if the admin had set them; the admin can still change them for their game. `none` leaves the maps' own defaults alone. They are put back (and every other option back to its map default) after every game and whenever the lobby empties, so the next group starts from the host's settings. Players joining are told which options differ from the map's defaults, as stock does for an admin's changes. |
| `Server.PaceLog` | `True` | Dedicated servers: write `Logs/zerolag-pace.log`, one line per player per second with what the server measures about the player (frames behind, of which in flight and backlog, ms behind, buffer, speed asked of them) next to what the player's own game reports in its ping replies (frames received but not yet run). CSV with a header line; a few MB per hour of a full game. Nothing is decided from it. |

**The server log** (`Logs/dedicated-server.log`) records every slowdown with the player's
name and what their computer managed, every dropout that was turned into buffer, lists
players who are behind every 10 s (and why), and writes a one-line summary for each player
when they leave, including their connection's dropouts. `grep -E "Slowing|full speed|behind|dropped out|Summary for"` is a good start.

**The pace log** (`Logs/zerolag-pace.log`, v1.4) is the same story as numbers, once a second per
player: the server's estimate of how far behind each player is and how much of that is their own
computer (`backlog_frames`), beside the player's own report of frames waiting to be run
(`queue_frames`). When the two agree the server's picture is right; when the queue is empty but the
server sees lag, the delay is on the network, not in the computer.

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
