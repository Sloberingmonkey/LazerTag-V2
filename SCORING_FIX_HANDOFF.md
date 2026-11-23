# Handoff: inflated hit counts (V1, local only)

Read `CLAUDE_HANDOFF.md` first. Its rules still apply: work in V1 (`C:\Server`), keep the
fix local, don't deploy to the Pi or switch releases, and leave the Wi-Fi adapter, hotspot
and ESP32 firmware alone. The working tree has many uncommitted changes. Preserve them, and
don't commit, stash, reset or check out anything unless the user asks.

## Problem
The user reports that the number of hits scored is sometimes exaggerated.

## Cause
In the detailed debrief, each tagger sends one count byte per player who hit it, through
`ProcessTeamTagReport` in `LazerTagHostLibrary/HostGun.cs`. Field hardware sometimes sends
the non-canonical packed-decimal byte `0x8A`. For compatibility the server still accepts
that byte and decodes it with nibble arithmetic, so it becomes 90 hits (see
`TryDecodeTeamTagCount` and `DEBRIEF_HOTFIX.md`). Scoring then capped this at the target's
HP, usually 10, so one or two real hits were scored as 10. One on One, Kings and Hunt the
Tag Master read the raw counts with no cap at all.

## Fix applied (uncommitted, in the working tree)
- `LazerTagHostLibrary/Player.cs`: new `TaggedByCountSuspect` bool array. It is true when
  that tagger's count against this player arrived as a non-canonical byte.
- `LazerTagHostLibrary/HostGun.cs`:
  - `ProcessTeamTagReport` sets the suspect flag, and the existing warning now says the
    count will be limited.
  - The new `EffectiveHitsOn` / `EffectiveHitsOnCore` helpers calculate hits per shooter and
    target. Each count is capped at the target's HP, as before. A suspect count is further
    limited to the target's own `TagsTaken` (from the tag summary, which arrives as
    canonical BCD), minus the hits already credited by well-formed counts. Several suspect
    counts against one target share whatever remains, in player-number order.
    Well-formed counts score exactly as before.
  - `CountEffectiveHitsScoredCore`, `CalculateScoresKings`, `CalculateScoresOneOnOne` and
    `CalculateScoresHuntTheTagMasterTwoTeams` all use the helper now. As a side effect,
    those three modes now apply the same per-target HP cap as the other modes.
- `LazerTagWebServer.Tests/DebriefRegressionTests.cs`:
  - Two existing tests had locked in the inflated result (expected 10 hits when the target
    reported 0). They now give the target 3 hits taken and expect 3. In the survival test,
    the shooter's score is 23 and the team score is `(1 << 10) + 23 * 4`.
  - New tests: `Noncanonical_count_is_limited_to_the_targets_reported_hits_taken` (two-team
    and One on One), `Noncanonical_count_only_gets_hits_the_target_has_not_already_attributed`
    (2 + 0x8A, 0x8A + 2, 0x8A + 0x8A against 5 hits taken) and
    `Canonical_counts_are_scored_as_reported`.
  - The `TagSummary` helper has a new optional `tagsTaken` parameter.

## Verification so far
NuGet wasn't reachable from the previous session, so the full suite hasn't run.
`LazerTagHostLibrary` plus `DebriefRegressionTests` and `ProtocolRegressionTests` were
compiled against SDK 10.0.115 with a stand-in serial port and a minimal xunit runner.
32 of 32 tests passed.

## Next steps
1. Run the real suite from `C:\Server`:
   `dotnet test .\LazerTagServer.sln -c Release` and
   `node --test .\LazerTagWebServer.Tests\operator-console.test.cjs`.
   Fix any failures without loosening the cap on suspect counts.
2. Review `git diff` for `HostGun.cs`, `Player.cs` and `DebriefRegressionTests.cs`.
3. Optional, read only: on the Pi, look for the log line "Accepted non-canonical tag count"
   around a bad game to confirm the cause. If scores were inflated with no such warning,
   there is a second cause. Ask the user for the game mode and the expected versus shown
   hits.
4. Report back. The user decides about committing and deploying.
