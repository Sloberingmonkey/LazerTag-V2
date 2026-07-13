# LTAR Arena Raspberry Pi Deployment and Operations Guide

This runbook builds a reproducible, appliance-style LTAR server on a Raspberry
Pi 3B/3B+. It targets **Raspberry Pi OS Lite 64-bit (Trixie / Debian 13)** and a
self-contained .NET 8 `linux-arm64` release.

The production layout is intentionally split:

| Purpose | Path | Ownership |
|---|---|---|
| Immutable releases | `/opt/lazertag/releases/release-*` | `root:lazertag`, read-only |
| Active release link | `/opt/lazertag/current` | managed by deploy/rollback scripts |
| Secrets and configuration | `/etc/lazertag` | root-owned; environment file mode `0600` |
| Persistent arena state | `/var/lib/lazertag` | `lazertag:lazertag`, mode `0750` |
| Logs | systemd journal | `journalctl -u lazertag` |
| Backups | `/var/backups/lazertag` | root-only, mode `0600` |

The web server runs as the non-login `lazertag` system user. It never runs as
`pi`, the administrator, or root.

## 1. Production hardware and prerequisites

- Raspberry Pi 3B or 3B+, quality 5 V / 2.5 A supply, ventilated enclosure
- High-endurance 16 GB or larger microSD card; a small UPS is strongly advised
- ESP32 relay configured for TCP port `8266`
- Wired Ethernet for initial installation and later administration
- Secured, dedicated arena Wi-Fi; the installer uses standalone hostapd on the
  built-in radio, while a dedicated external AP remains preferable at scale
- Optional HDMI display for the read-only TV kiosk
- A workstation with the .NET 8 SDK, OpenSSH client, and `tar`

Use Raspberry Pi Imager to install **Raspberry Pi OS Lite (64-bit), Trixie**.
In Imager customization, set a unique administrator username (not `pi`), install
an SSH public key, set the correct country/time zone, and disable password-based
SSH after confirming key login. Do not expose this appliance directly to the
internet.

After first boot, connect Ethernet and update the base image:

```bash
sudo apt update
sudo apt full-upgrade -y
sudo reboot
```

Reconnect over Ethernet. The installer refuses older OS releases and non-aarch64
images so the deployment matches its tested package, kernel, and service baseline.

## 2. Fixed arena network contract

| Node | Address | Notes |
|---|---|---|
| Pi hotspot/server | `192.168.4.1/24` | UI on TCP `5000` |
| Phone/tablet leases | `192.168.4.20`-`192.168.4.99` | 8-hour dnsmasq leases |
| ESP32 relay | `192.168.4.100/24` | Static; TCP `8266`; gateway `192.168.4.1` |

`configure-hotspot.sh` assigns **`192.168.4.1/24`** before starting a private
dnsmasq instance with DHCP range `.20`-`.99`, so `.100` can never be leased to a
guest device. hostapd AP isolation prevents stations from reaching one another
or opening the relay directly. The nftables policy lets hotspot clients reach
DHCP/DNS, ICMP, and the arena app on port 5000, while blocking forwarding in
either direction between the arena WLAN and an attached business/admin network.

The radio is removed from NetworkManager control without changing Ethernet
administration. Standalone hostapd explicitly advertises WPA2-PSK/RSN/CCMP only:
no WPA1, TKIP, WPA3, mixed mode, or open fallback. The reusable Wi-Fi key is
stored as a root-only derived 64-hex PSK instead of a plaintext passphrase.

This backend is intentional. Raspberry Pi OS Trixie has open Pi 3 Broadcom
hotspot problems in NetworkManager/wpa_supplicant AP mode. In field testing the
mixed NetworkManager profile accepted Motorola phones but rejected the ESP32,
while strict RSN accepted the ESP32/Windows but rejected the phones. Direct
hostapd avoids that negotiation path and sets `ieee80211w=0`, required by the
affected Pi 3 firmware. PMF being disabled reduces protection against forged
wireless deauthentication frames. For a crowded public venue, a dedicated AP
with tested WPA2/PMF support and adequate client capacity remains preferable.

> Reconfiguring `wlan0` drops any SSH session using it. Install and deploy over
> Ethernet or from the local console.

## 3. Build a release on the workstation

From PowerShell in `C:\Server`:

```powershell
Remove-Item -Recurse -Force C:\Server\artifacts\publish -ErrorAction SilentlyContinue
dotnet publish C:\Server\LazerTagWebServer\LazerTagWebServer.csproj `
  -c Release `
  -r linux-arm64 `
  --self-contained true `
  -o C:\Server\artifacts\publish

tar -czf C:\Server\artifacts\lazertag-linux-arm64.tar.gz `
  -C C:\Server\artifacts\publish .
