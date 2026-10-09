using System.Windows;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Assistant.UI.Tests")]

// The release smoke tests (step 127) build the app's own container, as the bootstrapper does, and drive its controllers.
[assembly: InternalsVisibleTo("Assistant.SmokeTests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
