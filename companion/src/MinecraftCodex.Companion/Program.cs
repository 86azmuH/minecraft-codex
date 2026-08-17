using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftCodex.Companion.Codex;
using MinecraftCodex.Companion.Security;
using MinecraftCodex.Companion.Server;

if (args.Length > 0 && string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase))
{
    var serveOptions = ServeOptions.Parse(args[1..]);
    if (!serveOptions.IsValid)
    {
        Console.Error.WriteLine("Usage: minecraft-codex-companion serve --working-directory <path>");
        return 2;
    }

    return await CompanionServer.RunAsync(serveOptions.WorkingDirectory!, CancellationToken.None);
}

var options = DiagnosticOptions.Parse(args);
if (!options.IsValid)
{
    Console.Error.WriteLine("Usage: minecraft-codex-companion diagnose [--codex <path>] [--working-directory <path>] [--json]");
    return 2;
}

var policy = TrustedPathPolicy.CreateDefault();
PathDecision? pathDecision = options.WorkingDirectory is null
    ? null
    : policy.EvaluateWorkingDirectory(options.WorkingDirectory);

var discovery = new CodexCliDiscovery();
var cli = await discovery.DiscoverAsync(options.CodexPath, CancellationToken.None);
var report = new DiagnosticReport(
    ProtocolVersion: 1,
    Cli: cli,
    WorkingDirectory: pathDecision,
    CredentialBoundary: "enforced: credential paths are never probed or returned");

if (options.Json)
{
    Console.WriteLine(JsonSerializer.Serialize(report, JsonDefaults.Options));
}
else
{
    Console.WriteLine($"Protocol: {report.ProtocolVersion}");
    Console.WriteLine($"Codex CLI: {cli.Status} ({cli.Source})");
    Console.WriteLine(cli.Message);
    if (pathDecision is not null)
        Console.WriteLine($"Working directory: {pathDecision.Status} - {pathDecision.Reason}");
}

return cli.Status == CliStatus.Available &&
       cli.Authentication == AuthenticationStatus.Authenticated &&
       pathDecision?.Status == PathStatus.Trusted ? 0 : 1;

internal sealed record DiagnosticReport(
    int ProtocolVersion,
    CliProbeResult Cli,
    PathDecision? WorkingDirectory,
    string CredentialBoundary);

internal sealed record ServeOptions(bool IsValid, string? WorkingDirectory)
{
    public static ServeOptions Parse(string[] args) =>
        args.Length == 2 && args[0] == "--working-directory" && !string.IsNullOrWhiteSpace(args[1])
            ? new(true, args[1])
            : new(false, null);
}

internal sealed record DiagnosticOptions(bool IsValid, bool Json, string? CodexPath, string? WorkingDirectory)
{
    public static DiagnosticOptions Parse(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "diagnose", StringComparison.OrdinalIgnoreCase))
            return new(false, false, null, null);

        var json = false;
        string? codexPath = null;
        string? workingDirectory = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--codex" when i + 1 < args.Length:
                    codexPath = args[++i];
                    break;
                case "--working-directory" when i + 1 < args.Length:
                    workingDirectory = args[++i];
                    break;
                default:
                    return new(false, false, null, null);
            }
        }

        return new(true, json, codexPath, workingDirectory);
    }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static JsonDefaults() => Options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
}
