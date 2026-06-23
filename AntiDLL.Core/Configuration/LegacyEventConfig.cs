using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AntiDLL.Configuration;

/// <summary>
///     Configuration for the PRIMARY detector: the legacy game-event subscription check
///     (faithful to KillStr3aK / JDW1337 CS2-AntiDLL).
///
///     A clean CS2 client subscribes to a small, predictable set of legacy game events. Cheats that
///     drive ESP / radar / triggerbot from the legacy event stream subscribe to events a clean client
///     never asks for (footsteps, bullet impacts, blind, weapon-fire-on-empty, …). When the engine
///     fn <c>CSource1LegacyGameEventGameSystem::ListenBitsReceived</c> fires (the client sent
///     <c>CLC_ListenEvents</c>), the plugin asks the engine — via
///     <see cref="Sharp.Shared.Managers.IEventManager.FindListener" /> — whether the client subscribed
///     to any blacklisted event. Any hit is a detection.
///
///     Shipped as DATA (a hot-reloadable JSON list) so the blacklist can be tuned without recompiling.
/// </summary>
public sealed class LegacyEventConfig
{
    /// <summary>Master switch for the legacy-event detector. The native detour is only installed when true.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>
    ///     How many distinct blacklisted subscriptions a client must hold before it is flagged. 1 = flag on
    ///     the first blacklisted event (strict). Raise to require multiple ESP-style subscriptions together,
    ///     which is a stronger signal and less false-positive-prone with a broad list.
    /// </summary>
    [JsonPropertyName("minMatches")] public int MinMatches { get; set; } = 1;

    /// <summary>
    ///     The legacy events a clean client never subscribes to. Default = the canonical 50-event ESP/cheat
    ///     list from the original CS2-AntiDLL (data/antidll/events_detection.txt). Extend as needed.
    /// </summary>
    [JsonPropertyName("blacklist")] public List<string> Blacklist { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    ///     The canonical ESP/cheat legacy-event list shipped by the original CS2-AntiDLL
    ///     (JDW1337/AntiDLL data/antidll/events_detection.txt — 50 events, verbatim).
    /// </summary>
    public static List<string> DefaultBlacklist() =>
    [
        "other_death", "item_purchase", "hostage_rescued_all", "hostage_call_for_help", "vip_escaped",
        "vip_killed", "player_radio", "weapon_fire_on_empty", "grenade_thrown", "weapon_outofammo",
        "silencer_detach", "player_spawned", "item_remove", "enter_rescue_zone", "exit_rescue_zone",
        "silencer_off", "silencer_on", "round_prestart", "round_poststart", "grenade_bounce",
        "molotov_detonate", "tagrenade_detonate", "inferno_extinguish", "decoy_firing", "bullet_impact",
        "player_footstep", "player_jump", "player_blind", "player_falldamage", "door_moving",
        "mb_input_lock_success", "mb_input_lock_cancel", "nav_blocked", "nav_generate", "spec_mode_updated",
        "hltv_changed_mode", "freezecam_started", "repost_xbox_achievements", "match_end_conditions",
        "player_decal", "client_disconnect", "gg_player_levelup", "trial_time_expired",
        "enable_restart_voting", "sfuievent", "start_vote", "tr_player_flashbanged", "tr_highlight_ammo",
        "tr_exit_hint_trigger", "reset_player_controls", "teamchange_pending", "material_default_complete",
        "cs_handle_ime_event", "start_halftime", "dz_item_interaction", "guardian_wave_restart",
    ];

    public static LegacyEventConfig Load(string sharpPath, ILogger logger)
    {
        var path = Path.Combine(sharpPath, "configs", "antidll_legacy_events.json");
        try
        {
            if (!File.Exists(path))
            {
                var def = new LegacyEventConfig { Blacklist = DefaultBlacklist() };
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(def, JsonOpts));
                logger.LogInformation("[AntiDLL] Wrote default legacy-event blacklist to {Path}", path);
                return def;
            }

            var cfg = JsonSerializer.Deserialize<LegacyEventConfig>(File.ReadAllText(path), JsonOpts);
            if (cfg is null)
            {
                logger.LogError("[AntiDLL] antidll_legacy_events.json deserialized to null — using defaults");
                return new LegacyEventConfig { Blacklist = DefaultBlacklist() };
            }
            if (cfg.Blacklist.Count == 0)
            {
                logger.LogWarning("[AntiDLL] legacy-event blacklist is empty — falling back to the default list");
                cfg.Blacklist = DefaultBlacklist();
            }
            return cfg;
        }
        catch (Exception e)
        {
            logger.LogError(e, "[AntiDLL] Failed to load antidll_legacy_events.json — using defaults");
            return new LegacyEventConfig { Blacklist = DefaultBlacklist() };
        }
    }
}
