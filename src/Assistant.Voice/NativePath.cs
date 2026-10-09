using System.Runtime.InteropServices;
using System.Text;
using Assistant.Core.Voice;

namespace Assistant.Voice;

/// <summary>
/// Makes a path one the speech runtime can open. Its C# wrapper passes paths to native code in the system's ANSI code page, which cannot hold every
/// character a Windows user name or folder may have; a path that has such characters is replaced by its short (8.3) name when Windows keeps one, and
/// otherwise the load is refused with words for the user.
/// </summary>
internal static unsafe partial class NativePath
{
    private const int MaxPath = 32_767;

    static NativePath() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Prepares <paramref name="path"/> for the runtime.</summary>
    /// <exception cref="VoiceEngineException">The path has characters the runtime cannot read, and Windows has no short name for it.</exception>
    public static string Prepare(string path)
    {
        if (IsRepresentable(path))
        {
            return path;
        }

        if (OperatingSystem.IsWindows())
        {
            var buffer = new char[1024];
            fixed (char* destination = buffer)
            {
                var length = GetShortPathNameW(path, destination, buffer.Length);
                if (length > 0 && length < buffer.Length)
                {
                    var shortPath = new string(destination, 0, length);
                    if (IsRepresentable(shortPath))
                    {
                        return shortPath;
                    }
                }
            }
        }

        throw new VoiceEngineException(
            VoiceEngineFailure.LoadFailed,
            "The voice's folder has a name with characters the speech engine cannot read. Move the Assistant's voices to a folder whose path is plain letters and digits.");
    }

    // Whether the path survives being written in the ANSI code page and read back.
    private static bool IsRepresentable(string path)
    {
        if (path.All(character => character < 128))
        {
            return true;
        }

        try
        {
            var encoding = Encoding.GetEncoding(
                System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return string.Equals(encoding.GetString(encoding.GetBytes(path)), path, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is EncoderFallbackException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetShortPathNameW(string longPath, char* shortPath, int bufferLength);
}
