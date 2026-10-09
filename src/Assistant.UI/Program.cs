using Assistant.UI.Bootstrap;

namespace Assistant.UI;

/// <summary>Process entry point.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => AppBootstrapper.Run(args);
}
