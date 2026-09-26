#!/bin/bash
# run-server.sh <variant> <port> [extra args...]  -- runs the headless dedicated server from /home/claude/servers/<variant>
V=$1; PORT=$2; shift 2
SUP=/tmp/ora-support-$V-$PORT; rm -rf "$SUP"; mkdir -p "$SUP"
cd /home/claude/servers/$V
exec ./OpenRA.Server Game.Mod=ra Engine.SupportDir=$SUP Server.ListenPort=$PORT Server.AdvertiseOnline=False \
  Server.QueryMapRepository=False Server.EnableGeoIP=False Server.Name=nettest Server.EnableSingleplayer=True Server.Map=4d07635299dc34cc56f022188e850b6086926c74 "$@"
