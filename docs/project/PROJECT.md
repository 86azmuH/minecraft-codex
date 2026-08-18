# Minecraft Codex Project Memory

## Purpose

Minecraft Codex is a Windows-first Fabric client mod that will let its owner talk privately to Codex through normal Minecraft chat, then later manage chats and projects through richer interfaces.

## Current status

The Fabric client mod foundation is implemented for Minecraft 26.1.2. It registers private client-side `/codex on`, `/codex off`, and `/codex status` commands; enables Codex mode only after a successful companion handshake and snapshot; renders a Minecraft-style `[ CODEX ]` badge beside the hotbar; and resets the mode silently on join, disconnect, or level change. While enabled, ordinary chat is cancelled before Minecraft creates a server packet, shown privately as `You:`, and submitted as one read-only Codex task; complete responses are shown privately as concise, chat-sized `Codex:` messages. Slash commands remain on their normal path and additional prompts are blocked while a task is active. Companion work stays off Minecraft's game thread and the Windows named-pipe server executable is verified before a capability is accepted. The companion foundation finds the official Codex CLI, checks ChatGPT authentication, enforces trusted working folders, runs read-only Codex tasks, retains reconnectable in-memory snapshots, enforces one companion per Windows user, and exits after two idle minutes when unused.

## Major parts

- **Companion system:** A C# background executable, bootstrap broker, loopback protocol, and standalone development client. Its boundaries and handoff state are maintained in [`components/companion.md`](components/companion.md).
- **Fabric mod:** A client-only transport/status checkpoint is implemented. Its boundary and handoff state are maintained in [`components/fabric-mod.md`](components/fabric-mod.md).

## Cross-component flow

The Fabric mod acts as the private Minecraft client. Its implemented status path launches or attaches to the companion, obtains a temporary capability from the companion's Windows named pipe, verifies the pipe server executable, connects over loopback WebSocket, and requests a task snapshot. Future chat-mode requests and normalized task events will use the same boundary. The companion alone owns Codex execution, permission enforcement, and recoverable in-memory task state.

## Important dependencies

- **Official Codex CLI:** Uses the owner's existing ChatGPT authentication.
- **.NET 8 SDK/runtime:** A project-specific installation builds and runs the companion.
- **Java 25 and Fabric:** Build and run the Minecraft 26.1.2 client mod.
- **Windows named pipes:** Provide same-user bootstrap and reconnection.

## Constraints and assumptions

- Windows only.
- Automatically trusted roots remain those defined in `README.md`.
- Credentials are never read directly by the mod or companion.
- Codex is restricted to read-only mode in the bridge prototype.
- Task snapshots remain only in memory while the companion survives.

## Known gaps and next priorities

- Strengthen executable identity with final-file and publisher verification for release packaging.
- Separate host startup cancellation from host-lifetime cancellation.
- Add handle-based Windows path validation before enabling file changes.
- Verify the private prompt path in Prism on a multiplayer server, including server-side non-disclosure, busy-state behavior, cancellation, and several GUI scales.

The next concrete component handoff is maintained in [`components/fabric-mod.md`](components/fabric-mod.md).
