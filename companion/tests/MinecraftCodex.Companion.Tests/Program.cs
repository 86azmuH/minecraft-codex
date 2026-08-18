using MinecraftCodex.Companion.Security;
using MinecraftCodex.Companion.Codex;
using MinecraftCodex.Companion.Tasks;
using MinecraftCodex.Companion.Server;
using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

var profile = TrustedPathPolicy.ResolveUserProfile();
var trustedRoot = Path.Combine(profile, "SyncHub", "Projects");
var policy = TrustedPathPolicy.CreateDefault();
var failures = new List<string>();

var encodedStartInfo = CodexProcessEncoding.Apply(new ProcessStartInfo
{
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true
});
if (encodedStartInfo.StandardInputEncoding?.CodePage != Encoding.UTF8.CodePage ||
    encodedStartInfo.StandardOutputEncoding?.CodePage != Encoding.UTF8.CodePage ||
    encodedStartInfo.StandardErrorEncoding?.CodePage != Encoding.UTF8.CodePage)
    failures.Add("Codex process encoding: redirected streams were not configured as UTF-8");
var curlyText = "I’m ready — what’s next?";
if (encodedStartInfo.StandardOutputEncoding?.GetString(Encoding.UTF8.GetBytes(curlyText)) != curlyText)
    failures.Add("Codex process encoding: UTF-8 punctuation was not preserved");
var outputOnlyStartInfo = CodexProcessEncoding.Apply(new ProcessStartInfo
{
    RedirectStandardOutput = true,
    RedirectStandardError = true
});
if (outputOnlyStartInfo.StandardInputEncoding is not null ||
    outputOnlyStartInfo.StandardOutputEncoding?.CodePage != Encoding.UTF8.CodePage ||
    outputOnlyStartInfo.StandardErrorEncoding?.CodePage != Encoding.UTF8.CodePage)
    failures.Add("Codex process encoding: non-redirected input was incorrectly configured");

var leasePipeName = $"minecraft-codex-lease-test-{Guid.NewGuid():N}";
var leaseRuntimeDirectory = Path.Combine(Path.GetTempPath(), $"minecraft-codex-lease-tests-{Guid.NewGuid():N}");
using (var firstLease = CompanionInstanceLease.TryAcquire(leasePipeName, leaseRuntimeDirectory))
using (var secondLease = CompanionInstanceLease.TryAcquire(leasePipeName, leaseRuntimeDirectory))
{
    if (!firstLease.IsOwner || secondLease.IsOwner)
        failures.Add("instance lease: simultaneous duplicate owner was allowed");
}
using (var replacementLease = CompanionInstanceLease.TryAcquire(leasePipeName, leaseRuntimeDirectory))
{
    if (!replacementLease.IsOwner)
        failures.Add("instance lease: ownership was not released for replacement");
}
try
{
    var leaseFile = Path.Combine(leaseRuntimeDirectory, $"{leasePipeName}.lock");
    if (File.Exists(leaseFile)) File.Delete(leaseFile);
    if (Directory.Exists(leaseRuntimeDirectory)) Directory.Delete(leaseRuntimeDirectory);
}
catch (Exception ex)
{
    failures.Add($"instance lease: test runtime cleanup failed ({ex.GetType().Name})");
}
if (Directory.Exists(leaseRuntimeDirectory))
    failures.Add("instance lease: test runtime directory was retained");

