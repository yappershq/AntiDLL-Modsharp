using AntiDLL.Configuration;
using AntiDLL.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;

namespace AntiDLL;

/// <summary>
///     AntiDLL — detects cheat DLLs by interrogating client ConVars and acts (notify / kick / ban).
///
///     ModSharp port inspired by KillStr3aK/CS2-AntiDLL. The upstream detects via the native legacy
///     game-event listen-bits path (not exposed by ModSharp managed APIs); this port uses ModSharp's
///     <see cref="Sharp.Shared.Managers.IClientManager.QueryConVar"/> primitive against a configurable,
///     hot-refreshable signature list instead. See README for the fidelity verdict.
///
///     Lifecycle: cross-plugin interfaces (AdminCommands / AdminManager) resolve in OnAllModulesLoaded.
/// </summary>
public sealed class AntiDllPlugin : IModSharpModule
{
    public string DisplayName   => "AntiDLL";
    public string DisplayAuthor => "yappershq (port of KillStr3aK/CS2-AntiDLL)";

    private readonly ILogger<AntiDllPlugin> _logger;
    private readonly ILoggerFactory         _loggerFactory;
    private readonly InterfaceBridge        _bridge;
    private readonly AntiDllConfig          _config;
    private readonly DllDetectionModule     _detection;

    public AntiDllPlugin(
        ISharedSystem  sharedSystem,
        string         dllPath,
        string         sharpPath,
        System.Version version,
        IConfiguration configuration,
        bool           hotReload)
    {
        _loggerFactory = sharedSystem.GetLoggerFactory();
        _logger        = _loggerFactory.CreateLogger<AntiDllPlugin>();

        _bridge    = new InterfaceBridge(sharpPath, sharedSystem);
        _config    = AntiDllConfig.Load(sharpPath, _loggerFactory.CreateLogger<AntiDllConfig>());
        _detection = new DllDetectionModule(_bridge, _config, _loggerFactory.CreateLogger<DllDetectionModule>());
    }

    public bool Init() => true;

    public void PostInit() { }

    public void OnAllModulesLoaded()
    {
        _bridge.ResolveModules();

        var webhook      = new DiscordWebhook(_config.DiscordWebhook, _logger);
        var sharedBypass = SharedBypass.Load(_bridge.SharpPath, _config.SharedBypassConfig, _logger);
        _detection.Configure(webhook, sharedBypass);

        _detection.Start();

        _logger.LogInformation("[AntiDLL] Loaded (action={Action}, AdminCommands={Admin}, AdminManager={Mgr})",
            _config.Action, _bridge.AdminService is not null, _bridge.AdminManager is not null);

        if (_config is { Enabled: true } && _config.Action != AntiDllAction.Notify && _bridge.AdminService is null
            && _config.Action == AntiDllAction.Ban)
        {
            _logger.LogWarning("[AntiDLL] action=ban but AdminCommands is not loaded — bans will fall back to kicks");
        }
    }

    public void OnLibraryConnected(string name) { }

    public void OnLibraryDisconnect(string name) { }

    public void Shutdown()
    {
        _detection.Stop();
    }
}
