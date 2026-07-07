using System.Globalization;
using System.Text.RegularExpressions;

namespace STRIDE.Abstractions;

public sealed partial class ExpressionEvaluator
{
    private readonly IExpressionNode _root;

    private ExpressionEvaluator(IExpressionNode root)
    {
        _root = root;
    }

    public static ExpressionEvaluator Compile(string expression)
        => new(ExpressionParser.Parse(expression));

    public bool Evaluate(IRecordBatch batch, int rowIndex)
        => _root.Evaluate(batch, rowIndex);

    private interface IExpressionNode
    {
        bool Evaluate(IRecordBatch batch, int rowIndex);
    }

    private sealed class AndNode : IExpressionNode
    {
        private readonly IExpressionNode _left;
        private readonly IExpressionNode _right;

        public AndNode(IExpressionNode left, IExpressionNode right)
        {
            _left = left;
            _right = right;
        }

        public bool Evaluate(IRecordBatch batch, int rowIndex)
            => _left.Evaluate(batch, rowIndex) && _right.Evaluate(batch, rowIndex);
    }

    private sealed class OrNode : IExpressionNode
    {
        private readonly IExpressionNode _left;
        private readonly IExpressionNode _right;

        public OrNode(IExpressionNode left, IExpressionNode right)
        {
            _left = left;
            _right = right;
        }

        public bool Evaluate(IRecordBatch batch, int rowIndex)
            => _left.Evaluate(batch, rowIndex) || _right.Evaluate(batch, rowIndex);
    }

    private sealed class NotNode : IExpressionNode
    {
        private readonly IExpressionNode _inner;

        public NotNode(IExpressionNode inner)
        {
            _inner = inner;
        }

        public bool Evaluate(IRecordBatch batch, int rowIndex)
            => !_inner.Evaluate(batch, rowIndex);
    }

    private sealed class ConditionNode : IExpressionNode
    {
        private readonly CompiledPredicate _predicate;
        private Schema? _schema;
        private int _ordinal = -1;

        public ConditionNode(CompiledPredicate predicate)
        {
            _predicate = predicate;
        }

        public bool Evaluate(IRecordBatch batch, int rowIndex)
        {
            if (!ReferenceEquals(_schema, batch.Schema))
            {
                if (!batch.Schema.TryGetOrdinal(_predicate.FieldName, out var resolved))
                {
                    throw new InvalidOperationException($"Expression references unknown field '{_predicate.FieldName}'.");
                }

                _schema = batch.Schema;
                _ordinal = resolved;
            }

            return _predicate.Evaluate(batch, _ordinal, rowIndex);
        }
    }

    private readonly record struct CompiledPredicate(string FieldName, string Operator, string RightRaw)
    {
        public bool Evaluate(IRecordBatch batch, int fieldOrdinal, int rowIndex)
        {
            var fieldType = batch.Schema.Fields[fieldOrdinal].Type;
            return fieldType switch
            {
                FieldType.Int32 => EvaluateNumeric(batch.Column<int>(fieldOrdinal)[rowIndex]),
                FieldType.Int64 => EvaluateNumeric(batch.Column<long>(fieldOrdinal)[rowIndex]),
                FieldType.Float64 => EvaluateNumeric(batch.Column<double>(fieldOrdinal)[rowIndex]),
                FieldType.Boolean => EvaluateBoolean(batch.Column<bool>(fieldOrdinal)[rowIndex]),
                _ => EvaluateString(batch.GetValueAsString(fieldOrdinal, rowIndex)),
            };
        }

        private bool EvaluateNumeric<T>(T left)
            where T : struct, IConvertible
        {
            var leftNumber = Convert.ToDouble(left, CultureInfo.InvariantCulture);
            var rightNumber = double.Parse(StripQuotes(RightRaw), CultureInfo.InvariantCulture);

            return Operator switch
            {
                "==" => leftNumber == rightNumber,
                "!=" => leftNumber != rightNumber,
                ">" => leftNumber > rightNumber,
                "<" => leftNumber < rightNumber,
                ">=" => leftNumber >= rightNumber,
                "<=" => leftNumber <= rightNumber,
                _ => throw new InvalidOperationException($"Unsupported numeric operator '{Operator}'."),
            };
        }

        private bool EvaluateBoolean(bool left)
        {
            var rightValue = bool.Parse(StripQuotes(RightRaw));
            return Operator switch
            {
                "==" => left == rightValue,
                "!=" => left != rightValue,
                _ => throw new InvalidOperationException($"Unsupported boolean operator '{Operator}'."),
            };
        }

        private bool EvaluateString(string left)
        {
            var right = StripQuotes(RightRaw);
            return Operator switch
            {
                "==" => string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
                "!=" => !string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
                "contains" => left.Contains(right, StringComparison.OrdinalIgnoreCase),
                "startswith" => left.StartsWith(right, StringComparison.OrdinalIgnoreCase),
                "endswith" => left.EndsWith(right, StringComparison.OrdinalIgnoreCase),
                "matches" => Regex.IsMatch(left, right, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                _ => throw new InvalidOperationException($"Unsupported string operator '{Operator}'."),
            };
        }

        public static CompiledPredicate Parse(string expression)
        {
            var match = PredicateRegex().Match(expression);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Unsupported expression predicate '{expression}'.");
            }

            return new CompiledPredicate(
                match.Groups["field"].Value,
                match.Groups["op"].Value.ToLowerInvariant(),
                match.Groups["value"].Value.Trim());
        }

        private static string StripQuotes(string value)
            => value.Length >= 2 && ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
                ? value[1..^1]
                : value;
    }

