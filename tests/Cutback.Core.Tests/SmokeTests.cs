namespace Cutback.Core.Tests;

/// <summary>
/// Placeholder so the test project has something to run in step 1. Replaced by real
/// SegmentList tests in step 2.
/// </summary>
public sealed class SmokeTests
{
    [Fact]
    public void Test_project_is_wired_up()
    {
        typeof(Cutback.Core.Placeholder).Assembly.GetName().Name.Should().Be("Cutback.Core");
    }
}
