using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class CleanupRequestBudgetTests
{
    // The cleanup summary reports this reviewed envelope, and the run's request bounds come from here. The core tests
    // supply their own budget, so these literals pin the production values.
    [Fact]
    public void Production_budget_reports_its_reviewed_envelope_and_bounds()
    {
        using var budget = new CleanupRequestBudget();

        Assert.Equal("Azure.Core1.62.0/.NET11.0.0-rc.3.26522.148/direct HTTP1.x/no handler authentication", budget.EnvelopeScope);
        Assert.Equal(4, budget.MaxSendsPerDispatch);
        Assert.True(budget.CanStart(128, 32));
        Assert.False(budget.CanStart(129, 0));
        Assert.False(budget.CanStart(1, 33));
    }
}
