# Companion protocol version 1

This document summarizes the implemented local contract shared by the C# companion and Java Fabric client. Code remains authoritative if this summary and the implementation ever disagree; any incompatible change requires a new protocol version.

## Bootstrap and authentication

- Windows only.
- The companion listens on a current-user-only named pipe and an ephemeral IPv4 loopback WebSocket at `ws://127.0.0.1:<port>/v1/ws`.
- A bootstrap request is one JSON line:

```json
{"protocolVersion":1,"type":"capability.request"}
```

- A successful reply contains `protocolVersion`, `wsUri`, and a random capability. The capability expires after one minute, works once, and is sent as `Authorization: Bearer <capability>` during the WebSocket upgrade.
- Before requesting a capability, clients verify that Windows reports the configured companion executable as the named-pipe server. Clients reject any endpoint that is not the exact numeric IPv4-loopback `ws` shape above.
- Capabilities and task snapshots remain in memory and are never persisted by the companion.

## WebSocket envelopes

Client requests use:

```json
{"version":1,"type":"task.snapshot","requestId":"non-empty-client-id","payload":{}}
```

Server responses and events use the same top-level fields:

```json
{"version":1,"type":"server.hello","requestId":null,"taskId":null,"sequence":null,"payload":{"version":1}}
```

- Requests require `version: 1`, a non-empty `requestId`, a `type`, and a JSON `payload`.
- Text messages are limited to 64 KiB. Binary or oversized messages close the connection.
- Responses correlate through `requestId`. Task events use `taskId` plus a monotonically increasing `sequence` and have no request ID.

## Requests and responses

### `task.start`

Payload:

```json
{"prompt":"text","workingDirectory":"C:/optional/trusted/path"}
```

The prompt must contain 1–32,768 characters. The working directory defaults to the companion's startup directory and must pass the companion's trusted-path policy. Success returns `request.accepted` with `taskId` and a message; failure returns `request.rejected`.

A start request ID is idempotent for the companion lifetime. Repeating the same request ID with the same prompt and canonical working directory returns the original task. Reusing it with different content is rejected. Request IDs longer than 128 characters are rejected.

### `task.snapshot`

Payload is `{}`. The `task.snapshot` response contains `tasks`. Each task contains:

- `taskId`
- `state`: `starting`, `running`, `completed`, `failed`, or `cancelled`
- `threadId`, when supplied by Codex
- `lastSequence`
- `historyTruncated`
- ordered retained `events`

### `task.cancel`

Payload is `{"taskId":"..."}`. A cancellable task returns `request.accepted`; a missing or terminal task returns `request.rejected`.

## Task events

- `task.started` with `{ "threadId": ... }`
- `message.completed` with `{ "text": ... }`
- `task.completed` with a null payload
- `task.failed` with `{ "category": ... }`; cancellation currently appears as category `cancelled` and snapshot state `cancelled`

The current Codex adapter emits complete messages, not token deltas.

## Limits and lifecycle

- At most eight tasks are retained per companion process.
- At most 512 events are retained per task; older events are dropped and `historyTruncated` becomes true.
- Each connected client has a bounded 256-message event queue. A client that cannot keep up is disconnected rather than blocking task execution.
- Disconnecting a client does not cancel its tasks.
- Reconnection and snapshots work only while the original companion process remains alive.
- The companion exits after two minutes with no authenticated clients and no active tasks.
