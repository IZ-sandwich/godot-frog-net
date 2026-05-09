using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;
using System.Linq;

namespace MonkeNet.Client;

/// <summary>
/// Stores predicted game states for entities, upon receiving an snapshot, will check for deviation and perform rollback and re-simulation if needed.
/// </summary>
[GlobalClass]
public partial class ClientPredictionManager : InternalClientComponent
{
    // Hard cap on prediction history depth. Default 120 = 2 seconds at 60Hz.
    // Under sustained network degradation _predictedStates would otherwise grow without
    // bound and rollback would resimulate every entry — which is what overflows the
    // Jolt job ring buffer. When the cap is hit, oldest entries are dropped; a snapshot
    // arriving for a dropped tick is treated as a missed local state (counted, no rollback).
    [Export] public int MaxRollbackTicks { get; set; } = 120;

    private readonly List<PredictedState> _predictedStates = [];
    private int _lastTickReceived = 0;
    private int _misspredictionsCount = 0;
    private int _missedLocalState = 0;
    private int _trimmedTotal = 0;
    private ulong _lastTrimWarningMsec;
    private bool _wasRecentlyTrimmed;   // set when buffer hits cap; signals degraded network to the next misprediction log
    private EntitySpawner _subscribedSpawner;
    // Per-second cap on misprediction diagnostic logs to avoid log spam at 60Hz when
    // mispredictions are rapid-fire. State counts are kept in _misspredictionsCount.
    private ulong _mispredictionWindowStartMsec;
    private int _mispredictionLogsThisWindow;
    private const int MispredictionLogsPerSecond = 5;
    // Diffs above this are attributed to an external force (remote player hit, server impulse,
    // physics-object collision). Below it the drift is consistent with Jolt non-determinism.
    private const float ExternalForceThresholdM = 0.5f;

    // Listen-server: when ClientManager defers RegisterPrediction (because the SpaceStep
    // happens in ServerManager later this frame), the input + tick are stashed here and
    // committed by OnServerPostPhysicsTick after the step.
    private int _pendingTick;
    private IPackableElement _pendingInput;
    private bool _hasPendingPrediction;
    private Server.ServerManager _subscribedServer;

    public override void _Ready()
    {
        base._Ready();
        // Subscribe to EntityDestroyed so we can drop stale entries from _predictedStates.
        // Without this, an authority transfer (which destroys+recreates the local entity on
        // the previous owner) leaves the prediction loop iterating freed Godot objects.
        _subscribedSpawner = EntitySpawner.Instance;
        if (_subscribedSpawner != null)
            _subscribedSpawner.EntityDestroyed += OnEntityDestroyed;

        // In listen-server mode, ServerManager exists in the same process and its
        // _PhysicsProcess runs after ClientManager's. Subscribe to PostPhysicsTick so we
        // can commit the deferred prediction after the shared SpaceStep.
        _subscribedServer = Server.ServerManager.Instance;
        if (_subscribedServer != null)
            _subscribedServer.PostPhysicsTick += OnServerPostPhysicsTick;
    }

    public override void _ExitTree()
    {
        if (_subscribedSpawner != null && IsInstanceValid(_subscribedSpawner))
            _subscribedSpawner.EntityDestroyed -= OnEntityDestroyed;
        _subscribedSpawner = null;

        if (_subscribedServer != null && IsInstanceValid(_subscribedServer))
            _subscribedServer.PostPhysicsTick -= OnServerPostPhysicsTick;
        _subscribedServer = null;

        base._ExitTree();
    }

    /// <summary>
    /// Listen-server only: ClientManager calls this in place of <see cref="RegisterPrediction"/>
    /// while the SpaceStep is still pending in ServerManager. The actual RegisterPrediction
    /// fires from <see cref="OnServerPostPhysicsTick"/> after the step completes.
    /// </summary>
    public void StashForLatePrediction(int tick, IPackableElement input)
    {
        _pendingTick = tick;
        _pendingInput = input;
        _hasPendingPrediction = true;
    }

    private void OnServerPostPhysicsTick(int serverTick)
    {
        if (!_hasPendingPrediction) return;
        _hasPendingPrediction = false;
        var input = _pendingInput;
        _pendingInput = null;
        RegisterPrediction(_pendingTick, input);
    }

