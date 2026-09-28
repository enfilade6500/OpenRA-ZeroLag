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

## Worked example: several instances under systemd (Linux)

This is the layout the ZeroLag test servers use: one extracted AppImage per machine, one
systemd instance per port, and one state directory per instance. Most Linux hosts have
something like it.

```
/opt/openra/release-20250330/          the extracted AppImage
/opt/openra/current -> release-20250330 what the service runs
    usr/bin/openra-ra-server           the launcher
    usr/lib/openra/OpenRA.Game.dll     <- ZeroLag goes here (one copy, shared by all instances)
    usr/lib/openra/OpenRA.Game.dll.stock  the original

/etc/systemd/system/openra-ra@.service            one template unit, instantiated per port
/etc/systemd/system/openra-ra@.service.d/override.conf   log rotation (below)
/etc/openra/1234.env, 1235.env, ...               name and settings per instance

/var/lib/openra/1234/                  state directory of the instance on port 1234
    Logs/dedicated-server.log          truncated at every start by the stock server
    Logs/archive/<timestamp>/          the previous runs' logs (from the rotation below)
    Replays/ra/release-20250330/       one server-side replay per game
    maps/, motd.txt
```

The unit:

```ini
# /etc/systemd/system/openra-ra@.service
[Unit]
Description=OpenRA Red Alert dedicated server (port %i)
After=network-online.target
Wants=network-online.target

[Service]
User=openra
Group=openra
EnvironmentFile=/etc/openra/%i.env
StateDirectory=openra/%i
ExecStart=/opt/openra/current/usr/bin/openra-ra-server Engine.SupportDir=/var/lib/openra/%i Server.ListenPort=%i "Server.Name=${SERVER_NAME}" $EXTRA_ARGS
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

The settings go in the instance's environment file:

```sh
# /etc/openra/1234.env
SERVER_NAME=My server (ZeroLag)
EXTRA_ARGS=Server.VoteKickSlowest=True Server.AdvertiseOnline=True
```

The stock server empties `dedicated-server.log` every time it starts, and with
`Restart=always` it starts after every game. This override moves the previous run's logs
aside first (note `%%` for `%` inside a unit file; `|| true` so a hiccup here never stops
the server from starting):

```ini
# /etc/systemd/system/openra-ra@.service.d/override.conf
[Service]
ExecStartPre=
ExecStartPre=/bin/sh -c 'L=/var/lib/openra/%i/Logs; A=$L/archive/$(date +%%F-%%H%%M%%S); ls $L/dedicated-*.log >/dev/null 2>&1 && mkdir -p "$A" && mv $L/dedicated-*.log "$A"/ || true'
```

Installing or updating ZeroLag on this layout:

```sh
mkdir zerolag && cd zerolag        # a fresh directory, so an old .sha256 can't be picked up
wget https://github.com/enfilade6500/OpenRA-ZeroLag/releases/latest/download/OpenRA.Game.dll
wget https://github.com/enfilade6500/OpenRA-ZeroLag/releases/latest/download/OpenRA.Game.dll.sha256
sha256sum -c OpenRA.Game.dll.sha256

L=/opt/openra/current/usr/lib/openra
sudo cp "$L/OpenRA.Game.dll" "$L/OpenRA.Game.dll.stock"     # first time only
sudo cp OpenRA.Game.dll "$L/OpenRA.Game.dll"

# when nobody is playing; each instance picks the file up as it restarts
sudo systemctl restart openra-ra@1234 openra-ra@1235 openra-ra@1236
```

The state directories belong to the service user, so reading them takes `sudo`. To collect
every instance's logs and replays for a look (the replays are the more useful half):

```sh
sudo tar czf ~/openra-$(hostname)-$(date +%F).tgz -C /var/lib/openra --exclude='*/maps' .
```

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
