using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Settings;

namespace Assistant.Tools.Tests;

/// <summary>A file request service that answers as the test says, and records what it was asked.</summary>
internal sealed class FakeFileRequests : IFileRequestService
{
    public FileRequestResult Result { get; set; } = new(FileRequestStatus.NothingFound);

    public List<string> Asked { get; } = [];

    public bool IsFileRequest(string request) => false;

    public Task<FileRequestResult> FindAsync(string request, CancellationToken cancellationToken = default)
    {
        Asked.Add(request);
        return Task.FromResult(Result);
    }

    public Task<IReadOnlyList<SearchResultItem>> LookUpAsync(string typed, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SearchResultItem>>([]);
}

internal sealed class FakePermissions(bool allowed = true) : IPermissionPolicy
{
    public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PermissionDecision(capability, allowed ? PermissionDecisionReason.Granted : PermissionDecisionReason.TurnedOff));
}

internal sealed class FakeModels(ModelInfo? model = null) : IModelService
{
    public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) => Task.FromResult(model);

    public IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class FixedSettings : ISettingsService
{
    public AppSettings Current { get; set; } = new();

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        return Task.CompletedTask;
    }
}

/// <summary>A tool the test makes: what it returns, or how it goes wrong.</summary>
internal sealed class FakeTool(string name, RiskLevel risk = RiskLevel.ReadOnly, string schema = """{"type":"object"}""") : Assistant.Tools.ITool
{
    public Assistant.Core.Domain.ToolDefinition Definition { get; } = new(name, "A tool.", schema, risk);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public Func<System.Text.Json.JsonElement, ToolContext, CancellationToken, Task<string>> Run { get; init; } =
        (_, _, _) => Task.FromResult("{}");

    public int Runs { get; private set; }

    public async Task<ToolResult> RunAsync(
        ToolCall call, System.Text.Json.JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        Runs++;
        return new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, await Run(arguments, context, cancellationToken));
    }
}

internal sealed class FakeConfirmation(bool approve) : IPermissionService
{
    public int Asked { get; private set; }

    /// <summary>What the user was shown, one question after another.</summary>
    public List<ToolConfirmation> Shown { get; } = [];

    /// <summary>The calls the user was asked about, as they would be made.</summary>
    public List<ToolCall> Calls { get; } = [];

    /// <summary>How the question ends, when it is not just yes or no.</summary>
    public ConfirmationDecision? Decision { get; init; }

    public Task<ConfirmationDecision> ConfirmToolCallAsync(
        ToolDefinition tool, ToolCall call, ToolContext context, ToolConfirmation confirmation, CancellationToken cancellationToken = default)
    {
        Asked++;
        Shown.Add(confirmation);
        Calls.Add(call);
        return Task.FromResult(Decision ?? (approve ? ConfirmationDecision.Approved : ConfirmationDecision.Declined));
    }
}