Get-FileHash C:\Server\artifacts\lazertag-linux-arm64.tar.gz -Algorithm SHA256
```

The archive must be flat: `LazerTagWebServer`, its `.deps.json` and
`.runtimeconfig.json`, and `wwwroot/` must be at the archive root. Do not package
the `publish` directory itself.

Copy the deployment bundle and release over the wired management network:

```powershell
scp -r C:\Server\deployment arenaadmin@PI_ETHERNET_IP:/tmp/lazertag-deployment
scp C:\Server\artifacts\lazertag-linux-arm64.tar.gz arenaadmin@PI_ETHERNET_IP:/tmp/
```

Replace `arenaadmin` and `PI_ETHERNET_IP` with the Imager-created administrator
and the Pi's wired address.

## 4. Prepare secrets without putting them on a command line

Create a WPA2 password file on the Pi. Use a unique random password of at least
16 characters that the ESP32 can also store:

```bash
sudo bash -c 'umask 077; read -r -s -p "Arena Wi-Fi password: " p; printf "\n"; printf "%s\n" "$p" > /root/lazertag-wifi.pass'
```

For the operator PIN, either let the installer generate a 10-digit value or
provide an 8-12 digit value from the arena password manager:

```bash
sudo bash -c 'umask 077; read -r -s -p "Operator PIN (8-12 digits): " p; printf "\n"; printf "%s\n" "$p" > /root/lazertag-operator.pin'
```

Secret input files must be root-owned mode `0600`. Secrets passed directly as
command-line arguments can be exposed in process listings and shell history, so
the installer does not accept them that way.

## 5. Install the appliance

Run the idempotent installer from the Pi:

```bash
cd /tmp/lazertag-deployment
sudo ./install.sh \
  --release /tmp/lazertag-linux-arm64.tar.gz \
  --hotspot-password-file /root/lazertag-wifi.pass \
  --operator-pin-file /root/lazertag-operator.pin
```

If `--operator-pin-file` is omitted on the first install, the installer writes a
generated PIN to `/root/lazertag-initial-operator-pin.txt` without printing the
secret. Read it from the local console, put it in the password manager, then
remove the one-time file.

The installer:

1. installs the required OS packages (the app itself is self-contained);
2. creates the locked `lazertag` system account and production directories;
3. writes the root-only production environment;
4. removes only `wlan0` from NetworkManager, then enables the WPA2 hostapd,
   private dnsmasq, and isolation firewall services;
5. installs a checksum-addressed release and switches `current` atomically;
6. starts the hardened systemd service and requires `/healthz` to pass.

After recording the secrets, remove the temporary input files and deployment
copy:

```bash
sudo rm -f /root/lazertag-wifi.pass /root/lazertag-operator.pin \
  /root/lazertag-initial-operator-pin.txt
rm -rf /tmp/lazertag-deployment /tmp/lazertag-linux-arm64.tar.gz
```

The reusable derived Wi-Fi PSK remains in `/etc/lazertag/hostapd.conf` mode
`0600`; the operator PIN remains in `/etc/lazertag/lazertag.env` mode `0600`.

### Upgrade an older NetworkManager hotspot

This upgrade replaces the former NetworkManager/wpa_supplicant AP without
changing the SSID, password, IP addresses, application, or operator PIN. Connect
over Ethernet, upload the new `deployment` directory, recreate the root-only
Wi-Fi password file with the password already stored in the ESP32, and run:

```bash
cd /tmp/lazertag-deployment
sudo ./install.sh --hotspot-password-file /root/lazertag-wifi.pass
```

The installer stages and validates the new configuration before stopping the
old AP. If activation fails, it restores the previous backend. After a successful
migration, power-cycle the ESP32, forget/rejoin `LazerTag` on test phones, and run:

```bash
sudo /usr/local/libexec/lazertag/hotspot-check
sudo hostapd_cli -p /run/lazertag-hostapd -i wlan0 get_config
sudo /usr/local/libexec/lazertag/health-check
```

The hostapd output must show WPA2/RSN, `WPA-PSK`, and CCMP. Do not migrate over
an SSH session carried by `wlan0`; the safety check intentionally refuses it.

### External secured AP instead of Pi hotspot

If the built-in radio cannot pass the WPA acceptance tests, use a standalone AP
that is not bridged to the business LAN. Configure its LAN as `192.168.4.0/24`
with DHCP `.20`-`.99`, and reserve/exclude `.1` and `.100`. Give the Pi's arena
Ethernet interface `.1` (adjust the connection name if needed):

```bash
sudo nmcli connection modify 'Wired connection 1' \
  ipv4.method manual \
  ipv4.addresses 192.168.4.1/24 \
  ipv4.never-default yes \
  ipv6.method disabled
