using System;
using System.Collections;
using UnityEngine;
using Unity.Netcode;
using Object = UnityEngine.Object;

namespace NetcodePropsPrototype;

/// <summary>
/// Manages the lifecycle of our networked physics prop:
/// - Creates a "prefab" GameObject programmatically
/// - Registers it with Netcode so both server and client can instantiate it
/// - Spawns instances on the server
/// </summary>
public static class PhysicsPropManager
{
    // Our programmatic "prefab" — a template GameObject that Netcode clones on spawn
    private static GameObject prefabTemplate;

    // The GlobalObjectIdHash we assign to our prefab.
    // This must match on server and client. We pick an arbitrary fixed value
    // that won't collide with the game's existing prefabs.
    private const uint PROP_HASH = 0xDE_AD_BE_EF;

    // Keep a reference to our spawned instance for cleanup
    private static NetworkObject spawnedInstance;

    /// <summary>
    /// Creates the prefab template and registers it with Netcode.
    /// Must be called on BOTH server and client BEFORE NetworkManager starts.
    /// </summary>
    public static void RegisterPrefab()
    {
        Plugin.Log("Creating physics prop prefab template...");

        // Create the template GameObject (this is our "prefab")
        prefabTemplate = CreatePropGameObject("PhysicsPropPrefab");

        // Mark it as inactive so it doesn't appear in the scene —
        // Netcode will instantiate copies of it when Spawn() is called
        prefabTemplate.SetActive(false);
        Object.DontDestroyOnLoad(prefabTemplate);

        // Set the GlobalObjectIdHash on the prefab's NetworkObject FIRST
        // so Netcode knows this prefab by our chosen hash.
        SetGlobalObjectIdHash(prefabTemplate.GetComponent<NetworkObject>(), PROP_HASH);

        // Register with Netcode's PrefabHandler using our custom handler.
        // NetworkManager.Singleton may not exist yet at OnEnable time —
        // if so, we'll defer registration to when it becomes available.
        if (NetworkManager.Singleton != null)
        {
            RegisterWithNetworkManager();
        }
        else
        {
            Plugin.Log("NetworkManager.Singleton is null — deferring prefab registration...");
            prefabRegistrationPending = true;
        }
    }

    private static bool prefabRegistrationPending = false;

    /// <summary>
    /// Called when we know NetworkManager.Singleton is available.
    /// </summary>
    public static void RegisterWithNetworkManager()
    {
        if (prefabTemplate == null)
        {
            Plugin.LogError("Cannot register — prefab template is null!");
            return;
        }

        var handler = new PhysicsPropInstanceHandler(prefabTemplate);
        NetworkManager.Singleton.PrefabHandler.AddHandler(PROP_HASH, handler);
        prefabRegistrationPending = false;

        Plugin.Log($"Prefab registered with NetworkManager (hash 0x{PROP_HASH:X8})");
    }

    /// <summary>
    /// Call this to complete deferred registration if it was pending.
    /// </summary>
    public static void TryCompleteDeferredRegistration()
    {
        if (prefabRegistrationPending && NetworkManager.Singleton != null)
        {
            RegisterWithNetworkManager();
        }
    }

    /// <summary>
    /// Creates a GameObject with all the components needed for a networked physics prop.
    /// </summary>
    private static GameObject CreatePropGameObject(string name)
    {
        var go = new GameObject(name);

        // Visual: only add mesh/material on clients (server has no shaders)
        if (!Plugin.isServer)
        {
            var meshFilter = go.AddComponent<MeshFilter>();
            meshFilter.mesh = CreateCubeMesh();

            // Try URP lit shader first, then Standard as fallback
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                      ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                      ?? Shader.Find("Standard");
            if (shader != null)
            {
                var meshRenderer = go.AddComponent<MeshRenderer>();
                meshRenderer.material = new Material(shader)
                {
                    color = new Color(1f, 0.4f, 0.1f) // orange
                };
            }
            else
            {
                Plugin.LogWarning("Standard shader not found — prop will be invisible.");
                go.AddComponent<MeshRenderer>();
            }
        }

        // Set to the Puck layer so it collides with the rink floor
        int puckLayer = LayerMask.NameToLayer("Puck");
        if (puckLayer >= 0)
        {
            go.layer = puckLayer;
            Plugin.Log($"Set layer to Puck ({puckLayer})");
        }
        else
        {
            Plugin.LogWarning("Could not find 'Puck' layer — prop may fall through floor!");
        }

        // Physics: rigidbody + collider for server-side simulation
        var rb = go.AddComponent<Rigidbody>();
        rb.mass = 2f;
        rb.drag = 0.5f;
        rb.angularDrag = 1f;

        var collider = go.AddComponent<BoxCollider>();
        collider.size = Vector3.one;

        // Scale it down to a reasonable size
        go.transform.localScale = Vector3.one * 0.5f;

        // Networking: NetworkObject (required) + our behaviour for state sync
        go.AddComponent<NetworkObject>();
        go.AddComponent<PhysicsPropBehaviour>();

        return go;
    }

