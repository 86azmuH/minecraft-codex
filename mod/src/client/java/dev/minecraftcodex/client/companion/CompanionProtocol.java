package dev.minecraftcodex.client.companion;

import com.google.gson.JsonObject;
import com.google.gson.JsonNull;
import com.google.gson.JsonParser;

import java.util.UUID;

final class CompanionProtocol {
    static final int VERSION = 1;

    private CompanionProtocol() { }

    static String capabilityRequest() {
        return "{\"protocolVersion\":1,\"type\":\"capability.request\"}\n";
    }

    static String snapshotRequest(String requestId) {
        JsonObject request = new JsonObject();
        request.addProperty("version", VERSION);
        request.addProperty("type", "task.snapshot");
        request.addProperty("requestId", requestId);
        request.add("payload", new JsonObject());
        return request.toString();
    }

    static String startTaskRequest(String requestId, String prompt) {
        JsonObject payload = new JsonObject();
        payload.addProperty("prompt", prompt);
        payload.add("workingDirectory", JsonNull.INSTANCE);
        JsonObject request = new JsonObject();
        request.addProperty("version", VERSION);
        request.addProperty("type", "task.start");
        request.addProperty("requestId", requestId);
        request.add("payload", payload);
        return request.toString();
    }

    static String cancelTaskRequest(String requestId, String taskId) {
        JsonObject payload = new JsonObject();
        payload.addProperty("taskId", taskId);
        JsonObject request = new JsonObject();
        request.addProperty("version", VERSION);
        request.addProperty("type", "task.cancel");
        request.addProperty("requestId", requestId);
        request.add("payload", payload);
        return request.toString();
    }

    static String newRequestId() {
        return UUID.randomUUID().toString().replace("-", "");
    }

    static Bootstrap parseBootstrap(String json) {
        JsonObject root = JsonParser.parseString(json).getAsJsonObject();
        return new Bootstrap(
            root.get("protocolVersion").getAsInt(),
            requiredString(root, "pipeName"));
    }

    static Capability parseCapability(String json) {
        JsonObject root = JsonParser.parseString(json).getAsJsonObject();
        return new Capability(
            root.get("protocolVersion").getAsInt(),
            requiredString(root, "wsUri"),
            requiredString(root, "capability"));
    }

    static int parseHello(String json) {
        JsonObject root = JsonParser.parseString(json).getAsJsonObject();
        if (!"server.hello".equals(requiredString(root, "type")))
            throw new IllegalArgumentException("companion did not send server.hello");
        return root.getAsJsonObject("payload").get("version").getAsInt();
    }

    static int parseSnapshotTaskCount(String json, String requestId) {
        JsonObject root = JsonParser.parseString(json).getAsJsonObject();
        if (!"task.snapshot".equals(requiredString(root, "type")) ||
            !requestId.equals(requiredString(root, "requestId")))
            throw new IllegalArgumentException("companion returned an unexpected snapshot response");
        return root.getAsJsonObject("payload").getAsJsonArray("tasks").size();
    }

    static TaskMessage parseTaskMessage(String json) {
        JsonObject root = JsonParser.parseString(json).getAsJsonObject();
        String type = requiredString(root, "type");
        String requestId = optionalString(root, "requestId");
        String taskId = optionalString(root, "taskId");
        JsonObject payload = root.has("payload") && root.get("payload").isJsonObject()
            ? root.getAsJsonObject("payload") : new JsonObject();
        return new TaskMessage(type, requestId, taskId, payload);
    }

    private static String optionalString(JsonObject value, String name) {
        return !value.has(name) || value.get(name).isJsonNull() ? null : value.get(name).getAsString();
    }

    private static String requiredString(JsonObject value, String name) {
        if (!value.has(name) || value.get(name).isJsonNull())
            throw new IllegalArgumentException("missing " + name);
        String result = value.get(name).getAsString();
        if (result.isBlank()) throw new IllegalArgumentException("empty " + name);
        return result;
    }

    record Bootstrap(int protocolVersion, String pipeName) { }
    record Capability(int protocolVersion, String wsUri, String value) { }
    record TaskMessage(String type, String requestId, String taskId, JsonObject payload) {
        String payloadString(String name) {
            return payload.has(name) && !payload.get(name).isJsonNull() ? payload.get(name).getAsString() : null;
        }
    }
}
