using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminCommands.Shared;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Managers;

namespace AntiDLL;

/// <summary>
///     Holds engine managers and the cross-plugin interfaces AntiDLL consumes. Optional interfaces
///     (AdminCommands, AdminManager) are resolved in OnAllModulesLoaded — ModSharp guarantees all
///     publishers' PostInit finished by then.
/// </summary>
internal sealed class InterfaceBridge
{
    internal string SharpPath { get; }

    internal IModSharp           ModSharp           { get; }
    internal IClientManager      ClientManager      { get; }
    internal ISharpModuleManager SharpModuleManager { get; }
    internal ILoggerFactory      LoggerFactory      { get; }

    // Resolved in OnAllModulesLoaded.
    internal IAdminService? AdminService { get; private set; }
    internal IAdminManager? AdminManager { get; private set; }

    public InterfaceBridge(string sharpPath, ISharedSystem sharedSystem)
    {
        SharpPath          = sharpPath;
        ModSharp           = sharedSystem.GetModSharp();
        ClientManager      = sharedSystem.GetClientManager();
        SharpModuleManager = sharedSystem.GetSharpModuleManager();
        LoggerFactory      = sharedSystem.GetLoggerFactory();
    }

    internal void ResolveModules()
    {
        AdminService = SharpModuleManager
            .GetOptionalSharpModuleInterface<IAdminService>(IAdminService.Identity)?.Instance;
        AdminManager = SharpModuleManager
            .GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity)?.Instance;
    }
}
