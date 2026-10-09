namespace Assistant.Core.QuickSearch.Routing;

/// <summary>Where the words typed in the Search or Ask bar go (PROJECT_SPEC §4.1).</summary>
public enum QueryRouteKind
{
    /// <summary>
    /// Instant, deterministic search: applications, files by name, actions and the clipboard history are looked up as the user types,
    /// and nothing asks the model. Names, short phrases and commands are this.
    /// </summary>
    InstantSearch = 0,

    /// <summary>
    /// A structured Windows Search: the words ask for files in a way the fixed rules read into a query ("the pdf I edited last Tuesday",
    /// "the last 5 screenshots"). Still no model; the answer is a list of files.
    /// </summary>
    FileSearch = 1,

    /// <summary>
    /// Straightforward arithmetic ("9 + 10"): a deterministic calculation, not a question for the model. Only while a calculator is
    /// available; its result is a calculation card (<c>CalculationResult</c>).
    /// </summary>
    Calculation = 2,

    /// <summary>A question or instruction for the local model that needs nothing of the user's files or screen.</summary>
    DirectAnswer = 3,

    /// <summary>
    /// A request that needs the user's files or screen, so the local model is offered its tools to find and read them
    /// (PROJECT_SPEC §4.8).
    /// </summary>
    AgentRequest = 4,
}

/// <summary>Why the router chose a route; for tests and for telling the user in words.</summary>
public enum QueryRouteReason
{
    /// <summary>Nothing is typed: the categories are browsed.</summary>
    Empty = 0,

    /// <summary>A name, a short phrase or a command: nothing says it is anything else.</summary>
    LooksLikeAName = 1,

    /// <summary>The user chose to ask (Ctrl+Enter), whatever the words look like.</summary>
    ForcedAsk = 2,

    /// <summary>The words are a sum.</summary>
    Arithmetic = 3,

    /// <summary>The words clearly ask to find files.</summary>
    FileRequest = 4,

    /// <summary>The words are a question or an instruction to an assistant.</summary>
    Question = 5,

    /// <summary>The words are long enough to be a message rather than a name.</summary>
    LongText = 6,

    /// <summary>The words ask for something about the user's files or screen.</summary>
    NeedsFilesOrScreen = 7,
}

/// <summary>
/// The router's decision for one query. It is only a description: what is done is up to the bar, and a decision never changes what the
/// user can do (Enter on a highlighted result runs it, Ctrl+Enter always asks). The expression is private content and is never printed.
/// </summary>
/// <param name="Kind">Where the words go.</param>
/// <param name="Reason">Why.</param>
public sealed record QueryRoute(QueryRouteKind Kind, QueryRouteReason Reason)
{
    /// <summary>The words that say what path is used, for the user ("Search", "Calculate", "Ask").</summary>
    public string Label => Kind switch
    {
        QueryRouteKind.InstantSearch => "Search",
        QueryRouteKind.FileSearch => "File search",
        QueryRouteKind.Calculation => "Calculate",
        QueryRouteKind.DirectAnswer => "Ask",
        _ => "Ask with tools",
    };

    /// <summary>Whether the user forced Ask mode.</summary>
    public bool WasForced => Reason == QueryRouteReason.ForcedAsk;

    /// <summary>
    /// For a calculation, the sum as it should be worked out: operators written as <c>+ - * / ^ %</c> and the words around it
    /// ("what is") taken off. <see langword="null"/> for any other route.
    /// </summary>
    public string? Expression { get; init; }

    /// <summary>Whether the route is one the instant providers answer: the bar looks things up while the user types.</summary>
    public bool UsesInstantProviders => Kind is QueryRouteKind.InstantSearch or QueryRouteKind.FileSearch;

    /// <summary>Whether the words go to the local model when they are submitted.</summary>
    public bool GoesToTheModel => Kind is QueryRouteKind.DirectAnswer or QueryRouteKind.AgentRequest;

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}, Reason = {Reason}");
        return true;
    }
}

/// <summary>What the router is told besides the words.</summary>
public sealed record QueryRouteOptions
{
    /// <summary>The user chose to ask (Ctrl+Enter, or the Ask chip): the words go to the model whatever they look like.</summary>
    public bool ForceAsk { get; init; }
}

/// <summary>
/// Decides where the words typed in the bar go (PROJECT_SPEC §4.1) before anything is looked up or asked, and cheaply, by fixed rules,
/// never with the model.
/// </summary>
public interface IQueryRouter
{
    /// <summary>Routes <paramref name="query"/>. It never throws, and an empty query is instant search of the categories.</summary>
    QueryRoute Route(string? query, QueryRouteOptions? options = null);
}
