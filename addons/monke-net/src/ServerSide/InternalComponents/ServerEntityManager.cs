using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;

namespace MonkeNet.Server;

/// <summary>
/// Handles creation/deletion of entities
/// </summary>
[GlobalClass]
public partial class ServerEntityManager : InternalServerComponent
{
    /// <summary>
    /// Number of recent (tick, input) pairs per entity to include in each
    /// <see cref="GameSnapshotMessage"/>. Each observer caches these by
    /// (entityId, tick) so its rollback resim applies the correct per-tick
    /// input at each replayed tick instead of falling back to "latest cached
    /// input for everything in the rollback window" — the prior approximation
    /// flagged in <c>ClientPredictionManager.RollbackAndResimulate</c>. Default
    /// 5 trades a small bandwidth bump (5 × ~12 bytes per active-input entity
    /// per snapshot) for a measurable drop in observer mispredict counts on
    /// scenarios with frequent input direction changes.
    /// </summary>
    [Export] public int InputHistoryPerSnapshot { get; set; } = 5;

    /// <summary>
    /// Number of ticks to delay a newly-spawned entity's physics activation
    /// after the spawn event is broadcast. The server holds the body
    /// "frozen + no-collide" for this many ticks, broadcasts the spawn event
    /// stamped with <c>ActivationTick = serverTick + SpawnActivationDelayTicks</c>,
    /// and every client holds its replica in the same state until its synced
    /// clock reaches ActivationTick. All peers then go live on the same tick,
    /// closing the asymmetric-spawn contact window where the server's body
    /// would otherwise resolve contacts against a body that doesn't yet exist
    /// on a connected client — those impulses used to leak into the server's
    /// authoritative state and the client could only catch up via reconcile,
    /// producing the "cube settling on a stack" mispredict pattern.
    ///
    /// Default 2 ticks (≈ 33 ms at 60 Hz) — sized to cover typical localhost
    /// latency for the entity-event message. Increase for higher-latency
    /// networks; set to 0 to disable the delay entirely (legacy behaviour).
    /// </summary>
    [Export] public int SpawnActivationDelayTicks { get; set; } = 2;

    /// <summary>
    /// Game-defined approval policy for client-initiated authority requests. Returns
    /// true to grant ownership, false to reject. Default is reject — the game must
    /// opt-in by assigning this delegate so an unconfigured server doesn't let any
    /// peer claim any entity. Signature: <c>(requesterClientId, entityId) → approved</c>.
    /// </summary>
    public System.Func<int, int, bool> OwnershipApprover { get; set; }

    private EntitySpawner _entitySpawner;
    private int _entityIdCount = 0;
    private int _lastEntitiesPacked = 0;

    public override void _EnterTree()
    {
        _entitySpawner = MonkeNetManager.Instance?.EntitySpawner;
    }

    public void SendSnapshotData(int currentTick)
    {
        var snapshotCommand = PackSnapshot(currentTick);
        MonkeLogger.Debug($"[NET-SNAP-TX] tick={currentTick} entities={snapshotCommand.States.Length} -> broadcast");
        for (int i = 0; i < snapshotCommand.States.Length; i++)
        {
            var s = snapshotCommand.States[i];
            // Cast to GameDemo.EntityStateMessage shape via reflection-free interface check would
            // require the framework to know the demo type — instead, ToString the boxed struct.
            // Concrete field formatting comes from each IEntityStateData's own override; default
            // structs print field names which is good enough for replay/debug.
            MonkeLogger.Debug($"[NET-SNAP-TX]   eid={s.EntityId} state={s}");
        }
        SendCommandToClient(0, snapshotCommand, INetworkManager.PacketModeEnum.Unreliable, (int)ChannelEnum.Snapshot);
    }

    protected override void OnCommandReceived(int clientId, IPackableMessage command)
    {
        if (command is EntityRequestMessage entityRequest)
        {
            SpawnEntity<Node3D>(entityRequest.EntityType, clientId);
        }

        // Reclaim is no longer client-initiated: ServerConnectionMonitor now
        // owns the lookup, keyed by the client's persistent identity
        // (announced via ClientHelloMessage and surviving disconnect /
        // reconnect / process restart). On hello, the monitor reassigns
        // Authority directly through ChangeAuthority.

        if (command is OwnershipChangeRequestMessage ownershipReq)
        {
            HandleOwnershipRequest(clientId, ownershipReq.EntityId);
        }

        if (command is ReleaseAuthorityMessage releaseReq)
        {
            HandleReleaseRequest(clientId, releaseReq.EntityId);
        }
    }

