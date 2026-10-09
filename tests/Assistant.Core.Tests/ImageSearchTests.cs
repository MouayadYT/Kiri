using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.ImageSearch;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// Searching the web with a picture (PROJECT_SPEC §3.4, §4.6, §4.9): off in Local Only mode, behind its own permission, never tied to one
/// provider, and never started without the user's say-so for the very picture that is sent.
/// </summary>
public sealed class ImageSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Picture = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];

    private sealed class FakeProvider(bool sample = false, string name = "Example") : IImageSearchService
    {
        public string ProviderName => name;
        public bool IsSample => sample;
        public List<ImageSearchRequest> Requests { get; } = [];
        public Exception? Failure { get; set; }
        public bool Consumes { get; set; } = true;
        public TimeProvider Clock { get; set; } = new TestClock(Now);

        public Task<ImageSearchResults> SearchAsync(ImageSearchRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Consumes)
            {
                request.Consent.Consume(request.Image, Clock.GetUtcNow(), ((IImageSearchService)this).SendsImageOffPc);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(new ImageSearchResults(
                name, sample, [new ImageSearchResult("A page", "Site", sample ? null : new Uri("https://example.com/a"), ReadOnlyMemory<byte>.Empty)]));
        }
    }

    private sealed class FakeConfirmation(bool answer) : IImageSearchConfirmation
    {
        public List<ImageSearchDisclosure> Asked { get; } = [];

        public Task<bool> ConfirmAsync(ImageSearchDisclosure disclosure, CancellationToken cancellationToken = default)
        {
            Asked.Add(disclosure);
            return Task.FromResult(answer);
        }
    }

    private static ImageSearchFlow Flow(
        bool localOnly, bool permission, IImageSearchService? provider, IImageSearchConfirmation? confirmation, TimeProvider? clock = null)
    {
        var settings = new FixedSettings(new AppSettings
        {
            Privacy = new PrivacySettings { LocalOnly = localOnly },
            Permissions = new PermissionSettings { ExternalSearch = permission },
        });
        return new ImageSearchFlow(
            settings, new SettingsPermissionPolicy(settings), provider, confirmation, clock ?? new TestClock(Now),
            NullLogger<ImageSearchFlow>.Instance);
    }

    // ---- Whether it can be done ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, true, ImageSearchBlock.LocalOnly)]
    [InlineData(true, false, true, ImageSearchBlock.LocalOnly)]
    [InlineData(true, true, false, ImageSearchBlock.LocalOnly)]
    [InlineData(false, false, true, ImageSearchBlock.PermissionOff)]
    [InlineData(false, false, false, ImageSearchBlock.PermissionOff)]
    [InlineData(false, true, false, ImageSearchBlock.NoProvider)]
    [InlineData(false, true, true, ImageSearchBlock.None)]
    public void LocalOnlyComesFirst_ThenThePermission_ThenWhetherThereIsAProvider(
        bool localOnly, bool permission, bool hasProvider, ImageSearchBlock expected)
    {
        var availability = ImageSearchFlow.Evaluate(localOnly, permission, hasProvider ? new FakeProvider() : null);

        Assert.Equal(expected, availability.Block);
        Assert.Equal(expected == ImageSearchBlock.None, availability.IsAvailable);
    }

    [Fact]
    public async Task ByDefault_NothingCanBeSearched_BecauseLocalOnlyIsOn()
    {
        var settings = new FixedSettings();
        var flow = new ImageSearchFlow(
            settings, new SettingsPermissionPolicy(settings), new FakeProvider(), new FakeConfirmation(true), new TestClock(Now));

        Assert.True(new AppSettings().Privacy.LocalOnly);
        Assert.False(new AppSettings().Permissions.ExternalSearch);
        Assert.Equal(ImageSearchBlock.LocalOnly, (await flow.GetAvailabilityAsync()).Block);
    }

    [Fact]
    public async Task TheAvailability_FollowsTheSettingsAsTheyAreSavedNow_AndNamesTheProvider()
    {
        var settings = new FixedSettings(new AppSettings { Privacy = new PrivacySettings { LocalOnly = false } });
        var provider = new FakeProvider(name: "Example Search");
        var flow = new ImageSearchFlow(settings, new SettingsPermissionPolicy(settings), provider, new FakeConfirmation(true), new TestClock(Now));

        Assert.Equal(ImageSearchBlock.PermissionOff, (await flow.GetAvailabilityAsync()).Block);

        settings.Current = settings.Current with { Permissions = new PermissionSettings { ExternalSearch = true } };
        var allowed = await flow.GetAvailabilityAsync();
        Assert.True(allowed.IsAvailable);
        Assert.Equal(("Example Search", false), (allowed.ProviderName, allowed.IsSample));

        settings.Current = settings.Current with { Privacy = new PrivacySettings { LocalOnly = true } };
        Assert.Equal(ImageSearchBlock.LocalOnly, (await flow.GetAvailabilityAsync()).Block);
    }

    // ---- A permission set to ask every time (step 119) -----------------------------------------------------------------------------

    private sealed class Prompt(ConfirmationDecision answer) : IPermissionPrompt
    {
        public List<PermissionPromptRequest> Asked { get; } = [];

        public Task<ConfirmationDecision> AskAsync(PermissionPromptRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);
            return Task.FromResult(answer);
        }
    }

    private static (ImageSearchFlow Flow, Prompt Prompt) AskingFlow(
        IImageSearchService? provider, IImageSearchConfirmation? confirmation, ConfirmationDecision answer, bool withGate = true)
    {
        var settings = new FixedSettings(new AppSettings
        {
            Privacy = new PrivacySettings { LocalOnly = false },
            Permissions = new PermissionSettings().WithMode(PermissionCapability.ExternalSearch, PermissionMode.AskEveryTime),
        });
        var policy = new SettingsPermissionPolicy(settings);
        var prompt = new Prompt(answer);
        var gate = new PermissionGate(policy, prompt, NullLogger<PermissionGate>.Instance);
        return (new ImageSearchFlow(settings, policy, provider, confirmation, new TestClock(Now), NullLogger<ImageSearchFlow>.Instance, withGate ? gate : null), prompt);
    }

    [Fact]
    public async Task AskingEachTimeCountsAsOpenSoTheChipIsOffered()
    {
        var (flow, _) = AskingFlow(new FakeProvider(), new FakeConfirmation(true), ConfirmationDecision.Approved);

        Assert.True((await flow.GetAvailabilityAsync()).IsAvailable);
    }

    [Fact]
    public async Task APictureThatWouldBeSentIsAskedAboutOnlyInTheWindowThatShowsItAndNotTwice()
    {
        var provider = new FakeProvider();
        var confirmation = new FakeConfirmation(true);
        var (flow, prompt) = AskingFlow(provider, confirmation, ConfirmationDecision.Declined);

        var outcome = await flow.SearchAsync(Picture);

        Assert.Equal(ImageSearchOutcomeKind.Results, outcome.Kind);
        Assert.Single(confirmation.Asked);
        Assert.Empty(prompt.Asked);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task ASearchThatSendsNothingIsAskedAboutLikeAnyOtherUseAndAYesIsNeeded()
    {
        var sample = new FakeProvider(sample: true);
        var (yes, yesPrompt) = AskingFlow(sample, null, ConfirmationDecision.Approved);
        var (no, noPrompt) = AskingFlow(sample, null, ConfirmationDecision.Declined);
        var (nobody, _) = AskingFlow(sample, null, ConfirmationDecision.Approved, withGate: false);

        var answered = await yes.SearchAsync(Picture);
        var declined = await no.SearchAsync(Picture);
        var unasked = await nobody.SearchAsync(Picture);

        Assert.Equal(ImageSearchOutcomeKind.Results, answered.Kind);
        Assert.Equal(PermissionCapability.ExternalSearch, Assert.Single(yesPrompt.Asked).Capability);
        Assert.Equal(ImageSearchOutcomeKind.Declined, declined.Kind);
        Assert.Single(noPrompt.Asked);
        Assert.Equal(ImageSearchOutcomeKind.Declined, unasked.Kind);
        Assert.Single(sample.Requests);
    }

    // ---- The user's say-so ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InLocalOnlyMode_NothingIsSent_AndTheUserIsNotEvenAsked()
    {
        var provider = new FakeProvider();
        var confirmation = new FakeConfirmation(true);

        var outcome = await Flow(localOnly: true, permission: true, provider, confirmation).SearchAsync(Picture);

        Assert.Equal((ImageSearchOutcomeKind.Blocked, ImageSearchBlock.LocalOnly), (outcome.Kind, outcome.Block));
        Assert.Empty(provider.Requests);
        Assert.Empty(confirmation.Asked);
    }

    [Fact]
    public async Task WithThePermissionOff_NothingIsSent()
    {
        var provider = new FakeProvider();

        var outcome = await Flow(localOnly: false, permission: false, provider, new FakeConfirmation(true)).SearchAsync(Picture);

        Assert.Equal(ImageSearchBlock.PermissionOff, outcome.Block);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task APictureThatLeavesThePc_IsSentOnlyWhenTheUserSaysYes_ForThatPicture()
    {
        var provider = new FakeProvider();
        var confirmation = new FakeConfirmation(true);

        var outcome = await Flow(localOnly: false, permission: true, provider, confirmation).SearchAsync(Picture);

        Assert.Equal(ImageSearchOutcomeKind.Results, outcome.Kind);
        var disclosure = Assert.Single(confirmation.Asked);
        Assert.Equal("Example", disclosure.ProviderName);
        Assert.Equal(Picture, disclosure.Image.ToArray());
        var request = Assert.Single(provider.Requests);
        Assert.Equal(Picture, request.Image.ToArray());
        Assert.True(request.Consent.UserConfirmed);
        Assert.Single(outcome.Results!.Results);
    }

    [Fact]
    public async Task WhenTheUserSaysNo_OrNobodyCanAsk_NothingIsSent()
    {
        var provider = new FakeProvider();
        var declined = await Flow(false, true, provider, new FakeConfirmation(false)).SearchAsync(Picture);
        var unasked = await Flow(false, true, provider, confirmation: null).SearchAsync(Picture);

        Assert.Equal(ImageSearchOutcomeKind.Declined, declined.Kind);
        Assert.Equal(ImageSearchOutcomeKind.Declined, unasked.Kind);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ASampleProvider_SendsNothing_SoItIsNotAsked_ButStillBehindLocalOnlyAndThePermission()
    {
        var provider = new FakeProvider(sample: true);
        var confirmation = new FakeConfirmation(false);

        var blocked = await Flow(true, true, provider, confirmation).SearchAsync(Picture);
        var outcome = await Flow(false, true, provider, confirmation).SearchAsync(Picture);

        Assert.Equal(ImageSearchBlock.LocalOnly, blocked.Block);
        Assert.Equal(ImageSearchOutcomeKind.Results, outcome.Kind);
        Assert.True(outcome.Results!.IsSample);
        Assert.Empty(confirmation.Asked);
        Assert.False(Assert.Single(provider.Requests).Consent.UserConfirmed);
    }

    [Fact]
    public async Task AProviderThatFails_IsAFailedOutcome_AndAnythingElseThatGoesWrongToo()
    {
        var provider = new FakeProvider { Failure = new ImageSearchException() };

        var outcome = await Flow(false, true, provider, new FakeConfirmation(true)).SearchAsync(Picture);

        Assert.Equal(ImageSearchOutcomeKind.Failed, outcome.Kind);
        Assert.Null(outcome.Results);
        await Assert.ThrowsAsync<ArgumentException>(
            () => Flow(false, true, provider, new FakeConfirmation(true)).SearchAsync(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public async Task CancellingIsNotAFailure()
    {
        var provider = new FakeProvider { Failure = new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Flow(false, true, provider, new FakeConfirmation(true)).SearchAsync(Picture));
    }

    // ---- The consent itself -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASaySo_IsForOnePicture_OneSearch_AndAShortTime()
    {
        var clock = new TestClock(Now);
        var provider = new FakeProvider { Consumes = false };
        await Flow(false, true, provider, new FakeConfirmation(true), clock).SearchAsync(Picture);
        var consent = provider.Requests.Single().Consent;

        // Another picture is not the one the user said yes to.
        Assert.Throws<ImageSearchNotConfirmedException>(() => consent.Consume(new byte[] { 9, 9, 9 }, Now, sendsImageOffPc: true));

        // Once spent, it is spent: for the right picture too.
        Assert.Throws<ImageSearchNotConfirmedException>(() => consent.Consume(Picture, Now, sendsImageOffPc: true));

        await Flow(false, true, provider, new FakeConfirmation(true), clock).SearchAsync(Picture);
        var fresh = provider.Requests.Last().Consent;
        Assert.Throws<ImageSearchNotConfirmedException>(() => fresh.Consume(Picture, Now + ImageSearchConsent.Lifetime + TimeSpan.FromSeconds(1), true));

        await Flow(false, true, provider, new FakeConfirmation(true), clock).SearchAsync(Picture);
        var third = provider.Requests.Last().Consent;
        third.Consume(Picture, Now + TimeSpan.FromSeconds(30), sendsImageOffPc: true);
    }

    [Fact]
    public async Task ASaySoThatWasNeverGiven_CannotBeUsedToSendAPictureOffThePc()
    {
        var provider = new FakeProvider(sample: true) { Consumes = false };
        await Flow(false, true, provider, null).SearchAsync(Picture);
        var unconfirmed = provider.Requests.Single().Consent;

        Assert.False(unconfirmed.UserConfirmed);
        Assert.Throws<ImageSearchNotConfirmedException>(() => unconfirmed.Consume(Picture, Now, sendsImageOffPc: true));
    }

    [Fact]
    public async Task AProviderThatDoesNotConsume_IsNotTrusted_ButTheFlowStillDoesNotSendWithoutAsking()
    {
        // The contract makes the provider consume the say-so; what the flow guarantees on its own is that it asks first.
        var provider = new FakeProvider { Consumes = false };
        var confirmation = new FakeConfirmation(false);

        await Flow(false, true, provider, confirmation).SearchAsync(Picture);

        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task WhatWasFoundOrAsked_NeverReachesAToString()
    {
        var provider = new FakeProvider { Consumes = false };
        await Flow(false, true, provider, new FakeConfirmation(true)).SearchAsync(Picture);
        var request = provider.Requests.Single();
        var result = new ImageSearchResult("PRIVATE TITLE", "Site", new Uri("https://example.com/private"), new byte[] { 1, 2, 3 });
        var results = new ImageSearchResults("Example", false, [result]);

        Assert.DoesNotContain("PRIVATE", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private", results.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("137", new ImageSearchDisclosure("Example", Picture).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("137", request.ToString(), StringComparison.Ordinal);
        Assert.Contains("ImageBytes = 8", request.ToString(), StringComparison.Ordinal);
    }
}
