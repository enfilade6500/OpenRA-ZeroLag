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
server's freeze-run-freeze stutter — but not below `Server.MinGameSpeed` (default 30):
a player who would need the game slower than that is left to fall behind on their own,
still relayed and never waited for, and is asked for turbo speed so that they catch back
up if their load drops. `0` removes the floor, which is what the stock server does: the
game follows the slowest computer however slow it is. `Server.MaxPlayerLag` (ms) lets a
slow player absorb that much lag alone before any shared slowdown starts; the default of
3 s covers temporary load such as big battles, at the cost of that player feeling their
own delay while they are behind. Only a computer's *smooth* shortfall can slow the game;
dropouts and freezes are handled per player (see v1.1 below). A player who is still
sending frames, however slowly, never pauses the game; a player who has stopped
responding does, for at most `Server.MaxWaitForStalledPlayer`.

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
| `Server.MaxPlayerLag` | `3000` | ms a slow-PC player may fall behind (lagging alone) before the whole game is slowed for them. |
| `Server.MinGameSpeed` | `30` | Never slow below this for a slow PC; a PC needing less lags alone and catches up at turbo speed if it recovers. `0` = no floor (follow the slowest PC, as stock does). `100` = never slow anyone. |
| `Server.MaxPlayerBuffer` | `1500` | Largest buffer (ms) built for a player whose connection drops out. `0` disables adaptive buffering. |
| `Server.MaxCatchUpSpeed` | `400` | Fastest speed (percent) a client far behind is asked to run at. |
| `Server.MaxWaitForStalledPlayer` | `3000` | Longest pause (ms) for a player who stops responding, then the game continues without them. `0` never pauses. |
| `Server.AnnounceGameSpeed` | `True` | Tell players in the chat when the game slows down and when it is back to full speed (rate-limited). The slow player is told privately. `!speed` always works; `!quiet` hides the messages for that player. |
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
- A connection dead for more than a few seconds makes everyone wait for up to
  `Server.MaxWaitForStalledPlayer` (3 s); the "connection problems" dialog and the 60 s
  drop still come from the stock code, while the game goes on.
- A player on a spiky connection feels their own delay grow, up to `Server.MaxPlayerBuffer`,
  in exchange for a game that does not stop; holes longer than that still stop their game,
  and they catch up at turbo speed afterwards.

## v1.1: bad connections, and the sawtooth

What the first day of public games (v1.0 on seven servers, 35 games) showed, and what
changes because of it. The numbers come from the server logs and from the server-side
replays: a client's sync report for frame N is written into the replay the moment it
arrives, and frame closings are written when they happen, so the file order gives every
client's arrival pattern to one frame period (`netcode-tests/tools/arrival.py`).

### Findings

- **Two kinds of "can't keep up", and the server only knew one.** A computer that is too
  slow shows a *smooth* shortfall: its frame reports arrive at a steady cadence a little
  slower than the game. A bad connection shows *holes*: nothing for 0.3–1.4 s (a TCP
  retransmission timeout, doubling on repeated loss), then normal cadence again, or a
  burst of everything that was queued. v1.0 averaged both into one rate and reported it as
  "their computer is managing X%". In the worst game a player whose computer had measured
  136% was reported at 17% during a burst of dropouts, the game was slowed to 17% for four
  minutes, the other players kicked a spectator by mistake, and everyone left.
- **A stall is not a rate.** Three seconds of nothing followed by 136% averages to
  "managing 20%", and slowing the game to 20% helps nobody: the stalled player wasn't
  running anyway, and everyone else is now crawling.
- **The client cannot ride out a hole.** It keeps every frame only ~150 ms before it
  needs it, so any dropout longer than that stops its simulation, and afterwards it
  can only recover the lost time at 143%: freeze, then a stretch at "faster" speed, then
  normal, over and over. That is what a player on a lossy link experienced.
- **Recovery is additive** (1–3 points per second), which is fine from 80% but takes
  eight minutes from 17%.
- **The sawtooth.** A computer that can sustain 60% gets the game slowed to 60%, catches
  up, the recovery ramps the game back towards 100%, it falls behind again, and around
  it goes: 26 slowdowns and a dozen chat messages in one 28-minute game.
