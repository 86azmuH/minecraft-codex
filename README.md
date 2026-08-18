# Minecraft Codex

## Introduction

Minecraft Codex is a Windows-first, client-side Minecraft Java mod that brings Codex into Minecraft as a practical workspace. Its purpose is to let the player continue real work while playing: holding conversations, navigating projects, resuming existing Codex chats, and allowing Codex to edit files or run commands without leaving the game.

The experience will have two connected interfaces. The first version will turn the normal Minecraft chat box into a private Codex conversation. Later versions will add a dedicated, non-pausing workspace screen for the complete project and chat experience. Both interfaces will connect to the same Codex sessions through a local companion process.

This document is the authoritative product baseline. Current implementation state, operations, and newer architectural decisions are maintained under [`docs/project/`](docs/project/).

## Current implementation checkpoint

- The Windows C# companion is implemented with a same-user named-pipe bootstrap, authenticated IPv4-loopback WebSocket transport, read-only Codex execution, reconnectable in-memory snapshots, and idle shutdown.
- The Minecraft 26.1.2 Fabric client foundation is implemented with private `/codex on`, `/codex off`, and `/codex status` commands plus a hotbar-adjacent `[ CODEX ]` badge.
- The built mod has been verified in-game through Prism Launcher: it launches or attaches to the companion, verifies the pipe server executable, authenticates the WebSocket, and retrieves a snapshot without blocking Minecraft's game thread.
- The current bridge emits complete `message.completed` responses rather than token deltas; progressive rendering remains a future chat-mode capability.
- Codex mode, private ordinary-chat interception, single-task submission, complete-response rendering, and lifecycle safety are implemented. Chat selection, token streaming, and the dedicated workspace remain unimplemented.

## Project goals

The project should:

- Make Codex comfortably accessible without leaving Minecraft.
- Support general work across multiple projects instead of being tied to one codebase or task.
- Use the user's existing Codex installation and ChatGPT subscription authentication.
- Discover and interact with compatible chats created through Codex desktop, Codex CLI, Minecraft Codex, and Codex cloud interfaces.
- Allow Codex to edit files and run commands automatically inside explicitly trusted project locations.
- Keep active work running when the Codex screen closes and, when necessary, after Minecraft exits.
- Fail safely when Codex is unavailable or an experimental integration changes.
- Preserve ordinary Minecraft chat behavior and prevent accidental disclosure on multiplayer servers.

## Initial platform and scope

### Minecraft target

- Minecraft Java Edition 26.1.2 for the current development checkpoint.
- Fabric mod loader.
- Client-side mod.
- Single-player is the primary use case.
- The world must continue running while the dedicated Codex screen is open; opening it must not pause an integrated single-player server.
- Multiplayer should remain safe and compatible even though it is not the initial focus.

Minecraft 26.1 was selected as the initial compromise between a modern release and broad mod availability. The design should avoid unnecessary version coupling so it can later be ported to Minecraft 26.2 and newer releases.

### Operating system

- The first release targets Windows only.
- Cross-platform support is outside the first-version scope.

### OpenAI product

- The integration uses Codex, not ordinary ChatGPT conversations.
- The user signs in through the official Codex CLI using their ChatGPT account and subscription access.
- The mod must never imitate browser sessions, scrape ChatGPT, or reuse browser cookies.
- Ordinary ChatGPT conversation history is not part of this project.

## User experience

### Minecraft chat interface

The first interface reuses Minecraft's normal chat screen as a private Codex terminal.

Initial behavior:

- A local command such as `/codex on` enables Codex mode.
- A local command such as `/codex off` disables Codex mode.
- While enabled, ordinary messages typed into Minecraft chat are intercepted locally and routed privately to the active Codex chat.
- Codex prompts, commands, menus, and responses must never be sent to the Minecraft server or shown to other players.
- Codex responses appear as private client-side chat messages with a clear Codex label. The current bridge delivers complete messages rather than token deltas.
- Long responses are divided into readable chat-sized sections.
- Longer Codex reports should end with a short plain-language summary when available, without replacing or hiding the complete response.
- The chat input has a clearly different appearance while Codex mode is active, with a prominent `CODEX MODE` indicator.
- Codex mode automatically switches off whenever the player joins, leaves, or changes a world or server.
- The player must deliberately re-enable it after every world/server transition.