    /// <summary>
    /// Spawns the prop on the server after a delay (to let the scene load).
    /// </summary>
    public static void SpawnWithDelay(float delay)
    {
        // Use a coroutine runner since we're in a static context.
        // Create a temporary MonoBehaviour to host the coroutine.
        var runner = new GameObject("PropSpawnRunner").AddComponent<CoroutineRunner>();
        Object.DontDestroyOnLoad(runner.gameObject);
        runner.StartCoroutine(SpawnAfterDelay(delay, runner.gameObject));
    }

    private static IEnumerator SpawnAfterDelay(float delay, GameObject runner)
    {
        Plugin.Log($"Coroutine started, waiting {delay}s...");
        yield return new WaitForSeconds(delay);
        Plugin.Log("Delay complete, attempting spawn...");

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
    /// Spawns a physics prop at the given position. Server only.
    /// </summary>
    public static void SpawnProp(Vector3 position)
    {
        if (!NetworkManager.Singleton.IsServer)
        {
            Plugin.LogWarning("SpawnProp called on client — ignoring.");
            return;
        }

        Plugin.Log($"Spawning physics prop at {position}...");

        // Instantiate from our template
        var instance = Object.Instantiate(prefabTemplate, position, Quaternion.identity);
        instance.SetActive(true);
        instance.name = "PhysicsProp_Networked";

        // Ensure the hash is set on the instance too
        var netObj = instance.GetComponent<NetworkObject>();
        SetGlobalObjectIdHash(netObj, PROP_HASH);

        // Spawn as server-owned (no specific client ownership)
        netObj.Spawn(false);
        spawnedInstance = netObj;

        Plugin.Log($"Physics prop spawned! NetworkObjectId={netObj.NetworkObjectId}");
    }

    /// <summary>
    /// Sets GlobalObjectIdHash on a NetworkObject using reflection,
    /// since the setter is typically editor-only.
    /// </summary>
    private static void SetGlobalObjectIdHash(NetworkObject netObj, uint hash)
    {
        var allFlags = System.Reflection.BindingFlags.Public |
                       System.Reflection.BindingFlags.NonPublic |
                       System.Reflection.BindingFlags.Instance;

        // Log all fields on NetworkObject that contain "hash" or "id" for debugging
        Plugin.Log("NetworkObject fields (searching for hash field):");
        foreach (var f in typeof(NetworkObject).GetFields(allFlags))
        {
            if (f.Name.IndexOf("hash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                f.Name.IndexOf("Hash", StringComparison.Ordinal) >= 0 ||
                f.Name.IndexOf("Id", StringComparison.Ordinal) >= 0 ||
                f.Name.IndexOf("Prefab", StringComparison.Ordinal) >= 0)
            {
                Plugin.Log($"  field: {f.Name} ({f.FieldType.Name}) = {f.GetValue(netObj)}");
            }
        }
        foreach (var p in typeof(NetworkObject).GetProperties(allFlags))
        {
            if (p.Name.IndexOf("hash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                p.Name.IndexOf("Hash", StringComparison.Ordinal) >= 0 ||
                p.Name.IndexOf("GlobalObject", StringComparison.Ordinal) >= 0)
            {
                try
                {
                    Plugin.Log($"  prop: {p.Name} ({p.PropertyType.Name}) = {p.GetValue(netObj)} canWrite={p.CanWrite}");
                }
                catch
                {
                    Plugin.Log($"  prop: {p.Name} ({p.PropertyType.Name}) [could not read]");
                }
            }
        }

        // Try known field names (including the one found in DLL strings)
        foreach (var name in new[] {
            "GlobalObjectIdHash",
            "globalObjectIdHash",
            "m_GlobalObjectIdHash",
            "m_globalObjectIdHash",
            "<GlobalObjectIdHash>k__BackingField"
        })
        {
            var field = typeof(NetworkObject).GetField(name, allFlags);
            if (field != null && field.FieldType == typeof(uint))
            {
                field.SetValue(netObj, hash);
                Plugin.Log($"Set GlobalObjectIdHash via field '{name}' = 0x{hash:X8}");
                return;
            }
        }

        // Try setting via property setter
        var prop = typeof(NetworkObject).GetProperty("GlobalObjectIdHash", allFlags);
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(netObj, hash);
            Plugin.Log($"Set GlobalObjectIdHash via property setter = 0x{hash:X8}");
            return;
        }

        Plugin.LogError("Could not find writable GlobalObjectIdHash on NetworkObject! Spawn will likely fail.");
    }

    public static void SetGlobalObjectIdHashPublic(NetworkObject netObj)
    {
        SetGlobalObjectIdHash(netObj, PROP_HASH);
    }

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

    /// <summary>
    /// Creates a simple unit cube mesh using Unity's built-in primitive.
    /// Only called on client.
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
/// Minimal MonoBehaviour just to host coroutines from static context.
/// </summary>
public class CoroutineRunner : MonoBehaviour { }
