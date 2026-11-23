# LTAR Arena 2.0.0-rc.3 Release Candidate

This file is the handoff record for the operations/gameplay-focused Version 2 candidate.
It can be copied into another support chat to explain exactly what changed and
how Version 1 is protected.

## Release safety

- The last fully hardware-verified source is preserved locally as branch
  `codex/v1-known-good` and annotated tag `v1.0.2-known-good`.
- The earlier operations candidate is preserved on branch `codex/v2-operations`. The rc.3 changes are in the local working tree.
- The candidate identifies itself as `2.0.0-rc.3`; it is not a final Version 2
  release until the physical-arena acceptance checks below pass.
- No ESP32 firmware, IR timing, debrief parser, or scoring rule was changed for
  rc.2. The server-side enhanced Respawn announcement flags were corrected.
- Nothing is pushed to GitHub or installed on the Raspberry Pi automatically.

## Candidate changes

- The Pi server and Windows LAN updater ship with .NET 10. The Windows desktop host still targets .NET Framework 4.8.
- Games hold the Pi deployment lock from Start through the last pending result save. A live update can reserve the arena between games. Upgrading a running older server requires a one-time stop during an idle window.
- The phone and TV use server revisions to order updates, with HTTP fallback when live messages stop. The host schedules game deadlines from elapsed time and the TV displays the remaining interval sent by the server.
- The relay bounds queued commands and matches each ESP32 reply to the transmitted signature. A rejected enhanced debrief release can retry for that player during Summary or Game Over.

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
- Completed results retry in the background after a storage failure. Recent
  Results shows the pending saves. Hosting pauses if all 64 pending slots fill.
- The operator now exposes the documented LTAR-native Battle, Hide & Seek, Hunt the
  Prey, Kings, Own the Zone, Respawn, and Survival variants already present in
  the host engine.
- The TV/HDMI view shows objectives, Hide & Seek/Hunt role-cycle timing, and
  zone/respawn status during play instead of presenting debrief-only hit totals
  as if they were live telemetry.
- Enhanced-hosted Respawn now sets the neutralize and supply-zone revive flags
  that the standard hosted packet already used. Kings is deliberately kept on
  standard hosting so its original IFF/King behavior is preserved.

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
9. Run Hide & Seek for at least four minutes and verify the taggers switch roles
   every 60 seconds while the TV role timer stays aligned.
10. Run Kings and verify the intended King/IFF behavior on real LTARs.
11. Run Own the Zone and Respawn with the relay positioned as the zone emitter;
   verify Zone Time / respawn behavior on the taggers and final debrief. For Respawn, record a known number of hits from the same attacker across two lives. Compare the taggers, TV and saved results with a single-life control before changing the scoring rule.

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