if (OperatingSystem.IsWindows())
{
    var identityPipeName = $"minecraft-codex-identity-test-{Guid.NewGuid():N}";
    await using var identityServer = new NamedPipeServerStream(identityPipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    var identityWait = identityServer.WaitForConnectionAsync();
    await using var identityClient = new NamedPipeClientStream(".", identityPipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous);
    await identityClient.ConnectAsync();
    await identityWait;
    var currentExecutable = Environment.ProcessPath ??
                            throw new InvalidOperationException("Test process executable path is unavailable.");
    try { NamedPipeServerIdentity.VerifyExecutable(identityClient, currentExecutable); }
    catch (Exception ex) { failures.Add($"pipe identity: expected server was rejected ({ex.Message})"); }
    try
    {
        NamedPipeServerIdentity.VerifyExecutable(identityClient, Path.Combine(Path.GetTempPath(), "not-the-server.exe"));
        failures.Add("pipe identity: unexpected server executable was accepted");
    }
    catch (InvalidOperationException) { }
}

Expect("trusted root", policy.EvaluateWorkingDirectory(trustedRoot), PathStatus.Trusted);
Expect("trusted child", policy.EvaluateWorkingDirectory(Path.Combine(trustedRoot, "Minecraft Codex")), PathStatus.Trusted);
Expect("sibling prefix", policy.EvaluateWorkingDirectory(trustedRoot + "-malicious"), PathStatus.Denied);
Expect("parent traversal", policy.EvaluateWorkingDirectory(Path.Combine(trustedRoot, "..", "CodexSettings")), PathStatus.ConfirmationRequired);
Expect("protected config", policy.EvaluateWorkingDirectory(Path.Combine(profile, ".codex", "config.toml")), PathStatus.ConfirmationRequired);
Expect("credential file", policy.EvaluateWorkingDirectory(Path.Combine(profile, ".codex", "auth.json")), PathStatus.Denied);
Expect("credential parent", policy.EvaluateWorkingDirectory(Path.Combine(profile, ".codex")), PathStatus.Denied);
Expect("profile contains credentials", policy.EvaluateWorkingDirectory(profile), PathStatus.Denied);
Expect("sandbox secrets", policy.EvaluateWorkingDirectory(Path.Combine(profile, ".sandbox-secrets")), PathStatus.Denied);
Expect("nonexistent trusted child", policy.EvaluateWorkingDirectory(Path.Combine(trustedRoot, $"missing-{Guid.NewGuid():N}")), PathStatus.Denied);
Expect("UNC path", policy.EvaluateWorkingDirectory(@"\\server\share"), PathStatus.Denied);
Expect("device path", policy.EvaluateWorkingDirectory(@"\\?\C:\safe"), PathStatus.Denied);

var terminalRun = new TaskRun("terminal-test", _ => { });
terminalRun.Started("thread-test");
terminalRun.Message("hello");
terminalRun.Complete();
terminalRun.Fail("must-not-replace-completion");
terminalRun.Complete();
var terminalSnapshot = terminalRun.Snapshot();
if (terminalSnapshot.State != CompanionTaskState.Completed)
    failures.Add($"terminal state: expected Completed, received {terminalSnapshot.State}");
if (terminalSnapshot.Events.Count(item => item.Type is "task.completed" or "task.failed") != 1)
    failures.Add("terminal state: expected exactly one terminal event");

var fakeAdapter = new FakeAdapter();
var registry = new TaskRegistry(fakeAdapter, policy, trustedRoot);
var first = registry.Start("same-request", new("first prompt", null));
var duplicate = registry.Start("same-request", new("first prompt", null));
var mismatch = registry.Start("same-request", new("different prompt", null));
SpinWait.SpinUntil(() => fakeAdapter.ExecutionCount == 1, TimeSpan.FromSeconds(2));
if (!first.Accepted || !duplicate.Accepted || first.TaskId != duplicate.TaskId || fakeAdapter.ExecutionCount != 1)
    failures.Add("idempotency: identical request did not resolve to one task execution");
if (mismatch.Accepted)
    failures.Add("idempotency: mismatched request content was accepted");
var registrySnapshot = registry.Snapshot().SingleOrDefault(item => item.TaskId == first.TaskId);
if (registrySnapshot?.State != CompanionTaskState.Completed)
    failures.Add("snapshot: completed fake task state was not retained");

var now = DateTimeOffset.UtcNow;
var capabilities = new CapabilityStore();
var validCapability = capabilities.Mint(now, TimeSpan.FromMinutes(1)) ?? throw new InvalidOperationException("Capability test store was unexpectedly full.");
if (capabilities.TryConsume("wrong-capability", now))
    failures.Add("capability: incorrect secret was accepted");
if (!capabilities.TryConsume(validCapability, now))
    failures.Add("capability: valid secret was rejected");
if (capabilities.TryConsume(validCapability, now))
    failures.Add("capability: valid secret was reusable");
var expiredCapability = capabilities.Mint(now.AddMinutes(-2), TimeSpan.FromMinutes(1)) ?? throw new InvalidOperationException("Capability test store was unexpectedly full.");
if (capabilities.TryConsume(expiredCapability, now))
    failures.Add("capability: expired secret was accepted");

var brokerStore = new CapabilityStore();
var brokerPipeName = $"minecraft-codex-test-{Guid.NewGuid():N}";
var broker = new BootstrapBroker(brokerPipeName, brokerStore, "ws://127.0.0.1:12345/v1/ws");
using var brokerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var brokerTask = broker.RunAsync(brokerCancellation.Token);
var invalidBrokerResponse = await RequestBrokerAsync(brokerPipeName, "{\"protocolVersion\":2,\"type\":\"capability.request\"}", brokerCancellation.Token);
var firstBrokerCapability = await RequestBrokerAsync(brokerPipeName, "{\"protocolVersion\":1,\"type\":\"capability.request\"}", brokerCancellation.Token);
var secondBrokerCapability = await RequestBrokerAsync(brokerPipeName, "{\"protocolVersion\":1,\"type\":\"capability.request\"}", brokerCancellation.Token);
if (invalidBrokerResponse.Capability is not null || invalidBrokerResponse.Error != "invalid request")
    failures.Add("bootstrap broker: invalid protocol request was accepted");
if (string.IsNullOrWhiteSpace(firstBrokerCapability.Capability) ||
    string.IsNullOrWhiteSpace(secondBrokerCapability.Capability) ||
    firstBrokerCapability.Capability == secondBrokerCapability.Capability)
    failures.Add("bootstrap broker: fresh capabilities were not minted");
else if (!brokerStore.TryConsume(firstBrokerCapability.Capability, DateTimeOffset.UtcNow) ||
         !brokerStore.TryConsume(secondBrokerCapability.Capability, DateTimeOffset.UtcNow))
    failures.Add("bootstrap broker: minted capabilities could not be consumed");

await using (var stalledPipe = new NamedPipeClientStream(".", brokerPipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
{
    await stalledPipe.ConnectAsync(brokerCancellation.Token);
    var afterStall = await RequestBrokerAsync(brokerPipeName,
        "{\"protocolVersion\":1,\"type\":\"capability.request\"}", brokerCancellation.Token);
    if (string.IsNullOrWhiteSpace(afterStall.Capability))
        failures.Add("bootstrap broker: stalled client prevented a later capability request");
}

var boundedStore = new CapabilityStore();
var minted = Enumerable.Range(0, 16).Select(_ => boundedStore.Mint(now, TimeSpan.FromMinutes(1))).ToArray();
if (minted.Any(string.IsNullOrWhiteSpace) || boundedStore.Mint(now, TimeSpan.FromMinutes(1)) is not null)
    failures.Add("capability: outstanding capability bound was not enforced");
brokerCancellation.Cancel();
try { await brokerTask; } catch (OperationCanceledException) { }

await BridgeServerTests.RunAsync(policy, trustedRoot, failures);

if (failures.Count == 0)
{
    Console.WriteLine("All companion policy, lifecycle, idempotency, and snapshot tests passed.");
    return 0;
}

foreach (var failure in failures)
    Console.Error.WriteLine(failure);
return 1;

void Expect(string name, PathDecision actual, PathStatus expected)
{
    if (actual.Status != expected)
        failures.Add($"{name}: expected {expected}, received {actual.Status} ({actual.Reason})");
}

static async Task<BrokerResponse> RequestBrokerAsync(string pipeName, string request,
    CancellationToken cancellationToken)
{
    await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(cancellationToken);
    using var reader = new StreamReader(pipe, leaveOpen: true);
    await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    await writer.WriteLineAsync(request);
    var response = await reader.ReadLineAsync(cancellationToken);
    return response is null
        ? new(0, null, "missing response")
        : JsonSerializer.Deserialize<BrokerResponse>(response, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}

sealed class FakeAdapter : ICodexTaskAdapter
{
    private int executionCount;
    public int ExecutionCount => executionCount;

    public Task RunAsync(TaskRun run, string prompt, string workingDirectory, TrustedPathPolicy pathPolicy,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref executionCount);
        run.Started("fake-thread");
        run.Message("fake-response");
        run.Complete();
        return Task.CompletedTask;
    }
}

sealed record BrokerResponse(int ProtocolVersion, string? Capability, string? Error);
