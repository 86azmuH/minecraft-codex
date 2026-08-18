using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MinecraftCodex.Companion.Server;

if (args.Length == 3 && args[0] == "--snapshot-only")
{
    using var reconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    return await RunSnapshotOnlyAsync(args[1], args[2], reconnectTimeout.Token);
}

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
var expectedServerPath = Path.GetFullPath(args[1]);
using var server = StartServer(expectedServerPath, args[3]);
try
{
    var bootstrapLine = await server.StandardOutput.ReadLineAsync(timeout.Token);
    Bootstrap? bootstrap;
    try { bootstrap = bootstrapLine is null ? null : JsonSerializer.Deserialize<Bootstrap>(bootstrapLine, ClientJson.Options); }
    catch (JsonException) { bootstrap = null; }
    var expectedPipeName = BootstrapIdentity.CurrentUserPipeName();
    if (bootstrap is null || bootstrap.ProtocolVersion != 1 || bootstrap.PipeName != expectedPipeName)
    {
        var error = await server.StandardError.ReadToEndAsync(timeout.Token);
        Console.Error.WriteLine($"Companion bootstrap failed. {Sanitize(error)}");
        return 2;
    }

    await VerifyDuplicateLaunchAsync(expectedServerPath, args[3], expectedPipeName, timeout.Token);
    var taskId = await RunTaskConnectionAsync(expectedPipeName, expectedServerPath, prompt,
        Path.GetFullPath(args[3]), timeout.Token);
    await RunFreshProcessSnapshotAsync(taskId, expectedServerPath, timeout.Token);
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

static async Task<string> RunTaskConnectionAsync(string pipeName, string expectedServerPath, string prompt,
    string workingDirectory, CancellationToken cancellationToken)
{
    var bootstrap = await RequestCapabilityAsync(pipeName, expectedServerPath, cancellationToken);
    using var socket = await ConnectAsync(new Uri(bootstrap.WsUri!), bootstrap.Capability!, cancellationToken);
    Console.WriteLine(await ReceiveAsync(socket, cancellationToken));
    await SendAsync(socket, new
    {
        version = 1,
        type = "task.start",
        requestId = Guid.NewGuid().ToString("N"),
        payload = new { prompt, workingDirectory }
    }, cancellationToken);

    string? taskId = null;
    while (socket.State == WebSocketState.Open)
    {
        var message = await ReceiveAsync(socket, cancellationToken);
        if (message is null) break;
        Console.WriteLine(message);
        using var document = JsonDocument.Parse(message);
        var type = document.RootElement.GetProperty("type").GetString();
        if (type == "request.accepted")
            taskId = document.RootElement.GetProperty("payload").GetProperty("taskId").GetString();
        if (type is "task.completed" or "task.failed") break;
    }
    return taskId ?? throw new InvalidOperationException("Task ID was not returned.");
}

static async Task<int> RunSnapshotOnlyAsync(string expectedTaskId, string expectedServerPath,
    CancellationToken cancellationToken)
{
    var bootstrap = await RequestCapabilityAsync(BootstrapIdentity.CurrentUserPipeName(), expectedServerPath,
        cancellationToken);
    using var socket = await ConnectAsync(new Uri(bootstrap.WsUri!), bootstrap.Capability!, cancellationToken);
    Console.WriteLine(await ReceiveAsync(socket, cancellationToken));
    await SendAsync(socket, new
    {
        version = 1,
        type = "task.snapshot",
        requestId = Guid.NewGuid().ToString("N"),
        payload = new { }
    }, cancellationToken);
    var snapshot = await ReceiveAsync(socket, cancellationToken);
    Console.WriteLine(snapshot);
    if (snapshot is null) throw new InvalidOperationException("Reconnect snapshot was not returned.");
    using var document = JsonDocument.Parse(snapshot);
    if (document.RootElement.GetProperty("type").GetString() != "task.snapshot")
        throw new InvalidOperationException("Reconnect returned an unexpected response.");
    var task = document.RootElement.GetProperty("payload").GetProperty("tasks").EnumerateArray()
        .FirstOrDefault(item => item.GetProperty("taskId").GetString() == expectedTaskId);
    if (task.ValueKind == JsonValueKind.Undefined || task.GetProperty("state").GetString() != "completed")
        throw new InvalidOperationException("Reconnect snapshot did not contain the completed task.");
    var lastSequence = task.GetProperty("lastSequence").GetInt64();
    var sequences = task.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("sequence").GetInt64()).ToArray();
    if (sequences.Length == 0 || !sequences.SequenceEqual(sequences.Order()) || sequences[^1] != lastSequence)
        throw new InvalidOperationException("Reconnect snapshot sequence was inconsistent.");
    return 0;
}

