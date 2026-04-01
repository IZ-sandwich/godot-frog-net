using Godot;
using MonkeNet.Shared;

namespace MonkeNet.Client;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class ClientInterpolatedEntity : ClientNetworkBehaviour
{
    public virtual void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor) { }
}
