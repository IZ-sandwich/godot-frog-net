using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.NetworkMessages;

public enum EntityEventEnum : byte //TODO: move somewhere else
{
    Created,
    Destroyed

}
public enum ChannelEnum : int
{
    Snapshot,
    Clock,
    EntityEvent,
    ClientInput,
    GameReliable,
    GameUnreliable
}

public struct EntityRequestMessage : IPackableMessage
{
    public required byte EntityType { get; set; }

    public void ReadBytes(MessageReader reader)
    {
        EntityType = reader.ReadByte();
    }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(EntityType);
    }
}

public struct ClockSyncMessage : IPackableMessage
{
    public required int ClientTime { get; set; }
    public required int ServerTime { get; set; }

    public void ReadBytes(MessageReader reader)
    {
        ClientTime = reader.ReadInt32();
        ServerTime = reader.ReadInt32();
    }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(ClientTime);
        writer.Write(ServerTime);
    }
}

public struct EntityEventMessage : IPackableMessage
{
    public required EntityEventEnum Event { get; set; }
    public required int EntityId { get; set; }
    public required byte EntityType { get; set; }
    public required int Authority { get; set; }
    public Vector3 Position { get; set; }
    public float Yaw { get; set; }
    /// <summary>
    /// Server tick at which the body's physics collider should become active on
    /// every peer. The server stamps this at <c>serverTick + SpawnActivationDelayTicks</c>
    /// when broadcasting a <see cref="EntityEventEnum.Created"/> event, and each
    /// peer (server and clients) holds the body in "frozen + no-collide" until
    /// its local clock reaches this tick. This eliminates the asymmetric-spawn
    /// contact window where the server simulates contacts against a body the
    /// clients haven't received yet, leaking impulses into the server's
    /// authoritative state that the client can't reproduce until reconcile —
    /// each spawn now goes live on every peer at exactly the same server tick.
    ///
    /// Zero (the default for legacy callers) means "activate immediately on
    /// receipt", preserving the prior behaviour for any code path that
    /// constructs an EntityEventMessage without setting this field.
    /// </summary>
    public int ActivationTick { get; set; }
    public string Metadata { get; set; } //TODO: his should contain Position, Yaw, etc all the other specific stuff

    public void ReadBytes(MessageReader reader)
    {
        Event = (EntityEventEnum)reader.ReadByte();
        EntityId = reader.ReadInt32();
        EntityType = reader.ReadByte();
        Authority = reader.ReadInt32();
        Position = reader.ReadVector3();
        Yaw = reader.ReadSingle();
        ActivationTick = reader.ReadInt32();
        Metadata = reader.ReadString();
    }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write((byte)Event);
        writer.Write(EntityId);
        writer.Write(EntityType);
        writer.Write(Authority);
        writer.Write(Position);
        writer.Write(Yaw);
        writer.Write(ActivationTick);
        writer.Write(Metadata);
    }

}

/// <summary>
/// Owner-side state relay for AuthorityTransfer entities. When a client holds
/// State Authority for a physics entity (cube under InterpolationPolicy.AuthorityTransfer),
/// the server stops simulating its own copy and instead acts as a relay: it
/// frozen-kinematically holds the body at the pose reported by the owner each
/// tick, and broadcasts that pose to non-owner clients via the normal snapshot
/// stream. Sent unreliably (latest-wins; missing packets are OK because the
/// next tick overwrites them anyway).
/// </summary>
public struct EntityStateRelayMessage : IPackableMessage
{
    public int EntityId { get; set; }
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 LinearVelocity { get; set; }
    public Vector3 AngularVelocity { get; set; }

    public void ReadBytes(MessageReader reader)
    {
        EntityId = reader.ReadInt32();
        Position = reader.ReadVector3();
        Rotation = reader.ReadQuaternion();
        LinearVelocity = reader.ReadVector3();
        AngularVelocity = reader.ReadVector3();
    }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(EntityId);
        writer.Write(Position);
        writer.Write(Rotation);
        writer.Write(LinearVelocity);
        writer.Write(AngularVelocity);
    }
}

