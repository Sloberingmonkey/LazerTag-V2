# LazerTag ESP32 IR Relay

Production deployment notes for the Wi-Fi/TCP ESP32 relay used by the arena
server. The ESP32 uses the RMT peripheral for 38 kHz LTTO transmit/receive
timing and accepts one control client at a time.

## Network layout

The normal configuration is station (STA) mode:

| Device | Address | Purpose |
|---|---:|---|
| Arena controller / Pi hotspot | `192.168.4.1/24` | Gateway, DNS, and game server |
| ESP32 relay | `192.168.4.100/24` | TCP IR relay on port `8266` |

The firmware applies the following static configuration before every STA
connection:

- address: `192.168.4.100`
- subnet mask: `255.255.255.0`
- gateway: `192.168.4.1`
- DNS: `192.168.4.1`

Reserve or exclude `192.168.4.100` from the hotspot DHCP pool. An address
collision will make game control unreliable.

Copy `sketches/RelayESP32/RelaySecrets.example.h` to `RelaySecrets.h` in the
same directory and replace the password placeholder. `RelaySecrets.h` is
git-ignored so the WPA2 credential is not stored with the source. The normal
STA build intentionally fails when this file is absent; open arena Wi-Fi is not
a supported production configuration.

`USE_AP` is a development fallback in the sketch. When enabled, the ESP32
creates an access point instead of using the documented `192.168.4.100` STA
address. Do not enable it on the normal Pi-hosted arena network.

## TCP protocol

Connect the arena controller to `192.168.4.100:8266`. Lines are ASCII and end
with CRLF. A client must complete the version handshake before sending IR
commands.

| Direction | Line |
|---|---|
| Host to relay | `HELLO 1` |
| Relay to host | `READY 1` |
| Host to relay | `PING` |
| Relay to host | `PONG` |
| Host to relay | `CMD 10 <dataHex> <bitsHex> <beaconFlag>` |
| Relay to host | `OK` or `ERROR <code>` |
| Relay to host | `RCV <valueHex> <bitsHex> <isBeacon>` |
| Host to relay | `BYE` |

For `CMD 10`, `beaconFlag` must be `0` or `1`. Non-beacon frames accept 7, 8,
or 9 bits; beacon frames accept 5 or 9 bits. `dataHex` must fit the declared
width. Malformed values are rejected without transmitting. `OK` is returned
only after the serialized RMT transmission completes successfully, so rapid
back-to-back TCP commands cannot overwrite a live RMT buffer.

Packet and checksum signatures are both sent as 9-bit non-beacon commands.
The host sets bit 8 (`0x100`) on a checksum. The relay must therefore map a
9-bit value with bit 8 clear to encoder type `P`, and one with bit 8 set to
encoder type `C`. Type `C` supplies the required 80 ms packet-ending gap;
treating a checksum as `P` supplies only the normal 25 ms signature gap and can
prevent a tagger from recognizing the completed packet.

Legacy ESP32 RMT duration fields are 15 bits, so one field can represent at
most 32,767 us. The encoder therefore emits the 80 ms checksum silence as four
20 ms low phases in two RMT items instead of overflowing two 40 ms fields.
Compile-time assertions lock the 25/56/80 ms gaps to 2/2/4 phases, and the
source-invariant test rejects a return to the unsafe direct half-gap encoding.

An announcement packet arrives as several `CMD` lines in one TCP write. The
relay deliberately processes at most one complete line per main-loop pass and
then services RMT receive before reading the next line. This scheduling rule is
part of the IR transport contract: a blocking transmit can make the receiver
capture the relay's own emitter, and the receive ring buffer must be drained
between signatures instead of being starved for the whole announcement burst.

The RMT receiver ends a signature after 8 ms of idle input. Consequently, the
space after the final data mark is an extended end-of-signature gap, not the
normal 2 ms inter-bit space. The decoder validates the 2 ms pause after the
header and every intermediate data bit, but deliberately excludes that final
extended gap from the 2 ms upper limit. Bounded captures that still fail timing
validation are surfaced as `rxUnk` and are never forwarded to the host.

## Wiring

The pin and channel assignments in the current sketch are:

