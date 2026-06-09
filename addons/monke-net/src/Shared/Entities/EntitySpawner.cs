using Godot;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MonkeNet.Shared;

public partial class EntitySpawner : Node
{
    public const int AuthorityServer = 0;

    // Collision layers — must match the names set in Project Settings → Layer Names → 3D Physics.
    private const uint LayerEnvironment  = 1;        // layer  1 — static world geometry
    private const uint LayerClientPlayers = 2;       // layer  2 — LocalPlayer, DummyPlayer
    private const uint LayerServerPlayers = 1 << 15; // layer 16 — server entities in listen-server mode

    [Signal] public delegate void EntitySpawnedEventHandler(Node3D entity);
    [Signal] public delegate void EntityDestroyedEventHandler(int entityId);

    public static EntitySpawner Instance { get; private set; }
    public List<NetworkBehaviour> Entities { get; private set; } = [];       // Server entities only
    public List<NetworkBehaviour> ClientEntities { get; private set; } = []; // Client entities only (LocalPlayer, DummyPlayer)

    public override void _Ready()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    //TODO: do not cast, make Entities a list of INetworkedEntity directly
    public NetworkBehaviour GetEntityById(int entityId)
    {
        // Prefer client entities so that in listen-server mode the LocalPlayer/DummyPlayer
        // is returned instead of the ServerPlayer sharing the same EntityId.
        foreach (var e in ClientEntities)
            if (e.EntityId == entityId) return e;
        foreach (var e in Entities)
            if (e.EntityId == entityId) return e;

        throw new MonkeNetException($"Couldn't find entity by id {entityId}");
    }

    // Returns null when the entity doesn't exist yet (e.g. snapshot arrived before EntityEventMessage.Created).
    public NetworkBehaviour TryGetEntityById(int entityId)
    {
        foreach (var e in ClientEntities)
            if (e.EntityId == entityId) return e;
        foreach (var e in Entities)
            if (e.EntityId == entityId) return e;
        return null;
    }

    // Can be called from both the server or a client, so it needs to handle both scenarios.
    // Pass isServerSpawn: true when called from a server-side component so that the server
    // scene is selected even in listen-server mode where IsServer is true for both contexts.
    public Node SpawnEntity(EntityEventMessage @event, bool isServerSpawn = false)
    {
        var config = MonkeNetConfig.Instance
            .GetSpawnConfigurationForEntityType(@event.EntityType);

        var scene = SolveWhatEntitySceneToSpawn(config, @event, isServerSpawn);

        var instance = scene?.Instantiate()
            ?? throw new MonkeNetException($"Couldn't instance entity {@event.EntityType}");

        NetworkBehaviour networkBehaviour = MonkeNetComponents.GetComponent<NetworkBehaviour>(instance)
            ?? throw new MonkeNetException($"Can't spawn entity that doesn't have a {nameof(NetworkBehaviour)} node!");

        InitializeEntity(instance, networkBehaviour, @event);
        AddChild(instance);
        if (isServerSpawn)
            Entities.Add(networkBehaviour);
        else
            ClientEntities.Add(networkBehaviour);

        // In listen-server mode both a server entity and a client entity exist in the
        // same physics space. Without adjustment they share the default collision layer
        // (1) and block each other, causing stuck movement and a visible server mesh.
        // Server entities move to layer 16 (detected by nothing on the client side) so
        // they can still collide with the environment but never block client entities.
        // Client entities move to layer 2 so they detect environment (mask bit 1) and
        // each other (mask bit 2) but not server entities (layer 16).
        if (MonkeNetManager.Instance.IsServer && ClientManager.Instance != null)
        {
            if (isServerSpawn)
            {
                SetCollisionLayerRecursive(instance, layer: LayerServerPlayers, mask: LayerEnvironment | LayerServerPlayers);
                HideMeshesRecursive(instance);
            }
            else
            {
                SetCollisionLayerRecursive(instance, layer: LayerClientPlayers, mask: LayerEnvironment | LayerClientPlayers);
            }
        }

        EmitSignal(SignalName.EntitySpawned, instance);
        networkBehaviour.OnEntitySpawned();

        // Spawn activation delay. The server stamps EntityEventMessage.ActivationTick
        // with the future server tick at which the body should go live (see
        // ServerEntityManager.SpawnEntity). Until that tick is reached on this
        // peer's clock, the body is held "frozen + no-collide" so neither it nor
        // anything else can collide against it — that closes the asymmetric-
        // spawn window where the server's body experiences contacts before the
        // clients' replicas exist locally.
        //
        // ActivationTick == 0 means "no delay requested" (legacy spawn path):
        // skip the freeze/activate dance entirely.
        int currentTick = ResolveCurrentTick();
        if (@event.ActivationTick > 0 && @event.ActivationTick > currentTick)
        {
            DeactivateForPendingSpawn(instance, networkBehaviour.EntityId, @event.ActivationTick);
        }

        string layerInfo = instance is CollisionObject3D co
            ? $" Layer={co.CollisionLayer} Mask={co.CollisionMask}"
            : "";
        string activationInfo = @event.ActivationTick > 0
            ? $" ActivationTick={@event.ActivationTick} (currentTick={currentTick})"
            : "";
        MonkeLogger.Info($"Spawned entity:{@event.EntityId} ({@event.EntityType}) Auth:{@event.Authority} ServerSpawn:{isServerSpawn}{layerInfo}{activationInfo}");
        return instance;
    }

