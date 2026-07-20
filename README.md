# LTAR Arena Control Server

Production-oriented control, live display, and ESP32 IR relay software for an
LTAR/Tiger/Team Ops laser-tag arena.

The system runs on a 64-bit Raspberry Pi, hosts games through an ESP32
Wi-Fi-to-IR bridge, provides a PIN-protected operator console, and drives a
read-only 1280x720 TV scoreboard.

## Current status

The hardware-verified Version 1 source is preserved separately from the
operations-focused `2.0.0-rc.1` candidate. See
[`V2_RELEASE_CANDIDATE.md`](V2_RELEASE_CANDIDATE.md) for the exact scope,
acceptance checklist, and rollback path before installing the candidate in a
public arena.

The following paths have been exercised with real arena hardware:

- tagger joining and guided player assignment;
- countdown, normal play, early game ending, summary, and game-over states;
- solo-game debrief and correct individual score display;
- relay disconnect/reconnect handling;
- TV and mobile layouts;
- ARM64 server deployment to Raspberry Pi;
- ESP32 relay compilation and LTTO transmit/receive operation.

The current server includes a compatibility fix for a real tagger report that
used the non-canonical detailed count byte `0x8A`. Earlier strict validation
could repeatedly request the same player's report and leave debrief stuck. The
server now accepts that legacy-compatible report while still bounding effective
hits to the target's configured health. This is a server-side change and does
not require reflashing the ESP32.

A full two-team game should still be rerun after each scoring or debrief change.
Before opening to paying customers, complete the hardware and operations
acceptance checklist in [`deployment/PI_SETUP.md`](deployment/PI_SETUP.md).

## System overview

```text
Operator phone/tablet              TV display
        |                              |
        +--------- Wi-Fi --------------+
                       |
              Raspberry Pi server
              192.168.4.1:5000
                       |
                  TCP 8266
                       |
                 ESP32 relay
               192.168.4.100
                       |
                  LTTO infrared
                       |
                    Taggers
```

Normal arena addresses:

| Device or service | Address |
|---|---|
| Operator console | `http://192.168.4.1:5000/` |
| TV scoreboard | `http://192.168.4.1:5000/tv.html` |
| Health check | `http://192.168.4.1:5000/healthz` |
| ESP32 relay | `192.168.4.100:8266` |

Do not expose ports `5000` or `8266` to the public internet.

## Main capabilities

- ASP.NET Core 8 web server with SignalR live updates.
- PIN-protected operator console and read-only public TV display.
- Solo, two-team, and three-team hosted-game support.
- Guided tagger joining and confirmed player assignment.
- Live countdown, game-state, roster, score, and debrief updates.
- Operator readiness checks that block countdown until the relay, roster,
  assignments, and team coverage are ready.
- Persistent completed-game history with operator-only detail, JSON, and CSV
  access under the arena data directory.
- Player settings with validated, atomic persistence.
- Automatic relay reconnect and stale-game event protection.
- Responsive desktop, phone, and 1280x720 television interfaces.
- Mode-correct player and team ranking on the TV, with distinct relay-offline
  and scoreboard-offline warnings that automatically clear after recovery.
- Hardened Raspberry Pi deployment with health checks and automatic rollback.
- WPA2 arena hotspot, DHCP exclusions, client isolation, and nftables rules.
- Versioned read-only releases and persistent state outside the application tree.
- CI verification for .NET, JavaScript, deployment scripts, ARM64 packaging,
  and ESP32 firmware compilation.

## Repository layout

| Path | Purpose |
|---|---|
| `LazerTagHostLibrary/` | LTAR protocol, game state machine, roster, debrief, and scoring |
| `LazerTagWebServer/` | ASP.NET Core API, SignalR hub, operator UI, and TV UI |
| `LazerTagWebServer.Tests/` | Integration and regression tests |
| `esp32_relay/` | ESP32 TCP/IR relay source, library, tests, and wiring notes |
| `deployment/` | Pi installer, hotspot, firewall, systemd, deploy, backup, and rollback tools |
| `deployment-tests/` | Shell-level deployment and hotspot checks |
| `.github/workflows/ci.yml` | GitHub Actions build and verification workflow |
| `LazerTagServerApp/` | Legacy Windows Forms reference host; not used in production |

