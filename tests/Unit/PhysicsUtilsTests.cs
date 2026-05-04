using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Unit;

/// <summary>
/// PU-01..PU-08: PhysicsUtils — tick conversions, angle quantization, modulo helpers.
/// Requires Godot runtime because the conversion constants depend on
/// Engine.PhysicsTicksPerSecond resolved at class-load time.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PhysicsUtilsTests
{
    // PU-01 ────────────────────────────────────────────────────────────────────
    [TestCase]
    public void MsecToTick_OneFrame_EqualsOne()
    {
        // One full physics frame in milliseconds should round to exactly 1 tick
        int oneFrameMsec = (int)PhysicsUtils.FrameTimeInMsec;
        AssertThat(PhysicsUtils.MsecToTick(oneFrameMsec)).IsEqual(1);
    }

    // PU-02 ────────────────────────────────────────────────────────────────────
    [TestCase]
    public void MsecToTick_FractionalFrame_RoundsUp()
    {
        // CeilToInt: 1ms is well under one frame at 60Hz (~16.67ms) so it rounds up to 1
        AssertThat(PhysicsUtils.MsecToTick(1)).IsEqual(1);
    }

    // PU-03 ────────────────────────────────────────────────────────────────────
    [TestCase]
    public void SecToTick_OneDeltaTime_EqualsOne()
    {
        AssertThat(PhysicsUtils.SecToTick(PhysicsUtils.DeltaTime)).IsEqual(1);
    }

    // PU-04 ────────────────────────────────────────────────────────────────────
    [TestCase]
    public void SecToTick_OneSecond_EqualsPhysicsTickRate()
    {
        // SecToTick(1) should equal the configured physics tickrate (typically 60)
        AssertThat(PhysicsUtils.SecToTick(1f)).IsEqual(Engine.PhysicsTicksPerSecond);
    }

    // PU-05 ────────────────────────────────────────────────────────────────────
    [TestCase]
    public void EncodeDecodeAngle_RoundTrip_WithinQuantizationTolerance()
    {
        // Single byte = 256 buckets across [0, Tau), so worst-case error is Tau/256
        const float tolerance = Mathf.Tau / 256f;
        float[] samples = { 0f, Mathf.Tau / 8f, Mathf.Pi / 2f, Mathf.Pi, 3f * Mathf.Pi / 2f };

        foreach (float original in samples)
        {
            byte encoded = PhysicsUtils.EncodeRadianAngleAsByte(original);
            float decoded = PhysicsUtils.DecodeRadianAngleAsByte(encoded);
            AssertThat(Mathf.Abs(decoded - original)).IsLess(tolerance);
        }
    }

    // PU-06 ────────────────────────────────────────────────────────────────────
    [TestCase]
    public void EncodeAngle_Zero_DecodesToZero()
    {
        byte encoded = PhysicsUtils.EncodeRadianAngleAsByte(0f);
        AssertThat(PhysicsUtils.DecodeRadianAngleAsByte(encoded)).IsEqual(0f);
    }

    // PU-07 ────────────────────────────────────────────────────────────────────
    [TestCase]
    public void EveryNSeconds_AtSecondBoundary_IsTrue()
    {
        int ticksPerSecond = PhysicsUtils.SecToTick(1f);

        AssertThat(PhysicsUtils.EveryNSeconds(0, 1)).IsTrue();             // 0 % anything == 0
        AssertThat(PhysicsUtils.EveryNSeconds(ticksPerSecond, 1)).IsTrue();
        AssertThat(PhysicsUtils.EveryNSeconds(ticksPerSecond * 5, 1)).IsTrue();
    }

    // PU-08 ────────────────────────────────────────────────────────────────────
    [TestCase]
    public void EveryNSeconds_OffBoundary_IsFalse()
    {
        int ticksPerSecond = PhysicsUtils.SecToTick(1f);

        AssertThat(PhysicsUtils.EveryNSeconds(ticksPerSecond - 1, 1)).IsFalse();
        AssertThat(PhysicsUtils.EveryNSeconds(1, 1)).IsFalse();
    }
}
