namespace MonkeNet.Shared;

public interface ITimestampProvider
{
    ulong GetTicksMsec();
}

public class RealTimestampProvider : ITimestampProvider
{
    public ulong GetTicksMsec() => Godot.Time.GetTicksMsec();
}
