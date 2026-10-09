using System.Runtime.CompilerServices;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Generation;
using Assistant.ModelHost.Models;
using Assistant.ModelHost.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.ModelHost.Tests;

/// <summary>Request handlers put together as the host puts them together, for sessions in tests.</summary>
internal static class TestHandlers
{
    /// <summary>
    /// A handler over <paramref name="controller"/>, whose generator asks <paramref name="engine"/>, or an engine that
    /// cannot be reached.
    /// </summary>
    public static ModelHostRequestHandler Create(IModelController controller, IChatEngine? engine = null) =>
        new(TimeProvider.System, TestRuntimes.Bundled, controller, CreateGenerator(controller, engine));

    public static TextGenerator CreateGenerator(IModelController controller, IChatEngine? engine = null) =>
        new(controller, engine ?? new UnreachableChatEngine(), TimeProvider.System, NullLogger<TextGenerator>.Instance);
}

/// <summary>An engine that is never there.</summary>
internal sealed class UnreachableChatEngine : IChatEngine
{
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
        GenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new GenerationException(ModelHostErrorCode.GenerationFailed);
#pragma warning disable CS0162 // An iterator needs a yield, even one it never reaches.
        yield break;
#pragma warning restore CS0162
    }
}
