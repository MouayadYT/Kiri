namespace Assistant.Windows.Gaming;

/// <summary>How much graphics memory a process holds, as Task Manager shows it.</summary>
public static class ProcessGraphicsMemory
{
    /// <summary>
    /// The dedicated graphics memory process <paramref name="processId"/> holds, over every graphics card, in bytes; or <see langword="null"/> when
    /// Windows keeps no such number for it (it has not used a graphics card, or the driver does not report it).
    /// </summary>
    public static long? DedicatedBytes(int processId)
    {
        using var counter = GpuProcessCounter.Open(processId);
        return counter?.Sample()?.DedicatedBytes;
    }
}
