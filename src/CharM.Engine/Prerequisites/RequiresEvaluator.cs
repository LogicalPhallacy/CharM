using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace CharM.Engine.Prerequisites;

/// <summary>
/// Evaluates <c>requires</c> condition expressions against a character's element set.
///
/// Grammar:
///   requires     := '!' negated_expr | or_expr | and_expr
///   negated_expr := or_expr | and_expr
///   or_expr      := token ('|' token)*
///   and_expr     := token ('&amp;' token)*   (& is XML-escaped in source)
///   token        := '(' requires ')'
///                  | type ':' category
///                  | ELEMENT_NAME
///
/// The expression string is parsed ONCE into a cached <see cref="RequiresNode"/>
/// tree (keyed by the raw string) and re-evaluated against character state on
/// each call. Grammar and short-circuit semantics are identical to the previous
/// string-rescanning implementation; only the per-call string splitting /
/// allocation is eliminated, which matters because slot reconciliation evaluates
/// requires across the whole element tree after every pick.
/// </summary>
public sealed class RequiresEvaluator
{
    private static readonly ConcurrentDictionary<string, RequiresNode> ParseCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Evaluate a requires expression against a set of character elements.
    /// </summary>
    /// <param name="requires">The requires expression string.</param>
    /// <param name="hasElement">Callback that checks if the character has a named element.</param>
    /// <param name="hasElementOfTypeAndCategory">Callback for Type:Category checks.</param>
    /// <param name="characterLevel">Current character level (for "N level" checks).</param>
    /// <returns>True if the requirements are met.</returns>
    public static bool Evaluate(
        string? requires,
        Func<string, bool> hasElement,
        Func<string, string, bool>? hasElementOfTypeAndCategory = null,
        int characterLevel = 30)
    {
        if (string.IsNullOrWhiteSpace(requires))
            return true;

        var node = ParseCache.GetOrAdd(requires, static r => Parse(r.AsSpan()));
        return node.Evaluate(hasElement, hasElementOfTypeAndCategory, characterLevel);
    }

    // ===================== Parsing (once per distinct string) =====================

    private static RequiresNode Parse(ReadOnlySpan<char> expr)
    {
        expr = expr.Trim();
        if (expr.IsEmpty)
            return RequiresNode.AlwaysTrue;

        // Leading '!' negates the entire expression.
        if (expr[0] == '!')
            return new NotNode(Parse(expr[1..]));

        // Top-level OR (pipe outside parentheses).
        if (TrySplitTopLevel(expr, '|', out var orParts))
            return new OrNode(orParts.ConvertAll(p => Parse(p.AsSpan())));

        // Top-level AND (& outside parentheses).
        if (TrySplitTopLevel(expr, '&', out var andParts))
            return new AndNode(andParts.ConvertAll(p => Parse(p.AsSpan())));

        return ParseToken(expr);
    }

    private static RequiresNode ParseToken(ReadOnlySpan<char> token)
    {
        token = token.Trim();
        if (token.IsEmpty)
            return RequiresNode.AlwaysTrue;

        // Parenthesized sub-expression.
        if (token[0] == '(' && token[^1] == ')')
            return Parse(token[1..^1]);

        string tokenStr = token.ToString();

        // Level check: "N level" pattern (e.g., "11 level", "21 level").
        if (tokenStr.EndsWith(" level", StringComparison.OrdinalIgnoreCase))
        {
            var numPart = tokenStr[..^6].Trim();
            if (int.TryParse(numPart, out int requiredLevel))
                return new LevelNode(requiredLevel);
        }

        // Level check: "level N" pattern (reversed, rare — 1 instance in data).
        if (tokenStr.StartsWith("level ", StringComparison.OrdinalIgnoreCase))
        {
            var numPart = tokenStr[6..].Trim();
            if (int.TryParse(numPart, out int requiredLevel))
                return new LevelNode(requiredLevel);
        }

        if (tokenStr.Equals("Heroic Tier", StringComparison.OrdinalIgnoreCase))
            return new LevelNode(1);
        if (tokenStr.Equals("Paragon Tier", StringComparison.OrdinalIgnoreCase))
            return new LevelNode(11);
        if (tokenStr.Equals("Epic Tier", StringComparison.OrdinalIgnoreCase))
            return new LevelNode(21);

        // Type:Category check (e.g., "Power:encounter").
        int colonIdx = tokenStr.IndexOf(':');
        if (colonIdx > 0 && colonIdx < tokenStr.Length - 1)
        {
            string type = tokenStr[..colonIdx].Trim();
            string category = tokenStr[(colonIdx + 1)..].Trim();
            return new TypeCategoryNode(type, category, tokenStr);
        }

        return new NameNode(tokenStr);
    }

    /// <summary>
    /// Split an expression at top-level occurrences of <paramref name="separator"/>,
    /// respecting parenthesized sub-expressions.
    /// Returns false if the separator doesn't appear at top level.
    /// </summary>
    private static bool TrySplitTopLevel(
        ReadOnlySpan<char> expr,
        char separator,
        [NotNullWhen(true)] out List<string>? parts)
    {
        parts = null;
        int depth = 0;
        bool found = false;

        // First pass: check if separator exists at top level
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == separator && depth == 0)
            {
                found = true;
                break;
            }
        }

        if (!found)
            return false;

        // Second pass: split
        parts = [];
        depth = 0;
        int start = 0;

        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == separator && depth == 0)
            {
                parts.Add(expr[start..i].ToString().Trim());
                start = i + 1;
            }
        }

        parts.Add(expr[start..].ToString().Trim());
        return true;
    }
}

/// <summary>Parsed node of a requires expression. Immutable; safe to cache and share across threads.</summary>
internal abstract class RequiresNode
{
    public static readonly RequiresNode AlwaysTrue = new TrueNode();

    public abstract bool Evaluate(
        Func<string, bool> hasElement,
        Func<string, string, bool>? hasElementOfTypeAndCategory,
        int characterLevel);
}

internal sealed class TrueNode : RequiresNode
{
    public override bool Evaluate(Func<string, bool> h, Func<string, string, bool>? c, int l) => true;
}

internal sealed class NotNode(RequiresNode child) : RequiresNode
{
    public override bool Evaluate(Func<string, bool> h, Func<string, string, bool>? c, int l)
        => !child.Evaluate(h, c, l);
}

internal sealed class OrNode(List<RequiresNode> children) : RequiresNode
{
    public override bool Evaluate(Func<string, bool> h, Func<string, string, bool>? c, int l)
    {
        foreach (var child in children)
            if (child.Evaluate(h, c, l)) return true;
        return false;
    }
}

internal sealed class AndNode(List<RequiresNode> children) : RequiresNode
{
    public override bool Evaluate(Func<string, bool> h, Func<string, string, bool>? c, int l)
    {
        foreach (var child in children)
            if (!child.Evaluate(h, c, l)) return false;
        return true;
    }
}

internal sealed class LevelNode(int minLevel) : RequiresNode
{
    public override bool Evaluate(Func<string, bool> h, Func<string, string, bool>? c, int l)
        => l >= minLevel;
}

internal sealed class TypeCategoryNode(string type, string category, string rawToken) : RequiresNode
{
    public override bool Evaluate(Func<string, bool> hasElement, Func<string, string, bool>? hasTypeCat, int l)
        => hasTypeCat is not null ? hasTypeCat(type, category) : hasElement(rawToken);
}

internal sealed class NameNode(string name) : RequiresNode
{
    public override bool Evaluate(Func<string, bool> hasElement, Func<string, string, bool>? c, int l)
        => hasElement(name);
}