public struct GameSnapshotMessage : IPackableMessage
{
    public required int Tick { get; set; }
    public IEntityStateData[] States { get; set; }
    /// <summary>
    /// Per-entity inputs the server consumed at this tick. Sparse — only entities
    /// whose authority is a connected client and that actually consumed an input
    /// this tick appear here (server-authoritative passive props are absent). Every
    /// client uses this to drive prediction for entities it doesn't own: without
    /// the driver's input the observer's local prediction of a driven vehicle would
    /// just coast at the last-known velocity, drifting from the server's input-driven
    /// trajectory every tick and forcing a reconcile on every snapshot. With this
    /// field, observers re-apply the same input the server used, so their forward
    /// prediction matches the server modulo cross-process Jolt drift.
    /// </summary>
    public EntityInput[] Inputs { get; set; }
    /// <summary>
    /// Option C clock-sync feedback: sparse map of (clientNetworkId, last
    /// input-tick the server consumed from that client). Each client picks
    /// its own entry, computes <c>lead = currentTick − lastInputTick</c>,
    /// and adjusts its clock-stretch to keep <c>lead</c> in a target band.
    /// Independent of RTT estimation, so it catches clock drift caused by
    /// engine-tick-rate slowdown under physics load — exactly the bug the
    /// ping-pong RTT measurement is blind to. Sparse because not every
    /// client will have sent input by every snapshot (passive observers,
    /// freshly-joined clients before their first input).
    /// </summary>
    public InputFrontier[] InputFrontiers { get; set; }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(Tick);
        writer.Write(States);
        // EntityInput is a struct (IPackableElement) — value-type arrays don't
        // covariance-convert to IPackableMessage[], so box explicitly.
        var inputs = Inputs ?? System.Array.Empty<EntityInput>();
        var boxed = new IPackableMessage[inputs.Length];
        for (int i = 0; i < inputs.Length; i++) boxed[i] = inputs[i];
        writer.Write(boxed);
        var frontiers = InputFrontiers ?? System.Array.Empty<InputFrontier>();
        var boxedFrontiers = new IPackableMessage[frontiers.Length];
        for (int i = 0; i < frontiers.Length; i++) boxedFrontiers[i] = frontiers[i];
        writer.Write(boxedFrontiers);
    }

    public void ReadBytes(MessageReader reader)
    {
        Tick = reader.ReadInt32();
        States = reader.ReadArray<IEntityStateData>();
        Inputs = reader.ReadArray<EntityInput>();
        InputFrontiers = reader.ReadArray<InputFrontier>();
    }
}

/// <summary>Option C per-client input-frontier entry in a snapshot.</summary>
public struct InputFrontier : IPackableElement
{
    public required int ClientNetworkId { get; set; }
    public required int LastInputTick { get; set; }
    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(ClientNetworkId);
        writer.Write(LastInputTick);
    }
    public void ReadBytes(MessageReader reader)
    {
        ClientNetworkId = reader.ReadInt32();
        LastInputTick = reader.ReadInt32();
    }
    public readonly IPackableElement GetCopy() => this;
}

/// <summary>
/// One per-(entity, tick) input as observed by the server, included in every
/// <see cref="GameSnapshotMessage"/>. Multiple entries per entity are now
/// possible — a snapshot carries the last N inputs per entity so observers can
/// look up the correct per-tick input during forward prediction and rollback
/// resim. Tagged with both the entity id (route to the right body) and the
/// server tick (route to the right past/future tick).
/// </summary>
public struct EntityInput : IPackableElement
{
    public required int EntityId { get; set; }
    public required int Tick { get; set; }
    public required IPackableElement Input { get; set; }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(EntityId);
        writer.Write(Tick);
        writer.Write(MessageSerializer.GetByteTypeFromMessage(Input));
        Input.WriteBytes(writer);
    }

    public void ReadBytes(MessageReader reader)
    {
        EntityId = reader.ReadInt32();
        Tick = reader.ReadInt32();
        byte typeId = reader.ReadByte();
        Input = (IPackableElement)MessageSerializer.GetMessageFromByteType(typeId);
        Input.ReadBytes(reader);
    }

    public readonly IPackableElement GetCopy() => this;
}

