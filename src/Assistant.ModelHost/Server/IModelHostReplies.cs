using Assistant.Core.ModelHosting;

namespace Assistant.ModelHost.Server;

/// <summary>Sends the replies to one request, each carrying that request's id.</summary>
internal interface IModelHostReplies
{
    /// <summary>Sends one reply.</summary>
    /// <exception cref="IOException">The connection is broken.</exception>
    ValueTask SendAsync(ModelHostReply reply, CancellationToken cancellationToken = default);
}
