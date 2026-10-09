namespace Assistant.Tools.Mcp;

/// <summary>
/// Reads a stream a line at a time without ever holding more than a limit of one line: a server that never ends a line cannot make the
/// Assistant use more and more memory. A line ends at a line feed; a carriage return before it is dropped. Lines are bytes: UTF-8 is read by whoever uses them.
/// </summary>
internal sealed class BoundedLineReader
{
    private readonly Stream _stream;
    private readonly int _maxLineBytes;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private readonly MemoryStream _line = new();
    private int _start;
    private int _end;

    /// <summary>Creates the reader over <paramref name="stream"/>, which accepts lines of at most <paramref name="maxLineBytes"/> bytes.</summary>
    public BoundedLineReader(Stream stream, int maxLineBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 1);
        _stream = stream;
        _maxLineBytes = maxLineBytes;
    }

    /// <summary>The next line, or <see langword="null"/> at the end of the stream (a last line without a line feed is still given).</summary>
    /// <exception cref="McpException">A line is longer than the limit (<see cref="McpFailure.TooLarge"/>).</exception>
    public async ValueTask<byte[]?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                if (_end == 0)
                {
                    return _line.Length == 0 ? null : TakeLine();
                }
            }

            var feed = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            var stop = feed < 0 ? _end : feed;
            _line.Write(_buffer, _start, stop - _start);
            _start = feed < 0 ? _end : feed + 1;
            if (_line.Length > _maxLineBytes)
            {
                throw new McpException(McpFailure.TooLarge);
            }

            if (feed >= 0)
            {
                return TakeLine();
            }
        }
    }

    private byte[] TakeLine()
    {
        var length = (int)_line.Length;
        var bytes = _line.GetBuffer();
        if (length > 0 && bytes[length - 1] == (byte)'\r')
        {
            length--;
        }

        var line = bytes.AsSpan(0, length).ToArray();
        _line.SetLength(0);
        return line;
    }
}
