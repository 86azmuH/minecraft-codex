package dev.minecraftcodex.client;

import dev.minecraftcodex.client.companion.CompanionConnectionService;
import dev.minecraftcodex.client.companion.CompanionSession;
import dev.minecraftcodex.client.companion.CompanionStatus;
import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.fabric.api.client.command.v2.ClientCommandRegistrationCallback;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientLevelEvents;
import net.fabricmc.fabric.api.client.message.v1.ClientSendMessageEvents;
import net.fabricmc.fabric.api.client.networking.v1.ClientPlayConnectionEvents;
import net.fabricmc.fabric.api.client.rendering.v1.hud.HudElementRegistry;
import net.fabricmc.fabric.api.client.rendering.v1.hud.VanillaHudElements;
import net.minecraft.client.Minecraft;
import net.minecraft.network.chat.Component;
import net.minecraft.resources.Identifier;

import java.util.concurrent.atomic.AtomicBoolean;

import static net.fabricmc.fabric.api.client.command.v2.ClientCommands.literal;

public final class MinecraftCodexClient implements ClientModInitializer {
    private static final Identifier HUD_ID = Identifier.fromNamespaceAndPath("minecraft-codex", "mode_badge");
    private static final String HUD_TEXT = "[ CODEX ]";
    private static final int HUD_HEIGHT = 15;
    private final CompanionConnectionService companion = CompanionConnectionService.fromEnvironment();
    private final CodexModeController mode = new CodexModeController();
    private final AtomicBoolean activeTask = new AtomicBoolean();
    private volatile CompanionSession activeSession;

    @Override
    public void onInitializeClient() {
        registerCommands();
        registerSafetyResets();
        ClientSendMessageEvents.ALLOW_CHAT.register(this::interceptChat);
        HudElementRegistry.attachElementAfter(VanillaHudElements.HOTBAR, HUD_ID, (graphics, tickCounter) -> {
            if (!mode.isEnabled()) return;
            Minecraft client = Minecraft.getInstance();
            int width = client.font.width(HUD_TEXT) + 8;
            CodexHudLayout layout = CodexHudLayout.besideHotbar(
                graphics.guiWidth(), graphics.guiHeight(), width, HUD_HEIGHT);
            graphics.fill(layout.x(), layout.y(), layout.x() + layout.width(), layout.y() + layout.height(), 0xCCB8860B);
            graphics.fill(layout.x() + 1, layout.y() + 1,
                layout.x() + layout.width() - 1, layout.y() + layout.height() - 1, 0xD0181818);
            graphics.text(client.font, HUD_TEXT, layout.x() + 4, layout.y() + 3, 0xFFFFAA00, true);
        });
    }

    private void registerCommands() {
        ClientCommandRegistrationCallback.EVENT.register((dispatcher, registryAccess) ->
            dispatcher.register(literal("codex")
                .then(literal("on").executes(context -> {
                    long attempt = mode.beginEnable();
                    if (attempt < 0) {
                        context.getSource().sendFeedback(Component.literal(mode.state() == CodexModeController.State.ENABLED
                            ? "[Codex] Codex mode is already enabled."
                            : "[Codex] Companion connection is already being checked."));
                        return 1;
                    }
                    context.getSource().sendFeedback(Component.literal("[Codex] Connecting…"));
                    companion.openSession().whenComplete((session, error) ->
                        context.getSource().getClient().execute(() -> finishEnable(context.getSource(), attempt, session, error)));
                    return 1;
                }))
                .then(literal("off").executes(context -> {
                    boolean changed = disableMode();
                    context.getSource().sendFeedback(Component.literal(changed
                        ? "[Codex] Codex mode disabled."
                        : "[Codex] Codex mode is already disabled."));
                    return 1;
                }))
                .then(literal("status").executes(context -> {
                    context.getSource().sendFeedback(Component.literal("[Codex] Checking companion…"));
                    companion.refresh().thenAccept(status -> context.getSource().getClient().execute(() ->
                        context.getSource().sendFeedback(Component.literal(formatStatus(status) + " Mode: " + modeLabel() + "."))));
                    return 1;
                }))));
    }

    private void registerSafetyResets() {
        ClientPlayConnectionEvents.JOIN.register((handler, sender, client) -> disableMode());
        ClientPlayConnectionEvents.DISCONNECT.register((handler, client) -> disableMode());
        ClientLevelEvents.AFTER_CLIENT_LEVEL_CHANGE.register((client, level) -> disableMode());
    }

    private void finishEnable(net.fabricmc.fabric.api.client.command.v2.FabricClientCommandSource source,
                              long attempt, CompanionSession session, Throwable error) {
        boolean connected = error == null && session != null;
        CodexModeController.Completion completion = mode.finishEnable(attempt, connected);
        if (completion == CodexModeController.Completion.STALE) {
            if (session != null) session.close();
            return;
        }
        if (completion == CodexModeController.Completion.FAILED) {
            source.sendFeedback(Component.literal(formatStatus(CompanionConnectionService.unavailableStatus(error))));
            return;
        }
        activeSession = session;
        source.sendFeedback(Component.literal("[Codex] Codex mode enabled."));
        session.closed().whenComplete((ignored, closeError) -> source.getClient().execute(() -> {
            if (activeSession == session) disableMode();
        }));
    }

    private boolean disableMode() {
        CompanionSession session = activeSession;
        activeSession = null;
        activeTask.set(false);
        boolean changed = mode.disable();
        if (session != null) session.close();
        return changed;
    }

    private boolean interceptChat(String message) {
        if (!mode.isEnabled()) return true;
        if (message == null || message.isBlank()) return false;
        Minecraft client = Minecraft.getInstance();
        CompanionSession session = activeSession;
        if (session == null) {
            disableMode();
            client.execute(() -> addPrivate(client, "Codex", "Companion connection was lost."));
            return false;
        }
        if (!activeTask.compareAndSet(false, true)) {
            client.execute(() -> addPrivate(client, "Codex", "Still answering—please wait."));
            return false;
        }

        client.execute(() -> {
            addChunks(client, "You", message);
            addPrivate(client, "Codex", "Thinking…");
        });
        session.runTask(PromptPolicy.forCodex(message), response -> client.execute(() -> {
            if (activeSession == session) addChunks(client, "Codex", response);
        })).whenComplete((ignored, error) -> client.execute(() -> {
            if (activeSession != session) return;
            activeTask.set(false);
            if (error != null) {
                addPrivate(client, "Codex", CompanionConnectionService.unavailableStatus(error).detail());
                disableMode();
            }
        }));
        return false;
    }

    private static void addChunks(Minecraft client, String label, String text) {
        for (String chunk : ChatChunker.split(text, 220)) addPrivate(client, label, chunk);
    }

    private static void addPrivate(Minecraft client, String label, String text) {
        client.gui.getChat().addClientSystemMessage(Component.literal(label + ": " + text));
    }

    private String modeLabel() {
        return switch (mode.state()) {
            case DISABLED -> "disabled";
            case CONNECTING -> "connecting";
            case ENABLED -> "enabled";
        };
    }

    private static String formatStatus(CompanionStatus status) {
        return switch (status.state()) {
            case CONNECTED -> "[Codex] Companion connected (protocol " + status.protocolVersion() + ").";
            case NOT_CONFIGURED -> "[Codex] Companion path is not configured. Set -Dminecraftcodex.companion.executable=<path>.";
            case UNAVAILABLE -> "[Codex] Companion unavailable: " + status.detail();
        };
    }
}
