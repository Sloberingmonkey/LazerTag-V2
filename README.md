# LTAR Arena Control Server

Production-oriented control, live display, and ESP32 IR relay software for an
LTAR/Tiger/Team Ops laser-tag arena. The controller runs on a 64-bit Raspberry
Pi, hosts games through an ESP32 Wi-Fi-to-IR bridge, provides a PIN-protected
operator console, and drives a read-only 1280x720 TV scoreboard.

## Readiness status

The software build, automated API/security tests, browser layouts, ARM64 publish,
and ESP32 compilation are repeatable from this repository. A venue must still
complete the hardware-in-the-loop and public-game acceptance checklist in
[`deployment/PI_SETUP.md`](deployment/PI_SETUP.md) before opening to customers.
That checklist covers IR range, electrical/thermal safety, 24-player soak,
dropout recovery, power-loss recovery, backups, and rollback drills.

## Components

- `LazerTagHostLibrary` - LTAR protocol, state machine, roster, debrief, and scoring.
- `LazerTagWebServer` - ASP.NET Core 8 API, SignalR updates, operator UI, and TV UI.
- `LazerTagWebServer.Tests` - end-to-end API, roster-capacity, and operator-access tests.
- `esp32_relay` - ESP32 RMT-based TCP/IR bridge and wiring/build guide.
- `deployment` - secured hotspot, firewall, hardened systemd unit, installer,
  health checks, atomic deployment, backup, and rollback tooling.
- `LazerTagServerApp` - legacy Windows Forms reference host; not used in production.

## Production safety controls

- Mutating game APIs and saved rosters require a rate-limited operator PIN session.
- The public SignalR/TV surface is read-only; detailed logs go only to authenticated operators.
- Two-team games are limited to 16 players, three-team and solo games to 24.
- Countdown requires the exact planned roster and every tagger to confirm assignment.
- Debrief data cannot be cleared early; forcing incomplete reports requires an explicit action.
- Relay address, data directory, session lifetime, and listen URL are external configuration.
- Player settings use bounded validation and atomic writes outside the release directory.
- The ESP32 serializes RMT transmissions, bounds receive buffers, validates commands,
  and reports transmit failures instead of acknowledging them as successful.
- The Pi deployment uses WPA2, Wi-Fi client isolation, an nftables allowlist,
  a dedicated service account, read-only versioned releases, and automatic rollback.

## Developer verification

Requires the .NET 8 SDK and Node.js:

```powershell
cd C:\Server
dotnet test .\LazerTagWebServer.Tests\LazerTagWebServer.Tests.csproj -c Release
node --check .\LazerTagWebServer\wwwroot\js\app.js
dotnet publish .\LazerTagWebServer\LazerTagWebServer.csproj `
  -c Release -r linux-arm64 --self-contained true `
  -o .\artifacts\publish
```

Build the firmware with the tested Espressif board core after creating the
git-ignored `RelaySecrets.h` described in [`esp32_relay/README.md`](esp32_relay/README.md):

```powershell
arduino-cli compile --warnings all --fqbn esp32:esp32:esp32 `
  --libraries C:\Server\esp32_relay\libraries `
  C:\Server\esp32_relay\sketches\RelayESP32
```

## Production deployment

The supported target is Raspberry Pi OS 64-bit Trixie/Debian 13 or newer on
`aarch64`. Publish a flat `linux-arm64` self-contained archive, transfer it and
the `deployment` directory to the Pi over the wired/admin network, then follow
[`deployment/PI_SETUP.md`](deployment/PI_SETUP.md).

For routine web-server updates after that one-time installation, open
`LazerTagWebServer` in Visual Studio Code and run **Terminal > Run Task > Deploy
Web Server to Pi**. The task prompts for `username@Pi-address`, publishes and
uploads the current server, then uses the installed health-checked deployer with
automatic rollback.

Runtime configuration is stored in `/etc/lazertag/lazertag.env`; the operator
PIN is required in Production and is never committed. Releases live under
`/opt/lazertag/releases`, current state under `/var/lib/lazertag`, and backups
under `/var/backups/lazertag`.

Normal arena URLs:

- Operator console: `http://192.168.4.1:5000/`
- TV display: `http://192.168.4.1:5000/tv.html`
- Health: `http://192.168.4.1:5000/healthz`
- ESP32 relay: `192.168.4.100:8266`

Do not expose port 5000 or 8266 to the internet. Keep the operations SSID and
operator PIN private from guests; use the TV for customer-facing results.