## Operator quick start

1. Power on the Raspberry Pi, ESP32 relay, TV, and taggers.
2. Connect the operator phone or tablet to the private arena Wi-Fi.
3. Open `http://192.168.4.1:5000/`.
4. Unlock the control page with the operator PIN.
5. Confirm that the page reports the relay as connected.
6. Choose the game settings and planned roster.
7. Add and confirm every tagger.
8. Start the countdown and game.
9. At game end, leave the system in Summary until every player is debriefed.
10. Use `tv.html` as the customer-facing results display.

Do not clear an incomplete debrief unless the missing tagger cannot be recovered
and the operator deliberately accepts incomplete results.

## Local development and verification

Requirements:

- .NET 8 SDK
- Node.js
- PowerShell
- OpenSSH client and `tar` for Pi deployment

From the repository root:

```powershell
cd C:\Server

dotnet restore .\LazerTagWebServer.Tests\LazerTagWebServer.Tests.csproj

dotnet test .\LazerTagWebServer.Tests\LazerTagWebServer.Tests.csproj `
  -c Release

node --check .\LazerTagWebServer\wwwroot\js\app.js

dotnet publish .\LazerTagWebServer\LazerTagWebServer.csproj `
  -c Release `
  -r linux-arm64 `
  --self-contained true `
  -o .\artifacts\publish
```

For local UI work without contacting the arena relay:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Arena__OperatorPin = 'USE_A_TEMPORARY_DEVELOPMENT_PIN'
$env:Arena__DisableRelayReconnect = 'true'

dotnet run --project .\LazerTagWebServer\LazerTagWebServer.csproj
```

Open `http://localhost:5000`.

## ESP32 relay

The normal relay configuration is:

| Setting | Value |
|---|---|
| Wi-Fi mode | Station mode on the secured arena WPA2 network |
| Static address | `192.168.4.100/24` |
| Gateway and DNS | `192.168.4.1` |
| TCP port | `8266` |
| IR receiver | GPIO 15, RMT channel 0 |
| IR transmitter driver | GPIO 4, RMT channel 1 |

Copy:

```text
esp32_relay/sketches/RelayESP32/RelaySecrets.example.h
```

to:

```text
esp32_relay/sketches/RelayESP32/RelaySecrets.h
```

Then enter the same WPA2 credentials used by the arena access point.
`RelaySecrets.h` is intentionally ignored by Git and must never be committed.

Compile with the tested Espressif board core:

```powershell
arduino-cli compile --warnings all `
  --fqbn esp32:esp32:esp32 `
  --libraries C:\Server\esp32_relay\libraries `
  C:\Server\esp32_relay\sketches\RelayESP32
