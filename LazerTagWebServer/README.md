# LazerTag Web Server

ASP.NET Core 8 control server and live TV display for the LTAR arena appliance.
The production architecture, build commands, security model, and deployment
procedure are documented in the repository [`README.md`](../README.md) and
[`deployment/PI_SETUP.md`](../deployment/PI_SETUP.md).

## Local development

With the .NET 10 SDK installed:

```powershell
cd C:\Server
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Arena__OperatorPin = '73918426'
$env:Arena__DisableRelayReconnect = 'true' # do not contact arena hardware
dotnet run --project .\LazerTagWebServer\LazerTagWebServer.csproj
```

Open `http://localhost:5000`. Development refuses game mutations until the
operator console is unlocked. Do not use the example PIN in Production.

Run the integration suite with:

```powershell
dotnet test .\LazerTagWebServer.Tests\LazerTagWebServer.Tests.csproj -c Release
```

Production requires `Arena__OperatorPin`, uses `Arena__RelayAddress` for the
ESP32 endpoint, and stores state under `Arena__DataDirectory`. The hardened Pi
installer creates and protects those settings automatically.

## Completed-game history

The first transition to Game Over freezes an immutable result and queues an
atomic JSON write under `Arena__DataDirectory/game-history`.
`Arena__HistoryRetention` controls the number of results retained (default
1000, maximum 10000). Slow or failed storage never holds the game lifecycle
lock or blocks the next game. Forced/incomplete debriefs are retained for
diagnostics with `resultsFinalized=false`; their score and rank fields must not
be treated as authoritative.

Authorized operators can review the eight most recent results directly in the
control page, inspect player rankings, and download either CSV or JSON.

After unlocking the operator console, its bearer token authorizes these routes:

- `GET /api/history?limit=50` - recent result summaries
- `GET /api/history/{gameId}` - one saved result, including finalization status
- `GET /api/history/{gameId}/download.json` - original JSON record
- `GET /api/history/{gameId}/download.csv` - spreadsheet-friendly player results

## Deploy from Visual Studio Code

After completing the one-time Pi installation in
[`deployment/PI_SETUP.md`](../deployment/PI_SETUP.md), open this
`LazerTagWebServer` directory in Visual Studio Code and run:

1. **Terminal > Run Task**
2. **Deploy Web Server to Pi**
3. Enter the Pi SSH target, such as `arenaadmin@192.168.1.50`

The task publishes the latest code for `linux-arm64`, creates an archive,
uploads it with SCP, and runs the installed Pi deployer. The deployer restarts
the service, checks `/healthz`, and automatically restores the previous release
if the new server does not become healthy. SSH keys are recommended; if the Pi
requires a password or `sudo` password, enter it in the VS Code terminal.
