using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using Unity.Netcode;
using Object = UnityEngine.Object;

namespace NetcodePropsPrototype;

/// <summary>
/// Manages the full lifecycle of our networked physics prop:
///   1. Creates a "prefab" GameObject programmatically (no Unity Editor needed)
///   2. Registers it with Netcode's PrefabHandler so both server and client
///      know how to instantiate it by its hash
///   3. Spawns instances on the server with physics enabled
///
/// On the client, the game's SynchronizedObject system handles position sync —
/// it snaps the object to the server's position each network tick via OnClientTick().
/// </summary>
public static class PhysicsPropManager
{
    /// <summary>
    /// The inactive template GameObject that Netcode clones when spawning.
    /// Lives in DontDestroyOnLoad so it persists across scene loads.
    /// </summary>
    private static GameObject prefabTemplate;

    /// <summary>
    /// Reference to the spawned instance so we can clean it up on disable.
    /// </summary>
    private static NetworkObject spawnedInstance;

    /// <summary>
    /// True if RegisterPrefab() ran before NetworkManager.Singleton was available.
    /// Registration will complete in TryCompleteDeferredRegistration().
    /// </summary>
    private static bool prefabRegistrationPending;

    /// <summary>
    /// Arbitrary fixed hash that identifies our prefab. Must match on server and client.
    /// Chosen to not collide with the game's existing prefab hashes.
    /// </summary>
    private const uint PROP_HASH = 0xDE_AD_BE_EF;

    /// <summary>
    /// Reflection binding flags — we use reflection to set fields on NetworkObject
    /// because their setters are editor-only or internal.
    /// </summary>
    private static readonly BindingFlags AllFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // =====================
    //  Prefab Registration
    // =====================

    /// <summary>
    /// Creates the prefab template and registers it with Netcode.
    /// Must be called on BOTH server and client BEFORE NetworkManager starts.
    /// </summary>
    public static void RegisterPrefab()
    {
        // Build the template GameObject with all needed components
        prefabTemplate = CreatePropGameObject("PhysicsPropPrefab");

        // Keep it inactive and persistent — Netcode instantiates copies on spawn
        prefabTemplate.SetActive(false);
        Object.DontDestroyOnLoad(prefabTemplate);

        // Tell Netcode what hash identifies this prefab
        SetGlobalObjectIdHash(prefabTemplate.GetComponent<NetworkObject>(), PROP_HASH);

        // Disable Netcode's built-in transform sync — the game uses its own
        // SynchronizedObject system for tick-based position updates instead
        DisableNetcodeTransformSync(prefabTemplate.GetComponent<NetworkObject>());

        // Register with Netcode now if possible, otherwise defer to startup patches
        if (NetworkManager.Singleton != null)
            RegisterWithNetworkManager();
        else
            prefabRegistrationPending = true;
    }

    /// <summary>
    /// Called from Harmony patches on NetworkManager.Start* methods.
    /// Completes registration that was deferred because NetworkManager
    /// wasn't available yet during OnEnable.
    /// </summary>
    public static void TryCompleteDeferredRegistration()
    {
        if (prefabRegistrationPending && NetworkManager.Singleton != null)
            RegisterWithNetworkManager();
    }

    /// <summary>
    /// Registers our custom instance handler with Netcode's PrefabHandler.
    /// This tells Netcode: "when you need to instantiate hash 0xDEADBEEF,
    /// use PhysicsPropInstanceHandler instead of the default Instantiate."
    /// </summary>
    private static void RegisterWithNetworkManager()
    {
        var handler = new PhysicsPropInstanceHandler(prefabTemplate);
        NetworkManager.Singleton.PrefabHandler.AddHandler(PROP_HASH, handler);
        prefabRegistrationPending = false;
        Plugin.Log($"Prefab registered with NetworkManager (hash 0x{PROP_HASH:X8})");
    }

    // =================
    //  Prefab Creation
    // =================

    /// <summary>
    /// Builds a GameObject with all components needed for a networked physics prop:
    /// mesh + material (client only), rigidbody, collider, SynchronizedObject, NetworkObject.
    /// </summary>
    private static GameObject CreatePropGameObject(string name)
    {
        var go = new GameObject(name);

        // --- Visual (client only — server has no GPU/shaders) ---
        if (!Plugin.isServer)
        {
            var meshFilter = go.AddComponent<MeshFilter>();
            meshFilter.mesh = CreateCubeMesh();

            var shader = Shader.Find("Universal Render Pipeline/Lit")
                      ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                      ?? Shader.Find("Standard");

            var meshRenderer = go.AddComponent<MeshRenderer>();
            if (shader != null)
                meshRenderer.material = new Material(shader) { color = new Color(1f, 0.4f, 0.1f) };
        }

        // --- Layer: use "Puck" layer so it collides with the rink floor ---
        int puckLayer = LayerMask.NameToLayer("Puck");
        if (puckLayer >= 0)
            go.layer = puckLayer;

        // --- Physics ---
        // Defaults to kinematic (no simulation). Server enables physics after spawn;
        // client stays kinematic because SynchronizedObject sets position from ticks.
        var rb = go.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.mass = 2f;
        rb.drag = 0.5f;
        rb.angularDamping = 1f;

        go.AddComponent<BoxCollider>().size = Vector3.one;
        go.transform.localScale = Vector3.one * 0.5f;

        // --- Networking ---
        // SynchronizedObject must be added BEFORE NetworkObject so that
        // NetworkObject.Awake() discovers it as a NetworkBehaviour.
        // If added after, OnNetworkPostSpawn() never fires and the object
        // is never registered with SynchronizedObjectManager for tick-based sync.
        go.AddComponent<SynchronizedObject>();
        go.AddComponent<NetworkObject>();

        return go;
    }