    /// <summary>Look up a server-side entity by id, or null if it doesn't
    /// exist (e.g. it was just destroyed by another path). Used by
    /// <c>ServerConnectionMonitor</c>'s reclaim flow and by anything else
    /// that needs to inspect an entity without iterating the full list.</summary>
    public NetworkBehaviour FindEntityById(int entityId) =>
        _entitySpawner.Entities.Find(e => e.EntityId == entityId);

    private void HandleReleaseRequest(int requesterId, int entityId)
    {
        var entity = _entitySpawner.Entities.Find(e => e.EntityId == entityId);
        if (entity == null || entity.Authority != requesterId)
        {
            MonkeLogger.Debug($"HandleReleaseRequest: ignoring release from client {requesterId} for entity {entityId} (entity null or not owner)");
            return;
        }
        var policy = MonkeNetConfig.Instance?.GetSpawnConfigurationForEntityType(entity.EntityType)?.OwnershipPolicy;
        if (policy != null && !policy.AllowOwnerRelease)
        {
            MonkeLogger.Debug($"HandleReleaseRequest: policy denies owner-release for entity type {entity.EntityType}");
            return;
        }
        ChangeAuthority(entityId, 0);
    }

    private void HandleOwnershipRequest(int requesterId, int entityId)
    {
        var entity = _entitySpawner.Entities.Find(e => e.EntityId == entityId);
        // Reject silently if entity doesn't exist — a stale request from a client that
        // just saw the entity destroyed is normal, not a misbehavior to warn about.
        if (entity == null)
        {
            SendRejection(requesterId, entityId);
            return;
        }

        if (!EvaluatePolicy(entity, requesterId))
        {
            SendRejection(requesterId, entityId);
            return;
        }

        // Custom approver runs as a final gate after the declarative policy. Lets games
        // express predicates not expressible in OwnershipPolicy (e.g. "requester holds
        // a key"). A null approver passes through — pure policy-driven decisions don't
        // need it.
        if (OwnershipApprover != null && !OwnershipApprover(requesterId, entityId))
        {
            SendRejection(requesterId, entityId);
            return;
        }

        ChangeAuthority(entityId, requesterId);
    }

    private bool EvaluatePolicy(NetworkBehaviour entity, int requesterId)
    {
        var config = MonkeNetConfig.Instance?.GetSpawnConfigurationForEntityType(entity.EntityType);
        var policy = config?.OwnershipPolicy;
        if (policy == null)
        {
            // No policy configured = the entity type opts out of client-initiated claims.
            // Server-side ChangeAuthority calls (e.g. demo's vehicle-reclaim on disconnect)
            // still work; this only gates the request path.
            return false;
        }

        if (policy.RequireUnowned && entity.Authority != 0)
            return false;

        if (policy.MaxRequesterDistance > 0f)
        {
            var entityRoot = _entitySpawner.GetEntityRoot(entity);
            if (entityRoot == null) return false;
            Vector3 entityPos = entityRoot.GlobalPosition;

            float maxSq = policy.MaxRequesterDistance * policy.MaxRequesterDistance;
            bool anyInRange = false;
            foreach (int ownedId in _entitySpawner.GetAllEntitiesByAuthority(requesterId))
            {
                var ownedEntity = _entitySpawner.Entities.Find(e => e.EntityId == ownedId);
                var ownedRoot = ownedEntity != null ? _entitySpawner.GetEntityRoot(ownedEntity) : null;
                if (ownedRoot == null) continue;
                if (ownedRoot.GlobalPosition.DistanceSquaredTo(entityPos) <= maxSq)
                {
                    anyInRange = true;
                    break;
                }
            }
            if (!anyInRange) return false;
        }

        return true;
    }

