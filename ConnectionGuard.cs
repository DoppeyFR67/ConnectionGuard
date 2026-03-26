using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using FishNet;
using FishNet.Transporting;
using FishySteamworks;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.Networking;
using ScheduleOne.Persistence;
using ScheduleOne.Persistence.Datas;
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI.MainMenu;
using Steamworks;
using UnityEngine;

[assembly: MelonInfo(typeof(ConnectionGuard.ConnectionGuard), "ConnectionGuard", "1.2.0", "Claude")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace ConnectionGuard
{
    public sealed class ConnectionGuard : MelonMod
    {
        private sealed class PlayerSnapshot
        {
            public PlayerData Data;
            public string InventoryString;
            public string AppearanceString;
            public string ClothingString;
            public VariableData[] Variables;
            public float CapturedAt;
        }

        private const float SnapshotIntervalSeconds = 2f;
        private const float AutoSaveIntervalSeconds = 45f;
        private const float LobbyCheckIntervalSeconds = 5f;
        private const float DisconnectPopupCooldownSeconds = 1f;
        private const int SteamTimeoutMilliseconds = 120000;

        private static readonly Dictionary<string, PlayerSnapshot> Snapshots =
            new Dictionary<string, PlayerSnapshot>(StringComparer.Ordinal);

        private static readonly MethodInfo ClientConnectionStateChangedMethod =
            AccessTools.Method(typeof(Player), "ClientConnectionStateChanged");

        internal static ConnectionGuard Current { get; private set; }

        private bool _timeoutsSet;
        private bool _cleanupRegistered;
        private float _lastSnapshotRefresh;
        private float _lastAutoSave;
        private float _lastLobbyCheck;
        private float _lastDisconnectPopupTime = -999f;

        public override void OnInitializeMelon()
        {
            Current = this;
            HarmonyInstance.PatchAll(typeof(ConnectionGuard).Assembly);
            LoggerInstance.Msg("ConnectionGuard initialized");
        }

        public override void OnUpdate()
        {
            TrySetSteamTimeouts();

            if (!_cleanupRegistered && Singleton<LoadManager>.InstanceExists)
            {
                Singleton<LoadManager>.Instance.onPreSceneChange.AddListener(OnPreSceneChange);
                _cleanupRegistered = true;
            }

            float now = Time.realtimeSinceStartup;

            if (!InstanceFinder.IsServer && now - _lastLobbyCheck >= LobbyCheckIntervalSeconds)
            {
                _lastLobbyCheck = now;
                CheckHostPresence();
            }

            if (!InstanceFinder.IsServer
                || !Singleton<LoadManager>.InstanceExists
                || !Singleton<LoadManager>.Instance.IsGameLoaded)
            {
                return;
            }

            if (now - _lastSnapshotRefresh >= SnapshotIntervalSeconds)
            {
                _lastSnapshotRefresh = now;
                CaptureRemoteSnapshots();
            }

            if (now - _lastAutoSave >= AutoSaveIntervalSeconds)
            {
                _lastAutoSave = now;
                AutoSaveRemotePlayers();
            }
        }

        internal static bool TryApplySnapshot(
            string playerCode,
            ref PlayerData data,
            ref string inventoryString,
            ref string appearanceString,
            ref string clothingString,
            ref VariableData[] variables)
        {
            PlayerSnapshot snapshot;
            if (string.IsNullOrEmpty(playerCode) || !Snapshots.TryGetValue(playerCode, out snapshot))
            {
                return false;
            }

            data = snapshot.Data;
            inventoryString = snapshot.InventoryString ?? string.Empty;
            appearanceString = snapshot.AppearanceString ?? string.Empty;
            clothingString = snapshot.ClothingString ?? string.Empty;
            variables = snapshot.Variables;
            return true;
        }

        internal static bool HostStillPresentInLobby()
        {
            if (!Singleton<Lobby>.InstanceExists)
            {
                return false;
            }

            Lobby lobby = Singleton<Lobby>.Instance;
            if (!lobby.IsInLobby || lobby.IsHost)
            {
                return false;
            }

            CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobby.LobbySteamID);
            if (!owner.IsValid())
            {
                return false;
            }

            int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobby.LobbySteamID);
            for (int i = 0; i < memberCount; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobby.LobbySteamID, i) == owner)
                {
                    return true;
                }
            }

            return false;
        }

        internal static void UnsubscribeClientConnectionState(Player player)
        {
            if (player == null || ClientConnectionStateChangedMethod == null)
            {
                return;
            }

            if (InstanceFinder.TransportManager == null || InstanceFinder.TransportManager.Transport == null)
            {
                return;
            }

            Delegate handler = Delegate.CreateDelegate(
                typeof(Action<ClientConnectionStateArgs>),
                player,
                ClientConnectionStateChangedMethod,
                false);
            if (handler == null)
            {
                return;
            }

            InstanceFinder.TransportManager.Transport.OnClientConnectionState -=
                (Action<ClientConnectionStateArgs>)handler;
        }

        internal static void Log(string message)
        {
            if (Current != null)
            {
                Current.LoggerInstance.Msg(message);
            }
        }

        internal static void Warn(string message)
        {
            if (Current != null)
            {
                Current.LoggerInstance.Warning(message);
            }
        }

        internal static bool TryShowDisconnectPopup(bool hostStillPresent)
        {
            if (Current == null || !Singleton<LoadManager>.InstanceExists)
            {
                return false;
            }

            LoadManager loadManager = Singleton<LoadManager>.Instance;
            if (loadManager.IsLoading || !loadManager.IsGameLoaded)
            {
                return false;
            }

            float now = Time.realtimeSinceStartup;
            if (now - Current._lastDisconnectPopupTime < DisconnectPopupCooldownSeconds)
            {
                return false;
            }

            Current._lastDisconnectPopupTime = now;

            MainMenuPopup.Data popup;
            if (hostStillPresent)
            {
                popup = new MainMenuPopup.Data(
                    "Connection Lost",
                    "Lost connection to the host. Your latest synced inventory will be restored when you rejoin.",
                    true);
            }
            else
            {
                popup = new MainMenuPopup.Data(
                    "Exited Game",
                    "Host left the game",
                    false);
            }

            loadManager.ExitToMenu(null, popup, preventLeaveLobby: hostStillPresent);
            return true;
        }

        private void TrySetSteamTimeouts()
        {
            if (_timeoutsSet || !SteamManager.Initialized)
            {
                return;
            }

            SetGlobalConfigInt32(
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected,
                SteamTimeoutMilliseconds);
            SetGlobalConfigInt32(
                ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial,
                SteamTimeoutMilliseconds);

            _timeoutsSet = true;
            LoggerInstance.Msg("Steam networking timeouts set to 120 seconds");
        }

        private static void SetGlobalConfigInt32(ESteamNetworkingConfigValue key, int value)
        {
            IntPtr pointer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(pointer, value);
                SteamNetworkingUtils.SetConfigValue(
                    key,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                    IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    pointer);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        private void CheckHostPresence()
        {
            if (!Singleton<Lobby>.InstanceExists || !Singleton<LoadManager>.InstanceExists)
            {
                return;
            }

            Lobby lobby = Singleton<Lobby>.Instance;
            if (!lobby.IsInLobby || lobby.IsHost || !Singleton<LoadManager>.Instance.IsGameLoaded)
            {
                return;
            }

            if (!HostStillPresentInLobby())
            {
                LoggerInstance.Msg("Host is no longer present in the lobby");
                TryShowDisconnectPopup(hostStillPresent: false);
            }
        }

        private void OnPreSceneChange()
        {
            Snapshots.Clear();
            _lastSnapshotRefresh = 0f;
            _lastAutoSave = 0f;
            _lastLobbyCheck = 0f;
            _lastDisconnectPopupTime = -999f;
            LoggerInstance.Msg("Session cleanup: snapshots cleared");
        }

        private void CaptureRemoteSnapshots()
        {
            for (int i = 0; i < Player.PlayerList.Count; i++)
            {
                Player player = Player.PlayerList[i];
                if (!CanSnapshot(player))
                {
                    continue;
                }

                Snapshots[player.PlayerCode] = CreateSnapshot(player);
            }
        }

        private void AutoSaveRemotePlayers()
        {
            if (!Singleton<PlayerManager>.InstanceExists)
            {
                return;
            }

            if (Singleton<SaveManager>.InstanceExists && Singleton<SaveManager>.Instance.IsSaving)
            {
                return;
            }

            CaptureRemoteSnapshots();

            int saved = 0;
            for (int i = 0; i < Player.PlayerList.Count; i++)
            {
                Player player = Player.PlayerList[i];
                if (!CanSnapshot(player))
                {
                    continue;
                }

                try
                {
                    Singleton<PlayerManager>.Instance.SavePlayer(player);
                    saved++;
                }
                catch (Exception ex)
                {
                    LoggerInstance.Error("Auto-save failed for " + player.PlayerCode + ": " + ex.Message);
                }
            }

            if (saved > 0)
            {
                LoggerInstance.Msg("Auto-saved " + saved + " remote player(s)");
            }
        }

        private static bool CanSnapshot(Player player)
        {
            if (player == null || player == Player.Local)
            {
                return false;
            }

            if (string.IsNullOrEmpty(player.PlayerCode) || !player.playerDataRetrieveReturned)
            {
                return false;
            }

            return true;
        }

        private static PlayerSnapshot CreateSnapshot(Player player)
        {
            return new PlayerSnapshot
            {
                Data = player.GetPlayerData(),
                InventoryString = player.GetInventoryString(),
                AppearanceString = player.GetAppearanceString(),
                ClothingString = player.GetClothingString(),
                Variables = ParseVariables(player.GetVariablesString()),
                CapturedAt = Time.realtimeSinceStartup
            };
        }

        private static VariableData[] ParseVariables(string variablesJson)
        {
            if (string.IsNullOrEmpty(variablesJson))
            {
                return null;
            }

            try
            {
                VariableCollectionData collection = JsonUtility.FromJson<VariableCollectionData>(variablesJson);
                return collection != null ? collection.Variables : null;
            }
            catch
            {
                return null;
            }
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.TryGetPlayerData))]
    internal static class PlayerManagerTryGetPlayerDataPatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            string playerCode,
            ref PlayerData data,
            ref string inventoryString,
            ref string appearanceString,
            ref string clothingString,
            ref VariableData[] variables,
            ref bool __result)
        {
            if (!ConnectionGuard.TryApplySnapshot(
                    playerCode,
                    ref data,
                    ref inventoryString,
                    ref appearanceString,
                    ref clothingString,
                    ref variables))
            {
                return;
            }

            __result = data != null;
            ConnectionGuard.Log("Serving live snapshot for reconnecting player " + playerCode);
        }
    }

    [HarmonyPatch(typeof(Player), "ClientConnectionStateChanged")]
    internal static class PlayerClientConnectionStateChangedPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Player __instance, ClientConnectionStateArgs args)
        {
            ConnectionGuard.Log("Client connection state changed: " + args.ConnectionState);

            if (args.ConnectionState != LocalConnectionState.Stopping
                && args.ConnectionState != LocalConnectionState.Stopped)
            {
                return true;
            }

            if (__instance != Player.Local)
            {
                return false;
            }

            if (ConnectionGuard.HostStillPresentInLobby())
            {
                ConnectionGuard.Warn("Client transport stopped while host is still present in lobby");
                ConnectionGuard.TryShowDisconnectPopup(hostStillPresent: true);
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Player), "RpcLogic___HostExitedGame_2166136261")]
    internal static class PlayerHostExitedGamePatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (InstanceFinder.IsServer)
            {
                return true;
            }

            if (!ConnectionGuard.HostStillPresentInLobby())
            {
                return true;
            }

            ConnectionGuard.Warn("HostExitedGame fired while the host still exists in the lobby");
            ConnectionGuard.TryShowDisconnectPopup(hostStillPresent: true);
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnStopClient))]
    internal static class PlayerOnStopClientPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Player __instance)
        {
            ConnectionGuard.UnsubscribeClientConnectionState(__instance);
        }
    }

    [HarmonyPatch(typeof(Player), "OnDestroy")]
    internal static class PlayerOnDestroyPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Player __instance)
        {
            ConnectionGuard.UnsubscribeClientConnectionState(__instance);
        }
    }

    [HarmonyPatch(typeof(CommonSocket), "Send")]
    internal static class CommonSocketSendPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            ArraySegment<byte> segment,
            ref EResult __result)
        {
            if (segment.Array == null)
            {
                __result = EResult.k_EResultInvalidParam;
                ConnectionGuard.Warn("Blocked Steam send with a null payload array");
                return false;
            }

            return true;
        }
    }
}
