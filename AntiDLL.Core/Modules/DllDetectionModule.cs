using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AntiDLL.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminCommands.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace AntiDLL.Modules;

/// <summary>
///     Cheat-DLL detection by client-ConVar interrogation.
///
///     ModSharp's <see cref="Sharp.Shared.Managers.IClientManager.QueryConVar"/> asks the client for
///     the value of a named cvar; the engine replies asynchronously and ModSharp invokes our callback
///     ON THE GAME THREAD with a <see cref="QueryConVarValueStatus"/> and the value. We fire one query
///     per configured <see cref="DllSignature"/> when a client passes the admin check, evaluate each
///     reply against its match rule, accumulate the matches per slot, and once all outstanding queries
///     for that client have returned we punish (notify / kick / ban) if any signature matched.
///
///     This is a DIFFERENT mechanism from upstream CS2-AntiDLL (which hooks the legacy game-event
///     listen-bits path natively — not exposed by ModSharp managed APIs). See README for the verdict.
/// </summary>
internal sealed class DllDetectionModule : IClientListener
{
    public int ListenerVersion  => IClientListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge             _bridge;
    private readonly AntiDllConfig               _config;
    private readonly ILogger<DllDetectionModule> _logger;

    private bool             _installed;
    private DiscordWebhook?  _webhook;
    private HashSet<string>? _sharedBypass;

    // Per-slot in-flight scan state. Keyed by engine slot value. Only ever touched on the game thread
    // (OnClientPostAdminCheck + QueryConVar callbacks both run there), so a plain Dictionary is safe.
    private sealed class ScanState
    {
        public required SteamID      SteamId;
        public required string       SteamStr   = string.Empty;
        public required string       Name       = string.Empty;
        public          int          Outstanding;
        public readonly List<string> Matches    = [];
        public          bool         Punished;
    }

    private readonly Dictionary<int, ScanState> _scans = new();

    public DllDetectionModule(InterfaceBridge bridge, AntiDllConfig config, ILogger<DllDetectionModule> logger)
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

