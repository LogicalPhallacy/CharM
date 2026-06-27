using CharM.Engine.Rules;

namespace CharM.Engine.Evaluation;

/// <summary>
/// Runtime field modification overlay. Stores field value overrides for RulesElements.
/// When a field is queried, the overlay is checked first; if no override exists,
/// the base element field value is used.
///
/// Supports:
/// - Direct value replacement: field = newValue
/// - List addition: append to comma-separated field value
/// - Die increase: upgrade weapon damage dice (d6→d8→d10→d12)
/// - Conditional modifications: only apply if requires expression is met
/// </summary>
public sealed class ModifyOverlay
{
    private readonly Dictionary<(string ElementId, string Field), string> _overrides = new(KeyComparer.Instance);

    /// <summary>
    /// Get the effective field value for an element, checking overlay first.
    /// </summary>
    public string? GetField(RulesElement element, string fieldName)
    {
        var key = (element.InternalId, fieldName);
        if (_overrides.TryGetValue(key, out var value))
            return value;

        return element.Fields.TryGetValue(fieldName, out var baseValue) ? baseValue : null;
    }

    /// <summary>
    /// Get all field modifications recorded for a single element, using a lazily
    /// built per-element index instead of scanning the entire overlay. The index
    /// is invalidated on any mutation.
    /// </summary>
    public IReadOnlyList<(string Field, string Value)> GetModificationsFor(string elementId)
    {
        var index = _byElement ??= BuildByElementIndex();
        return index.TryGetValue(elementId, out var list) ? list : [];
    }

    private Dictionary<string, List<(string Field, string Value)>>? _byElement;

    private Dictionary<string, List<(string Field, string Value)>> BuildByElementIndex()
    {
        var index = new Dictionary<string, List<(string Field, string Value)>>(StringComparer.Ordinal);
        foreach (var ((elementId, field), value) in _overrides)
        {
            if (!index.TryGetValue(elementId, out var list))
                index[elementId] = list = [];
            list.Add((field, value));
        }
        return index;
    }

    /// <summary>
    /// Apply a ModifyDirective to the overlay.
    /// </summary>
    public void Apply(ModifyDirective directive, RulesElement targetElement)
    {
        _byElement = null; // invalidate per-element index on mutation
        if (directive.DieIncrease is > 0)
        {
            ApplyDieIncrease(directive, targetElement);
            return;
        }

        if (directive.ListAddition is not null)
        {
            ApplyListAddition(directive, targetElement);
            return;
        }

        // Direct value replacement. Special-case the "X vs. Y" attack-pattern:
        // multiple ModifyDirectives that target the same Attack field with
        // different stat-option lists (e.g. Deft Blade ships 9 variants —
        // "Strength or Dexterity vs. AC or Reflex", "Strength or Intelligence
        // vs. AC or Reflex", etc. — each gated on a different multiclass /
        // melee-training feat) need to UNION their stat options instead of
        // last-wins overwriting. Otherwise the order in which directives fire
        // chooses the stat for the character, defeating OCB's "best of all
        // unlocked options" behavior. Same shape applies to "X vs. Y" fields
        // on attack-like properties — anything pre-existing with " vs. "
        // gets merged with the new value when they share the same defense.
        var key = (targetElement.InternalId, directive.Field);
        var newValue = directive.Value ?? string.Empty;
        if (_overrides.TryGetValue(key, out var existing)
            && TryMergeAttackStatOptions(existing, newValue, out var merged))
        {
            _overrides[key] = merged;
            return;
        }

        _overrides[key] = newValue;
    }

