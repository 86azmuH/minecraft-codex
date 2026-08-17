using System.ComponentModel;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

if (args.Length != 4 || args[0] != "--server" || args[2] != "--working-directory")
{
    Console.Error.WriteLine("Usage: minecraft-codex-client --server <exe> --working-directory <path>; prompt is read from stdin");
    return 2;
}

var prompt = await Console.In.ReadToEndAsync();
if (string.IsNullOrWhiteSpace(prompt))
{
    Console.Error.WriteLine("A prompt must be piped through stdin.");
    return 2;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
using var server = StartServer(args[1], args[3]);
try
{
    var bootstrapLine = await server.StandardOutput.ReadLineAsync(timeout.Token);
    Bootstrap? bootstrap;
    try { bootstrap = bootstrapLine is null ? null : JsonSerializer.Deserialize<Bootstrap>(bootstrapLine, ClientJson.Options); }
    catch (JsonException) { bootstrap = null; }
    if (bootstrap is null || bootstrap.ProtocolVersion != 1 ||
        !Uri.TryCreate(bootstrap.WsUri, UriKind.Absolute, out var uri))
    {
        var error = await server.StandardError.ReadToEndAsync(timeout.Token);
        Console.Error.WriteLine($"Companion bootstrap failed. {Sanitize(error)}");
        return 2;
    }

    using var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Authorization", $"Bearer {bootstrap.Capability}");
    await socket.ConnectAsync(uri, timeout.Token);
    Console.WriteLine(await ReceiveAsync(socket, timeout.Token));

    var request = JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        type = "task.start",
        requestId = Guid.NewGuid().ToString("N"),
        payload = new { prompt, workingDirectory = (string?)null }
    }, ClientJson.Options);
    await socket.SendAsync(request, WebSocketMessageType.Text, true, timeout.Token);

    while (socket.State == WebSocketState.Open)
    {
        var message = await ReceiveAsync(socket, timeout.Token);
        if (message is null) break;
        Console.WriteLine(message);
        using var document = JsonDocument.Parse(message);
        if (document.RootElement.GetProperty("type").GetString() is "task.completed" or "task.failed") break;
    }
    return 0;
}
finally
{
    try
    {
        if (!server.HasExited) server.Kill(entireProcessTree: true);
        await server.WaitForExitAsync(CancellationToken.None);
    }
    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
}

static Process StartServer(string executable, string workingDirectory)
{
    var start = new ProcessStartInfo
    {
        FileName = executable,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    start.ArgumentList.Add("serve");
    start.ArgumentList.Add("--working-directory");
    start.ArgumentList.Add(workingDirectory);
    var process = new Process { StartInfo = start };
    process.Start();
    return process;
}

static async Task<string?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
{
    using var stream = new MemoryStream();
    var buffer = new byte[4096];
    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        stream.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);
    return Encoding.UTF8.GetString(stream.ToArray());
}

static string Sanitize(string value) => string.IsNullOrWhiteSpace(value) ? "No diagnostic detail." : value.Split('\n')[0].Trim();

sealed record Bootstrap(int ProtocolVersion, string WsUri, string Capability);
static class ClientJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
