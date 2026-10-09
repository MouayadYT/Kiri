using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Core.Domain;
using Xunit;

namespace Assistant.Documents.Tests;

/// <summary>Reading a document behind the Files permission, in the service itself (PROJECT_SPEC §4.7, §4.9, step 119).</summary>
public sealed class PermissionCheckedDocumentContextServiceTests
{
    private sealed class Policy(bool allowed) : IPermissionPolicy
    {
        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PermissionDecision(
                capability, allowed && capability == PermissionCapability.Files ? PermissionDecisionReason.Granted : PermissionDecisionReason.TurnedOff));
    }

    private sealed class Inner : IDocumentContextService
    {
        public List<string> Opened { get; } = [];

        public Task<DocumentContextResult> GetContextAsync(
            string filePath, string question, DocumentContextOptions? options = null, CancellationToken cancellationToken = default)
        {
            Opened.Add(filePath);
            return Task.FromResult(DocumentContextResult.Failed(DocumentReadStatus.NoText));
        }
    }

    [Fact]
    public async Task NoFileIsOpenedWhileFilesIsNotAllowed()
    {
        var inner = new Inner();

        var result = await new PermissionCheckedDocumentContextService(inner, new Policy(false)).GetContextAsync(@"C:\Users\me\Documents\notes.txt", "what does it say?");

        Assert.Equal(DocumentReadStatus.NotAllowed, result.Status);
        Assert.Equal(string.Empty, result.Text);
        Assert.Empty(inner.Opened);
    }

    [Fact]
    public async Task WhenFilesIsAllowedTheDocumentIsReadAsItWas()
    {
        var inner = new Inner();

        var result = await new PermissionCheckedDocumentContextService(inner, new Policy(true)).GetContextAsync(@"C:\Users\me\Documents\notes.txt", "what does it say?");

        Assert.Equal(DocumentReadStatus.NoText, result.Status);
        Assert.Equal([@"C:\Users\me\Documents\notes.txt"], inner.Opened);
    }
}
