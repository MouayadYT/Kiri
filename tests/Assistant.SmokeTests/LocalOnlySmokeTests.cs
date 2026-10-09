using Assistant.Core.Contracts;
using Assistant.Core.ImageSearch;
using Assistant.Core.Settings;
using Assistant.ModelHost.FakeEngine;
using Assistant.SmokeTests.Support;
using Assistant.UI.Settings;
using Assistant.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Assistant.SmokeTests;

/// <summary>
/// Checklist 13: Local Only mode (PROJECT_SPEC §3.4) is on from the first start, and while it is on the two things that could send something off this PC
/// (searching the web with a picture, and looking for an integration for an app) are not done. Each is also behind a second lock, and a picture is sent only
/// after the user has been shown it. The providers here write down what they are asked; a real one would be a network request, which the suite's network watch
/// would fail on.
/// </summary>
public sealed class LocalOnlySmokeTests
{
    // An app the Assistant has no server for, so the finder is what would be asked; and one it knows (Microsoft To Do), for which it offers to connect.
    private const string AddMilk = "Add 'buy milk' to Asana";
    private const string AddMilkToKnownApp = "Add 'buy milk' to Microsoft To Do";

    private static readonly byte[] Picture = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6, 7, 8];

    [Fact]
    public Task LocalOnlyIsOnFromTheFirstStart_AndSettingsShowsIt() => Smoke.RunAsync(async app =>
    {
        Assert.True((await app.Get<ISettingsService>().LoadAsync()).Privacy.LocalOnly);
        Assert.True(app.Get<SettingsViewModel>().Privacy.LocalOnly);
    });

    [Fact]
    public Task APictureIsNotSearchedWhileLocalOnlyIsOn_NorWithoutThePermission_AndIsSentOnlyAfterTheUserSaysYes() => Smoke.RunAsync(async app =>
    {
        var provider = (RecordingProvider)app.Get<IImageSearchService>();
        var user = (RecordingConfirmation)app.Get<IImageSearchConfirmation>();
        var flow = app.Get<ImageSearchFlow>();

        // Local Only on (the default) with the permission on: nothing is asked and nothing is sent.
        await app.ChangeSettingsAsync(settings => settings with { Permissions = settings.Permissions with { ExternalSearch = true } });
        Assert.Equal(ImageSearchBlock.LocalOnly, (await flow.GetAvailabilityAsync()).Block);
        var blocked = await flow.SearchAsync(Picture);
        Assert.Equal(ImageSearchOutcomeKind.Blocked, blocked.Kind);
        Assert.Equal(ImageSearchBlock.LocalOnly, blocked.Block);

        // Local Only off but the permission off: still nothing.
        await app.ChangeSettingsAsync(settings => settings with
        {
            Privacy = settings.Privacy with { LocalOnly = false },
            Permissions = settings.Permissions with { ExternalSearch = false },
        });
        var noPermission = await flow.SearchAsync(Picture);
        Assert.Equal((ImageSearchOutcomeKind.Blocked, ImageSearchBlock.PermissionOff), (noPermission.Kind, noPermission.Block));
        Assert.Equal((0, 0), (provider.Searches, user.Shown));

        // Both locks open: the user is shown the picture and the provider's name, and a no sends nothing.
        await app.ChangeSettingsAsync(settings => settings with { Permissions = settings.Permissions with { ExternalSearch = true } });
        user.Answer = false;
        var declined = await flow.SearchAsync(Picture);
        Assert.Equal(ImageSearchOutcomeKind.Declined, declined.Kind);
        Assert.Equal((1, 0), (user.Shown, provider.Searches));
        Assert.Equal("Smoke search", user.ShownProvider);

        // Only a yes sends it, once, with a consent that is for this picture.
        user.Answer = true;
        var found = await flow.SearchAsync(Picture);
        Assert.Equal(ImageSearchOutcomeKind.Results, found.Kind);
        Assert.Equal((2, 1), (user.Shown, provider.Searches));
        Assert.Equal(Picture, provider.LastImage);
    }, services =>
    {
        services.AddSingleton<IImageSearchService>(new RecordingProvider());
        services.AddSingleton<IImageSearchConfirmation>(new RecordingConfirmation());
    });

    [Fact]
    public Task ARequestForAnAppTheAssistantDoesNotHave_IsNotLookedUpOnline_WhileEitherLockIsClosed() => Smoke.RunInFolderAsync(async scratch =>
    {
        // The real finder: it checks both locks before anything is sent, and the suite's network watch fails the check if anything is.
        await using var model = FakeLocalModel.Create(scratch, new FakeEngineScenario { Chat = new FakeChatReply { Pieces = ["The model was asked."] } });
        await using var app = await SmokeApp.StartAsync(scratch, model.Replace);
        await app.ChangeSettingsAsync(model.Use);
        var conversation = app.Get<ConversationViewModel>();

        async Task<string> AskAsync()
        {
            conversation.StartNew(AddMilk);
            await Wait.UntilAsync(
                () => conversation.Messages.Count == 2 && conversation.Messages[1].Status == MessageStatus.Complete, "The request was not answered.");
            return conversation.Messages[1].Text;
        }

        // Local Only on, the permission on: the Assistant says what it would need and that it did not look, and the model was not asked.
        await app.ChangeSettingsAsync(settings => settings with { Permissions = settings.Permissions with { ExternalSearch = true } });
        var localOnly = await AskAsync();
        Assert.StartsWith("I can't create a task in Asana yet: no Asana integration is installed.", localOnly, StringComparison.Ordinal);
        Assert.True(localOnly.Contains("Local Only", StringComparison.Ordinal), localOnly);

        // Local Only off, the permission off: the other lock holds, and it says which.
        await app.ChangeSettingsAsync(settings => settings with
        {
            Privacy = settings.Privacy with { LocalOnly = false },
            Permissions = settings.Permissions with { ExternalSearch = false },
        });
        var noPermission = await AskAsync();
        Assert.StartsWith("I can't create a task in Asana yet: no Asana integration is installed.", noPermission, StringComparison.Ordinal);
        Assert.False(noPermission.Contains("Local Only", StringComparison.Ordinal), noPermission);
        Assert.True(noPermission.Contains("permission", StringComparison.OrdinalIgnoreCase) || noPermission.Contains("Permissions", StringComparison.Ordinal), noPermission);

        Assert.Equal(0, model.EngineLaunches);
    });

    [Fact]
    public Task ARequestForAnAppTheAssistantKnows_IsOfferedAConnectionOnlyWhileLocalOnlyIsOff_AndNothingIsSentBeforeTheUserClicks() => Smoke.RunInFolderAsync(async scratch =>
    {
        await using var model = FakeLocalModel.Create(scratch, new FakeEngineScenario { Chat = new FakeChatReply { Pieces = ["The model was asked."] } });
        await using var app = await SmokeApp.StartAsync(scratch, model.Replace);
        await app.ChangeSettingsAsync(model.Use);
        var conversation = app.Get<ConversationViewModel>();

        async Task<string> AskAsync()
        {
            conversation.StartNew(AddMilkToKnownApp);
            await Wait.UntilAsync(
                () => conversation.Messages.Count == 2 && conversation.Messages[1].Status == MessageStatus.Complete, "The request was not answered.");
            return conversation.Messages[1].Text;
        }

        // Local Only on: Microsoft To Do is reached over the internet, so it is not even offered.
        var localOnly = await AskAsync();
        Assert.StartsWith("I can't create a task in Microsoft To Do:", localOnly, StringComparison.Ordinal);
        Assert.Contains("Local Only", localOnly, StringComparison.Ordinal);

        // Local Only off: the connection is offered, and the suite's network watch fails the check if anything was sent before a click.
        await app.ChangeSettingsAsync(settings => settings with { Privacy = settings.Privacy with { LocalOnly = false } });
        var offered = await AskAsync();
        Assert.StartsWith("I can't create a task in Microsoft To Do yet: Microsoft To Do isn't connected.", offered, StringComparison.Ordinal);
        Assert.Contains("I never see your password", offered, StringComparison.Ordinal);

        Assert.Equal(0, model.EngineLaunches);
    });

    private sealed class RecordingProvider : IImageSearchService
    {
        public string ProviderName => "Smoke search";

        public bool IsSample => false;

        public int Searches { get; private set; }

        public byte[]? LastImage { get; private set; }

        public Task<ImageSearchResults> SearchAsync(ImageSearchRequest request, CancellationToken cancellationToken = default)
        {
            Searches++;
            LastImage = request.Image.ToArray();
            return Task.FromResult(new ImageSearchResults(ProviderName, false, []));
        }
    }

    private sealed class RecordingConfirmation : IImageSearchConfirmation
    {
        public bool Answer { get; set; }

        public int Shown { get; private set; }

        public string? ShownProvider { get; private set; }

        public Task<bool> ConfirmAsync(ImageSearchDisclosure disclosure, CancellationToken cancellationToken = default)
        {
            Shown++;
            ShownProvider = disclosure.ProviderName;
            return Task.FromResult(Answer);
        }
    }
}
