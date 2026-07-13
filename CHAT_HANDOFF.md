# Laser Tag Arena Project — Chat Handoff

Copy this entire file into a new chat when asking another assistant to continue
working on the project.

## Goal

Make the LTAR laser-tag web server, Raspberry Pi appliance, and ESP32 IR relay
safe and reliable enough for a professional arena. Preserve the familiar game
workflow while allowing routine web-server deployments from Visual Studio Code.

## Project location

- Working project: `C:\Server`
- Original source archive: `C:\Server\New Compressed (zipped) Folder.zip`
- Do not modify or delete the original ZIP.
- The project is not currently connected to a Git remote.

## Current deployment workflow

The Raspberry Pi requires the one-time platform installation documented in:

`C:\Server\deployment\PI_SETUP.md`

After that installation, routine updates are performed from Visual Studio Code:

1. Open `C:\Server\LazerTagWebServer` in VS Code.
2. Select **Terminal > Run Task**.
3. Select **Deploy Web Server to Pi**.
4. Enter the wired/admin SSH target, for example
   `arenaadmin@lazertag-pi.local` or the venue's actual `username@address`.

The task is defined in:

`C:\Server\LazerTagWebServer\.vscode\tasks.json`

It runs:

`C:\Server\deployment\deploy-from-vscode.ps1`

That script publishes for `linux-arm64`, creates a flat archive, uploads it with
SCP, and calls `/usr/local/sbin/lazertag-deploy` on the Pi. The Pi deployer uses
versioned releases, a readiness check, and automatic rollback. Do not replace
this workflow with a direct copy into the old `/home/pi/lazertag` directory.

## Production network contract

- Secured arena SSID: `LazerTag`
- Pi hotspot and web server: `192.168.4.1/24`
- Operator UI: `http://192.168.4.1:5000/`
- TV UI: `http://192.168.4.1:5000/tv.html`
- Readiness endpoint: `http://192.168.4.1:5000/healthz`
- Phone/tablet DHCP leases: `192.168.4.20` through `192.168.4.99`
- ESP32 relay: static `192.168.4.100/24`, TCP port `8266`
- ESP32 gateway: `192.168.4.1`
- Management and routine VS Code deployments should use Ethernet/admin access.
- Port `5000` and relay port `8266` must not be exposed to the internet.

## Web-server fixes

- Added required production operator-PIN authentication.
- Added random, expiring in-memory bearer sessions and logout/revocation.
- Added constant-time PIN comparison, weak-PIN rejection, and login rate limits.
- Protected every game-changing and saved-player API operation.
- Kept game status, telemetry, health, and the TV scoreboard read-only/public.
- Restricted detailed SignalR operational logs to authenticated operators.
- Added a login overlay, session restoration, operator status, logout, and clear
  reauthentication/rate-limit behavior to the browser UI.
- Added problem-details responses and request rate limiting.
- Added request-body, request-header, keep-alive, and shutdown limits.
- Added CSP, frame, content-type, referrer, permissions, and API no-store headers.
- Added `/healthz` readiness and `/healthz/live` liveness endpoints.
- Added startup validation so Production refuses to start without a valid PIN.
- Externalized relay address, data directory, operator session duration, and
  listen URLs through configuration/environment variables.
- Added deterministic release builds and warnings-as-errors.

Important production configuration:

```text
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:5000
Arena__OperatorPin=<unique 8-12 digit PIN>
Arena__OperatorSessionHours=12
Arena__RelayAddress=192.168.4.100:8266
Arena__DataDirectory=/var/lib/lazertag
```

Never place the real PIN in source control or a chat.

## Game and roster fixes

- Minimum roster is 2 players.
- Two-team games support up to 16 players.
- Three-team and solo games support up to 24 players.
- The UI dynamically enforces the correct limit and team choices.
- Countdown requires the exact planned roster and confirmation from every
  expected tagger.
- Unexpected extra taggers are detected instead of silently starting a game.
- Game lifecycle changes are serialized to prevent conflicting operator actions.
- Ending a lobby/countdown cleans up stale host state.
- New-game and cleanup actions are limited to safe lifecycle states.
- Incomplete debrief results cannot be cleared accidentally; forcing an end is
  an explicit operator action.
- Saved-player rows, names, teams, and numeric values are bounded and validated.
- Player settings are written atomically to `/var/lib/lazertag/players.json`.

## Host protocol and scoring fixes

In `C:\Server\LazerTagHostLibrary\HostGun.cs`:

- Team score reports validate the team number.
- Bitmask payload length is checked exactly.
- Team-summary and detailed per-player tag counts use the commercial taggers'
  BCD encoding. Detailed reports retain strict nibble and payload validation and
  are applied transactionally.
- Shared debrief packets now use their mode-specific player address: standard
  hosting uses 4:4 packing and LTAR enhanced hosting uses 2:3 packing. Requests
  and both response types are decoded consistently, preventing T2P1 from being
  mistaken for T1P1 in a standard two-team game.
- Debrief no longer sends assignment-release packets before report requests.
  The response window is six seconds, the first request starts with roster
  player 1, and solo games request all three internal eight-player groups.
