using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AntiDLL.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminCommands.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace AntiDLL.Modules;

/// <summary>
///     The single punishment / reporting funnel. Both detectors — the PRIMARY legacy-event subscription
///     detector and the SECONDARY cvar probe — call <see cref="Act" /> with a client and a human-readable
///     reason; this applies the configured action (notify / kick / ban), notifies admins, and posts the
///     Discord webhook. Centralised so the two detectors share identical, audited enforcement behaviour.
///
///     All methods are expected to run on the GAME THREAD (callers marshal to it first). The client is
///     re-validated here before any punishment so a disconnect/slot-reuse race can never punish the wrong
///     player.
/// </summary>
internal sealed class DetectionSink
{
    private readonly InterfaceBridge _bridge;
    private readonly AntiDllConfig   _config;
    private readonly ILogger         _logger;

    private DiscordWebhook?  _webhook;
    private HashSet<string>? _sharedBypass;

    public DetectionSink(InterfaceBridge bridge, AntiDllConfig config, ILogger logger)
    {
        _bridge = bridge;
        _config = config;
        _logger = logger;
    }

    public void Configure(DiscordWebhook webhook, HashSet<string> sharedBypass)
    {
        _webhook      = webhook;
        _sharedBypass = sharedBypass;
    }

    /// <summary>
    ///     Returns true if this client should be skipped entirely by detection (whitelist / shared bypass /
    ///     admin / bots / HLTV). Game thread.
    /// </summary>
    public bool IsExempt(IGameClient client)
    {
        if (client.IsFakeClient || client.IsHltv)
            return true;

        var steamStr = ((ulong) client.SteamId).ToString();

        if (_config.Whitelist.Contains(steamStr) || (_sharedBypass?.Contains(steamStr) ?? false))
            return true;

        if (_config.AdminBypass && _bridge.AdminManager?.GetAdmin(client.SteamId) is not null)
            return true;

        return false;
    }

    /// <summary>
    ///     Apply the configured action for a confirmed detection. <paramref name="detail" /> is a short,
    ///     human-readable description of what matched (e.g. the offending event list, or the cvar list).
    ///     Game thread.
    /// </summary>
    public void Act(IGameClient client, string source, string detail)
    {
        // Re-validate: the player may have left, or the callback chain may have raced a disconnect.
        if (!client.IsValid || !client.IsInGame || client.IsFakeClient)
            return;

        var name     = client.Name;
        var steamStr = ((ulong) client.SteamId).ToString();
        var action   = _config.Action;

        _logger.LogWarning("[AntiDLL] {Name} ({Steam}) flagged by {Source}: {Detail} — action={Action}",
            name, steamStr, source, detail, action);

        if (_config.NotifyAdmins)
            NotifyAdmins(name, steamStr, source, detail);

        var actionStr = action.ToString().ToLowerInvariant();
        if (_webhook is { Enabled: true })
        {
            var n = name;
            var s = steamStr;
            var d = $"[{source}] {detail}";
            _ = Task.Run(() => _webhook.PostDetectionAsync(n, s, d, actionStr));
        }

        switch (action)
        {
            case AntiDllAction.Notify:
                // Log + admin notice + webhook only. No punishment (safe default).
                break;

            case AntiDllAction.Kick:
                _bridge.ClientManager.KickClient(client, $"AntiDLL: {_config.Reason}",
                    NetworkDisconnectionReason.KickedUntrustedAccount);
                break;

            case AntiDllAction.Ban:
                IssueBan(client, steamStr);
                break;
        }
    }

    private void IssueBan(IGameClient client, string steamStr)
    {
        if (_bridge.AdminService is not { } admin)
        {
            _logger.LogError("[AntiDLL] AdminService unavailable — falling back to kick for {Steam}", steamStr);
            _bridge.ClientManager.KickClient(client, $"AntiDLL: {_config.Reason}",
                NetworkDisconnectionReason.KickedUntrustedAccount);
            return;
        }

        var duration = _config.BanDurationMinutes <= 0
            ? (TimeSpan?) null
            : TimeSpan.FromMinutes(_config.BanDurationMinutes);

        // admin = null → console/system actor. AdminCommands kicks the online target and persists the
        // ban; its BanHandler then blocks the account at the connection gate on future joins.
        admin.Ban.Ban(null, client, duration, _config.Reason);
    }

    private void NotifyAdmins(string name, string steamStr, string source, string detail)
    {
        var msg = $" [AntiDLL/{source}] {name} <{steamStr}>: {detail}";
        foreach (var c in _bridge.ClientManager.GetGameClients(inGame: true))
        {
            if (c.IsFakeClient || c.IsHltv)
                continue;
            if (_bridge.AdminManager?.GetAdmin(c.SteamId) is null)
                continue;
            c.Print(HudPrintChannel.Chat, msg);
        }
    }
}
