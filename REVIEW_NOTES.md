# Production Readiness Review

Latest software pass: July 13, 2026

## Resolved deployment blockers

- Fixed the ESP32 transmit-buffer recursion that reset the relay on every send.
- Bounded RMT receive decoding and corrected byte-count/element-count misuse.
- Serialized back-to-back RMT transmissions and propagated real transmit failures.
- Fixed field-discovered RMT receive starvation by processing only one TCP line
  per loop pass, allowing the receive ring buffer to be drained between the
  blocking transmissions in a multi-signature announcement.
- Mapped 9-bit values with the checksum `0x100` marker to encoder type `C`,
  retaining the required 80 ms checksum gap instead of the 25 ms packet gap.
- Fixed the legacy RMT 15-bit duration overflow that truncated two 40 ms
  checksum-gap halves; the encoder now emits four 20 ms low phases in two
  items. Compile-time assertions and source invariants protect the 25/56/80 ms
  gaps at 2/2/4 phases.
- Fixed receive validation that incorrectly applied the 2 ms inter-bit limit
  to the final data mark's 8 ms RMT idle tail, causing valid join responses to
  be silently discarded. Invalid bounded captures now appear in `rxUnk`.
- Fixed all eight score-report firmware bitmasks.
- Made Pi `.1` and ESP32 `.100` addressing deterministic and excluded `.100` from DHCP.
- Replaced the incompatible Pi 3/Trixie NetworkManager AP backend with strict
  WPA2-only hostapd, private dnsmasq, AP isolation, and an nftables allowlist;
  open or mixed WPA1 public Wi-Fi is unsupported.
- Aligned dnsmasq lease ownership with its pre-drop startup order using a
  root-owned systemd state directory, fixing DHCP startup without adding
  `CAP_DAC_OVERRIDE` or weakening the service sandbox.
- Removed fragile hostapd control-socket readiness gating that rolled back a
  healthy hostapd/dnsmasq stack after an ESP32 completed its WPA2 handshake.
  Readiness now verifies the active services, exact arena address, and the
  kernel-reported AP mode; regression coverage also rejects non-AP mode.
- Added required, rate-limited operator PIN sessions to every game mutation and saved roster API.
- Restricted live operational logs to authenticated operator SignalR connections.
- Fixed lobby/countdown end-game cleanup, exact/confirmed roster countdown checks,
  team capacity validation, validated BCD team-report counts, and premature debrief cleanup.
- Made tag-report decoding transactional so truncated/invalid payloads cannot mark a
  report complete or shift later player values.

## Production operations added

- Dedicated non-login `lazertag` service account and sandboxed systemd unit.
- Versioned, checksum-addressed, read-only releases with atomic activation.
- Startup health validation and automatic rollback to the prior release.
- Root-only configuration/state backups with SHA-256 verification and retention.
- Explicit manual rollback and documented restore drills.
- PIN and Wi-Fi credentials kept outside tracked source/release files.
- CI definition for server tests, JavaScript validation, ARM64 packaging, and
  ESP32 compile verification.

## Verification evidence

- Full solution Release build: 0 warnings, 0 errors.
- ASP.NET integration tests: 5 passed, 0 failed, including all public web assets.
- Packaged cross-working-directory smoke: operator UI, TV UI, CSS, and JavaScript
  returned HTTP 200 from the published release; `/healthz` returned version `1.0.1`.
- Production smoke: missing PIN startup was
  rejected; anonymous mutation returned HTTP 401; CSP security header was present.
- Browser: operator lock/unlock, mode-specific roster limits, phone layout, and
  1280x720 TV idle display passed with no console warnings/errors.
- `linux-arm64` self-contained apphost verified as 64-bit ARM ELF and packaged.
- ESP32 Arduino-ESP32 3.3.7 build produced a 967,776-byte firmware image; only
  the board core's legacy-RMT deprecation warnings remain.
- All deployment shell scripts pass Bash syntax validation.
- Hostapd policy is pinned to WPA2/RSN, `WPA-PSK`, CCMP, and the Pi 3 PMF
  workaround; the remaining phone/ESP32 coexistence test requires Pi hardware.

The recorded firmware image predates the receive-scheduling, checksum-gap, and
final-space decoder fixes above. Recompile and upload before the next hardware
run. During that run, `rx` may increase because the receiver hears the relay's
own emitter; that counter is not sufficient evidence of a real tagger response.
The legacy driver message `RMT RX BUFFER FULL` means a capture was dropped and
must not appear during standard or enhanced announcement soaks. Unexpected
`rxUnk` growth means captures are reaching RMT but failing validation. Confirm
the full join/assignment/acknowledgement flow in the host UI, not only counters.

## Required physical acceptance

This code cannot certify venue wiring, optics, power, RF conditions, tagger
condition, staff procedure, or customer safety. Public use remains gated on the
recorded acceptance checklist in `deployment/PI_SETUP.md`, including maximum-roster
game tests, score reconciliation, relay dropout/power-cycle tests, IR range and
thermal measurements, eight-hour endurance, backup restore, and rollback drills.
