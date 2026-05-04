using MonkeNet.Shared;

namespace MonkeNet.Tests.Infrastructure;

/// <summary>
/// Controllable timestamp source for tests that exercise silence detection
/// in <see cref="MonkeNet.Client.ClientConnectionMonitor"/>.
/// Set <see cref="CurrentMs"/> directly to advance perceived wall-clock time.
/// </summary>
public class FakeTimestampProvider : ITimestampProvider
{
    public ulong CurrentMs { get; set; } = 0;

    public ulong GetTicksMsec() => CurrentMs;

    public void AdvanceBy(ulong ms) => CurrentMs += ms;
}
