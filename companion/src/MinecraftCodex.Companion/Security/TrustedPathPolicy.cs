namespace MinecraftCodex.Companion.Security;

public enum PathStatus { Trusted, ConfirmationRequired, Denied }

public sealed record PathDecision(PathStatus Status, string? CanonicalPath, string Reason);

public sealed class TrustedPathPolicy
{
    private readonly string[] trustedRoots;
    private readonly string[] protectedConfigurationPaths;
    private readonly string[] deniedCredentialPaths;

    public TrustedPathPolicy(
        IEnumerable<string> trustedRoots,
        IEnumerable<string> protectedConfigurationPaths,
        IEnumerable<string> deniedCredentialPaths)
    {
        this.trustedRoots = trustedRoots.Select(CanonicalizeConfiguredPath).ToArray();
        this.protectedConfigurationPaths = protectedConfigurationPaths.Select(CanonicalizeConfiguredPath).ToArray();
        this.deniedCredentialPaths = deniedCredentialPaths.Select(CanonicalizeConfiguredPath).ToArray();
    }

    public static TrustedPathPolicy CreateDefault()
    {
        var profile = ResolveUserProfile();
        return new TrustedPathPolicy(
            [
                Path.Combine(profile, "Documents", "Codex"),
                Path.Combine(profile, "SyncHub", "Projects")
            ],
            [
                Path.Combine(profile, "SyncHub", "CodexSettings"),
                Path.Combine(profile, ".codex", "config.toml")
            ],
            [
                Path.Combine(profile, ".codex", "auth.json"),
                Path.Combine(profile, ".sandbox-secrets")
            ]);
    }

    public static string ResolveUserProfile()
    {
        var environmentProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(environmentProfile) && Path.IsPathFullyQualified(environmentProfile))
            return Path.GetFullPath(environmentProfile);

        var specialFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(specialFolder) && Path.IsPathFullyQualified(specialFolder))
            return Path.GetFullPath(specialFolder);

        throw new InvalidOperationException("The Windows user profile directory could not be resolved safely.");
    }

    public PathDecision EvaluateWorkingDirectory(string input)
    {
        string canonical;
        try
        {
            canonical = Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(PathStatus.Denied, null, "invalid path");
        }

        if (!Path.IsPathFullyQualified(canonical) || IsDevicePath(canonical))
            return new(PathStatus.Denied, null, "path must be a normal, fully qualified local path");

        if (deniedCredentialPaths.Any(path => IsSameOrChild(canonical, path) || IsSameOrChild(path, canonical)))
            return new(PathStatus.Denied, canonical, "credential locations and workspaces containing them are never accessible");

        if (protectedConfigurationPaths.Any(path => IsSameOrChild(canonical, path) || IsSameOrChild(path, canonical)))
            return new(PathStatus.ConfirmationRequired, canonical, "protected Codex configuration requires explicit confirmation");

        if (!Directory.Exists(canonical))
            return new(PathStatus.Denied, canonical, "working directory must already exist");

        if (!trustedRoots.Any(root => IsSameOrChild(canonical, root)))
            return new(PathStatus.ConfirmationRequired, canonical, "outside automatically trusted roots");

        if (HasReparsePointInExistingPath(canonical))
            return new(PathStatus.Denied, canonical, "reparse points require explicit resolution before trust can be established");

        return new(PathStatus.Trusted, canonical, "inside an automatically trusted root");
    }

    private static string CanonicalizeConfiguredPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSameOrChild(string candidate, string root) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsDevicePath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
        path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
        path.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool HasReparsePointInExistingPath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is null)
            return true;

        var current = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
                break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }

        return false;
    }
}