| Signal | ESP32 pin | RMT channel | Notes |
|---|---:|---:|---|
| Demodulated IR receiver output | GPIO 15 | 0 | TSOP4838-class, active low |
| IR transmitter driver input | GPIO 4 | 1 | Never drive a high-current IR LED directly |

Power a compatible receiver from 3.3 V so its output cannot exceed the ESP32
input rating. Place a 100 nF ceramic capacitor at the receiver supply pins;
add local bulk capacitance if the LED pulses disturb the supply.

Use a low-side transistor or 3.3 V logic-level MOSFET for the transmitter:

```text
+5 V --- current-limit resistor --- IR LED anode
                                  IR LED cathode --- drain/collector
GPIO 4 --- gate/base resistor --------------------- gate/base
GND ----------------------------------------------- source/emitter
```

Tie the ESP32 and LED-supply grounds together. For a MOSFET, add a gate
pulldown. Choose the LED resistor from the supply voltage, LED pulse-current
rating, forward voltage, and transistor drop; then verify pulse current and
device temperature on the assembled hardware. Enclose exposed wiring so a
short or disconnected emitter cannot interrupt a public game.

## Build and upload

1. Install Arduino IDE 2.x or `arduino-cli` and Espressif's `esp32` board core.
2. Keep the bundled library at `libraries/ESP32_IR_LTTO/`. If using the IDE,
   copy that directory into the Arduino libraries directory, or add the parent
   `libraries` directory to the CLI command.
3. Select `ESP32 Dev Module` (or the exact installed board) and open
   `sketches/RelayESP32/RelayESP32.ino`.
4. Compile, upload, and monitor the USB serial port at 115200 baud.

Example CLI compile from this directory:

```powershell
arduino-cli compile --fqbn esp32:esp32:esp32 --libraries C:\Server\esp32_relay\libraries C:\Server\esp32_relay\sketches\RelayESP32
```

The bundled IR library uses ESP-IDF's legacy RMT API. Arduino-ESP32 3.x still
provides it with deprecation warnings, but a future board-core release may
require migration to the newer RMT encoder API. Compile against the exact core
version intended for arena deployment and keep the resulting firmware binary
with the release artifacts.

## Security and operations

The TCP protocol has no authentication or encryption. Treat the relay as arena
control equipment, not as an internet service.

- Put the controller and relays on a dedicated WPA2/WPA3 operations SSID or an
  isolated VLAN; do not bridge that network to guest Wi-Fi.
- Firewall TCP port `8266` so only the arena controller can reach it. Never
  forward it from the internet.
- Replace default SSIDs/passwords and restrict physical access to USB and reset
  controls. Wi-Fi credentials are embedded in the firmware image.
- Reserve each relay address and label the enclosure with its assigned IP,
  MAC address, firmware build, RX/TX pins, and power requirements.
- Keep a tested spare relay and a known-good firmware binary available during
  public sessions.

Before opening games, test receiver decoding, transmitter range, self-echo
suppression, reconnect behavior, and a burst of back-to-back commands with the
actual taggers. Watch the 30-second serial `STATE` line: `parseErr`, `txFail`,
and `tcpWrFail` should remain zero during a clean soak test. Because receive is
active around transmissions, optical self-echo can make `rx` increase; an
increasing `rx` counter alone does not prove that a tagger joined. By contrast,
`E rmt(legacy): RMT RX BUFFER FULL` must never appear in a clean test. It means
the ESP-IDF receive ring buffer could not accept a completed capture and that
IR data was dropped. A rapidly increasing `rxUnk` indicates captured IR that
failed timing/type validation and should trigger an optics, range, or decoder
investigation. Also verify LED driver temperature and supply stability at the
maximum expected duty cycle.

## Current scope

- The decoder accepts LTTO/LTAR-style tag, beacon, packet, data, and checksum
  frame shapes; unrelated IR protocols are not supported.
- One TCP control session is active at a time.
- mDNS advertises the host name `lazertag.local`, but production control should
  use the reserved static address.
- A successful compile is not a substitute for range, electrical, thermal, and
  multi-tagger testing in the installed arena.