    public void Start()
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("[AntiDLL] Disabled by config");
            return;
        }
        if (_config.Signatures.Count == 0)
        {
            _logger.LogWarning("[AntiDLL] No signatures configured — detection inactive");
            return;
        }

        _bridge.ClientManager.InstallClientListener(this);
        _installed = true;
        _logger.LogInformation("[AntiDLL] Active — action={Action}, signatures={Count}",
            _config.Action, _config.Signatures.Count);
    }

    public void Stop()
    {
        if (_installed)
            _bridge.ClientManager.RemoveClientListener(this);
        _installed = false;
        _scans.Clear();
    }

    // Game thread.
    public void OnClientPostAdminCheck(IGameClient client)
    {
        if (client.IsFakeClient || client.IsHltv)
            return;

        var steamId  = client.SteamId;
        var steamStr = ((ulong) steamId).ToString();

        if (_config.Whitelist.Contains(steamStr) || (_sharedBypass?.Contains(steamStr) ?? false))
            return;

        if (_config.AdminBypass && _bridge.AdminManager?.GetAdmin(steamId) is not null)
            return;

        var slot = client.Slot.AsPrimitive();
        var state = new ScanState
        {
            SteamId  = steamId,
            SteamStr = steamStr,
            Name     = client.Name ?? "?",
        };

        // Issue one query per signature. Each callback decrements Outstanding; when it hits 0 we act.
        foreach (var sig in _config.Signatures)
        {
            if (string.IsNullOrWhiteSpace(sig.Cvar))
                continue;

            var localSig = sig;
            try
            {
                _bridge.ClientManager.QueryConVar(client, sig.Cvar,
                    (c, status, name, value) => OnQueryResult(slot, localSig, c, status, value));
                state.Outstanding++;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "[AntiDLL] QueryConVar('{Cvar}') threw for {Steam}", sig.Cvar, steamStr);
            }
        }

        if (state.Outstanding == 0)
            return;

        _scans[slot] = state;
    }

    // Game thread (ModSharp marshals the engine reply here).
    private void OnQueryResult(int slot, DllSignature sig, IGameClient client, QueryConVarValueStatus status, string value)
    {
        if (!_scans.TryGetValue(slot, out var state))
            return;

        // Guard against a slot being reused by a different player between query and reply.
        if (!client.IsValid || client.SteamId != state.SteamId)
        {
            _scans.Remove(slot);
            return;
        }

        if (Evaluate(sig, status, value))
        {
            var label = string.IsNullOrWhiteSpace(sig.Label) ? sig.Cvar : sig.Label;
            state.Matches.Add($"{label} [{sig.Cvar}={(status == QueryConVarValueStatus.ValueIntact ? value : status.ToString())}]");
        }

        if (--state.Outstanding > 0)
            return;

        _scans.Remove(slot);

        if (state.Matches.Count == 0 || state.Punished)
            return;

        state.Punished = true;
        Act(client, state);
    }

    /// <summary>Decide whether a single query result is incriminating per the signature's rule.</summary>
    private static bool Evaluate(DllSignature sig, QueryConVarValueStatus status, string value)
    {
        return sig.Rule switch
        {
            MatchRule.Exists    => status is QueryConVarValueStatus.ValueIntact or QueryConVarValueStatus.NotACvar,
            MatchRule.Missing   => status == QueryConVarValueStatus.CvarNotFound,
            MatchRule.Protected => status == QueryConVarValueStatus.CvarProtected,
            MatchRule.Equals    => status == QueryConVarValueStatus.ValueIntact &&
                                   string.Equals(value, sig.Value, StringComparison.OrdinalIgnoreCase),
            MatchRule.NotEquals => status == QueryConVarValueStatus.ValueIntact &&
                                   !string.Equals(value, sig.Value, StringComparison.OrdinalIgnoreCase),
            MatchRule.Contains  => status == QueryConVarValueStatus.ValueIntact &&
                                   value.Contains(sig.Value, StringComparison.OrdinalIgnoreCase),
            _                   => false,
        };
    }

    // Game thread.
    private void Act(IGameClient client, ScanState state)
    {
        // Re-validate: the player may have left, or the callback chain may have raced a disconnect.
        if (!client.IsValid || !client.IsInGame || client.IsFakeClient)
            return;

        var sigList = string.Join(", ", state.Matches);
        var action  = _config.Action;

        _logger.LogWarning("[AntiDLL] {Name} ({Steam}) matched {Count} signature(s): {Sigs} — action={Action}",
            state.Name, state.SteamStr, state.Matches.Count, sigList, action);

        if (_config.NotifyAdmins)
            NotifyAdmins(state.Name, state.SteamStr, sigList);

        var actionStr = action.ToString().ToLowerInvariant();
        if (_webhook is { Enabled: true })
        {
            var name     = state.Name;
            var steamStr = state.SteamStr;
            _ = Task.Run(() => _webhook.PostDetectionAsync(name, steamStr, sigList, actionStr));
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
                IssueBan(client);
                break;
        }
    }

    private void IssueBan(IGameClient client)
    {
        if (_bridge.AdminService is not { } admin)
        {
            _logger.LogError("[AntiDLL] AdminService unavailable — falling back to kick for {Steam}",
                (ulong) client.SteamId);
            _bridge.ClientManager.KickClient(client, $"AntiDLL: {_config.Reason}",
                NetworkDisconnectionReason.KickedUntrustedAccount);
            return;
        }

        var duration = _config.BanDurationMinutes <= 0
            ? (TimeSpan?) null
            : TimeSpan.FromMinutes(_config.BanDurationMinutes);

        // null admin = console/system actor. AdminCommands kicks the online target and persists the
        // ban; its BanHandler then blocks the account at the connection gate on future joins.
        admin.Ban.Ban(null, client, duration, _config.Reason);
    }

    private void NotifyAdmins(string name, string steamStr, string sigList)
    {
        var msg = $" [AntiDLL] {name} <{steamStr}> matched cheat signature(s): {sigList}";
        foreach (var c in _bridge.ClientManager.GetGameClients(inGame: true))
        {
            if (c.IsFakeClient || c.IsHltv)
                continue;
            if (_bridge.AdminManager?.GetAdmin(c.SteamId) is null)
                continue;
            c.Print(HudPrintChannel.Chat, msg);
        }
    }

    // Drop scan state if the player leaves mid-scan.
    public void OnClientDisconnecting(IGameClient client, NetworkDisconnectionReason reason)
        => _scans.Remove(client.Slot.AsPrimitive());
}
