using System.Text;

namespace CharM.Compendium;

/// <summary>
/// Minimal reader for the MySQL-dialect dump files (<c>ddi*.sql</c>) shipped by
/// the Portable D&amp;D Compendium. Each file is one table:
/// <c>CREATE TABLE `T` ( `Col` type, ... );</c> followed by one or more
/// <c>INSERT INTO `T` VALUES (v,v,...)[,(...)]* ;</c> statements.
///
/// We only need the column names (in order) and the row value tuples, so this
/// is a focused tokenizer rather than a full SQL parser: it reads backtick
/// column names from the CREATE, then scans INSERT value lists respecting
/// single-quoted strings with MySQL backslash escapes. HTML stored in a column
/// (with its own commas, parens and quotes) is preserved because those
/// characters are only significant when NOT inside a quoted string.
/// </summary>
public static class MySqlDump
{
    /// <summary>A parsed table: ordered column names + rows (each row is field values by column index).</summary>
    public sealed record Table(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows);

    /// <summary>Read a dump file, decoding as UTF-8 with a Windows-1252 fallback.</summary>
    public static Table ParseFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(DecodeText(bytes));
    }

    /// <summary>
    /// Decode raw bytes as UTF-8; if the bytes are not valid UTF-8 (the dumps
    /// are Latin-1/Windows-1252), fall back to code page 1252 so accented and
    /// smart-punctuation characters survive instead of becoming U+FFFD.
    /// </summary>
    public static string DecodeText(byte[] bytes)
    {
        // Strip a UTF-8 BOM if present.
        int start = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return strict.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            var win1252 = GetWindows1252();
            return win1252.GetString(bytes, start, bytes.Length - start);
        }
    }

    private static Encoding GetWindows1252()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
        catch
        {
            // Latin-1 is a close-enough fallback if the code-page provider is unavailable.
            return Encoding.Latin1;
        }
    }

    /// <summary>Parse the full text of one dump file.</summary>
    public static Table Parse(string sql)
    {
        var columns = ParseColumns(sql);
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var valuesBody in EnumerateInsertBodies(sql))
            ParseValueTuples(valuesBody, rows);
        return new Table(columns, rows);
    }

    // --- CREATE TABLE columns ---------------------------------------------

    private static List<string> ParseColumns(string sql)
    {
        var columns = new List<string>();
        int create = sql.IndexOf("CREATE TABLE", StringComparison.OrdinalIgnoreCase);
        if (create < 0) return columns;
        int open = sql.IndexOf('(', create);
        if (open < 0) return columns;

        // Split the column-definition region by top-level commas (respecting
        // nested parens like `varchar(50)` and `KEY (...)`). A segment that
        // starts with a backtick is a column; constraint segments (PRIMARY KEY,
        // FULLTEXT, KEY, UNIQUE, CONSTRAINT, INDEX) start with a keyword and are
        // skipped.
        int i = open + 1;
        int depth = 1;
        var seg = new System.Text.StringBuilder();
        while (i < sql.Length && depth > 0)
        {
            char c = sql[i];
            if (c == '(') { depth++; seg.Append(c); }
            else if (c == ')')
            {
                depth--;
                if (depth == 0) { AddColumn(columns, seg); break; }
                seg.Append(c);
            }
            else if (c == ',' && depth == 1) { AddColumn(columns, seg); seg.Clear(); }
            else seg.Append(c);
            i++;
        }
        return columns;
    }

    private static void AddColumn(List<string> columns, System.Text.StringBuilder segBuilder)
    {
        var seg = segBuilder.ToString().Trim();
        if (seg.Length == 0 || seg[0] != '`') return; // not a column definition
        int close = seg.IndexOf('`', 1);
        if (close > 1) columns.Add(seg.Substring(1, close - 1));
    }

    // --- INSERT ... VALUES bodies -----------------------------------------

    /// <summary>Yield the substring after <c>VALUES</c> up to the terminating <c>;</c> for each INSERT.</summary>
    private static IEnumerable<string> EnumerateInsertBodies(string sql)
    {
        int i = 0;
        while (true)
        {
            int ins = sql.IndexOf("INSERT INTO", i, StringComparison.OrdinalIgnoreCase);
            if (ins < 0) yield break;
            int values = sql.IndexOf("VALUES", ins, StringComparison.OrdinalIgnoreCase);
            if (values < 0) yield break;
            int bodyStart = values + "VALUES".Length;
            int end = FindStatementEnd(sql, bodyStart);
            yield return sql.Substring(bodyStart, end - bodyStart);
            i = end + 1;
        }
    }

    /// <summary>Find the <c>;</c> that ends the statement, skipping ones inside quoted strings.</summary>
    private static int FindStatementEnd(string sql, int start)
    {
        bool inStr = false;
        for (int i = start; i < sql.Length; i++)
        {
            char c = sql[i];
            if (inStr)
            {
                if (c == '\\') { i++; continue; }
                if (c == '\'') inStr = false;
            }
            else
            {
                if (c == '\'') inStr = true;
                else if (c == ';') return i;
            }
        }
        return sql.Length;
    }

    // --- value tuple parsing ----------------------------------------------

    /// <summary>Parse <c>(a,b,...),(c,d,...)</c> into rows of unescaped field values.</summary>
    private static void ParseValueTuples(string body, List<IReadOnlyList<string?>> rows)
    {
        int i = 0;
        int n = body.Length;
        while (i < n)
        {
            // Find the next top-level '('.
            while (i < n && body[i] != '(') i++;
            if (i >= n) break;
            i++; // past '('
            var fields = new List<string?>();
            var sb = new StringBuilder();
            bool inStr = false;
            bool fieldIsNull = false;
            bool fieldStarted = false;

            while (i < n)
            {
                char c = body[i];
                if (inStr)
                {
                    if (c == '\\' && i + 1 < n)
                    {
                        sb.Append(Unescape(body[i + 1]));
                        i += 2;
                        continue;
                    }
                    if (c == '\'')
                    {
                        inStr = false;
                        i++;
                        continue;
                    }
                    sb.Append(c);
                    i++;
                    continue;
                }

                if (c == '\'')
                {
                    inStr = true;
                    fieldStarted = true;
                    fieldIsNull = false;
                    i++;
                    continue;
                }
                if (c == ',' || c == ')')
                {
                    // End of a field.
                    fields.Add(fieldIsNull ? null : sb.ToString());
                    sb.Clear();
                    fieldIsNull = false;
                    fieldStarted = false;
                    i++;
                    if (c == ')') break;
                    continue;
                }
                if (char.IsWhiteSpace(c) && !fieldStarted)
                {
                    i++;
                    continue; // leading whitespace before a field
                }
                // Unquoted token (number, NULL, etc.).
                fieldStarted = true;
                if (!inStr)
                {
                    // Accumulate the raw token; detect NULL.
                    int tokStart = i;
                    while (i < n && body[i] != ',' && body[i] != ')') i++;
                    var tok = body.Substring(tokStart, i - tokStart).Trim();
                    if (tok.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                        fieldIsNull = true;
                    else
                        sb.Append(tok);
                }
            }
            rows.Add(fields);
        }
    }

    private static char Unescape(char escaped) => escaped switch
    {
        'n' => '\n',
        'r' => '\r',
        't' => '\t',
        '0' => '\0',
        'b' => '\b',
        _ => escaped, // \' \" \\ and anything else -> literal
    };
}
