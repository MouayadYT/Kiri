namespace Assistant.Core.Calendar;

/// <summary>How likely it is that a calendar event is an exam.</summary>
public enum ExamLikelihood
{
    /// <summary>Nothing about the event says it is an exam.</summary>
    NotAnExam = 0,

    /// <summary>It might be: it mentions an exam or a test without being clear (a "final" with no subject, a medical exam, a study session for an exam).</summary>
    Possible = 1,

    /// <summary>It reads as an exam: the title or the place says so.</summary>
    Likely = 2,
}

/// <summary>What the check of an event's words found.</summary>
/// <param name="Likelihood">How likely it is an exam.</param>
/// <param name="Reason">Why, in a few words the model and the user can read ("the title says exam"); empty for <see cref="ExamLikelihood.NotAnExam"/>.</param>
public readonly record struct ExamAssessment(ExamLikelihood Likelihood, string Reason)
{
    /// <summary>Nothing says it is an exam.</summary>
    public static ExamAssessment None { get; } = new(ExamLikelihood.NotAnExam, string.Empty);

    /// <summary>Whether it is at least possibly an exam.</summary>
    public bool IsCandidate => Likelihood != ExamLikelihood.NotAnExam;
}

/// <summary>
/// Tells from the words of a calendar event whether it is probably an exam (PROJECT_SPEC §4.8, step 116), by fixed word lists and nothing else: no model, no network, the same
/// answer every time. A calendar event has no "exam" field, so the title, the place and the notes are all there is: "Physics final exam" and "Calculus midterm" are exams,
/// "Dentist" is not, and what is only <i>about</i> an exam ("Exam review session", "Study for the chemistry test") or may be another kind (an "eye exam", a "final" with no subject, a
/// "test meeting") is <see cref="ExamLikelihood.Possible"/>, so that the Assistant can say what it was unsure of and never counts it as sure. A word in the title or the place decides;
/// a word only in the notes is a possibility ("bring a pencil, exam notes"). It is a hint to the model and the user and decides nothing on its own: whatever the Assistant then
/// does with the events is shown to the user first.
/// </summary>
public static class ExamDetector
{
    // Words that say an exam, whatever else the event says.
    private static readonly HashSet<string> Strong = new(StringComparer.Ordinal)
    {
        "exam", "exams", "examination", "examinations", "midterm", "midterms", "finals", "viva", "vivas",
    };

    // Words for a test that is an exam when the event is about a school subject, and may be something else when it is not.
    private static readonly HashSet<string> TestWords = new(StringComparer.Ordinal)
    {
        "test", "tests", "quiz", "quizzes", "assessment", "assessments", "final",
    };

    // Words for the subjects and the school that make a "test" or a "final" an exam.
    private static readonly HashSet<string> Academic = new(StringComparer.Ordinal)
    {
        "math", "maths", "mathematics", "calculus", "algebra", "geometry", "trigonometry", "statistics", "physics", "chemistry", "chem", "biology", "bio", "history", "geography",
        "english", "literature", "grammar", "vocabulary", "french", "spanish", "german", "arabic", "latin", "economics", "accounting", "psychology", "sociology", "philosophy",
        "anatomy", "course", "class", "lecture", "semester", "term", "module", "chapter", "school", "university", "college", "written", "oral", "theory", "lab",
        "organic", "biochemistry", "engineering", "programming", "cs", "nursing", "law", "driving", "language",
    };

    // Words that say the event is the work before an exam and not the exam.
    private static readonly HashSet<string> Preparation = new(StringComparer.Ordinal)
    {
        "review", "revision", "revise", "revising", "study", "studying", "prep", "preparation", "prepare", "cram", "tutoring", "tutorial", "practice", "mock", "session", "workshop", "recap",
    };

    // Words that make "exam" a medical one and "test" a thing that is not school at all.
    private static readonly HashSet<string> Medical = new(StringComparer.Ordinal)
    {
        "physical", "medical", "eye", "dental", "health", "annual", "vet", "checkup", "pelvic", "prostate", "skin", "hearing", "vision", "blood", "doctor", "clinic", "gp",
    };

