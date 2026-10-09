using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.ModelHosting;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>One well-formed instance of every message, with sample private content where a message carries some.</summary>
internal static class SampleMessages
{
    public const string PrivatePrompt = "PRIVATE-PROMPT-7f3c: summarize my tax letter";
    public const string PrivateInstructions = "PRIVATE-INSTRUCTIONS-91ab";
    public const string PrivateAnswer = "PRIVATE-ANSWER-4d2e";
    public const string PrivateToolArguments = "{\"path\":\"C:\\\\Users\\\\PRIVATE-PATH-0c5a\"}";

    public static readonly byte[] PrivateImage = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x7F, 0x3C];

    public static IReadOnlyList<string> PrivateStrings { get; } =
        [PrivatePrompt, PrivateInstructions, PrivateAnswer, "PRIVATE-PATH-0c5a", Convert.ToBase64String(PrivateImage)];

    public static IReadOnlyList<PromptMessage> Conversation { get; } =
    [
        new(MessageRole.User, PrivatePrompt),
        new(MessageRole.Assistant, string.Empty)
        {
            ToolCalls = [new ToolCall("call-1", "read_file", PrivateToolArguments)],
        },
        new(MessageRole.Tool, PrivateAnswer) { ToolCallId = "call-1" },
    ];

    public static GenerateTextRequest GenerateText { get; } = new("chat-model", PrivateInstructions, Conversation)
    {
        Tools = [new ToolDefinition("read_file", "Reads a file.", "{\"type\":\"object\"}", RiskLevel.ReadOnly)],
        MaxOutputTokens = 256,
    };

    public static GenerateMultimodalRequest GenerateMultimodal { get; } =
        new("vision-model", PrivateInstructions, [new(MessageRole.User, PrivatePrompt)], [PrivateImage]);

    public static IReadOnlyList<ModelHostMessage> All { get; } =
    [
        new HealthRequest(),
        new LoadModelRequest("chat-model")
        {
            Files = new ModelFiles(@"C:\Users\PRIVATE-PATH-0c5a\models\chat.gguf")
            {
                ProjectorPath = @"C:\Users\PRIVATE-PATH-0c5a\models\chat-mmproj.gguf",
                ChatTemplatePath = @"C:\Users\PRIVATE-PATH-0c5a\models\chat.jinja",
                ContextLength = 8192,
            },
        },
        new UnloadModelRequest(),
        GenerateText,
        GenerateMultimodal,
        new CancelGenerationRequest(4),
        new ShutdownRequest(),
        new HealthReport(new Version(1, 2, 3, 4), 4242, TimeSpan.FromSeconds(12.5))
        {
            WorkingSetBytes = 123_456_789,
            LoadedModelId = "chat-model",
            ModelStatus = ModelStatus.Ready,
            Runtime = ModelRuntimeState.MissingSystemComponent,
        },
        new ModelLoadProgress("chat-model", 0.25),
        new ModelLoaded(new ModelInfo("chat-model", 8192) { SupportsToolCalling = true, SupportsVision = true }),
        new ModelUnloaded { ModelId = "chat-model" },
        new ModelStatusReport(3, ModelStatus.Failed) { ModelId = "chat-model", Failure = ModelFailure.LoadFailed },
        new TextDelta(PrivateAnswer),
        new ToolCallGenerated(new ToolCall("call-2", "read_file", PrivateToolArguments)),
        new GenerationEnded(GenerationStopReason.OutputLimit) { PromptTokens = 900, OutputTokens = 256 },
        new ShutdownAccepted(),
        new ModelHostError(ModelHostErrorCode.ModelNotFound),
    ];

    /// <summary>The type names of <see cref="All"/>, as theory data that the test explorer can show one by one.</summary>
    public static TheoryData<string> TypeNames()
    {
        var data = new TheoryData<string>();
        foreach (var message in All)
        {
            data.Add(message.GetType().Name);
        }

        return data;
    }

    public static ModelHostMessage Get(string typeName) => All.Single(message => message.GetType().Name == typeName);
}
