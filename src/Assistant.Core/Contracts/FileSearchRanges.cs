namespace Assistant.Core.Contracts;

/// <summary>
/// A span of time for a file search: from one instant, which is in it, up to another, which is not. Either end may be left
/// out. The index records times to the whole second, and the ends are compared at that resolution. A range that ends at or
/// before its start holds nothing, and a search for it finds nothing.
/// </summary>
/// <param name="From">The earliest time, included; <see langword="null"/> for no start.</param>
/// <param name="To">The time the range stops at, excluded; <see langword="null"/> for no end.</param>
public readonly record struct DateRange(DateTimeOffset? From = null, DateTimeOffset? To = null)
{
    /// <summary>Whether neither end is given, so that the range holds every time.</summary>
    public bool IsUnbounded => From is null && To is null;
}

/// <summary>
/// A span of file sizes in bytes for a file search, both ends included. Either end may be left out. A range whose smallest
/// size is above its largest holds nothing, and a search for it finds nothing; a smallest size below zero is no start.
/// </summary>
/// <param name="MinBytes">The smallest size; <see langword="null"/> for no lower limit.</param>
/// <param name="MaxBytes">The largest size; <see langword="null"/> for no upper limit.</param>
public readonly record struct SizeRange(long? MinBytes = null, long? MaxBytes = null)
{
    /// <summary>Whether neither end is given, so that the range holds every size.</summary>
    public bool IsUnbounded => MinBytes is null && MaxBytes is null;
}
