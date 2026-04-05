using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Netcode;

namespace NetcodePropsPrototype;

/// <summary>
/// Entry point for the mod. Registers the networked physics prop prefab on load,
/// patches NetworkManager startup to complete deferred registration, and spawns
/// the prop on the server once a match begins.
/// </summary>
public class Plugin : IPuckMod
{
    public static string MOD_NAME = "NetcodePropsPrototype";
    public static string MOD_GUID = "pw.stellaric.netcode.props.prototype";

    static readonly Harmony harmony = new Harmony(MOD_GUID);

    /// <summary>
    /// True when running on the dedicated server (no GPU).
    /// Used to skip client-only logic like mesh/material creation.
    /// </summary>
    public static bool isServer;

    public bool OnEnable()
    {
        Log("Enabling...");
        try
        {
            // Dedicated servers have no GPU — use that to detect server vs client.
            isServer = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
            Log($"Environment: {(isServer ? "dedicated server" : "client")}");

            // Create and register the prop prefab on both server and client.
            // Must happen BEFORE NetworkManager starts so both sides know how
            // to instantiate the NetworkObject by its hash.
            PhysicsPropManager.RegisterPrefab();

            // These patches ensure prefab registration completes right before
            // networking starts, in case NetworkManager.Singleton wasn't available
            // yet during RegisterPrefab().
            harmony.PatchAll(typeof(NetworkManagerStartServerPatch));
            harmony.PatchAll(typeof(NetworkManagerStartHostPatch));
            harmony.PatchAll(typeof(NetworkManagerStartClientPatch));

            // This patch spawns the prop on the server once a match starts.
            harmony.PatchAll(typeof(ServerStartPatch));

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

    // --- Harmony patches ---
    // Complete deferred prefab registration right before any networking mode starts.
    // We patch all three modes (server/host/client) to cover every scenario.

    [HarmonyPatch(typeof(NetworkManager), "StartServer")]
    class NetworkManagerStartServerPatch
    {
        [HarmonyPrefix]
        static void Prefix() => PhysicsPropManager.TryCompleteDeferredRegistration();
    }

    [HarmonyPatch(typeof(NetworkManager), "StartHost")]
    class NetworkManagerStartHostPatch
    {
        [HarmonyPrefix]
        static void Prefix() => PhysicsPropManager.TryCompleteDeferredRegistration();
    }

    [HarmonyPatch(typeof(NetworkManager), "StartClient")]
    class NetworkManagerStartClientPatch
    {
        [HarmonyPrefix]
        static void Prefix() => PhysicsPropManager.TryCompleteDeferredRegistration();
    }

    /// <summary>
    /// Once the game server has fully started, spawn our prop after a short delay
    /// to give the scene time to load.
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

    // --- Logging helpers ---

    public static void Log(string message) =>
        Debug.Log($"[{MOD_NAME}] {message}");

    public static void LogWarning(string message) =>
        Debug.LogWarning($"[{MOD_NAME}] {message}");

    public static void LogError(string message) =>
        Debug.LogError($"[{MOD_NAME}] {message}");
}