sudo nmcli connection up 'Wired connection 1'
```

Then run `install.sh` with `--external-network` and no hotspot password file.
The external AP/firewall must isolate the entire `192.168.4.0/24` arena segment
from staff, payment, camera, and internet-facing networks while still allowing
arena clients to reach the wired Pi and ESP32.

## 6. Configure and verify the ESP32

Program the ESP32 with:

```text
SSID:             LazerTag
Security:         WPA2-PSK, same password as the hotspot
IPv4 address:     192.168.4.100
Prefix/netmask:   /24 (255.255.255.0)
Gateway:          192.168.4.1
TCP listen port:  8266
```

Do not leave the ESP32 on DHCP unless its firmware uses a true DHCP reservation
identified by a fixed MAC. The server's production configuration is:

```text
Arena__RelayAddress=192.168.4.100:8266
Arena__DataDirectory=/var/lib/lazertag
```

Power-cycle the ESP32 after changing Wi-Fi credentials.

## 7. Optional HDMI TV kiosk

The server does not require a GUI. For a locally attached TV, create a separate,
non-sudo kiosk login; do not run Chromium as `lazertag` or the administrator:

```bash
sudo apt install -y --no-install-recommends xserver-xorg xinit chromium unclutter
sudo useradd --create-home --shell /bin/bash arena-kiosk
sudo usermod -aG video,render,input arena-kiosk
sudo passwd --lock arena-kiosk
sudo install -o arena-kiosk -g arena-kiosk -m 0755 \
  /usr/local/libexec/lazertag/xinitrc /home/arena-kiosk/.xinitrc

sudo bash -c 'cat > /home/arena-kiosk/.bash_profile <<"EOF"
if [ -z "${DISPLAY:-}" ] && [ "$(tty)" = /dev/tty1 ]; then
    exec startx -- -nocursor
fi
EOF
chown arena-kiosk:arena-kiosk /home/arena-kiosk/.bash_profile
chmod 0644 /home/arena-kiosk/.bash_profile'

sudo install -d -m 0755 /etc/systemd/system/getty@tty1.service.d
sudo bash -c 'cat > /etc/systemd/system/getty@tty1.service.d/autologin.conf <<"EOF"
[Service]
ExecStart=
ExecStart=-/sbin/agetty --autologin arena-kiosk --noclear %I $TERM
Type=idle
EOF'
sudo systemctl daemon-reload
sudo systemctl restart getty@tty1
```

The kiosk waits for `/healthz`, uses a dedicated Chromium profile, and opens
`http://127.0.0.1:5000/tv.html`. Modern KMS normally detects HDMI correctly; do
not force a video mode unless a specific display fails its acceptance test.

## 8. Normal operations

Useful commands:

```bash
# Service and health
sudo systemctl status lazertag --no-pager
sudo /usr/local/libexec/lazertag/health-check

# Live and recent logs (the PIN is never logged by deployment scripts)
sudo journalctl -fu lazertag
sudo journalctl -u lazertag --since today --no-pager

# Hotspot and address
sudo /usr/local/libexec/lazertag/hotspot-check
sudo systemctl status lazertag-hostapd lazertag-dnsmasq --no-pager
ip -4 address show wlan0

# Confirm the relay is reachable and the server has a TCP session
ping -c 3 192.168.4.100
sudo ss -tnp | grep '192.168.4.100:8266'

# Controlled restart between games
sudo systemctl restart lazertag
sudo /usr/local/libexec/lazertag/health-check --wait 45
```

Game-day opening checklist:

1. Inspect power, cooling, HDMI, relay wiring, and IR emitters/receivers.
2. Confirm the secured `LazerTag` SSID and `192.168.4.1/24` on the Pi.
3. Run the health check and confirm the relay is connected in the operator UI.
4. Join one gun and run a short staff-only game through countdown and scoring.
5. Confirm the TV and at least one phone receive live updates.
6. Only then admit public players.

Use the operator UI at `http://192.168.4.1:5000`. Do not share the operator PIN
with guests. The default authenticated operator session lifetime is 12 hours;
restart/close shared control browsers at the end of the day.

## 9. Updates and automatic rollback

Build and copy a new archive as in section 3. Never update during a live game.
On the Pi:

```bash
sudo lazertag-deploy /tmp/lazertag-linux-arm64.tar.gz
```

`lazertag-deploy` verifies archive paths, required files, ARM64 ELF architecture,
and the archive checksum. It creates a pre-update backup, atomically switches the
release symlink, restarts the service, and waits for `/healthz`. A failed update
automatically switches back to the previously active release. The last five
releases are retained.

List and manually select installed releases:

```bash
sudo lazertag-rollback --list
sudo lazertag-rollback previous
# or:
sudo lazertag-rollback release-0123456789abcdef
```

Rollback changes application binaries only. It backs up but does not rewind
persistent state. If a release introduced an incompatible data migration, restore
the matching pre-update backup after switching binaries.

