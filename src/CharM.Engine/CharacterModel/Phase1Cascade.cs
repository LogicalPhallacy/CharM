using CharM.Engine.Rules;

namespace CharM.Engine.CharacterModel;

/// <summary>
/// Shared Phase-1 (skeleton) cascade helpers used by the three replayers of the
/// grant/select directive chain — <c>CharacterCreationWizard</c> (interactive
/// build), <c>CharacterBuilder</c> (snapshot replay), and <c>ExportTreeBuilder</c>
/// (export replay).
///
/// NOTE: only the genuinely-identical sub-step is shared here. The three
/// <c>ExecutePhase1</c> directive-switch bodies are deliberately NOT unified:
/// they encode different responsibilities (the wizard does select-slot keying,
/// <c>AutoFillSelectIfPossible</c>, default-suggestion recording and Freebee
/// stat handling; the builder collects Phase-2 work, buckets future-level
/// grants and acknowledges retraining <c>replace</c> directives; the exporter
/// has its own replay shape). Forcing them behind a common abstraction would
/// add more branching than the duplication removes and risk the
/// heavily-validated import/export parity — so they stay separate.
/// </summary>
internal static class Phase1Cascade
{
    /// <summary>
    /// Retry grants whose <c>requires</c> condition failed during the initial
    /// walk, looping to a fixed point. Identical across all three replayers
    /// except that the caller supplies its own tree, its own deferred-grant
    /// list, and a callback to its own <c>ExecutePhase1</c>.
    /// </summary>
    /// <returns><c>true</c> if any deferred grant fired this call.</returns>
    public static bool ProcessDeferredGrants(
        CharacterElementTree tree,
        List<(GrantDirective Grant, CharacterElement Parent, int Level)> deferredGrants,
        Action<RulesElement, CharacterElement, int> executePhase1)
    {
        bool anyProgress = false;
        bool progress = true;
        int maxIterations = deferredGrants.Count + 1; // safety valve
        while (progress && deferredGrants.Count > 0 && maxIterations-- > 0)
        {
            progress = false;
            for (int i = deferredGrants.Count - 1; i >= 0; i--)
            {
                var (grant, parent, deferredLevel) = deferredGrants[i];
                var child = tree.ProcessGrant(grant, parent, deferredLevel);
                if (child?.RulesElement is { } grantedElement)
                {
                    deferredGrants.RemoveAt(i);
                    executePhase1(grantedElement, child, deferredLevel);
                    progress = true;
                    anyProgress = true;
                }
            }
        }

        return anyProgress;
    }
}
