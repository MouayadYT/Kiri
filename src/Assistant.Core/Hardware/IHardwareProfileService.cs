namespace Assistant.Core.Hardware;

/// <summary>
/// Tells what this PC offers a local model (PROJECT_SPEC §5.6, step 124): the processor, the memory and the graphics adapters with the
/// video memory each has. Nothing leaves the PC and nothing is kept on disk.
/// </summary>
public interface IHardwareProfileService
{
    /// <summary>
    /// The profile as it was first read. It is read on first use and then kept, which is right for what does not change while the app runs
    /// (the processor, the size of the memory and of each card's); <see cref="Refresh"/> reads again what does (the memory and video memory that are free).
    /// </summary>
    HardwareProfile Current { get; }

    /// <summary>Reads the hardware again and returns the new profile, which <see cref="Current"/> then holds. Does not throw.</summary>
    HardwareProfile Refresh();
}
