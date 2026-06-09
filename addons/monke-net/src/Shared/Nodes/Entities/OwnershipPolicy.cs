using Godot;

namespace MonkeNet.Shared;

/// <summary>
/// Declarative per-entity-type policy for authority-request approval. Attached to an
/// <see cref="EntitySpawnConfiguration"/>. The server consults this policy when a client
/// sends <c>OwnershipChangeRequestMessage</c> or <c>ReleaseAuthorityMessage</c>; a null
/// policy on the config means "reject all requests" (the secure default — games opt in
/// per entity type by assigning a policy resource).
/// </summary>
[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class OwnershipPolicy : Resource
{
    /// <summary>
    /// Approve a claim only if the entity is currently unowned (Authority == 0).
    /// Default true. Set false to allow stealing ownership from another client (rare).
    /// </summary>
    [Export] public bool RequireUnowned { get; set; } = true;

    /// <summary>
    /// Max distance in meters from any of the requesting client's owned entities to the
    /// requested entity. A request is approved only if at least one of the requester's
    /// owned entities is within this distance of the target. -1 disables the check
    /// (any requester can claim regardless of distance). Default -1.
    /// </summary>
    [Export] public float MaxRequesterDistance { get; set; } = -1f;

    /// <summary>
    /// If true, the entity's current owner can release authority back to the server
    /// (Authority=0) by sending <c>ReleaseAuthorityMessage</c>. Default true. Set false
    /// to make ownership sticky (server must explicitly reclaim).
    /// </summary>
    [Export] public bool AllowOwnerRelease { get; set; } = true;

    /// <summary>
    /// When true, the server treats the entity's RigidBody3D as a kinematic relay
    /// while a client holds authority: the server stops simulating it and instead
    /// mirrors the owner-reported pose+velocity (via <c>EntityStateRelayMessage</c>).
    /// On release the body unfreezes and the server resumes natural physics from the
    /// last relayed state. Eliminates the on-release pose snap that arose when the
    /// server's parallel sim diverged from the owner's. Only meaningful for entities
    /// whose root is a <c>RigidBody3D</c> and whose client-side policy is
    /// <c>InterpolationPolicy.AuthorityTransfer</c>.
    /// </summary>
    [Export] public bool UseAuthorityTransferRelay { get; set; } = false;

    /// <summary>
    /// Bitmask of collision-layer bits to clear from the entity's RigidBody3D
    /// (BOTH <c>CollisionLayer</c> and <c>CollisionMask</c>) while frozen as a
    /// relay. On release, the original layer/mask values are restored.
    ///
    /// Use case: the server's own player physics still simulates against owned
    /// (kinematic-frozen) cubes — and treats them as immovable walls because
    /// kinematic bodies don't yield to contact impulses. The server's player
    /// then decelerates dramatically on every cube hit, and each subsequent
    /// snapshot's HasMisspredicted check pulls the owner client's velocity
    /// down to match. Clearing the relevant layer/mask bits while frozen
    /// removes the cube from the server's physics graph (relative to the
    /// chosen bits) so the server's player passes through it. Visuals on
    /// other clients are unaffected — they read pose from the snapshot
    /// (relay-driven, = owner's view).
    ///
    /// Default 0 = collision unchanged (no relay-time mask edit).
    /// </summary>
    [Export(PropertyHint.Layers3DPhysics)] public uint RelayDisableCollisionAgainst { get; set; } = 0;
}
