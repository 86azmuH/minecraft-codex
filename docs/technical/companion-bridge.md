# Companion bridge checkpoint

This document describes the companion checkpoint, which was implemented independently before the Fabric client connected to it.

## Implemented behavior

- `serve --working-directory <path>` starts the companion only for an automatically trusted existing directory.
- The server discovers the manifest-verified official npm Codex CLI and checks authentication through `codex login status`.
- Kestrel binds to an ephemeral IPv4 loopback port only.
- Startup emits a non-secret descriptor containing protocol version, WebSocket URI, and the current user's deterministic bootstrap-pipe name through inherited stdout.
- A current-user-only Windows named-pipe broker mints fresh random 256-bit WebSocket capabilities. Each capability expires after one minute and is consumed by its first successful connection.
- A newly started client can calculate the same per-user pipe name, request the current WebSocket address and a fresh capability, reconnect to the surviving companion, and request a task snapshot.
- Protocol version 1 supports `task.start`, `task.snapshot`, and `task.cancel`.
- The authoritative implemented wire contract is summarized in [`protocol-v1.md`](protocol-v1.md).
- Client request IDs are idempotent for the lifetime of the companion, preventing a repeated request from starting duplicate work.
- Codex prompts are written through stdin, not process arguments.
- Codex runs with `--sandbox read-only` for this checkpoint.
- Raw Codex JSONL is normalized into sequenced `task.started`, `message.completed`, `task.completed`, and `task.failed` events.
- Recent task events and authoritative lifecycle state remain in bounded memory and can be returned in a snapshot.
- A disconnected client does not cancel a running Codex task.
- Slow client queues are bounded and removed rather than blocking Codex output processing.
- stderr is drained but never forwarded or logged by default.
- One companion owns an exclusive per-user LocalAppData lease; duplicate launches attach to the existing broker instead of starting another server.
- Before requesting a capability, the standalone client verifies the named-pipe server process uses the expected companion executable and accepts only the exact IPv4 loopback WebSocket endpoint shape.
- The companion exits after a two-minute grace only when no authenticated clients and no active tasks remain.

## Deliberate limitations

- Minecraft and Fabric code remain outside the companion subsystem under `mod/`; the current mod uses this bridge for startup and snapshot status.
- The standalone test client launches the companion, completes a task, disconnects, and launches a distinct second client process. The second process discovers the surviving companion through the per-user named pipe, obtains a fresh capability, and verifies the retained task snapshot. Capabilities are never written to disk.
- Task history is memory-only and is lost if the companion exits.
- There is no recovery after the companion itself exits. Reconnection works only while the original companion process survives; idle shutdown now occurs after two unused minutes.
- The in-memory registry is intentionally capped at eight tasks for this prototype and does not evict completed tasks. Restart the prototype between separate manual sessions.
- Current Codex CLI JSONL emits a completed agent message rather than token-level text deltas, so the normalized bridge currently emits `message.completed`.
- File-changing Codex tasks remain disabled. Enabling workspace writes requires stronger final-path/handle validation immediately before launch.

## Build

```powershell
.\scripts\build.ps1
```

This builds the companion, runs the trusted-path and production-host transport tests, and builds the standalone client. The transport suite uses real Kestrel, named pipes, and WebSockets while substituting only a deterministic Codex adapter. It covers missing, wrong, replayed, and expired capabilities; invalid protocol traffic; post-error health; request idempotency; untrusted paths; active reconnect; cancellation; active-work shutdown; and pipe reuse.

## Live test

```powershell
.\scripts\test-companion.ps1
```

The script performs a real authenticated read-only Codex request and expects the normalized response `COMPANION_BRIDGE_OK`. It starts and stops the local server automatically and removes its temporary bootstrap files.

To try a different harmless prompt:

```powershell
.\scripts\test-companion.ps1 -Prompt "Explain what a Minecraft chunk is in one sentence."
```

The output is protocol JSON. A successful run contains, in order:

1. `server.hello`
2. `request.accepted`
3. `task.started`
4. `message.completed`
5. `task.completed`
