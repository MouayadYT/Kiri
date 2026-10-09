using Assistant.Core.Contracts;
using Assistant.Core.ModelHosting;

namespace Assistant.ModelHost.Models;

/// <summary>
/// Checks a model's files before the engine is started for them, so a missing file or something that is not a GGUF file
/// fails at once with a clear reason instead of after the engine has started and exited.
/// </summary>
internal static class ModelFileCheck
{
    private static readonly byte[] GgufMagic = "GGUF"u8.ToArray();

    /// <summary>Checks the files of <paramref name="files"/>, throwing for the first problem.</summary>
    /// <exception cref="ModelRequestException">
    /// <see cref="ModelHostErrorCode.ModelNotFound"/> when a file is missing, or
    /// <see cref="ModelHostErrorCode.ModelLoadFailed"/> when a model or projector file is unreadable or not GGUF, or the
    /// engine arguments are not allowed.
    /// </exception>
    public static void Verify(ModelFiles files)
    {
        // The protocol already refuses these, but the controller does not rely on who called it.
        if (!EngineArguments.IsValid(files.RuntimeArguments))
        {
            throw new ModelRequestException(ModelHostErrorCode.ModelLoadFailed, ModelFailure.LoadFailed);
        }

        VerifyGguf(files.ModelPath);
        if (files.ProjectorPath is not null)
        {
            VerifyGguf(files.ProjectorPath);
        }

        if (files.ChatTemplatePath is not null && !File.Exists(files.ChatTemplatePath))
        {
            throw NotFound();
        }
    }

    private static void VerifyGguf(string path)
    {
        if (!File.Exists(path))
        {
            throw NotFound();
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[4];
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length
                || !header.SequenceEqual(GgufMagic))
            {
                throw new ModelRequestException(ModelHostErrorCode.ModelLoadFailed, ModelFailure.LoadFailed);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelRequestException(ModelHostErrorCode.ModelLoadFailed, ModelFailure.LoadFailed);
        }
    }

    private static ModelRequestException NotFound() =>
        new(ModelHostErrorCode.ModelNotFound, ModelFailure.ModelNotFound);
}
