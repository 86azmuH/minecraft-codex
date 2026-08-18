# Build Log

## 2026-08-18 — Connection-gated Codex mode and HUD

- **Completed:** Added client-only `/codex on`, `/codex off`, expanded `/codex status`, a generation-safe disabled/connecting/enabled state machine, persistent session ownership with disconnect reset, silent join/disconnect/level-change resets, and a Minecraft-style `[ CODEX ]` badge beside the hotbar.
- **Why it matters:** Codex mode cannot appear active until the secure companion handshake succeeds, and world or server transitions cannot leave a stale mode enabled before chat interception exists.
- **Verified by:** Clean Gradle build; state and HUD-layout tests including stale cross-generation completion; exact Fabric 26.1.2 API signature inspection; the real Java-to-companion integration test; and an independent review followed by fixes for persistent-session ownership and stale feedback.
- **Relevant areas:** `mod/src/client/java/dev/minecraftcodex/client`, `mod/src/test/java/dev/minecraftcodex/client`.
- **Still open:** In-game visual and lifecycle smoke testing, private chat interception, response rendering, and multiplayer disclosure tests.

## 2026-08-18 — Fabric transport/status checkpoint

- **Completed:** Added the Minecraft 26.1.2 client-only Fabric project, private `/codex status`, asynchronous companion startup, executable-verified Windows named-pipe bootstrap, authenticated loopback WebSocket snapshot request, protocol fixtures, and a real companion integration test.
- **Why it matters:** The Java mod can cross the existing C# security and transport boundary without blocking Minecraft's game thread, before chat interception introduces multiplayer disclosure risk.
- **Verified by:** Successful Gradle build and unit suite, an opt-in test against the actual companion that validated pipe identity, `server.hello`, and `task.snapshot`, and an in-game Prism Launcher test that displayed `Companion connected (protocol 1)`.
- **Relevant areas:** `mod/`, `docs/project/components/fabric-mod.md`.
- **Still open:** Codex mode, chat interception, world-transition reset, response rendering, and release packaging.

## 2026-08-17 — Singleton identity and idle lifecycle

- **Completed:** Added a per-user companion lease, named-pipe server executable verification, strict loopback endpoint validation, broker startup readiness, and automatic idle shutdown gated by authenticated clients and active tasks.
- **Why it matters:** Duplicate companions no longer compete, ordinary same-user pipe imposters are rejected before receiving a capability request, and background work survives disconnects without leaving an unused companion running forever.
- **Verified by:** Zero-warning automated build and production-transport suite, real broker-collision cleanup, idle-state tests, live duplicate-process check, and the official Codex response `STEP3_FINAL_OK` followed by fresh-process snapshot recovery.
- **Relevant areas:** `CompanionInstanceLease`, `NamedPipeServerIdentity`, `BootstrapBroker`, `IdleShutdownCoordinator`, `CompanionHost`, `TaskRegistry`, standalone client.
- **Still open:** Authenticode/final-file identity, explicit runtime-directory ACL verification, and startup/lifetime token separation.

## 2026-08-17 — Production bridge transport test checkpoint

- **Completed:** Extracted the production server into a testable host and added real loopback Kestrel, named-pipe, and WebSocket tests for authentication, token replay and expiry, invalid traffic, idempotency, active reconnect, cancellation, and clean shutdown.
- **Why it matters:** The bridge's security and lifecycle behavior is now tested through the same network code that production uses, with only Codex execution replaced by deterministic fake adapters.
- **Verified by:** Zero-warning build, complete automated suite, active-work shutdown and pipe-reuse test, plus a live official Codex response `STEP2_HOST_OK` and fresh-client snapshot.
- **Relevant areas:** `CompanionHost`, `TaskRegistry`, `BridgeServerTests`, `scripts/build.ps1`, `scripts/test-companion.ps1`.
- **Still open:** Genuine broker identity, duplicate-companion startup, idle shutdown, and real long-running Codex process cancellation.

## 2026-08-17 — Fresh-process companion reconnection

- **Completed:** Added a deterministic per-user named-pipe broker that mints bounded, one-minute, one-use WebSocket capabilities.
- **Why it matters:** A newly started client can find a surviving companion without retaining a bearer secret or the original launcher output.
- **Verified by:** Build with zero warnings, broker security tests, stalled-client recovery test, and a live Codex run followed by a distinct second client process recovering the same completed task and ordered events.
- **Relevant areas:** `BootstrapBroker`, `BootstrapIdentity`, `CapabilityStore`, standalone client, `scripts/test-companion.ps1`.
- **Still open:** Recovery after the companion itself exits is not implemented.

## 2026-08-17 — Standalone read-only companion bridge

- **Completed:** Added authenticated loopback WebSocket transport, normalized Codex events, task state, idempotent request handling, cancellation, snapshots, and a standalone client.
- **Why it matters:** The Codex integration can be developed and tested before Minecraft is involved.
- **Verified by:** Automated policy/lifecycle tests and the live response `COMPANION_BRIDGE_OK` through the official authenticated Codex CLI.
- **Relevant areas:** `companion/src`, `companion/tools`, `scripts/build.ps1`, `scripts/test-companion.ps1`.
- **Still open:** Workspace-write remains disabled pending stronger final-path validation.
## 2026-08-18 — Private single-task chat slice

- **Completed:** Added pre-packet ordinary-chat interception while Codex mode is enabled, private `You:` and `Codex:` messages, an immediate `Codex: Thinking…` acknowledgement, concise-response guidance, response chunking, busy-state blocking, task cancellation on disable, and timeout-safe message delivery.
- **Why it matters:** A player can now hold the first private Codex exchange from normal Minecraft chat without changing slash-command behavior.
- **Verified by:** Clean Gradle unit/build suite and an opt-in real companion integration test that submitted a task and received its `message.completed` result. Fabric/Minecraft bytecode inspection confirmed `ALLOW_CHAT=false` cancels before chat signing and packet construction.
- **Still open:** Prism visual testing and real multiplayer non-disclosure verification; the companion still returns complete messages rather than token deltas.
## 2026-08-18 — Preserve Unicode in Codex responses

- **Completed:** Explicitly configured the Codex CLI subprocess input, output, and error streams as strict UTF-8.
- **Why it matters:** Curly apostrophes and other Unicode punctuation now survive the Windows companion boundary instead of appearing as mojibake such as `ΓÇÖ` in Minecraft chat.
- **Verified by:** Companion regression coverage for UTF-8 stream configuration and punctuation round-tripping, plus the full companion test suite and release build.
- **Still open:** Replace the Prism companion executable and visually confirm Unicode punctuation in Minecraft.