    private static void SendRejection(int requesterId, int entityId)
    {
        SendCommandToClient(requesterId, new OwnershipChangeRejectedMessage { EntityId = entityId },
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
    }

    /// <summary>
    /// Reassigns ownership of a server-authoritative entity. Mutates the entity's Authority
    /// in place and broadcasts <see cref="AuthorityChangedMessage"/> so every client updates
    /// its local <c>entity.Authority</c> field. No scene swap, no rigid-body state loss —
    /// the same client-side scene instance keeps simulating; only input routing changes.
    /// <paramref name="newAuthority"/> = 0 means the server reclaims ownership.
    /// </summary>
    public void ChangeAuthority(int entityId, int newAuthority)
    {
        var entity = _entitySpawner.Entities.Find(e => e.EntityId == entityId);
        if (entity == null)
        {
            MonkeLogger.Warn($"ChangeAuthority: entity {entityId} not found on server");
            return;
        }

        int oldAuthority = entity.Authority;
        if (oldAuthority == newAuthority) return;

        entity.Authority = newAuthority;

        SendCommandToClient((int)NetworkManagerEnet.AudienceMode.Broadcast,
            new AuthorityChangedMessage { EntityId = entityId, NewAuthority = newAuthority },
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.EntityEvent);

        MonkeLogger.Info($"ChangeAuthority: entity {entityId} authority {oldAuthority} -> {newAuthority}");
    }

    protected override void OnClientConnected(int clientId)
    {
        SyncWorldState(clientId);
    }

    public List<int> GetEntityIdsForClient(int clientId) =>
        _entitySpawner.GetAllEntitiesByAuthority(clientId);

    public void DestroyEntitiesForClient(int clientId)
    {
        var ids = _entitySpawner.GetAllEntitiesByAuthority(clientId);
        foreach (int id in ids)
            DestroyEntity(id, (int)NetworkManagerEnet.AudienceMode.Broadcast);
    }

    public void OrphanEntitiesForClient(int clientId)
    {
        foreach (var entity in _entitySpawner.Entities)
            if (entity.Authority == clientId)
                entity.Authority = 0;
    }

    /// <summary>
    /// Destroys an entity only if it is currently orphaned (Authority == 0).
    /// Used by the reclaim-expiry sweep to clean up entities whose owners never reconnected,
    /// without clobbering entities that were reclaimed in the meantime.
    /// </summary>
    public void DestroyOrphanedEntity(int entityId)
    {
        var entity = _entitySpawner.Entities.Find(e => e.EntityId == entityId);
        if (entity == null || entity.Authority != 0) return;
        DestroyEntity(entityId, (int)NetworkManagerEnet.AudienceMode.Broadcast);
    }

    /// <summary>
    /// Packs the current game state for a tick (Snapshot)
    /// </summary>
    /// <param name="currentTick"></param>
    private GameSnapshotMessage PackSnapshot(int currentTick)
    {
        // Solve which entities we should include in this snapshot
        List<ServerStateSyncronizer> includedEntities = [];
        foreach (NetworkBehaviour entity in _entitySpawner.Entities)
        {
            var serverStateSyncronizer = entity.GetComponent<ServerStateSyncronizer>();
            if (serverStateSyncronizer != null)
            {
                includedEntities.Add(serverStateSyncronizer);
            }
        }

        // Pack entity data into snapshot
        var entityCount = includedEntities.Count;
        _lastEntitiesPacked = entityCount;

        var snapshot = new GameSnapshotMessage
        {
            Tick = currentTick,
            States = new IEntityStateData[entityCount]
        };

        // Include per-entity per-tick input history so observers can drive their
        // local prediction of entities they don't own with the same input the
        // server applied — for each tick, not just the latest. The
        // ServerInputReceiver lives as a sibling under ServerManager; look it up
        // once per snapshot rather than caching to keep restart paths simple.
        var inputReceiver = GetParent().GetNodeOrNull<ServerInputReceiver>("ServerInputReceiver");
        var inputs = new List<EntityInput>(entityCount * InputHistoryPerSnapshot);

        for (int i = 0; i < entityCount; i++)
        {
            snapshot.States[i] = includedEntities[i].PackEntityState();
            if (inputReceiver != null)
            {
                var recent = inputReceiver.GetRecentAppliedInputs(
                    includedEntities[i], InputHistoryPerSnapshot, currentTick);
                foreach (var (tick, input) in recent)
                {
                    inputs.Add(new EntityInput
                    {
                        EntityId = includedEntities[i].EntityId,
                        Tick = tick,
                        Input = input,
                    });
                }
            }
        }
        snapshot.Inputs = inputs.ToArray();

        // Option C: stamp the per-client input-frontier signal so each
        // client can detect engine-tick-rate drift relative to the server.
        if (inputReceiver != null)
        {
            var frontierDict = inputReceiver.LastReceivedInputTickByClient;
            var frontiers = new InputFrontier[frontierDict.Count];
            int idx = 0;
            foreach (var kv in frontierDict)
            {
                frontiers[idx++] = new InputFrontier
                {
                    ClientNetworkId = kv.Key,
                    LastInputTick = kv.Value,
                };
            }
            snapshot.InputFrontiers = frontiers;
        }

        return snapshot;
    }

    /// <summary>
    /// Notifies all clients that an Entity has spawned
    /// </summary>
    /// <param name="entityId"></param>
    /// <param name="entityType"></param>
    /// <param name="targetId"></param>
    /// <param name="authority"></param>
    public T SpawnEntity<T>(byte entityType, int authority, Vector3? position = null, string metadata = "") where T : Node3D
    {
        // Stamp the activation tick BEFORE the local server-side spawn so the
        // server's own EntitySpawner sees the same ActivationTick it broadcasts
        // to clients and holds its body frozen for the same delay window.
        // Without this the server's body would go live immediately while
        // clients held theirs frozen — the inverse asymmetry of the bug this
        // fixes.
        int spawnTick = ResolveCurrentServerTick();
        int activationTick = (SpawnActivationDelayTicks > 0 && spawnTick > 0)
            ? spawnTick + SpawnActivationDelayTicks
            : 0; // 0 means "no delay" (see EntityEventMessage.ActivationTick docs)
        var entityEvent = new EntityEventMessage
        {
            Event = EntityEventEnum.Created,
            EntityId = ++_entityIdCount,
            EntityType = entityType,
            Authority = authority,
            ActivationTick = activationTick,
            Metadata = metadata
        };

        // TODO: this should be inside metadata
        // Execute event locally and retrieve position and rotation data
        T instancedEntity = _entitySpawner.SpawnEntity(entityEvent, isServerSpawn: true) as T;
        // Caller-supplied position overrides whatever OnEntitySpawned set (some
        // entities have a hardcoded default like (0,10,0) for the ball/cube
        // drop-in). Apply BEFORE capturing entityEvent.Position so the broadcast
        // carries the final position — otherwise every client's DummyEntity
        // spawns at the default for one frame before the first snapshot
        // teleports it to the real location, which is a visible jump.
        if (position.HasValue) instancedEntity.GlobalPosition = position.Value;
        entityEvent.Position = instancedEntity.Position;
        entityEvent.Yaw = instancedEntity.Rotation.Y;

        SendCommandToClient((int)NetworkManagerEnet.AudienceMode.Broadcast, entityEvent, INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.EntityEvent);
        return instancedEntity;
    }

    /// <summary>
    /// Notifies all clients that an Entity has been destroyed
    /// </summary>
    /// <param name="entityId"></param>
    /// <param name="targetId"></param>
    public void DestroyEntity(int entityId, int targetId)
    {
        var entityEvent = new EntityEventMessage
        {
            Event = EntityEventEnum.Destroyed,
            EntityId = entityId,
            EntityType = 0,
            Authority = 0,
            Metadata = ""
        };

        _entitySpawner.DestroyEntity(entityEvent);  // Execute event locally

        SendCommandToClient(targetId, entityEvent, INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.EntityEvent);
    }

    /// <summary>
    /// Sends the whole game state to a specific clientId, used when the client connects to replicate world state
    /// </summary>
    /// <param name="clientId"></param>
    private void SyncWorldState(int clientId)
    {
        foreach (NetworkBehaviour entity in _entitySpawner.Entities)
        {
            var entityRoot = _entitySpawner.GetEntityRoot(entity);
            var entityEvent = new EntityEventMessage
            {
                Event = EntityEventEnum.Created,
                EntityId = entity.EntityId,
                EntityType = entity.EntityType,
                Authority = entity.Authority,
                Position = entityRoot?.GlobalPosition ?? Vector3.Zero,
                Yaw = entityRoot?.GlobalRotation.Y ?? 0f,
                Metadata = entity.Metadata
            };

            SendCommandToClient(clientId, entityEvent, INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.EntityEvent);
        }

    }

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Entity Manager"))
        {
            ImGui.Text($"Entity Count {_entityIdCount}");
            ImGui.Text($"Entities Packed {_lastEntitiesPacked}");
        }
    }

    // Resolve the server's current network tick for stamping EntityEventMessage.ActivationTick.
    // Returns -1 when the clock isn't initialised yet (very early-boot spawns,
    // e.g. autoload-time setup) — in that case the caller falls back to a
    // zero ActivationTick, which means "no delay" and matches legacy behaviour.
    private int ResolveCurrentServerTick()
    {
        var clock = GetParent()?.GetNodeOrNull<ServerNetworkClock>("ServerNetworkClock");
        return clock?.CurrentTick ?? -1;
    }
}