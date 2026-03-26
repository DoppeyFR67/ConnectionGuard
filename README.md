# ConnectionGuard

A [MelonLoader](https://melonwiki.xyz/) mod for **Schedule I** that protects player progress during network disconnections in multiplayer sessions.

## The Problem

In Schedule I multiplayer, network hiccups and unstable connections can cause clients to lose their inventory and progress when they disconnect unexpectedly. The game's default behavior doesn't distinguish between a temporary network drop and the host intentionally leaving, often resulting in data loss even when the session is still active and rejoinable.

## The Solution

ConnectionGuard continuously captures snapshots of every remote player's state (inventory, appearance, clothing, variables) while the game is running. When a player reconnects after a network issue, the mod serves their most recent snapshot so they pick up right where they left off. It also monitors the Steam lobby to determine *why* a disconnection happened and shows the appropriate UI to the player.

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

1. Install [MelonLoader](https://melonwiki.xyz/) for Schedule I (v0.6+ recommended)
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
