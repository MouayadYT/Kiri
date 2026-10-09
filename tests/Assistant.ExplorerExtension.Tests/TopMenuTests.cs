using System.Xml.Linq;
using Assistant.Core.Contracts;
using Assistant.ExplorerExtension.Registration;
using Assistant.ExplorerExtension.TopMenu;
using Xunit;

namespace Assistant.ExplorerExtension.Tests;

/// <summary>
/// Ask Assistant in File Explorer's Windows 11 first menu (PROJECT_SPEC §4.4): the sparse package's manifest, and the command File Explorer asks about a selection.
/// Registering the package needs Developer Mode, so what is checked here is everything up to that: that the manifest is what Windows reads, and what the command answers.
/// </summary>
public sealed class TopMenuTests
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Desktop4 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4";
    private static readonly XNamespace Desktop5 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5";
    private static readonly XNamespace Com = "http://schemas.microsoft.com/appx/manifest/com/windows10";
    private static readonly XNamespace Uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";

    private static XDocument Manifest(string executable = "Assistant.ExplorerExtension.exe") =>
        XDocument.Parse(TopMenuPackage.Manifest(executable, ExplorerFileTypes.Extensions));

    [Fact]
    public void TheManifestNamesThePackageTheExecutableAndExternalFiles()
    {
        var package = Manifest().Root!;

        Assert.Equal(TopMenuPackage.PackageName, package.Element(Foundation + "Identity")!.Attribute("Name")!.Value);
        Assert.Equal("true", package.Element(Foundation + "Properties")!.Element(Uap10 + "AllowExternalContent")!.Value);
        var application = package.Element(Foundation + "Applications")!.Element(Foundation + "Application")!;
        Assert.Equal("Assistant.ExplorerExtension.exe", application.Attribute("Executable")!.Value);
        Assert.Equal("win32App", application.Attribute(Uap10 + "RuntimeBehavior")!.Value);
    }

    [Fact]
    public void EveryFileTypeTheAssistantReadsGetsOneVerbThatRunsTheCommand()
    {
        var application = Manifest().Root!.Element(Foundation + "Applications")!.Element(Foundation + "Application")!;
        var menus = application.Descendants(Desktop4 + "Extension").Single(extension => extension.Attribute("Category")!.Value == "windows.fileExplorerContextMenus");
        var types = menus.Descendants(Desktop5 + "ItemType").ToList();

        Assert.Equal(ExplorerFileTypes.Extensions.Order(StringComparer.OrdinalIgnoreCase), types.Select(type => type.Attribute("Type")!.Value).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(types, type =>
        {
            var verb = Assert.Single(type.Elements(Desktop5 + "Verb"));
            Assert.Equal("AskAssistant", verb.Attribute("Id")!.Value);
            Assert.Equal(AskAssistantCommand.ClassId, Guid.Parse(verb.Attribute("Clsid")!.Value));
        });
    }

    [Fact]
    public void TheManifestStartsTheServerThatHostsTheCommandWithTheArgumentTheProgramRecognises()
    {
        var application = Manifest().Root!.Element(Foundation + "Applications")!.Element(Foundation + "Application")!;
        var server = application.Descendants(Com + "ExeServer").Single();

        Assert.Equal("Assistant.ExplorerExtension.exe", server.Attribute("Executable")!.Value);
        Assert.True(TopMenuServer.IsServerStart([server.Attribute("Arguments")!.Value]));
        Assert.Equal(AskAssistantCommand.ClassId, Guid.Parse(Assert.Single(server.Elements(Com + "Class")).Attribute("Id")!.Value));
        Assert.True(TopMenuServer.IsServerStart(["-Embedding"]));
        Assert.False(TopMenuServer.IsServerStart(["ask", @"C:\Docs\plan.pdf"]));
        Assert.False(TopMenuServer.IsServerStart(["register"]));
    }

    [Fact]
    public void AnExecutableNameWithMarkupCannotBreakTheManifestOut()
    {
        var document = Manifest("a\"><evil/>.exe");

        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "evil");
    }

    // ---- The command ----

    private sealed class Item(string path) : IShellItem
    {
        public int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result) { result = 0; return ComNative.ENotImpl; }

        public int GetParent(out nint parent) { parent = 0; return ComNative.ENotImpl; }

        public int GetDisplayName(uint sigdn, out nint name)
        {
            name = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni(path);
            return ComNative.SOk;
        }

        public int GetAttributes(uint mask, out uint attributes) { attributes = 0; return ComNative.ENotImpl; }

        public int Compare(nint other, uint hint, out int order) { order = 0; return ComNative.ENotImpl; }
    }

    private sealed class Selection(params string[] paths) : IShellItemArray
    {
        public int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result) { result = 0; return ComNative.ENotImpl; }

        public int GetPropertyStore(int flags, in Guid riid, out nint result) { result = 0; return ComNative.ENotImpl; }

        public int GetPropertyDescriptionList(nint key, in Guid riid, out nint result) { result = 0; return ComNative.ENotImpl; }

        public int GetAttributes(int flags, uint mask, out uint attributes) { attributes = 0; return ComNative.ENotImpl; }

        public int GetCount(out uint count) { count = (uint)paths.Length; return ComNative.SOk; }

        public int GetItemAt(uint index, out IShellItem item) { item = new Item(paths[index]); return ComNative.SOk; }
    }

    private static int StateOf(params string[] paths)
    {
        new AskAssistantCommand().GetState(new Selection(paths), 0, out var state);
        return state;
    }

    [Fact]
    public void TheCommandIsShownForFilesTheAssistantCanReadAndHiddenForTheRest()
    {
        Assert.Equal(0, StateOf(@"C:\Docs\plan.pdf"));
        Assert.Equal(0, StateOf(@"C:\Docs\plan.docx", @"C:\Pictures\cat.png"));
        Assert.Equal(2, StateOf(@"C:\Archives\all.zip"));
        Assert.Equal(2, StateOf(@"C:\Docs\plan.pdf", @"C:\Archives\all.zip"));
        Assert.Equal(2, StateOf());

        new AskAssistantCommand().GetState(null, 0, out var none);
        Assert.Equal(2, none);
    }

    [Fact]
    public void TheCommandIsTitledAsTheClassicEntryIsAndAsksForNoSubmenu()
    {
        var command = new AskAssistantCommand();

        Assert.Equal(0, command.GetTitle(0, out var title));
        try
        {
            Assert.Equal(ExplorerMenuRegistration.Title, System.Runtime.InteropServices.Marshal.PtrToStringUni(title));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(title);
        }

        Assert.Equal(0, command.GetFlags(out var flags));
        Assert.Equal(0, flags);
        Assert.NotEqual(0, command.EnumSubCommands(out _));
    }

    [Fact]
    public void TheIconIsTheMarkThatSuitsTheMenuAndIsOneThatShipsBesideTheProgram()
    {
        var command = new AskAssistantCommand();
        var expected = ExplorerMenuRegistration.SystemUsesLightTheme() ? ExplorerMenuRegistration.LightMenuIconName : ExplorerMenuRegistration.DarkMenuIconName;

        var result = command.GetIcon(0, out var icon);

        Assert.Equal(0, result);
        try
        {
            var path = System.Runtime.InteropServices.Marshal.PtrToStringUni(icon)!;
            Assert.Equal(expected, Path.GetFileName(path));
            Assert.True(File.Exists(path), path);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(icon);
        }
    }
}