    // Per-entity bookkeeping for spawns that are currently held frozen and
    // waiting to go live. Activation tick comes from the EntityEventMessage;
    // the cached collision layer/mask are what we restore on activation so the
    // body's wiring isn't lost across the freeze window.
    private sealed class PendingActivation
    {
        public int EntityId;
        public int ActivationTick;
        public Node3D Root;
        public uint SavedLayer;
        public uint SavedMask;
        public bool WasFrozenBefore;
    }
    private readonly List<PendingActivation> _pendingActivations = new();

    private void DeactivateForPendingSpawn(Node root, int entityId, int activationTick)
    {
        var pending = new PendingActivation
        {
            EntityId = entityId,
            ActivationTick = activationTick,
            Root = root as Node3D,
        };
        // Most networked entities are a single CollisionObject3D root (rigid
        // body, character body, or static body) with one or more
        // CollisionShape3D children. Cache the root's layer/mask so we can
        // restore exactly what was configured by the scene + listen-server
        // layer-swap above, then zero them so nothing collides.
        if (root is CollisionObject3D co)
        {
            pending.SavedLayer = co.CollisionLayer;
            pending.SavedMask = co.CollisionMask;
            co.CollisionLayer = 0;
            co.CollisionMask = 0;
        }
        // For RigidBody3D specifically, also freeze the body so gravity doesn't
        // accumulate motion during the delay window — without this every peer
        // would unfreeze a body that has fallen a different distance based on
        // when its local clock processed the spawn event, defeating the
        // synchronisation we're trying to establish.
        //
        // FreezeMode=Static (not Kinematic): both modes halt simulation, but
        // they take different transform-sync paths in Jolt. Kinematic-mode
        // transform writes are stored in JoltBody3D::kinematic_transform
        // without touching the underlying Jolt body's mPosition; the next
        // physics step then synthesises mLinearVelocity = (kinematic_target −
        // mPosition) / dt via MoveKinematic (Jolt MotionProperties.inl:17),
        // and a caller-applied GlobalPosition override that races after this
        // freeze produces a (target/dt) ghost velocity in the first snapshot
        // — observed in OffsetPushPlots/offset_push_baseline.server.log as
        // vel=(39, 30, −150) m/s for a cube spawned at (0.65, 0.5, −2.5).
        // Static-frozen bodies don't run the kinematic-motion path at all,
        // so the same transform write goes through BodyInterface::SetPositionAndRotation
        // and snaps mPosition directly with no derived velocity.
        if (root is RigidBody3D rb)
        {
            pending.WasFrozenBefore = rb.Freeze;
            rb.Freeze = true;
            // STATIC, not Kinematic: a Kinematic-frozen body has Jolt track
            // its position changes and computes an "implied" linear velocity
            // from (pos_now − pos_prev) / dt. Spawn handlers (e.g. ServerBall's
            // OnEntitySpawned teleports to y=10) write Position immediately
            // after instantiation; Jolt sees that as 10 m of motion in one
            // tick and stores velocity ≈ 600 m/s. When the body unfreezes a
            // tick later, it inherits the 600 m/s and launches skyward —
            // observed as NRB03 endY=230 m after 1 s instead of falling.
            // STATIC bodies don't track implied velocity, so the spawn-time
            // position write is a clean teleport.
            rb.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
        }
        _pendingActivations.Add(pending);
        MonkeLogger.Debug($"[SPAWN-DEACTIVATE] eid={entityId} activationTick={activationTick} held with layer/mask=(0/0), freeze=true");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_pendingActivations.Count == 0) return;
        int currentTick = ResolveCurrentTick();
        if (currentTick <= 0) return;
        // Walk backwards so RemoveAt during iteration is safe.
        for (int i = _pendingActivations.Count - 1; i >= 0; i--)
        {
            var pending = _pendingActivations[i];
            if (currentTick < pending.ActivationTick) continue;
            if (pending.Root == null || !IsInstanceValid(pending.Root))
            {
                _pendingActivations.RemoveAt(i);
                continue;
            }
            if (pending.Root is CollisionObject3D co)
            {
                co.CollisionLayer = pending.SavedLayer;
                co.CollisionMask = pending.SavedMask;
            }
            if (pending.Root is RigidBody3D rb)
            {
                rb.Freeze = pending.WasFrozenBefore;
                // Belt-and-braces: zero out any phantom velocity / spin Jolt
                // may have accumulated during the freeze window from spawn-
                // time position writes. With FreezeMode=Static this should
                // already be zero, but the Reset is cheap insurance against
                // future regressions (e.g. someone re-enables Kinematic for
                // a specific entity type) — the spawn-tick body is supposed
                // to start at rest unless the spawn handler explicitly says
                // otherwise.
                rb.LinearVelocity = Vector3.Zero;
                rb.AngularVelocity = Vector3.Zero;
            }
            MonkeLogger.Debug($"[SPAWN-ACTIVATE] eid={pending.EntityId} activationTick={pending.ActivationTick} currentTick={currentTick} restored layer={pending.SavedLayer} mask={pending.SavedMask}");
            _pendingActivations.RemoveAt(i);
        }
    }

    // Resolve "current tick" from whichever clock is available in this process.
    // Server side: ServerManager exposes the tick via signal but we read the
    // ServerNetworkClock node directly. Client side: ClientNetworkClock
    // provides the network-synced tick. In listen-server mode both exist and
    // they agree within ±1 tick — either source is fine. Returns -1 if no
    // clock is found (pre-init or torn-down state); callers should treat -1
    // as "skip activation this frame, retry next physics tick".
    private static int ResolveCurrentTick()
    {
        if (ClientManager.Instance != null)
        {
            var cc = ClientManager.Instance.GetNodeOrNull<MonkeNet.Client.ClientNetworkClock>("ClientNetworkClock");
            if (cc != null) return cc.GetCurrentTick();
        }
        if (Server.ServerManager.Instance != null)
        {
            var sc = Server.ServerManager.Instance.GetNodeOrNull<Server.ServerNetworkClock>("ServerNetworkClock");
            if (sc != null) return sc.CurrentTick;
        }
        return -1;
    }

    public void DestroyEntity(EntityEventMessage @event)
    {
        var serverEntity = Entities.Find(e => e.EntityId == @event.EntityId);
        if (serverEntity != null)
        {
            Entities.Remove(serverEntity);
            FreeEntityRoot(serverEntity);
            EmitSignal(SignalName.EntityDestroyed, @event.EntityId);
            MonkeLogger.Info($"Destroyed server entity {serverEntity.EntityId}");
            return;
        }

        var clientEntity = ClientEntities.Find(e => e.EntityId == @event.EntityId);
        if (clientEntity != null)
        {
            ClientEntities.Remove(clientEntity);
            FreeEntityRoot(clientEntity);
            EmitSignal(SignalName.EntityDestroyed, @event.EntityId);
            MonkeLogger.Info($"Destroyed client entity {clientEntity.EntityId}");
            return;
        }

        MonkeLogger.Error($"DestroyEntity: entity {@event.EntityId} not found");
    }

    public void DestroyClientEntity(EntityEventMessage @event)
    {
        var clientEntity = ClientEntities.Find(e => e.EntityId == @event.EntityId);
        if (clientEntity != null)
        {
            ClientEntities.Remove(clientEntity);
            FreeEntityRoot(clientEntity);
            EmitSignal(SignalName.EntityDestroyed, @event.EntityId);
            MonkeLogger.Info($"Destroyed client entity {clientEntity.EntityId}");
            return;
        }
        MonkeLogger.Error($"DestroyClientEntity: entity {@event.EntityId} not found in ClientEntities");
    }

    public void ClearClientEntities()
    {
        foreach (var entity in ClientEntities.ToList())
            FreeEntityRoot(entity);
        ClientEntities.Clear();
    }

    public void ClearServerEntities()
    {
        foreach (var entity in Entities.ToList())
            FreeEntityRoot(entity);
        Entities.Clear();
    }

    public Node3D GetEntityRoot(NetworkBehaviour entity)
    {
        Node current = entity;
        while (current.GetParent() != this && current.GetParent() != null)
            current = current.GetParent();
        return current as Node3D;
    }

    private void FreeEntityRoot(NetworkBehaviour entity)
    {
        // Walk up until we find the direct child of EntitySpawner, then free it
        Node current = entity;
        while (current.GetParent() != this && current.GetParent() != null)
            current = current.GetParent();
        current.QueueFree();
    }

    public List<int> GetAllEntitiesByAuthority(int authority)
    {
        List<int> entitiesGeneratedByAuthority = [];

        for (int i = 0; i < Entities.Count; i++)
        {
            if (Entities[i].Authority == authority)
            {
                entitiesGeneratedByAuthority.Add(Entities[i].EntityId);
            }
        }

        return entitiesGeneratedByAuthority;
    }

    private static void InitializeEntity(Node node, NetworkBehaviour entity, EntityEventMessage @event)
    {
        node.Name = @event.EntityId.ToString();
        entity.EntityId = @event.EntityId;
        entity.EntityType = @event.EntityType;
        entity.Authority = @event.Authority;
        entity.Metadata = @event.Metadata;

        // Apply the spawn pose carried in the EntityEventMessage. For initial spawns this is
        // Vector3.Zero / 0f (the default), so nothing visible changes. For reclaim spawns the
        // server fills these from the orphaned entity's last known transform — without this
        // the reclaimed body would respawn at scene-origin instead of where it was when the
        // owner disconnected.
        if (node is Node3D node3D)
        {
            node3D.Position = @event.Position;
            var rotation = node3D.Rotation;
            rotation.Y = @event.Yaw;
            node3D.Rotation = rotation;
        }
    }

    private PackedScene SolveWhatEntitySceneToSpawn(EntitySpawnConfiguration entitySpawnConfig, EntityEventMessage @event, bool isServerSpawn)
    {
        if (isServerSpawn)
            return entitySpawnConfig.ServerScene;
        // Owner vs non-owner used to pick different scenes (predicted vs interpolated).
        // The unified-prediction refactor collapsed both into a single ClientScene that
        // predicts locally regardless of ownership — authority only governs whose input
        // drives the entity.
        return entitySpawnConfig.ClientScene;
    }

    private static void SetCollisionLayerRecursive(Node node, uint layer, uint mask)
    {
        if (node is CollisionObject3D body)
        {
            body.CollisionLayer = layer;
            body.CollisionMask = mask;
        }
        foreach (Node child in node.GetChildren())
            SetCollisionLayerRecursive(child, layer, mask);
    }

    private static void HideMeshesRecursive(Node node)
    {
        if (node is MeshInstance3D mesh)
            mesh.Visible = false;
        foreach (Node child in node.GetChildren())
            HideMeshesRecursive(child);
    }
}