- Reports are decoded transactionally before changing live scores.
- Every set bit is consumed even if a roster entry is missing, preventing score
  bytes from becoming misaligned.
- A report is marked received only after the entire decode succeeds.
- The existing hardware-tested `CountEffectiveHitsScored` behavior was preserved.

## ESP32 relay fixes

Project location:

`C:\Server\esp32_relay`

- Fixed an accidental recursive `clearTransmitBuffer` implementation.
- Bounded IR receive data and corrected the returned element count.
- Serialized/blocking RMT transmission and propagated transmit failures.
- Fixed field-discovered receive starvation during multi-line announcement
  bursts: the TCP loop now handles one line per pass so RMT receive is serviced
  between transmitted signatures.
- Distinguished 9-bit packet (`P`) and checksum (`C`) commands by the checksum's
  `0x100` marker, preserving the checksum encoder's 80 ms packet-ending gap.
- Fixed the legacy RMT 15-bit duration overflow in that 80 ms gap by encoding
  it as four 20 ms low phases across two items. Compile-time assertions and the
  source-invariant test protect the 25/56/80 ms gaps at 2/2/4 phases.
- Fixed the field-discovered receive decoder regression that rejected every
  signature's extended final space as though it were a normal 2 ms inter-bit
  pause. The decoder now validates the header and intermediate pauses, excludes
  only the final 8 ms idle tail, and reports other bounded failures as `rxUnk`.
- Added stricter TCP command parsing and bounded line output.
- Corrected all eight team-score bitmasks.
- Added buffer and initialization guards.
- Configured static `192.168.4.100`, gateway `192.168.4.1`, port `8266`.
- Removed hardcoded/default arena credentials.
- Added required, git-ignored `RelaySecrets.h` with compile-time SSID and WPA2
  length checks.
- Added `RelaySecrets.example.h` and detailed wiring/security documentation.

Before compiling firmware, copy:

`C:\Server\esp32_relay\sketches\RelayESP32\RelaySecrets.example.h`

to `RelaySecrets.h` in the same directory and set the same WPA2 password used
by the Pi hotspot. Never commit or paste the real file into a chat.

The tested board target is `ESP32 Dev Module` with Arduino-ESP32 core `3.3.7`.
The bundled IR library uses the legacy RMT API and currently produces only
deprecation warnings under that core.

Field diagnostic contract: the receiver can hear the relay's own emitter, so
the serial `rx` counter may rise during hosting even when no tagger has joined.
Do not use `rx > 0` by itself as proof of a successful join. The message
`E rmt(legacy): RMT RX BUFFER FULL` is different: it is a dropped-capture error
and must not occur. After any firmware change, soak several complete standard
and enhanced announcement bursts and confirm `cmd`/`tx` advance, `txFail`
remains zero, the UI receives real join/ack packets, and no RMT buffer-full line
is emitted. `rxUnk` should also remain low; growth there means the receiver is
seeing IR but rejecting its timing or type.

## Raspberry Pi deployment fixes

Project location:

`C:\Server\deployment`

- Added an idempotent first-time installer for Raspberry Pi OS 64-bit
  Trixie/Debian 13 on `aarch64`.
- Added a locked `lazertag` service user.
- Added root-owned configuration and `/var/lib/lazertag` persistent data.
- Added versioned, read-only releases under `/opt/lazertag/releases`.
- Added an atomic `/opt/lazertag/current` release switch.
- Added automatic pre-deployment backups, readiness checks, rollback, and old
  release retention.
- Added a hardened systemd service with limited privileges and filesystem access.
- Replaced NetworkManager/wpa_supplicant AP mode after field testing proved the
  Pi 3/Trixie backend could not serve Motorola phones and the ESP32 with one
  secure profile. The temporary mixed WPA/WPA2 test connected the phones but
  rejected the ESP32 and must never be used for public games.
- Added standalone hostapd with WPA2-only `WPA-PSK`/RSN/CCMP, PMF disabled for
  the affected Pi 3 firmware, client isolation, a private dnsmasq instance, and
  a boot-time hotspot health check. NetworkManager continues to manage Ethernet
  but no longer controls `wlan0`.
- Corrected the dedicated dnsmasq state boundary after the first Pi field run:
  dnsmasq opens its lease database as root before dropping privileges, so
  systemd now creates a root-owned private state directory without granting the
  broad `CAP_DAC_OVERRIDE` capability. The installer safely repairs ownership
  and preserves an existing lease database on upgrades.
- Corrected field-discovered false rollbacks in `hotspot-check.sh`. The affected
  Pi had active hostapd/dnsmasq services and an ESP32 that completed its WPA2
  handshake, but the optional hostapd control query still caused activation to
  fail. Readiness now checks the active services, exact arena address, and
  kernel-reported `AP` mode. The control query remains diagnostic only, and
  regression tests cover both an unavailable control socket and non-AP mode.
- Added transactional first migration: configuration is staged before the AP is
  stopped, and activation failure restores a secure legacy profile. Prefer a
  dedicated PMF-capable AP for larger or hostile-RF public venues.
