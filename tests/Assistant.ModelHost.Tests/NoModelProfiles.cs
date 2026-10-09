using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;

namespace Assistant.ModelHost.Tests;

/// <summary>A resolver that knows no profile, so a test's model is only ever the file its settings name.</summary>
internal sealed class NoModelProfiles : IModelProfileResolver
{
    public ResolvedModel? Resolve(ModelSettings settings) => null;
}
