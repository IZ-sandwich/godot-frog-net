using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;

namespace MonkeNet.Client;

/// <summary>
/// Receives and presents the Player the snapshots emmited by the server
/// </summary>
[GlobalClass]
public partial class ClientSnapshotInterpolator : InternalClientComponent
{
    [Export] private int _minBufferTime = 2;

    private const int RecentPast = 0, NextFuture = 1;
    private double _interpolationFactor = 0;
    private int _bufferTime = 0;                // How many ticks in the past we are rendering the world state
    private double _currentTick = 0;            // Current local tick
    private readonly List<GameSnapshotMessage> _snapshotBuffer = new();

    public override void _Ready()
    {
        _bufferTime = 6; //TODO: magic number
        base._Ready();
    }

    public override void _Process(double delta)
    {
        _currentTick += delta / PhysicsUtils.DeltaTime;
        // (Current tick - _bufferTime) the point in time in the past which we want to render
        double tickToProcess = _currentTick - _bufferTime * 2;
        InterpolateStates(tickToProcess);
    }

    protected override void OnProcessTick(int currentTick, IPackableElement input)
    {
        _currentTick = currentTick;
    }

    protected override void OnCommandReceived(IPackableMessage command)
    {
        if (command is GameSnapshotMessage snapshot)
        {
            // Add snapshot tu buffer if we don't have any or if it is a future one
            if (_snapshotBuffer.Count <= 0 || snapshot.Tick > _snapshotBuffer[^1].Tick)
            {
                _snapshotBuffer.Add(snapshot);
                MonkeLogger.Debug($"[INTERP-RX] tick={snapshot.Tick} entities={snapshot.States.Length} bufferSize={_snapshotBuffer.Count}");
            }
            else
            {
                MonkeLogger.Debug($"[INTERP-RX] dropped out-of-order tick={snapshot.Tick} (buffer head={_snapshotBuffer[^1].Tick})");
            }
        }
    }

    protected override void OnLatencyCalculated(int latencyAverageTicks, int jitterAverageTicks)
    {
        _bufferTime = (jitterAverageTicks + _minBufferTime + latencyAverageTicks);
    }

    /* Example:
     * Current Render Time  = 15
     * Last Snapshot        = 13
     * Future Snapshot      = 20
     * |--------|--------------------|
     * 13(0)    15(x)                20(1)
     * Interpolation factor (x) = 0.28
     */
    private void InterpolateStates(double renderTick)
    {
        if (_snapshotBuffer.Count <= 1)
        {
            return; // We need at least 2 stated to interpolate
        }

        // Clear any unwanted (past) states
        while (_snapshotBuffer.Count > 2 && _snapshotBuffer[1].Tick < renderTick)
        {
            _snapshotBuffer.RemoveAt(0);
        }

        var nextSnapshot = _snapshotBuffer[NextFuture];
        var pastSnapshot = _snapshotBuffer[RecentPast];

        int diffBetweenStates = nextSnapshot.Tick - pastSnapshot.Tick;  // How "long" is the "line" between past and future states
        double currentRenderPoint = renderTick - pastSnapshot.Tick;     // Where in this "line" we are located based on current clock

        _interpolationFactor = currentRenderPoint / diffBetweenStates;  // Where in the line we are represented as a coefficient

        // Clamp to [0, 1] so a stale future snapshot (jitter spike: render
        // clock advances but no new snapshot arrived in the buffer) doesn't
        // cause the lerp to extrapolate beyond the latest known state. Without
        // this clamp, dummies overshoot during burst-loss / jitter and then
        // visibly snap back when the next snapshot arrives — producing
        // multi-meter frame deltas instead of a smooth pause-then-resume.
        if (_interpolationFactor > 1.0) _interpolationFactor = 1.0;
        else if (_interpolationFactor < 0.0) _interpolationFactor = 0.0;
        var futureStateCount = nextSnapshot.States.Length;

        for (int i = 0; i < futureStateCount; i++)
        {
            if (nextSnapshot.States.Length > i && pastSnapshot.States.Length > i)
            {
                IEntityStateData futureState = nextSnapshot.States[i];
                IEntityStateData pastState = pastSnapshot.States[i];

                // Entity may not exist yet if snapshot arrived before EntityEventMessage.Created (reliable channel delay).
                NetworkBehaviour networkBehaviour = EntitySpawner.Instance.TryGetEntityById(futureState.EntityId);
                if (networkBehaviour == null) continue;

                ClientInterpolatedEntity clientInterpolator = networkBehaviour.GetComponent<ClientInterpolatedEntity>(); //FIXME: instead of searching for the component, I should already have a reference for it somewhere
                clientInterpolator?.HandleStateInterpolation(pastState, futureState, (float)_interpolationFactor);
            }
        }
    }

    /// <summary>
    /// Snap every interpolated entity's physics body to its state in the LATEST
    /// received snapshot. Called from <see cref="ClientManager._PhysicsProcess"/>
    /// just before <see cref="ClientPredictionManager.Predict"/> runs so that the
    /// locally-predicted player collides with these bodies at their server-truth
    /// pose during the physics tick — not at whatever pose the last render-frame
    /// interpolator pass happened to land them on.
    ///
    /// Without this call the body's transform between render frames is whatever
    /// the previous <c>HandleStateInterpolation</c> wrote to it, plus any Jolt
    /// integration the engine did in the meantime; either source can drift it
    /// out of sync with the server. Refreshing right before SpaceStep gives the
    /// player's contact resolution the same cube pose the server's player saw
    /// when it computed the snapshot we're about to compare against.
    /// </summary>
    public void SnapInterpolatedBodiesToLatestSnapshot()
    {
        if (_snapshotBuffer.Count == 0) return;
        var latest = _snapshotBuffer[^1];
        for (int i = 0; i < latest.States.Length; i++)
        {
            IEntityStateData state = latest.States[i];
            NetworkBehaviour networkBehaviour = EntitySpawner.Instance.TryGetEntityById(state.EntityId);
            if (networkBehaviour == null) continue;
            var interp = networkBehaviour.GetComponent<ClientInterpolatedEntity>();
            interp?.HardSnapToAuthoritativeState(state);
        }
    }

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Snapshot Interpolator"))
        {
            if (_interpolationFactor > 1) ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF);
            ImGui.Text($"Interp. Factor {_interpolationFactor:0.00}");
            ImGui.PopStyleColor();

            ImGui.Text($"Buffer Size {_snapshotBuffer.Count} snapshots");
            ImGui.Text($"Buffer Time {_bufferTime} ticks");

            int bufferTimeMs = (int)(_bufferTime * PhysicsUtils.DeltaTime * 1000);
            ImGui.Text($"World State is {bufferTimeMs}ms in the past (relative to server state)");
        }
    }
}