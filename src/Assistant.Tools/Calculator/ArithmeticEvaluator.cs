using System.Globalization;
using System.Text;
using Assistant.Core.Tools;

namespace Assistant.Tools.Calculator;

/// <summary>
/// Works out a sum written with numbers, <c>+ - * / ^ %</c> and brackets (PROJECT_SPEC §4.8, <c>calculate</c>): a recursive-descent parser
/// over a fixed set of tokens, evaluated with <see cref="decimal"/> so that 0.1 + 0.2 is 0.3. There is no code in what it reads: it knows
/// no names, no functions, no variables and no way to call anything, so nothing the model writes can do more than be a number or fail
/// to be one. <c>%</c> is the remainder, as <c>10 % 3</c> is 1; <c>^</c> is a power, right to left, and binds tighter than a sign
/// (<c>-2^2</c> is -4); a sign may stand before any operand; <c>×</c>, <c>÷</c> and the minus sign <c>−</c> are read as <c>*</c>, <c>/</c>
/// and <c>-</c>, and an <c>x</c> between two operands is a times sign. A number may be written with thousands commas
/// (<c>1,000</c>) or with a decimal point; nothing else, no exponents and no implied multiplication (<c>2(3)</c>), is accepted, so that
/// nothing is ever guessed. It is bounded in length, depth and the size of an exponent, so no sum can take long.
/// </summary>
internal static class ArithmeticEvaluator
{
    /// <summary>The longest a sum may be, in characters.</summary>
    public const int MaxLength = 200;

    /// <summary>How deeply brackets, signs and powers may nest.</summary>
    public const int MaxDepth = 32;

    /// <summary>The most decimal places a value is shown with; a longer one is rounded, and the card says so.</summary>
    public const int MaxDecimals = 10;

    // An exponent of a whole number is worked out by squaring: nobody needs more, and nothing past this fits a decimal.
    private const int MaxIntegerExponent = 10_000;

    private const int MaxDigits = 29;

    // As many decimal places as a decimal has, none of them a trailing zero, and never an exponent.
    private static readonly string PlainFormat = "0." + new string('#', 28);

    /// <summary>Works out <paramref name="text"/>.</summary>
    /// <param name="text">The sum.</param>
    /// <param name="output">The sum as it is read (<c>9 + 10</c>), its value, and a note when the value was rounded.</param>
    /// <param name="problem">Why it could not be worked out, in words the model can pass on; <see langword="null"/> when it was.</param>
    /// <returns>Whether it was worked out.</returns>
    public static bool TryEvaluate(string? text, out CalculationOutput output, out string? problem)
    {
        output = new CalculationOutput(string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "There is nothing to work out. Give the sum, such as 9 + 10.";
            return false;
        }

        if (text.Length > MaxLength)
        {
            problem = $"The sum is too long: at most {MaxLength} characters.";
            return false;
        }

        try
        {
            var tokens = Tokenize(text);
            var value = new Parser(tokens).Parse();
            var (shown, rounded) = Show(value);
            output = new CalculationOutput(Format(tokens), shown, rounded ? $"Rounded to {MaxDecimals} decimal places." : null);
            problem = null;
            return true;
        }
        catch (CalculationException failure)
        {
            problem = failure.Message;
            return false;
        }
    }

    // ---- Reading ----

    private enum Kind
    {
        Number,
        Plus,
        Minus,
        Times,
        Divide,
        Remainder,
        Power,
        Open,
        Close,
    }

