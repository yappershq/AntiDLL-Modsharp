using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AntiDLL.Configuration;

/// <summary>
///     Loads a shared SteamID bypass list from configs/&lt;file&gt; that AntiDLL, AltGuard and
///     AntiVpnGuard all read, so one edit exempts a player everywhere.
///     File shape: { "steamIds": ["7656...", "7656..."] }
/// </summary>
public static class SharedBypass
{
    private sealed class File_
    {
        [JsonPropertyName("steamIds")] public List<string> SteamIds { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    /// <summary>
    ///     Returns the SteamID64 strings from configs/&lt;fileName&gt;. Empty set if the file is absent,
    ///     empty, or unparseable (logged once). Never throws.
    /// </summary>
    public static HashSet<string> Load(string sharpPath, string fileName, ILogger logger)
    {
        var result = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(fileName)) return result;

        var path = Path.Combine(sharpPath, "configs", fileName);
        try
        {
            if (!System.IO.File.Exists(path))
                return result; // optional file — silently absent is fine

            var parsed = JsonSerializer.Deserialize<File_>(System.IO.File.ReadAllText(path), Opts);
            if (parsed?.SteamIds is { Count: > 0 })
            {
                foreach (var id in parsed.SteamIds)
                {
                    var t = id?.Trim();
                    if (!string.IsNullOrEmpty(t)) result.Add(t);
                }
                logger.LogInformation("[AntiDLL] Loaded {Count} shared-bypass SteamIDs from {File}", result.Count, fileName);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "[AntiDLL] Failed to read shared bypass file '{Path}'", path);
        }
        return result;
    }
}
