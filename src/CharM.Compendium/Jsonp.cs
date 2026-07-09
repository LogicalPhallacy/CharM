using System.Text.Json;

namespace CharM.Compendium;

/// <summary>
/// Parses the compendium's JSONP payloads. Every data file has the shape
/// <c>od.reader.jsonp_&lt;kind&gt;(&lt;timestamp&gt;, &lt;arg1&gt;, ...);</c> where each argument
/// after the timestamp is a valid JSON value (number, string, object or
/// array). We strip the function-call wrapper and re-wrap the argument list as
/// a JSON array so the whole thing parses with a single <see cref="JsonDocument"/>.
/// </summary>
public static class Jsonp
{
    /// <summary>
    /// Compendium compression marker (Base85 + LZMA). Not present on any data
    /// payload in the current build (only referenced by the site runtime
    /// <c>res/script.js</c>), so decoding is intentionally unimplemented.
    /// </summary>
    public const string CompressionPrefix = "T>t<;";

    /// <summary>
    /// Parse the JSONP file at <paramref name="text"/> and return the argument
    /// array (index 0 is the timestamp). The returned document owns the parsed
    /// arguments; callers must keep it alive while reading elements.
    /// </summary>
    public static JsonDocument ParseArguments(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        int open = text.IndexOf('(');
        int close = text.LastIndexOf(')');
        if (open < 0 || close < 0 || close <= open)
            throw new FormatException("Not a JSONP payload: missing call parentheses.");

        var inner = text.Substring(open + 1, close - open - 1);
        return JsonDocument.Parse("[" + NormalizeToJson(inner) + "]", LenientOptions);
    }

    private static readonly JsonDocumentOptions LenientOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// JSONP is JavaScript, so compendium data files use JS-only constructs that
    /// are invalid JSON: unquoted object keys (<c>feat2577: "..."</c>) and
    /// single-quoted string values (<c>'&lt;h1&gt;...'</c> with <c>\'</c> escapes
    /// and embedded <c>"</c>). This string-aware pass rewrites both into valid
    /// JSON: bare identifier keys are quoted (only at key positions, never inside
    /// a value), and single-quoted strings are transcoded to double-quoted JSON
    /// strings (unescaping <c>\'</c>, escaping bare <c>"</c>). Already-valid JSON
    /// passes through unchanged.
    /// </summary>
    internal static string NormalizeToJson(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 64);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"')
            {
                // Copy a JSON (double-quoted) string verbatim, respecting escapes.
                sb.Append(c);
                i++;
                while (i < s.Length)
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i]); sb.Append(s[i + 1]); i += 2; continue; }
                    sb.Append(s[i]);
                    if (s[i] == '"') { i++; break; }
                    i++;
                }
                continue;
            }

            if (c == '\'')
            {
                // Transcode a single-quoted JS string into a double-quoted JSON string.
                sb.Append('"');
                i++;
                while (i < s.Length)
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        char next = s[i + 1];
                        if (next == '\'') sb.Append('\'');       // \' -> ' (bare in JSON)
                        else { sb.Append('\\'); sb.Append(next); } // keep other escapes (\n, \\, \" ...)
                        i += 2;
                        continue;
                    }
                    if (s[i] == '"') { sb.Append("\\\""); i++; continue; }   // bare " -> \"
                    if (s[i] == '\'') { sb.Append('"'); i++; break; }         // closing quote
                    sb.Append(s[i]);
                    i++;
                }
                continue;
            }

            if (c == '{' || c == ',')
            {
                sb.Append(c);
                i++;
                int ws = i;
                while (ws < s.Length && char.IsWhiteSpace(s[ws])) ws++;
                if (ws < s.Length && (char.IsLetter(s[ws]) || s[ws] == '_' || s[ws] == '$'))
                {
                    int idStart = ws;
                    int idEnd = idStart;
                    while (idEnd < s.Length && (char.IsLetterOrDigit(s[idEnd]) || s[idEnd] == '_' || s[idEnd] == '$')) idEnd++;
                    int afterId = idEnd;
                    while (afterId < s.Length && char.IsWhiteSpace(s[afterId])) afterId++;
                    if (afterId < s.Length && s[afterId] == ':')
                    {
                        sb.Append(s, i, ws - i);        // leading whitespace
                        sb.Append('"');
                        sb.Append(s, idStart, idEnd - idStart);
                        sb.Append('"');
                        i = idEnd;
                        continue;
                    }
                }
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Return the decompressed value of a payload string. If the string carries
    /// the <see cref="CompressionPrefix"/> the compendium expects a Base85+LZMA
    /// decode, which is not implemented (no data file uses it in this build).
    /// </summary>
    public static string Inflate(string value)
    {
        if (value is not null && value.StartsWith(CompressionPrefix, StringComparison.Ordinal))
            throw new NotSupportedException(
                "Compressed (Base85+LZMA) compendium payloads are not supported; " +
                "no data file in the current build uses them.");
        return value ?? string.Empty;
    }
}