static async Task RunFreshProcessSnapshotAsync(string expectedTaskId, string expectedServerPath,
    CancellationToken cancellationToken)
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Client executable path is unavailable.");
    var start = new ProcessStartInfo
    {
        FileName = executable,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    start.ArgumentList.Add("--snapshot-only");
    start.ArgumentList.Add(expectedTaskId);
    start.ArgumentList.Add(expectedServerPath);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Snapshot client could not start.");
    var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
    var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
    await process.WaitForExitAsync(cancellationToken);
    Console.Write(await stdout);
    var error = await stderr;
    if (process.ExitCode != 0) throw new InvalidOperationException($"Fresh snapshot client failed: {Sanitize(error)}");
}

static async Task VerifyDuplicateLaunchAsync(string expectedServerPath, string workingDirectory,
    string expectedPipeName, CancellationToken cancellationToken)
{
    using var duplicate = StartServer(expectedServerPath, workingDirectory);
    try
    {
        var line = await duplicate.StandardOutput.ReadLineAsync(cancellationToken);
        Bootstrap? response;
        try { response = line is null ? null : JsonSerializer.Deserialize<Bootstrap>(line, ClientJson.Options); }
        catch (JsonException) { response = null; }
        await duplicate.WaitForExitAsync(cancellationToken);
        if (duplicate.ExitCode != 0 || response?.AlreadyRunning != true || response.PipeName != expectedPipeName)
            throw new InvalidOperationException("A duplicate companion did not attach to the existing instance safely.");
    }
    finally
    {
        if (!duplicate.HasExited) duplicate.Kill(entireProcessTree: true);
    }
}

static async Task<CapabilityResponse> RequestCapabilityAsync(string pipeName, string expectedServerPath,
    CancellationToken cancellationToken)
{
    await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous);
    await pipe.ConnectAsync(cancellationToken);
    NamedPipeServerIdentity.VerifyExecutable(pipe, expectedServerPath);
    using var reader = new StreamReader(pipe, leaveOpen: true);
    await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    await writer.WriteLineAsync("{\"protocolVersion\":1,\"type\":\"capability.request\"}");
    var response = await reader.ReadLineAsync(cancellationToken);
    CapabilityResponse? capability;
    try { capability = response is null ? null : JsonSerializer.Deserialize<CapabilityResponse>(response, ClientJson.Options); }
    catch (JsonException) { capability = null; }
    if (capability?.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(capability.Capability) ||
        !IsStrictLoopbackWebSocketUri(capability.WsUri, out _))
        throw new InvalidOperationException("Bootstrap broker did not return a capability.");
    return capability;
}

static bool IsStrictLoopbackWebSocketUri(string? value, out Uri? endpoint)
{
    endpoint = null;
    if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != "ws" ||
        parsed.Host != IPAddress.Loopback.ToString() || parsed.IsDefaultPort || parsed.Port is < 1 or > 65535 ||
        parsed.AbsolutePath != "/v1/ws" || !string.IsNullOrEmpty(parsed.UserInfo) ||
        !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        return false;
    endpoint = parsed;
    return true;
}

static async Task<ClientWebSocket> ConnectAsync(Uri uri, string capability,
    CancellationToken cancellationToken)
{
    var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Authorization", $"Bearer {capability}");
    try
    {
        await socket.ConnectAsync(uri, cancellationToken);
        return socket;
    }
    catch
    {
        socket.Dispose();
        throw;
    }
}

static async Task SendAsync(ClientWebSocket socket, object message, CancellationToken cancellationToken)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ClientJson.Options);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
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

static string Sanitize(string value) => string.IsNullOrWhiteSpace(value)
    ? "No diagnostic detail."
    : value.Split('\n')[0].Trim();

sealed record Bootstrap(int ProtocolVersion, string WsUri, string PipeName, bool AlreadyRunning = false);
sealed record CapabilityResponse(int ProtocolVersion, string? WsUri, string? Capability, string? Error);
static class ClientJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