    private enum TokenKind
    {
        And,
        Or,
        Not,
        LParen,
        RParen,
        Condition,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Value);

    private sealed class ExpressionParser
    {
        private readonly List<Token> _tokens;
        private int _position;

        private ExpressionParser(List<Token> tokens)
        {
            _tokens = tokens;
        }

        public static IExpressionNode Parse(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                throw new InvalidOperationException("Expression cannot be empty.");
            }

            var tokens = Tokenize(expression);
            var parser = new ExpressionParser(tokens);
            var result = parser.ParseOr();
            if (parser.Current.Kind != TokenKind.End)
            {
                throw new InvalidOperationException($"Unexpected token '{parser.Current.Value}'.");
            }

            return result;
        }

        private Token Current => _tokens[_position];

        private Token Consume()
            => _tokens[_position++];

        private bool Match(TokenKind kind)
        {
            if (Current.Kind != kind)
            {
                return false;
            }

            _position++;
            return true;
        }

        private IExpressionNode ParseOr()
        {
            var node = ParseAnd();
            while (Match(TokenKind.Or))
            {
                node = new OrNode(node, ParseAnd());
            }

            return node;
        }

        private IExpressionNode ParseAnd()
        {
            var node = ParseUnary();
            while (Match(TokenKind.And))
            {
                node = new AndNode(node, ParseUnary());
            }

            return node;
        }

        private IExpressionNode ParseUnary()
        {
            if (Match(TokenKind.Not))
            {
                return new NotNode(ParseUnary());
            }

            return ParsePrimary();
        }

        private IExpressionNode ParsePrimary()
        {
            if (Match(TokenKind.LParen))
            {
                var nested = ParseOr();
                if (!Match(TokenKind.RParen))
                {
                    throw new InvalidOperationException("Unbalanced parentheses in expression.");
                }

                return nested;
            }

            if (Current.Kind == TokenKind.Condition)
            {
                var token = Consume();
                return new ConditionNode(CompiledPredicate.Parse(token.Value));
            }

            throw new InvalidOperationException($"Unexpected token '{Current.Value}'.");
        }

        private static List<Token> Tokenize(string expression)
        {
            var tokens = new List<Token>();
            var span = expression.AsSpan();
            var i = 0;

            while (i < span.Length)
            {
                if (char.IsWhiteSpace(span[i]))
                {
                    i++;
                    continue;
                }

                if (i + 1 < span.Length && span[i] == '&' && span[i + 1] == '&')
                {
                    tokens.Add(new Token(TokenKind.And, "&&"));
                    i += 2;
                    continue;
                }

                if (i + 1 < span.Length && span[i] == '|' && span[i + 1] == '|')
                {
                    tokens.Add(new Token(TokenKind.Or, "||"));
                    i += 2;
                    continue;
                }

                if (span[i] == '!')
                {
                    tokens.Add(new Token(TokenKind.Not, "!"));
                    i++;
                    continue;
                }

                if (span[i] == '(')
                {
                    tokens.Add(new Token(TokenKind.LParen, "("));
                    i++;
                    continue;
                }

                if (span[i] == ')')
                {
                    tokens.Add(new Token(TokenKind.RParen, ")"));
                    i++;
                    continue;
                }

                var start = i;
                var inSingleQuote = false;
                var inDoubleQuote = false;
                while (i < span.Length)
                {
                    var ch = span[i];
                    if (!inSingleQuote && ch == '"')
                    {
                        inDoubleQuote = !inDoubleQuote;
                        i++;
                        continue;
                    }

                    if (!inDoubleQuote && ch == '\'')
                    {
                        inSingleQuote = !inSingleQuote;
                        i++;
                        continue;
                    }

                    if (!inSingleQuote && !inDoubleQuote)
                    {
                        if (i + 1 < span.Length && span[i] == '&' && span[i + 1] == '&')
                        {
                            break;
                        }

                        if (i + 1 < span.Length && span[i] == '|' && span[i + 1] == '|')
                        {
                            break;
                        }

                        if (span[i] == ')')
                        {
                            break;
                        }
                    }

                    i++;
                }

                var conditionText = expression[start..i].Trim();
                if (conditionText.Length == 0)
                {
                    throw new InvalidOperationException("Invalid empty condition in expression.");
                }

                tokens.Add(new Token(TokenKind.Condition, conditionText));
            }

            tokens.Add(new Token(TokenKind.End, string.Empty));
            return tokens;
        }
    }

    [GeneratedRegex("^(?<field>[A-Za-z_][A-Za-z0-9_]*)\\s*(?<op>==|!=|>=|<=|>|<|contains|startsWith|endsWith|matches)\\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PredicateRegex();
}
