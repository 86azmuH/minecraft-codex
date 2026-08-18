# Fabric Mod Component

## Purpose and boundary

The Fabric mod is the private Minecraft user interface for Codex. It owns client commands, Minecraft presentation, local UI state, and asynchronous communication with the Windows companion. It does not own Codex credentials, trusted-path enforcement, Codex process execution, or durable task storage.

## Current checkpoint

The client-only Minecraft 26.1.2 scaffold is under `mod/`. It uses Java 25, Gradle 9.4, pinned Fabric Loom 1.15.5, Fabric Loader 0.19.3, and Fabric API 0.155.2+26.1.2.

The private client commands now provide:

1. `/codex on` enters a connecting state, performs the full executable-verified bootstrap and snapshot request, and enables Codex mode only after success while retaining that authenticated WebSocket session;
2. while enabled, ordinary non-command chat is cancelled locally before packet creation, echoed privately as `You:`, followed immediately by `Codex: Thinking…`, and submitted to Codex with concise-response guidance;
3. complete `message.completed` responses are split into readable private `Codex:` chat messages, with only one task accepted at a time;
4. `/codex off` and world lifecycle resets request cancellation of an accepted task before closing the session; slash commands are not intercepted;
5. `/codex off` immediately cancels any pending enable attempt, closes the active session, and disables the mode;
6. `/codex status` reports companion availability plus disabled, connecting, or enabled mode state;
7. all process, named-pipe, and WebSocket work runs on a daemon background thread and returns feedback through Minecraft's client thread.

While enabled, a bordered dark `[ CODEX ]` badge with amber text appears beside the hotbar. It falls back to the left side when the right side lacks room. Joining, disconnecting, replacing the active client level, or losing the retained companion WebSocket silently disables the mode. Overlapping lifecycle callbacks are safe because reset is idempotent. Each asynchronous completion is tied to its exact generation, so an older connection result can neither re-enable the mode nor print a stale error during a newer attempt.

The development companion path is supplied with `-Dminecraftcodex.companion.executable=<path>`. The working directory defaults to the process working directory and can be overridden with `-Dminecraftcodex.workingDirectory=<path>`. Release packaging and automatic installed-path discovery are not implemented.

## Verification

- The ordinary Gradle suite compiles the mod and tests real protocol JSON fixtures.
- Focused state tests cover successful and failed enablement, repeated enable requests, stale asynchronous completion after reset, and an old result arriving during a newer attempt. Layout tests cover right-side placement and the narrow-GUI fallback.
- An opt-in integration test launches the actual C# companion, verifies its Windows pipe identity through JNA, connects over authenticated WebSocket, receives `server.hello`, and requests a real snapshot.
- The built jar was loaded through Prism Launcher in a Fabric 26.1.2 instance. In a running world, `/codex status` displayed `Companion connected (protocol 1)`.
- Unit tests cover prompt guidance, response chunking, task request/event parsing, and the `task.cancel` envelope. The real companion integration test submits a prompt and receives its response.

For Prism Launcher, Java arguments containing Windows paths must use forward slashes. Prism treats backslashes as escapes and otherwise removes them before launching Java. The verified development configuration is maintained in [`../OPERATIONS.md`](../OPERATIONS.md).

## Known issues and next handoff

- Verify enabled-mode chat cancellation on a real multiplayer server and confirm that neither prompts nor responses appear to peers or the server console.
- Add progressive rendering if the companion gains token-delta events; it currently emits complete `message.completed` events.
- Decide how the release artifact packages and locates the companion instead of relying on a JVM property.
- Visually verify the badge, chunked responses, busy message, and silent lifecycle resets in Prism Launcher at multiple GUI scales.
