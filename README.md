# LTAR Laser Tag Server

Web-based game server for LTAR laser tag guns, running on a Raspberry Pi 3.

## Architecture

- **LazerTagHostLibrary** — C# game protocol engine (IR packet encoding, state machine, scoring)
- **LazerTagWebServer** — ASP.NET Core 8 web server with SignalR real-time updates
- **LazerTagServerApp** — Original Windows Forms host app (reference only, not used in production)

## Hardware

- Raspberry Pi 3 (Raspberry Pi OS Lite 64-bit, Trixie)
- ESP32 WiFi relay board (IR bridge between Pi and LTAR guns via TCP)
- LTAR / Tiger / Team Ops laser tag guns

## Network Topology

The Pi creates an open WiFi hotspot called "LazerTag". The ESP32 relay joins it as a client.

| Device | IP | Role |
|--------|----|------|
| Raspberry Pi | 192.168.4.1 | Hotspot + web server (port 5000) |
| ESP32 relay | 192.168.4.100 | IR bridge (port 8266) |
| Phones/tablets | DHCP | Game controller UI via browser |
| TV (via Pi HDMI) | localhost | Kiosk display (`/tv.html`) |

## Requirements

- .NET 8 ASP.NET Core Runtime (`linux-arm64`)
- `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` (set in the systemd service)

## Build & Deploy

```bash
# From your dev machine (Windows)
dotnet publish LazerTagWebServer -c Release -r linux-arm64
scp -r LazerTagWebServer/bin/Release/net8.0/linux-arm64/publish/* pi@192.168.0.169:/home/pi/lazertag/
ssh pi@192.168.0.169 "chmod +x /home/pi/lazertag/LazerTagWebServer && sudo systemctl restart lazertag"
```

## Usage

1. Power on the Pi — the server starts automatically via systemd.
2. Connect your phone to the "LazerTag" WiFi network.
3. Open `http://192.168.4.1:5000` in any browser.
4. Set up players, choose a game type, and tap **Start Hosting**.
5. Turn on guns — they join in the order listed.
6. Tap **Countdown** when all players are connected.

The TV display at `/tv.html` shows lobby, countdown, game clock, and scores automatically.
