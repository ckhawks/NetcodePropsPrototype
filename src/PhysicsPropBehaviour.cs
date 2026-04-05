using System;
using UnityEngine;
using Unity.Netcode;

namespace NetcodePropsPrototype;

/// <summary>
/// Registers custom serializers for types we need in NetworkVariables.
/// Must be called once before any NetworkVariable using these types is initialized.
/// </summary>
public static class NetcodeSerializerSetup
{
    private static bool initialized = false;

    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;

        Plugin.Log("Registering custom NetworkVariable serializers for Vector3 and Quaternion...");

        // Vector3: 3 floats = 12 bytes
        UserNetworkVariableSerialization<Vector3>.WriteValue = (FastBufferWriter writer, in Vector3 value) =>
        {
            writer.WriteValueSafe(value.x);
            writer.WriteValueSafe(value.y);
            writer.WriteValueSafe(value.z);
        };
        UserNetworkVariableSerialization<Vector3>.ReadValue = (FastBufferReader reader, out Vector3 value) =>
        {
            reader.ReadValueSafe(out float x);
            reader.ReadValueSafe(out float y);
            reader.ReadValueSafe(out float z);
            value = new Vector3(x, y, z);
        };
        UserNetworkVariableSerialization<Vector3>.DuplicateValue = (in Vector3 value, ref Vector3 duplicatedValue) =>
        {
            duplicatedValue = value;
        };

        SetAreEqualVector3();

        // Quaternion: 4 floats = 16 bytes
        UserNetworkVariableSerialization<Quaternion>.WriteValue = (FastBufferWriter writer, in Quaternion value) =>
        {
            writer.WriteValueSafe(value.x);
            writer.WriteValueSafe(value.y);
            writer.WriteValueSafe(value.z);
            writer.WriteValueSafe(value.w);
        };
        UserNetworkVariableSerialization<Quaternion>.ReadValue = (FastBufferReader reader, out Quaternion value) =>
        {
            reader.ReadValueSafe(out float x);
            reader.ReadValueSafe(out float y);
            reader.ReadValueSafe(out float z);
            reader.ReadValueSafe(out float w);
            value = new Quaternion(x, y, z, w);
        };
        UserNetworkVariableSerialization<Quaternion>.DuplicateValue = (in Quaternion value, ref Quaternion duplicatedValue) =>
        {
            duplicatedValue = value;
        };

        SetAreEqualQuaternion();

        Plugin.Log("Custom serializers registered.");
    }

    private static void SetAreEqualViaReflection<T>(System.Reflection.MethodInfo method)
    {
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var field = typeof(NetworkVariableSerialization<T>).GetField("<AreEqual>k__BackingField", flags);
        if (field == null)
        {
            Plugin.LogWarning($"Could not find AreEqual backing field for {typeof(T).Name}");
            return;
        }

        var del = Delegate.CreateDelegate(field.FieldType, method);
        field.SetValue(null, del);
        Plugin.Log($"Set AreEqual for {typeof(T).Name}");
    }

    private static bool Vector3AreEqual(ref Vector3 a, ref Vector3 b) => a == b;
    private static bool QuaternionAreEqual(ref Quaternion a, ref Quaternion b) => a == b;

    private static void SetAreEqualVector3()
    {
        var method = typeof(NetcodeSerializerSetup).GetMethod("Vector3AreEqual",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        SetAreEqualViaReflection<Vector3>(method);
    }

    private static void SetAreEqualQuaternion()
    {
        var method = typeof(NetcodeSerializerSetup).GetMethod("QuaternionAreEqual",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        SetAreEqualViaReflection<Quaternion>(method);
    }
}

/// <summary>
/// NetworkBehaviour that syncs a server-authoritative physics object to all clients.
///
/// Server: runs physics via Rigidbody, writes position/rotation to NetworkVariables.
/// Client: reads NetworkVariables and applies position/rotation (kinematic, no local physics).
/// </summary>
public class PhysicsPropBehaviour : NetworkBehaviour
{
    private NetworkVariable<Vector3> netPosition = new NetworkVariable<Vector3>(
        Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private NetworkVariable<Quaternion> netRotation = new NetworkVariable<Quaternion>(
        Quaternion.identity, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private Rigidbody rb;

    private Vector3 interpTargetPos;
    private Quaternion interpTargetRot;

    // --- Manual Netcode initialization (replaces source-generated code) ---

    protected override void __initializeVariables()
    {
        NetcodeSerializerSetup.Initialize();

        if (netPosition == null)
            throw new Exception("PhysicsPropBehaviour.netPosition cannot be null.");
        netPosition.Initialize(this);
        __nameNetworkVariable(netPosition, "netPosition");
        NetworkVariableFields.Add(netPosition);

        if (netRotation == null)
            throw new Exception("PhysicsPropBehaviour.netRotation cannot be null.");
        netRotation.Initialize(this);
        __nameNetworkVariable(netRotation, "netRotation");
        NetworkVariableFields.Add(netRotation);

        base.__initializeVariables();
    }

    protected override void __initializeRpcs()
    {
        base.__initializeRpcs();
    }

    // --- Lifecycle ---

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        rb = GetComponent<Rigidbody>();

        if (IsServer)
        {
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            netPosition.Value = transform.position;
            netRotation.Value = transform.rotation;
            Plugin.Log($"SERVER — physics active, pos={transform.position}");
        }
        else
        {
            rb.isKinematic = true;
            rb.useGravity = false;
            interpTargetPos = netPosition.Value;
            interpTargetRot = netRotation.Value;
            transform.position = interpTargetPos;
            transform.rotation = interpTargetRot;

            netPosition.OnValueChanged += OnPositionChanged;
            netRotation.OnValueChanged += OnRotationChanged;
            Plugin.Log($"CLIENT — receiving state, pos={interpTargetPos}");
        }
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer)
        {
            netPosition.OnValueChanged -= OnPositionChanged;
            netRotation.OnValueChanged -= OnRotationChanged;
        }
        base.OnNetworkDespawn();
    }

    private void OnPositionChanged(Vector3 oldVal, Vector3 newVal)
    {
        interpTargetPos = newVal;
    }

    private void OnRotationChanged(Quaternion oldVal, Quaternion newVal)
    {
        interpTargetRot = newVal;
    }

    private void FixedUpdate()
    {
        if (!IsSpawned) return;

        if (IsServer)
        {
            netPosition.Value = transform.position;
            netRotation.Value = transform.rotation;
        }
    }

    private void Update()
    {
        if (!IsSpawned || IsServer) return;

        // Snap directly to server state — no interpolation lag
        transform.position = interpTargetPos;
        transform.rotation = interpTargetRot;
    }
}
