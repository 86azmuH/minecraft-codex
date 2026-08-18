package dev.minecraftcodex.client.companion;

import com.google.gson.JsonParseException;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.WebSocket;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.Locale;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;

public final class CompanionConnectionService {
    private final Path executable;
    private final Path workingDirectory;
    private final ExecutorService executor;

    private CompanionConnectionService(Path executable, Path workingDirectory) {
        this.executable = executable;
        this.workingDirectory = workingDirectory;
        this.executor = Executors.newSingleThreadExecutor(Thread.ofPlatform()
            .name("minecraft-codex-companion", 0).daemon(true).factory());
    }

    public static CompanionConnectionService fromEnvironment() {
        String configured = System.getProperty("minecraftcodex.companion.executable", "").trim();
        Path executable = configured.isEmpty() ? null : Path.of(configured).toAbsolutePath().normalize();
        String configuredWorkdir = System.getProperty("minecraftcodex.workingDirectory", System.getProperty("user.dir"));
        return new CompanionConnectionService(executable, Path.of(configuredWorkdir).toAbsolutePath().normalize());
    }

    public CompletableFuture<CompanionStatus> refresh() {
        if (executable == null) return CompletableFuture.completedFuture(CompanionStatus.notConfigured());
        return openSession().thenApply(session -> {
                try { return session.status(); }
                finally { session.close(); }
            })
            .exceptionally(error -> CompanionStatus.unavailable(rootMessage(error)));
    }

    public CompletableFuture<CompanionSession> openSession() {
        if (executable == null)
            return CompletableFuture.failedFuture(new IllegalStateException("companion executable is not configured"));
        return CompletableFuture.supplyAsync(this::connectAndSnapshot, executor);
    }

    public static CompanionStatus unavailableStatus(Throwable error) {
        return CompanionStatus.unavailable(rootMessage(error));
    }

    private CompanionSession connectAndSnapshot() {
        try {
            if (!isWindows()) throw new IOException("Windows is required");
            if (!Files.isRegularFile(executable)) throw new IOException("configured companion executable was not found");
            if (!Files.isDirectory(workingDirectory)) throw new IOException("configured working directory was not found");

            CompanionProtocol.Bootstrap bootstrap = startCompanion();
            if (bootstrap.protocolVersion() != CompanionProtocol.VERSION)
                throw new IOException("unsupported companion bootstrap protocol");

            CompanionProtocol.Capability capability = WindowsBootstrapPipe.request(
                bootstrap.pipeName(), executable, CompanionProtocol.capabilityRequest());
            validateCapability(capability);
            return requestSnapshot(capability);
        } catch (IOException | InterruptedException | JsonParseException | IllegalArgumentException e) {
            if (e instanceof InterruptedException) Thread.currentThread().interrupt();
            throw new CompletionException(e);
        }
    }

    private CompanionProtocol.Bootstrap startCompanion() throws IOException, InterruptedException {
        ProcessBuilder builder = new ProcessBuilder(
            executable.toString(), "serve", "--working-directory", workingDirectory.toString())
            .redirectErrorStream(false);
        configureDevelopmentDotnet(builder);
        Process process = builder.start();
        try (BufferedReader output = new BufferedReader(new InputStreamReader(process.getInputStream()))) {
            CompletableFuture<String> line = CompletableFuture.supplyAsync(() -> {
                try { return output.readLine(); }
                catch (IOException e) { throw new CompletionException(e); }
            });
            String bootstrap = line.orTimeout(10, TimeUnit.SECONDS).join();
            if (bootstrap == null) {
                String error = new BufferedReader(new InputStreamReader(process.getErrorStream())).readLine();
                throw new IOException(error == null ? "companion returned no bootstrap descriptor" : error);
            }
            return CompanionProtocol.parseBootstrap(bootstrap);
        }
    }

    private static void configureDevelopmentDotnet(ProcessBuilder builder) {
        String configured = System.getProperty("minecraftcodex.dotnetRoot", "").trim();
        Path root;
        if (!configured.isEmpty()) root = Path.of(configured);
        else {
            String localAppData = System.getenv("LOCALAPPDATA");
            root = localAppData == null ? null : Path.of(localAppData, "MinecraftCodex", "dotnet");
        }
        if (root != null && Files.isRegularFile(root.resolve("dotnet.exe")))
            builder.environment().put("DOTNET_ROOT", root.toAbsolutePath().normalize().toString());
    }

    private CompanionSession requestSnapshot(CompanionProtocol.Capability capability) {
        SnapshotListener listener = new SnapshotListener();
        WebSocket socket = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(5)).build()
            .newWebSocketBuilder()
            .header("Authorization", "Bearer " + capability.value())
            .connectTimeout(Duration.ofSeconds(5))
            .buildAsync(URI.create(capability.wsUri()), listener)
            .orTimeout(6, TimeUnit.SECONDS).join();
        try {
            int helloVersion = CompanionProtocol.parseHello(listener.next().orTimeout(6, TimeUnit.SECONDS).join());
            if (helloVersion != CompanionProtocol.VERSION) throw new IllegalArgumentException("unsupported WebSocket protocol");
            String requestId = CompanionProtocol.newRequestId();
            socket.sendText(CompanionProtocol.snapshotRequest(requestId), true).join();
            int taskCount = CompanionProtocol.parseSnapshotTaskCount(
                listener.next().orTimeout(6, TimeUnit.SECONDS).join(), requestId);
            return new CompanionSession(socket, CompanionStatus.connected(helloVersion, taskCount), listener);
        } catch (RuntimeException error) {
            socket.abort();
            throw error;
        }
    }

    private static void validateCapability(CompanionProtocol.Capability capability) {
        if (capability.protocolVersion() != CompanionProtocol.VERSION)
            throw new IllegalArgumentException("unsupported broker protocol");
        URI uri = URI.create(capability.wsUri());
        if (!"ws".equals(uri.getScheme()) || !"127.0.0.1".equals(uri.getHost()) || uri.getPort() < 1 ||
            !"/v1/ws".equals(uri.getPath()) || uri.getUserInfo() != null || uri.getQuery() != null || uri.getFragment() != null)
            throw new IllegalArgumentException("broker returned a non-loopback endpoint");
    }

    private static boolean isWindows() {
        return System.getProperty("os.name", "").toLowerCase(Locale.ROOT).startsWith("windows");
    }

    private static String rootMessage(Throwable error) {
        Throwable current = error;
        while (current.getCause() != null) current = current.getCause();
        return current.getMessage();
    }
}
