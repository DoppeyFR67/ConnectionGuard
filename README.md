# ConnectionGuard

A [MelonLoader](https://melonwiki.xyz/) mod for **Schedule I** that protects player progress during network disconnections in multiplayer sessions.

## The Problem

In Schedule I multiplayer, network hiccups and unstable connections can cause clients to lose their inventory and progress when they disconnect unexpectedly. The game's default behavior doesn't distinguish between a temporary network drop and the host intentionally leaving, often resulting in data loss even when the session is still active and rejoinable.

## The Solution

ConnectionGuard continuously captures snapshots of every remote player's state (inventory, appearance, clothing, variables) while the game is running. When a player reconnects after a network issue, the mod serves their most recent snapshot so they pick up right where they left off. It also monitors the Steam lobby to determine *why* a disconnection happened and shows the appropriate UI to the player.

## Base Game Issues Addressed

ConnectionGuard fixes or works around several bugs, oversights, and design gaps in Schedule I's multiplayer networking code:

### 1. Null Payload Crash in Steam Transport (`CommonSocket.Send`)

**Type:** Crash bug (unhandled exception)

The game's `CommonSocket.Send` method in the FishySteamworks transport layer passes an `ArraySegment<byte>` directly to the Steam networking API without validating that the underlying `Array` is non-null. During connection teardown or under race conditions, the segment can end up with a null backing array. When this reaches the native Steam API, it causes an unhandled exception that crashes the game.

**Fix:** A Harmony prefix on `CommonSocket.Send` checks `segment.Array == null` before the method body runs. If null, it short-circuits with `EResult.k_EResultInvalidParam` and logs a warning instead of letting the call propagate into native code.

---

### 2. Spurious "Host Exited Game" RPC (`Player.RpcLogic___HostExitedGame`)

**Type:** Race condition / false positive

The game fires the `HostExitedGame` RPC to clients when the FishNet transport detects a connection interruption. However, this fires *before* actually verifying whether the host has left — a momentary network blip (packet loss, route change, brief Wi-Fi drop) triggers the same RPC as the host genuinely closing the game. The result: clients get kicked to the main menu with a "host exited" message even though the host is still in the Steam lobby and the session is perfectly fine.

**Fix:** A Harmony prefix intercepts the RPC on the client side and cross-references the Steam lobby membership list via `SteamMatchmaking.GetLobbyMemberByIndex`. If the host's `CSteamID` is still present in the lobby, the RPC is suppressed and a "Connection Lost" popup is shown instead (with `preventLeaveLobby: true` so the client stays in the lobby and can rejoin). The original RPC only runs if the host is genuinely gone.

---

### 3. No Disconnect Type Differentiation (`Player.ClientConnectionStateChanged`)

**Type:** Missing logic / design gap

When FishNet's transport fires a `ClientConnectionStateChanged` event with `LocalConnectionState.Stopping` or `Stopped`, the game's handler in the `Player` class treats every disconnect identically — it kicks the client to the menu. There is no check to determine *why* the transport stopped:
- Was it a temporary network interruption (host is still in the lobby)?
- Did the host actually shut down the session?

Both cases produce the same behavior, which means players lose their session context on every brief network hiccup.

**Fix:** A Harmony prefix on `ClientConnectionStateChanged` intercepts `Stopping`/`Stopped` states for the local player and queries the Steam lobby to check host presence. If the host is still there, it shows a "Connection Lost" popup (rejoinable) and blocks the default handler. If the host is gone, it lets the original handler run normally.

---

### 4. Transport Event Handler Leak (`Player.OnStopClient` / `Player.OnDestroy`)

**Type:** Memory/event leak leading to stale callbacks

The `Player` class subscribes its `ClientConnectionStateChanged` method to `InstanceFinder.TransportManager.Transport.OnClientConnectionState` but never unsubscribes when the player stops (`OnStopClient`) or is destroyed (`OnDestroy`). This means:
- Destroyed `Player` objects remain subscribed to transport events
- When the transport fires connection state changes, it invokes callbacks on dead/disposed player instances
- This causes `NullReferenceException`s or other undefined behavior as the callback tries to access destroyed Unity objects

**Fix:** Harmony postfixes on both `Player.OnStopClient` and `Player.OnDestroy` manually unsubscribe the handler. Since `ClientConnectionStateChanged` is a private method, the mod uses `AccessTools.Method` + `Delegate.CreateDelegate` to reconstruct the delegate at runtime and remove it with `-=` from the transport's event.

---

### 5. Stale Player Data on Reconnect (`PlayerManager.TryGetPlayerData`)

**Type:** Data staleness / design gap

When a disconnected player reconnects, the game's `PlayerManager.TryGetPlayerData` reads their state from the last save on disk. The problem: saves don't happen frequently, and there's no mechanism to capture the player's *live* in-memory state (current inventory, position, appearance changes, variable updates) between saves. A player who disconnects 10 minutes after the last save loses all progress from that window.

**Fix:** The mod maintains a `Dictionary<string, PlayerSnapshot>` on the host, refreshed every 2 seconds with each remote player's current `PlayerData`, inventory string, appearance string, clothing string, and variable data. A Harmony postfix on `TryGetPlayerData` checks this dictionary first — if a snapshot exists for the reconnecting player's code, it overwrites the ref parameters with the live data instead of the stale disk data.

---

### 6. No Periodic Auto-Save for Remote Players

**Type:** Design gap

The base game's save system doesn't prioritize or frequently save remote (non-host) players' data during gameplay. If the host crashes, the power goes out, or a client disconnects between the game's own save points, all unsaved remote player progress is lost.

**Fix:** The mod runs an auto-save cycle every 45 seconds on the host. It captures fresh snapshots of all remote players, then calls `PlayerManager.SavePlayer()` for each one. It respects the game's own save state (skips if `SaveManager.IsSaving` is true) to avoid conflicts.

---

### 7. Aggressive Default Steam Networking Timeouts

**Type:** Configuration issue

Schedule I uses Steam's default networking timeout values for both `TimeoutInitial` (new connections) and `TimeoutConnected` (established connections). These defaults are relatively short — short enough that common real-world network events like ISP route changes, brief Wi-Fi drops, or transient packet loss cause the connection to be declared dead before it has a chance to recover.

**Fix:** On initialization (once `SteamManager.Initialized` is true), the mod sets both `k_ESteamNetworkingConfig_TimeoutConnected` and `k_ESteamNetworkingConfig_TimeoutInitial` to 120,000 ms (2 minutes) at the global scope via `SteamNetworkingUtils.SetConfigValue` with unmanaged memory marshaling. This gives connections significantly more headroom to survive temporary interruptions.

---

## Features

- **Live Player Snapshots** — Captures periodic snapshots of all remote players' data every 2 seconds on the host, including inventory, appearance, clothing, and game variables.

- **Reconnection Recovery** — When a player reconnects after a network drop, their last synchronized state is automatically restored via a Harmony patch on `PlayerManager.TryGetPlayerData`.

- **Host Presence Detection** — Clients poll the Steam lobby every 5 seconds to check whether the host is still present, enabling accurate disconnect reason detection.

- **Graceful Disconnect Popups** — Shows context-aware popups when a client loses connection:
  - **"Connection Lost"** if the host is still in the lobby (player can rejoin with restored inventory)
  - **"Exited Game"** if the host has actually left

- **Auto-Save** — Periodically saves all remote players' data every 45 seconds on the host to protect against data loss between manual saves.

- **Extended Steam Timeouts** — Configures Steam networking timeouts to 120 seconds (up from the default), giving connections more time to recover from temporary interruptions.

- **Null Payload Protection** — Intercepts outgoing Steam network sends and blocks any with a null payload array, preventing crashes from malformed packets.

## How It Works

ConnectionGuard uses [Harmony](https://harmony.pardeike.net/) to patch several game methods at runtime:

| Patch Target | Purpose |
|---|---|
| `PlayerManager.TryGetPlayerData` | Intercepts player data lookups and serves the live snapshot when a player reconnects |
| `Player.ClientConnectionStateChanged` | Detects when the local client's transport connection drops and shows the appropriate popup |
| `Player.RpcLogic___HostExitedGame` | Catches false "host exited" RPCs when the host is actually still in the lobby |
| `Player.OnStopClient` / `Player.OnDestroy` | Cleans up transport event subscriptions to prevent stale callbacks |
| `CommonSocket.Send` | Guards against null payload arrays that would crash the Steam networking layer |

### Snapshot Lifecycle

```
Host is running
  |
  +-- Every 2s: CaptureRemoteSnapshots()
  |     Iterates Player.PlayerList, captures PlayerData + inventory/appearance/clothing/variables
  |     Stores in Dictionary<string, PlayerSnapshot> keyed by PlayerCode
  |
  +-- Every 45s: AutoSaveRemotePlayers()
  |     Refreshes snapshots, then calls PlayerManager.SavePlayer() for each remote player
  |
  +-- On reconnect: TryGetPlayerData patch
  |     Looks up snapshot by PlayerCode, overwrites the ref parameters with snapshot data
  |
  +-- On scene change: OnPreSceneChange()
        Clears all snapshots and resets timers
```

## Installation

1. Install [MelonLoader](https://melonwiki.xyz/) for Schedule I (v0.7.0+ recommended)
2. Download `ConnectionGuard.dll` from the [Releases](../../releases) page
3. Drop the DLL into your `Schedule I/Mods/` folder
4. Launch the game — ConnectionGuard loads automatically

## Building from Source

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) (any version supporting `netstandard2.1`)
- A copy of Schedule I with MelonLoader installed

### Steps

1. Clone this repo
2. Update the `<HintPath>` entries in `ConnectionGuard.csproj` to match your Schedule I install path:
   ```xml
   <HintPath>YOUR_STEAM_PATH\steamapps\common\Schedule I\MelonLoader\net35\MelonLoader.dll</HintPath>
   ```
3. Build:
   ```
   dotnet build -c Release
   ```
4. The compiled DLL will be in `bin/Release/netstandard2.1/`

### Required References

All references come from your Schedule I installation — nothing extra to download:

| Assembly | Location |
|---|---|
| `MelonLoader.dll` | `Schedule I/MelonLoader/net35/` |
| `0Harmony.dll` | `Schedule I/MelonLoader/net35/` |
| `Assembly-CSharp.dll` | `Schedule I/Schedule I_Data/Managed/` |
| `FishNet.Runtime.dll` | `Schedule I/Schedule I_Data/Managed/` |
| `com.rlabrecque.steamworks.net.dll` | `Schedule I/Schedule I_Data/Managed/` |
| `UnityEngine.dll` | `Schedule I/Schedule I_Data/Managed/` |
| `UnityEngine.CoreModule.dll` | `Schedule I/Schedule I_Data/Managed/` |
| `UnityEngine.JSONSerializeModule.dll` | `Schedule I/Schedule I_Data/Managed/` |

## Configuration

All tunable values are constants at the top of `ConnectionGuard.cs`:

| Constant | Default | Description |
|---|---|---|
| `SnapshotIntervalSeconds` | `2` | How often player snapshots are captured (host only) |
| `AutoSaveIntervalSeconds` | `45` | How often remote players are auto-saved (host only) |
| `LobbyCheckIntervalSeconds` | `5` | How often clients check if the host is still in the lobby |
| `DisconnectPopupCooldownSeconds` | `1` | Minimum time between disconnect popups |
| `SteamTimeoutMilliseconds` | `120000` | Steam networking timeout (2 minutes) |

## Compatibility

- **Game:** Schedule I by TVGS
- **Framework:** MelonLoader
- **Mod Version:** 1.2.0
- **Target:** .NET Standard 2.1

## License

This project is provided as-is for educational and personal use.
