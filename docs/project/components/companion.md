# Companion Component

## Purpose and boundary

The companion is the Windows-only background boundary between a local client and the official Codex CLI. It owns Codex process execution, trusted-path policy, task state, authentication of local connections, reconnection, and idle shutdown. It does not own Minecraft presentation, Codex credentials, or persistent task history.

## The short version

The current prototype is a secure bridge between local clients and the official Codex command-line program. A client asks a private Windows pipe for temporary permission, connects to the companion on the local computer, and exchanges requests and simplified task events. The Fabric mod now proves startup, authenticated connection, and snapshot recovery; prompt submission is still proven only by the standalone client.

## Main flow

1. The standalone client starts the companion for an approved project folder.
2. The companion finds the official Codex CLI and confirms that it is logged in through ChatGPT.
3. The client contacts a current-user-only Windows named pipe.
4. The pipe returns the current local WebSocket address and a fresh capability that expires after one minute and works once.
5. The client starts a read-only Codex task over the authenticated WebSocket.
6. The companion converts Codex JSONL into ordered task and message events.
7. After disconnecting, a newly started client calculates the same pipe name, obtains a new capability, reconnects, and requests the retained task snapshot.
8. When the last client is gone and all tasks are terminal, the companion waits two minutes and exits automatically unless a client reconnects or new work begins.

## Internal parts

### Companion server

`CompanionHost` listens only on IPv4 loopback, authenticates WebSocket upgrades, routes protocol requests, and keeps task state in memory. Production and automated transport tests use this same host.

### Bootstrap broker

Listens on a Windows named pipe restricted to the current user. Before requesting a capability, the client checks that Windows reports the expected companion executable as the pipe server. The broker returns no long-lived password; it mints bounded, short-lived, one-use capabilities.

### Instance lease and idle shutdown

An exclusive non-secret lock under the current user's LocalAppData prevents two companions from becoming owners. Client and active-task counters control a two-minute idle timer; disconnected background work keeps the companion alive.

### Task registry and Codex adapter

The registry prevents duplicate starts and stores ordered events. The adapter sends prompts to Codex through stdin, drains process output, and normalizes supported JSONL events.

### Standalone client

Proves the complete bridge without Minecraft. Its reconnect test launches a distinct second client process and verifies the same task, state, and event sequence.

### Automated bridge tests

Connect to the real production host through real named pipes and WebSockets. Deterministic fake Codex adapters make active reconnect, cancellation, and shutdown repeatable without weakening the tested transport boundary.

## Data and external services

- Prompts and responses travel locally between client and companion, then through the official Codex CLI to OpenAI.
- Capabilities remain in memory and are not written to disk.
- Task history remains in companion memory and disappears when the companion exits.
- Codex authentication remains owned by the official CLI.

## Stable interface

- A current-user-only named pipe returns the active loopback WebSocket endpoint and a fresh one-minute, one-use capability.
- Protocol version 1 currently accepts `task.start`, `task.snapshot`, and `task.cancel` requests.
- The companion emits normalized, ordered task events and retains snapshots only for its process lifetime.
- The checkpoint behavior is described in [`../../technical/companion-bridge.md`](../../technical/companion-bridge.md), and the implemented wire contract is summarized in [`../../technical/protocol-v1.md`](../../technical/protocol-v1.md).

## Safety and failure behavior

- Untrusted, credential-containing, missing, UNC, device, and reparse-point working paths are rejected.
- The WebSocket is loopback-only.
- Clients reject broker-provided endpoints unless they use plain local `ws` with a numeric loopback address.
- Named-pipe requests have a three-second read deadline so a stalled client cannot block reconnects indefinitely.
- At most sixteen unused capabilities can exist at once.
- Codex currently runs read-only.

## Terms worth knowing

- **Companion:** The background Windows executable between clients and Codex.
- **Bootstrap broker:** The private named-pipe service that gives a client temporary connection permission.
- **Capability:** A random one-use secret authorizing one WebSocket connection.
- **Snapshot:** The companion's current in-memory description of tasks and their ordered events.

## Current state

The standalone companion bridge is implemented and covered by production-transport tests. The Minecraft mod has also completed a real in-game status connection and snapshot request. Build and live-test commands are authoritative in [`../OPERATIONS.md`](../OPERATIONS.md); architectural reasons are authoritative in [`../DECISIONS.md`](../DECISIONS.md).

## Known issues and next handoff

- Strengthen executable identity with final-file and publisher verification before release packaging.
- Separate startup cancellation from host-lifetime cancellation.
- Add handle-based Windows path validation before changing Codex from read-only mode.
- Treat the implemented version 1 status contract as the current integration baseline while the Fabric component adds chat mode and prompt submission.
