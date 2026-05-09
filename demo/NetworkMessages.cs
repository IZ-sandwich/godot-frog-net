using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

// Entity state sent by the server to all clients every time a snapshot is produced
public struct EntityStateMessage : IEntityStateData
{
    public int EntityId { get; set; } // Entity Id
    public Vector3 Position { get; set; } // Entity Position
    public Vector3 Velocity { get; set; } // Entity velocity
    public Vector3 AngularVelocity { get; set; } // Entity velocity
    // Full-precision quaternion on the wire. Sending Euler angles round-trips through
    // FromEuler/GetEuler which is lossy near gimbal lock and accumulates error over
    // many snapshots — especially noticeable on tumbling rigidbodies (ball, vehicle).
    public Quaternion Rotation { get; set; }
    public float Yaw { get; set; } // Looking angle

    public void ReadBytes(MessageReader reader)
    {
        EntityId = reader.ReadInt32();
        Position = reader.ReadVector3();
        Velocity = reader.ReadVector3();
        AngularVelocity = reader.ReadVector3();
        Rotation = reader.ReadQuaternion();
        Yaw = reader.ReadSingle();
    }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(EntityId);
        writer.Write(Position);
        writer.Write(Velocity);
        writer.Write(AngularVelocity);
        writer.Write(Rotation);
        writer.Write(Yaw);
    }

    public readonly IPackableElement GetCopy() => this;

    public override readonly string ToString() =>
        $"eid={EntityId} pos=({Position.X:F3},{Position.Y:F3},{Position.Z:F3}) "
        + $"vel=({Velocity.X:F3},{Velocity.Y:F3},{Velocity.Z:F3}) "
        + $"rot=({Rotation.X:F3},{Rotation.Y:F3},{Rotation.Z:F3},{Rotation.W:F3}) "
        + $"angvel=({AngularVelocity.X:F3},{AngularVelocity.Y:F3},{AngularVelocity.Z:F3}) yaw={Yaw:F3}";
}

// Client → server: "I'm done driving this vehicle, return authority to the server."
// Counterpart to the framework's OwnershipChangeRequestMessage which only claims, never
// releases. The server validates that the sender is the current authority before
// calling ChangeAuthority(eid, 0).
public struct ReleaseVehicleMessage : IPackableMessage
{
    public required int EntityId { get; set; }

    public void ReadBytes(MessageReader reader) { EntityId = reader.ReadInt32(); }
    public readonly void WriteBytes(MessageWriter writer) { writer.Write(EntityId); }
}

// Client → server: spawn a vehicle owned by the *server*, not the requester.
// The framework's EntityRequestMessage always sets authority = requester, which would
// instantly hand the spawning client predicted ownership and trigger the rider anchor.
// Vehicles must spawn unowned so any nearby player can claim one via the F-key
// proximity flow — that's the whole point of the interaction prompt.
public struct SpawnVehicleRequestMessage : IPackableMessage
{
    public void ReadBytes(MessageReader reader) { }
    public readonly void WriteBytes(MessageWriter writer) { }
}

// Character inputs sent to the server by a local player every tick.
// MoveX/MoveY carry analog values (-1..1) so controller sticks work correctly.
// Keyboard produces exactly -1/0/1; a controller stick produces values in between.
public struct CharacterInputMessage : IPackableElement
{
    public byte Keys { get; set; }   // Bit flags for binary actions (see InputFlags).
    public float MoveX { get; set; } // -1..1: negative = left, positive = right.
    public float MoveY { get; set; } // -1..1: negative = forward, positive = backward.
    public float CameraYaw { get; set; }

    public readonly void WriteBytes(MessageWriter writer)
    {
        writer.Write(Keys);
        writer.Write(MoveX);
        writer.Write(MoveY);
        writer.Write(CameraYaw);
    }

    public void ReadBytes(MessageReader reader)
    {
        Keys = reader.ReadByte();
        MoveX = reader.ReadSingle();
        MoveY = reader.ReadSingle();
        CameraYaw = reader.ReadSingle();
    }

    public readonly IPackableElement GetCopy() => this;

    public override readonly string ToString() =>
        $"keys=0x{Keys:X2} move=({MoveX:F3},{MoveY:F3}) yaw={CameraYaw:F3}";
}