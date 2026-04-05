using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Netcode;

namespace NetcodePropsPrototype;

public class Plugin : IPuckMod
{
    public static string MOD_NAME = "NetcodePropsPrototype";
    public static string MOD_GUID = "pw.stellaric.netcode.props.prototype";

    static readonly Harmony harmony = new Harmony(MOD_GUID);

    public static bool isServer;

    public bool OnEnable()
    {
        Log("Enabling...");
        try
        {
            isServer = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
            Log($"Environment: {(isServer ? "dedicated server" : "client")}");

            // Register our custom prefab handler on both sides BEFORE network starts.
            // This is critical — both server and client need to know how to
            // instantiate our NetworkObject by its hash.
            PhysicsPropManager.RegisterPrefab();

            // Patch to hook into server start event for spawning,
            // and NetworkManager availability for deferred registration.
            harmony.PatchAll(typeof(ServerStartPatch));
            harmony.PatchAll(typeof(NetworkManagerStartServerPatch));
            harmony.PatchAll(typeof(NetworkManagerStartHostPatch));
            harmony.PatchAll(typeof(NetworkManagerStartClientPatch));

            Log("Enabled!");
            return true;
        }
        catch (Exception e)
        {
            LogError($"Failed to enable: {e.Message}\n{e.StackTrace}");
            return false;
        }
    }

    public bool OnDisable()
    {
        Log("Disabling...");
        try
        {
            PhysicsPropManager.Cleanup();
            harmony.UnpatchSelf();
            Log("Disabled!");
            return true;
        }
        catch (Exception e)
        {
            LogError($"Failed to disable: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Hook into NetworkManager startup to complete deferred prefab registration.
    /// Patching both StartServer and StartClient so both sides register before networking begins.
    /// </summary>
    [HarmonyPatch(typeof(NetworkManager), "StartServer")]
    class NetworkManagerStartServerPatch
    {
        [HarmonyPrefix]
        static void Prefix()
        {
            Log("NetworkManager.StartServer — completing prefab registration...");
            PhysicsPropManager.TryCompleteDeferredRegistration();
        }
    }

    [HarmonyPatch(typeof(NetworkManager), "StartHost")]
    class NetworkManagerStartHostPatch
    {
        [HarmonyPrefix]
        static void Prefix()
        {
            Log("NetworkManager.StartHost — completing prefab registration...");
            PhysicsPropManager.TryCompleteDeferredRegistration();
        }
    }

    [HarmonyPatch(typeof(NetworkManager), "StartClient")]
    class NetworkManagerStartClientPatch
    {
        [HarmonyPrefix]
        static void Prefix()
        {
            Log("NetworkManager.StartClient — completing prefab registration...");
            PhysicsPropManager.TryCompleteDeferredRegistration();
        }
    }

    /// <summary>
    /// Hook into server start to spawn our prop after networking is ready.
    /// </summary>
    [HarmonyPatch(typeof(ServerManagerController), "Event_Server_OnServerStarted")]
    class ServerStartPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            if (!isServer) return;

            Log("Server started — spawning physics prop in 3 seconds...");
            PhysicsPropManager.SpawnWithDelay(3f);
        }
    }

    public static void Log(string message)
    {
        Debug.Log($"[{MOD_NAME}] {message}");
    }

    public static void LogWarning(string message)
    {
        Debug.LogWarning($"[{MOD_NAME}] {message}");
    }

    public static void LogError(string message)
    {
        Debug.LogError($"[{MOD_NAME}] {message}");
    }
}
