using System.Text;
using Assistant.Core.Documents;
using Assistant.Documents.Context;
using Xunit;

namespace Assistant.Documents.Tests;

/// <summary>
/// Normalizing a document's text, cutting it into passages with overlap, and saying where each passage is (PROJECT_SPEC §4.7):
/// the same text and options always give the same passages, and nothing of the text is lost.
/// </summary>
public sealed class DocumentContextBuilderTests
{
    private static readonly DocumentContextBuilder Builder = new();

    // Small passages, so a few paragraphs make several.
    private static readonly DocumentChunkingOptions Small = new() { TargetCharacters = 300, OverlapCharacters = 50, MinCharacters = 100 };

    private static DocumentReadResult Read(bool truncated = false, params DocumentSegment[] segments) =>
        new() { Status = DocumentReadStatus.Success, Segments = segments, Truncated = truncated };

    private static DocumentSegment Piece(DocumentLocation location, string text) => new(location, text);

    // Sentences that are all different, so a cut in the wrong place shows.
    private static string Sentences(int count, int firstNumber = 1)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            builder.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Sentence number {firstNumber + i} talks about topic {(firstNumber + i) * 7} in some detail. "));
        }

        return builder.ToString().TrimEnd();
    }

    private static string WithoutSpace(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    // The text the passages of one piece say, the overlap once.
    private static string Reassemble(IEnumerable<DocumentPassage> passages)
    {
        var text = new StringBuilder();
        foreach (var passage in passages)
        {
            text.Append(text.Length == 0 ? passage.Text : passage.Text[passage.OverlapLength..]).Append(' ');
        }

        return text.ToString();
    }

    // -- Normalizing. --

    [Fact]
    public void TheTextIsNormalizedBeforeItIsCut()
    {
        var noBreakSpace = ((char)0xA0).ToString();
        var zeroWidth = ((char)0x200B).ToString();
        var context = Builder.Build($"One  two\t\tthree{zeroWidth}\r\nfour{noBreakSpace}{noBreakSpace}five   \r\n\r\n\r\n\r\n    indented  and   spaced\r\n");

        var passage = Assert.Single(context.Passages);
        Assert.Equal("One two\tthree\nfour five\n\n    indented and spaced", passage.Text);
    }

    [Fact]
    public void NormalizingAgainChangesNothing()
    {
        var once = DocumentTextNormalizer.Normalize("a  b\t c\r\n\r\n\r\n   d    e  \n");

        Assert.Equal(once, DocumentTextNormalizer.Normalize(once));
        Assert.Equal("a b\tc\n\n   d e", once);
    }

    [Fact]
    public void ADocumentWithNoTextOrThatWasNotReadHasNoPassages()
    {
        Assert.Empty(Builder.Build("").Passages);
        Assert.Empty(Builder.Build("  \r\n\t ").Passages);
        Assert.Empty(Builder.Build((string?)null).Passages);
        Assert.Empty(Builder.Build(DocumentReadResult.Failed(DocumentReadStatus.NoText)).Passages);
        Assert.Empty(Builder.Build(Read(false)).Passages);
        Assert.Empty(Builder.Build(Read(false, Piece(DocumentLocation.ForPage(1), " \n "))).Passages);
    }

    // -- One passage, or several. --

    [Fact]
    public void AShortDocumentIsOnePassageWithItsPlace()
    {
        var page = DocumentLocation.ForPage(3);
        var context = Builder.Build(Read(false, Piece(page, "A short page.")));

        var passage = Assert.Single(context.Passages);
        Assert.Equal(0, passage.Index);
        Assert.Equal("A short page.", passage.Text);
        Assert.Equal(page, passage.Location);
        Assert.Equal(page, passage.EndLocation);
        Assert.Equal(0, passage.OverlapLength);
        Assert.Equal("page 3", passage.Describe());
        Assert.Equal("A short page.".Length, context.SourceCharacters);
        Assert.False(context.Truncated);
    }

    [Fact]
    public void ALongTextIsCutIntoPassagesOfAboutTheTargetThatOverlapAndLoseNothing()
    {
        var text = Sentences(60);
        var context = Builder.Build(text, Small);

        Assert.True(context.Passages.Count > 5);
        Assert.Equal(Enumerable.Range(0, context.Passages.Count), context.Passages.Select(passage => passage.Index));

        // No passage is longer than the target and the smallest passage together; all but the last are about the target.
        Assert.All(context.Passages, passage => Assert.InRange(passage.Text.Length, 1, 400));
        Assert.All(context.Passages.SkipLast(1), passage => Assert.InRange(passage.Text.Length, 190, 400));

        // Each passage after the first starts with the end of the one before it, at a word, and never with more than the overlap.
        foreach (var (previous, passage) in context.Passages.Zip(context.Passages.Skip(1)))
        {
            Assert.InRange(passage.OverlapLength, 1, 50);
            Assert.EndsWith(passage.Text[..passage.OverlapLength], previous.Text, StringComparison.Ordinal);
            Assert.True(passage.OverlapLength == passage.Text.Length || !char.IsWhiteSpace(passage.Text[0]));
        }

        // Nothing of the text is lost: the passages say all of it, the overlap once.
        Assert.Equal(WithoutSpace(text), WithoutSpace(Reassemble(context.Passages)));
        Assert.Equal(text.Length, context.SourceCharacters);
    }

    [Fact]
    public void TheSameTextAndOptionsAlwaysGiveTheSamePassages()
    {
        var read = Read(false,
            Piece(DocumentLocation.ForPage(1), Sentences(40)),
            Piece(DocumentLocation.ForPage(2), "Short."),
            Piece(DocumentLocation.ForPage(3), Sentences(25, 100)));

        var first = Builder.Build(read, Small);
        var second = new DocumentContextBuilder().Build(read, Small);

        Assert.Equal(first.Passages, second.Passages);
        Assert.Equal(first.SourceCharacters, second.SourceCharacters);
    }

    [Fact]
    public void ACutIsAtTheParagraphBreakWhenOneIsNear_ElseAtTheEndOfASentence_ElseAtALine_ElseBetweenWords()
    {
        var options = new DocumentChunkingOptions { TargetCharacters = 100, OverlapCharacters = 0, MinCharacters = 20 };

        // A paragraph break at 85 beats the sentence end at 95.
        var paragraph = new string('a', 84) + ".\n\n" + new string('b', 9) + ". " + new string('c', 60);
        Assert.Equal(new string('a', 84) + ".", Builder.Build(paragraph, options).Passages[0].Text);

        // No paragraph break: the end of the sentence at 75 beats the line break at 90 and the space at 95.
        var sentence = new string('a', 74) + ". " + new string('b', 12) + "\n" + new string('c', 3) + " " + new string('d', 60);
        Assert.Equal(new string('a', 74) + ".", Builder.Build(sentence, options).Passages[0].Text);

        // No sentence end: the line break.
        var line = new string('a', 80) + "\n" + new string('b', 10) + " " + new string('c', 60);
        Assert.Equal(new string('a', 80), Builder.Build(line, options).Passages[0].Text);

        // Nothing but words: the last space that fits.
        var words = new string('a', 60) + " " + new string('b', 30) + " " + new string('c', 60);
        Assert.Equal(new string('a', 60) + " " + new string('b', 30), Builder.Build(words, options).Passages[0].Text);
    }

    [Fact]
    public void ABreakFartherBackThanTheLastThirdIsNotUsed()
    {
        var options = new DocumentChunkingOptions { TargetCharacters = 120, OverlapCharacters = 0, MinCharacters = 20 };
        // The only paragraph break is at 30, in the first part of the target: the cut is at the last space that fits instead.
        var text = new string('a', 30) + "\n\n" + new string('b', 40) + " " + new string('c', 40) + " " + new string('d', 60);

        var first = Builder.Build(text, options).Passages[0].Text;

        Assert.StartsWith(new string('a', 30) + "\n\n" + new string('b', 40), first, StringComparison.Ordinal);
        Assert.True(first.Length > 100);
    }

    [Fact]
    public void ATextWithNoSpacesIsCutWhereItIs_AndNeverThroughAPairOfSurrogates()
    {
        var options = new DocumentChunkingOptions { TargetCharacters = 100, OverlapCharacters = 20, MinCharacters = 20 };
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);

        var plain = Builder.Build(new string('x', 1000), options);
        Assert.True(plain.Passages.Count > 8);
        Assert.All(plain.Passages.Skip(1), passage => Assert.InRange(passage.OverlapLength, 1, 20));

        var rocket = char.ConvertFromUtf32(0x1F680);
        var emoji = Builder.Build(string.Concat(Enumerable.Repeat(rocket, 500)), options);
        Assert.True(emoji.Passages.Count > 8);
        foreach (var passage in emoji.Passages)
        {
            // Encoding throws on a lone surrogate.
            Assert.Equal(passage.Text.Length, strict.GetString(strict.GetBytes(passage.Text)).Length);
            Assert.Equal(0, passage.Text.Length % 2);
        }
    }

    [Fact]
    public void WithNoOverlapThePassagesFollowEachOtherExactly()
    {
        var text = Sentences(40);
        var context = Builder.Build(text, new DocumentChunkingOptions { TargetCharacters = 300, OverlapCharacters = 0, MinCharacters = 100 });

        Assert.All(context.Passages, passage => Assert.Equal(0, passage.OverlapLength));
        Assert.Equal(WithoutSpace(text), WithoutSpace(string.Concat(context.Passages.Select(passage => passage.Text))));
    }

    [Fact]
    public void WhatIsLeftOverAtTheEndIsJoinedToThePassageBeforeItAndNeverLeavesATinyPassage()
    {
        // The piece is 20 characters more than the target and the smallest passage together would leave a tail too small.
        var options = new DocumentChunkingOptions { TargetCharacters = 300, OverlapCharacters = 40, MinCharacters = 100 };
        for (var extra = 0; extra < 60; extra += 7)
        {
            var text = Sentences(1).PadRight(380 + extra, 'z').Replace("z", "zz ");
            var context = Builder.Build(text, options);
            Assert.All(context.Passages, passage => Assert.True(passage.Text.Length <= 400, $"{passage.Text.Length} characters"));
            Assert.All(context.Passages.Skip(1), passage => Assert.True(passage.Text.Length - passage.OverlapLength >= 100 - 1 || passage == context.Passages[^1]));
        }
    }

    // -- Where each passage is. --

    [Fact]
    public void EveryPassageIsInOnePlaceAndAPageIsNeverMixedWithTheNext()
    {
        var read = Read(false,
            Piece(DocumentLocation.ForPage(1), Sentences(30)),
            Piece(DocumentLocation.ForPage(2), Sentences(30, 500)));

        var context = Builder.Build(read, Small);

        var pageOne = context.Passages.Where(passage => passage.Location.Number == 1).ToList();
        var pageTwo = context.Passages.Where(passage => passage.Location.Number == 2).ToList();
        Assert.True(pageOne.Count > 2 && pageTwo.Count > 2);
        Assert.Equal(context.Passages.Count, pageOne.Count + pageTwo.Count);
        Assert.All(context.Passages, passage => Assert.Equal(passage.Location, passage.EndLocation));

        // The passages are in the order of the document, and the first of a page repeats nothing of the page before.
        Assert.Equal(pageOne.Concat(pageTwo), context.Passages);
        Assert.Equal(0, pageTwo[0].OverlapLength);
        Assert.DoesNotContain("number 500", string.Concat(pageOne.Select(passage => passage.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void ShortPiecesAreGatheredWithTheOnesAfterThemAndMarkedWhereEachStarts()
    {
        var options = new DocumentChunkingOptions { TargetCharacters = 300, OverlapCharacters = 0, MinCharacters = 150 };
        var risks = "Supply of parts is the main risk to the plan in the next quarter, and the team watches it every week. " +
            "The second risk is the price of steel, which has moved by a tenth in two months and may move again soon.";
        var read = Read(false,
            Piece(DocumentLocation.ForSlide(1, "Agenda"), "Budget review and plans."),
            Piece(DocumentLocation.ForSlide(2, "Budget"), "Revenue grew by twelve percent in the year."),
            Piece(DocumentLocation.ForSlide(3), "Costs fell."),
            Piece(DocumentLocation.ForSlide(4, "Risks"), risks),
            Piece(DocumentLocation.ForSlide(5), "Questions?"));

        var context = Builder.Build(read, options);

        // Slides 1 to 3 are together, each under the smallest passage, until slide 4 would not fit; slide 4 is long enough to
        // stand alone, and slide 5 has nothing after it to join.
        Assert.Equal(3, context.Passages.Count);
        var first = context.Passages[0];
        Assert.Equal(DocumentLocation.ForSlide(1, "Agenda"), first.Location);
        Assert.Equal(DocumentLocation.ForSlide(3), first.EndLocation);
        Assert.Equal("slides 1-3", first.Describe());
        Assert.Equal("slides 1-3 (Agenda)", first.Header);
        Assert.Equal(
            "Budget review and plans.\n\n[Slide 2: Budget]\nRevenue grew by twelve percent in the year.\n\n[Slide 3]\nCosts fell.",
            first.Text);
        Assert.Equal(DocumentLocation.ForSlide(4, "Risks"), context.Passages[1].Location);
        Assert.Equal(risks, context.Passages[1].Text);
        Assert.Equal("slide 5", context.Passages[2].Describe());
    }

    [Fact]
    public void APieceThatIsNotShortStandsAloneAndAGatheringStopsAtTheTarget()
    {
        var options = new DocumentChunkingOptions { TargetCharacters = 250, OverlapCharacters = 0, MinCharacters = 150 };
        var pieces = Enumerable.Range(1, 10)
            .Select(number => Piece(DocumentLocation.ForSlide(number), new string((char)('a' + number), 100)))
            .ToArray();

        var context = Builder.Build(Read(false, pieces), options);

        Assert.Equal(5, context.Passages.Count);
        Assert.All(context.Passages, passage => Assert.InRange(passage.Text.Length, 100, 250));

        // A piece as long as the smallest passage is one passage by itself.
        var alone = Builder.Build(Read(false,
            Piece(DocumentLocation.ForSection(1), new string('a', 160)), Piece(DocumentLocation.ForSection(2), "Tiny.")), options);
        Assert.Equal([1, 2], alone.Passages.Select(passage => passage.Location.Number));
    }

    [Fact]
    public void ALongPieceIsNotGatheredWithTheShortOneThatFollowsIt()
    {
        var read = Read(false,
            Piece(DocumentLocation.ForSection(1, "Long"), Sentences(20)),
            Piece(DocumentLocation.ForSection(2, "Short"), "Tiny."));

        var context = Builder.Build(read, Small);

        Assert.Equal(DocumentLocation.ForSection(2, "Short"), context.Passages[^1].Location);
        Assert.Equal("Tiny.", context.Passages[^1].Text);
    }

    [Fact]
    public void ALongPassageOfATextFileKnowsWhichLinesItIsOn()
    {
        var lines = Enumerable.Range(1, 60).Select(number => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"line {number:00} says something useful")).ToArray();
        var text = string.Join('\n', lines);
        var read = Read(false, Piece(DocumentLocation.WholeDocument(1, 60), text));

        var context = Builder.Build(read, Small);

        Assert.True(context.Passages.Count > 4);
        foreach (var passage in context.Passages)
        {
            // A passage starts and ends at a word, so its first and last lines may be parts of lines of the file.
            var first = passage.Location.FirstLine!.Value;
            var last = passage.Location.LastLine!.Value;
            var passageLines = passage.Text.Split('\n');
            Assert.EndsWith(passageLines[0], lines[first - 1], StringComparison.Ordinal);
            Assert.StartsWith(passageLines[^1], lines[last - 1], StringComparison.Ordinal);
            Assert.Equal(last - first, passage.Text.Count(c => c == '\n'));
        }

        Assert.Equal(1, context.Passages[0].Location.FirstLine);
        Assert.Equal(60, context.Passages[^1].Location.LastLine);
        Assert.Matches(@"^document, lines \d+-\d+$", context.Passages[1].Describe());
    }

    [Fact]
    public void WhenALongPiecesLineNumbersDoNotAgreeWithItsTextItsPassagesKeepTheLinesOfTheWholePiece()
    {
        // The reader counted 100 lines in the file, but the text has fewer (blank lines were collapsed).
        var text = string.Join('\n', Enumerable.Range(1, 40).Select(number => $"line {number} says something useful"));
        var piece = DocumentLocation.ForSection(2, "Install", 10, 109);

        var context = Builder.Build(Read(false, Piece(piece, text)), Small);

        Assert.True(context.Passages.Count > 2);
        Assert.All(context.Passages, passage => Assert.Equal(piece, passage.Location));
    }

    [Fact]
    public void AGatheredPassageOfATextFileHasTheFirstAndLastLinesOfItsPieces()
    {
        var read = Read(false,
            Piece(DocumentLocation.ForSection(1, "A", 1, 3), "one\ntwo\nthree"),
            Piece(DocumentLocation.ForSection(2, "B", 4, 5), "four\nfive"));

        var passage = Assert.Single(Builder.Build(read, Small).Passages);

        Assert.Equal("sections 1-2, lines 1-5", passage.Describe());
    }

    // -- Limits. --

    [Fact]
    public void ThePassagesStopAtTheLimitAndTheDocumentIsSaidToBeCut()
    {
        var options = new DocumentChunkingOptions { TargetCharacters = 300, OverlapCharacters = 50, MinCharacters = 100, MaxPassages = 4 };

        var one = Builder.Build(Sentences(60), options);
        var many = Builder.Build(Read(false,
            Piece(DocumentLocation.ForPage(1), Sentences(30)), Piece(DocumentLocation.ForPage(2), Sentences(30)),
            Piece(DocumentLocation.ForPage(3), Sentences(30))), options);

        Assert.Equal(4, one.Passages.Count);
        Assert.True(one.Truncated);
        Assert.Equal(4, many.Passages.Count);
        Assert.True(many.Truncated);

        // Exactly as many as allowed is not cut.
        var exact = Builder.Build(Sentences(6), options);
        Assert.True(exact.Passages.Count <= 4);
    }

    [Fact]
    public void WhatTheReaderLeftOutIsStillSaidToBeLeftOut()
    {
        var context = Builder.Build(Read(true, Piece(DocumentLocation.ForPage(1), "Some text.")));

        Assert.True(context.Truncated);
        Assert.Single(context.Passages);
    }

    [Fact]
    public void TheOptionsAreBroughtIntoRange()
    {
        var defaults = DocumentChunkingOptions.Default.Resolve();
        Assert.Equal((1200, 150, 250, 5000), (defaults.TargetCharacters, defaults.OverlapCharacters, defaults.MinCharacters, defaults.MaxPassages));

        var unset = new DocumentChunkingOptions { TargetCharacters = 0, OverlapCharacters = -1, MinCharacters = 0, MaxPassages = 0 }.Resolve();
        Assert.Equal((1200, 150, 250, 5000), (unset.TargetCharacters, unset.OverlapCharacters, unset.MinCharacters, unset.MaxPassages));

        // Zero overlap is no overlap; the overlap is at most a third of the target and the smallest passage half of it.
        var none = new DocumentChunkingOptions { OverlapCharacters = 0 }.Resolve();
        Assert.Equal(0, none.OverlapCharacters);
        var tight = new DocumentChunkingOptions { TargetCharacters = 300, OverlapCharacters = 280, MinCharacters = 280 }.Resolve();
        Assert.Equal((300, 100, 150), (tight.TargetCharacters, tight.OverlapCharacters, tight.MinCharacters));
        Assert.Equal(DocumentChunkingOptions.SmallestTargetCharacters, new DocumentChunkingOptions { TargetCharacters = 3 }.Resolve().TargetCharacters);
    }

    [Fact]
    public void ALargeDocumentIsCutQuicklyAndCompletely()
    {
        var read = Read(false, Enumerable.Range(0, 20).Select(page => Piece(DocumentLocation.ForPage(page + 1), Sentences(1500, page * 2000))).ToArray());

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var context = Builder.Build(read);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);

        Assert.True(read.CharacterCount > 1_000_000);
        Assert.True(context.Passages.Count > 1000);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), elapsed.ToString());
    }

    // -- Privacy. --

    [Fact]
    public void WhatAPassageOrAContextPrintsHoldsNoText()
    {
        var context = Builder.Build(Read(false, Piece(DocumentLocation.ForSection(1, "SECRET-HEADING"), "SECRET-BODY " + Sentences(30))), Small);

        Assert.DoesNotContain("SECRET", context.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", context.Passages[0].ToString(), StringComparison.Ordinal);
        Assert.Contains("Passages =", context.ToString(), StringComparison.Ordinal);
        Assert.Contains("Length =", context.Passages[0].ToString(), StringComparison.Ordinal);
    }
}
