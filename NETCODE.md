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
game does not fill the chat. The player concerned is told privately, a couple of seconds
later so that the line stands on its own, flagged with their own name (">>> NAME, THIS IS
ABOUT YOU: the game is slowed to 78% because your computer is not keeping up (it is
managing about 80%). Settings > Display: untick "Enable VSync", tick "Limit framerate to
game tick rate"; close other programs."), and given the all-clear when the game is no
longer slowed for them; by default nobody else is told who it is. Anyone can type `!speed` to ask. Two options, both off by default:
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
| `Server.StartDelay` | `2000` | Wait (ms) after the last player has loaded before the first frame, so their graphics warm up like everyone else's did during the freeze (v1.3). `0` starts at once. |
| `Server.AnnounceGameSpeed` | `True` | Tell players in the chat when the game slows down and when it is back to full speed (rate-limited). The slow player is told privately. `!speed` always works; `!quiet` hides the messages for that player. |
| `Server.NameSlowestPlayer` | `False` | Name the player the game is slowed down for in the public chat messages and in `!speed` replies. |
| `Server.VoteKickSlowest` | `False` | Players can type `!kickslow` (or `kickslow`) to vote to kick whoever the game is currently slowed down for. Needs `EnableVoteKick` (stock, default on). |

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

5. *Probe and hold* (replaced by the creeping hold in v1.2, below). Speeding back up is a probe: once everyone is keeping up the speed
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

## v1.2: the creeping hold

### Findings

The first day of v1.1 on the two servers (58 games, 21.6 game-hours, 285 player-sessions,
with every server replay) confirmed the connection side and found the compute side paying
for its calm with speed:

- Bounded waits, buffers and attribution behaved as designed: ten waits, six of them the
  full 3 s and then on without the player (five of those players were gone for good; one
  came back 5 s later 8.5 s behind and caught up at turbo), 51 buffers built and 20 later
  undone by the freeze rule, no slowdown blamed on a connection. v1.0's day had a 56 s
  pause and several of 8–11 s. The floor never triggered (lowest speed 34%).
- 60% of the "dropouts" in the summaries were bursts: the packets were delayed and arrived
  together, the game never stopped. One player with a 0.4 s delay every four seconds was
  reported as "dropped out 208 times".
- The slowdown *rate* was the same as v1.0's (2.0 episodes per game-hour, 16 of 58 games),
  but an episode lasted 3.1 minutes instead of 1.8, and 58% of all game time lost was spent
  in the hold after a failed probe. The probe climbs at up to four points a second, so by
  the time a player "fails" they have kept up with every speed for 20–30 s; reverting to
  where the probe *started* threw that away, for up to two minutes. In a 14-player game the
  game sat at 54% for 14 minutes while the slowest player could manage 80%; the replay
  shows his PC at 92% in minute six, 60% by minute eight (a battle), and back near 80%
  whenever a probe got high enough to see it. Nine kick votes were started against him
  before one passed.
- The hold logic only applied to failures *during* a probe. Once a probe reached 100% a
  player whose ceiling was just below it caused a fresh slowdown every minute: 22 of the
  28 first slowdowns after "back to full speed" came within 60 s.
- When the slowest player was kicked the game jumped to 100% and six of the remaining
  thirteen fell behind at once; nobody had been tested above 54% for fifteen minutes.
- Kick votes lapsed 30 s after the last vote (the stock timer, meant for a dialog everyone
  answers at once), so in a big game they kept resetting: failures at 5, 6, 6 and 7 of the
  8 needed. Players also typed `!kicklag`, `!kicksllow` and `!kickslow name`, none of which
  counted.

### The control problem

A player's computer sustains some fraction of normal speed, c(t), which moves with the game
(one player went 92% → 60% → 80% within five minutes). The server picks one pace for
everyone. The catch is that c is *censored*: it can only be measured while the pace is
above it, when the player is behind and running flat out, because in lockstep a client can
never run ahead of the frames it has been sent. To find the ceiling you have to hit it,
and hitting it costs that player lag and, past their budget, everyone a speed change.
Probing too gently leaves the game slow after the load has passed; probing too fast
overshoots, because the loop needs a second to deliver the pace and about three to confirm
a trend, so at four points a second a failure lands 12–15 points above the ceiling. This
is TCP congestion control almost literally, and the two ideas that fixed TCP apply:
operate at the measured bottleneck rate (BBR), and grow slowly near the last known
ceiling, fast away from it (CUBIC).

