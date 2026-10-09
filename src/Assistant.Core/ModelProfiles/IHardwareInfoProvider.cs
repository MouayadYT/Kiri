namespace Assistant.Core.ModelProfiles;

/// <summary>Tells what the machine offers a local model.</summary>
public interface IHardwareInfoProvider
{
    /// <summary>Reads the machine's hardware. It does not fail: what cannot be read is left at its unknown value.</summary>
    HardwareInfo Get();
}
