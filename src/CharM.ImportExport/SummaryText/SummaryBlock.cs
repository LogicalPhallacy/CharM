using CharM.Engine.Creation;
using CharM.RulesDb.Storage;

namespace CharM.ImportExport.SummaryText;

/// <summary>
/// Base for one section of the OCB-compatible SummaryText format
/// (decompiled in <c>decompiled/CharacterBuilder/Character_Builder/SummaryBlock.cs</c>
/// and its concrete subclasses).
///
/// Each block knows how to render itself from a populated
/// <see cref="CharacterSession"/> on the write side, and how to consume the
/// head of an input string on the read side, advancing past whatever it
/// recognized and replaying the encoded choice into a fresh session.
///
/// The driver loop tries every block against the head of the input until one
/// matches; on no-match it rotates the first line to the tail and retries
/// (so blocks can appear out of order).
/// </summary>
public abstract class SummaryBlock
{
    /// <summary>OCB-canonical newline (CRLF).</summary>
    public const string Newline = "\r\n";

    /// <summary>
    /// Append <paramref name="addition"/> to <paramref name="text"/> with a
    /// ", " separator iff <paramref name="text"/> is non-empty. Empty
    /// <paramref name="addition"/> is a no-op. Mirrors OCB's <c>CommaAppend</c>.
    /// </summary>
    protected static void CommaAppend(ref string text, string addition)
    {
        if (string.IsNullOrEmpty(addition)) return;
        if (text.Length > 0) text += ", ";
        text += addition;
    }

    /// <summary>Advance past the next newline in <paramref name="input"/>.</summary>
    protected static void SkipLine(ref string input)
    {
        int idx = input.IndexOf(Newline, StringComparison.Ordinal);
        if (idx != -1) input = input[(idx + Newline.Length)..];
    }

    /// <summary>
    /// Split a comma-separated line into trimmed tokens. Empty tokens are kept
    /// (trimmed to ""), matching the prior <c>Split(',').Select(Trim)</c> blocks.
    /// </summary>
    protected static string[] SplitCommaTokens(string line) =>
        line.Split(',', StringSplitOptions.TrimEntries);

    /// <summary>
    /// Format the OCB " (replaces &lt;OldName&gt; @ &lt;RetrainAtSource&gt;)"
    /// retraining marker. Returns empty string when there's nothing to mark.
    /// </summary>
    protected static string FormatRetraining(string? oldName, string? retrainAtSource)
    {
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(retrainAtSource))
            return string.Empty;
        return $" (replaces {oldName} @ {retrainAtSource})";
    }

    /// <summary>
    /// Strip an OCB retraining marker from a line tail. On match,
    /// <paramref name="text"/> is shortened, and out params return the
    /// captured names; on no match, all out params are null.
    /// </summary>
    protected static void RemoveRetraining(ref string text, out string? retrainTo, out string? retrainAt)
    {
        retrainTo = null;
        retrainAt = null;
        const string mark = " (replaces ";
        int idx = text.IndexOf(mark, StringComparison.Ordinal);
        if (idx == -1) return;

        string tail = text[(idx + mark.Length)..];
        string head = text[..idx];

        int atIdx = tail.IndexOf(" @ ", StringComparison.Ordinal);
        if (atIdx == -1) return; // plain "(replaces X)" without retrain mark — leave intact

        string newName = tail[..atIdx];
        string rest = tail[(atIdx + 3)..];
        int closeIdx = rest.IndexOf(')');
        if (closeIdx == -1) return;

        retrainTo = newName;
        retrainAt = rest[..closeIdx];
        text = head;
    }

    /// <summary>
    /// Strip a plain " (replaces X)" marker (no retrain @) from a line tail.
    /// Returns the captured old element name, or null if no marker present.
    /// </summary>
    protected static string? RemoveReplaces(ref string text)
    {
        const string mark = " (replaces ";
        int idx = text.IndexOf(mark, StringComparison.Ordinal);
        if (idx == -1) return null;

        string tail = text[(idx + mark.Length)..];
        int closeIdx = tail.IndexOf(')');
        if (closeIdx == -1) return null;

        // Ensure this is the simple form (no " @ " before close).
        int atIdx = tail.IndexOf(" @ ", StringComparison.Ordinal);
        if (atIdx != -1 && atIdx < closeIdx) return null;

        string oldName = tail[..closeIdx];
        text = text[..idx];
        return oldName;
    }

    /// <summary>
    /// Render this block for <paramref name="session"/>. May return empty
    /// when the block has nothing to emit (e.g. no class build chosen).
    /// </summary>
    public abstract string Write(CharacterSession session, IRulesDatabase database);

    /// <summary>
    /// Try to consume the head of <paramref name="input"/> and apply its
    /// effect to <paramref name="session"/>. Returns true on match (advancing
    /// <paramref name="input"/>); false leaves <paramref name="input"/>
    /// unchanged.
    /// </summary>
    public abstract bool TryRead(CharacterSession session, IRulesDatabase database, ref string input);
}
