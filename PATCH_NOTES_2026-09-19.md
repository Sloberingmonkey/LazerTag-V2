# LazerTag V2 reliability patch - 2026-09-19

This patch addresses the reliability issues found during the source review of the 2.0.0-rc.1 project.

## Fixed

- Unexpected TCP relay disconnects now raise `IoError` and clear the connection state instead of being treated like intentional shutdowns.
- `StartHostingAsync` now fails when the HostGun cannot actually connect to the configured ESP32 relay.
- Writes no longer perform multi-second reconnect attempts inline on the game update thread. Reconnect attempts are scheduled in the background and sends fail fast while disconnected.
- HostGun state access is synchronized so packet reception, update ticks, operator actions, snapshots, and history capture do not concurrently mutate/enumerate the player/team collections.
- Lobby/assignment-failure player removal now removes the player from both the global roster and `Team.Players`, and clears related join state.
- Repeated join requests from an already-confirmed tagger reuse the existing player slot rather than creating a duplicate player.
- A failed repeated-join handshake preserves the already-confirmed player's roster slot.
- Dropped players are no longer exposed to the TV as successfully debriefed; the TV shows them as `DROPPED`.
- `GamesStarted` telemetry now increments when gameplay actually enters `Playing`, not when a lobby is opened.
- HostGun deadline calculations use UTC time to avoid local clock / daylight-saving transitions affecting timers.

## Regression coverage added

`LazerTagWebServer.Tests/ReliabilityRegressionTests.cs` adds coverage for:

- duplicate confirmed joins;
- removal from team rosters;
- unexpected TCP relay closure notification/state clearing;
- nonblocking enqueue while disconnected.

`HostGunServiceTests.cs` also verifies that dropped players are not marked successfully debriefed in snapshots.

## Verification performed in this workspace

- ESP32 source/timing invariant tests: passed.
- Deployment shell scripts: syntax checked.
- TV JavaScript: Node syntax check passed.
- Edited C# files: structural brace checks passed.

A .NET SDK was not available in this workspace, so `dotnet build` / `dotnet test` could not be executed here. Run the normal CI/test suite before deployment.

## Security note

`esp32_relay/sketches/RelayESP32/RelaySecrets.h` contains the local Wi-Fi credential and should not be committed or shared. The shareable source archive intentionally omits it; use `RelaySecrets.example.h` to recreate it locally.
