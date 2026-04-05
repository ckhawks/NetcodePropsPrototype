using UnityEngine;
using Unity.Netcode;

namespace NetcodePropsPrototype;

/// <summary>
/// Custom prefab instance handler for Netcode.
/// Called on CLIENTS when the server spawns a NetworkObject with our hash (0xDEADBEEF).
/// Netcode calls Instantiate() to create the client-side copy of the object.
/// </summary>
public class PhysicsPropInstanceHandler : INetworkPrefabInstanceHandler
{
    private readonly GameObject prefabTemplate;

    public PhysicsPropInstanceHandler(GameObject prefabTemplate)
    {
        this.prefabTemplate = prefabTemplate;
    }

    /// <summary>
    /// Called by Netcode on the client when the server spawns our prop.
    /// Creates a local instance from our template and configures it for
    /// client-side use (no physics, no Netcode transform sync).
    /// </summary>
    public NetworkObject Instantiate(ulong ownerClientId, Vector3 position, Quaternion rotation)
    {
        var instance = Object.Instantiate(prefabTemplate, position, rotation);
        instance.SetActive(true);
        instance.name = "PhysicsProp_Networked";

        var netObj = instance.GetComponent<NetworkObject>();
        PhysicsPropManager.SetGlobalObjectIdHashPublic(netObj);

        // Disable Netcode's built-in Update()-based transform sync.
        // The game's SynchronizedObject system handles position via network ticks instead.
        PhysicsPropManager.DisableNetcodeTransformSyncPublic(netObj);

        // Client: physics off — server is authoritative for position.
        // SynchronizedObject.OnNetworkPostSpawn() also does this, but we enforce
        // it here to be safe in case the NetworkBehaviour discovery has issues.
        var rb = instance.GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.None; // Same behaviour with RigidbodyInterpolation.Interpolate;

        return netObj;
    }

    /// <summary>
    /// Called by Netcode when the object is despawned/destroyed.
    /// </summary>
    public void Destroy(NetworkObject networkObject)
    {
        Object.Destroy(networkObject.gameObject);
    }
}