    private void OnEntityDestroyed(int entityId)
    {
        // Remove every PredictedState entry that references this entity, so subsequent
        // rollback iterations don't touch the now-freed Godot object.
        foreach (var state in _predictedStates)
        {
            var key = state.Entities.Keys.FirstOrDefault(k => k != null && k.EntityId == entityId);
            if (key != null) state.Entities.Remove(key);
        }
    }

    protected override void OnCommandReceived(IPackableMessage command)
    {
        if (!NetworkReady)
            return;

        if (command is GameSnapshotMessage snapshot)
        {
            if (snapshot.Tick > _lastTickReceived)
            {
                MonkeLogger.Debug($"[NET-SNAP-RX] tick={snapshot.Tick} entities={snapshot.States.Length} (last={_lastTickReceived})");
                for (int i = 0; i < snapshot.States.Length; i++)
                    MonkeLogger.Debug($"[NET-SNAP-RX]   state[{i}]={snapshot.States[i]}");
                _lastTickReceived = snapshot.Tick;
                ProcessServerState(snapshot);
            }
            else
            {
                MonkeLogger.Debug($"[NET-SNAP-RX] dropped out-of-order tick={snapshot.Tick} (last={_lastTickReceived})");
            }
        }
    }

    public void Predict(int tick, IPackableElement input)
    {
        if (input == null) return;
        EntitySpawner.Instance.ClientEntities.ForEach(entity =>
        {
            var clientPredictedEntity = entity.GetComponent<ClientPredictedEntity>();
            clientPredictedEntity?.OnProcessTick(tick, input);
        });
    }

    public void RegisterPrediction(int tick, IPackableElement input)
    {
        if (input == null) return;
        var predictedState = new PredictedState
        {
            Tick = tick,
            Input = input,
            Entities = []
        };

        _predictedStates.Add(predictedState);

        if (_predictedStates.Count > MaxRollbackTicks)
        {
            int toRemove = _predictedStates.Count - MaxRollbackTicks;
            _predictedStates.RemoveRange(0, toRemove);
            _trimmedTotal += toRemove;
            _wasRecentlyTrimmed = true;
            LogTrimWarningThrottled();
        }

        //TODO: use array of ClientPredictedEntity that updates each time a new entity is spawned/despawned
        //TODO: store entity state inside entity itself instead of having everything here on PredictionManager
        EntitySpawner.Instance.ClientEntities.ForEach(entity =>
        {
            var clientPredictedEntity = entity.GetComponent<ClientPredictedEntity>();
            if (clientPredictedEntity != null)
            {
                var snap = clientPredictedEntity.GetSnapshotState();
                predictedState.Entities.Add(clientPredictedEntity, snap);
                MonkeLogger.Debug($"[PRED-REG] tick={tick} eid={clientPredictedEntity.EntityId} input={input} pos=({snap.Position.X:F3},{snap.Position.Y:F3},{snap.Position.Z:F3}) vel=({snap.LinearVelocity.X:F3},{snap.LinearVelocity.Y:F3},{snap.LinearVelocity.Z:F3}) angvel=({snap.AngularVelocity.X:F3},{snap.AngularVelocity.Y:F3},{snap.AngularVelocity.Z:F3})");
            }
        });
    }

