# Decision Record

## D-010 — Codex mode is connection-gated and visibly local

- **Date:** 2026-08-18
- **Status:** Active
- **Context:** Chat interception must never appear active when the companion is unavailable, and mode state must not survive world or server transitions.
- **Decision:** `/codex on` enables only after a successful companion hello and snapshot, then retains the authenticated WebSocket for the enabled-mode lifetime. Enabled state is shown by a bordered amber `[ CODEX ]` badge beside the hotbar. Companion-session loss, join, disconnect, and client-level replacement disable the mode silently.
- **Reason:** The player gets an unambiguous local safety signal, while stale asynchronous results and lifecycle changes fail closed to disabled.
- **Alternatives considered:** Enabling immediately with a connecting indicator, showing the indicator only in chat, and posting an automatic chat message on every lifecycle reset.
- **Consequences:** Connection failure leaves the mode disabled; duplicate lifecycle callbacks must remain idempotent; the badge needs visual checks across GUI scales.
- **Revisit when:** Chat interception or a dedicated workspace changes how active mode should be communicated.

## D-009 — Prove secure Fabric transport before intercepting chat

- **Date:** 2026-08-18
- **Status:** Active
- **Context:** Java-to-Windows bootstrap, executable identity, and asynchronous Minecraft integration were the highest-risk unknowns, while chat interception could accidentally expose prompts to multiplayer servers.
- **Decision:** Implement a private `/codex status` transport checkpoint before Codex mode or ordinary-chat interception. Use JNA only for the required Windows handle/process identity calls and Java's standard HTTP WebSocket client for loopback transport.
- **Reason:** This isolates and verifies the security boundary without creating any new path that can transmit a prompt to a Minecraft server.
- **Alternatives considered:** Implementing chat interception and transport together, or temporarily trusting a named-pipe name without verifying its server process.
- **Consequences:** The first mod checkpoint is useful for diagnostics but does not yet send prompts. JNA is nested into the client mod, and packaging must still supply the expected companion executable.
- **Revisit when:** A packaged companion has a stable installed identity or a smaller Windows-native bridge replaces JNA.

## D-001 — Minecraft chat is the first user interface

- **Date:** 2026-08-17
- **Status:** Active
- **Context:** The original roadmap prioritized a dedicated workspace screen.
- **Decision:** Build private normal-chat interaction first, command-driven chat selection second, and the dedicated project workspace later.
- **Reason:** The chat experience is the smallest useful Minecraft interface and can prove multiplayer privacy early.
- **Alternatives considered:** Building the full workspace first.
- **Consequences:** The initial mod stays narrow; project and chat management arrive later.
- **Revisit when:** The chat-first vertical slice is stable.

## D-002 — Codex runs behind a separate Windows companion

- **Date:** 2026-08-17
- **Status:** Active
- **Context:** Codex work must survive Minecraft UI closure and enforce permissions outside the game client.
- **Decision:** A C# companion owns Codex processes, task state, security checks, and local transport.
- **Reason:** Minecraft's render thread and process lifetime should not own long-running Codex work.
- **Alternatives considered:** Launching and managing Codex directly from the Fabric mod.
- **Consequences:** Distribution must include or provision the companion and its runtime.
- **Revisit when:** Cross-platform support is considered.

## D-003 — Reconnection uses a same-user pipe plus one-use WebSocket capabilities

- **Date:** 2026-08-17
- **Status:** Active
- **Context:** A restarted client needs to find a surviving companion without storing a reusable bearer secret.
- **Decision:** Use a deterministic per-user Windows named pipe to return the current loopback endpoint and a fresh, one-minute, one-use capability.
- **Reason:** The pipe provides a secure local bootstrap channel while WebSocket remains convenient for streamed events.
- **Alternatives considered:** Plaintext state files, reusable stdout tokens, and a fixed unauthenticated port.
- **Consequences:** This remains Windows-specific and needs continued abuse/negative testing.
- **Revisit when:** Cross-platform transport or multiple simultaneous Minecraft clients are required.

## D-004 — Keep Codex read-only until final-path validation is stronger

