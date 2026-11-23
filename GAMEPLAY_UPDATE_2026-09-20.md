# LTAR Gameplay Update — 2.0.0-rc.2

This update expands the arena around what LTAR taggers can enforce locally. It
does not invent a live scoring feed: individual tag/score details remain
authoritative only after the post-game debrief.

## Newly exposed operator modes

- Battle: Custom solo, 2 Teams, 3 Teams
- Role Games: Hide & Seek, Hunt the Prey
- Kings: 2 Teams, 3 Teams
- Zone: Own the Zone solo, 2 Teams, 3 Teams
- Respawn: solo, 2 Teams, 3 Teams
- Survival: solo, 2 Teams, 3 Teams

Tag Master and One-on-One variants remain hidden until their assignment and
role behavior is verified with physical taggers.

## Operator safeguards

- Hide & Seek and Hunt the Prey accept only 2–98 minute games in 2-minute steps.
- Team modes require planned coverage of every team and still honor the eight
  players-per-team protocol limit.
- Kings uses standard LTAR hosting. Enhanced-only options are disabled/cleared
  because the original hosted game packet carries the King/IFF behavior.
- Medic Mode is normalized off because LTAR does not support hosted Medic Mode.

## TV / HDMI behavior

The Wi-Fi TV page and Raspberry Pi HDMI kiosk continue to use the same
`tv.html` view. During play the display now focuses on information the Pi really
knows: game clock, roster, objective, role-cycle timer, and zone/respawn state.
It no longer labels debrief-only tag totals as live scores. Final scores, hits,
and ranks appear after debrief as before.

Hide & Seek displays the current seeking/hiding team and the next 60-second
switch. Hunt the Prey displays the next 60-second hunt-direction switch without
claiming a live target map the host cannot observe. Zone and Respawn games show
that the relay IR zone is active. Kings keeps King identities off the public TV.

## Protocol correction

Enhanced Respawn announcements now set both neutralize-on-tag and supply-zone
unneutralize flags, matching the semantics already used by the standard hosted
Respawn packet. This matters because normal web-host configurations often use
the enhanced LTAR announcement when ammo, sleep, IFF, hit-register, auto-reload,
or full-auto controls are active.

## Verification before arena use

Run `dotnet build -c Release` and `dotnet test -c Release` on a machine with the
.NET 8 SDK, then physically test at least Hide & Seek, Kings, Own the Zone, and
Respawn with real LTAR taggers before considering rc.2 hardware-verified.
