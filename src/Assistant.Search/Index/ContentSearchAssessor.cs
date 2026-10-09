using Assistant.Core.Contracts;

namespace Assistant.Search.Index;

/// <summary>
/// Decides whether the index can look inside the files a query covers, from what is known before the query runs: whether it
/// holds anything under the query's folder, and what Windows can read of the types the query names. The verdict explains an
/// answer and never changes it, so it claims a limit only where one is certain: a type or a place that cannot be told
/// about is not called unavailable.
/// </summary>
internal static class ContentSearchAssessor
{
    /// <summary>
    /// The capability for <paramref name="plan"/>.
    /// </summary>
    /// <param name="plan">The query, read.</param>
    /// <param name="types">What Windows can read of the types the query names.</param>
    /// <param name="locationHoldsItems">
    /// Whether the index holds anything under the plan's folder; null when the plan has no folder, or it was not asked.
    /// </param>
    public static ContentSearchCapability Assess(FileSearchPlan plan, IContentTypeCatalog types, bool? locationHoldsItems)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(types);

        if (!plan.ContentRequested)
        {
            return ContentSearchCapability.NotRequested;
        }

        var limits = ContentSearchLimits.None;
        var everyTypeLimited = false;

        var unsupported = plan.Extensions
            .Where(extension => types.Lookup(extension) is ContentTypeSupport.NoContentFilter or ContentTypeSupport.NoTextInMedia)
            .ToArray();
        if (unsupported.Length > 0)
        {
            limits |= ContentSearchLimits.FileTypeNotContentIndexed;
            everyTypeLimited = unsupported.Length == plan.Extensions.Count;
        }

        // A picture, a video or a sound has properties to search and no text.
        if (plan.Kind is FileKind.Picture or FileKind.Video or FileKind.Music)
        {
            limits |= ContentSearchLimits.FileTypeNotContentIndexed;
            everyTypeLimited = true;
        }

        var nothingThere = locationHoldsItems == false;
        if (nothingThere)
        {
            limits |= ContentSearchLimits.LocationNotIndexed;
        }

        if (limits == ContentSearchLimits.None)
        {
            return ContentSearchCapability.Available;
        }

        return new ContentSearchCapability
        {
            Support = nothingThere || everyTypeLimited ? ContentSearchSupport.Unavailable : ContentSearchSupport.Partial,
            Limits = limits,
            UnsupportedExtensions = unsupported,
        };
    }
}
