using System.Security;
using System.Text;
using Assistant.Core.Contracts;
using Windows.Management.Deployment;

namespace Assistant.ExplorerExtension.TopMenu;

/// <summary>
/// Gives this entry point the package identity File Explorer's Windows 11 first menu requires, with a sparse package: a manifest
/// (written to the user's own folder) that names the COM server and the file types, while the files stay where the app is. Windows
/// accepts an unsigned sparse package only when Developer Mode is on; otherwise registering fails, and the entry stays under
/// "Show more options" (<see cref="Registration.ExplorerMenuRegistration"/>).
/// </summary>
internal static class TopMenuPackage
{
    /// <summary>The package's name.</summary>
    public const string PackageName = "Assistant.ExplorerMenu";

    private const string LogoName = "PackageLogo.png";

    /// <summary>
    /// The folder the manifest is written to: inside the program's own folder, which Windows' package service can read. The Assistant's data folder cannot be used: its permissions
    /// leave out application packages, and registration fails there with "cannot find the path" (measured on Windows 11 26200).
    /// </summary>
    public static string ManifestFolderFor(string externalLocation) => Path.Combine(externalLocation, "ExplorerMenuPackage");

    /// <summary>The manifest for the entry point at <paramref name="executableName"/>, which sits in the package's external location.</summary>
    public static string Manifest(string executableName, IEnumerable<string> extensions)
    {
        var types = new StringBuilder();
        foreach (var extension in extensions)
        {
            types.Append($"""

                        <desktop5:ItemType Type="{SecurityElement.Escape(extension)}">
                          <desktop5:Verb Id="AskAssistant" Clsid="{AskAssistantCommand.ClassId}" />
                        </desktop5:ItemType>
              """);
        }

        var executable = SecurityElement.Escape(executableName);
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                     xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                     xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                     xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"
                     xmlns:desktop5="http://schemas.microsoft.com/appx/manifest/desktop/windows10/5"
                     xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10"
                     IgnorableNamespaces="uap uap10 rescap desktop4 desktop5 com">
              <Identity Name="{PackageName}" Publisher="CN=Assistant" Version="1.0.0.0" ProcessorArchitecture="x64" />
              <Properties>
                <DisplayName>Assistant File Explorer menu</DisplayName>
                <PublisherDisplayName>Assistant</PublisherDisplayName>
                <Logo>{LogoName}</Logo>
                <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
              </Properties>
              <Resources>
                <Resource Language="en-us" />
              </Resources>
              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.22621.0" />
              </Dependencies>
              <Capabilities>
                <rescap:Capability Name="runFullTrust" />
              </Capabilities>
              <Applications>
                <Application Id="AskAssistant" Executable="{executable}" uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="win32App">
                  <uap:VisualElements AppListEntry="none" DisplayName="Assistant File Explorer menu" Description="Ask Assistant in File Explorer's menu"
                                      BackgroundColor="transparent" Square150x150Logo="{LogoName}" Square44x44Logo="{LogoName}" />
                  <Extensions>
                    <desktop4:Extension Category="windows.fileExplorerContextMenus">
                      <desktop4:FileExplorerContextMenus>{types}
                      </desktop4:FileExplorerContextMenus>
                    </desktop4:Extension>
                    <com:Extension Category="windows.comServer">
                      <com:ComServer>
                        <com:ExeServer Executable="{executable}" Arguments="{TopMenuServer.ServeName}" DisplayName="Ask Assistant">
                          <com:Class Id="{AskAssistantCommand.ClassId}" DisplayName="Ask Assistant" />
                        </com:ExeServer>
                      </com:ComServer>
                    </com:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """;
    }

    /// <summary>
    /// Writes the manifest and registers the package for the current user, with the files in <paramref name="externalLocation"/>.
    /// Returns whether Windows accepted it.
    /// </summary>
    public static bool Register(string externalLocation, string executableName)
    {
        try
        {
            var folder = ManifestFolderFor(externalLocation);
            Directory.CreateDirectory(folder);
            var logo = Path.Combine(externalLocation, LogoName);
            if (File.Exists(logo))
            {
                File.Copy(logo, Path.Combine(folder, LogoName), overwrite: true);
            }

            var manifest = Path.Combine(folder, "AppxManifest.xml");
            File.WriteAllText(manifest, Manifest(executableName, ExplorerFileTypes.Extensions), new UTF8Encoding(false));

            var options = new RegisterPackageOptions
            {
                ExternalLocationUri = new Uri(Path.TrimEndingDirectorySeparator(externalLocation) + Path.DirectorySeparatorChar),
                DeveloperMode = true,
                ForceUpdateFromAnyVersion = true,
            };
            var result = new PackageManager().RegisterPackageByUriAsync(new Uri(manifest), options).AsTask().GetAwaiter().GetResult();
            return result.IsRegistered;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Removes the package for the current user. Returns true when it is gone, or was never there.</summary>
    public static bool Unregister()
    {
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty).Where(p => p.Id.Name == PackageName))
            {
                var result = manager.RemovePackageAsync(package.Id.FullName).AsTask().GetAwaiter().GetResult();
                if (result.ExtendedErrorCode is not null)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}
