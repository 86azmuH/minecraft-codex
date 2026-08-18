package dev.minecraftcodex.client;

record CodexHudLayout(int x, int y, int width, int height) {
    private static final int HOTBAR_HALF_WIDTH = 91;
    private static final int GAP = 6;
    private static final int MARGIN = 2;

    static CodexHudLayout besideHotbar(int screenWidth, int screenHeight, int badgeWidth, int badgeHeight) {
        int center = screenWidth / 2;
        int right = center + HOTBAR_HALF_WIDTH + GAP;
        int left = center - HOTBAR_HALF_WIDTH - GAP - badgeWidth;
        int x = right + badgeWidth <= screenWidth - MARGIN ? right : Math.max(MARGIN, left);
        int y = Math.max(MARGIN, screenHeight - 22 + (22 - badgeHeight) / 2);
        return new CodexHudLayout(x, y, badgeWidth, badgeHeight);
    }
}
