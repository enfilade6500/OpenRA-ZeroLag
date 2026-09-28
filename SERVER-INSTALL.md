# Installing ZeroLag on a dedicated server

ZeroLag replaces one file of an OpenRA **release-20250330** dedicated server:
`OpenRA.Game.dll`. Players need nothing and notice nothing except that the game no longer
freezes for everyone when one player lags.

## 1. Get the file

Download `OpenRA.Game.dll` and `OpenRA.Game.dll.sha256` from the
[Releases](../../releases) page, or build it yourself (see the README). Check the hash:

```sh
sha256sum -c OpenRA.Game.dll.sha256
```

## 2. Find your server's copy

| Install | Location of `OpenRA.Game.dll` |
|---|---|
| Linux, extracted AppImage | `<extracted>/usr/lib/openra/` |
| Linux, distribution package | usually `/usr/lib/openra/` or `/usr/lib64/openra/` |
| Windows | the game's install folder (next to `OpenRA.Server.exe`) |
| macOS | `OpenRA.app/Contents/Resources/` |
| Built from source | `bin/` |

If in doubt, look at the server's log: the first lines of `Logs/dedicated-server.log` (or
the console) print the paths it loaded.

## 3. Swap it in

Keep the original so you can go back:

```sh
L=/path/to/lib/openra
cp "$L/OpenRA.Game.dll" "$L/OpenRA.Game.dll.stock"
mv OpenRA.Game.dll "$L/OpenRA.Game.dll"
```

A running server keeps using the file it already loaded, so do this any time and restart
the server when nobody is playing. On Windows, stop the server first; the file is locked
while it runs.

## 4. Restart and check

After a restart, start a game and look for this line in `Logs/dedicated-server.log`:

```
Netcode: dynamic.
```

That confirms ZeroLag is active. (`Netcode: classic.` means `Server.Netcode=classic` was
set; no line at all means the stock file is still in use.)

## 5. Optional settings

Passed like any other server setting (command line, `settings.yaml`, or your service's
environment file):

```
Server.VoteKickSlowest=True         # players can type !kickslow (recommended)
Server.NameSlowestPlayer=True       # chat messages name the slow player
Server.AnnounceGameSpeed=False      # no chat messages about game speed (log only; players can also !quiet)
Server.MinGameSpeed=0               # no floor: follow the slowest PC however slow (default 30: a PC needing less is left behind)
Server.MaxPlayerLag=0               # slow everyone as soon as a PC falls behind (default: it may lag alone for 3 s first)
Server.MaxPlayerBuffer=0            # no adaptive buffering for lossy connections (default 1500 ms)
Server.MaxCatchUpSpeed=200          # ask a player far behind for at most 2x (default 400)
Server.MaxWaitForStalledPlayer=0    # never pause for a player who stops responding (default 3000 ms)
Server.Netcode=classic              # original behaviour, without swapping the file back
Server.ZeroLagNotice=True           # add a "this is a ZeroLag server, type !speed" line when players join
```

The README explains each one. Defaults are: announcements on, nobody named, no vote, a 30%
floor, a 3 s lag budget, buffering up to 1.5 s, catch-up up to 4x, a 3 s pause at most, join
notice off.

## Rolling back

```sh
mv "$L/OpenRA.Game.dll.stock" "$L/OpenRA.Game.dll"
```

and restart. Or set `Server.Netcode=classic`, which keeps the file but restores the
original scheduling.

## Reading the log

```sh
grep -E "Netcode:|Slowing|too slow|caught up|full speed|behind|dropped out|buffer|waiting|waited|paused|Summary for|vote" Logs/dedicated-server.log
```

- `Slowing the game to 78% ... so that Name can keep up (their computer is managing 80% ...)` —
  a player's PC couldn't keep up and the game was slowed. `... The game will not be sped up
  again for 30s.` means an attempt to speed back up had just failed.
- `Name's connection dropped out for 1.2s; buffering 1.4s for them from now on.` — a
  dropout was turned into buffer for that player; nobody else is affected.
- `Name is 3.4s behind because of connection dropouts (2.1s in the last 3s), not their
  computer; the game is not slowed down for them.` — what it says.
- `Players behind: ...` — every 10 s while anyone is more than 0.5 s behind.
- `Everyone is waiting for Name, who has stopped responding.` then either `Game resumed after
  waiting 1.8s` or `Everyone waited 3.0s for Name ...; the game continues without them.` —
  a dead connection; the only thing that still pauses the game, and only for up to
  `Server.MaxWaitForStalledPlayer`.
- `Summary for Name: worst 2.1s behind, average 0.3s; caused 1 slowdown(s). Their computer
  managed 80% while it was slowing the game, and at least 143% at best. Their connection
  dropped out 12 time(s), 6.1s in total, longest 1.4s; buffered up to 1.5s for them.` — one
  line per player when they leave.

Note that the stock server truncates `dedicated-server.log` every time the process starts;
copy it, or rotate it from your service manager, if you want to keep it. Server-side replays
(`Replays/`) accumulate and are the most useful thing to keep alongside the log: they record
when every packet arrived, which is how the patterns in NETCODE.md were found.