    private static readonly HashSet<string> NotSchoolTests = new(StringComparer.Ordinal)
    {
        "drive", "covid", "blood", "eye", "hearing", "pregnancy", "allergy", "unit", "pen", "penetration", "beta", "load", "smoke", "regression", "software", "code", "build", "deploy",
        "deployment", "case", "cases", "run", "trivia", "pub", "stress", "dna", "paternity", "urine", "swab", "pcr", "rapid", "automation", "qa", "api", "release", "sprint", "night",
    };

    /// <summary>Whether <paramref name="item"/> is probably an exam, from its title, place and notes.</summary>
    public static ExamAssessment Assess(CalendarEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Assess(item.Title, item.Location, item.Notes);
    }

    /// <summary>Whether an event with this <paramref name="title"/>, <paramref name="location"/> and <paramref name="notes"/> is probably an exam.</summary>
    public static ExamAssessment Assess(string? title, string? location = null, string? notes = null)
    {
        var inTitle = Words(title);
        var inPlace = Words(location);
        var inNotes = Words(notes);

        var titleStrong = inTitle.Any(Strong.Contains);
        var placeStrong = inPlace.Any(Strong.Contains);
        var notesStrong = inNotes.Any(Strong.Contains);
        var testInTitle = inTitle.Any(TestWords.Contains);

        if (titleStrong || placeStrong)
        {
            var where = titleStrong ? "title" : "place";
            if (inTitle.Any(Preparation.Contains))
            {
                return new ExamAssessment(ExamLikelihood.Possible, "it mentions an exam but looks like preparation for one");
            }

            if (inTitle.Concat(inPlace).Any(Medical.Contains) && !inTitle.Concat(inPlace).Any(Academic.Contains))
            {
                return new ExamAssessment(ExamLikelihood.Possible, "it says exam but may be a medical one");
            }

            return new ExamAssessment(ExamLikelihood.Likely, $"the {where} says exam");
        }

        if (testInTitle)
        {
            var word = inTitle.First(TestWords.Contains);
            if (inTitle.Any(NotSchoolTests.Contains) && !inTitle.Any(Academic.Contains))
            {
                return ExamAssessment.None;
            }

            if (inTitle.Any(Preparation.Contains))
            {
                return new ExamAssessment(ExamLikelihood.Possible, "it mentions a test but looks like preparation for one");
            }

            if (inTitle.Any(Academic.Contains) || (word is "quiz" or "quizzes" && !inTitle.Any(NotSchoolTests.Contains)))
            {
                return new ExamAssessment(ExamLikelihood.Likely, $"the title says {word} for a school subject");
            }

            return new ExamAssessment(ExamLikelihood.Possible, $"the title says {word} but not what for");
        }

        if (notesStrong)
        {
            return new ExamAssessment(ExamLikelihood.Possible, "the notes mention an exam");
        }

        return inNotes.Any(TestWords.Contains) && inNotes.Any(Academic.Contains)
            ? new ExamAssessment(ExamLikelihood.Possible, "the notes mention a test")
            : ExamAssessment.None;
    }

    // The words of a text: lower case, letters and digits; "mid-term" and "mid term" are one word, so are "pre-exam" and the like (the part after the dash stands).
    private static List<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return words;
        }

        var current = new System.Text.StringBuilder();
        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
            }
            else if (character is '\'' or '’')
            {
                // "what's" is one word.
            }
            else
            {
                Flush();
            }
        }

        Flush();

        // "mid term" and "mid-term" read as "midterm".
        for (var index = 0; index + 1 < words.Count; index++)
        {
            if (words[index] == "mid" && words[index + 1] is "term" or "terms")
            {
                words[index] = "midterm";
                words.RemoveAt(index + 1);
            }
        }

        return words;
    }
}
