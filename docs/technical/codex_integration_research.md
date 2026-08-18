# Phase 1 integration spike (historical checkpoint)

This document preserves the evidence gathered during the initial Codex integration investigation. For current implementation state, read [`../project/PROJECT.md`](../project/PROJECT.md), the component files, and [`companion-bridge.md`](companion-bridge.md).

## Current findings

- The workspace began with only `README.md` and was not a Git repository.
- The Codex desktop package exposes an internal executable under `WindowsApps`, but an ordinary child-process launch receives `Access is denied` in the current environment.
- The desktop package's private executable path is not treated as a supported integration contract.
- The official npm Codex CLI `0.147.0` is installed and authenticated through ChatGPT. The companion discovers its native Windows binary through the `@openai/codex` package manifest instead of trusting the inaccessible desktop-app path.
- Automatic PATH and explicit candidates remain untrusted unless their installation provenance can be established.
- The companion never inspects `auth.json`, browser state, Credential Manager, or equivalent credentials.
- A read-only JSONL execution created thread `01a00dd0-3ebe-7df1-87dc-3f0ab30decf4`; official resume reused the same thread successfully.
- The observed minimal JSONL lifecycle is `thread.started`, `turn.started`, `item.completed`, `turn.completed`.

## Diagnostic harness

Build and run the boundary tests with:

```powershell
.\scripts\build.ps1
```

Run the companion diagnostic with the project-local SDK used for this spike:

```powershell
& "$env:LOCALAPPDATA\MinecraftCodex\dotnet\dotnet.exe" run --project companion/src/MinecraftCodex.Companion -- diagnose --working-directory (Resolve-Path '.').Path --json
```

An explicit CLI path can be tested with `--codex <path>`, but unverified explicit executables are never marked available. Discovery performs a `--version` probe and asks a verified official CLI for `login status`. It does not submit prompts, inspect sessions, or access authentication files directly.

## Security boundary implemented in this checkpoint

- Working directories under the two README-approved roots are automatically trusted.
- Paths outside those roots require confirmation.
- Protected Codex configuration paths require confirmation.
- Known credential paths are denied.
- UNC/device paths and existing paths containing reparse points are denied until a later resolver can prove their final targets remain trusted.

At the time of this spike, the Minecraft UI was not implemented. The later Fabric status client does not alter this security model: all path and Codex-execution checks remain in the companion.

## Outcome and remaining research

Completed after this spike:

- The minimal Codex lifecycle was normalized into task and message events.
- The authenticated named-pipe and WebSocket transport was implemented and tested through both the standalone client and Fabric status client.
- Version 1 request, snapshot, cancellation, sequencing, and reconnection behavior was implemented.

Still open:

1. Probe App Server schemas and cloud-task listing without persisting sensitive payloads.
2. Resolve final Windows filesystem targets with handle-based checks and repeat authorization at process launch before enabling workspace writes.
3. Add version-aware richer Codex adapters while retaining the current read-only `codex exec` fallback.
