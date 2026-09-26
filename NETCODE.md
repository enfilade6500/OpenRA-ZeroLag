# OpenRA server-side netcode improvements

This fork changes **only the dedicated server**, on top of OpenRA
**release-20250330**. Unmodified release clients connect and play as normal; the
network protocol is unchanged. The goal is that one player's slow or unreliable
connection no longer freezes everyone else.

All changes are in `OpenRA.Game/Server/` (plus one server setting and one test file).
They are a series of commits on top of the `release-20250330` tag, so they can be read
and reviewed one at a time.

## The problem

OpenRA is lockstep: every client simulates the whole game and may only advance a frame
once it has every player's orders for it. Orders are scheduled a fixed delay ahead
(~360ms at normal speed). If one player's orders arrive late, every other player waits,
so a single bad connection produces repeated game-wide freezes.

## What changed

**1. Relay orders immediately.** The stock per-connection thread only flushed its send
queue after a 100ms poll, so relayed orders (and recovery from every stall) could wait
up to 100ms. Sending now happens on its own thread as data is queued.

**2. Server-clock frame scheduling** (`FrameScheduler`). In multiplayer the server no
longer assigns each client's orders to `frame + OrderLatency`. It closes one frame per
net-frame period on its own clock, containing whatever each client has sent since the
last frame — nothing if a client's packet is late, or several merged if it is catching
up. Release clients accept this unchanged: frames still arrive in order with no gaps,
and the sender learns where its orders landed from the existing acknowledgement packet
(count 0 = empty frame, >1 = merged). Each client is kept a small, steady distance
behind using the existing per-client TickScale message; a client that falls behind runs
slightly faster to catch up.

**3. Slow computers.** If a player's *computer* can't keep up (long games with many
units), the server slows the whole game **smoothly** to their pace, instead of the stock
server's freeze-run-freeze stutter. By default there is no floor, exactly like the stock
server: the game follows the slowest computer however slow it is, and the players decide
via vote-kick whether to wait — the log names who is slowing the game and by how much. A
host can set `Server.MinGameSpeed` (e.g. 75) to protect the majority instead: a player
who would need the game slower than that is left to fall behind on their own (which, for
that player, is much like being kicked, so it is off by default). `Server.MaxPlayerLag`
(ms) lets a slow player absorb that much lag alone before any shared slowdown starts,
which covers temporary load such as big battles. A player who is still sending frames,
however slowly, never pauses the game; only a player who has stopped responding does
(which keeps the connection-problems / vote-kick flow).

**4. Spectators and defeated players never slow the game.** A spectator (no lobby slot),
or a player once they have been defeated, is kept in lockstep and relayed, but the game
never waits for them and is never slowed for them. If they can't keep up they fall behind
on their own (and are told to run flat out so they catch up if their machine recovers).

