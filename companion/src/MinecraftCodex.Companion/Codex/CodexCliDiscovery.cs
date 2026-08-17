using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinecraftCodex.Companion.Codex;

public enum CliStatus { Available, Missing, AccessDenied, TimedOut, Failed }
public enum AuthenticationStatus { Authenticated, LoggedOut, Unknown, NotChecked }

public sealed record CliProbeResult(
    CliStatus Status,
    string Source,
    string? ExecutablePath,
    string Message,
    int? ExitCode = null,
    AuthenticationStatus Authentication = AuthenticationStatus.NotChecked);

public sealed class CodexCliDiscovery
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private sealed record Candidate(string Path, string Source, bool TrustedProvenance, string? ExpectedVersion = null);

    public async Task<CliProbeResult> DiscoverAsync(string? explicitPath, CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
            candidates.Add(new(explicitPath, "explicit-unverified", false));
        var npmCandidate = TryLocateOfficialNpmCli();
        if (npmCandidate is not null)
            candidates.Add(npmCandidate);
        candidates.Add(new("codex", "PATH-unverified", false));

        CliProbeResult? lastFailure = null;
        foreach (var candidate in candidates)
        {
            var result = await ProbeAsync(candidate, cancellationToken);
            if (result.Status == CliStatus.Available)
                return result;
            lastFailure = result;
        }

        return lastFailure ?? new(CliStatus.Missing, "none", null, "No Codex CLI candidate was configured.");
    }

    private static async Task<CliProbeResult> ProbeAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        var executable = candidate.Path;
        var source = candidate.Source;
        try
        {
            if (Path.IsPathFullyQualified(executable))
            {
                executable = Path.GetFullPath(executable);
                if (!File.Exists(executable) || Directory.Exists(executable))
                    return new(CliStatus.Missing, source, executable, "Codex CLI candidate is not an executable file.");
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new(CliStatus.TimedOut, source, executable, "Codex CLI version probe timed out.");
            }

            var stdout = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
            var stderr = (await process.StandardError.ReadToEndAsync(cancellationToken)).Trim();
            if (process.ExitCode == 0 && TryParseCodexVersion(stdout, out var version) &&
                candidate.ExpectedVersion is not null && !string.Equals(version, candidate.ExpectedVersion, StringComparison.OrdinalIgnoreCase))
                return new(CliStatus.Failed, source, executable,
                    "Codex CLI version did not match its official package manifest.", process.ExitCode);

            if (process.ExitCode == 0 && TryParseCodexVersion(stdout, out version) && candidate.TrustedProvenance)
            {
                var authentication = await ProbeAuthenticationAsync(executable, cancellationToken);
                return new(CliStatus.Available, source, executable, $"Codex CLI {version}", process.ExitCode, authentication);
            }

            if (process.ExitCode == 0)
                return new(CliStatus.Failed, source, executable,
                    "Executable identity or installation provenance could not be established.", process.ExitCode);

            return new(CliStatus.Failed, source, executable,
                "Codex CLI exited unsuccessfully during its version probe.", process.ExitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return new(CliStatus.Missing, source, executable, "Codex CLI was not found.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return new(CliStatus.AccessDenied, source, executable, "Codex CLI exists but Windows denied process creation.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new(CliStatus.Failed, source, executable, $"Codex CLI could not be started ({ex.GetType().Name}).");
        }
    }

    private static bool TryParseCodexVersion(string value, out string version)
    {
        var match = Regex.Match(value.Trim(), @"^codex(?:-cli)?\s+v?(?<version>\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.-]+)?)$", RegexOptions.IgnoreCase);
        version = match.Success ? match.Groups["version"].Value : string.Empty;
        return match.Success;
    }

    private static Candidate? TryLocateOfficialNpmCli()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
                appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrWhiteSpace(appData))
                return null;

            var packageRoot = Path.Combine(appData, "npm", "node_modules", "@openai", "codex");
            var manifestPath = Path.Combine(packageRoot, "package.json");
            if (!File.Exists(manifestPath))
                return null;

            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = manifest.RootElement;
            if (!root.TryGetProperty("name", out var name) || name.GetString() != "@openai/codex" ||
                !root.TryGetProperty("version", out var versionProperty))
                return null;
            var version = versionProperty.GetString();
            if (string.IsNullOrWhiteSpace(version))
                return null;

            var executable = Path.Combine(packageRoot, "node_modules", "@openai", "codex-win32-x64", "vendor",
                "x86_64-pc-windows-msvc", "bin", "codex.exe");
            return File.Exists(executable) ? new(executable, "official-npm", true, version) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task<AuthenticationStatus> ProbeAuthenticationAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "login status",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return AuthenticationStatus.Unknown;
            }
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            if (process.ExitCode == 0 &&
                (stdout.Contains("Logged in", StringComparison.OrdinalIgnoreCase) ||
                 stderr.Contains("Logged in", StringComparison.OrdinalIgnoreCase)))
                return AuthenticationStatus.Authenticated;
            return process.ExitCode == 0 ? AuthenticationStatus.Unknown : AuthenticationStatus.LoggedOut;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return AuthenticationStatus.Unknown;
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
