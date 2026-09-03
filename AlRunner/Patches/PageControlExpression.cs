// Evaluate the AL "client expression" the compiler writes into a page control's Visible,
// Editable or Enabled property.
//
// These properties do not take a variable name. They take an expression, and the AL compiler
// writes its SOURCE TEXT into the page metadata with the identifiers already resolved to their
// emitted spelling. Measured on BC 28.1, for a page 65901 with globals HideIt/LockIt/Flag2 and a
// source table carrying Value, Flag, Qty, Kind and "Spaced Name":
//
//   AL on the control                       metadata property text
//   --------------------------------------  ----------------------------------------------
//   Visible = HideIt                        p65901p65901HideIt
//   Visible = not HideIt                    not p65901p65901HideIt
//   Visible = HideIt and LockIt             p65901p65901HideIt and p65901p65901LockIt
//   Visible = not (HideIt or LockIt)        not ( p65901p65901HideIt or p65901p65901LockIt )
//   Visible = Rec.Flag                      Flag
//   Visible = not Rec.Flag                  not Flag
//   Visible = Rec.Value <> ''               Value <> ''
//   Visible = Rec."Spaced Name"             "Spaced Name"
//   Visible = Rec.Qty > 0                   Qty > 0
//   Visible = Rec.Kind = Rec.Kind::Second   Kind = 1
//   Visible = (Rec.Value = 'x') or Flag2    ( Value = 'x' ) or p65901p65901Flag2
//   Visible = Rec.Qty > 1 + 1               Qty > 1 + 1
//
// Four things that shape the parser, all of them measured rather than assumed:
//
//   - A page global arrives under its emitted name and is registered in the page's
//     SourceExpressions table. A source-table field arrives as the FIELD NAME and is not in that
//     table at all, so identifier resolution has two sources, in that order.
//   - An enum or option comparand is already an ORDINAL (`Kind = 1`), so nothing here has to
//     resolve an enum member.
//   - A name needing quoting keeps its AL double quotes (`"Spaced Name"`).
//   - Tokens are separated by single spaces, but nothing may depend on that: the tokenizer reads
//     the text character by character.
//
// The grammar is bounded by the compiler, which rejects a procedure call in one of these
// properties: "AL0322: Procedure calls is not valid for client expressions. Client expressions can
// only use simple data types and field references."
//
// Precedence follows AL's, which is Pascal's and NOT C's — `and` binds like multiplication, `or`
// like addition, and the comparison operators are LOWEST. That is why `A = B and C` means
// `A = (B and C)` in AL, and why AL code parenthesizes comparisons that it combines. Getting this
// backwards would silently mis-evaluate expressions that parse either way.
//
// Anything this cannot parse, and any identifier that resolves to neither a registered expression
// nor a field on the source record, is reported to the caller as a failure so it can raise
// RunnerOutOfScopeException naming the expression. Per .claude/rules/loud-failures.md, an
// expression we cannot evaluate must never come back as a default answer: `Visible` and `Editable`
// ARE the page's contract, and inventing "true" for one makes every test of that contract
// unfailable.

using System.Globalization;

namespace AlRunner.Patches;

/// <summary>
/// Parser and evaluator for the client-expression grammar the AL compiler writes into a page
/// control's boolean properties. Pure: identifier and field resolution are supplied by the caller.
/// </summary>
internal static class PageControlExpression
{
    /// <summary>
    /// Evaluate <paramref name="text"/> to a Boolean.
    ///
    /// Returns false and sets <paramref name="failure"/> when the text cannot be parsed, an
    /// identifier cannot be resolved, an operator is applied to operands it does not accept, or
    /// the result is not a Boolean. The caller turns that into a loud out-of-scope failure; this
    /// method never guesses a value.
    /// </summary>
    /// <param name="resolve">
    /// Resolves one identifier to its current value. Returns false when the name is neither a
    /// registered source expression nor a field on the page's source record.
    /// </param>
    internal static bool TryEvaluateBoolean(
        string text,
        ResolveIdentifier resolve,
        out bool value,
        out string? failure)
    {
        value = false;
        failure = null;

        if (!TryTokenize(text, out var tokens, out failure)) return false;

        var parser = new Parser(tokens, resolve);
        if (!parser.TryParse(out var result, out failure)) return false;

        if (result is not bool b)
        {
            failure = $"it evaluated to '{Describe(result)}', which is not a Boolean";
            return false;
        }

        value = b;
        return true;
    }

