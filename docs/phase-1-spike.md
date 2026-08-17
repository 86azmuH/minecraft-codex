# Phase 1 integration spike

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

The Minecraft UI is not yet implemented and will not be treated as a security boundary. All path checks live in the companion.

## Remaining spike gates

1. Expand the normalized event adapter beyond the minimal successful JSONL lifecycle.
2. Probe App Server schemas and cloud-task listing for CLI `0.147.0` without persisting sensitive payloads.
3. Resolve final Windows filesystem targets with handle-based checks and repeat authorization at process launch.
4. Convert capability results into version-aware adapters before adding Minecraft transport.