```

See [`esp32_relay/README.md`](esp32_relay/README.md) for the TCP protocol,
wiring, IR timing, and upload instructions.

## Production deployment

The supported appliance target is Raspberry Pi OS Lite 64-bit
Trixie/Debian 13 on `aarch64`.

The one-time installer creates:

- a locked `lazertag` service account;
- root-owned production configuration under `/etc/lazertag`;
- immutable releases under `/opt/lazertag/releases`;
- the active `/opt/lazertag/current` symlink;
- persistent state under `/var/lib/lazertag`;
- backups under `/var/backups/lazertag`;
- a hardened systemd service;
- the private hotspot, DHCP exclusions, client isolation, and firewall;
- health-checked atomic deployment and rollback.

Follow the complete first-install procedure in
[`deployment/PI_SETUP.md`](deployment/PI_SETUP.md). Use Ethernet or the local
console while configuring the hotspot because changing `wlan0` can disconnect
an SSH session carried over Wi-Fi.

### Routine server update

After the one-time appliance installation:

1. Open the `LazerTagWebServer` folder in Visual Studio Code.
2. Select **Terminal > Run Task**.
3. Run **Deploy Web Server to Pi**.
4. Enter the Pi SSH target, such as `arenaadmin@192.168.1.50`.

The task publishes the current `linux-arm64` server, creates a release archive,
uploads it, switches releases atomically, restarts the service, checks
`/healthz`, and restores the previous release automatically if readiness fails.

Do not use the obsolete direct-copy deployment path `/home/pi/lazertag`.

## Runtime configuration and data

Production values are supplied outside the repository. Important settings are:

```text
Arena__RelayAddress=192.168.4.100:8266
Arena__DataDirectory=/var/lib/lazertag
Arena__OperatorPin=<private 8-12 digit PIN>
```

The operator PIN and Wi-Fi password must not appear in source control, shell
history, screenshots, or public issue reports.

Existing `players.json` files are not automatically migrated from an old
installation. Validate a required roster file and install it as:

```text
/var/lib/lazertag/players.json
```

owned by `lazertag:lazertag`.

## Security and repository hygiene

The repository ignores:

- `RelaySecrets.h`;
- production app settings;
- `.env` files;
- `bin`, `obj`, `publish`, and `artifacts` output;
- ZIP and `tar.gz` release packages;
- Visual Studio user files.

Before every public push, check:

```powershell
git status
git diff --cached
git ls-files | Select-String 'RelaySecrets.h|appsettings.Production.json|\.env'
```

The final command should not reveal a live credential file. If a password or
PIN is ever committed publicly, removing the file is not enough: rotate the
credential immediately.

## CI

Every push and pull request runs GitHub Actions that:

1. restores and tests the .NET server;
2. checks browser JavaScript syntax;
3. validates deployment shell scripts and hotspot policy;
4. publishes a self-contained `linux-arm64` server;
5. packages server and deployment archives with SHA-256 checksums;
6. compiles the ESP32 relay using placeholder CI credentials;
7. uploads the generated release artifacts for temporary retention.

CI placeholder credentials are compile-only and are never published as usable
arena firmware.

## Troubleshooting

### A player remains in Summary and is repeatedly requested

Check the operator log. The current server source includes compatibility handling for the
observed non-canonical detailed count `0x8A`. Update and redeploy the server
before changing ESP32 firmware. Confirm the log progresses to:

```text
All players debriefed
State: Summary -> GameOver
```

### Relay will not connect

- Confirm the ESP32 is powered and joined to the secured arena SSID.
- Confirm its address is `192.168.4.100` and that DHCP never leases `.100`.
- Confirm the server configuration points to `192.168.4.100:8266`.
- Power-cycle the ESP32 after changing Wi-Fi credentials.
- Check the Pi service and logs:

```bash
sudo systemctl status lazertag --no-pager
sudo journalctl -fu lazertag
sudo /usr/local/libexec/lazertag/health-check
sudo /usr/local/libexec/lazertag/hotspot-check
```

### UI reconnects during a game

A brief SignalR reconnect does not necessarily mean the game engine stopped.
Confirm `/healthz`, relay status, and the systemd journal before restarting the
server during an active match.

## Public-opening acceptance

Software tests cannot certify a physical venue. Before admitting paying
customers, complete the checklist in `deployment/PI_SETUP.md`, including:

- every real tagger and IR receiver/emitter;
- maximum-player join, gameplay, scoring, and debrief tests;
- solo, two-team, and three-team score verification;
- two-hour phone/tablet and SignalR soak testing;
- relay dropout and recovery during every game state;
- repeated Pi and ESP32 power-cycle tests;
- IR range, dead-zone, interference, and cross-arena testing;
- thermal, electrical, cabling, mounting, and emergency-stop inspection;
- firewall and client-isolation checks from an actual guest device;
- backup restoration and deployment rollback drills;
- staff procedures and venue-specific safety requirements.

Keep a spare known-good microSD card during commissioning so the previous
working installation remains an immediate fallback.
