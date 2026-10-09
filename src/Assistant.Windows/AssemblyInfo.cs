using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Assistant.Windows.Tests")]

// The app's tests build the answers of the selection service (SelectionResult) that its fake returns.
[assembly: InternalsVisibleTo("Assistant.UI.Tests")]

// The release smoke tests (step 127) drive the Windows services through the app's own container.
[assembly: InternalsVisibleTo("Assistant.SmokeTests")]