    private readonly record struct Token(Kind Kind, string Text, decimal Value = 0m)
    {
        public bool IsOperand => Kind is Kind.Number or Kind.Close;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < text.Length)
        {
            var c = text[index];
            if (char.IsWhiteSpace(c))
            {
                index++;
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && index + 1 < text.Length && char.IsAsciiDigit(text[index + 1])))
            {
                tokens.Add(ReadNumber(text, ref index));
            }
            else if (c is '+')
            {
                tokens.Add(new Token(Kind.Plus, "+"));
                index++;
            }
            else if (c is '-' or '−')
            {
                tokens.Add(new Token(Kind.Minus, "-"));
                index++;
            }
            else if (c is '*' or '×' || (c is 'x' or 'X' && IsBetweenOperands(tokens, text, index)))
            {
                tokens.Add(new Token(Kind.Times, "*"));
                index++;
            }
            else if (c is '/' or '÷')
            {
                tokens.Add(new Token(Kind.Divide, "/"));
                index++;
            }
            else if (c is '%')
            {
                tokens.Add(new Token(Kind.Remainder, "%"));
                index++;
            }
            else if (c is '^')
            {
                tokens.Add(new Token(Kind.Power, "^"));
                index++;
            }
            else if (c is '(')
            {
                tokens.Add(new Token(Kind.Open, "("));
                index++;
            }
            else if (c is ')')
            {
                tokens.Add(new Token(Kind.Close, ")"));
                index++;
            }
            else
            {
                throw new CalculationException(
                    $"Only numbers, + - * / ^ % and brackets can be worked out. The character at position {index + 1} is not one of them.");
            }
        }

        return tokens;
    }

    // An x is a times sign only with a number or a bracket before it and a number, a point or a bracket after it, never a word.
    private static bool IsBetweenOperands(List<Token> tokens, string text, int index)
    {
        if (tokens.Count == 0 || !tokens[^1].IsOperand)
        {
            return false;
        }

        var next = index + 1;
        while (next < text.Length && char.IsWhiteSpace(text[next]))
        {
            next++;
        }

        return next < text.Length && (char.IsAsciiDigit(text[next]) || text[next] is '(' or '.');
    }

    // A number: digits with an optional decimal point, and commas only as thousands separators ("1,000,000.5").
    private static Token ReadNumber(string text, ref int index)
    {
        var start = index;
        while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] is ',' or '.'))
        {
            index++;
        }

        var written = text[start..index];
        var digits = written;
        var point = written.IndexOf('.', StringComparison.Ordinal);
        if (point >= 0 && written.IndexOf('.', point + 1) >= 0)
        {
            throw new CalculationException("A number has only one decimal point.");
        }

        var whole = point >= 0 ? written[..point] : written;
        if (written.EndsWith('.'))
        {
            throw new CalculationException("A number cannot end with a decimal point.");
        }

        if (whole.Contains(',', StringComparison.Ordinal))
        {
            var groups = whole.Split(',');
            if (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(group => group.Length != 3) || (point >= 0 && written[point..].Contains(',', StringComparison.Ordinal)))
            {
                throw new CalculationException("A comma in a number is a thousands separator only, as in 1,000.");
            }

            digits = whole.Replace(",", string.Empty, StringComparison.Ordinal) + (point >= 0 ? written[point..] : string.Empty);
        }
        else if (point >= 0 && written[point..].Contains(',', StringComparison.Ordinal))
        {
            throw new CalculationException("A comma in a number is a thousands separator only, as in 1,000.");
        }

        if (digits.Count(char.IsAsciiDigit) > MaxDigits)
        {
            throw new CalculationException($"A number has at most {MaxDigits} digits.");
        }

        if (!decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            throw new CalculationException("That number is too large to work with.");
        }

        return new Token(Kind.Number, digits.StartsWith('.') ? "0" + digits : digits, value);
    }

    // The sum as it is read: one space around each binary operator, none inside brackets, and a sign stays on its number.
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
                        builder.Append(token.Text);
                    }

                    break;
            }
        }

        return builder.ToString().Trim();
    }

    // ---- Showing ----

    // The value as it is read: no trailing zeros, no exponent, rounded to MaxDecimals places unless that would make a value that is not
    // nothing read as nothing.
    private static (string Text, bool Rounded) Show(decimal value)
    {
        var rounded = Math.Round(value, MaxDecimals, MidpointRounding.AwayFromZero);
        var isRounded = rounded != value;
        if (isRounded && rounded == 0m)
        {
            rounded = value;
            isRounded = false;
        }

        if (rounded == 0m)
        {
            return ("0", false);
        }

        return (rounded.ToString(PlainFormat, CultureInfo.InvariantCulture), isRounded);
    }

    // ---- Evaluating ----

    private sealed class CalculationException(string message) : Exception(message);

    // expression := term (("+" | "-") term)*
    // term       := unary (("*" | "/" | "%") unary)*
    // unary      := ("+" | "-") unary | power
    // power      := primary ("^" unary)?
    // primary    := number | "(" expression ")"
    private sealed class Parser(List<Token> tokens)
    {
        private int _position;
        private int _depth;

        public decimal Parse()
        {
            if (tokens.Count == 0)
            {
                throw new CalculationException("There is nothing to work out. Give the sum, such as 9 + 10.");
            }

            var value = Expression();
            if (_position < tokens.Count)
            {
                throw tokens[_position].Kind == Kind.Close
                    ? new CalculationException("A bracket is closed that was never opened.")
                    : new CalculationException("Put an operator between the numbers, such as 2 + 3.");
            }

            return value;
        }

        private decimal Expression()
        {
            Enter();
            var value = Term();
            while (_position < tokens.Count && tokens[_position].Kind is Kind.Plus or Kind.Minus)
            {
                var plus = tokens[_position++].Kind == Kind.Plus;
                var right = Term();
                value = Checked(() => plus ? value + right : value - right);
            }

            _depth--;
            return value;
        }

        private decimal Term()
        {
            var value = Unary();
            while (_position < tokens.Count && tokens[_position].Kind is Kind.Times or Kind.Divide or Kind.Remainder)
            {
                var kind = tokens[_position++].Kind;
                var right = Unary();
                var left = value;
                value = kind switch
                {
                    Kind.Times => Checked(() => left * right),
                    Kind.Divide => right == 0m ? throw ZeroDivision() : Checked(() => left / right),
                    _ => right == 0m ? throw ZeroDivision() : Checked(() => left % right),
                };
            }

            return value;
        }

        private decimal Unary()
        {
            if (_position < tokens.Count && tokens[_position].Kind is Kind.Plus or Kind.Minus)
            {
                Enter();
                var negative = tokens[_position++].Kind == Kind.Minus;
                var value = Unary();
                _depth--;
                return negative ? -value : value;
            }

            return Power();
        }

        private decimal Power()
        {
            var value = Primary();
            if (_position < tokens.Count && tokens[_position].Kind == Kind.Power)
            {
                _position++;
                Enter();
                var exponent = Unary();
                _depth--;
                return Raise(value, exponent);
            }

            return value;
        }

        private decimal Primary()
        {
            if (_position >= tokens.Count)
            {
                throw new CalculationException("The sum is not complete: it ends where a number or a bracket should come.");
            }

            var token = tokens[_position];
            switch (token.Kind)
            {
                case Kind.Number:
                    _position++;
                    if (_position < tokens.Count && tokens[_position].Kind == Kind.Open)
                    {
                        throw new CalculationException("Put an operator between a number and a bracket, such as 2 * (3 + 4).");
                    }

                    return token.Value;
                case Kind.Open:
                    _position++;
                    var inner = Expression();
                    if (_position >= tokens.Count || tokens[_position].Kind != Kind.Close)
                    {
                        throw new CalculationException("A bracket is opened and never closed.");
                    }

                    _position++;
                    if (_position < tokens.Count && tokens[_position].Kind is Kind.Number or Kind.Open)
                    {
                        throw new CalculationException("Put an operator between a bracket and what follows it, such as (1 + 2) * 3.");
                    }

                    return inner;
                case Kind.Close:
                    throw new CalculationException("A bracket is closed where a number should come.");
                default:
                    throw new CalculationException($"The sum does not read: \"{token.Text}\" is where a number or a bracket should come.");
            }
        }

        private void Enter()
        {
            if (++_depth > MaxDepth)
            {
                throw new CalculationException("The sum is nested too deeply.");
            }
        }

        private static CalculationException ZeroDivision() => new("Division by zero has no value.");

        // A decimal that does not fit is too large to say, not a crash.
        private static decimal Checked(Func<decimal> work)
        {
            try
            {
                return work();
            }
            catch (OverflowException)
            {
                throw new CalculationException("The result is too large to work out.");
            }
        }

        // A power: a whole exponent by squaring in decimals, so that 2^10 is 1024 and not 1023.99999; any other with doubles, which a
        // decimal has no way to do, and then only for a base that has a real result.
        private static decimal Raise(decimal value, decimal exponent)
        {
            if (exponent == decimal.Truncate(exponent))
            {
                if (Math.Abs(exponent) > MaxIntegerExponent)
                {
                    throw new CalculationException("The exponent is too large to work out.");
                }

                if (value == 0m && exponent < 0m)
                {
                    throw ZeroDivision();
                }

                var count = (int)Math.Abs(exponent);
                var result = Checked(() => IntegerPower(value, count));
                if (exponent < 0m)
                {
                    result = Checked(() => 1m / result);
                }

                if (result == 0m && value != 0m)
                {
                    throw new CalculationException("The result is too small to work out.");
                }

                return result;
            }

            if (value < 0m)
            {
                throw new CalculationException("A negative number raised to a fraction has no real value.");
            }

            var approximate = Math.Pow((double)value, (double)exponent);
            if (!double.IsFinite(approximate) || Math.Abs(approximate) >= (double)decimal.MaxValue)
            {
                throw new CalculationException("The result is too large to work out.");
            }

            return (decimal)approximate;
        }

        private static decimal IntegerPower(decimal value, int exponent)
        {
            var result = 1m;
            var factor = value;
            while (exponent > 0)
            {
                if ((exponent & 1) == 1)
                {
                    result *= factor;
                }

                exponent >>= 1;
                if (exponent > 0)
                {
                    factor *= factor;
                }
            }

            return result;
        }
    }
}
