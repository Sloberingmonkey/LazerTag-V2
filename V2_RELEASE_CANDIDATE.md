# LTAR Arena 2.0.0-rc.1 Release Candidate

This file is the handoff record for the operations-focused Version 2 candidate.
It can be copied into another support chat to explain exactly what changed and
how Version 1 is protected.

## Release safety

- The last fully hardware-verified source is preserved locally as branch
  `codex/v1-known-good` and annotated tag `v1.0.2-known-good`.
- Version 2 work is isolated on branch `codex/v2-operations`.
- The candidate identifies itself as `2.0.0-rc.1`; it is not a final Version 2
  release until the physical-arena acceptance checks below pass.
- No ESP32 firmware, IR timing, tagger protocol, debrief parser, or scoring rule
  was changed for this candidate.
- Nothing is pushed to GitHub or installed on the Raspberry Pi automatically.

## Candidate changes

- Persistent, operator-only completed-game history with atomic JSON storage,
  retention, detail review, and JSON/CSV downloads.
- Pre-countdown readiness checks for relay status, exact roster, tagger
  confirmation, and valid team coverage.
- Deterministic Auto team planning so every declared team has a player before a
  team-game lobby can be opened.
- Reconnect-safe operator state: active game type and timing come from the
  server, and stale controls are disabled if the browser loses current state.
- TV results use the game engine's authoritative player/team scores and ranks,
  including tied ranks and team-mode scoring.
- Incomplete forced debriefs are retained for diagnostics but clearly marked as
  incomplete; they do not announce zero/stale values as final scores.
- Persistent TV warnings distinguish a disconnected ESP32 relay from a web
  server/state connection outage.
- Solo snapshots and saved results use global player slots and do not expose the
  library's internal team-addressing buckets as real teams.
- Completed-result disk writes run outside the game lifecycle gate so slow Pi SD
  storage cannot hold up End Game, New Game, or the next lobby.

## Hardware acceptance before promoting to Version 2

Run these checks on the arena network while Version 1 remains available for
rollback:

1. Run one complete solo game with at least two taggers; verify joining,
   countdown, play, natural debrief, tied ranks if practical, TV results, and the
   saved history download.
2. Run complete two-team and three-team games; verify Auto assignments cover all
   teams and the TV/history team winner matches the taggers.
3. During a lobby, disconnect the operator device from Wi-Fi; verify Countdown is
   disabled/stale, then reconnect and verify readiness recovers.
4. During play, power off the ESP32 relay; verify the TV and operator page warn
   about relay loss without erasing the last game state. Restore it and verify
   the warning clears.
5. Force an incomplete debrief only as a deliberate test; verify it is labeled
   incomplete and no winner is announced from unfinished scores.
6. Reload the operator page during lobby, play, and results; verify the displayed
   game type and timing remain correct.
7. Start the next game immediately after Game Over while also opening/downloading
   history; verify the UI remains responsive.
8. Reboot the Pi and confirm `/healthz`, the operator page, TV page, relay, and
   saved history recover normally.

## Deploy and rollback

To test the candidate, open `C:\Server\LazerTagWebServer` in Visual Studio Code,
choose **Terminal > Run Task**, and run **Deploy Web Server to Pi**. The deployment
task publishes ARM64, health-checks the new release, and automatically restores
the previous Pi release if startup fails.

If a hardware behavior regression appears after a successful deploy, stop the
test and restore the previous installed Pi release:

```bash
sudo lazertag-rollback previous
sudo /usr/local/libexec/lazertag/health-check
```

The source-side Version 1 reference is:

```powershell
git switch codex/v1-known-good
git describe --tags --always
```

Return to the candidate with:

```powershell
git switch codex/v2-operations
```

Do not switch branches while local files are uncommitted.
