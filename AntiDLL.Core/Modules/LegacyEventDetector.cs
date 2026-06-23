using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AntiDLL.Configuration;
using AntiDLL.Native;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace AntiDLL.Modules;

/// <summary>
///     PRIMARY detector — faithful native port of KillStr3aK / JDW1337 CS2-AntiDLL.
///
///     Installs a native mid-function hook on <c>CSource1LegacyGameEventGameSystem::ListenBitsReceived</c>,
///     the engine fn that runs every time a client sends <c>CLC_ListenEvents</c> (its legacy game-event
///     subscription bitmask). In the hook we read the per-client legacy-event proxy that arg2 (SysV rsi)
///     points at and recover the player's engine slot from it:
///     <code>
///         proxy + 0x48 = int   listen-bit dword count
///         proxy + 0x50 = void* listen-bit array (512-bit field, indexed by engine event id)
///         proxy + 0x58 = int   per-client proxy slot index (== engine player slot, bounds &lt;= 0x3F)
///     </code>
///     The hook itself only READS the slot (a tiny, bounded native deref behind <see cref="NativeUtil.IsUserPtr"/>),
///     then marshals to the game thread. There, for each blacklisted event, it asks the engine
///     <see cref="Sharp.Shared.Managers.IEventManager.FindListener" /> (= <c>IGameEventManager2::FindListener</c>,
///     per-slot) whether that client subscribed. Using the engine's own name→event-id resolution means the
///     detector needs ZERO fragile descriptor-table reverse engineering on the hot path — only the one
///     gamedata signature for the hook target itself.
///
///     <para><b>Thread safety:</b> the mid-hook runs in native engine context (possibly off the game
///     thread). It performs only the bounded slot read, captures the int slot, and dispatches the actual
///     FindListener + punishment work via <c>IModSharp.InvokeFrameAction</c> onto the game thread, where
///     the client handle is re-resolved and re-validated. No managed/native object is retained across the
///     boundary. The hook body is wrapped so it never throws into the engine.</para>
///
///     <para>The hook is observe-only (a mid-func hook, no trampoline). It never alters the subscription —
///     it cannot break game-event delivery.</para>
/// </summary>
internal sealed unsafe class LegacyEventDetector
{
    private const string ListenBitsKey = "CSource1LegacyGameEventGameSystem::ListenBitsReceived";

    // proxy field offsets (see gamedata / docs/FINDINGS.md). Linux build; documented in gamedata.
    private const int OffsetSlotIndex = 0x58;

    private readonly InterfaceBridge   _bridge;
    private readonly LegacyEventConfig _config;
    private readonly DetectionSink     _sink;
    private readonly ILogger           _logger;

    private IMidFuncHook? _hook;
    private bool          _installed;

    // The detour is a static UnmanagedCallersOnly fn — it can't capture instance state, so the live
    // detector is published to this static slot at install time and cleared on uninstall.
    private static LegacyEventDetector? _active;

    private string[] _blacklist = [];

    public LegacyEventDetector(InterfaceBridge bridge, LegacyEventConfig config, DetectionSink sink, ILogger logger)
    {
        _bridge = bridge;
        _config = config;
        _sink   = sink;
        _logger = logger;
    }

