using Assistant.Tools.Integrations;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Step 107: whether the runtime the Assistant sets up satisfies what a package says it needs.</summary>
public sealed class VersionConstraintTests
{
    private static readonly Version Node = new(24, 21, 0);
    private static readonly Version Python = new(3, 12, 15);

    [Theory]
    [InlineData(">=18", true)]
    [InlineData(">=18.0.0", true)]
    [InlineData(">=24.21.0", true)]
    [InlineData(">=24.21.1", false)]
    [InlineData(">=25", false)]
    [InlineData(">24.21.0", false)]
    [InlineData(">24", false)]
    [InlineData("<25", true)]
    [InlineData("<24", false)]
    [InlineData("<=24.21.0", true)]
    [InlineData("<=24.20", false)]
    [InlineData("^18", false)]
    [InlineData("^24.0.0", true)]
    [InlineData("^24.22.0", false)]
    [InlineData("^18 || ^20 || ^22 || ^24", true)]
    [InlineData("^18 || ^20", false)]
    [InlineData("~24.21.0", true)]
    [InlineData("~24.20.0", false)]
    [InlineData(">=18 <25", true)]
    [InlineData(">=18 <24", false)]
    [InlineData(">= 18", true)]
    [InlineData("24.x", true)]
    [InlineData("23.x", false)]
    [InlineData("24.21.0", true)]
    [InlineData("24.21.1", false)]
    [InlineData("18 - 25", true)]
    [InlineData("18 - 20", false)]
    [InlineData("*", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("^0.2.3", false)]
    public void NpmRangesAreCheckedAgainstTheNodeTheAssistantSetsUp(string? range, bool expected)
    {
        Assert.Equal(expected, VersionConstraint.SatisfiesNpm(range, Node));
    }

    [Theory]
    [InlineData("banana")]
    [InlineData(">=")]
    [InlineData("^")]
    [InlineData("1.2.3.4.5")]
    [InlineData("git+https://example.com/x")]
    public void AnNpmRangeThatCannotBeReadIsUnknownAndNeverAPass(string range)
    {
        Assert.Null(VersionConstraint.SatisfiesNpm(range, Node));
    }

    [Theory]
    [InlineData(">=3.10", true)]
    [InlineData(">=3.10,<4", true)]
    [InlineData(">=3.13", false)]
    [InlineData(">=3.10, <3.12", false)]
    [InlineData(">3.12.15", false)]
    [InlineData("<=3.12.15", true)]
    [InlineData("==3.12.*", true)]
    [InlineData("==3.11.*", false)]
    [InlineData("!=3.12.*", false)]
    [InlineData("!=3.9.*", true)]
    [InlineData("~=3.10", true)]
    [InlineData("~=3.13", false)]
    [InlineData("~=3.12.1", true)]
    [InlineData("~=3.11.1", false)]
    [InlineData(">=3.9,!=3.9.*,!=3.10.*", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void PythonSpecifiersAreCheckedAgainstThePythonTheAssistantSetsUp(string? specifier, bool expected)
    {
        Assert.Equal(expected, VersionConstraint.SatisfiesPython(specifier, Python));
    }

    [Theory]
    [InlineData("banana")]
    [InlineData(">=")]
    [InlineData("=>3.10")]
    public void APythonSpecifierThatCannotBeReadIsUnknown(string specifier)
    {
        Assert.Null(VersionConstraint.SatisfiesPython(specifier, Python));
    }

    [Theory]
    [InlineData("24.21.0", true, 24, 21, 0)]
    [InlineData("3.12.15", true, 3, 12, 15)]
    [InlineData("v18", true, 18, 0, 0)]
    [InlineData("nonsense", false, 0, 0, 0)]
    [InlineData(null, false, 0, 0, 0)]
    public void ARuntimesVersionIsReadAsNumbers(string? text, bool ok, int major, int minor, int patch)
    {
        Assert.Equal(ok, VersionConstraint.TryParseVersion(text, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }
}
