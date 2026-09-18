namespace BPM.Workflow.Engine;

// A deliberately tiny, safe expression language for FormRuleType.Calculated (Phase 5.4.2 §13):
// numeric literals, field references, + - * / and parentheses, nothing else. No eval(), no
// dynamic compilation, no reflection-based method calls — unsupported syntax is a parse error,
// rejected before it can ever reach evaluation (fail closed). Shared by FormRuleValidator (schema-
// time: does this formula parse, and which fields does it reference?) and FormRuleEngine
// (runtime: what does it currently evaluate to?) so there is exactly one implementation of "what a
// formula means," not two that could drift.
public static class FormFormulaEvaluator
{
    public static bool TryParse(string formula, out FormulaNode? node, out string? error)
    {
        try
        {
            var tokens = Tokenize(formula);
            var parser = new Parser(tokens);
            node = parser.ParseExpression();
            if (!parser.AtEnd)
            {
                error = "Unexpected token in formula.";
                node = null;
                return false;
            }
            error = null;
            return true;
        }
        catch (FormulaParseException ex)
        {
            node = null;
            error = ex.Message;
            return false;
        }
    }

    public static IReadOnlyCollection<string> ReferencedFields(FormulaNode node)
    {
        var fields = new HashSet<string>();
        Collect(node, fields);
        return fields;
    }

    private static void Collect(FormulaNode node, HashSet<string> fields)
    {
        switch (node)
        {
            case FieldRefNode f:
                fields.Add(f.Field);
                break;
            case BinaryNode b:
                Collect(b.Left, fields);
                Collect(b.Right, fields);
                break;
            case UnaryMinusNode u:
                Collect(u.Operand, fields);
                break;
        }
    }

    // Value=null means "not computable yet" (a referenced field has no value, or isn't numeric) —
    // a normal, benign state while a Draft is still being filled in, not a rejected formula.
    // Error is set only for a genuine evaluation failure (division by zero) that Phase 5.4.2 §13
    // requires to "fail safely" rather than silently produce a wrong number.
    public static FormulaEvalResult Evaluate(FormulaNode node, IReadOnlyDictionary<string, decimal?> fieldValues)
    {
        switch (node)
        {
            case NumberNode n:
                return new FormulaEvalResult(n.Value, null);

            case FieldRefNode f:
                var has = fieldValues.TryGetValue(f.Field, out var v) && v is decimal;
                return new FormulaEvalResult(has ? v : null, null);

            case UnaryMinusNode u:
                var operand = Evaluate(u.Operand, fieldValues);
                if (operand.Error is not null) return operand;
                return new FormulaEvalResult(operand.Value is decimal ov ? -ov : null, null);

            case BinaryNode b:
                var left = Evaluate(b.Left, fieldValues);
                if (left.Error is not null) return left;
                var right = Evaluate(b.Right, fieldValues);
                if (right.Error is not null) return right;
                if (left.Value is not decimal lv || right.Value is not decimal rv)
                {
                    return new FormulaEvalResult(null, null);
                }
                return b.Operator switch
                {
                    '+' => new FormulaEvalResult(lv + rv, null),
                    '-' => new FormulaEvalResult(lv - rv, null),
                    '*' => new FormulaEvalResult(lv * rv, null),
                    '/' => rv == 0m
                        ? new FormulaEvalResult(null, "DIVISION_BY_ZERO")
                        : new FormulaEvalResult(lv / rv, null),
                    _ => throw new InvalidOperationException($"Unknown formula operator '{b.Operator}'."),
                };

            default:
                throw new InvalidOperationException("Unknown formula node.");
        }
    }

    // --- Tokenizer ---

    private enum TokenKind { Number, Ident, Plus, Minus, Star, Slash, LParen, RParen, End }

    private record struct Token(TokenKind Kind, string Text);

    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsDigit(c) || c == '.')
            {
                var start = i;
                var sawDot = false;
                while (i < input.Length && (char.IsDigit(input[i]) || (input[i] == '.' && !sawDot)))
                {
                    if (input[i] == '.') sawDot = true;
                    i++;
                }
                tokens.Add(new Token(TokenKind.Number, input[start..i]));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_')) i++;
                tokens.Add(new Token(TokenKind.Ident, input[start..i]));
                continue;
            }

            TokenKind kind = c switch
            {
                '+' => TokenKind.Plus,
                '-' => TokenKind.Minus,
                '*' => TokenKind.Star,
                '/' => TokenKind.Slash,
                '(' => TokenKind.LParen,
                ')' => TokenKind.RParen,
                _ => throw new FormulaParseException($"Unsupported character '{c}' in formula."),
            };
            tokens.Add(new Token(kind, c.ToString()));
            i++;
        }
        tokens.Add(new Token(TokenKind.End, string.Empty));
        return tokens;
    }

    // --- Recursive-descent parser: expr := term (('+'|'-') term)* ; term := factor (('*'|'/') factor)* ;
    //     factor := NUMBER | IDENT | '(' expr ')' | '-' factor ---
    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private int _pos;

        public Parser(List<Token> tokens) => _tokens = tokens;

        public bool AtEnd => _tokens[_pos].Kind == TokenKind.End;

        private Token Current => _tokens[_pos];

        public FormulaNode ParseExpression()
        {
            var left = ParseTerm();
            while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                var op = Current.Kind == TokenKind.Plus ? '+' : '-';
                _pos++;
                var right = ParseTerm();
                left = new BinaryNode(op, left, right);
            }
            return left;
        }

        private FormulaNode ParseTerm()
        {
            var left = ParseFactor();
            while (Current.Kind is TokenKind.Star or TokenKind.Slash)
            {
                var op = Current.Kind == TokenKind.Star ? '*' : '/';
                _pos++;
                var right = ParseFactor();
                left = new BinaryNode(op, left, right);
            }
            return left;
        }

        private FormulaNode ParseFactor()
        {
            if (Current.Kind == TokenKind.Minus)
            {
                _pos++;
                return new UnaryMinusNode(ParseFactor());
            }
            if (Current.Kind == TokenKind.Number)
            {
                var text = Current.Text;
                _pos++;
                if (!decimal.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    throw new FormulaParseException($"Invalid number '{text}' in formula.");
                }
                return new NumberNode(value);
            }
            if (Current.Kind == TokenKind.Ident)
            {
                var name = Current.Text;
                _pos++;
                return new FieldRefNode(name);
            }
            if (Current.Kind == TokenKind.LParen)
            {
                _pos++;
                var inner = ParseExpression();
                if (Current.Kind != TokenKind.RParen)
                {
                    throw new FormulaParseException("Missing closing parenthesis in formula.");
                }
                _pos++;
                return inner;
            }
            throw new FormulaParseException("Unexpected token in formula.");
        }
    }

    private sealed class FormulaParseException : Exception
    {
        public FormulaParseException(string message) : base(message) { }
    }
}

public abstract record FormulaNode;
public sealed record NumberNode(decimal Value) : FormulaNode;
public sealed record FieldRefNode(string Field) : FormulaNode;
public sealed record UnaryMinusNode(FormulaNode Operand) : FormulaNode;
public sealed record BinaryNode(char Operator, FormulaNode Left, FormulaNode Right) : FormulaNode;

public readonly record struct FormulaEvalResult(decimal? Value, string? Error);
