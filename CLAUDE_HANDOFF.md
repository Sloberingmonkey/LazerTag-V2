# LTAR laser tag server handoff

**My preference is V1.** It works with my physical taggers and I prefer using it. Use the existing V1 server as the baseline for the next fix I ask for. Keep fixes local and test them before discussing a Pi update. I’ll decide when to deploy or switch releases. Leave the alternate Wi-Fi adapter alone. Keep the current hotspot and ESP32 firmware as configured.

## Which version is which

| Name I use | What it means |
| --- | --- |
| **V1** | The original working server in `C:\Server`. Its repository is [Sloberingmonkey/LazerTag-V2](https://github.com/Sloberingmonkey/LazerTag-V2). The repo name and the version shown in the web app can both say “V2” even when this is the server I call V1. |
| **Known-good V1 source** | Git tag `v1.0.2-known-good` at `2eba0ce`, also preserved as branch `codex/v1-known-good`. Use it as a reference for hardware-tested behavior. |
| **V1 work in progress** | `C:\Server` on `main` at `d8b0b91`, with substantial modified and untracked files. The local web project declares `2.0.0-rc.3`. Preserve the working tree. A fresh clone does not contain all of these changes. |
| **V2 rewrite** | Independent project in `C:\LazerTagServerV2`. Its local `2.0.0-rc.2` build and Windows manager are available for bench testing. The rewrite is optional. |

## Raspberry Pi

At the last check, the original server was active, reported `2.0.0-rc.2` at commit `d8b0b91`, and showed a connected relay. Read `/healthz` and check the active service before assuming the Pi still matches that state.

- Management host is `lazertag-pi.local`, last seen at `192.168.0.170`. The SSH user is `arenaadmin`.
- On the management LAN, use the [operator console](http://192.168.0.170:5000/) and [TV scoreboard](http://192.168.0.170:5000/tv.html).
- On the **LazerTag** arena Wi-Fi, use the [operator console](http://192.168.4.1:5000/) and [TV scoreboard](http://192.168.4.1:5000/tv.html). The TV is public on the arena network. The operator console takes a PIN.
- The ESP32 relay uses `192.168.4.100:8266` over TCP. Run only one arena service at a time. Both need port 5000 and the same relay.
- V1 uses `lazertag.service`, the release link `/opt/lazertag/current`, and data under `/var/lib/lazertag`. Its operator PIN is in `/etc/lazertag/lazertag.env`. V2 uses `lazertag-v2.service` and a separate PIN in `/etc/lazertag-v2/operator.pin`.

## Where to work on V1

| Area | Start here in `C:\Server` |
| --- | --- |
| Tagger registration, game timing, scoring, and debrief | `LazerTagHostLibrary/HostGun.cs`, `LazerTagSerial.cs`, and `LazerTagProtocol.cs` in the same folder |
| Operator console, TV, API, and history | `LazerTagWebServer/wwwroot/`, `LazerTagWebServer/Services/HostGunService.cs`, and `GameHistoryStore.cs` in the same folder |
| Regression coverage | `LazerTagWebServer.Tests/ArenaRelayTests.cs`, `DebriefRegressionTests.cs`, and `ReliabilityRegressionTests.cs` |
| Local build and CI | `LazerTagServer.sln`, `global.json`, and `.github/workflows/ci.yml` |
| LAN updates and rollback | `deployment/PI_SETUP.md`, `deployment/deploy.sh`, `deployment/rollback.sh`, and `artifacts/LazerTagUpdater.exe` |

Start with `git status --short` and inspect the local changes around the requested fix. The working tree already has regression tests for lost and delayed assignment acknowledgments, relay reconnects, and final ranks after debrief. Use the existing tests when they cover the change. Run the Release tests from `C:\Server` and use the CI workflow for the JavaScript and deployment checks.

```powershell
dotnet test .\LazerTagServer.sln -c Release
node --test .\LazerTagWebServer.Tests\operator-console.test.cjs
```

## V2 bench status

On the Pi, V2 RC1 announced the game and the taggers found it, but they never received player numbers. The local RC2 build restores whole-packet relay writes and gives the tagger up to two seconds to acknowledge its assignment before the next game announcement. It also logs the join request, relay transmission, and tagger acknowledgment separately. Local server, browser, ARM64 emulation, and installer checks passed. RC2 was packaged locally and its physical registration is still unverified.

The optional manager at `C:\LazerTagServerV2\artifacts\LazerTagV2Manager.exe` has **Install V2**, **Switch to V2**, and **Return to V1**. If I decide to revisit the rewrite, start with a short two-tagger match. Confirm both taggers show different player numbers, then verify debrief ranks against the TV. For now, work from V1.
