using GdUnit4;
using MonkeNet.Tests.Quantitative;
using MonkeNet.Tests.Quantitative.Scenarios;

// Namespace deliberately differs from MonkeNet.Tests.Quantitative so this
// suite does NOT match the QuantitativeSuite namespace substring filter — it
// runs the same scenario / metrics / dashboard pipeline (subclasses
// QuantitativeTestBase) but writes to its own TestResults/StackPush/
// subdirectory and is invoked explicitly via:
//     run-tests.ps1 StackPush
// or as a one-off
//     run-tests.ps1 StackPushSuite.RunCubeStackPushMatrix
namespace MonkeNet.Tests.StackPush;

/// <summary>
/// Focused 3-cube-stack push test. Exists to isolate the "push looks worse
/// under C1 than under C4" inversion the user reported in S7-MultiBodyChaos —
/// S7 mixes 40 bodies + arbitrary contact manifolds so the visible artifact
/// gets averaged across too many entities to read cleanly. This suite runs
/// the same player-pushes-cubes setup with just a 3-tall vertical stack so
/// each cube's per-frame behaviour is individually visible in the recorded
/// MP4 and per-cube metrics aren't smeared by the rest of the pile.
///
/// <para>
/// Writes its own dashboard.svg + summary.csv + per-condition MP4s under
/// <c>TestResults/StackPush/</c> via the inherited <c>ArtifactSubdir</c>
/// override below. The MP4 + the debug log are the primary artifacts —
/// numeric metrics describe what the eye already sees in the video; reading
/// them in isolation would be misleading because the visible artifact is a
/// stuttering catch-up pattern that M14 only partially captures (see the
/// "M_freeze_frame_ratio" proposal in the user thread).
/// </para>
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class StackPushSuite : QuantitativeTestBase
{
    // Per-suite artifact directory so this test's outputs land in
    // TestResults/StackPush/<run-folder>/ instead of being interleaved with
    // the regular Quantitative matrix runs. dashboard.svg + summary.csv +
    // per-condition MP4s + debug logs all go to the same per-run folder.
    protected override string ArtifactSubdir => "StackPush";

    [BeforeTest] public void SetUp()    => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    /// <summary>Run the 3-cube-stack push across the full network condition
    /// matrix (C0, C1, C2, C3, C4, C5, CJITTER). Each cell produces an MP4 +
    /// debug log + a row in <c>summary.csv</c>. The dashboard SVG plots all
    /// condition rows side-by-side so the C1-vs-C4 inversion is visible at a
    /// glance.</summary>
    [TestCase]
    public void RunCubeStackPushMatrix()
    {
        RunMatrix(new IScenario[] { new S3CubeStackPush() });
    }

    /// <summary>Single-cube variant. Identical setup to the stack matrix
    /// but spawns ONE cube instead of three. Used to isolate the
    /// player-vs-cube contact event from the stack-collapse dynamics — see
    /// <see cref="S3SingleCubePush"/>.</summary>
    [TestCase]
    public void RunSingleCubePushMatrix()
    {
        RunMatrix(new IScenario[] { new S3SingleCubePush() });
    }

    /// <summary>Single-cube push run BACK-TO-BACK in both decay modes:
    /// Exponential (legacy default) and Linear (Q3 / UE-Linear-mode style).
    /// Two row-groups in the same summary CSV so the M14 / M15 / M16
    /// difference is immediately visible without diffing across runs.
    /// See <see cref="S3SingleCubePushLinear"/> for how the mode is flipped
    /// mid-scenario via the smoother-decay-mode harness command.</summary>
    [TestCase]
    public void RunSingleCubePushDecayModeComparison()
    {
        RunMatrix(new IScenario[] { new S3SingleCubePush(), new S3SingleCubePushLinear() });
    }

    /// <summary>Three-way decay-mode comparison: Exponential (legacy default),
    /// Linear (Q3 / UE-Linear style), and MagnitudeAdaptive (Photon Fusion 2
    /// InterpolatedErrorCorrectionSettings style). Runs the same player +
    /// single-cube setup across all 7 network conditions for each of the
    /// three modes in sequence; the summary CSV has 21 rows so the per-
    /// condition deltas are immediately diffable. See
    /// <see cref="S3SingleCubePushMagnitudeAdaptive"/> and
    /// <see cref="S3SingleCubePushLinear"/> for how each mode is enabled
    /// via the smoother-decay-mode harness command.</summary>
    [TestCase]
    public void RunSingleCubePushAllDecayModes()
    {
        RunMatrix(new IScenario[]
        {
            new S3SingleCubePush(),
            new S3SingleCubePushLinear(),
            new S3SingleCubePushMagnitudeAdaptive(),
        });
    }

    /// <summary>No-prediction baseline. Sets
    /// <c>ClientPredictionManager.MaxRollbackTicks = 0</c> so every snapshot
    /// routes through <c>BlendToAuthViaPvb</c> instead of any forward resim.
    /// What pure snapshot interpolation + PVB looks like on the same
    /// scenario, for direct comparison against the predicting modes.</summary>
    [TestCase]
    public void RunSingleCubePushNoResimMatrix()
    {
        RunMatrix(new IScenario[] { new S3SingleCubePushNoResim() });
    }

    /// <summary>3-cube stack push with prediction disabled
    /// (<c>MaxRollbackTicks = 0</c>). Multi-body version of
    /// <see cref="RunSingleCubePushNoResimMatrix"/>; see
    /// <see cref="S3CubeStackPushNoResim"/> for rationale. The stack
    /// scenario amplifies the contact-cascade rollback artefact in the
    /// predicting modes, so the snap+PVB-only baseline is most informative
    /// here.</summary>
    [TestCase]
    public void RunCubeStackPushNoResimMatrix()
    {
        RunMatrix(new IScenario[] { new S3CubeStackPushNoResim() });
    }

    /// <summary>3-cube stack push, predicting (default Exp smoother) and
    /// NoResim back-to-back so the two row-groups land in the same CSV for
    /// direct diffing.</summary>
    [TestCase]
    public void RunCubeStackPushPredictVsNoResim()
    {
        RunMatrix(new IScenario[] { new S3CubeStackPush(), new S3CubeStackPushNoResim() });
    }

    /// <summary>T2 KinematicInterpolation policy on the cubes. Player keeps
    /// default Hysteresis (predict + rollback); cubes are frozen-kinematic
    /// and pose-driven from snapshot history, but the proximity-sweep
    /// upgrade still flips them to Resim before contact so the player
    /// experiences clean contact resolution. See
    /// <see cref="S3CubeStackPushT2KinematicInterp"/>.</summary>
    [TestCase]
    public void RunCubeStackPushT2KIMatrix()
    {
        RunMatrix(new IScenario[] { new S3CubeStackPushT2KinematicInterp() });
    }

    /// <summary>Hybrid: predict the player normally, but flip
    /// <c>BypassResimUpgrade=true</c> on every prop so cubes stay
    /// Interpolate-tier forever and blend toward auth on each snapshot —
    /// effectively the T2 KinematicInterpolation policy on the cubes
    /// only. Compares against the pure-predict and pure-no-resim 3-cube
    /// results to quantify whether decoupling the policies recovers the
    /// best of both.</summary>
    [TestCase]
    public void RunCubeStackPushHybridMatrix()
    {
        RunMatrix(new IScenario[] { new S3CubeStackPushHybridPredictPlayer() });
    }
}
