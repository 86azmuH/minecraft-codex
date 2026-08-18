using System.IO.Pipes;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MinecraftCodex.Companion.Codex;
using MinecraftCodex.Companion.Protocol;
using MinecraftCodex.Companion.Security;
using MinecraftCodex.Companion.Server;
using MinecraftCodex.Companion.Tasks;

public static class BridgeServerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(TrustedPathPolicy policy, string trustedRoot, List<string> failures)
    {
        await RunAuthenticationAndProtocolTestsAsync(policy, trustedRoot, failures);
        await RunActiveReconnectAndCancellationTestsAsync(policy, trustedRoot, failures);
        await RunActiveShutdownTestAsync(policy, trustedRoot, failures);
        await RunIdleShutdownTestsAsync(policy, trustedRoot, failures);
        await RunBrokerCollisionTestAsync(policy, trustedRoot, failures);
        await RunExpiredCapabilityTestAsync(policy, trustedRoot, failures);
    }

    private static async Task RunAuthenticationAndProtocolTestsAsync(TrustedPathPolicy policy, string trustedRoot,
        List<string> failures)
    {
        var pipeName = $"minecraft-codex-host-test-{Guid.NewGuid():N}";
        var adapter = new ImmediateServerAdapter();
        var registry = new TaskRegistry(adapter, policy, trustedRoot);
        await using var host = new CompanionHost(registry, new CapabilityStore(),
            new(pipeName, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var endpoint = await host.StartAsync(timeout.Token);

        if (!await ConnectRejectedAsync(endpoint.WsUri, null, timeout.Token))
            failures.Add("server auth: missing capability was accepted");
        if (!await ConnectRejectedAsync(endpoint.WsUri, "wrong-capability", timeout.Token))
            failures.Add("server auth: wrong capability was accepted");

        var first = await RequestCapabilityAsync(pipeName, timeout.Token);
        using (var socket = await ConnectAsync(first, timeout.Token))
            _ = await ReceiveAsync(socket, timeout.Token);
        if (!await ConnectRejectedAsync(first.WsUri!, first.Capability, timeout.Token))
            failures.Add("server auth: consumed capability was replayable");

        var protocolCapability = await RequestCapabilityAsync(pipeName, timeout.Token);
        using (var socket = await ConnectAsync(protocolCapability, timeout.Token))
        {
            _ = await ReceiveAsync(socket, timeout.Token);
            await SendAsync(socket, new { version = 2, type = "unknown", requestId = "bad-version", payload = new { } }, timeout.Token);
            var rejectedVersion = await ReceiveUntilAsync(socket,
                message => message.Type == "request.rejected" && message.RequestId == "bad-version", timeout.Token);
            if (rejectedVersion is null) failures.Add("server protocol: unsupported version was not rejected");

            await SendAsync(socket, new { version = 1, type = "unknown", requestId = "unknown-type", payload = new { } }, timeout.Token);
            var rejectedType = await ReceiveUntilAsync(socket,
                message => message.Type == "request.rejected" && message.RequestId == "unknown-type", timeout.Token);
            if (rejectedType is null) failures.Add("server protocol: unknown request type was not rejected");

            await socket.SendAsync(Encoding.UTF8.GetBytes("{not-json"), WebSocketMessageType.Text, true, timeout.Token);
            var malformed = await ReceiveUntilAsync(socket, message => message.Type == "request.rejected", timeout.Token);
            if (malformed is null) failures.Add("server protocol: malformed JSON was not rejected");
        }

        var oversizedCapability = await RequestCapabilityAsync(pipeName, timeout.Token);
        using (var socket = await ConnectAsync(oversizedCapability, timeout.Token))
        {
            _ = await ReceiveAsync(socket, timeout.Token);
            await socket.SendAsync(new byte[65 * 1024], WebSocketMessageType.Text, true, timeout.Token);
            var closed = await ReceiveAsync(socket, timeout.Token);
            if (closed?.Type != "__closed") failures.Add("server protocol: oversized message did not close the socket");
        }

        var healthyCapability = await RequestCapabilityAsync(pipeName, timeout.Token);
        using (var socket = await ConnectAsync(healthyCapability, timeout.Token))
        {
            var hello = await ReceiveAsync(socket, timeout.Token);
            if (hello?.Type != "server.hello") failures.Add("server protocol: host was unhealthy after invalid traffic");

            var request = new { version = 1, type = "task.start", requestId = "network-idempotent", payload = new { prompt = "safe", workingDirectory = (string?)null } };
            await SendAsync(socket, request, timeout.Token);
            var accepted = await ReceiveUntilAsync(socket,
                message => message.Type == "request.accepted" && message.RequestId == "network-idempotent", timeout.Token);
            var firstTaskId = accepted?.Payload.TryGetProperty("taskId", out var firstId) == true ? firstId.GetString() : null;
            await SendAsync(socket, request, timeout.Token);
            var duplicate = await ReceiveUntilAsync(socket,
                message => message.Type == "request.accepted" && message.RequestId == "network-idempotent", timeout.Token);
            var duplicateTaskId = duplicate?.Payload.TryGetProperty("taskId", out var duplicateId) == true ? duplicateId.GetString() : null;
            if (firstTaskId is null || firstTaskId != duplicateTaskId || adapter.ExecutionCount != 1)
                failures.Add("server idempotency: identical request did not resolve to one execution");

            await SendAsync(socket, new { version = 1, type = "task.start", requestId = "network-idempotent", payload = new { prompt = "changed", workingDirectory = (string?)null } }, timeout.Token);
            var mismatch = await ReceiveUntilAsync(socket,
                message => message.Type == "request.rejected" && message.RequestId == "network-idempotent", timeout.Token);
            if (mismatch is null) failures.Add("server idempotency: changed content with reused ID was accepted");

            await SendAsync(socket, new { version = 1, type = "task.start", requestId = "unsafe-path", payload = new { prompt = "safe", workingDirectory = @"C:\Windows" } }, timeout.Token);
            var unsafePath = await ReceiveUntilAsync(socket,
                message => message.Type == "request.rejected" && message.RequestId == "unsafe-path", timeout.Token);
            if (unsafePath is null) failures.Add("server path: untrusted working directory was accepted");
        }

        await host.StopAsync(timeout.Token);
    }

    private static async Task RunActiveReconnectAndCancellationTestsAsync(TrustedPathPolicy policy, string trustedRoot,
        List<string> failures)
    {
        var pipeName = $"minecraft-codex-active-test-{Guid.NewGuid():N}";
        var adapter = new BlockingServerAdapter();
        var registry = new TaskRegistry(adapter, policy, trustedRoot);
        await using var host = new CompanionHost(registry, new CapabilityStore(),
            new(pipeName, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = await host.StartAsync(timeout.Token);

        string? taskId;
        var first = await RequestCapabilityAsync(pipeName, timeout.Token);
        using (var socket = await ConnectAsync(first, timeout.Token))
        {
            _ = await ReceiveAsync(socket, timeout.Token);
            await SendAsync(socket, new { version = 1, type = "task.start", requestId = "active-task", payload = new { prompt = "block", workingDirectory = (string?)null } }, timeout.Token);
            var accepted = await ReceiveUntilAsync(socket,
                message => message.Type == "request.accepted" && message.RequestId == "active-task", timeout.Token);
            taskId = accepted?.Payload.TryGetProperty("taskId", out var id) == true ? id.GetString() : null;
            _ = await ReceiveUntilAsync(socket, message => message.Type == "task.started", timeout.Token);
        }

        var second = await RequestCapabilityAsync(pipeName, timeout.Token);
        using (var reconnect = await ConnectAsync(second, timeout.Token))
        {
            _ = await ReceiveAsync(reconnect, timeout.Token);
            await SendAsync(reconnect, new { version = 1, type = "task.snapshot", requestId = "active-snapshot", payload = new { } }, timeout.Token);
            var snapshot = await ReceiveUntilAsync(reconnect,
                message => message.Type == "task.snapshot" && message.RequestId == "active-snapshot", timeout.Token);
            var runningTask = snapshot is null || taskId is null ? null : FindTask(snapshot.Payload, taskId);
            if (runningTask is null || runningTask.Value.GetProperty("state").GetString() != "running")
                failures.Add("server reconnect: active task was not retained as running");

            await SendAsync(reconnect, new { version = 1, type = "task.cancel", requestId = "cancel-active", payload = new { taskId } }, timeout.Token);
            var cancelAccepted = await ReceiveUntilAsync(reconnect,
                message => message.Type == "request.accepted" && message.RequestId == "cancel-active", timeout.Token);
            var cancelledEvent = await ReceiveUntilAsync(reconnect, message => message.Type == "task.failed", timeout.Token);
            if (cancelAccepted is null || cancelledEvent is null || !adapter.CancellationObserved)
                failures.Add("server cancellation: active task was not cancelled cleanly");

            await SendAsync(reconnect, new { version = 1, type = "task.snapshot", requestId = "cancelled-snapshot", payload = new { } }, timeout.Token);
            var cancelledSnapshot = await ReceiveUntilAsync(reconnect,
                message => message.Type == "task.snapshot" && message.RequestId == "cancelled-snapshot", timeout.Token);
            var cancelledTask = cancelledSnapshot is null || taskId is null ? null : FindTask(cancelledSnapshot.Payload, taskId);
            if (cancelledTask is null || cancelledTask.Value.GetProperty("state").GetString() != "cancelled")
                failures.Add("server cancellation: authoritative snapshot was not Cancelled");
        }

        await host.StopAsync(timeout.Token);

        var replacementRegistry = new TaskRegistry(new ImmediateServerAdapter(), policy, trustedRoot);
        await using var replacement = new CompanionHost(replacementRegistry, new CapabilityStore(),
            new(pipeName, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)));
        _ = await replacement.StartAsync(timeout.Token);
        await replacement.StopAsync(timeout.Token);
    }

    private static async Task RunExpiredCapabilityTestAsync(TrustedPathPolicy policy, string trustedRoot,
        List<string> failures)
    {
        var pipeName = $"minecraft-codex-expiry-test-{Guid.NewGuid():N}";
        var registry = new TaskRegistry(new ImmediateServerAdapter(), policy, trustedRoot);
        await using var host = new CompanionHost(registry, new CapabilityStore(),
            new(pipeName, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = await host.StartAsync(timeout.Token);
        var capability = await RequestCapabilityAsync(pipeName, timeout.Token);
        await Task.Delay(100, timeout.Token);
        if (!await ConnectRejectedAsync(capability.WsUri!, capability.Capability, timeout.Token))
            failures.Add("server auth: expired capability was accepted by WebSocket endpoint");
        await host.StopAsync(timeout.Token);
    }

    private static async Task RunActiveShutdownTestAsync(TrustedPathPolicy policy, string trustedRoot,
        List<string> failures)
    {
        var pipeName = $"minecraft-codex-shutdown-test-{Guid.NewGuid():N}";
        var adapter = new BlockingServerAdapter();
        var registry = new TaskRegistry(adapter, policy, trustedRoot);
        await using var host = new CompanionHost(registry, new CapabilityStore(),
            new(pipeName, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = await host.StartAsync(timeout.Token);

        var capability = await RequestCapabilityAsync(pipeName, timeout.Token);
        using (var socket = await ConnectAsync(capability, timeout.Token))
        {
            _ = await ReceiveAsync(socket, timeout.Token);
            await SendAsync(socket, new { version = 1, type = "task.start", requestId = "shutdown-active", payload = new { prompt = "block", workingDirectory = (string?)null } }, timeout.Token);
            _ = await ReceiveUntilAsync(socket, message => message.Type == "task.started", timeout.Token);
        }

        await host.StopAsync(timeout.Token);
        await host.StopAsync(timeout.Token);
        if (!adapter.CancellationObserved)
            failures.Add("server shutdown: active adapter did not observe cancellation");

        var replacementRegistry = new TaskRegistry(new ImmediateServerAdapter(), policy, trustedRoot);
        await using var replacement = new CompanionHost(replacementRegistry, new CapabilityStore(),
            new(pipeName, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)));
        _ = await replacement.StartAsync(timeout.Token);
        await replacement.StopAsync(timeout.Token);
    }

    private static async Task RunIdleShutdownTestsAsync(TrustedPathPolicy policy, string trustedRoot,
        List<string> failures)
    {
        var idleGrace = TimeSpan.FromMilliseconds(200);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using (var idleHost = new CompanionHost(
                         new TaskRegistry(new ImmediateServerAdapter(), policy, trustedRoot),
                         new CapabilityStore(),
                         new($"minecraft-codex-idle-empty-{Guid.NewGuid():N}", TimeSpan.FromMinutes(1),
                             TimeSpan.FromSeconds(1), idleGrace)))
        {
            _ = await idleHost.StartAsync(timeout.Token);
            await idleHost.WaitForShutdownAsync(timeout.Token);
            await idleHost.StopAsync(timeout.Token);
        }

        var connectedPipe = $"minecraft-codex-idle-client-{Guid.NewGuid():N}";
        await using (var connectedHost = new CompanionHost(
                         new TaskRegistry(new ImmediateServerAdapter(), policy, trustedRoot),
                         new CapabilityStore(),
                         new(connectedPipe, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), idleGrace)))
        {
            _ = await connectedHost.StartAsync(timeout.Token);
            var capability = await RequestCapabilityAsync(connectedPipe, timeout.Token);
            using (var socket = await ConnectAsync(capability, timeout.Token))
            {
                _ = await ReceiveAsync(socket, timeout.Token);
                await Task.Delay(idleGrace + idleGrace, timeout.Token);
                var premature = connectedHost.WaitForShutdownAsync(CancellationToken.None);
                if (premature.IsCompleted)
                    failures.Add("idle shutdown: connected client did not keep host alive");
            }
            await connectedHost.WaitForShutdownAsync(timeout.Token);
            await connectedHost.StopAsync(timeout.Token);
        }

        var activePipe = $"minecraft-codex-idle-active-{Guid.NewGuid():N}";
        var completable = new CompletableServerAdapter();
        await using (var activeHost = new CompanionHost(
                         new TaskRegistry(completable, policy, trustedRoot), new CapabilityStore(),
                         new(activePipe, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), idleGrace)))
        {
            _ = await activeHost.StartAsync(timeout.Token);
            var capability = await RequestCapabilityAsync(activePipe, timeout.Token);
            using (var socket = await ConnectAsync(capability, timeout.Token))
            {
                _ = await ReceiveAsync(socket, timeout.Token);
                await SendAsync(socket, new { version = 1, type = "task.start", requestId = "idle-active", payload = new { prompt = "block", workingDirectory = (string?)null } }, timeout.Token);
                _ = await ReceiveUntilAsync(socket, message => message.Type == "task.started", timeout.Token);
            }
            await Task.Delay(idleGrace + idleGrace, timeout.Token);
            if (activeHost.WaitForShutdownAsync(CancellationToken.None).IsCompleted)
                failures.Add("idle shutdown: disconnected active task did not keep host alive");
            completable.Release();
            await activeHost.WaitForShutdownAsync(timeout.Token);
            await activeHost.StopAsync(timeout.Token);
        }
    }

    private static async Task RunBrokerCollisionTestAsync(TrustedPathPolicy policy, string trustedRoot,
        List<string> failures)
    {
        var pipeName = $"minecraft-codex-collision-{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var imposter = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                         PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        await using (var collided = new CompanionHost(
                         new TaskRegistry(new ImmediateServerAdapter(), policy, trustedRoot),
                         new CapabilityStore(), new(pipeName, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1))))
        {
            try
            {
                _ = await collided.StartAsync(timeout.Token);
                failures.Add("broker startup: host advertised readiness despite a pipe-name collision");
            }
            catch (IOException) { }
        }

        await using var replacement = new CompanionHost(
            new TaskRegistry(new ImmediateServerAdapter(), policy, trustedRoot), new CapabilityStore(),
            new(pipeName, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)));
        _ = await replacement.StartAsync(timeout.Token);
        await replacement.StopAsync(timeout.Token);
    }

    private static JsonElement? FindTask(JsonElement payload, string taskId)
    {
        foreach (var task in payload.GetProperty("tasks").EnumerateArray())
            if (task.GetProperty("taskId").GetString() == taskId) return task.Clone();
        return null;
    }

    private static async Task<BrokerReply> RequestCapabilityAsync(string pipeName, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("{\"protocolVersion\":1,\"type\":\"capability.request\"}");
        var line = await reader.ReadLineAsync(cancellationToken);
        return JsonSerializer.Deserialize<BrokerReply>(line!, JsonOptions)!;
    }

    private static async Task<ClientWebSocket> ConnectAsync(BrokerReply reply, CancellationToken cancellationToken) =>
        await ConnectAsync(reply.WsUri!, reply.Capability, cancellationToken);

    private static async Task<ClientWebSocket> ConnectAsync(string uri, string? capability,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        if (capability is not null) socket.Options.SetRequestHeader("Authorization", $"Bearer {capability}");
        try
        {
            await socket.ConnectAsync(new Uri(uri), cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<bool> ConnectRejectedAsync(string uri, string? capability,
        CancellationToken cancellationToken)
    {
        try
        {
            using var socket = await ConnectAsync(uri, capability, cancellationToken);
            return false;
        }
        catch (WebSocketException) { return true; }
    }

    private static async Task SendAsync(ClientWebSocket socket, object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<WireMessage?> ReceiveUntilAsync(ClientWebSocket socket,
        Func<WireMessage, bool> predicate, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open)
        {
            var message = await ReceiveAsync(socket, cancellationToken);
            if (message is null || message.Type == "__closed") return null;
            if (predicate(message)) return message;
        }
        return null;
    }

    private static async Task<WireMessage?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return new("__closed", null, null, default);
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(stream.ToArray());
        var root = document.RootElement;
        return new(root.GetProperty("type").GetString()!,
            root.TryGetProperty("requestId", out var requestId) ? requestId.GetString() : null,
            root.TryGetProperty("taskId", out var taskId) ? taskId.GetString() : null,
            root.TryGetProperty("payload", out var payload) ? payload.Clone() : default);
    }

    private sealed record BrokerReply(int ProtocolVersion, string? WsUri, string? Capability, string? Error);
    private sealed record WireMessage(string Type, string? RequestId, string? TaskId, JsonElement Payload);
}

public sealed class ImmediateServerAdapter : ICodexTaskAdapter
{
    private int executionCount;
    public int ExecutionCount => executionCount;

    public Task RunAsync(TaskRun run, string prompt, string workingDirectory, TrustedPathPolicy pathPolicy,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref executionCount);
        run.Started("server-test-thread");
        run.Message("server-test-response");
        run.Complete();
        return Task.CompletedTask;
    }
}

public sealed class BlockingServerAdapter : ICodexTaskAdapter
{
    private int cancellationObserved;
    public bool CancellationObserved => Volatile.Read(ref cancellationObserved) != 0;

    public async Task RunAsync(TaskRun run, string prompt, string workingDirectory, TrustedPathPolicy pathPolicy,
        CancellationToken cancellationToken)
    {
        run.Started("blocking-test-thread");
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref cancellationObserved, 1);
            throw;
        }
    }
}

public sealed class CompletableServerAdapter : ICodexTaskAdapter
{
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => release.TrySetResult();

    public async Task RunAsync(TaskRun run, string prompt, string workingDirectory, TrustedPathPolicy pathPolicy,
        CancellationToken cancellationToken)
    {
        run.Started("completable-test-thread");
        await release.Task.WaitAsync(cancellationToken);
        run.Complete();
    }
}
