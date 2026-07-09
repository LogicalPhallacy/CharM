using CharM.Engine.Creation;
using CharM.RulesDb.Storage;

namespace CharM.ImportExport;

public static partial class Dnd4eImporter
{
    /// <summary>
    /// Immutable bag of the import-pass values that <see cref="AlignChildren"/>
    /// and the deferred-pick retry loops thread, unchanged, through every
    /// recursive call. Collapsing them removes the 11-argument
    /// <c>AlignChildren(...)</c> bundle that was duplicated at five call sites
    /// and lets the two deferred-pick retry loops share one
    /// <c>ProcessDeferredPicks</c> body parameterized only by the slot finder.
    ///
    /// Only <c>parentNode</c> / <c>parentInternalId</c> / <c>currentLevel</c>
    /// vary across calls; everything here is fixed for the whole import. The
    /// <c>List</c> members (<see cref="Unresolved"/>, <see cref="DeferredPicks"/>)
    /// are intentionally mutable shared references — callers append to and drain
    /// them exactly as before.
    /// </summary>
    private sealed record ImportAlignmentContext(
        CharacterSession Session,
        IRulesDatabase Database,
        IReadOnlyDictionary<string, (string InternalId, int Level)> CharelemMap,
        IReadOnlySet<string> PreservedSwapTargets,
        IReadOnlySet<(string NewId, string OldCharelem)> TallyReplaces,
        IReadOnlySet<string> TallyStandaloneCharelems,
        IReadOnlySet<string> TallySwapperInternalIds,
        List<string> Unresolved,
        List<DeferredPick> DeferredPicks,
        IReadOnlyDictionary<string, int>? TallyAcquisitionLevels);
}
