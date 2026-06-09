using GdUnit4;
using MonkeNet.Tests.Quantitative.Scenarios;

namespace MonkeNet.Tests.Quantitative;

/// <summary>
/// Focused S7 policy-comparison suite — runs the S7 chaos pile scenario
/// across the same network-condition matrix for the BlendedVelocity and
/// AuthorityTransfer policies, producing CSV rows that line up next to the
/// default Hysteresis S7 rows for direct comparison.
///
/// Separate test cases for BV-only, AT-only, and both let CI or local
/// runs pick a focused subset without re-running the whole quantitative
/// matrix.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class S7PolicyComparisonSuite : QuantitativeTestBase
{
    [BeforeTest] public void SetUp()    => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    [TestCase]
    public void RunS7BlendedVelocity()
    {
        RunMatrix(new IScenario[] { new S7BV_MultiBodyChaos() });
    }

    [TestCase]
    public void RunS7AuthorityTransfer()
    {
        RunMatrix(new IScenario[] { new S7AT_MultiBodyChaos() });
    }

    [TestCase]
    public void RunS7BothPolicies()
    {
        RunMatrix(new IScenario[]
        {
            new S7BV_MultiBodyChaos(),
            new S7AT_MultiBodyChaos(),
        });
    }

    [TestCase]
    public void RunS7Hysteresis()
    {
        // Default S7 — cubes/balls keep their inspector-default Hysteresis
        // policy (no metadata override). Provides the third axis for direct
        // comparison against BV and AT under identical code state.
        RunMatrix(new IScenario[] { new S7_MultiBodyChaos() });
    }

    [TestCase]
    public void RunS7AllPolicies()
    {
        RunMatrix(new IScenario[]
        {
            new S7_MultiBodyChaos(),
            new S7BV_MultiBodyChaos(),
            new S7AT_MultiBodyChaos(),
        });
    }
}
