package dev.minecraftcodex.client;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertTrue;

final class CodexHudLayoutTest {
    @Test
    void usesRightSideWhenThereIsRoom() {
        CodexHudLayout layout = CodexHudLayout.besideHotbar(427, 240, 58, 15);
        assertTrue(layout.x() > 427 / 2 + 91);
        assertTrue(layout.x() + layout.width() <= 427);
    }

    @Test
    void fallsBackToLeftSideOnNarrowGui() {
        CodexHudLayout layout = CodexHudLayout.besideHotbar(310, 180, 58, 15);
        assertTrue(layout.x() + layout.width() < 310 / 2 - 91);
        assertTrue(layout.y() >= 0);
    }
}
