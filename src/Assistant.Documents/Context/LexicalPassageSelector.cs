using Assistant.Core.Contracts;
using Assistant.Core.Documents;

namespace Assistant.Documents.Context;

/// <summary>
/// Chooses the passages of a document for a question by the words they share (PROJECT_SPEC §4.7): keyword ranking, with no
/// embeddings and no index besides the passages themselves. It is deterministic: the same passages, question and limits always
/// give the same selection, and equal scores are told apart by the order of the document.
/// </summary>
/// <remarks>
/// <para>
/// A passage is scored with BM25 over the passages of the document: each word of the question (see <see cref="TermExtractor"/>)
/// counts for more the rarer it is among the passages, a word repeated counts for less each time, and a long passage counts
/// less than a short one that says the same. The heading or title of the passage's place counts as part of it, two words of the
/// question that stand next to each other in the passage earn a little more, and a passage at a place the question names
/// (<c>page 4</c>) comes before every other.
/// </para>
/// <para>
/// A document whose passages all fit the limits is taken whole, whatever was asked, since nothing is gained by leaving part of
/// it out. Otherwise the best passages are taken, the best first, until the limits are reached: one that does not fit is
/// skipped and a shorter one may take its place, and passages that follow each other are counted once where they overlap. A
/// passage that holds only words that most passages hold, and scores under a fifth of the best one's, is not taken. A
/// question whose words no passage holds takes the start of the document in order; a question with no word worth looking for
/// ("what is this about") is about the whole of it, so it takes passages spread evenly over the document, the first and the last
/// among them. The selected passages come back in the order of the document.
/// </para>
/// </remarks>
public sealed class LexicalPassageSelector : IPassageSelector
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const double PhraseWeight = 0.5;
    private const double PlaceScore = 100;

    // A passage that only has the words most passages have scores a small share of the best one's, and is left out: it would
    // only be noise in the prompt. A passage at a place the question names is never left out for its words.
    private const double MinShareOfBestScore = 0.2;

    /// <inheritdoc/>
    public PassageSelection Select(IReadOnlyList<DocumentPassage> passages, string question, PassageSelectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(passages);
        ArgumentNullException.ThrowIfNull(question);
        if (passages.Count == 0)
        {
            return PassageSelection.Empty;
        }

        var limits = (options ?? PassageSelectionOptions.Default).Resolve();
        var query = QuestionTerms.Parse(question);
        var (scores, placed) = Score(passages, query);

        List<SelectedPassage> chosen;
        PassageSelectionReason reason;
        if (FitsWhole(passages, limits))
        {
            chosen = [.. passages.Select((passage, index) => new SelectedPassage(passage, scores[index]))];
            reason = PassageSelectionReason.WholeDocument;
        }
        else if (query.IsEmpty)
        {
            chosen = Spread(passages, scores, limits);
            reason = PassageSelectionReason.NoQueryTerms;
        }
        else if (!scores.Any(score => score > 0))
        {
            chosen = Start(passages, scores, limits);
            reason = PassageSelectionReason.NoMatch;
        }
        else
        {
            chosen = Best(passages, scores, placed, limits);
            reason = PassageSelectionReason.Matched;
        }

        return new PassageSelection
        {
            Passages = chosen,
            TotalPassages = passages.Count,
            Reason = reason,
            QueryTermCount = query.Terms.Count,
        };
    }

    // The passages cost what they add to the prompt: their own text, less what repeats the passage they follow.
    private static bool FitsWhole(IReadOnlyList<DocumentPassage> passages, PassageSelectionOptions limits)
    {
        if (passages.Count > limits.MaxPassages)
        {
            return false;
        }

        long characters = 0;
        foreach (var passage in passages)
        {
            characters += passage.Text.Length - passage.OverlapLength;
        }

        return characters <= limits.MaxCharacters;
    }

    // A question with no words to look for ("what is this about?", "summarize it") is about the whole document, and its start is
    // not that: passages spread evenly over the document, the first and the last among them, as many as fit the limits. The same
    // document and limits always give the same passages.
    private static List<SelectedPassage> Spread(IReadOnlyList<DocumentPassage> passages, double[] scores, PassageSelectionOptions limits)
    {
        var most = Math.Min(limits.MaxPassages, passages.Count);
        for (var count = most; count >= 2; count--)
        {
            var picked = Enumerable.Range(0, count)
                .Select(slot => (int)Math.Round(slot * (passages.Count - 1) / (double)(count - 1), MidpointRounding.AwayFromZero))
                .Distinct()
                .ToArray();
            long cost = 0;
            for (var i = 0; i < picked.Length; i++)
            {
                var follows = i > 0 && picked[i - 1] == picked[i] - 1;
                cost += passages[picked[i]].Text.Length - (follows ? passages[picked[i]].OverlapLength : 0);
            }

            if (cost <= limits.MaxCharacters)
            {
                return [.. picked.Select(index => new SelectedPassage(passages[index], scores[index]))];
            }
        }

        return Start(passages, scores, limits);
    }

    // The passages from the start of the document, in order, while they fit. The first is cut to fit if it alone is too long.
    private static List<SelectedPassage> Start(IReadOnlyList<DocumentPassage> passages, double[] scores, PassageSelectionOptions limits)
    {
        var chosen = new List<SelectedPassage>();
        var used = 0;
        for (var index = 0; index < passages.Count && chosen.Count < limits.MaxPassages; index++)
        {
            var passage = passages[index];
            var cost = passage.Text.Length - (chosen.Count > 0 ? passage.OverlapLength : 0);
            if (used + cost > limits.MaxCharacters)
            {
                if (chosen.Count == 0)
                {
                    chosen.Add(new SelectedPassage(Cut(passage, limits.MaxCharacters), scores[index]));
                }

                break;
            }

            chosen.Add(new SelectedPassage(passage, scores[index]));
            used += cost;
        }

        return chosen;
    }

    // The passages at a place the question names and those that hold the words it looks for, unless they hold little of them next
    // to the best, best first while they fit, then put in the order of the document.
    private static List<SelectedPassage> Best(
        IReadOnlyList<DocumentPassage> passages, double[] scores, bool[] placed, PassageSelectionOptions limits)
    {
        var bestWords = 0.0;
        for (var index = 0; index < passages.Count; index++)
        {
            bestWords = Math.Max(bestWords, scores[index] - (placed[index] ? PlaceScore : 0));
        }

        var ranked = Enumerable.Range(0, passages.Count)
            .Where(index => placed[index] || (scores[index] > 0 && scores[index] >= MinShareOfBestScore * bestWords))
            .OrderByDescending(index => scores[index]).ThenBy(index => index).ToList();
        var selected = new bool[passages.Count];
        var used = 0;
        var count = 0;
        foreach (var index in ranked)
        {
            if (count >= limits.MaxPassages)
            {
                break;
            }

            var cost = passages[index].Text.Length;
            if (index > 0 && selected[index - 1])
            {
                cost -= passages[index].OverlapLength;
            }

            if (index + 1 < passages.Count && selected[index + 1])
            {
                cost -= passages[index + 1].OverlapLength;
            }

            cost = Math.Max(cost, 0);
            if (used + cost > limits.MaxCharacters)
            {
                continue;
            }

            selected[index] = true;
            used += cost;
            count++;
        }

        if (count == 0)
        {
            // Nothing fits: the best passage, cut to fit, is better than nothing.
            var best = ranked[0];
            return [new SelectedPassage(Cut(passages[best], limits.MaxCharacters), scores[best])];
        }

        var chosen = new List<SelectedPassage>(count);
        for (var index = 0; index < passages.Count; index++)
        {
            if (selected[index])
            {
                chosen.Add(new SelectedPassage(passages[index], scores[index]));
            }
        }

        return chosen;
    }

    // A passage shortened to at most `limit` characters, where a paragraph, a sentence or a word ends when one is near.
    private static DocumentPassage Cut(DocumentPassage passage, int limit)
    {
        if (passage.Text.Length <= limit)
        {
            return passage;
        }

        var end = TextBreaks.FindEnd(passage.Text, 0, limit);
        var text = passage.Text[..end].TrimEnd();
        return passage with { Text = text, OverlapLength = Math.Min(passage.OverlapLength, Math.Max(text.Length - 1, 0)) };
    }

    // How well each passage answers the question, and whether it is at a place the question names; all zero, and nowhere, when the
    // question gives nothing to look for.
    private static (double[] Scores, bool[] Placed) Score(IReadOnlyList<DocumentPassage> passages, QuestionTerms query)
    {
        var scores = new double[passages.Count];
        var placed = new bool[passages.Count];
        if (query.IsEmpty)
        {
            return (scores, placed);
        }

        if (query.Terms.Count > 0)
        {
            ScoreTerms(passages, query.Terms, scores);
        }

        for (var index = 0; index < passages.Count; index++)
        {
            var passage = passages[index];
            if (query.Places.Any(place => place.Covers(passage.Location, passage.EndLocation)))
            {
                scores[index] += PlaceScore;
                placed[index] = true;
            }
        }

        return (scores, placed);
    }

    // BM25 over the passages, plus a share of the weight of the pairs of neighbouring words of the question that stand together.
    private static void ScoreTerms(IReadOnlyList<DocumentPassage> passages, IReadOnlyList<string> terms, double[] scores)
    {
        var termIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var term = 0; term < terms.Count; term++)
        {
            termIndex[terms[term]] = term;
        }

        var streams = new int[passages.Count][];
        var counts = new int[passages.Count][];
        var documentFrequency = new int[terms.Count];
        long totalLength = 0;
        for (var index = 0; index < passages.Count; index++)
        {
            var passage = passages[index];

            // The words of the heading or title of the place count as part of the passage.
            var words = TermExtractor.Extract(passage.Location.Label, skipStopWords: true);
            words.AddRange(TermExtractor.Extract(passage.Text, skipStopWords: true));
            var stream = new int[words.Count];
            var count = new int[terms.Count];
            for (var position = 0; position < words.Count; position++)
            {
                stream[position] = termIndex.GetValueOrDefault(words[position], -1);
                if (stream[position] >= 0)
                {
                    count[stream[position]]++;
                }
            }

            for (var term = 0; term < terms.Count; term++)
            {
                if (count[term] > 0)
                {
                    documentFrequency[term]++;
                }
            }

            streams[index] = stream;
            counts[index] = count;
            totalLength += stream.Length;
        }

        var averageLength = Math.Max(1.0, (double)totalLength / passages.Count);
        var idf = new double[terms.Count];
        for (var term = 0; term < terms.Count; term++)
        {
            idf[term] = Math.Log(1 + ((passages.Count - documentFrequency[term] + 0.5) / (documentFrequency[term] + 0.5)));
        }

        for (var index = 0; index < passages.Count; index++)
        {
            var lengthNorm = 1 - B + (B * streams[index].Length / averageLength);
            var score = 0.0;
            for (var term = 0; term < terms.Count; term++)
            {
                var frequency = counts[index][term];
                if (frequency > 0)
                {
                    score += idf[term] * frequency * (K1 + 1) / (frequency + (K1 * lengthNorm));
                }
            }

            for (var term = 0; term + 1 < terms.Count; term++)
            {
                if (counts[index][term] > 0 && counts[index][term + 1] > 0 && Adjacent(streams[index], term, term + 1))
                {
                    score += PhraseWeight * (idf[term] + idf[term + 1]);
                }
            }

            scores[index] = score;
        }
    }

    private static bool Adjacent(int[] stream, int first, int second)
    {
        for (var position = 0; position + 1 < stream.Length; position++)
        {
            if (stream[position] == first && stream[position + 1] == second)
            {
                return true;
            }
        }

        return false;
    }
}
