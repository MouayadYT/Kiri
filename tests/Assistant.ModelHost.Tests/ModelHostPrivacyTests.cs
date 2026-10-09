using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Server;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>Prompts, images and output cross the pipe, but never reach a log (PROJECT_SPEC §3.3).</summary>
public sealed class ModelHostPrivacyTests
{
    [Fact]
    public async Task ServingPrivateRequests_LogsNoContent()
    {
        using var capture = new CapturingLoggerProvider();
        using var loggers = capture.CreateFactory();
        var (hostStream, appStream) = await TestPipes.ConnectAsync();
        var session = new ModelHostSession(
            TestHandlers.Create(new NullModelController()), loggers.CreateLogger<ModelHostSession>());
        var serving = session.RunAsync(hostStream, TestPipes.Timeout());

        await using (var client = new ModelHostClient(appStream, loggers.CreateLogger<ModelHostClient>()))
        {
            var app = new ModelHostChannel(appStream);
            await app.SendAsync(1_000, SampleMessages.GenerateText, TestPipes.Timeout());
            await app.SendAsync(1_001, SampleMessages.GenerateMultimodal, TestPipes.Timeout());
            await app.SendAsync(1_002, new TextDelta(SampleMessages.PrivateAnswer), TestPipes.Timeout());
            await client.PingAsync(TestPipes.Timeout());
            await client.ShutdownAsync(TestPipes.Timeout());
        }

        await serving.WaitAsync(TestPipes.Timeout());

        Assert.NotEmpty(capture.Entries);
        Assert.Contains("GenerateTextRequest", capture.AllText, StringComparison.Ordinal);
        AssertNoPrivateContent(capture.AllText);
    }

    [Fact]
    public async Task StartingAndStoppingTheHost_LogsNoContentOrPaths()
    {
        using var capture = new CapturingLoggerProvider();
        using var loggers = capture.CreateFactory(privacyFilter: true);

        var host = await ModelHostProcess.StartAsync(
            ModelHostLaunchOptions.InDirectory(AppContext.BaseDirectory), loggers, TestPipes.Timeout());
        await host.DisposeAsync();
        await Assert.ThrowsAsync<ModelHostException>(() => ModelHostProcess.StartAsync(
            ModelHostLaunchOptions.InDirectory(Path.Combine(AppContext.BaseDirectory, "PRIVATE-PATH-0c5a")),
            loggers,
            TestPipes.Timeout()));

        Assert.Contains("answered the ping", capture.AllText, StringComparison.Ordinal);
        Assert.Contains("could not be started", capture.AllText, StringComparison.Ordinal);
        AssertNoPrivateContent(capture.AllText);
        Assert.DoesNotContain(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), capture.AllText, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoPrivateContent(string logs)
    {
        foreach (var secret in SampleMessages.PrivateStrings)
        {
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }
    }
}
