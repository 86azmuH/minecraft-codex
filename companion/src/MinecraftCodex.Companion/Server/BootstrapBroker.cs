using System.IO.Pipes;
using System.Text.Json;

namespace MinecraftCodex.Companion.Server;

public sealed class BootstrapBroker
{
    private readonly string pipeName;
    private readonly CapabilityStore capabilityStore;
    private readonly string webSocketUri;
    private readonly TimeSpan capabilityLifetime;
    private readonly TimeSpan readTimeout;

    public BootstrapBroker(string pipeName, CapabilityStore capabilityStore, string webSocketUri,
        TimeSpan? capabilityLifetime = null, TimeSpan? readTimeout = null)
    {
        this.pipeName = pipeName;
        this.capabilityStore = capabilityStore;
        this.webSocketUri = webSocketUri;
        this.capabilityLifetime = capabilityLifetime ?? TimeSpan.FromMinutes(1);
        this.readTimeout = readTimeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task RunAsync(CancellationToken cancellationToken,
        TaskCompletionSource<bool>? ready = null)
    {
        var firstInstance = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                if (firstInstance)
                {
                    firstInstance = false;
                    ready?.TrySetResult(true);
                }
            }
            catch (Exception ex)
            {
                ready?.TrySetException(ex);
                throw;
            }
            await using (pipe)
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(readTimeout);
                var request = await reader.ReadLineAsync(requestTimeout.Token);
                if (request is null || request.Length > 1024 || !IsValidRequest(request))
                {
                    await writer.WriteLineAsync("{\"protocolVersion\":1,\"error\":\"invalid request\"}");
                    continue;
                }

                var capability = capabilityStore.Mint(DateTimeOffset.UtcNow, capabilityLifetime);
                if (capability is null)
                {
                    await writer.WriteLineAsync("{\"protocolVersion\":1,\"error\":\"temporarily unavailable\"}");
                    continue;
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { protocolVersion = 1, wsUri = webSocketUri, capability },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // A connected client did not complete its request before the deadline.
            }
            catch (IOException)
            {
                // A client may disconnect mid-bootstrap. A fresh pipe instance is created for the next request.
            }
        }
        ready?.TrySetCanceled(cancellationToken);
    }

    private static bool IsValidRequest(string request)
    {
        try
        {
            using var document = JsonDocument.Parse(request);
            var root = document.RootElement;
            return root.TryGetProperty("protocolVersion", out var version) && version.GetInt32() == 1 &&
                   root.TryGetProperty("type", out var type) && type.GetString() == "capability.request";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