The first version uses one active Codex conversation. The next version adds a private command-driven conversation browser rendered into normal Minecraft chat:

- `/codex chats` prints a numbered list of available Codex chats.
- `/codex open <number>` selects a chat from the displayed list.
- `/codex new` creates and selects a new chat.
- `/codex next` and `/codex previous` navigate longer chat lists.
- The printed menu identifies the active chat and, when known, its working folder or project.
- Invalid or unavailable selections produce private, recoverable local errors.

These commands are the initial navigation interface. A graphical selector may be considered later, but it is not required before the full workspace.

### Dedicated Codex workspace screen

A later interface is a dedicated Minecraft screen opened with a configurable keybind. Opening the screen must not pause the world. It supplements rather than replaces the normal-chat experience.

The screen should support:

- Viewing projects.
- Selecting and changing the active project.
- Creating a new project association from a folder.
- Viewing chats belonging to a project.
- Creating new chats within a project.
- Creating standalone chats that are not intentionally organized under a named project.
- Discovering compatible existing Codex chats.
- Opening and resuming existing chats.
- Switching between chats without leaving Minecraft.
- Displaying each chat's working folder and current status.
- Streaming Codex responses as they arrive.
- Showing long-running tasks, completed tasks, failures, and tasks that need attention.
- Preserving unsent input when navigating away or when an integration fails.

The likely layout is a full workspace browser with projects, chats, and the active conversation visible together. The exact visual layout remains open for design iteration. When both interfaces exist, the full workspace and normal-chat mode share the same selected project, chat, conversation state, and background tasks.

### Notifications

When a background task completes, fails, or needs attention:

- Post a private message in Minecraft chat.
- Show a small unobtrusive in-game notification.
- Avoid repeated notifications for the same unchanged state.

Notification behavior after Minecraft has closed is not yet required for the first version.

### Future advancements

A later phase may add real Minecraft advancements and advancement popups for Codex-related milestones, such as:

- First Codex chat.
- Number-of-chats milestones.
- First completed task.
- Number-of-completed-tasks milestones.
- First project used.
- Multiple projects used.
- Long-running task completion.
- Possible usage streaks or other meaningful milestones.

These should behave like normal Minecraft advancements rather than replacing the main project-navigation interface. Advancement implementation is explicitly deferred until after the core integration works.

## Projects and chats

### Project model

A project is primarily identified by a trusted working directory. Chats associated with that directory appear within the project.

The mod should:

- Infer projects from the working directories recorded by Codex sessions.
- Allow the user to select another folder and make it a trusted project.
- Treat child project folders independently where appropriate.
- Allow movement between projects and their chats.
- Keep standalone chats accessible even when they do not map cleanly to a named project.

### Chat compatibility

Compatibility is prioritized over long-term API stability.

The system should attempt to discover and interact with:

- Chats created through Minecraft Codex.
- Local Codex CLI sessions.
- Compatible Codex desktop chats.
- Codex cloud or remote tasks when exposed through supported Codex commands or interfaces.
- Archived local chats where Codex supports listing and resuming them.

Local and cloud chats may use different discovery mechanisms. They should be presented through a unified Minecraft interface where possible, while retaining their true source and status.

The implementation must not edit raw Codex transcript JSONL files or SQLite databases to simulate official operations. Session files and databases may be inspected read-only only when required for compatible discovery and when official interfaces do not provide equivalent information. Creating, resuming, renaming, archiving, deleting, and otherwise changing sessions should go through Codex interfaces whenever possible.

## Architecture

### Fabric client mod

The Fabric mod is responsible for:

- Minecraft keybindings.
- The dedicated non-pausing screen.
- Quick chat mode and interception of local chat input.
- Rendering streamed conversation events.
- Displaying projects, chats, task states, notifications, and recoverable errors.
- Holding local UI state and unsent drafts.
- Starting or reconnecting to the Windows companion.
- Never blocking Minecraft's render or game thread on Codex operations.

