using Godot;
using MonkeNet.Shared;

namespace MonkeNet.Client;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class ClientInterpolatedEntity : ClientNetworkBehaviour
{
    public virtual void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor) { }

    /// <summary>
    /// Hard-snap the body's full state (position, rotation, linear + angular velocity)
    /// to <paramref name="state"/>. Called by <see cref="ClientPredictionManager"/>
    /// at the start of a rollback so that the resim loop's per-tick <c>SpaceStep</c>
    /// collides the just-reconciled predicted player against the cube/ball at its
    /// authoritative state for the rollback tick, rather than at whatever pose
    /// Jolt's local simulation has drifted to since.
    ///
    /// Default no-op so non-physics interpolated entities (UI labels, particle
    /// emitters, etc.) aren't disturbed. Override on each entity whose body
    /// participates in the physics space.
    /// </summary>
    public virtual void HardSnapToAuthoritativeState(IEntityStateData state) { }
}
