using System.Text;
using Assistant.Core.Documents;

namespace Assistant.Documents.Context;

/// <summary>
/// Cuts the pieces of a document into passages (PROJECT_SPEC §4.7), the same way every time. A piece is a page, a slide, a
/// section or the whole document, as a reader returned it, already normalized.
/// </summary>
/// <remarks>
/// <para>
/// A piece that fits a passage (up to the target and the smallest passage together) is one passage, and pieces that are shorter
/// than the smallest passage are gathered with the pieces after them while they fit the target, each marked in the text where it
/// starts, so a slide of a title is read with the slide under it. A longer piece is cut where the text breaks best within the
/// last third of the target: at a paragraph break, else at the end of a sentence, else at a line break, else between words, else
/// anywhere; the next passage starts the overlap before the cut, at a word, so what falls on a cut is whole in one of the two.
/// A passage never crosses from one piece into another by overlap, so its place is always one part of the document, or a run
/// of short parts it gathers.
/// </para>
/// <para>
/// The place of a passage in a text or Markdown file is refined to its own lines when the piece's line numbers agree with its
/// text; when they do not (blank lines the reader collapsed) the passage keeps the lines of the whole piece, which is coarser
/// and never wrong.
/// </para>
/// </remarks>
internal static class DocumentChunker
{
    private const string GatheredSeparator = "\n\n";

    public static DocumentContext Chunk(IReadOnlyList<DocumentSegment> pieces, DocumentChunkingOptions options, bool truncated)
    {
        var limits = options.Resolve();
        var passages = new List<DocumentPassage>();
        var source = 0;
        var index = 0;
        while (index < pieces.Count)
        {
            if (passages.Count >= limits.MaxPassages)
            {
                truncated = true;
                break;
            }

            var piece = pieces[index];
            source += piece.Text.Length;
            if (piece.Text.Length > limits.TargetCharacters + limits.MinCharacters)
            {
                if (!CutPiece(piece, limits, passages))
                {
                    truncated = true;
                    break;
                }

                index++;
                continue;
            }

            index = Gather(pieces, index, limits, passages, ref source);
        }

        return new DocumentContext { Passages = passages, Truncated = truncated, SourceCharacters = source };
    }

    // One passage from the piece at `start` and, while it is shorter than the smallest passage, the pieces that follow it
    // that still fit the target. Returns the index of the piece after the last one used.
    private static int Gather(
        IReadOnlyList<DocumentSegment> pieces, int start, DocumentChunkingOptions limits, List<DocumentPassage> passages, ref int source)
    {
        var first = pieces[start];
        var text = new StringBuilder(first.Text);
        var index = start + 1;
        while (text.Length < limits.MinCharacters && index < pieces.Count)
        {
            var next = pieces[index];
            var addition = GatheredSeparator.Length + (next.Header is { } header ? header.Length + 1 : 0) + next.Text.Length;
            if (text.Length + addition > limits.TargetCharacters)
            {
                break;
            }

            text.Append(GatheredSeparator);
            if (next.Header is { } marker)
            {
                text.Append(marker).Append('\n');
            }

            text.Append(next.Text);
            source += next.Text.Length;
            index++;
        }

        var last = pieces[index - 1];
        passages.Add(new DocumentPassage
        {
            Index = passages.Count,
            Text = text.ToString(),
            Location = first.Location,
            EndLocation = last.Location,
        });
        return index;
    }

    // A piece longer than a passage, cut into passages that overlap. False when the passages ran out before the piece did.
    private static bool CutPiece(DocumentSegment piece, DocumentChunkingOptions limits, List<DocumentPassage> passages)
    {
        var text = piece.Text;
        var length = text.Length;
        var largest = limits.TargetCharacters + limits.MinCharacters;
        var lines = LineMap.For(piece.Location, text);
        var start = 0;
        var previousEnd = -1;
        while (start < length && passages.Count < limits.MaxPassages)
        {
            var end = length - start <= largest ? length : TextBreaks.FindEnd(text, start, start + limits.TargetCharacters);
            var from = start;
            var to = end;
            while (from < to && char.IsWhiteSpace(text[from]))
            {
                from++;
            }

            while (to > from && char.IsWhiteSpace(text[to - 1]))
            {
                to--;
            }

            if (to > from)
            {
                passages.Add(new DocumentPassage
                {
                    Index = passages.Count,
                    Text = text[from..to],
                    Location = lines.Place(from, to),
                    OverlapLength = previousEnd > from ? previousEnd - from : 0,
                });
                previousEnd = to;
            }

            if (end >= length)
            {
                return true;
            }

            start = TextBreaks.FindRestart(text, end, limits.OverlapCharacters, start);
        }

        return start >= length;
    }

    // Finds the lines of a passage inside a piece of a text or Markdown file, when the piece's own line numbers agree with its text.
    private sealed class LineMap
    {
        private readonly DocumentLocation _location;
        private readonly List<int> _breaks;
        private readonly int _firstLine;

        private LineMap(DocumentLocation location, List<int> breaks, int firstLine)
        {
            _location = location;
            _breaks = breaks;
            _firstLine = firstLine;
        }

        public static LineMap For(DocumentLocation location, string text)
        {
            if (location.FirstLine is not { } first || location.LastLine is not { } last)
            {
                return new LineMap(location, [], 0);
            }

            var breaks = new List<int>();
            for (var position = text.IndexOf('\n'); position >= 0; position = text.IndexOf('\n', position + 1))
            {
                breaks.Add(position);
            }

            // Lines the reader counted in the file and lines in this text are the same lines only when their numbers agree.
            return last - first == breaks.Count ? new LineMap(location, breaks, first) : new LineMap(location, [], 0);
        }

        public DocumentLocation Place(int from, int to)
        {
            // No lines, a piece on one line, or a piece whose numbers disagree with its text: the place of the whole piece.
            if (_breaks.Count == 0)
            {
                return _location;
            }

            return _location with
            {
                FirstLine = _firstLine + BreaksBefore(from),
                LastLine = _firstLine + BreaksBefore(to),
            };
        }

        // The line breaks at positions before `position`.
        private int BreaksBefore(int position)
        {
            var found = _breaks.BinarySearch(position);
            return found >= 0 ? found : ~found;
        }
    }
}
