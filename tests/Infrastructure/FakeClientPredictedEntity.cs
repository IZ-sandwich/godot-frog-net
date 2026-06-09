using System.Collections.Generic;
using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Tests.Infrastructure;

/// <summary>
/// Test double for <see cref="ClientPredictedEntity"/>. Records calls to the
/// prediction lifecycle methods so tests can verify rollback ordering and arguments.
/// </summary>
public partial class FakeClientPredictedEntity : ClientPredictedEntity
{
    /// <summary>Configures what <see cref="HasMisspredicted"/> returns.</summary>
    public bool HasMisspredictedReturnValue { get; set; } = false;

    public List<(int Tick, IEntityStateData ReceivedState, RigidbodyState SavedState)> HasMisspredictedCalls { get; } = new();
    public List<IEntityStateData> HandleReconciliationCalls { get; } = new();
    public List<IPackableElement> ResimulateTickCalls { get; } = new();
    public int GetPositionCallCount { get; private set; }
    public int GetSnapshotStateCallCount { get; private set; }

    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, RigidbodyState savedState)
    {
        HasMisspredictedCalls.Add((tick, receivedState, savedState));
        return HasMisspredictedReturnValue;
    }

    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        HandleReconciliationCalls.Add(receivedState);
    }

    public override void ResimulateTick(IPackableElement input)
    {
        ResimulateTickCalls.Add(input);
    }

    public override Vector3 GetPosition()
    {
        GetPositionCallCount++;
        return Vector3.Zero;
    }

    public override RigidbodyState GetSnapshotState()
    {
        GetSnapshotStateCallCount++;
        // Rotation defaults to Quaternion.Identity (not (0,0,0,0) which is what
        // `default(RigidbodyState)` would produce). The manager's misprediction
        // logger calls `.Inverse()` on the predicted rotation, and Godot's
        // Quaternion.Inverse validates normalisation — a zero quat throws.
        // Real entities always carry a normalised rotation; this fake must too.
        return new RigidbodyState
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        };
    }
}
