using System.Globalization;
using System.Text;
using Nullbox.Core.Jev;

namespace Nullbox.Core.Rules;

/// <summary>
/// A decision condition over Jev answers. Evaluation is three-valued: a comparison on a missing answer is unknown,
/// and a rule only matches when its whole condition is definitely true, so missing outputs never trigger actions.
/// </summary>
public abstract record Condition
{
    /// <summary>True, false, or null when it depends on an answer that is missing.</summary>
    public abstract bool? Evaluate(JevResult result);

    public bool Matches(JevResult result) => Evaluate(result) == true;

    /// <summary>Every comparison in this condition, left to right.</summary>
    public abstract IEnumerable<Comparison> Comparisons();

    internal abstract int Precedence { get; }

    internal string Wrap(Condition child)
        => child.Precedence < Precedence ? $"({child})" : child.ToString()!;
}

public sealed record AndCondition(Condition Left, Condition Right) : Condition
{
    internal override int Precedence => 2;

    public override bool? Evaluate(JevResult result)
    {
        var left = Left.Evaluate(result);
        var right = Right.Evaluate(result);
        return left == false || right == false ? false : left is null || right is null ? null : true;
    }

    public override IEnumerable<Comparison> Comparisons() => Left.Comparisons().Concat(Right.Comparisons());

    public override string ToString() => $"{Wrap(Left)} && {Wrap(Right)}";
}

public sealed record OrCondition(Condition Left, Condition Right) : Condition
{
    internal override int Precedence => 1;

    public override bool? Evaluate(JevResult result)
    {
        var left = Left.Evaluate(result);
        var right = Right.Evaluate(result);
        return left == true || right == true ? true : left is null || right is null ? null : false;
    }

    public override IEnumerable<Comparison> Comparisons() => Left.Comparisons().Concat(Right.Comparisons());

    public override string ToString() => $"{Wrap(Left)} || {Wrap(Right)}";
}

public sealed record NotCondition(Condition Inner) : Condition
{
    internal override int Precedence => 3;

    public override bool? Evaluate(JevResult result) => !Inner.Evaluate(result);

    public override IEnumerable<Comparison> Comparisons() => Inner.Comparisons();

    public override string ToString() => Inner is NotCondition ? $"!{Inner}" : $"!({Inner})";
}

public enum ComparisonOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
}

/// <summary>
/// <c>&lt;question&gt;.&lt;field&gt; &lt;op&gt; &lt;value&gt;</c>. Fields: <c>choice</c>, <c>confidence</c> and
/// <c>probabilities.&lt;option&gt;</c> for choices; <c>score</c>, <c>confidence</c> and <c>probabilities.&lt;level&gt;</c>
/// for scores; <c>noul</c> for nouls.
/// </summary>
public sealed record Comparison(string Question, string Field, string? Key, ComparisonOperator Operator, object Value, int Position) : Condition
{
    public const string ProbabilitiesField = "probabilities";

    internal override int Precedence => 4;

    public string Path => Key is null ? $"{Question}.{Field}" : $"{Question}.{Field}.{Key}";

    /// <summary>Where the comparison starts in the text matters for errors, not for meaning.</summary>
    public bool Equals(Comparison? other)
        => other is not null && Question == other.Question && Field == other.Field && Key == other.Key
            && Operator == other.Operator && Equals(Value, other.Value);

    public override int GetHashCode() => HashCode.Combine(Question, Field, Key, Operator, Value);

    public override bool? Evaluate(JevResult result)
    {
        if (!result.Answers.TryGetValue(Question, out var answer))
        {
            return null;
        }

        object? actual = (answer, Field) switch
        {
            (JevChoiceAnswer c, "choice") => c.Choice,
            (JevChoiceAnswer c, "confidence") => c.Confidence,
            (JevChoiceAnswer c, ProbabilitiesField) => c.Probabilities.TryGetValue(Key!, out var p) ? p : 0d,
            (JevScoreAnswer s, "score") => s.Score,
            (JevScoreAnswer s, "confidence") => s.Confidence,
            (JevScoreAnswer s, ProbabilitiesField) => s.Probabilities.TryGetValue(Key!, out var p) ? p : 0d,
            (JevNoulAnswer n, "noul") => n.Noul,
            _ => null,
        };

        return (actual, Value) switch
        {
            (string a, string v) => Operator switch
            {
                ComparisonOperator.Equal => string.Equals(a, v, StringComparison.Ordinal),
                ComparisonOperator.NotEqual => !string.Equals(a, v, StringComparison.Ordinal),
                _ => null,
            },
            (double a, double v) => Operator switch
            {
                ComparisonOperator.Equal => a == v,
                ComparisonOperator.NotEqual => a != v,
                ComparisonOperator.GreaterThan => a > v,
                ComparisonOperator.GreaterOrEqual => a >= v,
                ComparisonOperator.LessThan => a < v,
                _ => a <= v,
            },
            _ => null,
        };
    }

    public override IEnumerable<Comparison> Comparisons() => [this];

    public override string ToString()
    {
        var op = Operator switch
        {
            ComparisonOperator.Equal => "==",
            ComparisonOperator.NotEqual => "!=",
            ComparisonOperator.GreaterThan => ">",
            ComparisonOperator.GreaterOrEqual => ">=",
            ComparisonOperator.LessThan => "<",
            _ => "<=",
        };
        var value = Value is string s
            ? "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : ((double)Value).ToString("R", CultureInfo.InvariantCulture);
        return $"{Path} {op} {value}";
    }
}

public sealed class ConditionSyntaxException(string message, int position)
    : FormatException($"{message} (at character {position + 1})")
{
    public int Position { get; } = position;
}