`netcode-tests/tools/policysim.py` is a one-second model of the loop (capacity traces
shaped like the real players, holes, measurement jitter, the client's one-interval delay,
a replica of the v1.1 controller) with the alternatives as switches. Averaged over the
slow-player scenarios (a two-minute dip, a wandering ceiling, spikes, a steady ceiling,
two slow players), 30 seeds each:

| policy | game time lost | speed changes/h | slow player's mean lag | time > 1 s behind | worst lag |
|---|---|---|---|---|---|
| v1.0 | 21.5% | 99 | 1.6 s | 60% | 5.5 s |
| v1.1 | 27.2% | 63 | 0.64 s | 20% | 5.2 s |
| v1.1 with a measured revert | 24.2% | 65 | 0.80 s | 27% | 5.2 s |
| dithered hold (±5% modulation) | 23.1% | 61 | 0.42 s | 14% | 2.9 s |
| **creeping hold (v1.2)** | **23.5%** | **39** | **0.39 s** | **13%** | **3.0 s** |

v1.0 was the fastest for the majority because it never waited, and paid with a speed change
every 36 s and a slow player 1.6 s behind on average; its one real defect was blindness to
holes (on a good PC with a bad line it lost 11% of the game; every later policy loses 0%).
v1.1 bought calm at five points of speed and got a third of the calm it should have. The
creeping hold sits two points from v1.0's speed with 60% fewer changes than v1.0 (40% fewer
than v1.1) and a slow player behind a quarter as often. (The lost-time figures are for
scenarios where a slow player is present throughout; the floor is about 21%.)

### Changes

**Compute side.**

1. *Measured revert.* A failed probe goes back to the speed the failing player was measured
   managing while they fell behind (never below where the probe started), instead of to
   the probe's start.
2. *The creeping hold* replaces the timed holds (MinHold, MaxHold and hold doubling are
   gone). After any slowdown or revert the speed creeps up by 0.3 points a second while
   the player it was slowed for keeps up, and is pulled back by 5 points a second for every
   second they are behind beyond 100 ms, moving at most a point a second and never more
   than 6% below the measured ceiling. The creep is a continuous, imperceptible probe and
   the pull-back is its answer: a computer at a steady ceiling settles a point or two under
   it and nothing else happens (in the model, 5 speed changes an hour instead of 37).
   Once that player has shown no resistance for 15 s with the speed 3% above the measured
   ceiling (a measurement is a few percent off either way, and the creep settles that on
   its own), the ceiling has moved: the doubling probe from v1.1 starts. Being more than
   200 ms behind resets the count; between 100 and 200 ms, and around a connection hole,
   nothing counts either way, so the ordinary jitter of a client's lateness does not keep a
   hold going forever.
3. *Early slowdown.* A player is slowed down for when their projected lag over the next ten
   seconds exceeds the budget (and they are at least half a second behind and falling),
   not only once the whole 3 s is used up. In the model this is what brings the slow
   player's worst lag from 5 s to 3 s, for about one extra change an hour.
4. *Probe up when the slowest player leaves.* Nobody else has been tested above the current
   speed, so the game speeds back up from where it is, starting at two points a second and
   doubling, and whoever cannot keep up is found on the way. The chat says "Speeding the
   game back up." at once and "back to full speed" when it gets there.

**Votes, messages and log.**

5. A `!kickslow` vote stands for as long as the same player keeps the game slow (a brief
   return to full speed does not clear it; two minutes at full speed, or a slowdown for
   someone else, does). `!kicklag`, `!kickslowest` and `!ks` count, words after the command
   are ignored, and any other `!kick…` gets a one-line hint. Naming the slowest player
   (`Server.NameSlowestPlayer`) remains the host's choice; the players in that game asked
   for it.
6. Summaries tell packet delays from dropouts ("their packets were delayed 175 times…
   their game kept running" versus "dropped out 21 times"), only quote the best rate seen
   when it says something, stop counting lag once a player is defeated, and a freeze only
   reclassifies earlier dropouts of about the buffer's length (a 6.7 s dead connection is
   not a freeze).
