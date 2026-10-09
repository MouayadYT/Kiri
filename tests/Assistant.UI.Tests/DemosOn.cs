using System.Runtime.CompilerServices;
using Assistant.UI.Bootstrap.Placeholders;

namespace Assistant.UI.Tests;

// The developer's samples ("demo task", "demo photos", the made-up results in the bar) are off in the app unless it is started with ASSISTANT_DEMOS=1
// (DemoAnswerProvider.DemosVariable). The tests that run the real container use them, so the variable is set for the test process, before any test runs.
internal static class DemosOn
{
#pragma warning disable CA2255 // A module initializer is what runs before the first test, whichever it is.
    [ModuleInitializer]
    internal static void Initialize() => Environment.SetEnvironmentVariable(DemoAnswerProvider.DemosVariable, "1");
#pragma warning restore CA2255
}