# LTAR arena server code audit

**22 November 2026 · Raspberry Pi production review · Improve the current server in place.**

## A. Overall assessment

The existing server has a workable boundary between HTTP, game logic and the infrared relay. Keep it. The highest-impact failure I reproduced was a two-player lobby that could never start after one tagger missed its assignment. I also reproduced a lost relay message at connection startup and a game that had all its reports yet transmitted rank zero. All three can be corrected at identifiable points in the current code. A new server would still need to reproduce the existing hardware behavior.

**Scope and baseline.** I reviewed the current working tree on `main`, based on `d8b0b913`, including the local edits and untracked source and tests. The web project identifies itself as `2.0.0-rc.2`. “V1” here means this working system. The solution includes the shared library, Pi web host, .NET Framework Windows host, Windows LAN updater, and two .NET test projects. The review also covered the XML rules, bundled browser apps, firmware sketch and RMT library, service units, hotspot, firewall, update, backup and rollback scripts, CI, and the existing operational notes. Credentials and generated release contents are outside the source findings. [Solution](LazerTagServer.sln) · [Release identity and GC settings](LazerTagWebServer/LazerTagWebServer.csproj#L3) · [Pi platform and directories](deployment/PI_SETUP.md#L3)

### What happens during a match

| Stage | Actual data and ownership |
|---|---|
| Setup | The phone authenticates with a PIN, then sends a bearer-authorized HTTP start request. The controller validates the mode and ranges, resolves Auto teams and caps each team at eight. The service releases its standby connection, opens the hosting connection and starts the game engine. [Controller](LazerTagWebServer/Controllers/GameController.cs#L139) · [StartHostingAsync](LazerTagWebServer/Services/HostGunService.cs#L250) |
| Registration | The Pi sends ASCII commands over one TCP connection. The ESP32 converts each signature into timed IR. Taggers join with a game ID and tagger ID. The Pi assigns an address, waits for the tagger's acknowledgment and releases enhanced taggers. Relay `OK` and tagger assignment acknowledgments signify different events. [Relay dispatch](esp32_relay/sketches/RelayESP32/RelayESP32.ino#L736) · [Host registration](LazerTagHostLibrary/HostGun.cs#L699) |
| Countdown and play | A 100 ms engine tick services deadlines. Countdown transmits about once a second. Zone and Respawn transmit a beacon about every 500 ms. Taggers enforce HP, ammunition, shields, firing behavior and eliminations locally. Individual shots and beacons observed at the relay are diagnostic. The TV can display elapsed play, role changes and the roster. [Host tick](LazerTagHostLibrary/HostGun.cs#L2169) · [Received shots](LazerTagHostLibrary/LazerTagProtocol.cs#L466) · [TV playing view](LazerTagWebServer/wwwroot/tv.html#L516) |
| Debrief and results | The Pi rotates directed requests every six seconds. Each tagger reports its own summary and indicated team hit arrays. Validated reports populate both directions of the hit matrix. The ordinary completion path calculates scores and competition ranks, broadcasts them back to taggers, and queues a schema-v1 history record. The public TV and authenticated console receive snapshots. [Report parser](LazerTagHostLibrary/HostGun.cs#L890) · [Completion and rank reports](LazerTagHostLibrary/HostGun.cs#L2230) · [Archive capture](LazerTagWebServer/Services/HostGunService.cs#L652) |
| Recovery | With the Pi process alive, a fresh TCP session can reattach to the same in-memory match. A Pi process restart loses the active match. Saved player settings and completed history survive in the data directory. An offline relay and a dead web process are separate health conditions. [Reconnect](LazerTagWebServer/Services/HostGunService.cs#L144) · [Persistence](LazerTagWebServer/Services/GameHistoryStore.cs#L40) · [Health endpoints](LazerTagWebServer/Program.cs#L117) |

**Strengths worth retaining.** Packets go into the write queue as complete units. The engine checks game IDs, ignores self-echo and duplicate detailed reports, validates lengths before changing score arrays, tolerates known noncanonical tag counts and distinguishes standard and enhanced addresses. History uses a unique temporary file, a flush and an atomic move. The installer separates immutable releases from writable data. The web host already uses workstation GC, static browser assets and client-side TV timers. Per-player arrays are bounded by the 24-address protocol. These are economical choices for the Pi. [Packet transmission](LazerTagHostLibrary/LazerTagProtocol.cs#L55) · [Detailed reports](LazerTagHostLibrary/HostGun.cs#L970) · [History writes](LazerTagWebServer/Services/GameHistoryStore.cs#L40) · [Service confinement](deployment/lazertag.service#L25)

The arena network is a meaningful trust boundary. The default hotspot isolates Wi-Fi clients and the Pi firewall filters the arena interface. The web endpoint is plain HTTP, operator commands carry bearer tokens and the hub accepts a token in its connection query. Access to the upstream management LAN and any reverse-proxy request logs affects credential exposure. The audit used no venue credentials. The production authorization filter, per-IP login limit, constrained input ranges and CSV cell escaping are existing protections. [Hotspot isolation](deployment/configure-hotspot.sh#L165) · [Firewall](deployment/lazertag-firewall.nft#L5) · [HTTP and limits](LazerTagWebServer/Program.cs#L34) · [Bearer validation](LazerTagWebServer/Security/OperatorAuthorizeAttribute.cs#L9) · [CSV export](LazerTagWebServer/Controllers/HistoryController.cs#L89)

### Verification performed

| Workstation check | Result |
|---|---|
| Release rebuild of the solution with build-time analyzers enabled, .NET SDK 8.0.425 | Passed with zero warnings and zero errors |
| Full Release .NET suite | 55 web, library and relay tests and 12 updater tests passed. Zero failures or skips |
| Node operator-console tests and JavaScript syntax check | 3 tests passed. Syntax check passed |
| ESP32 Python source invariants, Bash syntax per deployment script, and hotspot regression script | Passed. The Arduino CLI and board toolchain were unavailable locally. CI defines a separate compile against ESP32 core 3.3.7. [Firmware CI](.github/workflows/ci.yml#L98) |
| Self-contained Linux ARM64 publish | Passed. The published apphost is ELF64, AArch64 machine 183 |
| Additional temporary probes | Reproduced B1, B2 and B3 against the compiled V1 library using normal join, debrief and loopback TCP traffic. A fourth probe ran the Release web assemblies with a missing catalog on loopback and reproduced C2. Probe files were outside the repository |

The Pi target documented here is Pi 3B/3B+, Raspberry Pi OS Lite 64-bit, Trixie/Debian 13. The installer checks architecture and OS. No live arena session, RF/IR capture, flash, on-device benchmark, package-advisory scan or power-cut test was performed in this audit. CPU, RSS, browser latency and radio loss rates are unmeasured. The compatibility and measurement plan identifies where physical evidence is needed. [Installer checks](deployment/install.sh#L99) · [V2 development plan](V2_DEVELOPMENT_PLAN.md)

## B. Critical issues

The table in section E ranks every finding. **Reproduced** means observed in an isolated run of the compiled code. **Confirmed** means the cited control flow establishes the behavior. **Risk** identifies an exposure whose arena frequency or impact needs measurement. **Optional** identifies an improvement.

### B1. A failed join can make a valid lobby impossible to start — Critical · reproduced

`ProcessRequestJoinGame` uses the current collection count to index the plan. `CheckAssignPlayerFailed` can then remove an earlier pending entry. `ApplyGuidedNames` renames surviving players by their new list positions, while individual overrides already attached can stay with them. In a plan for Teams 1 and 2, tagger A joined first and missed its ACK. B joined and confirmed for Team 2. After A timed out, replacement C also got Team 2. The two physical taggers filled both planned seats, and the countdown correctly rejected the missing Team 1. It takes a new hosting session to recover through the phone. [HostGun.ProcessRequestJoinGame, lines 739–760](LazerTagHostLibrary/HostGun.cs#L739) · [CheckAssignPlayerFailed, lines 2374–2392](LazerTagHostLibrary/HostGun.cs#L2374) · [HostGunService.ApplyGuidedNames, lines 969–995](LazerTagWebServer/Services/HostGunService.cs#L969) · [StartCountdownAsync, lines 493–517](LazerTagWebServer/Services/HostGunService.cs#L493)

Bind a physical tagger to a stable planned seat on its first join. Free only that seat on removal, and derive the broadcast overrides, name, join hint and team preference from it. Preserve wire addresses and the current confirmed-rejoin behavior. Individual settings reach a tagger in the pre-join broadcast, so also test two taggers powered close together with different overrides. Verify through the HTTP-to-relay fixture with the sequence above, distinct names and HP, and an extra join after capacity. Assert both teams, settings, names, history ownership and successful countdown.

### B2. Force-finish can discard a complete result and send rank zero to taggers — High · reproduced

Two otherwise identical, fully reported games produced different results. The ordinary route gave the tested player three effective hits, score 3, rank 1 and a `0x32` rank byte of 1. Forcing completion after the last report but before the next processing tick kept the three hits, left score and rank at zero and emitted a rank byte of 0. Both paths reached GameOver. The TV and archive deliberately flag forced results as incomplete. The GameOver IR sender has no matching guard. [HostGun.EndGameCore, lines 1627–1634, and NextCore, lines 1679–1684](LazerTagHostLibrary/HostGun.cs#L1627) · [UpdateCore, lines 2246–2291](LazerTagHostLibrary/HostGun.cs#L2246) · [HostGunService.GetGameState, lines 843–847](LazerTagWebServer/Services/HostGunService.cs#L843) · [Existing history contract test, lines 55–84](LazerTagWebServer.Tests/GameHistoryTests.cs#L55)

Make the completion decision under the game lock. If every report is present, use the ordinary once-only scorer. If reports are missing, retain the incompleteness flag and available hit counts and suppress uncertified rank transmissions. Keep existing history readable. Verify both orderings of “last report” and “finish,” a missing player, repeat finish and a tied game. Compare history, TV and actual outgoing rank packets.

### B3. The relay handshake consumes the first post-handshake message — High · reproduced

`PerformHandshake` reads up to 128 bytes and returns as soon as it sees `READY 1`. The worker then starts with an empty line buffer. In a loopback run, a valid `RCV` in the same write as `READY 1` disappeared. A second `RCV` sent later arrived. A partial next line is vulnerable to the same handoff. The firmware starts forwarding IR as soon as it marks the session ready. [LazerTagSerial.PerformHandshake, lines 340–394](LazerTagHostLibrary/LazerTagSerial.cs#L340) · [TcpReadThreadProc, lines 677–736](LazerTagHostLibrary/LazerTagSerial.cs#L677) · [Firmware handleHello and processSignature](esp32_relay/sketches/RelayESP32/RelayESP32.ino#L708)

Use one reader and one incremental framer across handshake and normal reception. Reset its state per connection and deliver retained complete lines and partial bytes exactly once. Test a split READY, READY plus two lines, a partial following line, and reconnect while a packet is incomplete. Keep the relay's existing bytes and CRLF framing.

## C. Recommended improvements

### C1. A repeated End command can escalate into forced debrief — High · confirmed

The phone confirms the action against its last snapshot. The same HTTP route means “end play” in Playing and “discard remaining reports” in Summary. The service gate serializes two requests from different consoles, allowing both to succeed with different meanings. A single-tab busy flag only covers that tab. [Phone endGame and runAction, lines 1333–1368](LazerTagWebServer/wwwroot/js/app.js#L1333) · [GameController.EndGame, lines 106–114](LazerTagWebServer/Controllers/GameController.cs#L106) · [HostGunService.EndGameAsync, lines 527–563](LazerTagWebServer/Services/HostGunService.cs#L527)

Give ordinary end an idempotent intent and force its own explicit confirmation tied to the active match and expected phase. Preserve the ordinary endpoint for older clients and version the force operation with the console. Verify two simultaneous phones, a retry after an HTTP timeout, a stale previous match and an intentional force. Only the explicit force should drop players.

### C2. A successful release check can conceal an unusable game catalog — High · reproduced

The Pi deployer checks the apphost, JSON manifests and a web directory. The Windows uploader also requires `GameTypes.xml`, while the game library loads it lazily. The production readiness endpoint always returns success when the host can answer, and the deployment script accepts HTTP success. A release can therefore activate over the direct deploy path and fail when the operator requests defaults or starts a game. [deploy.sh validation, lines 118–136](deployment/deploy.sh#L118) · [ReleaseArchive.CheckAndHash, lines 10–44](LazerTagUpdater/ReleaseArchive.cs#L10) · [GameTypes static initialization, lines 10–18](LazerTagHostLibrary/GameTypes.cs#L10) · [GameController.GetGameTypeDefaults, lines 30–56](LazerTagWebServer/Controllers/GameController.cs#L30) · [Program readiness and liveness, lines 118–128](LazerTagWebServer/Program.cs#L118)

In an isolated web process with the mode XML omitted, `/healthz` returned HTTP 200 and the default 2TMS mode's settings endpoint returned HTTP 400. Validate the catalog and required operator, TV and bundled script assets before activation. Readiness should verify local hosting prerequisites and report relay status independently so maintenance with the relay powered off still works. Exercise a valid publish, a missing or malformed catalog, a missing TV asset, an offline relay and automatic rollback to the prior release.

### C3. The root deployer validates archive links after extraction — High · confirmed validation defect

The uploader only accepts ordinary files and directories. The Pi's direct installation path checks member names, extracts as root, then computes symlink destinations by appending every target to the link's parent. That computation misinterprets absolute targets, and checking after extraction is too late to be the trust boundary. This matters for direct LAN/CLI deployments even when the operator normally uses the Windows uploader. [deploy.sh validate_archive, validate_extracted_links and extraction, lines 87–127](deployment/deploy.sh#L87) · [ReleaseArchive.CheckAndHash, lines 21–32](LazerTagUpdater/ReleaseArchive.cs#L21)

Use a root-owned private copy of the verified input and the same flat-bundle member-type policy at the Pi entry point before extraction. Stage with least privilege and promote validated ordinary files. Verify the real ARM64 bundle plus harmless fixtures representing links, special entries and invalid names in disposable Linux storage. The active release must stay selected on rejection.

### C4. The self-contained web runtime has reached end of support — High · confirmed

The web project targets .NET 8 and packages its own runtime. Updating OS packages alone does not service the copy inside the release. Microsoft's .NET 8 support ended on 10 November 2026. The current Windows host also consumes the shared library's .NET Framework 4.8 target. [Web project, lines 3–18](LazerTagWebServer/LazerTagWebServer.csproj#L3) · [Shared library targets, lines 3–15](LazerTagHostLibrary/LazerTagHostLibrary.csproj#L3) · [CI ARM64 publish, lines 55–65](.github/workflows/ci.yml#L55) · [Microsoft lifecycle](https://dotnet.microsoft.com/en-us/platform/support/policy)

Move the Pi release to a serviced .NET 10 LTS patch as an independent release candidate. Retain the desktop target, XML file, serial library and existing wire fixtures. Rebuild on CI, exercise actual Pi cold start, one standard and one enhanced game, and roll back by explicit release ID. Debian 13 Arm64 is listed in Microsoft's .NET 10 platform matrix. [Supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)

### C5. History can disappear after a short storage outage — High · confirmed failure path

Archive capture sets `ArchiveQueued`. The writer retries three times with 250 ms and 500 ms delays, then logs and drops its task. The successful enqueue flag still prevents cleanup from scheduling another write. A recoverable storage problem can therefore cost a completed result while the arena continues normally. Existing atomic file writes protect records that reach disk. [HostGunService.QueueCompletedGameArchive, lines 625–648](LazerTagWebServer/Services/HostGunService.cs#L625) · [PersistHistoryWithRetryAsync and ObserveHistoryWriteAsync, lines 730–796](LazerTagWebServer/Services/HostGunService.cs#L730) · [GameHistoryStore.SaveAsync, lines 40–87](LazerTagWebServer/Services/GameHistoryStore.cs#L40)

Track “captured” and “persisted” separately. Keep a bounded pending queue keyed by history GUID, retry with backoff while the process lives, and show failed/pending status to the operator. A power outage before durable storage becomes available remains a loss boundary. Verify a storage outage lasting longer than the existing retries, a later successful write, rapid cleanup and duplicate completion. Expect exactly one file.

### C6. Phone and TV can regress to an older snapshot and generate duplicate work — Medium · confirmed ordering gap, load unmeasured

Both browsers apply SignalR states immediately and poll state and telemetry every five seconds. A slow older HTTP response can overwrite a newer push and reset the freshness clock. With 10 connected clients, polling alone requests 240 HTTP responses per minute, in addition to pushes. During hosting the periodic broadcast is capped at one per second with clients, plus event-triggered pushes. Full snapshots sort the roster and rescan targets per player. The 100 ms game tick itself is not the broadcast rate. [Phone refreshSnapshots and applyState, lines 283–295 and 1494–1500](LazerTagWebServer/wwwroot/js/app.js#L283) · [TV refresh and applyState, lines 367–400 and 478–486](LazerTagWebServer/wwwroot/tv.html#L367) · [HostGunService.GetGameState, lines 833–910](LazerTagWebServer/Services/HostGunService.cs#L833) · [Periodic broadcast, lines 1214–1229](LazerTagWebServer/Services/HostGunService.cs#L1214)

Add a process epoch and increasing revision shared by HTTP and SignalR. Accept newer snapshots only, keep a heartbeat, and poll on stale push/initial load/reconnect. Coalesce roster changes and cache one immutable projection per revision. Verify reordered responses including an Idle/new-match transition, a restart with a new epoch, a permanently unavailable hub and a slow TV. Measure Pi traffic and allocation before tuning further.

### C7. Disconnects leave gaps between queued, transmitted and received data — Medium · confirmed code paths, physical effects unmeasured

The outbound queue has no capacity. An enqueue during disconnection starts reconnect and silently returns. The protocol handles relay `ERROR` at Debug level. A debrief release is marked sent immediately after enqueue. Partial inbound packet state also belongs to the protocol object, which outlives the TCP connection. The sketch retains its only client while the ESP32 TCP stack reports it connected. A Pi restart or asymmetric link loss deserves a bench test because recovery time depends on when the board releases that session. [LazerTagSerial constructor and Enqueue, lines 57–60 and 535–555](LazerTagHostLibrary/LazerTagSerial.cs#L535) · [LazerTagProtocol.ProcessSerialFragment and Serial_IoError, lines 189–200 and 267–270](LazerTagHostLibrary/LazerTagProtocol.cs#L189) · [Packet assembly, lines 360–425](LazerTagHostLibrary/LazerTagProtocol.cs#L360) · [HostGun.SendLtarDebriefReleaseIfReady, lines 838–846](LazerTagHostLibrary/HostGun.cs#L838) · [Relay sessionManagerTick, lines 452–481](esp32_relay/sketches/RelayESP32/RelayESP32.ino#L452)

Bound complete packets and partial frames, tag them by connection, reset receive assembly on loss, expire old beacons/countdowns, and surface rejected writes and relay errors. Preserve the FIFO attribution of per-signature responses and tagger-level acknowledgments. Reschedule only actions safe in the current phase. Verify disconnects around assignment, T−1, report request, release and ranking with the simulator, then repeat on one real relay. Check queue age and resident memory with a slow relay.

### C8. Expired or revoked operator sessions can keep receiving logs — Medium · confirmed authorization gap

HTTP controls check the session on every request. SignalR checks it when adding a connection to the operator group. Expiry and logout remove tokens, but neither removes an already-connected recipient from the log group. Public state and telemetry are intentionally available. [GameHub.OnConnectedAsync and OnDisconnectedAsync, lines 27–57](LazerTagWebServer/Hubs/GameHub.cs#L27) · [OperatorSessionService.IsValid and Revoke, lines 74–86](LazerTagWebServer/Security/OperatorSessionService.cs#L74) · [OperatorController.Logout, lines 37–42](LazerTagWebServer/Controllers/OperatorController.cs#L37) · [HostGunService.PushLogAsync, line 1226](LazerTagWebServer/Services/HostGunService.cs#L1226)

Tie private subscriptions to expiry and revocation, including multiple connections sharing one session. Keep the public scoreboard connected. Use two real hub clients and a fake clock to verify immediate loss of private logs after logout or expiry and continued public state delivery.

### C9. Wall-clock adjustments change game duration — Medium · confirmed dependency, field impact unmeasured

Countdown, gameplay, debrief scheduling and join retries compare UTC wall-clock deadlines. A Pi that corrects its clock after boot can advance or extend those deadlines. The TV uses server wall-clock timestamps to reconstruct elapsed play. [HostGun.ChangeState, lines 1441–1483](LazerTagHostLibrary/HostGun.cs#L1441) · [UpdateCore, lines 2167–2207](LazerTagHostLibrary/HostGun.cs#L2167) · [CheckAssignPlayerFailed, lines 2342–2369](LazerTagHostLibrary/HostGun.cs#L2342) · [TV serverOffset and applyState](LazerTagWebServer/wwwroot/tv.html#L472)

Use an injectable monotonic clock for intervals and publish remaining time at the snapshot revision. Keep UTC timestamps for history and human-readable logs. Verify forward and backward clock steps, a reconnect halfway through countdown, and the original on-air cadence with an injected clock and hardware trace.

### C10. Configured hotspot interface and firewall interface can diverge — Medium · confirmed on alternate configurations

Hotspot provisioning validates and uses `HOTSPOT_INTERFACE`. The installed firewall table names `wlan0` literally and has accept defaults outside the matching rules. A valid alternate interface can therefore lack the intended arena ingress and forwarding restrictions. The supplied default matches. [configure-hotspot.sh interface validation and hostapd output, lines 64–92 and 152–165](deployment/configure-hotspot.sh#L64) · [install.sh firewall install, line 227](deployment/install.sh#L227) · [Firewall input and forward chains, lines 5–19](deployment/lazertag-firewall.nft#L5) · [firewall-apply.sh, lines 12–24](deployment/firewall-apply.sh#L12)

Render the rules from the same validated interface setting and keep the current atomic nftables transaction. Verify default and alternate interface names in a disposable network namespace, including DNS, DHCP, web access, denied forwarding and the separate management path.

### C11. Several operator counters silently under-report failures — Medium · confirmed

Two counters match Debug log prefixes, but the service filters below Information before it raises the log event. A TCP refusal or failed handshake is logged by the retry path with different text from the connection-failure counter's prefix. A displayed zero can conceal a real state warning or connection failure. [HostGunService constructor and OnLibraryLog, lines 109–110 and 1192–1209](LazerTagWebServer/Services/HostGunService.cs#L1192) · [Log.Add, lines 18–33](LazerTagHostLibrary/Log.cs#L18) · [HostGun.CheckPacketAgainstExplicitState, lines 538–554](LazerTagHostLibrary/HostGun.cs#L538) · [LazerTagSerial.ConnectTcpWithRetry and ConnectTcpOnce](LazerTagHostLibrary/LazerTagSerial.cs#L225)

Count typed outcomes independently of log verbosity and distinguish attempts, timeouts, refusals and handshake errors. Keep the mirror-state warnings diagnostic until their policy is validated. Check the API counters in Release at Information with a failed handshake and a simulated unexpected packet. Verify successful reconnect increments once.

### C12. USB fallback is a library capability, unavailable through the stock Pi service — Medium · confirmed scope mismatch

The shared transport supports 115200 8N1 serial. The web host requires a host/port endpoint and the systemd unit uses private devices. The shipped ESP32 sketch uses USB Serial for diagnostics and parses bridge commands on TCP. The web project nevertheless describes serial as a Pi fallback. [LazerTagSerial.ConnectSerial, lines 195–219](LazerTagHostLibrary/LazerTagSerial.cs#L195) · [HostGunService.ValidateRelayAddress, lines 1308–1320](LazerTagWebServer/Services/HostGunService.cs#L1308) · [Service device policy, line 35](deployment/lazertag.service#L35) · [Project comment, lines 16–19](LazerTagWebServer/LazerTagWebServer.csproj#L16) · [Firmware setup and loop](esp32_relay/sketches/RelayESP32/RelayESP32.ino#L190)

Document the production TCP route and retain the shared serial implementation for compatible devices and desktop callers. If an existing serial bridge is in use, characterize it separately before proposing a narrowly scoped service profile. Verify the unchanged TCP deployment and capture a complete session on any claimed USB combination before advertising it.

### C13. Respawn scoring needs a physical rule check — Medium · suspected gameplay discrepancy

Respawn permits repeated lives, but the shared effective-hit rule caps an attacker's contribution against each opponent at that opponent's starting HP for the whole match. This protects against repeated frames in raw LTAR reports. The source alone cannot distinguish an inflated raw count from valid tags across several lives. [HostGun.CountEffectiveHitsScoredCore, lines 295–315](LazerTagHostLibrary/HostGun.cs#L295) · [CalculateScores dispatch, lines 1088–1100](LazerTagHostLibrary/HostGun.cs#L1088) · [Respawn metadata](LazerTagWebServer/Models/GameModeCatalog.cs#L74)

Retain today's rule as the compatibility baseline. Observe a controlled two-life Respawn and its raw reports alongside a control match with no respawn. Record manual hit counts and equipment model before deciding whether a mode-specific policy is justified. Verify any approved change against recorded frames for both modes.

### C14. A failed start publishes a roster for a match that never opened — Medium · confirmed

The service stores its planned rows and rewrites the saved roster before the hosting connection succeeds. Its relay-failure return disposes the attempted connection but leaves the planned names installed. The Idle snapshot then instructs the operator to turn on the first player's tagger even though the host rejected the match. [HostGunService.StartHostingAsync, lines 274–305 and 346–352](LazerTagWebServer/Services/HostGunService.cs#L274) · [GetGameState, lines 817–830](LazerTagWebServer/Services/HostGunService.cs#L817)

Build a tentative plan locally and publish it only when hosting succeeds. If preserving failed attempts as editable drafts is desirable, give that saved draft an explicit identity independent of the active match. Verify a successful prior roster, a different attempted roster with an unavailable relay, an Idle state with no join hint, and a later successful retry.

### C15. Update preflight leaves an admission race before restart — High · confirmed gap, timing-dependent impact

The Windows updater checks the game state before handing installation to systemd. The Pi deployer holds a lock against other deployments, while the web start action uses a different in-process gate. A phone can open a lobby between the last check and the service restart. The documented PowerShell path invokes the Pi deployer directly. The deployment can therefore discard a newly started in-memory match. [PiUpdate.DeployAsync and CheckBetweenGames, lines 139–156 and 182–194](LazerTagUpdater/PiUpdate.cs#L139) · [deploy.sh deploy lock and activation, lines 65–66 and 162–176](deployment/deploy.sh#L162) · [PowerShell deployment, lines 69–73](deployment/deploy-from-vscode.ps1#L69) · [HostGunService.StartHostingAsync, lines 250–258](LazerTagWebServer/Services/HostGunService.cs#L250)

Have the Pi acquire a shared maintenance/admission hold before its final state check, reject new starts during that hold and release it on cancellation or failure. Make every normal deployment entry point use it, allow explicit offline installation, and keep recovery from an unstartable service available. Verify racing Start against activation and redeploying the same release. Exactly one of match admission and update admission should succeed.

## D. Low-priority improvements

### D1. Reduce the cost of maintaining two descriptions of game state — Low · optional

`HostingState` drives the actual tick and packet switch. The adapter and policy machine also track states, but an unexpected packet there produces a Debug message and the live handler continues. Edits to either model can silently diverge. The Windows host shares the same library. [HostGun.CheckPacketAgainstExplicitState and ProcessPacketCore, lines 538–570](LazerTagHostLibrary/HostGun.cs#L538) · [HostGun.UpdateCore, lines 2096–2109](LazerTagHostLibrary/HostGun.cs#L2096) · [HostStateMachine.OnTick, lines 126–136](LazerTagHostLibrary/HostStateMachine.cs#L126)

First write a transition table from production traces, label the mirror as diagnostic and compare its outputs. Consolidate one transition family at a time after the characterizations agree. Verify both front ends, particularly joining, early end and debrief. Fold a proven-unused wrapper into this work only after checking its callers.

### D2. CI syntax-checks only the first expanded shell script — Low · confirmed

The CI command passes a shell glob to a single `bash -n` invocation. Bash treats subsequent filenames as arguments to the first script. This local review syntax-checked each script individually. [CI shell check and hotspot fixture, lines 33–37](.github/workflows/ci.yml#L33)

Iterate over each deployment script, the kiosk `xinitrc` and test scripts. Verify a disposable invalid second script makes the job fail while the real set passes.

### D3. History listings deserialize full match files while holding the writer gate — Low · optional, Pi impact unmeasured

A request for recent summaries enumerates and sorts the history directory, then opens and deserializes full records under the same semaphore that protects new writes. Retention defaults to 1,000 files and may reach 10,000. Several phones opening history on an SD card could delay the next archive. [GameHistoryStore.GetRecentAsync, lines 91–133](LazerTagWebServer/Services/GameHistoryStore.cs#L91) · [SaveAsync, lines 48–76](LazerTagWebServer/Services/GameHistoryStore.cs#L48)

Measure first with the normal retention count on the venue card. If history opens contend with archival, cache a bounded list of summaries after successful writes and rebuild it from the existing immutable files at startup or on a cache miss. Compare list latency and archive wait time under concurrent reads and verify old records and pruning still work.

## E. Implementation roadmap

Benefit describes the outcome. Breakage risk describes the proposed change before its verification gates. Difficulty is relative to a maintainer familiar with this repository.

| Order | Finding | Priority | Expected benefit | Breakage risk | Difficulty |
|---:|---|---|---|---|---|
| 1 | B1 · Stable planned seats | Critical | Prevent blocked starts and misattributed players | Medium | Medium |
| 2 | B2 · Once-only finalization and trustworthy IR ranks | High | Preserve fully reported matches | Medium | Medium |
| 3 | C1 · Explicit end/force intent | High | Prevent accidental loss of debrief | Medium | Medium |
| 4 | B3 · Continuous relay framer | High | Preserve first frame after connection | Low | Low |
| 5 | C2 · Catalog and asset readiness | High | Reject unusable releases before activation | Low | Low |
| 6 | C3 · Pi archive validation | High | Enforce the same release boundary on all entry points | Medium | Medium |
| 7 | C4 · Serviced runtime | High | Restore runtime servicing on the Pi | Medium | Medium |
| 8 | C5 · Retriable archive status | High | Recover finished games after temporary storage faults | Medium | Medium |
| 9 | C15 · Update admission hold | High | Protect a match starting during an update | Medium | Medium |
| 10 | C6 · Ordered snapshots and adaptive polling | Medium | Keep displays current and reduce redundant Pi work | Medium | Medium |
| 11 | C7 · Transport bounds and delivery state | Medium | Bound memory and improve recovery diagnostics | High | High |
| 12 | C8 · Revoke private log subscriptions | Medium | Enforce logout and expiry on persistent connections | Low | Medium |
| 13 | C9 · Monotonic intervals | Medium | Stable rounds across time corrections | Medium | Medium |
| 14 | C10 · Configured interface firewall | Medium | Preserve isolation on supported interface choices | Medium | Low |
| 15 | C11 · Typed telemetry | Medium | Show meaningful failure counts | Low | Low |
| 16 | C12 · Accurate USB support boundary | Medium | Prevent dependence on an unavailable recovery path | Low | Low |
| 17 | C13 · Characterize Respawn | Medium | Establish scoring behavior across repeated lives | Low for observation | Medium |
| 18 | C14 · Tentative roster on failed start | Medium | Keep Idle and saved setup consistent | Low | Low |
| 19 | D1 · State-model simplification | Low | Reduce change and review effort | High if promoted wholesale | Medium |
| 20 | D2 · Per-script CI syntax check | Low | Catch deployment regressions early | Low | Low |
| 21 | D3 · Measure history listing | Low | Reduce SD-card reads and archive waits if demonstrated | Low | Low |

Start on an isolated branch with fixtures for B1–B3 and C1. Package the first corrections separately from the runtime migration. Use static readiness tests and a known-good rollback release before testing on the Pi between matches. Baseline the current Pi before performance changes. The decision document compares the alternatives, and the development plan gives the on-hardware acceptance gates. [V1 versus V2](V1_VS_V2_COMPARISON.md) · [Development and verification plan](V2_DEVELOPMENT_PLAN.md)

