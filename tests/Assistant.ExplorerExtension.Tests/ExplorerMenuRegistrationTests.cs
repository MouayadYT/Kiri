using Assistant.Core.Contracts;
using Assistant.ExplorerExtension.Registration;
using Microsoft.Win32;
using Xunit;

namespace Assistant.ExplorerExtension.Tests;

/// <summary>
/// The Ask Assistant menu entry in the user's registry (PROJECT_SPEC §4.4), written under a key of the test's own in the
/// current user's hive, which stands for <c>Software\Classes</c> and is deleted afterwards; the real one is never touched.
/// </summary>
public sealed class ExplorerMenuRegistrationTests : IDisposable
{
    private const string Executable = @"C:\Program Files\Assistant\Assistant.ExplorerExtension.exe";
    private readonly string _rootPath = $@"Software\Assistant.Tests\{Guid.NewGuid():N}";
    private readonly RegistryKey _classes;

    public ExplorerMenuRegistrationTests() => _classes = Registry.CurrentUser.CreateSubKey(_rootPath, writable: true);

    public void Dispose()
    {
        _classes.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\Assistant.Tests", writable: true);
        if (parent is { SubKeyCount: 0, ValueCount: 0 })
        {
            Registry.CurrentUser.DeleteSubKey(@"Software\Assistant.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void EverySupportedTypeGetsAnAskAssistantVerbThatRunsTheEntryPointWithTheFile()
    {
        var registration = new ExplorerMenuRegistration(_classes);
        registration.Register(Executable, ExplorerFileTypes.Extensions);

        Assert.Equal(
            ExplorerFileTypes.Extensions.Order(StringComparer.Ordinal),
            registration.RegisteredExtensions().Order(StringComparer.Ordinal));
        foreach (var extension in ExplorerFileTypes.Extensions)
        {
            using var verb = _classes.OpenSubKey($@"SystemFileAssociations\{extension}\shell\Assistant.AskAssistant");
            Assert.NotNull(verb);
            Assert.Equal("Ask Assistant", verb.GetValue(""));

            // Shown without Shift, at the top of the menu, with the entry point's icon, and for any number of files: without the
            // Player model File Explorer leaves a verb out of the menu when more than 15 files are selected.
            Assert.Null(verb.GetValue("Extended"));
            Assert.Equal("Player", verb.GetValue("MultiSelectModel"));
            Assert.Equal("Top", verb.GetValue("Position"));
            Assert.Equal("\"C:\\Program Files\\Assistant\\Assistant.ExplorerExtension.exe\",0", verb.GetValue("Icon"));
            Assert.Equal(
                "\"C:\\Program Files\\Assistant\\Assistant.ExplorerExtension.exe\" ask \"%1\"",
                registration.CommandFor(extension));
        }

        Assert.Contains(".pdf", ExplorerFileTypes.Extensions);
        Assert.Contains(".png", ExplorerFileTypes.Extensions);
        Assert.DoesNotContain(".exe", registration.RegisteredExtensions());
    }

    [Fact]
    public void RegisteringAgainPointsAtTheNewEntryPoint_AndDropsTypesNoLongerSupported()
    {
        var registration = new ExplorerMenuRegistration(_classes);
        registration.Register(Executable, [".pdf", ".old", ".png"]);
        registration.Register(@"D:\Moved\Assistant.ExplorerExtension.exe", [".PDF", ".png"]);

        Assert.Equal([".pdf", ".png"], registration.RegisteredExtensions().Order(StringComparer.Ordinal));
        Assert.Equal("\"D:\\Moved\\Assistant.ExplorerExtension.exe\" ask \"%1\"", registration.CommandFor(".pdf"));
        using var associations = _classes.OpenSubKey("SystemFileAssociations");
        Assert.DoesNotContain(".old", associations!.GetSubKeyNames());
    }

    [Fact]
    public void UnregisteringRemovesOnlyTheEntry_AndTheKeysThatHeldNothingElse()
    {
        // Another app's verb on .pdf, and another key on .png, are already there.
        using (var other = _classes.CreateSubKey(@"SystemFileAssociations\.pdf\shell\OtherApp.Print\command"))
        {
            other.SetValue("", "other.exe \"%1\"");
        }

        using (var values = _classes.CreateSubKey(@"SystemFileAssociations\.png\OpenWithList\viewer.exe"))
        {
        }

        var registration = new ExplorerMenuRegistration(_classes);
        registration.Register(Executable, [".pdf", ".png", ".md"]);
        registration.Unregister();

        Assert.Empty(registration.RegisteredExtensions());
        using var associations = _classes.OpenSubKey("SystemFileAssociations")!;
        Assert.NotNull(associations.OpenSubKey(@".pdf\shell\OtherApp.Print\command"));
        Assert.NotNull(associations.OpenSubKey(@".png\OpenWithList\viewer.exe"));
        Assert.Null(associations.OpenSubKey(@".png\shell"));
        Assert.Null(associations.OpenSubKey(".md"));

        // Again, with nothing there, does nothing.
        registration.Unregister();
        new ExplorerMenuRegistration(_classes).Unregister();
    }

    [Theory]
    [InlineData("Assistant.ExplorerExtension.exe")]
    [InlineData("")]
    [InlineData("C:\\a\"b.exe")]
    public void TheEntryPointMustBeAFullPath(string executable) =>
        Assert.Throws<ArgumentException>(() => new ExplorerMenuRegistration(_classes).Register(executable, [".pdf"]));

    [Theory]
    [InlineData("pdf")]
    [InlineData(@".pdf\shell")]
    [InlineData("")]
    public void ExtensionsMustBeExtensions(string extension) =>
        Assert.Throws<ArgumentException>(() => new ExplorerMenuRegistration(_classes).Register(Executable, [extension]));

    [Fact]
    public void IconFollowsTheMenusThemeWhenTheMarksShipBesideTheEntryPoint()
    {
        var folder = Directory.CreateTempSubdirectory("assistant-icons");
        try
        {
            var executable = Path.Combine(folder.FullName, "Assistant.ExplorerExtension.exe");
            Assert.Equal($"\"{executable}\",0", ExplorerMenuRegistration.IconFor(executable, lightTheme: true));

            File.WriteAllBytes(Path.Combine(folder.FullName, ExplorerMenuRegistration.DarkMenuIconName), [0]);
            File.WriteAllBytes(Path.Combine(folder.FullName, ExplorerMenuRegistration.LightMenuIconName), [0]);

            Assert.Equal($"\"{Path.Combine(folder.FullName, "AskAssistant.dark.ico")}\"", ExplorerMenuRegistration.IconFor(executable, lightTheme: false));
            Assert.Equal($"\"{Path.Combine(folder.FullName, "AskAssistant.light.ico")}\"", ExplorerMenuRegistration.IconFor(executable, lightTheme: true));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