7. "Everyone is waiting for X" is logged once a wait has lasted half a second, not for
   waits of 0.0 s.

No settings were added or changed. The constants (creep rate, gain, deadband, floor, free
time, probe rates, trigger horizon) are at the top of `FrameScheduler.cs` with their
reasons; the model in `policysim.py` is how to check a change to them (`--sweep` runs the
variants), and the harness scenarios `melo`, `wander` and `nextslowest` are the same
situations against the real client timing.

## v1.3: the hold reads frames, and a breath before the start

### Findings

v1.2's first two days (71 games across both servers, with every replay) did what the model
said: episodes of about a minute instead of three, probes within a minute of a slowdown,
reverts landing on the measured speed, the slow player held about a second behind instead
of three (median 1.1 s against 2.2 s; 9% of their lag reports over 2 s against 54%), and a
quarter as many other players reported behind during slowdowns. Two games showed the
hold's one blind spot:

- **A constant offset in the lateness measure held a game at 67% for 17 minutes.** EU 1235,
  1 Oct, five players: the slowest loader was slowed to 67% seven seconds into the game,
  while his first frames were still loading, and the game never came back up until he left.
  The replay shows him running a steady 3–5 net frames behind the frontier for all of those
  minutes — healthy players sit at 2 — a fixed 0.3–0.9 s that never grew. He was keeping up
  at 67%, and would have at 100%. The creeping hold read lateness *level*: anything over
  100 ms pulled the speed back, anything over 200 ms reset the probe timer, so a client
  with a constant offset (a round trip its ping understates, or a client that hands frames
  to its game a little late) was held at the measured speed indefinitely. v1.1 would have
  probed; its gate was "within 1.5 s". The model had no such offsets; with a 600 ms offset
  added (`offset*` scenarios in `policysim.py`) it reproduces the stuck hold exactly.
- **A volatile PC produced a probe every 85 s for half an hour.** EU 1234, 1 Oct, seven
  players: a PC that kept up with probes to 85–90% and fell behind at 60–70% a minute later
  was followed up and down 22 times. That is the design working on an input it cannot
  predict; a back-off after repeated failures would trade speed for consistency, and the
  decision was to live with it. Two players typed `kickslow` without the `!`.
