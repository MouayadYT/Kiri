namespace Assistant.Core.Hardware;

/// <summary>The processor (PROJECT_SPEC §5.6, step 124).</summary>
/// <param name="Name">The processor's name as Windows records it, such as "Intel(R) Core(TM) i5-14600K", or empty when it could not be read.</param>
/// <param name="PhysicalCores">The number of physical cores, or 0 when it could not be read.</param>
/// <param name="LogicalProcessors">The number of logical processors (threads), across all processor groups.</param>
/// <param name="Architecture">The processor family the PC runs, such as "X64" or "Arm64".</param>
/// <param name="MaxClockMegahertz">The processor's rated speed in MHz, or 0 when it could not be read.</param>
public sealed record CpuInfo(string Name, int PhysicalCores, int LogicalProcessors, string Architecture, int MaxClockMegahertz);
