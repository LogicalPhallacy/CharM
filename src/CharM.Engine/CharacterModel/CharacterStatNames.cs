namespace CharM.Engine.CharacterModel;

/// <summary>
/// Canonical 4e character stat-name strings used as keys into the computed
/// <see cref="StatBlock"/>. Centralized so UI/print/export consumers reference
/// one source instead of repeating the literals (a typo like "Fortitude" vs
/// "Fortitude Defense" silently yields a zero stat).
/// </summary>
public static class CharacterStatNames
{
    public const string ArmorClass = "AC";
    public const string FortitudeDefense = "Fortitude Defense";
    public const string ReflexDefense = "Reflex Defense";
    public const string WillDefense = "Will Defense";

    public const string Initiative = "Initiative";
    public const string Speed = "Speed";
    public const string PassiveInsight = "Passive Insight";
    public const string PassivePerception = "Passive Perception";

    /// <summary>The four defenses as (stat-name, short label) in display order.</summary>
    public static readonly IReadOnlyList<(string Stat, string Label)> Defenses =
    [
        (ArmorClass, "AC"),
        (FortitudeDefense, "FORT"),
        (ReflexDefense, "REFL"),
        (WillDefense, "WILL"),
    ];

    /// <summary>The common derived/passive stats as (stat-name, label) in display order.</summary>
    public static readonly IReadOnlyList<(string Stat, string Label)> Derived =
    [
        (Initiative, "Initiative"),
        (Speed, "Speed"),
        (PassiveInsight, "Passive Insight"),
        (PassivePerception, "Passive Perception"),
    ];
}
