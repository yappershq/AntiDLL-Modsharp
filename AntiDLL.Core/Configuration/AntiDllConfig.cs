using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AntiDLL.Configuration;

/// <summary>What to do when a connecting player matches a cheat signature.</summary>
public enum AntiDllAction
{
    /// <summary>Only log + (optionally) webhook + message online admins. Never kicks/bans. SAFE DEFAULT.</summary>
    Notify,

    /// <summary>Kick the client (transient — they can rejoin). Conservative enforcement.</summary>
    Kick,

    /// <summary>Ban the client for <see cref="AntiDllConfig.BanDurationMinutes"/> (0 = permanent).</summary>
    Ban,
}

/// <summary>
///     How a queried ConVar value is matched against a signature definition.
/// </summary>
public enum MatchRule
{
    /// <summary>Match if the cvar EXISTS at all (status == ValueIntact / NotACvar). Use for cheat-injected cvars
    ///     that a clean client simply does not have. This is the strongest, value-independent rule.</summary>
    Exists,

    /// <summary>Match if the cvar is reported MISSING (status == CvarNotFound) — for detecting that a stock cvar
    ///     was stripped/renamed by a cheat. Rarely needed.</summary>
    Missing,

    /// <summary>Match if status == ValueIntact AND value equals <see cref="DllSignature.Value"/> (case-insensitive).</summary>
    Equals,

    /// <summary>Match if status == ValueIntact AND value does NOT equal <see cref="DllSignature.Value"/>.
    ///     Use to catch a protected/forced cvar being set to a non-default value.</summary>
    NotEquals,

    /// <summary>Match if status == ValueIntact AND value contains <see cref="DllSignature.Value"/> (case-insensitive).</summary>
    Contains,

    /// <summary>Match if the server is NOT ALLOWED to read the value (status == CvarProtected). Some cheats mark
    ///     their cvars protected to dodge value inspection — the protection itself is the tell.</summary>
    Protected,
}

/// <summary>
///     A single cheat signature: a client ConVar to query plus the rule that decides whether the
///     queried result is incriminating. Shipped as DATA so the list can be refreshed as cheats change
///     without recompiling.
/// </summary>
public sealed class DllSignature
{
    /// <summary>The client ConVar name to query (e.g. an injected cheat cvar).</summary>
    [JsonPropertyName("cvar")] public string Cvar { get; set; } = string.Empty;

    /// <summary>exists | missing | equals | not_equals | contains | protected. Default exists.</summary>
    [JsonPropertyName("rule")] public string RuleRaw { get; set; } = "exists";

    /// <summary>Comparison value for equals / not_equals / contains rules. Ignored otherwise.</summary>
    [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;

    /// <summary>Human-readable label for logs / webhook (e.g. the cheat name this signature targets).</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;

    [JsonIgnore]
    public MatchRule Rule => RuleRaw.Trim().ToLowerInvariant() switch
    {
        "missing"    => MatchRule.Missing,
        "equals"     => MatchRule.Equals,
        "not_equals" => MatchRule.NotEquals,
        "contains"   => MatchRule.Contains,
        "protected"  => MatchRule.Protected,
        _            => MatchRule.Exists,
    };
}

public sealed class AntiDllConfig
{
    /// <summary>
    ///     Opt-in switch for the SECONDARY cvar-probe detector. OFF by default — the cvar probe is a
    ///     trivially-evadable tripwire kept only as an additive signal. The PRIMARY native legacy-event
    ///     detector is governed separately by <c>configs/antidll_legacy_events.json</c> and is on by default.
    /// </summary>
    [JsonPropertyName("cvarProbeEnabled")] public bool CvarProbeEnabled { get; set; } = false;

    /// <summary>
    ///     notify | kick | ban. DEFAULTS TO notify — signatures port from a ~1yr-old upstream and may be
    ///     stale; auto-banning on a stale/false-positive signature is destructive. Operators must opt into
    ///     enforcement explicitly after validating the list against a known-clean client.
    /// </summary>
    [JsonPropertyName("action")] public string ActionRaw { get; set; } = "notify";

    /// <summary>Ban length in minutes for the ban action. 0 = permanent.</summary>
    [JsonPropertyName("banDurationMinutes")] public int BanDurationMinutes { get; set; } = 0;

    /// <summary>Reason string used on the kick/ban + admin notification.</summary>
    [JsonPropertyName("reason")] public string Reason { get; set; } = "AntiDLL: cheat signature detected";

    /// <summary>Skip players who are registered admins (avoid punishing staff on a false positive).</summary>
    [JsonPropertyName("adminBypass")] public bool AdminBypass { get; set; } = true;

    /// <summary>Print a chat notice to online admins when a detection fires (in any action mode).</summary>
    [JsonPropertyName("notifyAdmins")] public bool NotifyAdmins { get; set; } = true;

    /// <summary>Optional Discord webhook URL. Empty disables webhook posting. Never includes the player IP.</summary>
    [JsonPropertyName("discordWebhook")] public string DiscordWebhook { get; set; } = string.Empty;

    /// <summary>SteamID64s exempt from detection entirely.</summary>
    [JsonPropertyName("whitelist")] public List<string> Whitelist { get; set; } = [];

    /// <summary>
    ///     Optional shared bypass file (in configs/) read by AntiDLL/AltGuard/AntiVpnGuard:
    ///     { "steamIds": ["7656..."] }. Merged on top of <see cref="Whitelist"/>.
    /// </summary>
    [JsonPropertyName("sharedBypassConfig")] public string SharedBypassConfig { get; set; } = "bypass_steamids.json";

    /// <summary>The signature list. Each entry = one client cvar query + match rule.</summary>
    [JsonPropertyName("signatures")] public List<DllSignature> Signatures { get; set; } = [];

    [JsonIgnore]
    public AntiDllAction Action => ActionRaw.Trim().ToLowerInvariant() switch
    {
        "kick" => AntiDllAction.Kick,
        "ban"  => AntiDllAction.Ban,
        _      => AntiDllAction.Notify,
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AntiDllConfig Load(string sharpPath, ILogger logger)
    {
        var path = Path.Combine(sharpPath, "configs", "antidll.json");
        try
        {
            if (!File.Exists(path))
            {
                // No example/placeholder signatures shipped — the secondary cvar probe is opt-in and a
                // bogus default cvar list would only invite false positives. Operators add their own.
                var def = new AntiDllConfig();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(def, JsonOpts));
                logger.LogInformation("[AntiDLL] Wrote default config to {Path}", path);
                return def;
            }

            var cfg = JsonSerializer.Deserialize<AntiDllConfig>(File.ReadAllText(path), JsonOpts);
            if (cfg is null)
            {
                logger.LogError("[AntiDLL] antidll.json deserialized to null — using defaults");
                return new AntiDllConfig();
            }
            return cfg;
        }
        catch (Exception e)
        {
            logger.LogError(e, "[AntiDLL] Failed to load antidll.json — using defaults");
            return new AntiDllConfig();
        }
    }
}