    public void Install()
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("[AntiDLL] Legacy-event detector disabled by config");
            return;
        }

        _blacklist = _config.Blacklist.ToArray();
        if (_blacklist.Length == 0)
        {
            _logger.LogWarning("[AntiDLL] Legacy-event blacklist is empty — primary detector inactive");
            return;
        }

        // Resolve the hook target from gamedata. If the signature does not resolve (engine update / wrong
        // build), the detector stays OFF and logs — it never installs a hook at a bad address.
        nint addr;
        try
        {
            if (!_bridge.GameData.GetAddress(ListenBitsKey, out addr) || addr == 0)
            {
                _logger.LogError(
                    "[AntiDLL] Could not resolve gamedata '{Key}' — legacy-event detector OFF. "
                    + "Re-derive the signature for this CS2 build (see docs/FINDINGS.md). "
                    + "The secondary cvar probe still runs if enabled.", ListenBitsKey);
                return;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[AntiDLL] gamedata lookup '{Key}' threw — legacy-event detector OFF", ListenBitsKey);
            return;
        }

        if (!NativeUtil.IsUserPtr(addr))
        {
            _logger.LogError("[AntiDLL] Resolved '{Key}' to non-user address 0x{Addr:X} — detector OFF", ListenBitsKey, addr);
            return;
        }

        try
        {
            _active = this;
            _hook   = _bridge.HookManager.CreateMidFuncHook();
            _hook.Prepare(addr, (nint) (delegate* unmanaged[Cdecl]<MidHookContext*, void>) &OnListenBits);

            if (!_hook.Install())
            {
                _logger.LogError("[AntiDLL] Failed to install ListenBitsReceived mid-hook at 0x{Addr:X}", addr);
                _hook.Dispose();
                _hook   = null;
                _active = null;
                return;
            }

            _installed = true;
            _logger.LogInformation(
                "[AntiDLL] Legacy-event detector ACTIVE — hooked ListenBitsReceived @0x{Addr:X}, {Count} blacklisted events, minMatches={Min}",
                addr, _blacklist.Length, Math.Max(1, _config.MinMatches));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[AntiDLL] Exception installing ListenBitsReceived hook — detector OFF");
            _hook?.Dispose();
            _hook   = null;
            _active = null;
        }
    }

    public void Uninstall()
    {
        if (_hook is not null)
        {
            try
            {
                _hook.Uninstall();
                _hook.Dispose();
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "[AntiDLL] Error uninstalling ListenBitsReceived hook");
            }
        }

        _hook      = null;
        _installed = false;
        if (ReferenceEquals(_active, this))
            _active = null;
    }

    public bool IsInstalled => _installed;

    // ── Native mid-hook (engine context, possibly off the game thread) ───────────────────────────────
    // SysV ABI: arg1 = rdi (GameSystem this), arg2 = rsi (the per-client CServerSideClient legacy proxy).
    // We only read the slot index from the proxy here, then bounce the real work to the game thread.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnListenBits(MidHookContext* ctx)
    {
        var self = _active;
        if (self is null)
            return;

        try
        {
            var proxy = ctx->rsi;
            if (!NativeUtil.IsUserPtr(proxy))
                return;

            // proxy + 0x58 = per-client proxy slot index (engine bounds-checks it <= 0x3F).
            var slotIndex = *(int*) (proxy + OffsetSlotIndex);
            if (slotIndex < 0 || slotIndex > 0x3F)
                return;

            // Hand off to the game thread: re-resolve the client by slot and run the FindListener checks
            // there (FindListener / IClientManager / punishment must all run on the game thread).
            self.DispatchCheck(slotIndex);
        }
        catch
        {
            // Boundary guard — never let a managed exception unwind into the engine.
        }
    }

    private void DispatchCheck(int slotIndex)
    {
        // InvokeAction runs immediately if already on the main thread, otherwise queues to end of frame.
        _bridge.ModSharp.InvokeAction(() => CheckClientOnGameThread(slotIndex));
    }

    // ── Game thread ─────────────────────────────────────────────────────────────────────────────────
    private void CheckClientOnGameThread(int slotIndex)
    {
        try
        {
            var slot   = new PlayerSlot((byte) slotIndex);
            var client = _bridge.ClientManager.GetGameClient(slot);
            // Validity gate is IsInGame (never just IsValid/IsConnected): the legacy listen-bits packet can
            // arrive during the loading/limbo window where the client is half-valid. Bots/HLTV are filtered
            // by IsExempt below.
            if (client is not { IsInGame: true })
                return;

            if (_sink.IsExempt(client))
                return;

            var hits     = new List<string>();
            var minMatch = Math.Max(1, _config.MinMatches);

            foreach (var ev in _blacklist)
            {
                if (string.IsNullOrWhiteSpace(ev))
                    continue;

                bool subscribed;
                try
                {
                    // Engine-authoritative per-client subscription check (IGameEventManager2::FindListener).
                    subscribed = _bridge.EventManager.FindListener(slot, ev);
                }
                catch (Exception e)
                {
                    _logger.LogDebug(e, "[AntiDLL] FindListener('{Event}') threw for slot {Slot}", ev, slotIndex);
                    continue;
                }

                if (subscribed)
                {
                    hits.Add(ev);
                    // Fast-exit once the threshold is met, unless we want the full list for the report.
                    if (hits.Count >= minMatch && minMatch == 1)
                        break;
                }
            }

            if (hits.Count < minMatch)
                return;

            var detail = $"subscribed to blacklisted legacy event(s): {string.Join(", ", hits)}";
            _sink.Act(client, "legacy-events", detail);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[AntiDLL] Legacy-event check failed for slot {Slot}", slotIndex);
        }
    }
}