    // ==========
    //  Spawning
    // ==========

    /// <summary>
    /// Spawns the prop after a delay. Uses a temporary coroutine runner
    /// because we're in a static context with no MonoBehaviour.
    /// </summary>
    public static void SpawnWithDelay(float delay)
    {
        var runner = new GameObject("PropSpawnRunner").AddComponent<CoroutineRunner>();
        Object.DontDestroyOnLoad(runner.gameObject);
        runner.StartCoroutine(SpawnAfterDelay(delay, runner.gameObject));
    }

    private static IEnumerator SpawnAfterDelay(float delay, GameObject runner)
    {
        yield return new WaitForSeconds(delay);

        try
        {
            SpawnProp(new Vector3(0f, 2f, 0f));
        }
        catch (Exception e)
        {
            Plugin.LogError($"Failed to spawn prop: {e.Message}\n{e.StackTrace}");
        }

        Object.Destroy(runner);
    }

    /// <summary>
    /// Instantiates and network-spawns a physics prop at the given position.
    /// Server only — clients receive the spawn via Netcode and create their
    /// own instance through PhysicsPropInstanceHandler.
    /// </summary>
    public static void SpawnProp(Vector3 position)
    {
        if (!NetworkManager.Singleton.IsServer) return;

        var instance = Object.Instantiate(prefabTemplate, position, Quaternion.identity);
        instance.SetActive(true);
        instance.name = "PhysicsProp_Networked";

        var netObj = instance.GetComponent<NetworkObject>();
        SetGlobalObjectIdHash(netObj, PROP_HASH);
        DisableNetcodeTransformSync(netObj);

        // On the server, enable physics so the rigidbody simulates.
        // Clients keep it kinematic — SynchronizedObject handles their position.
        var rb = instance.GetComponent<Rigidbody>();
        rb.isKinematic = false;
        rb.useGravity = true;

        // Spawn as server-owned (no specific client ownership)
        netObj.Spawn(false);
        spawnedInstance = netObj;

        Plugin.Log($"Physics prop spawned! NetworkObjectId={netObj.NetworkObjectId}");
    }

    // =========
    //  Cleanup
    // =========

    /// <summary>
    /// Despawns the prop and destroys the prefab template. Called on mod disable.
    /// </summary>
    public static void Cleanup()
    {
        if (spawnedInstance != null && spawnedInstance.IsSpawned)
        {
            spawnedInstance.Despawn(true);
            spawnedInstance = null;
        }
        if (prefabTemplate != null)
        {
            Object.Destroy(prefabTemplate);
            prefabTemplate = null;
        }
    }

    // =========
    //  Helpers
    // =========

    /// <summary>Public wrappers so PhysicsPropInstanceHandler can call these.</summary>
    public static void SetGlobalObjectIdHashPublic(NetworkObject netObj) =>
        SetGlobalObjectIdHash(netObj, PROP_HASH);

    public static void DisableNetcodeTransformSyncPublic(NetworkObject netObj) =>
        DisableNetcodeTransformSync(netObj);

    /// <summary>
    /// Sets GlobalObjectIdHash on a NetworkObject via reflection.
    /// The setter is editor-only, so we write directly to the backing field.
    /// This hash is how Netcode matches spawned objects to their prefab.
    /// </summary>
    private static void SetGlobalObjectIdHash(NetworkObject netObj, uint hash)
    {
        foreach (var name in new[] {
            "GlobalObjectIdHash", "globalObjectIdHash",
            "m_GlobalObjectIdHash", "<GlobalObjectIdHash>k__BackingField"
        })
        {
            var field = typeof(NetworkObject).GetField(name, AllFlags);
            if (field != null && field.FieldType == typeof(uint))
            {
                field.SetValue(netObj, hash);
                return;
            }
        }
        Plugin.LogError("Could not find writable GlobalObjectIdHash on NetworkObject!");
    }

    /// <summary>
    /// Disables Netcode's built-in transform synchronization via reflection.
    /// Without this, Netcode would sync the transform every Update() frame,
    /// fighting with the game's SynchronizedObject tick-based system.
    /// </summary>
    private static void DisableNetcodeTransformSync(NetworkObject netObj)
    {
        foreach (var name in new[] { "SynchronizeTransform", "m_SynchronizeTransform", "AutoObjectParentSync" })
        {
            var field = typeof(NetworkObject).GetField(name, AllFlags);
            if (field != null && field.FieldType == typeof(bool))
                field.SetValue(netObj, false);
        }

        var prop = typeof(NetworkObject).GetProperty("SynchronizeTransform", AllFlags);
        if (prop != null && prop.CanWrite)
            prop.SetValue(netObj, false);
    }

    /// <summary>
    /// Creates a unit cube mesh by borrowing from Unity's built-in cube primitive.
    /// </summary>
    private static Mesh CreateCubeMesh()
    {
        var tempCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var mesh = Object.Instantiate(tempCube.GetComponent<MeshFilter>().sharedMesh);
        Object.Destroy(tempCube);
        return mesh;
    }
}

/// <summary>
/// Minimal MonoBehaviour to host coroutines from static context.
/// </summary>
public class CoroutineRunner : MonoBehaviour { }
