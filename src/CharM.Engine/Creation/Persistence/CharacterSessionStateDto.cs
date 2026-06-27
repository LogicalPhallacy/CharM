using System.Text.Json.Serialization;

namespace CharM.Engine.Creation.Persistence;

/// <summary>
/// Lossless, replay-based serialization of an editable <see cref="CharacterSession"/>
/// that was built from scratch in the app (i.e. NOT imported from a .dnd4e file).
///
/// <para>
/// The autosave/restore slot previously persisted the character as exported
/// <c>.dnd4e</c> bytes. That is lossless for IMPORTED characters (their grant
/// tree re-derives the open choice slots on re-import) but LOSSY for
/// app-created partials: a half-built character's export has no grant tree, so
/// re-import reconstructs zero pending choices and the character comes back
/// looking "complete". This DTO captures the wizard's replay inputs (the ordered
/// choice history + scores + level + sources) plus equipment, retraining swaps
/// and houserule grants, so restore via <see cref="CharacterSession.RestoreFromReplayState"/>
/// regenerates the exact pending-choice state.
/// </para>
/// </summary>
public sealed class CharacterSessionStateDto
{
    public int SchemaVersion { get; set; } = 1;
    public int Level { get; set; } = 1;
    public string Name { get; set; } = "New Character";

    /// <summary>Base ability scores in <see cref="Ability"/> enum order, or null if unset.</summary>
    public int[]? AbilityScores { get; set; }

    public string? SourceFilter { get; set; }
    public List<string>? EnabledSources { get; set; }
    public bool AutoFillSelectDefaults { get; set; } = true;
    public bool IsCharacterHouseruled { get; set; }

    public Dictionary<string, string> Details { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TextStrings { get; set; } = new(StringComparer.Ordinal);

    public List<ChoiceRecordDto> Choices { get; set; } = [];
    public List<string> GrabbagGrantIds { get; set; } = [];
    public List<FreeGrantDto> Supplements { get; set; } = [];
    public List<FreeGrantDto> UserEditPicks { get; set; } = [];

    public List<EquippedItemDto> Equipped { get; set; } = [];
    public List<InventoryItemDto> Inventory { get; set; } = [];
    public List<ReplacementLevelDto> Replacements { get; set; } = [];
    public List<HouseruleGrantDto> HouseruleGrants { get; set; } = [];
}

/// <summary>A single user choice: the chosen element id plus the (stable) slot identity for replay.</summary>
public sealed class ChoiceRecordDto
{
    public string ElementId { get; set; } = "";
    public int Sequence { get; set; }
    public int Level { get; set; } = 1;
    public ChoiceSlotDto Slot { get; set; } = new();
}

/// <summary>
/// The stable subset of a <see cref="ChoiceSlot"/> used by the wizard to re-find
/// the matching pending slot on replay (see <c>FindMatchingSlot</c>). The
/// per-instance <c>DirectiveKey</c> is intentionally omitted — it embeds a
/// runtime hashcode and never matches across a wizard rebuild, so replay relies
/// on owner/category/name matching instead.
/// </summary>
public sealed class ChoiceSlotDto
{
    public string ElementType { get; set; } = "";
    public string? Name { get; set; }
    public string? DisplayLabel { get; set; }
    public string? Category { get; set; }
    public string? OwnerInternalId { get; set; }
    public string? Requires { get; set; }
    public int Number { get; set; } = 1;
    public bool Optional { get; set; }
    public bool Existing { get; set; }
    public int? Level { get; set; }
}

/// <summary>A free grant / supplement / user-edit pick replayed into the wizard.</summary>
public sealed class FreeGrantDto
{
    public string ElementId { get; set; } = "";
    public int AtLevel { get; set; }
    public string? SlotOwnerInternalId { get; set; }
}

/// <summary>A composite loot item (base + optional enchantment + optional augment) by element id.</summary>
public sealed class LootItemDto
{
    public string BaseId { get; set; } = "";
    public string? EnchantmentId { get; set; }
    public string? AugmentId { get; set; }
    public string? WornCategoryId { get; set; }
    public string? CompositeName { get; set; }
    public string? DamageOverride { get; set; }
    public bool ShowPowerCard { get; set; } = true;
    public double? Weight { get; set; }
    public string? AugmentXml { get; set; }
    public bool IsInAlternateSlot { get; set; }
}

public sealed class EquippedItemDto
{
    public string Slot { get; set; } = "";
    public LootItemDto Item { get; set; } = new();
}

public sealed class InventoryItemDto
{
    public LootItemDto Item { get; set; } = new();
    public int Quantity { get; set; } = 1;
}

public sealed class ReplacementLevelDto
{
    public int Level { get; set; }
    public List<ReplacementDto> Replacements { get; set; } = [];
}

public sealed class ReplacementDto
{
    public string OldInternalId { get; set; } = "";
    public string NewInternalId { get; set; } = "";
    public string? NewName { get; set; }
    public string? NewType { get; set; }
    public bool PreserveOld { get; set; }
    public string? SwapOwnerInternalId { get; set; }
}

public sealed class HouseruleGrantDto
{
    public string ElementId { get; set; } = "";
    public int AtLevel { get; set; }
    public string Kind { get; set; } = nameof(HouseruleGrantKind.RulesElement);
    public string? Slot { get; set; }
    public int Quantity { get; set; } = 1;
}

/// <summary>Source-generated, trim-safe JSON metadata for the session-state DTO graph.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CharacterSessionStateDto))]
public sealed partial class CharacterSessionStateJsonContext : JsonSerializerContext
{
}
