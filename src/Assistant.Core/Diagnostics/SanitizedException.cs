using System.Diagnostics;
using System.Text;

namespace Assistant.Core.Diagnostics;

/// <summary>
/// Stands in for an exception in log output. Keeps type names, HRESULTs and stack frames, and drops messages and
/// source file paths, which can contain private content (PROJECT_SPEC §3.3).
/// </summary>
public sealed class SanitizedException : Exception
{
    private const int MaxInnerDepth = 8;

    private readonly string _details;

    private SanitizedException(Exception original)
        : base($"{original.GetType().FullName} (message redacted)")
    {
        HResult = original.HResult;
        var builder = new StringBuilder();
        Describe(builder, original, depth: 0);
        _details = builder.ToString();
    }

    /// <summary>Creates the sanitized form of <paramref name="exception"/>.</summary>
    public static SanitizedException From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception as SanitizedException ?? new SanitizedException(exception);
    }

    /// <summary>Returns the type names, HRESULTs and stack frames of the original exception and its inner exceptions.</summary>
    public override string ToString() => _details;

    private static void Describe(StringBuilder builder, Exception exception, int depth)
    {
        if (depth > 0)
        {
            builder.AppendLine().Append(" ---> ");
        }

        builder.Append($"{exception.GetType().FullName} (HResult 0x{exception.HResult:X8}): message redacted");

        // Frames without file information, so no source paths leak.
        var frames = new StackTrace(exception, fNeedFileInfo: false).ToString().TrimEnd();
        if (frames.Length > 0)
        {
            builder.AppendLine().Append(frames);
        }

        if (depth == MaxInnerDepth)
        {
            return;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                Describe(builder, inner, depth + 1);
            }
        }
        else if (exception.InnerException is { } inner)
        {
            Describe(builder, inner, depth + 1);
        }
    }
}
