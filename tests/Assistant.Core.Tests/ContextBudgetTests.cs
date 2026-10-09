using Assistant.Core.Budgeting;
using Assistant.Core.Contracts;
using Assistant.Core.Settings;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>How the model's window is shared between the prompt and the answer, and which limit applies.</summary>
public sealed class ContextBudgetTests
{
    private static readonly ContextLimitSettings Limits = new()
    {
        NormalContextTokens = 4096,
        HeavyContextTokens = 16384,
        ReservedOutputTokens = 1024,
    };

    [Fact]
    public void TheDefaults_HoldChatToTheNormalLimit_AndDocumentsToTheHeavyOne()
    {
        var defaults = new ContextLimitSettings();
        var model = new ModelInfo("m", 32768);

        // 8,000 tokens for a chat and 32,000 for a conversation that carries files, which are also the two windows the model is loaded with.
        Assert.Equal(new ContextBudget(8000, 1024), ContextBudget.Resolve(model, defaults, ContextBudgetMode.Normal));
        Assert.Equal(new ContextBudget(32000, 1024), ContextBudget.Resolve(model, defaults, ContextBudgetMode.Heavy));
        Assert.Equal(ModelFiles.DefaultContextLength, defaults.NormalContextTokens);
        Assert.Equal(ModelFiles.DocumentContextLength, defaults.HeavyContextTokens);
        Assert.Equal(defaults, new AppSettings().ContextLimits);
    }

    [Fact]
    public void TheWindow_IsTheModesLimit_UnlessTheModelsOwnIsSmaller()
    {
        var small = new ModelInfo("small", 2048);
        var big = new ModelInfo("big", 65536);

        Assert.Equal(2048, ContextBudget.Resolve(small, Limits, ContextBudgetMode.Normal).WindowTokens);
        Assert.Equal(2048, ContextBudget.Resolve(small, Limits, ContextBudgetMode.Heavy).WindowTokens);
        Assert.Equal(4096, ContextBudget.Resolve(big, Limits, ContextBudgetMode.Normal).WindowTokens);
        Assert.Equal(16384, ContextBudget.Resolve(big, Limits, ContextBudgetMode.Heavy).WindowTokens);
    }

    [Fact]
    public void TheLimits_AreConfigurable()
    {
        var limits = Limits with { NormalContextTokens = 3000, HeavyContextTokens = 6000, ReservedOutputTokens = 500 };
        var model = new ModelInfo("m", 8192);

        Assert.Equal(new ContextBudget(3000, 500), ContextBudget.Resolve(model, limits, ContextBudgetMode.Normal));
        Assert.Equal(new ContextBudget(6000, 500), ContextBudget.Resolve(model, limits, ContextBudgetMode.Heavy));
        Assert.Equal(2500, ContextBudget.Resolve(model, limits, ContextBudgetMode.Normal).PromptTokens);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ALimitOfZeroOrLess_LeavesTheWindowToTheModel(int limit)
    {
        var limits = Limits with { NormalContextTokens = limit, HeavyContextTokens = limit };
        var model = new ModelInfo("m", 8192);

        Assert.Equal(8192, ContextBudget.Resolve(model, limits, ContextBudgetMode.Normal).WindowTokens);
        Assert.Equal(8192, ContextBudget.Resolve(model, limits, ContextBudgetMode.Heavy).WindowTokens);
    }

    [Fact]
    public void AModelWithNoKnownWindow_HasTheDefaultOne()
    {
        var limits = Limits with { NormalContextTokens = 0 };

        Assert.Equal(
            ModelFiles.DefaultContextLength,
            ContextBudget.Resolve(new ModelInfo("m", 0), limits, ContextBudgetMode.Normal).WindowTokens);
    }

    [Theory]
    [InlineData(4096, 1024, 1024)]
    [InlineData(4096, 3000, 2048)]
    [InlineData(1000, 900, 500)]
    [InlineData(2, 1024, 1)]
    public void TheAnswersShare_NeverTakesMoreThanHalfTheWindow(int window, int wanted, int expected)
    {
        var limits = Limits with { NormalContextTokens = window, ReservedOutputTokens = wanted };

        var budget = ContextBudget.Resolve(new ModelInfo("m", 1_000_000), limits, ContextBudgetMode.Normal);

        Assert.Equal(expected, budget.ReservedOutputTokens);
        Assert.True(budget.PromptTokens >= budget.ReservedOutputTokens || window < 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnAnswerShareOfZeroOrLess_MeansTheDefault(int wanted)
    {
        var limits = Limits with { ReservedOutputTokens = wanted };

        Assert.Equal(
            1024,
            ContextBudget.Resolve(new ModelInfo("m", 8192), limits, ContextBudgetMode.Normal).ReservedOutputTokens);
    }
}
