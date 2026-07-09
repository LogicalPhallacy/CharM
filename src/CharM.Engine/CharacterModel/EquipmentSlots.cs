namespace CharM.Engine.CharacterModel;

/// <summary>
/// The character's equippable slots in canonical display order. Shared by the
/// equipment UI and the houserule equip picker so the curated slot list lives
/// in one place. (The richer slot-alias normalization — Body→Chest,
/// Off Hand→Off-Hand, Ring→Ring 1/2, etc. — remains in the session's
/// equipment logic, which is the authoritative resolver.)
/// </summary>
public static class EquipmentSlots
{
    public static readonly IReadOnlyList<string> DisplayOrder =
    [
        "Main Hand", "Off-Hand", "Chest", "Head", "Neck",
        "Arms", "Hands", "Waist", "Feet", "Ring 1", "Ring 2",
    ];
}
