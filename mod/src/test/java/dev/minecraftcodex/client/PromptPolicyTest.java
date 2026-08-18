package dev.minecraftcodex.client;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

final class PromptPolicyTest {
    @Test
    void keepsOriginalPromptAndAddsConciseGuidance() {
        String result = PromptPolicy.forCodex("How do chunks work?");
        assertTrue(result.startsWith("Answer in a few concise sentences"));
        assertTrue(result.endsWith("How do chunks work?"));
        assertEquals(1, result.split("How do chunks work\\?", -1).length - 1);
    }
}
