using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Core.QuickSearch.Routing;

/// <summary>
/// Recognises straightforward arithmetic in what is typed (PROJECT_SPEC §4.1): numbers, <c>+ - * / ^ %</c> (and the signs <c>x</c>,
/// <c>×</c> and <c>÷</c> for multiplication and division) and parentheses, with at least two numbers and an operator between them, and,
/// before it, a word that asks for the sum ("what is", "calculate") or, after it, an equals sign. It only recognises: it does not work
/// anything out, which is the calculator's job. A date, a phone number, a version, or words with numbers in them are not arithmetic.
/// </summary>
public static partial class ArithmeticExpression
{
    private const int MaxLength = 120;

    // Words that may stand before a sum; "what is 9+10", "calc 3*4", "= 5+5".
    [GeneratedRegex(@"^\s*(?:what\s+is|what'?s|whats|calculate|calc|compute|solve|=)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Lead();

    [GeneratedRegex(@"\s*[=?]+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Tail();

    // A date (2024-10-02, 10/2/2026, 2.10.2026) or a phone number (555-123-4567): digits and one separator, no spaces.
    [GeneratedRegex(@"^\d{1,4}([-/.])\d{1,2}\1\d{1,4}$|^\d{2,}(?:-\d{2,}){2,}$|^\d+(?:\.\d+){2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateOrPhone();

    [GeneratedRegex(@"\d{1,3}(?:,\d{3})+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex ThousandsGrouped();

    /// <summary>
    /// Reads <paramref name="text"/> as arithmetic.
    /// </summary>
    /// <param name="text">What was typed.</param>
    /// <param name="expression">
    /// The sum with its words and equals sign taken off, operators written as <c>+ - * / ^ %</c> and one space around each binary one
    /// (<c>9 + 10</c>), when it is arithmetic.
    /// </param>
    /// <returns>Whether it is straightforward arithmetic.</returns>
    public static bool TryRead(string? text, out string expression)
    {
        expression = "";
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            return false;
        }

        var body = Tail().Replace(Lead().Replace(text, ""), "").Trim();
        if (body.Length == 0 || DateOrPhone().IsMatch(body))
        {
            return false;
        }

        // A comma is a thousands separator only inside a number grouped by threes ("1,000"), and is then dropped.
        body = ThousandsGrouped().Replace(body, match => match.Value.Replace(",", "", StringComparison.Ordinal));
        if (body.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var tokens = Tokenize(body);
        if (tokens is null || !IsSum(tokens, out var numbers, out var operators) || numbers < 2 || operators < 1)
        {
            return false;
        }

        expression = Format(tokens);
        return true;
    }

    private enum Kind { Number, Operator, Open, Close }

    private readonly record struct Token(Kind Kind, string Text);

    // The numbers, operators and parentheses, or null when anything else is in the text.
    private static List<Token>? Tokenize(string body)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < body.Length)
        {
            var c = body[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < body.Length && char.IsAsciiDigit(body[i + 1])))
            {
                var start = i;
                var dots = 0;
                while (i < body.Length && (char.IsAsciiDigit(body[i]) || body[i] == '.'))
                {
                    dots += body[i] == '.' ? 1 : 0;
                    i++;
                }

                if (dots > 1 || body[start..i].EndsWith('.'))
                {
                    return null;
                }

                tokens.Add(new Token(Kind.Number, body[start..i]));
            }
            else if (c is '+' or '-' or '*' or '/' or '^' or '%')
            {
                tokens.Add(new Token(Kind.Operator, c.ToString()));
                i++;
            }
            else if (c is '×' or 'x' or 'X' && IsBetweenOperands(tokens, body, i))
            {
                tokens.Add(new Token(Kind.Operator, "*"));
                i++;
            }
            else if (c == '÷')
            {
                tokens.Add(new Token(Kind.Operator, "/"));
                i++;
            }
            else if (c == '(')
            {
                tokens.Add(new Token(Kind.Open, "("));
                i++;
            }
            else if (c == ')')
            {
                tokens.Add(new Token(Kind.Close, ")"));
                i++;
            }
            else
            {
                return null;
            }
        }

        return tokens;
    }

    // An x is a times sign only with a number or a bracket before it and a number or a bracket after it: "3 x 4", never a word.
    private static bool IsBetweenOperands(List<Token> tokens, string body, int index)
    {
        if (tokens.Count == 0 || tokens[^1].Kind is not (Kind.Number or Kind.Close))
        {
            return false;
        }

        var next = index + 1;
        while (next < body.Length && char.IsWhiteSpace(body[next]))
        {
            next++;
        }

        return next < body.Length && (char.IsAsciiDigit(body[next]) || body[next] is '(' or '.');
    }

    // Whether the tokens read as a sum: an operand, then an operator and an operand, and so on, with brackets that match. A minus or
    // plus before an operand is a sign; a percent after an operand is the percent sign or the remainder.
    private static bool IsSum(List<Token> tokens, out int numbers, out int operators)
    {
        numbers = 0;
        operators = 0;
        var depth = 0;
        var expectOperand = true;
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case Kind.Number:
                    if (!expectOperand)
                    {
                        return false;
                    }

                    numbers++;
                    expectOperand = false;
                    break;
                case Kind.Open:
                    if (!expectOperand)
                    {
                        return false;
                    }

                    depth++;
                    break;
                case Kind.Close:
                    if (expectOperand || --depth < 0)
                    {
                        return false;
                    }

                    break;
                default:
                    if (expectOperand)
                    {
                        // A sign before an operand.
                        if (token.Text is not ("-" or "+"))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        operators++;
                        expectOperand = true;
                    }

                    break;
            }
        }

        return depth == 0 && !expectOperand;
    }

    // One space around each binary operator, none inside brackets, and a sign stays on its number: "-3 * (2 + 1)".
    private static string Format(List<Token> tokens)
    {
        var builder = new StringBuilder();
        var afterOperand = false;
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case Kind.Number:
                    builder.Append(token.Text);
                    afterOperand = true;
                    break;
                case Kind.Open:
                    // A bracket always follows an operator, a sign, another bracket or the start, never a number.
                    builder.Append('(');
                    afterOperand = false;
                    break;
                case Kind.Close:
                    builder.Append(')');
                    afterOperand = true;
                    break;
                default:
                    if (afterOperand)
                    {
                        builder.Append(' ').Append(token.Text).Append(' ');
                        afterOperand = false;
                    }
                    else
                    {
                        // A sign.
                        builder.Append(token.Text);
                    }

                    break;
            }
        }

        return builder.ToString().Trim();
    }
}