    private void ProcessServerState(GameSnapshotMessage receivedSnapshot)
    {
        // Capture and clear the trim flag so it applies to this snapshot only.
        bool networkDegraded = _wasRecentlyTrimmed;
        _wasRecentlyTrimmed = false;

        var predictedStateData = _predictedStates.Find(prediction => prediction.Tick == receivedSnapshot.Tick);
        _predictedStates.RemoveAll(predictedState => predictedState.Tick <= receivedSnapshot.Tick);

        if (predictedStateData == default(PredictedState) || predictedStateData.Tick != receivedSnapshot.Tick)
        {
            // No locally-owned predicted entities means there's nothing to reconcile this
            // tick — RegisterPrediction never recorded a state for it (no input, or no
            // ClientPredictedEntity in the scene). This is the normal pre-spawn / spectator
            // path, not a fault. Don't count it or log it.
            if (!HasAnyPredictedEntity()) return;
            _missedLocalState++;
            MonkeLogger.Debug($"[PRED-CHECK] tick={receivedSnapshot.Tick} MISSED-LOCAL-STATE (no matching predicted entry; total missed={_missedLocalState})");
            return;
        }

        // Iterate all entities saved for the tick
        foreach (ClientPredictedEntity predictableEntity in predictedStateData.Entities.Keys)
        {
            // Get predicted and authoritative state for the entity
            var predictedState = predictedStateData.Entities[predictableEntity];
            var authoritativeState = FindStateForEntityId(predictableEntity.EntityId, receivedSnapshot.States);

            Vector3 authPos = predictableEntity.ExtractAuthoritativePosition(authoritativeState);
            Vector3 posDiff = authPos - predictedState.Position;
            MonkeLogger.Debug($"[PRED-CHECK] tick={receivedSnapshot.Tick} eid={predictableEntity.EntityId} predPos=({predictedState.Position.X:F3},{predictedState.Position.Y:F3},{predictedState.Position.Z:F3}) authPos=({authPos.X:F3},{authPos.Y:F3},{authPos.Z:F3}) |posDiff|={posDiff.Length():F4}m predVel=({predictedState.LinearVelocity.X:F3},{predictedState.LinearVelocity.Y:F3},{predictedState.LinearVelocity.Z:F3})");

            if (predictableEntity.HasMisspredicted(receivedSnapshot.Tick, authoritativeState, predictedState))
            {
                _misspredictionsCount++;
                MonkeLogger.Debug($"[PRED-CHECK] tick={receivedSnapshot.Tick} eid={predictableEntity.EntityId} MISPREDICTED -> rollback");
                LogMispredictionThrottled(predictableEntity, predictedState.Position, authoritativeState, receivedSnapshot.Tick, networkDegraded);
                RollbackAndResimulate(receivedSnapshot.States, predictedStateData);
                return;
            }

            // Below the hard reconcile threshold — apply gentle silent correction. Mostly
            // a no-op (default impl), but entities like the local vehicle override this to
            // pull body state toward authoritative each snapshot, preventing the small
            // collision-response drifts from accumulating until they exceed the threshold.
            MonkeLogger.Debug($"[PRED-CHECK] tick={receivedSnapshot.Tick} eid={predictableEntity.EntityId} OK -> soft-correct");
            predictableEntity.ApplySoftCorrection(authoritativeState, predictedState);
        }
    }

    private void RollbackAndResimulate(IEntityStateData[] authoritativeStates, PredictedState predictedStateData)
    {
        // Listen-server short-circuit: client and server share the same World3D.Space, so
        // the body the client "predicts" IS the server-authoritative body. Stepping the
        // shared space N times here would advance every other peer's networked rigidbody
        // too — OfflineRigidbody3D only protects nodes explicitly tagged offline. By
        // construction (deferred RegisterPrediction via ServerManager.PostPhysicsTick) the
        // misprediction check should never fire here, but if it does — timing edge case,
        // float wobble in the velocity threshold check — the safer behaviour is to skip
        // the destructive resim. Diagnostic counters in ProcessServerState already logged it.
        if (MonkeNetManager.Instance != null && MonkeNetManager.Instance.IsServer)
        {
            MonkeLogger.Debug($"[PRED-ROLLBACK] tick={predictedStateData.Tick} SKIPPED (listen-server, shared physics space)");
            return;
        }

        MonkeLogger.Debug($"[PRED-ROLLBACK] tick={predictedStateData.Tick} entities={predictedStateData.Entities.Count} resimTicks={_predictedStates.Count}");

        // Snapshot non-networked rigidbodies so the resim's repeated SpaceStep calls
        // don't drift them. Restored after the loop.
        OfflineRigidbody3D.SnapshotAll();

        // Set all entities to authoritative state
        foreach (ClientPredictedEntity predictableEntity in predictedStateData.Entities.Keys)
        {
            var authoritativeState = FindStateForEntityId(predictableEntity.EntityId, authoritativeStates);
            MonkeLogger.Debug($"[PRED-RECONCILE] tick={predictedStateData.Tick} eid={predictableEntity.EntityId} -> auth={authoritativeState}");
            predictableEntity.HandleReconciliation(authoritativeState);
        }

        // Advance simulation forward for all remaining inputs
        for (int i = 0; i < _predictedStates.Count; i++)
        {
            var remainingInput = _predictedStates[i];
            MonkeLogger.Debug($"[PRED-RESIM] resimTick={remainingInput.Tick} entities={remainingInput.Entities.Count} input={remainingInput.Input}");
            foreach (ClientPredictedEntity predictableEntity in remainingInput.Entities.Keys)
            {
                predictableEntity.ResimulateTick(remainingInput.Input);
            }

            PhysicsServer3D.SpaceStep(MonkeNetManager.Instance.PhysicsSpace, PhysicsUtils.DeltaTime);
            PhysicsServer3D.SpaceFlushQueries(MonkeNetManager.Instance.PhysicsSpace);

            foreach (ClientPredictedEntity predictableEntity in remainingInput.Entities.Keys)
            {
                var post = predictableEntity.GetSnapshotState();
                remainingInput.Entities[predictableEntity] = post;
                MonkeLogger.Debug($"[PRED-RESIM]   eid={predictableEntity.EntityId} postPos=({post.Position.X:F3},{post.Position.Y:F3},{post.Position.Z:F3}) postVel=({post.LinearVelocity.X:F3},{post.LinearVelocity.Y:F3},{post.LinearVelocity.Z:F3})");
            }
        }

        OfflineRigidbody3D.RestoreAll();
        MonkeLogger.Debug($"[PRED-ROLLBACK] complete (offline bodies restored)");
    }

