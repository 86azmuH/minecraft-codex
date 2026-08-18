using System.Text.Json;
using MinecraftCodex.Companion.Codex;
using MinecraftCodex.Companion.Security;
using MinecraftCodex.Companion.Tasks;

namespace MinecraftCodex.Companion.Server;

public static class CompanionServer
{
    public static async Task<int> RunAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var pipeName = BootstrapIdentity.CurrentUserPipeName();
        using var instanceLease = CompanionInstanceLease.TryAcquire(pipeName);
        if (!instanceLease.IsOwner)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                protocolVersion = 1,
                wsUri = "",
                pipeName,
                alreadyRunning = true
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Console.Out.Flush();
            return 0;
        }

        var policy = TrustedPathPolicy.CreateDefault();
        var decision = policy.EvaluateWorkingDirectory(workingDirectory);
        if (decision.Status != PathStatus.Trusted || decision.CanonicalPath is null)
        {
            Console.Error.WriteLine("Working directory is not trusted.");
            return 1;
        }

        var cli = await new CodexCliDiscovery().DiscoverAsync(null, cancellationToken);
        if (cli.Status != CliStatus.Available || cli.Authentication != AuthenticationStatus.Authenticated ||
            cli.ExecutablePath is null)
        {
            Console.Error.WriteLine("Authenticated official Codex CLI is unavailable.");
            return 1;
        }

        var registry = new TaskRegistry(new CodexExecAdapter(cli.ExecutablePath), policy, decision.CanonicalPath);
        await using var host = new CompanionHost(registry, new CapabilityStore(),
            CompanionHostOptions.Production(pipeName));
        var endpoint = await host.StartAsync(cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(endpoint, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Console.Out.Flush();
        try { await host.WaitForShutdownAsync(cancellationToken); }
        finally { await host.StopAsync(CancellationToken.None); }
        return 0;
    }
}
