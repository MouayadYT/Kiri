namespace Assistant.Windows.Shell;

/// <summary>
/// Runs shell work on a thread of its own that is a single-threaded apartment, as the shell's objects expect: a thread-pool
/// thread is not one. The thread lives for one piece of work, which is rare (reading the applications once in a while, an icon the
/// first time it is listed) and so costs nothing that a long-lived thread would save.
/// </summary>
internal static class StaThread
{
    /// <summary>Runs <paramref name="work"/> on a new STA thread and returns what it returned.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the work started.</exception>
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.SetResult(work());
            }
            catch (OperationCanceledException exception)
            {
                completion.SetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Assistant shell",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