### Windows companion process

A separate local companion process is required because Codex tasks must survive screen closure and may need to survive Minecraft exiting.

Lifecycle:

- The mod launches the companion automatically when needed.
- The companion does not start automatically with Windows in the first version.
- It owns active Codex child processes and integration sessions.
- Closing the Codex screen does not stop active work.
- If Minecraft exits while work is active, the companion continues running.
- The companion exits automatically after Minecraft has disconnected, all work is complete, and an appropriate idle period has elapsed.
- Restarting Minecraft reconnects to the existing companion when it is still running.

Responsibilities:

- Locate and validate the installed Codex CLI.
- Check authentication status without reading authentication files.
- Discover local and cloud chats through Codex interfaces.
- Start, resume, and monitor Codex tasks.
- Stream normalized events to Minecraft.
- Enforce trusted-root policy independently of the Minecraft UI.
- Preserve enough state to reconnect after a Minecraft restart.
- Produce sanitized diagnostics.

### Codex integration adapters

Codex integration should be isolated behind version-aware adapters.

Preferred behavior:

1. Use richer Codex interfaces, such as the app-server and cloud/task commands, for session discovery and complete compatibility.
2. Use stable non-interactive `codex exec` and resume functionality as a fallback for basic conversations and work.
3. Detect interface or schema incompatibility rather than assuming all Codex versions behave identically.
4. Report reduced-functionality mode clearly inside Minecraft.

Some richer Codex app-server and cloud interfaces are experimental and may change after Codex updates. Compatibility is still the chosen priority. A change should require updating a small adapter rather than rewriting the Minecraft UI or companion.

### Local communication

The Minecraft mod and companion communicate only on the local computer.

Protocol version 1 currently uses a current-user-only Windows named pipe to mint one-minute, one-use capabilities for an IPv4-loopback WebSocket. It:

- Use a local-only transport.
- Authenticate each Minecraft client connection with a short-lived capability or equivalent local secret created specifically for the companion session.
- Never expose the companion on the LAN by default.
- Support requests, streamed events, cancellation, reconnection, and state snapshots.
- Use explicit protocol-version negotiation.
- Reject unsupported messages safely.
- Never transfer Codex login tokens to the Minecraft process.

The implemented JSON protocol supports `task.start`, `task.snapshot`, and `task.cancel`, plus normalized task events. It remains intentionally small and may evolve through explicit protocol versions.

## Permissions and security

### Automatically trusted work locations

Codex may automatically edit files and run commands inside:

- `%USERPROFILE%\Documents\Codex`
- A user-configured synchronized projects directory, such as `%USERPROFILE%\SyncHub\Projects`

New folders may be added as trusted projects through an explicit user action.

### Codex configuration locations

The following may be read so the integration can understand Codex behavior, but modifying them requires explicit confirmation:

- A user-configured portable Codex settings directory
- `%USERPROFILE%\.codex\config.toml`
- Codex `AGENTS.md` files.
- Custom agent definitions.
- Personal skills.
- Rules.
- Plugin configuration.
- Other settings that can change Codex behavior or propagate across synchronized computers.

### Chat and session access

The companion may discover and manage all local Codex chats through Codex interfaces. Relevant local storage currently includes:

- `%USERPROFILE%\.codex\sessions`
- `%USERPROFILE%\.codex\archived_sessions`
- `%USERPROFILE%\.codex\session_index.jsonl`
- Codex state and thread-summary databases under `%USERPROFILE%\.codex`

This access does not make the entire `.codex` directory an unrestricted command workspace. Session state is a separately controlled integration resource.

### Credential boundary

The mod and companion must not read, copy, display, synchronize, log, or directly modify:

- `%USERPROFILE%\.codex\auth.json`
- `.sandbox-secrets`
- OAuth tokens.
- API keys.
- Windows Credential Manager entries.
- Browser cookies or browser sessions.
- Any equivalent credential material discovered later.

Authentication remains owned by the installed Codex client. The companion asks Codex whether it is authenticated and directs the user through official login when necessary.

### Out-of-scope locations

