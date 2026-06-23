# AntiDLL (ModSharp)

A ModSharp / CS2 plugin that detects cheat clients by the **legacy game-event subscriptions** they
request — a faithful native port of **[KillStr3aK / JDW1337 CS2-AntiDLL](https://github.com/JDW1337/AntiDLL)**.

> **Default action is `notify` (log only).** The detection path is real (native hook + engine
> `FindListener`), but the gamedata signature and slot offset are reverse-engineered and should be
> confirmed on your live build before enabling `kick`/`ban`. See [Verdict](#verdict).

## How it works

### Primary detector — native legacy-event subscription check (`LegacyEventDetector`)

This is the upstream's actual mechanism. A clean CS2 client subscribes to a small, predictable set of
legacy game events; ESP / radar / triggerbot cheats that read the legacy event stream subscribe to events
a clean client never asks for (`player_footstep`, `bullet_impact`, `player_blind`, `weapon_fire_on_empty`,
…).

1. A **native mid-func hook** is installed on `CSource1LegacyGameEventGameSystem::ListenBitsReceived` —
   the engine fn that runs every time a client sends its legacy game-event subscription bitmask
   (`CLC_ListenEvents`).
2. In the hook, the plugin recovers the client's **engine slot** from the per-client legacy proxy
   (`arg2` → `proxy + 0x58`), behind a user-pointer gate and a bounds check. That is the only native read.
3. Work is marshaled to the **game thread**, where for each blacklisted event the plugin asks the engine
   `IEventManager.FindListener(slot, name)` (= `IGameEventManager2::FindListener`, per-client) whether the
   client subscribed.
4. If subscriptions ≥ `minMatches` match the blacklist → flag → notify / kick / ban.

Using the engine's own name→event-id resolution means the only perishable artifact is a single function
signature; there is no fragile descriptor-table walking on the hot path.

### Secondary detector — cvar probe (`DllDetectionModule`), OFF by default

An optional, trivially-evadable tripwire: `IClientManager.QueryConVar` asks the connecting client for a
configured cvar and matches the reply against a rule. Kept only as an additive signal, **off by default**.

Both detectors funnel through one `DetectionSink`:
- **notify** (default) — log + chat-notify online admins + optional Discord webhook. No punishment.
- **kick** — `KICKED_UNTRUSTEDACCOUNT` (transient).
- **ban** — via AdminCommands `IBanService`, system actor (`admin: null`), `banDurationMinutes`
  (0 = permanent). Falls back to kick if AdminCommands is absent.

## Gamedata

`.assets/gamedata/yappershq.antidll.jsonc` (deployed to `/game/sharp/gamedata/`) declares the one hook
target with a unique Linux signature **and** a `refs.strings` anchor fallback
(`"OnSource1LegacyGameEventListenBitsReceived: game event %i not found."`). The proxy offsets and the
`FindListener` vtable index are recorded there for reference; the running code does not dereference them
directly. Re-derive the signature on CS2 updates if it stops resolving (the detector logs and stays OFF
in that case — it never hooks a bad address).

### Reverse-engineering summary (libserver.so, Linux, stripped)
- `ListenBitsReceived` @ file-vaddr `0x15894C0`, found via the DevMsg string @ `0x90A6E0`.
- arg1 `rdi` = GameSystem, arg2 `rsi` = per-client legacy proxy.
  `proxy+0x48` = listen-bit dword count, `proxy+0x50` = 512-bit listen array, `proxy+0x58` = slot index
  (engine bounds-checks `<= 0x3F`). Confirmed by disassembly; matches the CS:GO source
  `CBaseClient::CLCMsg_ListenEvents`.
- Descriptor table (`base 0x26E0470`, `count 0x26E0468`, stride `0x58`, `eventid @ +0x8`) maps set-bit →
  event id. `IGameEventManager2::FindListener` = vtable index 16. The port resolves subscriptions through
  ModSharp's managed `IEventManager.FindListener` rather than walking these globals.

## Config

### `configs/antidll_legacy_events.json` (primary; written with defaults on first run)
```json
{
  "enabled": true,
  "minMatches": 1,
  "blacklist": [ "player_footstep", "bullet_impact", "player_blind", "weapon_fire_on_empty", "..." ]
}
```
`blacklist` defaults to the canonical 50-event ESP list from the original CS2-AntiDLL
(`data/antidll/events_detection.txt`). Edit and reload (re-load on plugin reload). Raise `minMatches` to
require several ESP-style subscriptions together (stronger signal, fewer false positives).

### `configs/antidll.json` (actions + secondary cvar probe)
```json
{
  "cvarProbeEnabled": false,                  // secondary tripwire, off by default
  "action": "notify",                          // notify | kick | ban
  "banDurationMinutes": 0,                      // 0 = permanent (ban action only)
  "reason": "AntiDLL: cheat signature detected",
  "adminBypass": true,                          // never punish registered admins
  "notifyAdmins": true,                         // chat-notify online admins on a detection
  "discordWebhook": "",                         // optional; never includes player IP
  "whitelist": ["7656..."],                     // SteamID64s exempt from detection
  "sharedBypassConfig": "bypass_steamids.json", // shared with AltGuard / AntiVpnGuard
  "signatures": []                              // optional cvar signatures (see rules below)
}
```

#### cvar match rules
| rule | matches when… |
|---|---|
| `exists` | cvar present (`ValueIntact`/`NotACvar`) — value-independent |
| `missing` | cvar reported `CvarNotFound` |
| `equals` / `not_equals` / `contains` | value comparisons (case-insensitive) |
| `protected` | server not allowed to read it (`CvarProtected`) |

## Verdict

The faithful native legacy-event detection is **fully implemented and builds clean** — it is a real
native hook + engine-authoritative `FindListener` check, not a simulation. It is **"working pending one
live smoke test"**: the gamedata signature and the `proxy+0x58` slot offset are RE-derived and must be
confirmed on a running server (look for `Legacy-event detector ACTIVE` in the log, and verify a known-clean
client produces zero hits) before flipping `action` to `kick`/`ban`. That is exactly why the default is the
non-destructive `notify`.

If the signature ever fails to resolve (engine update), the primary detector logs and stays OFF; the
optional cvar probe is unaffected.

## Thread / pointer safety
- The mid-hook (native context, possibly off-thread) only reads `ctx->rsi`, gates it with a user-ptr
  check, reads the `int` slot at `+0x58`, bounds-checks it, and dispatches. The body is wrapped so it never
  throws into the engine.
- All client/admin/FindListener/punishment work runs on the game thread (`IModSharp.InvokeAction`), with
  the client re-resolved by slot and re-validated before any action.

## Build & deploy
```bash
dotnet build AntiDLL.slnx -c Release
modsharp-deploy . <server-profile>      # outputs to .build/modules/AntiDLL.Core/AntiDLL.dll
```
Requires **AdminCommands** (for `ban`) and **AdminManager** (admin bypass / notifications) — both optional,
resolved in `OnAllModulesLoaded`, degrade gracefully (ban → kick fallback, admin bypass off).

## Credits
- Original concept & detection design: **KillStr3aK / JDW1337 — CS2-AntiDLL**.
- ModSharp native port: this repository.
