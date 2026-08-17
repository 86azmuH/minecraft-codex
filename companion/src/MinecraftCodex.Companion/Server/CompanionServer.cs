using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MinecraftCodex.Companion.Codex;
using MinecraftCodex.Companion.Protocol;
using MinecraftCodex.Companion.Security;
using MinecraftCodex.Companion.Tasks;

namespace MinecraftCodex.Companion.Server;

public static class CompanionServer
{
    private const int MaximumMessageBytes = 64 * 1024;

    public static async Task<int> RunAsync(string workingDirectory, CancellationToken cancellationToken)
    {
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

        var capability = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var capabilityGate = new CapabilityGate(capability, DateTimeOffset.UtcNow.AddMinutes(1));
        var registry = new TaskRegistry(new CodexExecAdapter(cli.ExecutablePath), policy, decision.CanonicalPath);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Map("/v1/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest ||
                !Authenticate(context, capabilityGate))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await HandleSocketAsync(socket, registry, context.RequestAborted);
        });

        await app.StartAsync(cancellationToken);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.SingleOrDefault(item => item.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        if (address is null) throw new InvalidOperationException("Loopback binding was not established.");
        var wsUri = address.Replace("http://", "ws://", StringComparison.Ordinal) + "/v1/ws";
        Console.WriteLine(JsonSerializer.Serialize(new { protocolVersion = 1, wsUri, capability },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Console.Out.Flush();
        try { await app.WaitForShutdownAsync(cancellationToken); }
        finally { registry.CancelAll(); }
        return 0;
    }

    private static bool Authenticate(HttpContext context, CapabilityGate capabilityGate)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        return capabilityGate.TryConsume(header[7..], DateTimeOffset.UtcNow);
    }

    private static async Task HandleSocketAsync(WebSocket socket, TaskRegistry registry,
        CancellationToken cancellationToken)
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
                await HandleRequestAsync(socket, request, registry, sendLock, session.Token);
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

    private static async Task HandleRequestAsync(WebSocket socket, ClientMessage request, TaskRegistry registry,
        SemaphoreSlim sendLock, CancellationToken cancellationToken)
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