- **The start.** The clock starts when the last client has loaded (as in stock); that client
  then pays its render warm-up (texture uploads, the map's vertex buffers) on live frames,
  while the players who waited did theirs during the freeze. Across 640 client-games 6% of
  clients fell 1.2 s or more behind in the first 30 s, almost always the slowest loader,
  and 8 of 129 logged games had a slowdown in the first 30 s.

### Changes

1. *The hold reads the backlog in frames.* Each control interval the scheduler notes how
   many frames it has closed beyond the last one each client reported; the fewest ever seen
   for a client is the frames in flight on its connection, and the backlog is what is above
   that. A constant in the lateness measure cannot reach it. The creep (0.3 points a second)
   runs while the backlog is at most 2 frames and not growing faster than 5% of the frames
   closed per second (or half a frame a second, since the lag is sampled to the frame);
   faster growth pulls the speed back by the deficit, and a backlog above 4 frames by 0.3
   points a second per frame, within the same ±1 point a second and 6% floor as before. The
   probe timer counts the same conditions and is reset by growth or a large backlog. A probe
   fails when the probed player's backlog reaches 3 frames having grown by 2 over the last
   three intervals (also offset-free). The lateness measure still decides *slowdowns* (the
   budget, the projected-lag trigger), where an offset only makes the server a little more
   cautious. In the model, with the offsets added, v1.2 was stuck for 263 s of a 20-minute
   `offsetdip` and 273 s of `offsethi` (speed lost 32% in both); v1.3 is stuck 25 s and loses
   12%, the same as the dip without an offset. On the scenarios without offsets the two are
   equal to the point on speed lost (dip 12%, wander 23%, spiky 24%, steady 25%, two slow
   31%) and on speed changes (36 against 38 an hour); v1.3 lets the slow player sit a little
   further behind (0.52 s against 0.39 s on average), because the lateness level was what the
   old rule drained and the backlog rule only drains above four frames. With four times the
   measurement noise the numbers do not move.
   The harness scenario `offsethold` (a client whose packets are handed to its game 600 ms
   late while its pings are answered at once) shows v1.2 holding 66% to the end and v1.3
   back at full speed 77 s after the slowdown.
2. *Start delay.* `Server.StartDelay` (default 2000 ms): the first frame closes that long
   after the last client's first packet, so the last loader renders the frozen map for a
   couple of seconds like everyone else did before the clock starts. It costs every start
   two seconds inside a freeze of usually five to ten; it removes the commonest start-of-game
   slowdown, which was the one at second seven.
3. *`kickslow` without the `!`* counts as the vote (as do `kick slow` and the aliases),
   and the line is still relayed as chat since that is how it was typed.
4. The private message to the player the game is slowed for now says what to try: turn off
   VSync and limit the frame rate in Settings > Display, and close other programs to free up
   the CPU. (On a 60 Hz screen, VSync alone costs a client that is over budget about 8 ms
   per tick, because the client renders a frame after every logic tick and the swap waits
   for the monitor.)

The lateness-level constants (CreepGain, CreepDeadband) are gone; the frame-backlog ones
(CreepBacklog, CreepPullBacklog, CreepDeficitDeadband, CreepGrowthFloor, CreepDeficitGain,
CreepLevelGain, ProbeFailureBacklog, ProbeFailureGrowth) are at the top of
`FrameScheduler.cs`. With a lag budget below the usual three seconds the backlog thresholds
shrink in proportion.

## v1.3.1: the private line

### Finding

On 1 Oct the author's own game (six players, game speed *faster*) was slowed to 67% for
him for five minutes on a PC with a GeForce 1080 Ti, and he did not notice the private
message: it arrived in the same instant as the public speed line, in the same "Battlefield
Control" colour, and he had learnt to ignore those. He noticed the input delay instead,
turned off VSync, ticked "Limit framerate to game tick rate", and the game came back.

The mechanism is the one the client benchmark predicted, sharpened by the game speed. At
*faster* a tick is 30 ms. With VSync on a 60 Hz monitor the frame hand-off after every tick
waits for the next refresh, so a tick that does not fit in one refresh period costs 33.3 ms
and one that spills past that costs 50: the ceiling is 90% and busy stretches read as
60–85%. The server measured 87, 84, 78 and 69% for him and the same ladder for the other
player it slowed for in that game. At normal speed 33.3 ms fits in the 40 ms budget and the
effect is invisible, which is why a strong PC only meets it at *faster* or *fastest*.

A server has no control over how a line looks: everything it sends is a system line in the
client's system colour, and the chat sound plays for every line already. It controls the
words and the timing.

### Changes

1. The private line opens with the player's own name in capitals, the one word that cuts
   through: `>>> ENFILADE, THIS IS ABOUT YOU: the game is slowed to 84% because your computer
   is not keeping up (it is managing about 87%).`
2. It is sent 2.5 s after the public line it belongs to, so it is a line of its own with its
   own sound instead of blending into the speed message everyone gets at the same moment.
3. When the game is no longer slowed for that player (back to full speed, or slowed for
   someone else) they get one private all-clear, `>>> ENFILADE: your computer is keeping up
   again.`, so a player who just changed a setting knows it worked.
4. The advice names the two checkboxes: `Settings > Display: untick "Enable VSync", tick
   "Limit framerate to game tick rate"; close other programs.` At game speeds *faster* and
   *fastest* (ticks under 34 ms, where two 60 Hz refresh periods no longer fit in one) it adds
   `At this game speed, VSync alone causes this on a 60 Hz monitor.`

There is deliberately no repetition while the slowdown lasts: one line when it starts, one
when it ends. (`GameSpeedAnnouncer.PrivateDelay`; the announcer's public rate limit does not
apply to the private lines, which are tied to a public line already sent.)

## Testing

`netcode-tests/` contains a harness of headless clients over simulated bad connections,
plus `OpenRA.Test/FrameSchedulerTest.cs` (run by `make tests`). Every scenario checks
lockstep across all clients and replay playback. See `netcode-tests/README.md`.

**These tests model the client's networking and timing, not real rendering or
simulation load.** Validate with real games before relying on it.

## Status

Unofficial and experimental. Not affiliated with the OpenRA project. GPLv3, like OpenRA.
