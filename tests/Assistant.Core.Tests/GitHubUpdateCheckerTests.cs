using System.Net;
using System.Text;
using Assistant.Core.Settings;
using Assistant.Core.Updates;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// The update check keeps AirMirror's rules: never by itself but when the user opens the window, at most once in thirty days (counted from GitHub's last
/// answer of any kind), not again for a version the user ignored, never for a draft or pre-release, and at once from the button. Local Only does not stop
/// it (the user's choice, 0.1.149).
/// </summary>
public sealed class GitHubUpdateCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static string Release(string tag, bool prerelease = false, bool draft = false, string? page = null) =>
        $$"""{"tag_name":"{{tag}}","html_url":"{{page ?? "https://github.com/MouayadYT/Kiri/releases/tag/" + tag}}","prerelease":{{(prerelease ? "true" : "false")}},"draft":{{(draft ? "true" : "false")}}}""";

    private static (GitHubUpdateChecker Checker, FixedSettings Settings, GitHub GitHub, TestClock Clock) Create(
        string answer, HttpStatusCode status = HttpStatusCode.OK, bool localOnly = false, DateTimeOffset? lastChecked = null, string? dismissed = null,
        string current = "0.1.148")
    {
        var settings = new FixedSettings(new AppSettings
        {
            ContextLimits = new ContextLimitSettings(),
            Privacy = new PrivacySettings { LocalOnly = localOnly },
            Updates = new UpdateSettings { LastCheckedAt = lastChecked, DismissedVersion = dismissed },
        });
        var github = new GitHub(answer, status);
        var clock = new TestClock(Now);
        return (new GitHubUpdateChecker(settings, clock: clock, handler: github, currentVersion: current), settings, github, clock);
    }

    private static async Task<AvailableUpdate?> TriggerAsync(GitHubUpdateChecker checker)
    {
        AvailableUpdate? found = null;
        checker.UpdateAvailable += (_, update) => found = update;
        checker.TriggerCheckIfDue();
        await checker.Checking;
        return found;
    }

    [Fact]
    public async Task ANewerReleaseIsAnnounced_WithItsPage_AndTheTimeOfTheLookIsKept()
    {
        var (checker, settings, github, _) = Create(Release("v0.2.0"));

        var found = await TriggerAsync(checker);

        Assert.Equal(new AvailableUpdate("0.1.148", "v0.2.0", "https://github.com/MouayadYT/Kiri/releases/tag/v0.2.0"), found);
        var request = Assert.Single(github.Requests);
        Assert.Equal("https://api.github.com/repos/MouayadYT/Kiri/releases/latest", request.Uri);
        Assert.Contains("application/vnd.github+json", request.Accept, StringComparison.Ordinal);
        Assert.StartsWith("Kiri/0.1.148", request.UserAgent, StringComparison.Ordinal);
        Assert.Equal(Now, settings.Current.Updates.LastCheckedAt);
    }

    [Theory]
    [InlineData("v0.1.148")]
    [InlineData("0.1.147")]
    [InlineData("v0.1.148-beta.2")]
    [InlineData("v0.1.148+build.7")]
    public async Task TheSameOrAnOlderRelease_IsNotAnnounced(string tag)
    {
        var (checker, _, _, _) = Create(Release(tag));

        Assert.Null(await TriggerAsync(checker));
        Assert.Null((await checker.CheckNowAsync()).Update);
        Assert.False((await checker.CheckNowAsync()).Failed);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task APreReleaseOrADraft_IsNeverAnnounced(bool prerelease, bool draft)
    {
        var (checker, _, _, _) = Create(Release("v9.0.0", prerelease, draft));

        Assert.Null(await TriggerAsync(checker));
        Assert.Null((await checker.CheckNowAsync()).Update);
    }

    [Fact]
    public async Task ItIsAskedAtMostOnceInThirtyDays_CountedFromTheLastAnswer()
    {
        var (checker, settings, github, clock) = Create(Release("v0.2.0"), lastChecked: Now - TimeSpan.FromDays(29));

        Assert.Null(await TriggerAsync(checker));
        Assert.Empty(github.Requests);

        clock.Advance(TimeSpan.FromDays(1));
        Assert.NotNull(await TriggerAsync(checker));
        Assert.Single(github.Requests);
        Assert.Equal(clock.GetUtcNow(), settings.Current.Updates.LastCheckedAt);

        // Opened again the same day: not asked again.
        Assert.Null(await TriggerAsync(checker));
        Assert.Single(github.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnAnswerThatSaysNo_AlsoCountsAsTheLook_SoGitHubIsNotAskedAgainAndAgain(HttpStatusCode status)
    {
        var (checker, settings, github, _) = Create("{}", status);

        Assert.Null(await TriggerAsync(checker));
        Assert.Equal(Now, settings.Current.Updates.LastCheckedAt);
        Assert.Null(await TriggerAsync(checker));
        Assert.Single(github.Requests);

        // The button says it could not tell, rather than "up to date".
        Assert.True((await checker.CheckNowAsync()).Failed);
    }

    [Fact]
    public async Task NoAnswerAtAll_IsNotKeptAsALook_AndTheButtonSaysItFailed()
    {
        var (checker, settings, github, _) = Create(Release("v0.2.0"));
        github.Unreachable = true;

        Assert.Null(await TriggerAsync(checker));
        Assert.Null(settings.Current.Updates.LastCheckedAt);
        Assert.True((await checker.CheckNowAsync()).Failed);
    }

    [Fact]
    public async Task AnIgnoredVersion_IsNotAnnouncedAgain_ButANewerOneIs_AndTheButtonStillShowsIt()
    {
        var (checker, settings, _, _) = Create(Release("v0.2.0"));
        await checker.DismissAsync("v0.2.0");
        Assert.Equal("v0.2.0", settings.Current.Updates.DismissedVersion);

        Assert.Null(await TriggerAsync(checker));
        Assert.Equal("v0.2.0", (await checker.CheckNowAsync()).Update?.NewVersion);

        var (newer, _, _, _) = Create(Release("v0.3.0"), dismissed: "v0.2.0");
        Assert.Equal("v0.3.0", (await TriggerAsync(newer))?.NewVersion);
    }

    [Fact]
    public async Task WithLocalOnlyOn_ADueCheckStillRuns_AsDoesTheButton()
    {
        var (checker, settings, github, _) = Create(Release("v0.2.0"), localOnly: true);

        Assert.Equal("v0.2.0", (await TriggerAsync(checker))?.NewVersion);
        Assert.Single(github.Requests);
        Assert.Equal(Now, settings.Current.Updates.LastCheckedAt);

        Assert.Equal("v0.2.0", (await checker.CheckNowAsync()).Update?.NewVersion);
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task OnlyOneCheckRunsAtATime_HoweverOftenTheAppIsOpened()
    {
        var (checker, _, github, _) = Create(Release("v0.2.0"));
        github.Gate = new TaskCompletionSource();

        checker.TriggerCheckIfDue();
        var first = checker.Checking;
        checker.TriggerCheckIfDue();
        checker.TriggerCheckIfDue();
        Assert.Same(first, checker.Checking);

        github.Gate.SetResult();
        await first;
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task TheButtonAsksAtOnce_WhateverTheLastLook()
    {
        var (checker, _, github, _) = Create(Release("v0.2.0"), lastChecked: Now - TimeSpan.FromMinutes(1));

        Assert.Equal("v0.2.0", (await checker.CheckNowAsync()).Update?.NewVersion);
        Assert.Single(github.Requests);
    }

    [Theory]
    [InlineData("https://example.com/MouayadYT/Kiri/releases/tag/v0.2.0")]
    [InlineData("http://github.com/MouayadYT/Kiri/releases/tag/v0.2.0")]
    [InlineData("https://github.com/someone-else/Kiri/releases/tag/v0.2.0")]
    [InlineData("javascript:alert(1)")]
    public async Task AReleaseWhosePageIsNotThisProjectsOwn_IsNotOffered(string page)
    {
        var (checker, _, _, _) = Create(Release("v0.2.0", page: page));

        Assert.Null(await TriggerAsync(checker));
        var result = await checker.CheckNowAsync();
        Assert.Null(result.Update);
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("""{"tag_name":"latest","html_url":"https://github.com/MouayadYT/Kiri/releases/tag/latest"}""")]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    public async Task AnAnswerThatCannotBeRead_IsAFailure_NotAnUpdate(string answer)
    {
        var (checker, _, _, _) = Create(answer);

        Assert.Null(await TriggerAsync(checker));
        Assert.True((await checker.CheckNowAsync()).Failed);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData(" V0.1.148-rc.1 ", "0.1.148.0")]
    [InlineData("0.1.148+abc", "0.1.148.0")]
    public void VersionsAreReadWithOrWithoutTheirV_AndWithoutWhatComesAfterADashOrPlus(string text, string expected)
    {
        Assert.True(GitHubUpdateChecker.TryParse(text, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    public void TextThatIsNotAVersion_IsNotRead(string text) => Assert.False(GitHubUpdateChecker.TryParse(text, out _));

    [Fact]
    public void TheIntervalIsAirMirrorsThirtyDays() => Assert.Equal(TimeSpan.FromDays(30), GitHubUpdateChecker.CheckInterval);

    private sealed record SentRequest(string Uri, string Accept, string UserAgent);

    private sealed class GitHub(string answer, HttpStatusCode status) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        public bool Unreachable { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SentRequest(request.RequestUri!.ToString(), request.Headers.Accept.ToString(), request.Headers.UserAgent.ToString()));
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            if (Unreachable)
            {
                throw new HttpRequestException("No route to host.");
            }

            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
