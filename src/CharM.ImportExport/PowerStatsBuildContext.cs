using CharM.Engine.Creation;
using CharM.Engine.Evaluation;
using CharM.Engine.Rules;

namespace CharM.ImportExport;

/// <summary>
/// Immutable bag of the per-build-pass values that <see cref="PowerStatsBuilder"/>
/// threads, unchanged, through every <c>BuildEntry</c> / <c>BuildWeaponBlock</c> /
/// <c>TryAddWeaponBlock</c> call while assembling a character's
/// <c>&lt;PowerStats&gt;</c> section. Collapsing them into one context object
/// removes the 13-argument call bundles that were duplicated verbatim at the two
/// <c>BuildEntry</c> call sites and the long parameter lists those helpers carried.
///
/// Only <c>power</c> (and the per-weapon <c>loot</c> / dual-implement bonus /
/// zero-mode flag) vary across calls; everything here is fixed for the whole pass.
/// </summary>
internal sealed record PowerStatsBuildContext(
    StatBlock Stats,
    IReadOnlyList<LootItem> WeaponCandidates,
    CharacterSnapshot Snapshot,
    int CharacterLevel,
    Func<string, string?> SourceNameResolver,
    Func<string, string?> HealingSourceNameResolver,
    Func<string, RulesElement?> SourceElementResolver,
    IReadOnlySet<string>? WieldedLootKeys,
    bool CharacterHasMultiWieldingState,
    IReadOnlyDictionary<string, int>? EquippedCompositeKeyCounts,
    bool HasDualImplementSpellcaster,
    IReadOnlyDictionary<string, string>? TextStrings,
    int? PrecomputedBeastAttackBonus)
{
    /// <summary>The active modify-overlay — always the snapshot's builder overlay.</summary>
    public ModifyOverlay Overlay => Snapshot.Builder.Overlay;
}
