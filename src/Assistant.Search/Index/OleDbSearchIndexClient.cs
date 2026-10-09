using System.Data.OleDb;
using System.Runtime.InteropServices;
using Assistant.Core.Contracts;

namespace Assistant.Search.Index;

/// <summary>
/// Queries the Windows Search index (<c>SystemIndex</c>) through its OLE DB provider, <c>Search.CollatorDSO</c>, as the
/// current user, so the file system's own access rights decide what is found. A query runs on a pool thread, one connection
/// for each, and stops when it is cancelled or runs past its time limit.
/// </summary>
internal sealed class OleDbSearchIndexClient(TimeSpan timeout, string connectionString = OleDbSearchIndexClient.WindowsSearchConnection)
    : ISearchIndexClient
{
    // The Application=Windows extension makes the provider read the query as Windows Search SQL and use the user's index.
    internal const string WindowsSearchConnection = "Provider=Search.CollatorDSO.1;Extended Properties='Application=Windows'";

    // DB_E_NOTABLE: the provider is there but there is no SystemIndex to query, as when the service or its catalog is off.
    private const int NoTable = unchecked((int)0x80040E37);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<object?[]>> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = abort.Token;
        var work = Task.Run(
            () =>
            {
                try
                {
                    return Run(connectionString, sql, token);
                }
                finally
                {
                    abort.Dispose();
                }
            },
            CancellationToken.None);

        try
        {
            return await work.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException && !work.IsCompleted)
        {
            // The provider may not stop at once: ask it to, and let nobody wait for it or hear what it fails with.
            Abort(abort);
            _ = work.ContinueWith(static finished => _ = finished.Exception, TaskScheduler.Default);
            if (exception is TimeoutException)
            {
                throw new FileSearchException(FileSearchFailure.TimedOut);
            }

            throw;
        }
    }

    private static void Abort(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished, and closed the source, first.
        }
    }

    private static IReadOnlyList<object?[]> Run(string connectionString, string sql, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        using var connection = new OleDbConnection(connectionString);
        try
        {
            connection.Open();
        }
        catch (Exception exception)
        {
            // A provider that is not registered, a service that is off and a catalog that is missing all fail here.
            token.ThrowIfCancellationRequested();
            throw new FileSearchException(FileSearchFailure.IndexUnavailable, ErrorCodeOf(exception));
        }

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var registration = token.Register(
            static command =>
            {
                try
                {
                    ((OleDbCommand)command!).Cancel();
                }
                catch (Exception)
                {
                    // Nothing is running any more, or the provider cannot stop it; the row loop checks the token as well.
                }
            },
            command);

        try
        {
            using var reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add([.. values.Select(value => value is DBNull ? null : value)]);
            }

            return rows;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            var code = ErrorCodeOf(exception);

            // The provider's own text can repeat the query, so only its code goes on.
            throw new FileSearchException(
                code == NoTable ? FileSearchFailure.IndexUnavailable : FileSearchFailure.QueryFailed,
                code);
        }
    }

    private static int? ErrorCodeOf(Exception exception) => exception is ExternalException external ? external.ErrorCode : null;
}
