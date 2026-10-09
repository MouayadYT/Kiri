using System.Runtime.InteropServices;
using Assistant.Core.Contracts;
using Assistant.Windows.Credentials;
using Assistant.Windows.Interop;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// The secret store against the real Windows Credential Manager, under a target prefix of the test's own so that the
/// app's credentials are never touched. Each test removes what it stored.
/// </summary>
public sealed class WindowsCredentialSecretStoreTests : IDisposable
{
    private readonly string _prefix = $"Assistant.Tests.{Guid.NewGuid():N}";
    private readonly WindowsCredentialSecretStore _store;
    private readonly List<string> _names = [];

    public WindowsCredentialSecretStoreTests() => _store = new WindowsCredentialSecretStore(_prefix);

    public void Dispose()
    {
        foreach (var name in _names)
        {
            _store.DeleteAsync(name).GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task ASecretComesBackAsItWasStored()
    {
        var name = Name("token");

        await _store.SetAsync(name, "s3cret-value");

        Assert.Equal("s3cret-value", await _store.GetAsync(name));
    }

    [Fact]
    public async Task StoringAgainReplacesTheSecret()
    {
        var name = Name("token");
        await _store.SetAsync(name, "first");

        await _store.SetAsync(name, "second");

        Assert.Equal("second", await _store.GetAsync(name));
    }

    [Fact]
    public async Task ASecretThatWasNeverStoredIsNull() => Assert.Null(await _store.GetAsync(Name("absent")));

    [Fact]
    public async Task DeletingRemovesTheSecretAndSaysWhetherThereWasOne()
    {
        var name = Name("token");
        await _store.SetAsync(name, "value");

        Assert.True(await _store.DeleteAsync(name));
        Assert.Null(await _store.GetAsync(name));
        Assert.False(await _store.DeleteAsync(name));
    }

    [Fact]
    public async Task SecretsKeptUnderDifferentNamesAreKeptApart()
    {
        var first = Name("one");
        var second = Name("two");
        await _store.SetAsync(first, "1");
        await _store.SetAsync(second, "2");

        await _store.DeleteAsync(first);

        Assert.Null(await _store.GetAsync(first));
        Assert.Equal("2", await _store.GetAsync(second));
    }

    [Fact]
    public async Task ATextWithAnyCharacterAndTheLongestLengthSurvives()
    {
        var name = Name("long");
        var text = "é😀日本語 \"quoted\" \\ \n" + new string('x', SecretNames.MaxSecretLength);
        text = text[..SecretNames.MaxSecretLength];

        await _store.SetAsync(name, text);

        Assert.Equal(text, await _store.GetAsync(name));
    }

    [Fact]
    public async Task ItIsKeptForThisPcAsAGenericCredentialUnderThePrefixedName()
    {
        var name = Name("token");
        await _store.SetAsync(name, "value");

        Assert.True(Advapi32.CredRead($"{_prefix}/{name}", Advapi32.CredTypeGeneric, 0, out var memory));
        try
        {
            var credential = Marshal.PtrToStructure<Advapi32.Credential>(memory);
            Assert.Equal(Advapi32.CredPersistLocalMachine, credential.Persist);
            Assert.Equal(Advapi32.CredTypeGeneric, credential.Type);
        }
        finally
        {
            Advapi32.CredFree(memory);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("a/b")]
    public async Task ABadNameIsRefusedBeforeWindowsIsAsked(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SetAsync(name, "value"));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetAsync(name));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync(name));
    }

    [Fact]
    public async Task AnEmptyOrTooLongSecretIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SetAsync("token", string.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SetAsync("token", new string('x', SecretNames.MaxSecretLength + 1)));
        Assert.Null(await _store.GetAsync("token"));
    }

    [Fact]
    public async Task ACancelledCallDoesNothing()
    {
        var name = Name("token");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.SetAsync(name, "value", cancelled.Token));

        Assert.Null(await _store.GetAsync(name));
    }

    [Fact]
    public void AFailureNamesNeitherTheSecretNorTheName()
    {
        var failure = new SecretStoreException("Windows could not store the secret.", 5);

        Assert.Equal(5, failure.ErrorCode);
        Assert.DoesNotContain("token", failure.Message, StringComparison.Ordinal);
    }

    private string Name(string name)
    {
        _names.Add(name);
        return name;
    }
}
