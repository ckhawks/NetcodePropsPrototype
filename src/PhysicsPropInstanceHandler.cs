using UnityEngine;
using Unity.Netcode;

namespace NetcodePropsPrototype;

/// <summary>
/// Custom prefab instance handler for Netcode.
/// Called by Netcode on CLIENTS when the server spawns a NetworkObject with our hash.
/// </summary>
public class PhysicsPropInstanceHandler : INetworkPrefabInstanceHandler
{
    private readonly GameObject prefabTemplate;

    public PhysicsPropInstanceHandler(GameObject prefabTemplate)
    {
        this.prefabTemplate = prefabTemplate;
    }

    public NetworkObject Instantiate(ulong ownerClientId, Vector3 position, Quaternion rotation)
    {
        Plugin.Log($"PrefabHandler.Instantiate called (owner={ownerClientId}, pos={position}, rot={rotation})");

        var instance = Object.Instantiate(prefabTemplate, position, rotation);
        instance.SetActive(true);
        instance.name = "PhysicsProp_Networked";

        // Set the hash on the instance so Netcode recognizes it
        PhysicsPropManager.SetGlobalObjectIdHashPublic(instance.GetComponent<NetworkObject>());

        return instance.GetComponent<NetworkObject>();
    }

    public void Destroy(NetworkObject networkObject)
    {
        Plugin.Log($"PrefabHandler.Destroy called (id={networkObject.NetworkObjectId})");
        Object.Destroy(networkObject.gameObject);
    }
}
