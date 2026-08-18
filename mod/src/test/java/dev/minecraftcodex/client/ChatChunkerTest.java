package dev.minecraftcodex.client;

import org.junit.jupiter.api.Test;

import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

final class ChatChunkerTest {
    @Test
    void leavesConciseResponseAsOneMessage() {
        assertEquals(List.of("A short answer."), ChatChunker.split("A short answer.", 40));
    }

    @Test
    void splitsLongResponseWithoutDroppingWords() {
        String text = "alpha beta gamma delta epsilon zeta eta theta";
        List<String> chunks = ChatChunker.split(text, 18);
        assertTrue(chunks.stream().allMatch(chunk -> chunk.length() <= 18));
        assertEquals(text, String.join(" ", chunks));
    }

    @Test
    void handlesEmptyText() {
        assertTrue(ChatChunker.split("   ", 20).isEmpty());
    }
}