- **Dead connections froze everyone for up to 60 s** (four games), because the
  last-resort wait for a stopped player had no limit.
- Connection dropouts alone almost never mattered: the worst rate seen was 2.5% of the
  game, and players with capable computers absorbed that without anyone noticing. They
  matter when a computer is near 100% capacity (the small extra shortfall tips it over)
  and, above all, when the dropouts are misread as a slow computer.

### Changes

**Connection side.** A client's shortfall is attributed before anything acts on it:
gaps in its packet arrivals of more than 2.5 frame periods (and three times its own
cadence) are *holes*, everything else is *smooth*. Holes are further classified by what
follows them: the queued packets arrive in a burst (the upload path stalled; the
simulation didn't), or the client simply resumes (the download path or the whole
connection stalled, or its game froze). Those last two look identical from the server —
the release client answers pings on its game thread, so even those stop — but the buffer
tells them apart after the fact: a download dropout shorter than a client's buffer is
played through and leaves no hole, so a hole that the buffer should have covered can only
be a freeze. A freeze that shows a buffer to be pointless takes it away again.

1. *Adaptive per-player buffer.* Each client has its own target buffer instead of the
   fixed 150 ms. When download-side holes stop a client twice within two minutes (one
   hiccup costs nobody anything; in the first day's replays 37% of players had one hole,
   16% had two within two minutes), the time it lost is converted into buffer rather
   than caught up: its delay grows by the length of the hole, up to
   `Server.MaxPlayerBuffer` (default 1500 ms), and the next hole of that length no longer
   stops it at all, because it keeps playing from the buffer while the link is dead and
   the frames arrive in a burst afterwards. The buffer shrinks back gently (half-life two
   minutes) by running the client a few percent fast. Nobody else's delay changes; the
   server still forwards that player's orders the moment they arrive. Growing costs the
   player nothing extra (the stall had already happened); shrinking is imperceptible.
   The player is told once, privately, and can opt out or set it by hand: `!buffer off`,
   `!buffer auto`, `!buffer 1.5`.
2. *Stalls don't set the pace.* Only a client's *smooth* shortfall (its rate with the
   holes taken out) can slow the whole game, and that is the rate reported. A player who
   is fine between dropouts never slows the game; a hole longer than the buffer is theirs
   to absorb (the lag budget) and to catch up.
3. *Graduated catch-up.* How much faster a client is asked to run grows with how far
   behind it is: about 1 + 0.6 × (seconds behind), so 0.5 s behind is 1.3×, 1 s is 1.6×,
   and 5 s or more is the cap, `Server.MaxCatchUpSpeed` (default 400%). Small hitches stay
   gentle; a player far behind fast-forwards, which is right, because their orders are
   based on a world several seconds old whatever the speed. The formula also means a
   client closes at most its whole deficit per control interval, so it cannot overshoot
   into running dry. The real ceiling is the client's own cost per tick (it renders every
   tick), so asking for more than it can do is safe: it runs flat out.
4. *Bounded wait for a stopped player.* The last-resort wait now applies only to a
   player who was keeping up and then went silent, and lasts at most
   `Server.MaxWaitForStalledPlayer` (default 3000 ms); then the game continues without
   them, and if they return they catch up at turbo speed. The stock "connection problems"
   dialog and the 60 s drop are unchanged. (The pause only begins once the player has been
   silent for a few seconds and is beyond their lag budget, so an 8 s outage costs the
   others about three seconds, a 60 s one the same three seconds instead of sixty.)

**Compute side.**

5. *Probe and hold.* Speeding back up is a probe: once everyone is keeping up the speed
   is raised by half a point per second, doubling every ten seconds while it succeeds (up
   to four points per second). As soon as a player starts falling behind while the probe
   runs, it has failed: the speed goes straight back to where the probe started, without
   waiting for that player to use up their lag budget, and the next probe waits 30 s,
   doubling per failure up to two minutes. The first slowdown for a player is never held,
   since it may be a passing load. A computer at a hard ceiling is therefore found out
   within a few points of it and then tested once in a while, instead of being pushed ten
   points past it every twenty seconds.
