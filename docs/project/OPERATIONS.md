# Running and Maintaining the Project

## Requirements

- Windows.
- Official Codex CLI installed through `@openai/codex` and logged in with ChatGPT.
- Project-specific .NET 8 SDK under `%LOCALAPPDATA%\MinecraftCodex\dotnet`.
- Java 25 for Fabric development and Minecraft 26.1.2.
- For in-game testing: Minecraft 26.1.2, Fabric Loader 0.19.3 or newer, and Fabric API `0.155.2+26.1.2`.

## Initial setup

The current development machine already has the required CLI and project-specific SDK. No general installer exists yet.

## Build and run automated tests

From the project root:

```powershell
.\scripts\build.ps1
```

**Expected result:** All projects build with zero warnings or errors and the companion policy plus real-server authentication, identity, duplicate lease, broker collision, protocol, idempotency, reconnect, cancellation, snapshot, idle lifecycle, and shutdown tests pass.

## Run the live reconnect test

```powershell
.\scripts\test-companion.ps1
```

**Expected result:** A duplicate companion process safely reports the existing instance, a read-only Codex task returns `COMPANION_BRIDGE_OK`, and a distinct second client process prints a `task.snapshot` containing the same completed task and ordered events.

Test another harmless prompt:

```powershell
.\scripts\test-companion.ps1 -Prompt "Explain a Minecraft chunk in one sentence."
```

## Build and test the Fabric mod

Minecraft 26.1.2 requires Java 25. Set `JAVA_HOME` to a JDK 25 installation, then run from `mod/`:

```powershell
.\gradlew.bat test build
```

The first run downloads Gradle, Minecraft, Fabric, JNA, and test dependencies. The deployable jar is `mod/build/libs/minecraft-codex-0.1.0-SNAPSHOT.jar`; the `-sources.jar` beside it is for source browsing and is not installed as a mod.

The real Java-to-companion test is intentionally opt-in. From `mod/`, supply the built companion and a trusted working directory:

```powershell
.\gradlew.bat test `
  '-Dminecraftcodex.integration=true' `
  '-Dminecraftcodex.companion.executable=C:\path\to\MinecraftCodex.Companion.exe' `
  '-Dminecraftcodex.workingDirectory=C:\path\to\trusted-project' `
  --tests '*CompanionConnectionIntegrationTest'
```

## Test the built mod with Prism Launcher

Use a Minecraft 26.1.2 instance with Fabric Loader 0.19.3 or newer and Fabric API compatible with 26.1.2. Add `mod/build/libs/minecraft-codex-0.1.0-SNAPSHOT.jar` to the instance's mods.

In **Edit instance → Settings → Java**, enable the per-instance Java override and select a Java 25 `javaw.exe`. Enable the Java-arguments override and use:

```text
--enable-native-access=ALL-UNNAMED "-Dminecraftcodex.companion.executable=C:/absolute/path/to/MinecraftCodex.Companion.exe" "-Dminecraftcodex.workingDirectory=C:/absolute/path/to/trusted-project"
```

Use forward slashes inside Prism JVM arguments, even on Windows. Backslashes are interpreted as escape characters and are removed from the value. After launching and entering a world, run:

```text
/codex status
```

**Expected status result:** The private client chat first shows `Checking companion…`, then `Companion connected (protocol 1). Mode: disabled.`

Run `/codex on`. After the connection succeeds, private chat should show `Codex mode enabled` and the `[ CODEX ]` badge should appear beside the hotbar. Type a harmless ordinary message such as `Reply with one short sentence confirming this works.` It should appear privately as `You: ...`, immediately followed by `Codex: Thinking…`, then one or more private `Codex: ...` response messages; it must not appear in server chat. A second message sent while Codex is answering should be blocked locally. Slash commands remain normal. Run `/codex off` to cancel an accepted active task and remove the badge. Leaving or changing the world/server should also disable the mode silently.

For the multiplayer privacy check, use a harmless unmistakable prompt, then confirm with another player or the server console that no prompt or response was received. Repeat once with Codex mode disabled and confirm ordinary chat is sent normally.

## Deploy or release

Deployment and packaging are not configured.

## Routine maintenance

- Rerun both scripts after companion security, protocol, process, or path-policy changes.
- Rerun the Gradle build and opt-in integration test after Fabric transport or protocol changes.
- Update the documented Codex CLI capability assumptions after CLI upgrades.

## Troubleshooting and recovery

### Authenticated official Codex CLI is unavailable

- **Likely cause:** The standalone npm CLI is missing, outdated, logged out, or inaccessible from the current process.
- **Check:** Run `codex login status` through the official standalone CLI.
- **Resolution:** Complete official Codex login; never inspect or copy `auth.json`.
- **Recovery or rollback:** The diagnostic command remains available and does not modify credentials.

### The app reports a missing ASP.NET Core runtime

- **Likely cause:** The executable did not inherit the project-specific `DOTNET_ROOT`.
- **Check:** Confirm `%LOCALAPPDATA%\MinecraftCodex\dotnet` exists.
- **Resolution:** Use the supplied PowerShell scripts, which set the correct runtime location.
- **Recovery or rollback:** No project data is affected.

### `/codex status` says the configured companion executable was not found

- **Likely cause:** The JVM property is missing, points to an old build, or Prism removed backslashes as escape characters.
- **Check:** Confirm the executable exists and inspect Prism's launch command for the resolved `minecraftcodex.companion.executable` value.
- **Resolution:** Quote the complete `-D...` argument and use forward slashes in its Windows path, as shown above.
- **Recovery or rollback:** This changes launcher configuration only; no project or Minecraft data is affected.
