using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MinecraftCodex.Companion.Protocol;
using MinecraftCodex.Companion.Tasks;

namespace MinecraftCodex.Companion.Server;

public sealed record CompanionHostOptions(
    string PipeName,
    TimeSpan CapabilityLifetime,
    TimeSpan BrokerReadTimeout,
    TimeSpan? IdleGrace = null)
{
    public static CompanionHostOptions Production(string pipeName) =>
        new(pipeName, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(2));
}

public sealed record CompanionEndpoint(int ProtocolVersion, string WsUri, string PipeName);

public sealed class CompanionHost : IAsyncDisposable
{
    private const int MaximumMessageBytes = 64 * 1024;
    private readonly TaskRegistry registry;
    private readonly CapabilityStore capabilityStore;
    private readonly CompanionHostOptions options;
    private WebApplication? app;
    private CancellationTokenSource? brokerCancellation;
    private Task? brokerTask;
    private readonly object stopGate = new();
    private Task? cleanupTask;
    private IdleShutdownCoordinator? idleShutdown;

    public CompanionHost(TaskRegistry registry, CapabilityStore capabilityStore, CompanionHostOptions options)
    {
        this.registry = registry;
        this.capabilityStore = capabilityStore;
        this.options = options;
    }

    public async Task<CompanionEndpoint> StartAsync(CancellationToken cancellationToken)
    {
        if (app is not null) throw new InvalidOperationException("Companion host already started.");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        app = builder.Build();
        if (options.IdleGrace is { } idleGrace)
        {
            idleShutdown = new IdleShutdownCoordinator(idleGrace, app.Lifetime.StopApplication);
            registry.ActiveCountChanged += idleShutdown.ActiveTasksChanged;
        }
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Map("/v1/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest || !Authenticate(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (idleShutdown is not null && !idleShutdown.TryClientConnected())
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await HandleSocketAsync(socket, context.RequestAborted);
            }
            finally { idleShutdown?.ClientDisconnected(); }
        });

        await app.StartAsync(cancellationToken);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.SingleOrDefault(item => item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        if (address is null) throw new InvalidOperationException("Loopback binding was not established.");
        var wsUri = address.Replace("http://", "ws://", StringComparison.Ordinal) + "/v1/ws";
        var broker = new BootstrapBroker(options.PipeName, capabilityStore, wsUri,
            options.CapabilityLifetime, options.BrokerReadTimeout);
        brokerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var brokerReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        brokerTask = broker.RunAsync(brokerCancellation.Token, brokerReady);
        _ = brokerTask.ContinueWith(_ => app.Lifetime.StopApplication(), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try { await brokerReady.Task.WaitAsync(cancellationToken); }
        catch
        {
            brokerCancellation.Cancel();
            await app.StopAsync(CancellationToken.None);
            throw;
        }
        idleShutdown?.Start(registry.ActiveCount);
        return new(1, wsUri, options.PipeName);
    }

    public async Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        if (app is null) throw new InvalidOperationException("Companion host has not started.");
        await app.WaitForShutdownAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task cleanup;
        lock (stopGate) cleanup = cleanupTask ??= CleanupAsync();
        await cleanup.WaitAsync(cancellationToken);
    }

    private async Task CleanupAsync()
    {
        idleShutdown?.Stop();
        if (idleShutdown is not null) registry.ActiveCountChanged -= idleShutdown.ActiveTasksChanged;
        brokerCancellation?.Cancel();
        await registry.ShutdownAsync(CancellationToken.None);
        if (app is not null) await app.StopAsync(CancellationToken.None);
        if (brokerTask is not null)
        {
            try { await brokerTask; } catch (OperationCanceledException) { }
            catch (Exception) { /* Startup already reports the failure; cleanup must remain reliable. */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await StopAsync(timeout.Token); } catch (OperationCanceledException) { }
        brokerCancellation?.Dispose();
        idleShutdown?.Dispose();
        if (app is not null) await app.DisposeAsync();
    }

    private bool Authenticate(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) &&
               capabilityStore.TryConsume(header[7..], DateTimeOffset.UtcNow);
    }

    private async Task HandleSocketAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var events = registry.Subscribe(out var unsubscribe);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sendLock = new SemaphoreSlim(1, 1);
        try
        {
            await SendAsync(socket, ProtocolMessage.Response("server.hello", null, new { version = 1 }),
                sendLock, session.Token);
            var sendPump = SendEventsAsync(socket, events, sendLock, session.Token);
            while (socket.State == WebSocketState.Open)
            {
                var request = await ReceiveAsync(socket, session.Token);
                if (request is null) break;
                if (request.Version != 1 || string.IsNullOrWhiteSpace(request.RequestId))
                {
                    await SendAsync(socket, ProtocolMessage.Response("request.rejected", request.RequestId,
                        new { reason = "invalid request" }), sendLock, session.Token);
                    continue;
                }
                await HandleRequestAsync(socket, request, sendLock, session.Token);
            }
            session.Cancel();
            try { await sendPump; } catch (OperationCanceledException) { }
        }
        finally
        {
            unsubscribe();
            session.Cancel();
        }
    }

    private static async Task<ClientMessage?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > MaximumMessageBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "invalid message", cancellationToken);
                return null;
            }
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        try { return JsonSerializer.Deserialize<ClientMessage>(stream.ToArray(), JsonDefaults.Options); }
        catch (JsonException) { return new ClientMessage(0, "invalid", null, default); }
    }

    private async Task HandleRequestAsync(WebSocket socket, ClientMessage request, SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case "task.start":
                StartTaskPayload? payload;
                try { payload = request.Payload.Deserialize<StartTaskPayload>(JsonDefaults.Options); }
                catch (JsonException) { payload = null; }
                if (payload is null)
                {
                    await SendAsync(socket, ProtocolMessage.Response("request.rejected", request.RequestId,
                        new { reason = "invalid task payload" }), sendLock, cancellationToken);
                    return;
                }
                var result = registry.Start(request.RequestId!, payload);
                await SendAsync(socket,
                    ProtocolMessage.Response(result.Accepted ? "request.accepted" : "request.rejected",
                        request.RequestId, new { taskId = result.TaskId, message = result.Message }),
                    sendLock, cancellationToken);
                break;
            case "task.snapshot":
                await SendAsync(socket, ProtocolMessage.Response("task.snapshot", request.RequestId,
                    new { tasks = registry.Snapshot() }), sendLock, cancellationToken);
                break;
            case "task.cancel":
                TaskIdPayload? cancelPayload;
                try { cancelPayload = request.Payload.Deserialize<TaskIdPayload>(JsonDefaults.Options); }
                catch (JsonException) { cancelPayload = null; }
                var cancelled = cancelPayload is not null && registry.Cancel(cancelPayload.TaskId);
                await SendAsync(socket, ProtocolMessage.Response(cancelled ? "request.accepted" : "request.rejected",
                    request.RequestId, new { message = cancelled ? "cancellation requested" : "task is not cancellable" }),
                    sendLock, cancellationToken);
                break;
            default:
                await SendAsync(socket, ProtocolMessage.Response("request.rejected", request.RequestId,
                    new { reason = "unsupported request type" }), sendLock, cancellationToken);
                break;
        }
    }

    private static async Task SendEventsAsync(WebSocket socket,
        System.Threading.Channels.ChannelReader<ProtocolMessage> events, SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        await foreach (var message in events.ReadAllAsync(cancellationToken))
            await SendAsync(socket, message, sendLock, cancellationToken);
    }

    private static async Task SendAsync(WebSocket socket, ProtocolMessage message, SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonDefaults.Options);
        await sendLock.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { sendLock.Release(); }
    }
}