6. *Fast recovery when the load passes.* The same accelerating probe gets a game that was
   slowed for a battle back to full speed in about half a minute once the battle is over
   (45% → 100% in ~35 s), where v1.0 crept up a point at a time and took eight minutes
   from 17%.
7. *A floor by default.* `Server.MinGameSpeed` defaults to 30. A player whose computer
   needs the game slower than that is left behind (like a spectator: relayed, never waited
   for) instead of dragging everyone to a crawl; they are asked for turbo speed the whole
   time, so if their load drops they can rejoin the present. The floor is deliberately
   low: on maps played with very large armies the slowest computer routinely sets a pace
   of 50–70% during the big battles and the players accept that as part of the map (a
   two-player game on "Grand massacre" ran at 48–82% for six minutes and finished happily;
   the only game seen below 30% was the 17% dropout case that attribution now prevents).
   Above the floor the decision belongs to the players: `!speed` says what is happening,
   and `!kickslow` (if the host enables it) lets them act on it. With fewer than three
   players the floor does not apply at all — there is no majority to protect, and leaving
   one of two players behind ends the game for both.

**Messages and log.**

8. Chat says which it is: "…so that X's connection can keep up" is never said, because a
   connection never slows the game; "Slowing the game to 60% so that the slowest computer
   can keep up" is only said for a smooth shortfall. The private hint tells the truth:
   "Your connection dropped out for 1.4 s; the server now buffers 1.5 s for you…" versus
   "…because your computer can't keep up…". Recoveries are announced only when the game is
   back to full speed; a player left behind by the floor, and a stopped player the game
   has continued without, are announced.
9. `!quiet` silences the speed messages for the player who types it; `-speed` is accepted
   as well as `!speed` (players guessed it).
10. The per-player summary line gains the connection verdict — "dropped out 30 times,
    12.4 s in total, longest 1.4 s; buffer reached 1.5 s" — and a recent capacity estimate
    ("at least 125% in the last five minutes") alongside the all-time best.

### New settings

| Setting | Default | Meaning |
|---|---|---|
| `Server.MaxPlayerBuffer` | `1500` | Largest buffer (ms) the server will build for a player whose connection drops out, at the cost of that player's own input delay. `0` disables adaptive buffering (fixed 150 ms for everyone, as in v1.0). |
| `Server.MaxCatchUpSpeed` | `400` | Fastest speed (percent of normal) a client far behind is asked to run at while catching up. |
| `Server.MaxWaitForStalledPlayer` | `3000` | Longest the game pauses (ms) for a player who was keeping up and has stopped responding, before continuing without them. `0` never pauses. |
| `Server.MinGameSpeed` | `30` (was `0`) | Floor for slowing the game for a slow computer; a player needing less is left behind. |

Everything else that changed is an internal constant derived from things the server
already measures: the hole threshold (2.5 frame periods and 3× the client's own cadence),
the burst window (20 ms), the buffer half-life (120 s), the catch-up formula
(1 + 0.6 × seconds behind), the probe rate (0.5 points/s, doubling every 10 s to 4) and
the hold (30 s, doubling to 120 s). They are listed at the top of `FrameScheduler.cs`
with their reasons.

### How the defaults were chosen, and how to tune

Each pattern seen in the real games is a harness scenario built from the measured
numbers — the hole train (bursts of 0.3–1.4 s download-side holes every minute, capable
computer), the smooth decline (88% to 32% over seven minutes), loss plus load (a 400 ms
hole three to eight times a minute all game, computer at ~100% late on), the 8 s outage,
the dead connection — each with a score: for the player with the bad connection, the
fraction of time their game is stalled, their input delay, and the slowdowns they caused;
for the slow computer, speed changes per minute and time under 50%; for everyone else,
nothing at all. A default is good when the table is green; the log after real games is
the check on the harness.

## Testing

`netcode-tests/` contains a harness of headless clients over simulated bad connections,
plus `OpenRA.Test/FrameSchedulerTest.cs` (run by `make tests`). Every scenario checks
lockstep across all clients and replay playback. See `netcode-tests/README.md`.

**These tests model the client's networking and timing, not real rendering or
simulation load.** Validate with real games before relying on it.

## Status

Unofficial and experimental. Not affiliated with the OpenRA project. GPLv3, like OpenRA.
