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
Server.MaxPlayerLag=0           # slow everyone as soon as a PC falls behind (default: it may lag alone for 3 s first)
Server.VoteKickSlowest=True     # players can type !kickslow
Server.NameSlowestPlayer=True   # chat messages name the slow player
Server.AnnounceGameSpeed=False  # no chat messages about game speed (log only)
Server.MinGameSpeed=75          # never slow below 75%; a slower PC is left behind instead
Server.Netcode=classic          # original behaviour, without swapping the file back
Server.ZeroLagNotice=False      # no "this is a ZeroLag server" line when players join
```

The README explains each one. Defaults are: announcements on, nobody named, no vote, no
floor, a 3 s lag budget, join notice on.

Players joining the lobby get your server's message of the day (`motd.txt` in the
server's support directory, re-read on every join) followed by a one-line ZeroLag notice
naming the chat commands. If you would rather explain it in your own words, put that in
`motd.txt` and set `Server.ZeroLagNotice=False`.

## Rolling back

```sh
mv "$L/OpenRA.Game.dll.stock" "$L/OpenRA.Game.dll"
```

and restart. Or set `Server.Netcode=classic`, which keeps the file but restores the
original scheduling.

## Reading the log

```sh
grep -E "Netcode:|Slowing|too slow|caught up|full speed|behind|waiting|paused|Summary for|vote" Logs/dedicated-server.log
```

- `Slowing the game to 78% ... so that Name can keep up (their computer is managing 80% ...)` —
  a player's PC couldn't keep up and the game was slowed.
- `Players behind: ...` — every 10 s while anyone is more than 0.5 s behind.
- `Everyone is waiting for Name, who has stopped responding.` — a dead or frozen connection;
  the only thing that still pauses the game.
- `Summary for Name: worst 2.1s behind, average 0.3s; caused 1 slowdown(s). Their computer
  managed 80% while it was slowing the game, and at least 143% at best.` — one line per
  player when they leave.

Note that the stock server truncates `dedicated-server.log` every time the process starts;
copy it, or rotate it from your service manager, if you want to keep it.
