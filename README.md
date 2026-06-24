<div align="center">
  <h1><strong>AntiDLL</strong></h1>
  <p>Detect injected cheat DLLs in CS2 and punish offenders — a ModSharp port of CS2-AntiDLL.</p>
</div>

<p align="center">
  <a href="https://github.com/Kxnrl/modsharp-public"><img src="https://img.shields.io/badge/framework-ModSharp-5865F2?logo=github" alt="ModSharp"></a>
  <img src="https://img.shields.io/badge/game-CS2-orange" alt="CS2">
  <img src="https://img.shields.io/github/stars/yappershq/AntiDLL-Modsharp?style=flat&logo=github" alt="Stars">
</p>

---

AntiDLL is a ModSharp/CS2 server module that flags clients running cheat DLLs. Its primary detector is a native hook on the engine's legacy game-event subscription path: a clean client only ever subscribes to a small, predictable set of legacy events, so a client that subscribes to ESP/radar/triggerbot events a clean client never asks for gives itself away. An optional secondary detector probes client ConVars for cheat-injected values. Detections funnel through one action sink — **notify** (default), **kick**, or **ban**. It is a faithful port of [KillStr3aK/JDW1337's CS2-AntiDLL](https://github.com/KillStr3aK/CS2-AntiDLL).

## 🚀 Install

Copy the build output into your ModSharp install (`<sharp>` = your `sharp` directory):

| From | To |
|------|----|
| `.build/modules/AntiDLL.Core/` | `<sharp>/modules/AntiDLL.Core/` |
| `.assets/gamedata/yappershq.antidll.jsonc` | `<sharp>/gamedata/yappershq.antidll.jsonc` |

Restart the server (or change map) to load. The config files (`configs/antidll.json`, `configs/antidll_legacy_events.json`) are written with safe defaults on first run.

Optional integrations: if **AdminCommands** is loaded, `action = ban` issues real bans (otherwise it falls back to kick); if **AdminManager** is loaded, `adminBypass` exempts registered admins from detection.

## ⚙️ Configuration

### `configs/antidll.json`

| Setting | Default | Meaning |
|---------|---------|---------|
| `action` | `notify` | `notify` \| `kick` \| `ban`. Default only logs/notifies — opt into enforcement after validating signatures against a clean client. |
| `banDurationMinutes` | `0` | Ban length in minutes for `action = ban`. `0` = permanent. |
| `reason` | `AntiDLL: cheat signature detected` | Reason shown on kick/ban and in the admin notice. |
| `adminBypass` | `true` | Skip registered admins (avoids punishing staff on a false positive). |
| `notifyAdmins` | `true` | Print a chat notice to online admins when a detection fires (any action mode). |
| `discordWebhook` | `""` | Optional Discord webhook URL. Empty disables posting. Never includes player IP. |
| `whitelist` | `[]` | SteamID64s exempt from detection entirely. |
| `sharedBypassConfig` | `bypass_steamids.json` | Optional shared bypass file in `configs/` (`{ "steamIds": [...] }`), merged on top of `whitelist`. |
| `cvarProbeEnabled` | `false` | Opt-in switch for the secondary cvar-probe detector (trivially evadable; additive signal only). |
| `signatures` | `[]` | Secondary-detector signatures: each is a client ConVar to query + a match rule. |

Each `signatures` entry: `cvar` (client ConVar to query), `rule` (`exists` \| `missing` \| `equals` \| `not_equals` \| `contains` \| `protected`, default `exists`), `value` (comparison value for the value-based rules), `label` (human-readable name for logs/webhook).

### `configs/antidll_legacy_events.json`

| Setting | Default | Meaning |
|---------|---------|---------|
| `enabled` | `true` | Master switch for the primary legacy-event detector. The native hook is installed only when `true`. |
| `minMatches` | `1` | Distinct blacklisted subscriptions a client must hold before it is flagged. Raise for a stronger, lower-false-positive signal. |
| `blacklist` | 50-event list | Legacy events a clean client never subscribes to (the canonical CS2-AntiDLL list). Editable; an empty list falls back to the default. |

## 🔧 How it works

The primary detector installs a native hook on `CSource1LegacyGameEventGameSystem::ListenBitsReceived`, which fires whenever a client sends `CLC_ListenEvents`. The plugin recovers the client's engine slot and asks the engine (via ModSharp's `IEventManager.FindListener`) whether that client subscribed to any blacklisted legacy event; a hit at or above `minMatches` is a detection. The hook target is resolved from gamedata (a byte signature with a string-anchored fallback) reverse-engineered from the Linux `libserver.so` — the detector cleanly no-ops if the signature fails to resolve. The optional secondary detector queries client ConVars and matches the result against the configured signatures.

## 📦 Build

```bash
dotnet build -c Release
```

Outputs `.build/modules/AntiDLL.Core/AntiDLL.dll`. Ship it alongside `.assets/gamedata/yappershq.antidll.jsonc`.

## 🙏 Credits

Port of [KillStr3aK/CS2-AntiDLL](https://github.com/KillStr3aK/CS2-AntiDLL) by KillStr3aK / JDW1337. The canonical 50-event ESP/cheat blacklist comes from the upstream's `data/antidll/events_detection.txt`.

---

<div align="center">
  <p>Made with ❤️ by <a href="https://github.com/yappershq">yappershq</a></p>
  <p>⭐ Star this repo if you find it useful!</p>
</div>