    /// <summary>
    /// Resolve one identifier — a registered source-expression name, or a field name on the
    /// page's source record — to its current value. <paramref name="quoted"/> says the name
    /// arrived in AL double quotes, which only ever happens for a field name.
    /// </summary>
    internal delegate bool ResolveIdentifier(string name, bool quoted, out object? value);

    // ---- tokens -------------------------------------------------------------------------------

    internal enum TokenKind { Identifier, QuotedIdentifier, Number, String, Operator, LeftParen, RightParen }

    internal readonly record struct Token(TokenKind Kind, string Text);

    /// <summary>
    /// Split the property text into tokens. Whitespace separates but is not required to: the
    /// emitted form puts single spaces around every operator, and this does not rely on that.
    /// </summary>
    internal static bool TryTokenize(string text, out List<Token> tokens, out string? failure)
    {
        tokens = new List<Token>();
        failure = null;

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '(') { tokens.Add(new Token(TokenKind.LeftParen, "(")); i++; continue; }
            if (c == ')') { tokens.Add(new Token(TokenKind.RightParen, ")")); i++; continue; }

            // AL string literal: single quotes, with '' as an escaped quote.
            if (c == '\'')
            {
                i++;
                var literal = new System.Text.StringBuilder();
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'') { literal.Append('\''); i += 2; continue; }
                        i++; closed = true; break;
                    }
                    literal.Append(text[i]); i++;
                }
                if (!closed) { failure = "it contains an unterminated string literal"; return false; }
                tokens.Add(new Token(TokenKind.String, literal.ToString()));
                continue;
            }

            // AL quoted identifier: double quotes, for a name that needs them ("Spaced Name").
            if (c == '"')
            {
                i++;
                var name = new System.Text.StringBuilder();
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { name.Append('"'); i += 2; continue; }
                        i++; closed = true; break;
                    }
                    name.Append(text[i]); i++;
                }
                if (!closed) { failure = "it contains an unterminated quoted name"; return false; }
                tokens.Add(new Token(TokenKind.QuotedIdentifier, name.ToString()));
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                tokens.Add(new Token(TokenKind.Number, text[start..i]));
                continue;
            }

            // An identifier the compiler emitted. Page globals come out as p<id>p<id><Name>;
            // field names come out as written. Underscores appear in both.
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                tokens.Add(new Token(TokenKind.Identifier, text[start..i]));
                continue;
            }

            // Two-character operators first, so <> and <= are not read as < followed by junk.
            if (i + 1 < text.Length)
            {
                var pair = text.Substring(i, 2);
                if (pair is "<>" or "<=" or ">=")
                {
                    tokens.Add(new Token(TokenKind.Operator, pair)); i += 2; continue;
                }
            }

            if (c is '=' or '<' or '>' or '+' or '-' or '*' or '/')
            {
                tokens.Add(new Token(TokenKind.Operator, c.ToString())); i++; continue;
            }

            failure = $"it contains the character '{c}', which is not part of the client-expression grammar";
            return false;
        }

        return true;
    }

    // ---- parser -------------------------------------------------------------------------------

    // AL's precedence, lowest binding first. This is Pascal's, not C's: `and` sits with the
    // multiplying operators and `or` with the adding ones, so the comparison operators bind
    // loosest of all.
    //
    //   1. = <> < <= > >=        (lowest)
    //   2. + - or xor
    //   3. * / div mod and
    //   4. not, unary + -        (highest)
    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly ResolveIdentifier _resolve;
        private int _at;
        private string? _failure;

        internal Parser(List<Token> tokens, ResolveIdentifier resolve)
        {
            _tokens = tokens;
            _resolve = resolve;
        }

        internal bool TryParse(out object? result, out string? failure)
        {
            result = null;
            failure = null;

            if (_tokens.Count == 0)
            {
                failure = "it is empty";
                return false;
            }

            var value = Comparison();
            if (_failure != null) { failure = _failure; return false; }

            if (_at != _tokens.Count)
            {
                failure = $"there is unparsed text starting at '{_tokens[_at].Text}'";
                return false;
            }

            result = value;
            return true;
        }

        private Token? Peek => _at < _tokens.Count ? _tokens[_at] : null;

        private bool NextIsOperator(params string[] names)
        {
            var t = Peek;
            if (t is not { Kind: TokenKind.Operator or TokenKind.Identifier }) return false;
            foreach (var n in names)
                if (string.Equals(t.Value.Text, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private object? Comparison()
        {
            var left = Additive();
            if (_failure != null) return null;

            while (NextIsOperator("=", "<>", "<", "<=", ">", ">="))
            {
                var op = _tokens[_at++].Text;
                var right = Additive();
                if (_failure != null) return null;
                left = Compare(op, left, right);
                if (_failure != null) return null;
            }
            return left;
        }

        private object? Additive()
        {
            var left = Multiplicative();
            if (_failure != null) return null;

            while (NextIsOperator("+", "-", "or", "xor"))
            {
                var op = _tokens[_at++].Text;
                var right = Multiplicative();
                if (_failure != null) return null;
                left = Binary(op, left, right);
                if (_failure != null) return null;
            }
            return left;
        }

        private object? Multiplicative()
        {
            var left = Unary();
            if (_failure != null) return null;

            while (NextIsOperator("*", "/", "div", "mod", "and"))
            {
                var op = _tokens[_at++].Text;
                var right = Unary();
                if (_failure != null) return null;
                left = Binary(op, left, right);
                if (_failure != null) return null;
            }
            return left;
        }

        private object? Unary()
        {
            if (NextIsOperator("not"))
            {
                _at++;
                var operand = Unary();
                if (_failure != null) return null;
                if (operand is not bool b)
                {
                    _failure = $"'not' was applied to '{Describe(operand)}', which is not a Boolean";
                    return null;
                }
                return !b;
            }

            if (NextIsOperator("-"))
            {
                _at++;
                var operand = Unary();
                if (_failure != null) return null;
                if (!TryAsNumber(operand, out var d))
                {
                    _failure = $"unary '-' was applied to '{Describe(operand)}', which is not a number";
                    return null;
                }
                return -d;
            }

            if (NextIsOperator("+")) { _at++; return Unary(); }

            return Primary();
        }

        private object? Primary()
        {
            var t = Peek;
            if (t == null)
            {
                _failure = "it ends where a value was expected";
                return null;
            }

            switch (t.Value.Kind)
            {
                case TokenKind.LeftParen:
                {
                    _at++;
                    var inner = Comparison();
                    if (_failure != null) return null;
                    if (Peek is not { Kind: TokenKind.RightParen })
                    {
                        _failure = "a '(' is not closed";
                        return null;
                    }
                    _at++;
                    return inner;
                }

                case TokenKind.String:
                    _at++;
                    return t.Value.Text;

                case TokenKind.Number:
                    _at++;
                    if (!decimal.TryParse(t.Value.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                    {
                        _failure = $"'{t.Value.Text}' is not a number this can read";
                        return null;
                    }
                    return n;

                case TokenKind.QuotedIdentifier:
                    _at++;
                    return Resolve(t.Value.Text, quoted: true);

                case TokenKind.Identifier:
                {
                    // `true` and `false` are the only reserved words that reach here as values.
                    // Every other word is a name. `not`/`and`/`or`/`xor`/`div`/`mod` are consumed
                    // as operators before Primary ever sees them.
                    if (string.Equals(t.Value.Text, "true", StringComparison.OrdinalIgnoreCase)) { _at++; return true; }
                    if (string.Equals(t.Value.Text, "false", StringComparison.OrdinalIgnoreCase)) { _at++; return false; }
                    _at++;
                    return Resolve(t.Value.Text, quoted: false);
                }

                default:
                    _failure = $"'{t.Value.Text}' cannot start a value";
                    return null;
            }
        }

        private object? Resolve(string name, bool quoted)
        {
            if (_resolve(name, quoted, out var value)) return value;
            _failure = $"'{name}' is neither an expression the page publishes a binding for "
                     + "nor a field on its source record";
            return null;
        }

        private object? Binary(string op, object? left, object? right)
        {
            if (string.Equals(op, "and", StringComparison.OrdinalIgnoreCase)
                || string.Equals(op, "or", StringComparison.OrdinalIgnoreCase)
                || string.Equals(op, "xor", StringComparison.OrdinalIgnoreCase))
            {
                // Both operands are already evaluated. AL's `and`/`or` do not short-circuit
                // either, so evaluating both is faithful, and there is nothing here that could
                // have a side effect: the grammar has no procedure calls in it.
                if (left is not bool l || right is not bool r)
                {
                    _failure = $"'{op}' was applied to '{Describe(left)}' and '{Describe(right)}', "
                             + "which are not both Boolean";
                    return null;
                }
                return op.ToLowerInvariant() switch
                {
                    "and" => l && r,
                    "or" => l || r,
                    _ => l ^ r,
                };
            }

            if (!TryAsNumber(left, out var a) || !TryAsNumber(right, out var b))
            {
                _failure = $"'{op}' was applied to '{Describe(left)}' and '{Describe(right)}', "
                         + "which are not both numbers";
                return null;
            }

            switch (op.ToLowerInvariant())
            {
                case "+": return a + b;
                case "-": return a - b;
                case "*": return a * b;
                case "/":
                case "div":
                case "mod":
                    if (b == 0)
                    {
                        _failure = $"'{op}' would divide by zero";
                        return null;
                    }
                    return op.ToLowerInvariant() switch
                    {
                        "/" => a / b,
                        "div" => decimal.Truncate(a / b),
                        _ => a % b,
                    };
                default:
                    _failure = $"'{op}' is not an operator this can evaluate";
                    return null;
            }
        }

        private object? Compare(string op, object? left, object? right)
        {
            int order;

            if (left is string || right is string)
            {
                // AL compares Text and Code case-insensitively, so 'a' = 'A' is true. Using an
                // ordinal comparison here would answer differently from BC on exactly the shape
                // these properties are written in.
                order = string.Compare(
                    left as string ?? Describe(left),
                    right as string ?? Describe(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            else if (left is bool lb && right is bool rb)
            {
                order = lb.CompareTo(rb);
            }
            else if (TryAsNumber(left, out var a) && TryAsNumber(right, out var b))
            {
                order = a.CompareTo(b);
            }
            else if (left == null || right == null)
            {
                order = left == null && right == null ? 0 : 1;
            }
            else
            {
                _failure = $"'{op}' was applied to '{Describe(left)}' and '{Describe(right)}', "
                         + "which this cannot compare";
                return null;
            }

            return op switch
            {
                "=" => order == 0,
                "<>" => order != 0,
                "<" => order < 0,
                "<=" => order <= 0,
                ">" => order > 0,
                ">=" => order >= 0,
                _ => null,
            };
        }
    }

    private static bool TryAsNumber(object? value, out decimal number)
    {
        switch (value)
        {
            case decimal d: number = d; return true;
            case int i: number = i; return true;
            case long l: number = l; return true;
            case short s: number = s; return true;
            case byte b: number = b; return true;
            case double db: number = (decimal)db; return true;
            case float f: number = (decimal)f; return true;
            default: number = 0; return false;
        }
    }

    /// <summary>A value's spelling for a failure message. Never used to compute an answer.</summary>
    internal static string Describe(object? value)
        => value switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "null",
        };
}