- File changes or commands outside trusted work locations require confirmation.
- Changes to protected Codex configuration require confirmation.
- Destructive operations must receive stricter handling even within trusted roots.
- The companion must enforce these boundaries; the Minecraft UI alone is not a security boundary.

## Existing local storage observations

Initial inspection established this local layout:

- `%USERPROFILE%\.codex` contains Codex sessions, indexes, state databases, configuration, customizations, caches, and credentials.
- `%USERPROFILE%\Documents\Codex` is the default location for working folders and deliverables created by desktop Codex tasks.
- A synchronized folder may optionally contain portable settings and working projects.
- Portable settings should use a deliberate allowlist that excludes authentication, sessions, histories, logs, databases, caches, system skills, and plugin binaries.
- Cloud Codex chats remain server-side and must be discovered through Codex rather than assumed to exist fully on disk.

## Reliability and failure handling

Minecraft must remain stable even when Codex fails.

Required behavior:

- Never crash Minecraft because Codex is missing, logged out, updating, incompatible, offline, or returning malformed output.
- Never perform network or process work on Minecraft's render thread.
- Detect missing Codex installation and give a clear setup message.
- Detect authentication problems without accessing credentials.
- Preserve unsent drafts when switching chats or after failures.
- Preserve the active project and chat selection where safe.
- Reconnect to a surviving companion after Minecraft restarts.
- Detect unsupported Codex interface versions.
- Fall back to reduced-functionality `codex exec` mode when possible.
- Explain which features are unavailable in reduced-functionality mode.
- Do not blindly retry any command or task that may already have modified files.
- Make retries explicit and idempotent where possible.
- Deduplicate completion and attention notifications.
- Surface background task status instead of silently abandoning work.
- Keep errors understandable from inside Minecraft while retaining sanitized diagnostic detail for troubleshooting.

### Diagnostics and privacy

Diagnostics should record operational facts such as:

- Component versions.
- Connection lifecycle.
- Adapter selected.
- Message type and request ID.
- Task lifecycle state.
- Exit codes and sanitized error categories.

Diagnostics must not record:

- Authentication tokens.
- API keys.
- Browser data.
- Full prompts or responses by default.
- Sensitive file contents.
- Environment-variable values.

## Background task behavior

- Tasks continue when the full Codex screen closes.
- Tasks continue when quick mode is disabled.
- Tasks may continue after Minecraft exits.
- Minecraft can reconnect and obtain a current state snapshot.
- A task that needs approval or clarification enters an attention-required state rather than guessing.
- The companion stops automatically only after no active tasks remain and Minecraft is disconnected.

## Explicitly deferred decisions

The following are not yet finalized:

- Exact screen layout and visual design.
- Default keybindings.
- Companion release packaging and installed-location discovery. The implementation language is C#.
- Future protocol additions beyond the implemented named-pipe plus WebSocket version 1 contract.
- Exact Codex app-server/cloud adapter APIs supported by the first prototype.
- Whether completed companion tasks produce Windows notifications after Minecraft closes.
- Search, filtering, pinning, and sorting behavior for large chat inventories.
- Markdown, code-block, diff, image, and tool-call rendering details.
- Cancellation and approval UI details.
- Advancement names, thresholds, icons, and progression tree.
- Distribution through Modrinth, CurseForge, GitHub releases, or an installer.

## Proposed development sequence

### Phase 1: Integration spike — completed for the read-only bridge

- Confirm the installed Codex CLI can be located and invoked from an ordinary companion process.
- Test ChatGPT-authenticated `codex exec` behavior.
- Test session creation and resume.
- Investigate the current app-server/session-listing interface.
- Investigate cloud-task discovery.
- Document actual event schemas and version behavior.
- Prove trusted-working-directory enforcement.

### Phase 2: Companion prototype — core transport completed

- Implement companion lifecycle.
- Implement local authenticated transport.
- Normalize Codex events.
- Implement basic session inventory.
- Start, resume, stream, and cancel a task.
- Persist reconnectable task state.
- Add structured sanitized logs.

### Phase 3: Minecraft chat interface prototype — in progress

