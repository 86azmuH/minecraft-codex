package dev.minecraftcodex.client.companion;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

final class CompanionProtocolTest {
    @Test
    void parsesRealBootstrapShape() {
        var bootstrap = CompanionProtocol.parseBootstrap(
            "{\"protocolVersion\":1,\"wsUri\":\"ws://127.0.0.1:51234/v1/ws\",\"pipeName\":\"minecraft-codex-test\"}");
        assertEquals(1, bootstrap.protocolVersion());
        assertEquals("minecraft-codex-test", bootstrap.pipeName());
    }

    @Test
    void parsesRealCapabilityShape() {
        var capability = CompanionProtocol.parseCapability(
            "{\"protocolVersion\":1,\"wsUri\":\"ws://127.0.0.1:51234/v1/ws\",\"capability\":\"secret\"}");
        assertEquals("secret", capability.value());
    }

    @Test
    void parsesHelloAndSnapshot() {
        assertEquals(1, CompanionProtocol.parseHello(
            "{\"version\":1,\"type\":\"server.hello\",\"requestId\":null,\"taskId\":null,\"sequence\":null,\"payload\":{\"version\":1}}"));
        assertEquals(1, CompanionProtocol.parseSnapshotTaskCount(
            "{\"version\":1,\"type\":\"task.snapshot\",\"requestId\":\"abc\",\"taskId\":null,\"sequence\":null,\"payload\":{\"tasks\":[{}]}}", "abc"));
    }

    @Test
    void rejectsUnexpectedSnapshotRequestId() {
        assertThrows(IllegalArgumentException.class, () -> CompanionProtocol.parseSnapshotTaskCount(
            "{\"type\":\"task.snapshot\",\"requestId\":\"wrong\",\"payload\":{\"tasks\":[]}}", "expected"));
    }

    @Test
    void createsTaskStartAndParsesTaskEvents() {
        String request = CompanionProtocol.startTaskRequest("req", "hello");
        var requestJson = com.google.gson.JsonParser.parseString(request).getAsJsonObject();
        assertEquals("task.start", requestJson.get("type").getAsString());
        assertEquals("hello", requestJson.getAsJsonObject("payload").get("prompt").getAsString());

        var event = CompanionProtocol.parseTaskMessage(
            "{\"version\":1,\"type\":\"message.completed\",\"taskId\":\"task\",\"sequence\":2,\"payload\":{\"text\":\"answer\"}}");
        assertEquals("task", event.taskId());
        assertEquals("answer", event.payloadString("text"));

        var cancelJson = com.google.gson.JsonParser.parseString(
            CompanionProtocol.cancelTaskRequest("cancel", "task")).getAsJsonObject();
        assertEquals("task.cancel", cancelJson.get("type").getAsString());
        assertEquals("task", cancelJson.getAsJsonObject("payload").get("taskId").getAsString());
    }
}
