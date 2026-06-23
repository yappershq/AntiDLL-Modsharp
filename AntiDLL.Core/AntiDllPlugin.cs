using AntiDLL.Configuration;
using AntiDLL.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;

namespace AntiDLL;

/// <summary>
///     AntiDLL — faithful ModSharp port of KillStr3aK / JDW1337 CS2-AntiDLL.
///
///     PRIMARY detector (<see cref="LegacyEventDetector" />): a native mid-hook on
///     <c>CSource1LegacyGameEventGameSystem::ListenBitsReceived</c>. Every time a client sends its legacy
///     game-event subscription bitmask (<c>CLC_ListenEvents</c>), the plugin recovers the client slot from
///     the per-client legacy proxy and asks the engine (<c>IGameEventManager2::FindListener</c>, via
///     ModSharp's <c>IEventManager.FindListener</c>) whether the client subscribed to any blacklisted ESP
///     event a clean client never would. This is the upstream's actual detection mechanism.
///
///     SECONDARY detector (<see cref="DllDetectionModule" />): an optional, OFF-by-default cvar-probe
///     tripwire (trivially evadable; additive signal only).
///
///     Both funnel through a shared <see cref="DetectionSink" /> (notify / kick / ban; default = notify).
///
///     Lifecycle: gamedata is registered + the native hook target resolved relative to module load; the
///     cross-plugin interfaces (AdminCommands / AdminManager) resolve in <c>OnAllModulesLoaded</c>, and the
///     detectors are started there so they have the resolved admin services for bypass / banning.
/// </summary>
public sealed class AntiDllPlugin : IModSharpModule
{
    public string DisplayName   => "AntiDLL";
    public string DisplayAuthor => "yappershq (port of KillStr3aK / JDW1337 CS2-AntiDLL)";

    private const string GameDataKey = "yappershq.antidll";

    private readonly ILogger<AntiDllPlugin> _logger;
    private readonly ILoggerFactory         _loggerFactory;
    private readonly InterfaceBridge        _bridge;
    private readonly AntiDllConfig          _config;
    private readonly LegacyEventConfig      _legacyConfig;
    private readonly DetectionSink          _sink;
    private readonly LegacyEventDetector    _legacy;
    private readonly DllDetectionModule     _cvarProbe;

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

        _bridge       = new InterfaceBridge(sharpPath, sharedSystem);
        _config       = AntiDllConfig.Load(sharpPath, _loggerFactory.CreateLogger<AntiDllConfig>());
        _legacyConfig = LegacyEventConfig.Load(sharpPath, _loggerFactory.CreateLogger<LegacyEventConfig>());

        _sink      = new DetectionSink(_bridge, _config, _loggerFactory.CreateLogger<DetectionSink>());
        _legacy    = new LegacyEventDetector(_bridge, _legacyConfig, _sink,
                         _loggerFactory.CreateLogger<LegacyEventDetector>());
        _cvarProbe = new DllDetectionModule(_bridge, _config, _sink,
                         _loggerFactory.CreateLogger<DllDetectionModule>());
    }

    public bool Init()
    {
        // Register gamedata early so the native hook target resolves before we install the detour.
        try
        {
            _bridge.GameData.Register(GameDataKey);
        }
        catch (System.Exception e)
        {
            _logger.LogWarning(e, "[AntiDLL] gamedata register '{Key}' failed — primary detector may be unavailable",
                GameDataKey);
        }

        return true;
    }

    public void PostInit() { }

    public void OnAllModulesLoaded()
    {
        _bridge.ResolveModules();

        var webhook      = new DiscordWebhook(_config.DiscordWebhook, _logger);
        var sharedBypass = SharedBypass.Load(_bridge.SharpPath, _config.SharedBypassConfig, _logger);
        _sink.Configure(webhook, sharedBypass);

        // PRIMARY — native legacy-event detector. Stays OFF (logged) if the signature fails to resolve.
        _legacy.Install();

        // SECONDARY — optional cvar probe.
        _cvarProbe.Start();

        _logger.LogInformation(
            "[AntiDLL] Loaded — action={Action}, primary(legacy-events)={Primary}, secondary(cvar-probe)={Cvar}, "
            + "AdminCommands={Admin}, AdminManager={Mgr}",
            _config.Action, _legacy.IsInstalled, _config.CvarProbeEnabled,
            _bridge.AdminService is not null, _bridge.AdminManager is not null);

        if (_config.Action == AntiDllAction.Ban && _bridge.AdminService is null)
            _logger.LogWarning("[AntiDLL] action=ban but AdminCommands is not loaded — bans will fall back to kicks");
    }

    public void OnLibraryConnected(string name) { }

    public void OnLibraryDisconnect(string name) { }

    public void Shutdown()
    {
        _legacy.Uninstall();
        _cvarProbe.Stop();

        try
        {
            _bridge.GameData.Unregister(GameDataKey);
        }
        catch
        {
            // best-effort
        }
    }
}