- Create the Fabric 26.1.2 client mod. **Completed.**
- Register local `/codex on`, `/codex off`, and `/codex status` commands. **Completed.**
- Intercept ordinary chat input locally while Codex mode is enabled. **Implemented; multiplayer verification remains.**
- Render complete Codex responses as labeled private client-side chat messages. **Implemented; token streaming remains future work.**
- Add a clear `CODEX MODE` indicator and visually distinct input state. **The hotbar badge is completed; chat-input styling remains.**
- Reset Codex mode on every world or server transition. **Completed in the client state layer.**
- Connect to the companion. **Completed for startup and snapshots.**
- Stream messages without blocking Minecraft's render or game thread. **The asynchronous transport boundary is completed; prompt and response flow remain.**
- Display connection and task errors safely. **Basic status errors are completed; task errors remain.**
- Verify that no prompt or Codex command can reach multiplayer chat.

### Phase 4: Command-driven chat selection

- Implement `/codex chats` with a numbered private chat listing.
- Implement `/codex open <number>`, `/codex new`, `/codex next`, and `/codex previous`.
- Show the active chat, source, status, and working folder when known.
- Preserve the active selection across safe reconnects.
- Keep menus and navigation commands entirely client-side.

### Phase 5: Dedicated workspace and project management

- Implement the dedicated non-pausing workspace screen.
- Add project discovery by working directory.
- Add project selection and trust management.
- List chats by project and source.
- Create, resume, rename, archive, and switch chats.
- Add standalone-chat handling.
- Add cloud-task presentation when supported.

### Phase 6: Resilience and release preparation

- Exercise missing CLI, logout, offline, version mismatch, malformed events, task interruption, Minecraft restart, and companion restart.
- Verify permission boundaries and credential exclusion.
- Add notifications and reduced-functionality mode.
- Package the Windows companion with the Fabric mod distribution.
- Write installation, update, recovery, and troubleshooting documentation.

### Later phase: Advancements

- Define meaningful milestones.
- Implement real Minecraft advancements and popups.
- Store advancement progress without exposing chat contents.
- Keep advancement tracking optional and local.

## First implementation checkpoint

Before building chat selection or the full workspace UI, the project should prove this narrow vertical slice. Companion launch and status connectivity are complete; the remaining items define the chat-mode milestone:

1. Minecraft launches the companion. **Completed.**
2. The companion locates the authenticated Codex installation. **Completed.**
3. The player enables private Codex mode through a local command.
4. The mod intercepts a normal chat message and sends it as a prompt for the configured trusted working directory without transmitting it to the Minecraft server.
5. Codex streams a labeled response back as private client-side Minecraft chat messages.
6. Longer responses are split into readable chat sections and may end with a short plain-language summary without replacing the full response.
7. Disabling Codex mode restores ordinary Minecraft chat behavior.
8. Joining, leaving, or changing a world or server automatically disables Codex mode.
9. The task remains alive when Minecraft chat closes.
10. Minecraft reconnects to the same task after chat is reopened or, when supported by the companion lifecycle, after Minecraft restarts.
11. A missing or incompatible Codex installation produces a recoverable private error rather than a crash.
12. Multiplayer testing proves that Codex prompts, commands, menus, and responses never reach the server.

This vertical slice is the first meaningful definition of success.

## Guidance for future Codex chats

When starting a new Codex chat for this project:

1. Select the cloned `Minecraft Codex` repository as the working folder.
2. Ask Codex to read this `README.md`, `docs/project/PROJECT.md`, and the relevant component file before proposing or making changes.
3. Treat this file as the product baseline and `docs/project/` as the current implementation memory.
4. Record new material decisions in project documentation instead of relying only on chat history.
5. Do not broaden permissions or access credential files without explicit user approval.
6. Continue from the next handoff in `docs/project/components/fabric-mod.md` unless the user chooses a different planning task.

A suitable first prompt is:

> Read `README.md`, `docs/project/PROJECT.md`, and `docs/project/components/fabric-mod.md`. We are continuing Minecraft Codex from the verified Fabric status checkpoint. Preserve the permission and credential boundaries, inspect the current implementation, and continue from the component's next handoff. Do not broaden permissions or begin unrelated phases.