public struct PackedClientInputMessage : IPackableMessage
{
    public required int Tick { get; set; } // This is the Tick stamp for the latest generated input (Inputs[Inputs.Length]), all other Ticks are (Tick - index)
    public IPackableElement[] Inputs { get; set; }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(Tick);
        writer.WriteSingleTypeArray(Inputs);
    }

    public void ReadBytes(MessageReader reader)
    {
        Tick = reader.ReadInt32();
        Inputs = reader.ReadSingleTypeArray<IPackableElement>();
    }
}

public enum DisconnectEntityMode { RemoveEntity, KeepEntity }

/// <summary>
/// First message a client sends after the ENet handshake completes. Carries
/// the client's <see cref="MonkeNet.Shared.ClientPersistentIdentity"/> —
/// a stable, client-generated GUID that survives disconnect/reconnect and
/// process restart. The server stores
/// <c>Dictionary&lt;peerNetId, ClientPersistentId&gt;</c> on receipt; on a
/// later disconnect it parks the player's entities in a reclaim entry keyed
/// by that <c>ClientPersistentId</c>. Any subsequent reconnect from the
/// same identity finds the entry and reassigns Authority — no server-issued
/// reclaim token, no one-shot reclaim message, identity stays the same
/// across every reconnect.
/// </summary>
public struct ClientHelloMessage : IPackableMessage
{
    public required string ClientId { get; set; }

    public void ReadBytes(MessageReader reader) { ClientId = reader.ReadString(); }
    public readonly void WriteBytes(MessageWriter writer) { writer.Write(ClientId); }
}

public struct DisconnectNotificationMessage : IPackableMessage
{
    public void ReadBytes(MessageReader reader) { }
    public readonly void WriteBytes(MessageWriter writer) { }
}

/// <summary>
/// Client → server: "I'd like to take ownership of this entity." The server runs the
/// game-defined approval policy (see <c>ServerEntityManager.OwnershipApprover</c>) and
/// either calls <c>ChangeAuthority</c> (which sends the Destroy+Create pair) on approval
/// or replies with <see cref="OwnershipChangeRejectedMessage"/>.
/// </summary>
public struct OwnershipChangeRequestMessage : IPackableMessage
{
    public required int EntityId { get; set; }

    public void ReadBytes(MessageReader reader) { EntityId = reader.ReadInt32(); }
    public readonly void WriteBytes(MessageWriter writer) { writer.Write(EntityId); }
}

/// <summary>
/// Server → requester: explicit rejection of an <see cref="OwnershipChangeRequestMessage"/>
/// or <see cref="ReleaseAuthorityMessage"/>. The client never speculatively mutates local
/// state, so receiving this is purely informational — game code may surface a UI hint.
/// </summary>
public struct OwnershipChangeRejectedMessage : IPackableMessage
{
    public required int EntityId { get; set; }

    public void ReadBytes(MessageReader reader) { EntityId = reader.ReadInt32(); }
    public readonly void WriteBytes(MessageWriter writer) { writer.Write(EntityId); }
}

/// <summary>
/// Server → all peers (broadcast on the reliable EntityEvent channel): authority over
/// <see cref="EntityId"/> has changed to <see cref="NewAuthority"/>. Clients mutate the
/// entity's Authority field in place — no scene swap, no destroy/respawn. Replaces the
/// older Destroy+Create pair that snapped rigid-body state on every authority transfer.
/// </summary>
public struct AuthorityChangedMessage : IPackableMessage
{
    public required int EntityId { get; set; }
    public required int NewAuthority { get; set; }

    public void ReadBytes(MessageReader reader)
    {
        EntityId = reader.ReadInt32();
        NewAuthority = reader.ReadInt32();
    }
    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(EntityId);
        writer.Write(NewAuthority);
    }
}

/// <summary>
/// Owner client → server: "I want to give this entity back to the server (Authority=0)."
/// Server validates the sender is the current authority (and policy permits release) before
/// calling ChangeAuthority(eid, 0). Used by interact-driven flows like leaving a vehicle or
/// dropping a held prop.
/// </summary>
public struct ReleaseAuthorityMessage : IPackableMessage
{
    public required int EntityId { get; set; }

    public void ReadBytes(MessageReader reader) { EntityId = reader.ReadInt32(); }
    public readonly void WriteBytes(MessageWriter writer) { writer.Write(EntityId); }
}