    private static bool HasAnyPredictedEntity()
    {
        if (EntitySpawner.Instance == null) return false;
        foreach (var entity in EntitySpawner.Instance.ClientEntities)
        {
            if (entity.GetComponent<ClientPredictedEntity>() != null)
                return true;
        }
        return false;
    }

    private static IEntityStateData FindStateForEntityId(int entityId, IEntityStateData[] authStates)
    {
        foreach (IEntityStateData state in authStates)
        {
            if (state.EntityId == entityId)
            {
                return state;
            }
        }

        return null;
    }

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Prediction Manager"))
        {
            ImGui.Text($"Misspredictions: {_misspredictionsCount}");
            ImGui.Text($"Missed Local States: {_missedLocalState}");
            ImGui.Text($"Prediction History: {_predictedStates.Count} / {MaxRollbackTicks}");
            ImGui.Text($"Trimmed Total: {_trimmedTotal}");
        }
    }

    private void LogMispredictionThrottled(ClientPredictedEntity entity, Vector3 predictedPos, IEntityStateData authoritativeState, int tick, bool networkDegraded)
    {
        ulong now = Time.GetTicksMsec();
        if (now - _mispredictionWindowStartMsec >= 1000)
        {
            _mispredictionWindowStartMsec = now;
            _mispredictionLogsThisWindow = 0;
        }
        if (_mispredictionLogsThisWindow >= MispredictionLogsPerSecond) return;
        _mispredictionLogsThisWindow++;

        // Log the actual authoritative-vs-predicted comparison the threshold check
        // is doing, plus its magnitude, so the log directly shows what triggered
        // reconcile. Previously this logged entity.GetPosition() — the body's
        // CURRENT pose, possibly several ticks past the snapshot tick — which made
        // small steady-state drifts look like the misprediction.
        Vector3 authPos = entity.ExtractAuthoritativePosition(authoritativeState);
        Vector3 diff = authPos - predictedPos;
        string cause = ClassifyMisprediction(diff.Length(), networkDegraded);
        MonkeLogger.Info($"Misprediction [{cause}]: entity {entity.EntityId} type {entity.EntityType} tick {tick} predicted {predictedPos} authoritative {authPos} diff {diff} |diff|={diff.Length():F3}m");
    }

    // Heuristic cause label for misprediction log messages, matching the known scenario table:
    //   degraded-network  — prediction buffer was trimmed before this snapshot arrived,
    //                       indicating snapshot packet-loss or an RTT spike (server used stale input).
    //   external-force    — diff well above threshold; another body applied an impulse the
    //                       client couldn't predict (remote player collision, server-side knockback).
    //   physics-nondeterminism — small drift near threshold; Jolt cross-process floating-point
    //                       divergence accumulating over time (expected in solo or low-contact play).
    private static string ClassifyMisprediction(float diffLength, bool networkDegraded)
    {
        if (networkDegraded)
            return "degraded-network";
        if (diffLength >= ExternalForceThresholdM)
            return "external-force";
        return "physics-nondeterminism";
    }

    private void LogTrimWarningThrottled()
    {
        ulong now = Time.GetTicksMsec();
        if (now - _lastTrimWarningMsec < 1000) return;
        _lastTrimWarningMsec = now;
        MonkeLogger.Warn($"ClientPredictionManager: prediction history hit cap of {MaxRollbackTicks} ticks; oldest entries dropped (degraded network conditions or no snapshots received)");
    }

    private class PredictedState
    {
        public int Tick;                                            // Tick at which the input was taken
        public IPackableElement Input;                              // Input message sent to the server
        public Dictionary<ClientPredictedEntity, RigidbodyState> Entities;
    }
}