    /// <summary>
    /// Try to merge two "X (or Y)... vs. D (or E)..." attack-stat strings into
    /// a single string with the union of stat options on the left and the
    /// (identical) defenses on the right. Returns false when either side
    /// doesn't match the pattern or when the defense clauses differ.
    /// </summary>
    internal static bool TryMergeAttackStatOptions(string existing, string incoming, out string merged)
    {
        merged = string.Empty;
        if (string.IsNullOrWhiteSpace(existing) || string.IsNullOrWhiteSpace(incoming))
            return false;

        if (!TrySplitAttackStat(existing, out var existingStats, out var existingDefense)) return false;
        if (!TrySplitAttackStat(incoming, out var incomingStats, out var incomingDefense)) return false;

        // The defense clause must match exactly — different defenses indicate
        // two semantically different attack rewrites (e.g. one targets AC,
        // the other AC or Reflex) that we shouldn't fuse.
        if (!string.Equals(existingDefense, incomingDefense, StringComparison.OrdinalIgnoreCase))
            return false;

        // Union stat options preserving existing order, then append any new
        // options from incoming.
        var union = new List<string>(existingStats);
        foreach (var s in incomingStats)
        {
            bool dup = false;
            foreach (var u in union)
            {
                if (string.Equals(u, s, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
            }
            if (!dup) union.Add(s);
        }

        if (union.Count == existingStats.Count) { merged = existing; return true; }

        merged = string.Join(" or ", union) + " vs. " + existingDefense;
        return true;
    }

    /// <summary>
    /// Split "Strength or Dexterity vs. AC or Reflex" into
    /// ({ "Strength", "Dexterity" }, "AC or Reflex"). Returns false when the
    /// string doesn't contain a " vs. " delimiter.
    /// </summary>
    private static bool TrySplitAttackStat(
        string value,
        out List<string> stats,
        out string defense)
    {
        stats = [];
        defense = string.Empty;

        int vsIndex = value.IndexOf(" vs. ", StringComparison.OrdinalIgnoreCase);
        if (vsIndex < 0)
        {
            // Some data uses "vs " without the dot. Accept that too.
            vsIndex = value.IndexOf(" vs ", StringComparison.OrdinalIgnoreCase);
            if (vsIndex < 0) return false;
            defense = value[(vsIndex + " vs ".Length)..].Trim();
        }
        else
        {
            defense = value[(vsIndex + " vs. ".Length)..].Trim();
        }

        var statClause = value[..vsIndex].Trim();
        foreach (var part in statClause.Split(" or ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            stats.Add(part);
        return stats.Count > 0;
    }

    /// <summary>
    /// Clear all modifications (used when re-evaluating character).
    /// </summary>
    public void Clear()
    {
        _overrides.Clear();
        _byElement = null;
    }

    /// <summary>
    /// Get all active modifications (for debugging/inspection).
    /// </summary>
    public IReadOnlyDictionary<(string ElementId, string Field), string> ActiveModifications => _overrides;

    private void ApplyListAddition(ModifyDirective directive, RulesElement targetElement)
    {
        var key = (targetElement.InternalId, directive.Field);
        string? existing = GetField(targetElement, directive.Field);
        string addition = directive.ListAddition!;

        _overrides[key] = string.IsNullOrEmpty(existing)
            ? addition
            : $"{existing}, {addition}";
    }

    private void ApplyDieIncrease(ModifyDirective directive, RulesElement targetElement)
    {
        string fieldName = directive.Field;
        var key = (targetElement.InternalId, fieldName);
        string? currentDamage = GetField(targetElement, fieldName);

        if (currentDamage is null)
            return;

        _overrides[key] = DieProgression.Increase(currentDamage, directive.DieIncrease!.Value);
    }

    /// <summary>
    /// Comparer that uses ordinal element ID matching and case-insensitive field name matching.
    /// </summary>
    private sealed class KeyComparer : IEqualityComparer<(string ElementId, string Field)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string ElementId, string Field) x, (string ElementId, string Field) y) =>
            string.Equals(x.ElementId, y.ElementId, StringComparison.Ordinal) &&
            string.Equals(x.Field, y.Field, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ElementId, string Field) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.ElementId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Field));
    }
}
