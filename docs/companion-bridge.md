# Companion bridge checkpoint

This checkpoint implements the local bridge independently of Minecraft.

## Implemented behavior

- `serve --working-directory <path>` starts the companion only for an automatically trusted existing directory.
- The server discovers the manifest-verified official npm Codex CLI and checks authentication through `codex login status`.
- Kestrel binds to an ephemeral IPv4 loopback port only.
- Startup emits a bootstrap descriptor containing protocol version, WebSocket URI, and a random 256-bit capability through inherited stdout.
- WebSocket upgrades require the capability through `Authorization: Bearer`, compare its SHA-256 digest in fixed time, reject it after one minute, and consume it after the first successful connection.
- Protocol version 1 supports `task.start`, `task.snapshot`, and `task.cancel`.
- Client request IDs are idempotent for the lifetime of the companion, preventing a repeated request from starting duplicate work.
- Codex prompts are written through stdin, not process arguments.
- Codex runs with `--sandbox read-only` for this checkpoint.
- Raw Codex JSONL is normalized into sequenced `task.started`, `message.completed`, `task.completed`, and `task.failed` events.
- Recent task events and authoritative lifecycle state remain in bounded memory and can be returned in a snapshot.
- A disconnected client does not cancel a running Codex task.
- Slow client queues are bounded and removed rather than blocking Codex output processing.
- stderr is drained but never forwarded or logged by default.

## Deliberate limitations

- No Minecraft or Fabric code is included.
- The standalone test client launches the companion and reads its bootstrap capability directly through an inherited stdout pipe. It does not write the capability to disk.
- Task history is memory-only and is lost if the companion exits.
- There is no idle shutdown or durable reconnect discovery yet. The one-time bootstrap capability intentionally allows one client connection in this checkpoint; reconnect token minting requires a current-user bootstrap broker later.
- The in-memory registry is intentionally capped at eight tasks for this prototype and does not evict completed tasks. Restart the prototype between separate manual sessions.
- Current Codex CLI JSONL emits a completed agent message rather than token-level text deltas, so the normalized bridge currently emits `message.completed`.
- File-changing Codex tasks remain disabled. Enabling workspace writes requires stronger final-path/handle validation immediately before launch.

## Build

```powershell
.\scripts\build.ps1
```

This builds the companion, runs the trusted-path tests, and builds the standalone client.

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
