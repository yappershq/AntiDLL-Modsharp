using System;
using System.Collections.Generic;
using AntiDLL.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace AntiDLL.Modules;

/// <summary>
///     SECONDARY (clearly-weaker) detector — cheat-DLL detection by client-ConVar interrogation.
///
///     This is NOT the upstream CS2-AntiDLL mechanism (that is <see cref="LegacyEventDetector" />, the
///     primary native legacy-event-subscription detector). The cvar probe is kept only as an optional,
///     additive tripwire: it is trivial for a cheat author to rename/hide a cvar, so it is OFF by default
///     and never the sole line of defence. When enabled it runs alongside the primary detector and routes
///     any hit through the same <see cref="DetectionSink" />.
///
///     ModSharp's <see cref="Sharp.Shared.Managers.IClientManager.QueryConVar" /> asks the client for the
///     value of a named cvar; the engine replies asynchronously and ModSharp invokes our callback ON THE
///     GAME THREAD. We fire one query per configured <see cref="DllSignature" /> when a client passes the
///     admin check, evaluate each reply against its rule, and once all outstanding queries for that client
///     have returned we flag (via the sink) if any signature matched.
/// </summary>
internal sealed class DllDetectionModule : IClientListener
{
    public int ListenerVersion  => IClientListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge             _bridge;
    private readonly AntiDllConfig               _config;
    private readonly DetectionSink               _sink;
    private readonly ILogger<DllDetectionModule> _logger;

    private bool _installed;

    // Per-slot in-flight scan state. Keyed by engine slot value. Only ever touched on the game thread
    // (OnClientPostAdminCheck + QueryConVar callbacks both run there), so a plain Dictionary is safe.
    private sealed class ScanState
    {
        public required SteamID      SteamId;
        public required string       SteamStr = string.Empty;
        public          int          Outstanding;
        public readonly List<string> Matches  = [];
        public          bool         Punished;
    }

    private readonly Dictionary<int, ScanState> _scans = new();

    public DllDetectionModule(InterfaceBridge bridge, AntiDllConfig config, DetectionSink sink,
        ILogger<DllDetectionModule> logger)
    {
        _bridge = bridge;
        _config = config;
        _sink   = sink;
        _logger = logger;
    }

    public void Start()
    {
        if (!_config.CvarProbeEnabled)
        {
            _logger.LogInformation("[AntiDLL] Secondary cvar probe disabled by config");
            return;
        }
        if (_config.Signatures.Count == 0)
        {
            _logger.LogInformation("[AntiDLL] Secondary cvar probe enabled but no signatures configured — inactive");
            return;
        }

        _bridge.ClientManager.InstallClientListener(this);
        _installed = true;
        _logger.LogInformation("[AntiDLL] Secondary cvar probe active — {Count} signature(s)", _config.Signatures.Count);
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
        if (_sink.IsExempt(client))
            return;

        var steamId = client.SteamId;
        var slot    = client.Slot.AsPrimitive();
        var state = new ScanState
        {
            SteamId  = steamId,
            SteamStr = ((ulong) steamId).ToString(),
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
                    (c, status, _, value) => OnQueryResult(slot, localSig, c, status, value));
                state.Outstanding++;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "[AntiDLL] QueryConVar('{Cvar}') threw for {Steam}", sig.Cvar, state.SteamStr);
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
        _sink.Act(client, "cvar-probe", string.Join(", ", state.Matches));
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

    // Drop scan state if the player leaves mid-scan.
    public void OnClientDisconnecting(IGameClient client, NetworkDisconnectionReason reason)
        => _scans.Remove(client.Slot.AsPrimitive());
}