- **Date:** 2026-08-17
- **Status:** Active
- **Context:** String-based path checks can race with Windows junction replacement before process launch.
- **Decision:** Run bridge tasks with the Codex read-only sandbox.
- **Reason:** The project must not claim safe file-changing access before handle-based target validation exists.
- **Alternatives considered:** Enabling workspace-write immediately after repeated string checks.
- **Consequences:** Current testing can converse with Codex but cannot edit files through the bridge.
- **Revisit when:** Final filesystem targets are resolved and bound safely at launch.

## D-005 — Test the exact production transport with fake Codex execution

- **Date:** 2026-08-17
- **Status:** Active
- **Context:** Internal registry tests could not prove that authentication, protocol handling, reconnect, cancellation, and shutdown worked through the real server.
- **Decision:** Keep one production `CompanionHost` and test it through real loopback Kestrel, named pipes, and WebSockets while replacing only `ICodexTaskAdapter` with deterministic fakes.
- **Reason:** This exercises the actual security and lifecycle boundary without requiring network-dependent Codex runs for every test.
- **Alternatives considered:** A separate test server or direct calls to private request handlers.
- **Consequences:** Automated tests are realistic and repeatable; a separate live smoke test still verifies the official Codex adapter.
- **Revisit when:** The transport architecture changes.

## D-006 — Verify one local companion by lease and executable identity

- **Date:** 2026-08-17
- **Status:** Active
- **Context:** A deterministic named-pipe name alone could be occupied by another same-user process, and duplicate companions could compete for the same broker.
- **Decision:** Hold an exclusive per-user LocalAppData lease for the companion lifetime and verify the named-pipe server process uses the expected companion executable before requesting a capability.
- **Reason:** This provides a practical unsigned-development identity boundary without placing reusable secrets on disk.
- **Alternatives considered:** Trusting the pipe name alone, persisting a bearer secret, or a thread-owned named mutex.
- **Consequences:** Duplicate launches attach to the existing broker. The non-secret lock file may remain in `%LOCALAPPDATA%\MinecraftCodex\runtime`; its exclusive handle, not file existence, represents ownership. Publisher and final-file verification remain release work.
- **Revisit when:** The companion is packaged and code-signed.

## D-007 — Idle exit requires zero clients and zero active tasks

- **Date:** 2026-08-17
- **Status:** Active
- **Context:** The companion must survive Minecraft disconnects during work but should not run forever when unused.
- **Decision:** Start a two-minute idle grace only when no authenticated clients are connected and no Codex tasks are active; any connection or active task cancels the countdown.
- **Reason:** This preserves background work and reconnectability while providing automatic cleanup.
- **Alternatives considered:** Exiting with the client, never exiting automatically, or counting retained completed snapshots as active work.
- **Consequences:** Completed snapshots remain available during the grace period and disappear when the companion exits.
- **Revisit when:** Persistent task recovery or configurable lifecycle settings are added.

## D-008 — Keep all maintained documentation under `docs`

- **Date:** 2026-08-17
- **Status:** Active
- **Context:** Project memory and technical notes were split between top-level `project-docs/` and `docs/` folders.
- **Decision:** Store durable project memory under `docs/project/` and subsystem or investigation notes under `docs/technical/`.
- **Reason:** One documentation root is easier to understand while the subfolders still distinguish project-wide memory from technical detail.
- **Alternatives considered:** Keeping `project-docs/` as a separate top-level convention.
- **Consequences:** Project-memory maintenance targets `docs/project/` for this repository, overriding only the skill's default top-level folder location while retaining its standard shared filenames and `components/` structure.
- **Revisit when:** The documentation becomes large enough to need a generated site or different information architecture.
## D-011 — Enabled ordinary chat is private, concise, and single-task

- **Date:** 2026-08-18
- **Context:** The first useful chat slice must not disclose prompts to multiplayer servers and must fit Codex output into Minecraft chat.
- **Decision:** When Codex mode is enabled, cancel ordinary chat at Fabric's pre-packet `ALLOW_CHAT` hook, echo it locally as `You:`, and render complete Codex messages locally as `Codex:` chunks. Leave slash commands untouched, guide Codex toward a few sentences unless detail is necessary, and allow only one active task. Disabling mode requests `task.cancel` before closing the session.
- **Consequences:** There is no token streaming yet, and real multiplayer non-disclosure still requires an in-game verification pass.
