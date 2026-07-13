# LTAR Arena System - Production Summary

Updated: July 13, 2026  
Application version: 1.0.1

## System purpose

A Raspberry Pi hosts and scores LTAR laser-tag games on a self-contained arena
operations network. An ESP32 bridges the host's TCP protocol to 38 kHz infrared.
Staff use a PIN-protected phone/tablet console; a local HDMI TV presents the
public lobby, countdown, game clock, debrief progress, and final scores.

## Supported topology

| Component | Address | Access |
|---|---:|---|
| Raspberry Pi / web server | `192.168.4.1/24`, TCP `5000` | operator console and TV |
| ESP32 relay | `192.168.4.100/24`, TCP `8266` | Pi controller only |
| Arena clients | DHCP `.20-.99` | web server/DNS/DHCP only |

The deployment uses standalone hostapd with WPA2-PSK/RSN/CCMP and a private
dnsmasq instance with an explicit address and DHCP range. NetworkManager remains
available for Ethernet but does not control `wlan0`, avoiding the Pi 3/Trixie AP
negotiation failure observed with Motorola phones and the ESP32. Client
isolation prevents station-to-station access; nftables blocks upstream
forwarding, SSH, and unrelated Pi services from the arena WLAN. If the built-in
radio cannot pass multi-client soak testing, use a dedicated isolated access
point - never an open or WPA1 hotspot.

The private dnsmasq lease state is root-owned because dnsmasq opens that file
before dropping to its dedicated account. A systemd state-directory boundary
keeps the path writable under `ProtectSystem=strict` without granting
`CAP_DAC_OVERRIDE`; the installer preserves and repairs the lease file on rerun.
Hotspot readiness validates the three service states, exact arena address, and
the kernel-reported `AP` interface mode. The optional hostapd control socket is
retained for diagnostics but cannot roll back an otherwise operational radio.

## Application architecture

- .NET 8 ASP.NET Core server, self-contained `linux-arm64` release.
- SignalR publishes state/telemetry to TV and console; operational logs are in
  an authenticated operator group.
- A single lifecycle gate serializes relay reconnect, start, countdown, end,
  cleanup, and shutdown paths.
- Operator login uses a configured PIN, constant-time comparison, random
  in-memory bearer sessions, 12-hour maximum lifetime, and per-IP rate limits.
- Public GET state and TV endpoints remain read-only. Every mutation and saved
  roster request requires an operator session.
- Player settings are range-checked, normalized, written atomically, and stored
  in `/var/lib/lazertag` rather than the release tree.

## Game integrity controls

- Supported public modes: custom solo, custom 2/3 team, respawn 2/3 team, and
  survival 2 team.
- Minimum roster is 2. Protocol capacity is 16 for two-team games and 24 for
  three-team/solo games.
- Planned settings are applied in stable slot/join order.
- Countdown is rejected until the exact planned roster is connected and all
  taggers are confirmed.
- Ending in lobby/countdown disposes the stale host immediately so a new game
  can start cleanly.
- A completed game cannot be cleared while debrief reports are pending. Staff
  may explicitly force debrief completion if a failed tagger cannot report.
- Team-tag reports are length/BCD validated and decoded transactionally; invalid
  or truncated payloads do not mark reports complete or shift later scores.
- Hardware-observed duplicate detailed IR frames are normalized by capping each
  shooter-to-target count at the target's effective HP.

## ESP32 reliability controls

- Static address `.100`, gateway/DNS `.1`, with the DHCP pool excluding `.100`.
- Wi-Fi credentials live in git-ignored `RelaySecrets.h`.
- Receive counts are element-bounded and oversized/truncated RMT frames are rejected.
- Transmit buffers are cleared correctly and guarded against capacity overflow.
- RMT writes are serialized and blocking so back-to-back TCP commands cannot
  overwrite an active DMA/RMT buffer.
- Multi-line TCP announcement bursts are scheduled one command per loop pass so
  receive processing drains self-echo between transmitted signatures instead
  of starving the legacy RMT ring buffer.
- A 9-bit non-beacon with the checksum marker `0x100` uses encoder type `C` and
  its 80 ms packet-ending gap; ordinary 9-bit packet signatures use type `P`.
- Because legacy RMT duration fields are only 15 bits, the 80 ms checksum gap
  is split into four 20 ms low phases across two items instead of overflowing
  two 40 ms fields. Compile-time and source-invariant checks lock the
  25/56/80 ms gaps to 2/2/4 phases.
- Receive decoding validates the 2 ms header/intermediate pauses but not the
  final data mark's intentionally extended 8 ms idle tail. This fixes a field
  regression that silently discarded every otherwise-valid join response;
  bounded timing failures now increment `rxUnk` instead of looking like no IR.
- TCP command widths/data/beacon flags and response buffer lengths are validated.
- Score bitmasks use correct bitwise tests for all eight players.
- Firmware reports `ERROR TX_FAILED` when transmission fails.

The receiver can optically hear the relay's own emitter. Consequently, `rx` in
the serial `STATE` line may increase from self-echo and is not, by itself,
evidence that a tagger joined. `E rmt(legacy): RMT RX BUFFER FULL` is never an
acceptable steady-state message: it means a completed capture could not be
queued and IR data was dropped. Firmware acceptance must include repeated
standard and enhanced announcement bursts with no buffer-full messages, plus a
real join/assignment/acknowledgement exchange observed by the host application.
Unexpected `rxUnk` growth means captures are arriving but failing validation.

## Runtime configuration

The hardened systemd unit reads `/etc/lazertag/lazertag.env`:

```text
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:5000
Arena__OperatorPin=<unique 8-12 digit value>
Arena__OperatorSessionHours=12
Arena__RelayAddress=192.168.4.100:8266
Arena__DataDirectory=/var/lib/lazertag
```

Production startup fails if the PIN is missing, too short, or a common value.

## Deployment and recovery

- Dedicated `lazertag` system user with no login shell.
- Read-only, checksum-addressed releases in `/opt/lazertag/releases` and an
  atomic `/opt/lazertag/current` symlink.
- systemd sandboxing, empty capability set, bounded address families, restart
  policy, startup health gate, and deterministic shutdown.
- `lazertag-deploy` validates ARM64 archives, backs up state, activates atomically,
  performs `/healthz`, and automatically restores the previous release on failure.
- `lazertag-backup` creates verified root-only state/config archives with SHA-256.
- `lazertag-rollback` selects an installed release and health-checks the result.

See `deployment/PI_SETUP.md` for exact installation, update, restore, operations,
and public-game acceptance procedures.

## Verification completed in this workspace

- Release C# build: zero warnings/errors.
- Automated ASP.NET integration tests: four passed (public health/state,
  anonymous mutation rejection, login/logout token lifecycle, roster capacity).
- JavaScript syntax check: passed.
- Browser interaction: operator lock/unlock and roster limits passed; no console
  errors at phone size or 1280x720 TV size.
- ESP32 Arduino-ESP32 3.3.7 warning-strict compile produced a 73% flash / 15% RAM
  image; only legacy-RMT deprecation warnings remain.

## Mandatory venue acceptance before public use

Software verification cannot certify the installed IR optics, wiring, power,
radio environment, taggers, TV, or emergency procedures. Complete every item in
`deployment/PI_SETUP.md` section "Hardware and public-game acceptance," record
the results, train staff, and keep a tested spare Pi image and ESP32 relay onsite.