/// <summary>
/// Parses decision conditions: comparisons joined with <c>&amp;&amp;</c>, <c>||</c>, <c>!</c> and parentheses
/// (<c>&amp;&amp;</c> binds tighter than <c>||</c>). Values are double-quoted strings or numbers.
/// </summary>
public static class ConditionParser
{
    public const int MaxLength = 4000;

    public static Condition Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxLength)
        {
            throw new ConditionSyntaxException($"Conditions can be at most {MaxLength} characters", MaxLength);
        }

        var parser = new Parser(Tokenize(text));
        var condition = parser.ParseOr();
        parser.Expect(TokenKind.End, "Unexpected text after the condition");
        return condition;
    }

    public static bool TryParse(string text, out Condition? condition, out ConditionSyntaxException? error)
    {
        try
        {
            condition = Parse(text);
            error = null;
            return true;
        }
        catch (ConditionSyntaxException ex)
        {
            condition = null;
            error = ex;
            return false;
        }
    }

    private enum TokenKind
    {
        Path,
        String,
        Number,
        Operator,
        And,
        Or,
        Not,
        Open,
        Close,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Position);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= text.Length)
                    {
                        throw new ConditionSyntaxException("Unterminated string", start);
                    }

                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        sb.Append(text[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (text[i] == '"')
                    {
                        i++;
                        break;
                    }

                    sb.Append(text[i++]);
                }

                tokens.Add(new Token(TokenKind.String, sb.ToString(), start));
            }
            else if (char.IsAsciiDigit(c) || (c is '-' or '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                i++;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Number, text[start..i], start));
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.' or '-'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Path, text[start..i], start));
            }
            else
            {
                var two = i + 1 < text.Length ? text.Substring(i, 2) : null;
                (TokenKind Kind, int Length)? match = two switch
                {
                    "&&" => (TokenKind.And, 2),
                    "||" => (TokenKind.Or, 2),
                    "==" or "!=" or ">=" or "<=" => (TokenKind.Operator, 2),
                    _ => c switch
                    {
                        '>' or '<' => (TokenKind.Operator, 1),
                        '!' => (TokenKind.Not, 1),
                        '(' => (TokenKind.Open, 1),
                        ')' => (TokenKind.Close, 1),
                        _ => null,
                    },
                };
                if (match is not { } m)
                {
                    throw new ConditionSyntaxException($"Unexpected character '{c}'", i);
                }

                tokens.Add(new Token(m.Kind, text.Substring(i, m.Length), i));
                i += m.Length;
            }
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, text.Length));
        return tokens;
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _index;

        private Token Current => tokens[_index];

        public Condition ParseOr()
        {
            var left = ParseAnd();
            while (Current.Kind == TokenKind.Or)
            {
                _index++;
                left = new OrCondition(left, ParseAnd());
            }

            return left;
        }

        public void Expect(TokenKind kind, string message)
        {
            if (Current.Kind != kind)
            {
                throw new ConditionSyntaxException(Current.Kind == TokenKind.End ? $"{message}: the condition ended early" : $"{message}: '{Current.Text}'", Current.Position);
            }
        }

        private Condition ParseAnd()
        {
            var left = ParseUnary();
            while (Current.Kind == TokenKind.And)
            {
                _index++;
                left = new AndCondition(left, ParseUnary());
            }

            return left;
        }

        private Condition ParseUnary()
        {
            switch (Current.Kind)
            {
                case TokenKind.Not:
                    _index++;
                    return new NotCondition(ParseUnary());
                case TokenKind.Open:
                    _index++;
                    var inner = ParseOr();
                    Expect(TokenKind.Close, "Expected ')'");
                    _index++;
                    return inner;
                default:
                    return ParseComparison();
            }
        }

        private Comparison ParseComparison()
        {
            Expect(TokenKind.Path, "Expected an answer such as category.choice");
            var path = Current;
            var parts = path.Text.Split('.', 3);
            if (parts.Length < 2 || parts.Any(p => p.Length == 0))
            {
                throw new ConditionSyntaxException($"'{path.Text}' should look like question.field", path.Position);
            }

            if (parts.Length == 3 && parts[1] != Comparison.ProbabilitiesField)
            {
                throw new ConditionSyntaxException($"Only probabilities take a key, as in {parts[0]}.probabilities.<option>", path.Position);
            }

            if (parts is [_, Comparison.ProbabilitiesField])
            {
                throw new ConditionSyntaxException($"Name the option, as in {parts[0]}.probabilities.<option>", path.Position);
            }

            _index++;
            Expect(TokenKind.Operator, $"Expected a comparison (==, !=, >, >=, <, <=) after {path.Text}");
            var op = Current.Text switch
            {
                "==" => ComparisonOperator.Equal,
                "!=" => ComparisonOperator.NotEqual,
                ">" => ComparisonOperator.GreaterThan,
                ">=" => ComparisonOperator.GreaterOrEqual,
                "<" => ComparisonOperator.LessThan,
                _ => ComparisonOperator.LessOrEqual,
            };
            _index++;

            object value;
            if (Current.Kind == TokenKind.String)
            {
                value = Current.Text;
            }
            else if (Current.Kind == TokenKind.Number
                && double.TryParse(Current.Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            {
                value = number;
            }
            else
            {
                throw new ConditionSyntaxException(
                    Current.Kind == TokenKind.Path ? $"Put text values in double quotes: \"{Current.Text}\"" : "Expected a number or a \"quoted\" value",
                    Current.Position);
            }

            _index++;
            return new Comparison(parts[0], parts[1], parts.Length == 3 ? parts[2] : null, op, value, path.Position);
        }
    }
}