## 10. Backup, off-device copy, and restore

Create a consistent backup between games:

```bash
sudo lazertag-backup
```

The command briefly stops a running service, archives `/var/lib/lazertag` plus
the arena environment, hostapd, dnsmasq, hotspot, and firewall configuration,
validates the archive,
writes a SHA-256 sidecar, and restarts with a health check. Fourteen local backups
are retained by default. Because a backup contains the operator PIN, treat it as
a secret and copy it to encrypted off-device storage after every configuration
change and at least daily during operation.

Example from the administration workstation:

```powershell
scp arenaadmin@PI_ETHERNET_IP:/var/backups/lazertag/lazertag-backup-*.tar.gz* `
  D:\EncryptedArenaBackups\
```

Root permissions may prevent direct SCP. In that case, use `sudo cp` to a
temporary root-only file owned by the administrator, transfer it immediately,
then remove the temporary copy.

Restore only a trusted backup and inspect its paths first:

```bash
cd /var/backups/lazertag
sudo sha256sum -c lazertag-backup-YYYYMMDDTHHMMSSZ.tar.gz.sha256
sudo tar -tzf lazertag-backup-YYYYMMDDTHHMMSSZ.tar.gz

sudo systemctl stop lazertag
sudo tar --extract --gzip \
  --file lazertag-backup-YYYYMMDDTHHMMSSZ.tar.gz \
  --directory / \
  --same-owner \
  --acls \
  --xattrs
sudo chown -R lazertag:lazertag /var/lib/lazertag
sudo chown root:root /etc/lazertag/lazertag.env
sudo chmod 0600 /etc/lazertag/lazertag.env
sudo systemctl start lazertag
sudo /usr/local/libexec/lazertag/health-check --wait 45
```

For total SD-card recovery: reinstall the same OS, run `install.sh` with a known
good release and the network secret, stop the service, restore the backup, and
complete the full acceptance checklist before reopening.

## 11. Credential rotation

To change the operator PIN, create a root-only input file and rerun `install.sh`
with `--operator-pin-file`; existing PINs are otherwise preserved during every
upgrade. Restart and health validation are automatic when a release is active.

To rotate Wi-Fi, prepare a new root-only password file, update the ESP32 stored
credential during a maintenance window, and run:

```bash
sudo lazertag-configure-hotspot \
  --config /etc/lazertag/hotspot.conf \
  --password-file /root/new-lazertag-wifi.pass
sudo rm -f /root/new-lazertag-wifi.pass
```

All phones must rejoin with the new password. Verify the ESP32 reconnects before
starting another public game.

## 12. Hardware and public-game acceptance

Do not declare the arena production-ready until every item passes and is recorded
with date, OS/release checksum, tester, and result.

### Network and security

- Reboot five times; `wlan0` is `192.168.4.1/24`, hostapd reports `ENABLED`,
  dnsmasq is active, and WPA2-only is enabled every time.
- Connect at least the maximum expected number of phones/tablets; none receives
  `.100`, and all can load the UI and maintain SignalR updates for two hours.
- From a guest phone, verify SSH and unrelated Pi ports are blocked and the arena
  subnet cannot reach staff/payment/camera networks.
- From a guest phone, verify `192.168.4.100:8266` and another Wi-Fi client are
  unreachable while the Pi itself can still maintain the relay connection.
- Verify a wrong/missing operator PIN cannot start, alter, end, or reset a game.
- Verify the correct operator session expires according to policy.

### Relay and game lifecycle

- Confirm the ESP32 remains `192.168.4.100` after ten power cycles.
- Power-cycle the relay in Idle, Lobby, Countdown, and during a game; confirm the
  UI reports the outage, reconnects, and never silently starts/stops a game.
- Run every offered game type with the maximum supported gun count.
- Run at least ten consecutive games without rebooting, including a complete
  second game after Game Over; validate scores against known tags.
- Test join-order overrides, teams, countdown packets, end-game reports, and a
  gun that joins late or loses power.

### Display, recovery, and endurance

- Disconnect/reconnect HDMI and cold boot the TV/Pi in both orders; kiosk recovers.
- Interrupt power only in a controlled maintenance test with a cloned SD card;
  verify filesystem boot, state load, service health, and the latest backup.
- Deploy a deliberately invalid test release and confirm automatic rollback.
- Perform a manual rollback and a full backup restore drill.
- Run the entire appliance for at least eight hours while watching temperature,
  undervoltage indicators (`vcgencmd get_throttled`), relay connectivity, and
  journal errors.

Keep a tested spare Pi/SD image, power supply, ESP32 relay, IR cable, and printed
opening/recovery checklist on site. A software health check cannot replace a
physical staff test of the actual guns and IR path before customers play.
