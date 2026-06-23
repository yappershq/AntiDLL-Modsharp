# AntiDLL (ModSharp)

A ModSharp / CS2 plugin that detects cheat clients by **interrogating client ConVars** and acts
(notify / kick / ban). Inspired by and credited to **[KillStr3aK/CS2-AntiDLL](https://github.com/KillStr3aK/CS2-AntiDLL)**.

> **Default action is `notify` (log only).** Read the [Fidelity verdict](#fidelity-verdict) before
> enabling enforcement — the bundled signature list is a starting point, not a verified truth.

## What it does

When a player passes the admin check (`OnClientPostAdminCheck` — Steam-authenticated, in-game), the
plugin issues an `IClientManager.QueryConVar` for every configured signature's ConVar. The engine
replies asynchronously (on the game thread); each reply is matched against that signature's rule. If
any signature matches, the configured action fires:

- **notify** (default) — log + notify online admins in chat + optional Discord webhook. No punishment.
- **kick** — disconnect with `KICKED_UNTRUSTEDACCOUNT` (transient; player may rejoin).
- **ban** — ban via AdminCommands `IBanService` attributed to the system actor (`admin: null`), for
  `banDurationMinutes` (0 = permanent). Falls back to a kick if AdminCommands is not loaded.

## Relationship to upstream CS2-AntiDLL — important

The original CS2-AntiDLL does **not** use ConVar queries. It hooks the engine's *legacy game-event
listen-bits* path natively (`CSource1LegacyGameEventGameSystem::ListenBitsReceived` +
`IGameEventManager2::FindListener`) and flags clients that subscribe to game events a clean client
never would (footstep / bullet_impact / player_blind / weapon_fire, etc.). That is a native-hook,
gamedata-signature plugin.

**ModSharp does not expose those natives as first-class managed APIs.** A faithful port would require
re-resolving three server-binary signatures (drift on every CS2 update) and installing native detours.
This port instead uses ModSharp's supported `QueryConVar` primitive against a **configurable,
hot-refreshable signature list** — a different, additive detection model with a clean public API and
no perishable byte signatures.

### Not portable as-is
- The upstream's native game-event-listener detection (the legacy `IGameEventManager2` listen-bits
  hook) is **not portable** to a managed-only ModSharp plugin. Reproducing it needs native detours +
  freshly re-resolved gamedata signatures (`libserver.so`), which is out of scope for this managed port.
- The upstream `OnDetection` consumer-callback extension point is not reproduced (no second plugin
  consuming detections); the equivalent here is the notify/webhook output.

## Fidelity verdict

- **Mechanism (ConVar query):** sound and supported by ModSharp, but **weaker** than the upstream's
  event-listener approach. A cvar-based signature is easier for a cheat author to rename/hide than
  engine-internal subscription bookkeeping. Treat this as a tripwire layer, not a definitive AC.
- **Signature list:** the bundled `signatures` are **EXAMPLES / PLACEHOLDERS** and must be reviewed
  against current cheats **and a known-clean client** before enabling `kick`/`ban`. An over-aggressive
  list will false-positive on legit clients / HLTV / casters.
- **Default is conservative on purpose:** `notify` only. Operators must opt into enforcement after
  validating, so a stale signature never auto-permabans an innocent player.
- **Keep signatures current.** Like all signature-based detection, the list decays as cheats change.

## Config — `configs/antidll.json`

Written with defaults on first run.

```json
{
  "enabled": true,
  "action": "notify",                       // notify | kick | ban
  "banDurationMinutes": 0,                   // 0 = permanent (ban action only)
  "reason": "AntiDLL: cheat signature detected",
  "adminBypass": true,                       // never punish registered admins
  "notifyAdmins": true,                      // chat-notify online admins on a detection
  "discordWebhook": "",                      // optional; never includes player IP
  "whitelist": ["7656..."],                  // SteamID64s exempt from detection
  "sharedBypassConfig": "bypass_steamids.json", // shared with AltGuard / AntiVpnGuard
  "signatures": [
    { "cvar": "some_cheat_cvar", "rule": "exists",     "label": "ExampleCheat" },
    { "cvar": "m_pitch",         "rule": "not_equals", "value": "0.022", "label": "pitch tamper" }
  ]
}
```

### Match rules
| rule         | matches when…                                                              |
|--------------|----------------------------------------------------------------------------|
| `exists`     | cvar is present (`ValueIntact`/`NotACvar`) — value-independent, strongest   |
| `missing`    | cvar reported `CvarNotFound` (a stock cvar was stripped)                    |
| `equals`     | value equals `value` (case-insensitive)                                    |
| `not_equals` | value present and differs from `value`                                      |
| `contains`   | value contains `value` (case-insensitive)                                  |
| `protected`  | server not allowed to read it (`CvarProtected`) — the protection is the tell|

The bypass list reuses the shared `bypass_steamids.json` (`{ "steamIds": [...] }`) read by AltGuard
and AntiVpnGuard — one edit exempts a player everywhere.

## Thread / pointer safety
- `QueryConVar` callbacks run on the game thread; the in-flight scan map is touched only there.
- Before punishing, the client is re-validated (`IsValid` + `IsInGame` + SteamID still matches the
  slot) — no native object is stored across the async query, and a slot reused by a new player aborts
  the stale scan.
- Scan state is dropped on `OnClientDisconnecting`.

## Build & deploy

```bash
dotnet build AntiDLL.slnx -c Release
modsharp-deploy . <server-profile>      # outputs to .build/modules/AntiDLL.Core/AntiDLL.dll
```

Requires the **AdminCommands** module (for `ban` action) and **AdminManager** (for admin bypass /
admin notifications). Both are optional and resolved in `OnAllModulesLoaded`; absence degrades
gracefully (ban → kick fallback, admin bypass off).

## Credits
- Original concept & detection design: **KillStr3aK / CS2-AntiDLL**.
- ModSharp port: this repository.
