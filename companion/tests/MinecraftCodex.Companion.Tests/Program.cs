using MinecraftCodex.Companion.Security;
using MinecraftCodex.Companion.Codex;
using MinecraftCodex.Companion.Tasks;
using MinecraftCodex.Companion.Server;

var profile = TrustedPathPolicy.ResolveUserProfile();
var trustedRoot = Path.Combine(profile, "SyncHub", "Projects");
var policy = TrustedPathPolicy.CreateDefault();
var failures = new List<string>();

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
var gate = new CapabilityGate("correct-capability", now.AddMinutes(1));
if (gate.TryConsume("wrong-capability", now))
    failures.Add("capability: incorrect secret was accepted");
if (!gate.TryConsume("correct-capability", now))
    failures.Add("capability: valid secret was rejected");
if (gate.TryConsume("correct-capability", now))
    failures.Add("capability: valid secret was reusable");
var expiredGate = new CapabilityGate("expired", now.AddSeconds(-1));
if (expiredGate.TryConsume("expired", now))
    failures.Add("capability: expired secret was accepted");

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