- Added DHCP exclusions so `.100` cannot collide with the ESP32.
- Enabled Wi-Fi AP client isolation.
- Added nftables rules that allow only required arena traffic and block guest
  access to SSH, unrelated Pi ports, the ESP32 TCP relay, other Wi-Fi clients,
  and attached business/admin networks.
- Added optional local HDMI Chromium kiosk instructions for the TV scoreboard.
- Added backup, restore, credential-rotation, game-day, and public-opening
  acceptance procedures.

## CI and repository hygiene

- Added `C:\Server\.github\workflows\ci.yml`.
- CI builds/tests the .NET server, checks JavaScript and deployment syntax,
  verifies the WPA2 and dnsmasq privilege boundaries, creates a `linux-arm64`
  package, and compiles the ESP32 firmware using placeholder CI credentials
  without publishing that placeholder firmware.
- Added `.gitignore` entries for secrets, builds, and generated artifacts.
- No repository was initialized, committed, pushed, or connected to GitHub.

## Verification already completed

- Full Release solution build: 0 warnings and 0 errors.
- Integration tests: 5 passed, including published operator/TV asset routes.
- JavaScript syntax check passed.
- Desktop operator UI, 1280x720 TV UI, and 390x844 mobile UI were exercised in
  a browser with no observed console errors.
- Operator lock/unlock, protected mutations, session logout, team-mode limits,
  and TV updates were exercised.
- Production startup without a PIN correctly failed.
- Production health check returned HTTP 200 with a valid PIN configuration.
- Anonymous game mutation returned HTTP 401.
- ARM64 apphost was verified as ELF 64-bit ARM aarch64.
- Deployment shell scripts passed `bash -n` syntax validation.
- ESP32 firmware compiled for Arduino-ESP32 3.3.7; approximate usage was 73% of
  flash and 15% of RAM.
- A later tagger-join field run exposed RMT receive-ring starvation and the
  packet/checksum timing distinction described above. The source correction
  still requires a fresh compile, upload, and real-tagger soak before it can be
  counted as venue acceptance evidence.
- A final scan found no stale default passwords, open-hotspot calls, obsolete
  `/home/pi/lazertag` deployment commands, or the known score-divisor pattern.
- VS Code task JSON and deployment PowerShell syntax/target validation passed.

## Current release artifacts

- Server archive:
  `C:\Server\artifacts\lazertag-linux-arm64.tar.gz`
- Deployment archive:
  `C:\Server\artifacts\lazertag-deployment.tar.gz`
- Recorded checksums:
  `C:\Server\artifacts\SHA256SUMS`

Recorded SHA-256 values at the time of packaging:

```text
9986c272d52ae6cd95594ea3b55c149d2e41c001d13702dbb922b9504f6b19a3  lazertag-linux-arm64.tar.gz
86878dda7d9bee57b84df32f2202d7c7ac2d881f4627d715444ddc36b506c563  lazertag-deployment.tar.gz
```

If any packaged source or deployment file changes, rebuild the relevant archive
and regenerate `SHA256SUMS`; do not continue presenting these old hashes as the
new package hashes.

## Known migration differences from the old installation

- The normal game experience remains familiar, but operators must unlock the
  control UI with a PIN.
- The old direct-copy directory `/home/pi/lazertag` is no longer the supported
  runtime location.
- The supported web-server address on the arena hotspot is now
  `http://192.168.4.1:5000`.
- The ESP32 must use the same secured SSID/password and static `.100` address.
- Existing `players.json` is not automatically migrated from an old directory.
  If required, validate it and install it as
  `/var/lib/lazertag/players.json`, owned by `lazertag:lazertag`.
- Use a spare/new microSD for initial commissioning when possible so the old
  working card remains an immediate fallback.

## Work that still requires real arena hardware

Software testing cannot certify the physical venue. Before admitting paying
customers, complete the acceptance checklist at the end of
`C:\Server\deployment\PI_SETUP.md`, including:

- every real tagger and IR receiver/emitter;
- maximum-player join, gameplay, score, and debrief tests;
- two-hour phone/tablet and SignalR soak testing;
- relay dropout and recovery during every game state;
- repeated Pi and ESP32 power-cycle testing;
- IR range, dead-zone, interference, and cross-arena testing;
- thermal, electrical, cabling, mounting, and emergency-stop inspection;
- firewall/client-isolation checks from an actual guest device;
- backup restoration and deployment rollback drills;
- venue-specific staff procedures and applicable safety requirements.

Do not describe the arena as fully certified or ready for public opening until
those hardware and operational checks pass.

## Suggested prompt for the next chat

> Continue work on the laser-tag arena project in `C:\Server`. Read
> `C:\Server\CHAT_HANDOFF.md`, `C:\Server\README.md`, and
> `C:\Server\deployment\PI_SETUP.md` before making changes. Preserve the
> hardened authentication, network isolation, atomic deployment/rollback, and
> existing hardware-tested scoring behavior. Do not expose or invent secrets.
> First tell me what you plan to inspect or change, then verify any changes with
> the existing build and tests.
