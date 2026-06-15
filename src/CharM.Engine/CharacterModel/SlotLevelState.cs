namespace CharM.Engine.CharacterModel;

/// <summary>
/// <see cref="ICharacterState"/> wrapper that pins the effective level to a
/// specific choice slot's grant level. Used during candidate filtering so a
/// fresh paragon+ character building from level 1 doesn't see paragon-tier
/// feats in the level 1 / 2 / 4 / 6 / 8 / 10 feat slots.
///
/// <para>The "Paragon Tier" and "Epic Tier" Internal Tier elements
/// (<c>ID_INTERNAL_TIER_PARAGON</c> / <c>ID_INTERNAL_TIER_EPIC</c>) are
/// granted globally at L11 / L21 and stay granted for the rest of the
/// character's life. A naive <see cref="ICharacterState.HasElement(string)"/>
/// check therefore passes them for every slot regardless of the slot's
/// grant level, which lets paragon-tier feats slip into heroic-tier feat
/// slots. This adapter pins <see cref="ICharacterState.Level"/> to the
/// slot's effective level and rejects the tier marker elements when the
/// effective level is below their threshold; everything else delegates to
/// the wrapped state.</para>
///
/// <para>This is intentionally a shallow gate — it doesn't try to roll
/// back later-level grant chains, ability scores, or other state. The
/// goal is to catch the common "wrong-tier feat" case, not to simulate a
/// fully consistent earlier-level snapshot.</para>
/// </summary>
internal sealed class SlotLevelState : ICharacterState
{
    private readonly ICharacterState _inner;

    public SlotLevelState(ICharacterState inner, int effectiveLevel)
    {
        _inner = inner;
        Level = effectiveLevel;
    }

    public int Level { get; }

    public bool HasElement(string name)
    {
        // Gate the tier marker elements by the effective level. These are
        // the only elements whose presence is purely a function of
        // character level rather than a user pick or auto-grant.
        if (name.Equals("Paragon Tier", System.StringComparison.OrdinalIgnoreCase))
            return Level >= 11;
        if (name.Equals("Epic Tier", System.StringComparison.OrdinalIgnoreCase))
            return Level >= 21;
        if (name.Equals("Heroic Tier", System.StringComparison.OrdinalIgnoreCase))
            return Level >= 1;
        return _inner.HasElement(name);
    }

    public bool HasElementOfTypeAndCategory(string type, string category)
        => _inner.HasElementOfTypeAndCategory(type, category);

    public int GetAbilityScore(string abilityName)
        => _inner.GetAbilityScore(abilityName);
}