**5. Diagnostics in the server log.** When the game is slowed, the log names the player,
how fast their computer is managing and how far behind they are; lists players more than
0.5s behind every 10s; records waits; and writes a one-line per-player summary when each
player leaves, including what their computer was seen to manage (see *Measuring each
player's computer* below).

**6. Game chat.** Players are told when the game is slowed down and when it is back to
full speed — "Slowing the game to 78% so that the slowest computer can keep up." — at
most one message every 30 seconds, and only for changes of 10 points or more, so a long
game does not fill the chat. The player concerned is told privately ("The game has been
slowed to 78% because your computer can't keep up..."); by default nobody else is told
who it is. Anyone can type `!speed` to ask. Two options, both off by default:
`Server.NameSlowestPlayer` names the player in the public messages, and
`Server.VoteKickSlowest` lets players type `!kickslow` to vote to kick whoever the game
is currently slowed down for, without needing to know who it is (same majority, timeout
and cooldown rules as the normal vote kick). Commands start with `!` because the
client handles `/` commands itself and never sends them. (Sending chat and kicking are
things the stock server already does; no client change is needed for any of this.)

**7. `Server.Netcode` switch.** `dynamic` (default) uses the new scheduler; `classic`
falls back to the fixed-latency relay (still with the immediate-relay and pacing
improvements) for A/B testing or rollback without swapping binaries.

Singleplayer and saved games keep the original scheme.

## Trade-off

Each player's input delay now tracks **their own** connection, not the worst in the
game. Players on good connections get lower delay (~280ms vs ~400ms); a player on a bad
connection feels more of their own delay, and nobody else is affected. This is how most
modern lockstep relay servers behave, but it is a real behavioural change and should be
described to players.

## Measuring each player's computer

A lockstep client can never run ahead of the frames it has been sent, so while the game
is slowed to the slowest computer the others only show that they *can* keep up at that
speed — their headroom is invisible. What the server can and does measure:

- **The slowest player: an actual measurement.** They are running flat out and still
  falling behind, so their frame rate *is* their computer's capacity ("managing 56%").
- **Everyone else: a lower bound, from catch-ups.** Whenever a client works through a
  backlog — after a hitch, a network dropout, or a pause for someone else — it is told to
  run faster, and the rate it manages while it still has frames waiting is at least what
  its computer can do. Most players get a few of these per game for free. The client is
  only ever asked for up to ~143% of normal speed, so the bound is capped there.

Both appear in the per-player summary in the log: *"Their computer managed 52% while it
was slowing the game, and at least 60% at best."* A player whose line only says "at
least 143%" never limited anything. Measuring headroom actively would mean deliberately
holding frames back from a player to give them a backlog to burn through — a visible
hiccup — so it is not done; the passive bounds are enough to tell who is close to the
edge.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `Server.Netcode` | `dynamic` | `dynamic` = server-clock scheduler; `classic` = fixed-latency relay. |
| `Server.MaxPlayerLag` | `0` | ms a slow-PC player may fall behind before the whole game is slowed. |
| `Server.MinGameSpeed` | `0` | `0` = no floor (follow the slowest PC, as stock does). `75` = never slow below 75%; a PC needing less lags alone. `100` = never slow anyone. |
| `Server.AnnounceGameSpeed` | `True` | Tell players in the chat when the game slows down / speeds up (rate-limited). The slow player is told privately. `!speed` always works. |
| `Server.NameSlowestPlayer` | `False` | Name the player the game is slowed down for in the public chat messages and in `!speed` replies. |
| `Server.VoteKickSlowest` | `False` | Players can type `!kickslow` to vote to kick whoever the game is currently slowed down for. Needs `EnableVoteKick` (stock, default on). |

## Compatibility

Works with unmodified release-20250330 clients. Invariants preserved: each sender's
frames arrive strictly consecutive (else the release client throws); acknowledgement
counts never exceed what the client has queued; disconnect markers land on the frame
after a client's last; recorded replays play back identically. Also fixes a latent
crash where starting a game with no valid clients terminated the server process.

## Limitations

- A player whose PC can't keep up still slows the game for everyone (smoothly, and the
  log says who), as in the stock game. `Server.MinGameSpeed` trades that for leaving the
  slow player behind, and `Server.VoteKickSlowest` lets the players decide.
- When the player the game is slowed down for is kicked, leaves or is defeated, the game
  returns to full speed at once and says so, so the effect is plain to see. (A slow
  computer that merely *recovers* is ramped back up gradually, since it may dip again.)
- A connection dead for more than ~2s still makes everyone wait (by design; preserves
  the "connection problems" / vote-kick flow).
- A player on a spiky connection sees their own brief freezes. Per-connection adaptive
  buffering (sizing each player's buffer to their own jitter) is possible future work.

## Testing

`netcode-tests/` contains a harness of headless clients over simulated bad connections,
plus `OpenRA.Test/FrameSchedulerTest.cs` (run by `make tests`). Every scenario checks
lockstep across all clients and replay playback. See `netcode-tests/README.md`.

**These tests model the client's networking and timing, not real rendering or
simulation load.** Validate with real games before relying on it.

## Status

Unofficial and experimental. Not affiliated with the OpenRA project. GPLv3, like OpenRA.
