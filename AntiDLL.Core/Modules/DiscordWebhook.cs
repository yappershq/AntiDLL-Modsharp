using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AntiDLL.Modules;

/// <summary>
///     Minimal Discord webhook poster for cheat-signature detections. Best-effort, off the game
///     thread, never throws into the caller. One shared HttpClient. The player IP is never included.
/// </summary>
internal sealed class DiscordWebhook
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ILogger _logger;
    private readonly string  _url;

    public bool Enabled => !string.IsNullOrWhiteSpace(_url);

    public DiscordWebhook(string url, ILogger logger)
    {
        _url    = url ?? string.Empty;
        _logger = logger;
    }

    /// <summary>
    ///     Post a detection embed. action is one of notify|kick|ban. Returns silently on failure.
    /// </summary>
    public async Task PostDetectionAsync(string name, string steamId, string signatures, string action)
    {
        if (!Enabled) return;

        try
        {
            // Red for ban, orange for kick, yellow for notify.
            int color = action.ToLowerInvariant() switch
            {
                "ban"  => 0xE74C3C,
                "kick" => 0xE67E22,
                _      => 0xF1C40F,
            };

            var profile = $"https://steamcommunity.com/profiles/{steamId}";
            var payload = new
            {
                username = "AntiDLL",
                embeds = new[]
                {
                    new
                    {
                        title       = "AntiDLL — cheat signature detected",
                        color,
                        description = $"**{Sanitize(name)}** matched one or more cheat signatures.",
                        fields = new object[]
                        {
                            new { name = "SteamID",    value = $"[{steamId}]({profile})", inline = true },
                            new { name = "Action",     value = action,                    inline = true },
                            new { name = "Signatures", value = string.IsNullOrEmpty(signatures) ? "—" : signatures, inline = false },
                        },
                        timestamp = DateTime.UtcNow.ToString("o"),
                    },
                },
            };

            var json     = JsonSerializer.Serialize(payload);
            using var c   = new StringContent(json, Encoding.UTF8, "application/json");
            using var rsp = await Http.PostAsync(_url, c).ConfigureAwait(false);
            if (!rsp.IsSuccessStatusCode)
                _logger.LogWarning("[AntiDLL] Webhook POST returned {Code}", (int) rsp.StatusCode);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[AntiDLL] Webhook POST failed");
        }
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        s = s.Replace("`", "'").Replace("*", "").Replace("_", "").Replace("@", "@​");
        return s.Length > 64 ? s[..64] : s;
    }
}
