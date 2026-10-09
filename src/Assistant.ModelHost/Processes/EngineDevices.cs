using System.Globalization;
using System.Text.RegularExpressions;

namespace Assistant.ModelHost.Processes;

/// <summary>A graphics card (or other accelerator) the engine can offload a model to.</summary>
/// <param name="Id">The engine's name for the device, such as <c>Vulkan0</c>, as <c>--device</c> takes it.</param>
/// <param name="FreeBytes">The memory the device has free now.</param>
/// <param name="Name">What the engine calls the card ("NVIDIA GeForce RTX 5070", "Intel(R) UHD Graphics 770"), or empty when it did not say.</param>
internal sealed record EngineDevice(string Id, long FreeBytes, string Name = "")
{
    /// <summary>
    /// How much the card is worth choosing, by what it is: 2 for a card of its own (NVIDIA, a Radeon RX or Pro, an Intel Arc with a model number),
    /// 0 for graphics built into the processor (Intel UHD, Iris or HD, a Radeon without a model of its own), and 1 for one that cannot be told.
    /// Built-in graphics borrow the PC's own memory, so they report more of it free than any card has, and are much slower with it.
    /// </summary>
    public int Rank
    {
        get
        {
            var name = Name.ToUpperInvariant();
            if (name.Length == 0)
            {
                return 1;
            }

            if (name.Contains("NVIDIA", StringComparison.Ordinal) || name.Contains("GEFORCE", StringComparison.Ordinal)
                || name.Contains("QUADRO", StringComparison.Ordinal) || name.Contains("RTX", StringComparison.Ordinal))
            {
                return 2;
            }

            if (name.Contains("RADEON", StringComparison.Ordinal) || name.Contains("AMD", StringComparison.Ordinal))
            {
                return name.Contains(" RX ", StringComparison.Ordinal) || name.Contains("RADEON PRO", StringComparison.Ordinal)
                    || name.Contains("FIREPRO", StringComparison.Ordinal) || name.Contains(" XT", StringComparison.Ordinal)
                    ? 2
                    : 0;
            }

            if (name.Contains("INTEL", StringComparison.Ordinal))
            {
                // "Intel(R) Arc(TM) A770 Graphics" is a card; "Intel(R) Arc(TM) Graphics" is what newer processors call their own.
                return ArcModel().IsMatch(name) ? 2 : 0;
            }

            return name.Contains("MICROSOFT", StringComparison.Ordinal) || name.Contains("LLVMPIPE", StringComparison.Ordinal) ? 0 : 1;
        }
    }

    private static Regex ArcModel() => ArcModelPattern.Value;

    private static readonly Lazy<Regex> ArcModelPattern = new(() => new Regex(@"ARC(\(TM\))?\s+(PRO\s+)?[AB]\d{3}", RegexOptions.CultureInvariant));
}

/// <summary>Reads the engine's device list and picks the devices a model runs on.</summary>
/// <remarks>
/// Measured on a two-card machine, a model that fits on the card with the most free memory runs fastest on that card
/// alone: spreading it over both cards was 25 to 35 percent slower for 4B and 9B models. So the fewest devices that
/// hold the model are used, the roomiest first, and every device only when even they cannot hold it (the engine then
/// keeps the rest on the CPU). A card of its own is always chosen before the graphics built into the processor
/// (<see cref="EngineDevice.Rank"/>): on a laptop with both, the built-in graphics report the most free memory, since it
/// is the PC's own, and were what "Automatic" chose. The built-in graphics are used only where there is no card.
/// </remarks>
internal static partial class EngineDevices
{
    private const long Mebibyte = 1024 * 1024;

    /// <summary>Room the engine needs beside the model's weights: the context's cache and compute buffers.</summary>
    private const long WorkingMemoryBytes = 1024 * Mebibyte;

    /// <summary>The share of the weights added on top of <see cref="WorkingMemoryBytes"/>.</summary>
    private const double WeightsMargin = 0.1;

    /// <summary>
    /// The devices in <paramref name="output"/>, the text of <c>llama-server --list-devices</c>: one line each, such as
    /// <c>  Vulkan0: NVIDIA GeForce RTX 5070 (11943 MiB, 11175 MiB free)</c>. Lines that are not devices are ignored.
    /// </summary>
    public static IReadOnlyList<EngineDevice> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var devices = new List<EngineDevice>();
        foreach (Match line in DeviceLine().Matches(output))
        {
            if (long.TryParse(line.Groups["free"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var freeMebibytes))
            {
                devices.Add(new EngineDevice(line.Groups["id"].Value, freeMebibytes * Mebibyte, line.Groups["name"].Value.Trim()));
            }
        }

        return devices;
    }

    /// <summary>The memory a model needs on its devices: its files, a margin and the working memory.</summary>
    public static long RequiredBytes(long modelBytes, long projectorBytes) =>
        modelBytes + projectorBytes + (long)(modelBytes * WeightsMargin) + WorkingMemoryBytes;

    /// <summary>
    /// The ids of the devices to run on, roomiest first, or <see langword="null"/> when there are none (the engine then
    /// runs on the CPU).
    /// </summary>
    public static IReadOnlyList<string>? Choose(IReadOnlyList<EngineDevice> devices, long requiredBytes)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
        {
            return null;
        }

        // Only the best kind of device there is: a card of its own when there is one, and built-in graphics only when there is nothing else.
        var best = devices.Max(device => device.Rank);
        var roomiestFirst = devices
            .Where(device => device.Rank == best)
            .OrderByDescending(device => device.FreeBytes)
            .ThenBy(device => device.Id, StringComparer.Ordinal)
            .ToList();
        long free = 0;
        for (var count = 1; count <= roomiestFirst.Count; count++)
        {
            free += roomiestFirst[count - 1].FreeBytes;
            if (free >= requiredBytes)
            {
                return roomiestFirst.Take(count).Select(device => device.Id).ToArray();
            }
        }

        return roomiestFirst.Select(device => device.Id).ToArray();
    }

    [GeneratedRegex(@"^\s*(?<id>[A-Za-z][A-Za-z0-9_]*\d+):(?<name>.*)\(\d+ MiB, (?<free>\d+) MiB free\)\s*$", RegexOptions.Multiline)]
    private static partial Regex DeviceLine();
}
