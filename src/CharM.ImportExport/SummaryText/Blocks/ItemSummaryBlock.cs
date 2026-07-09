using CharM.Engine.Creation;
using CharM.Engine.Rules;
using CharM.RulesDb.Storage;

namespace CharM.ImportExport.SummaryText.Blocks;

/// <summary>
/// "ITEMS\r\n&lt;Item&gt;[ (count)], ...\r\n"
/// plus optional "RITUALS\r\n..." and "FORMULAS\r\n..." sections.
/// See decompiled <c>ItemSummary.cs</c>.
/// </summary>
internal sealed class ItemSummaryBlock : SummaryBlock
{
    public const string ItemsHeader = "ITEMS";
    public const string RitualsHeader = "RITUALS";
    public const string FormulasHeader = "FORMULAS";

    public override string Write(CharacterSession session, IRulesDatabase database)
    {
        var items = new List<string>();
        var rituals = new List<string>();
        var formulas = new List<string>();

        // Equipped loot first (composite names like "Cobra Strike Ki Focus +3").
        foreach (var (_, loot) in session.GetEquippedLoot())
        {
            string display = LootNaming.Compose(loot);
            if (string.IsNullOrWhiteSpace(display)) continue;
            ClassifyAndAdd(loot.Base, display, 1, items, rituals, formulas);
        }

        // Then non-equipped inventory.
        foreach (var inv in session.GetInventory())
        {
            string display = LootNaming.Compose(inv.Item);
            if (string.IsNullOrWhiteSpace(display)) continue;
            int qty = inv.Quantity;
            if (qty <= 0) continue;
            ClassifyAndAdd(inv.Item.Base, display, qty, items, rituals, formulas);
        }

        var sb = new System.Text.StringBuilder();
        EmitList(sb, ItemsHeader, items);
        EmitList(sb, RitualsHeader, rituals);
        EmitList(sb, FormulasHeader, formulas);
        return sb.ToString();
    }

    private static void ClassifyAndAdd(
        RulesElement baseEl, string display, int qty,
        List<string> items, List<string> rituals, List<string> formulas)
    {
        string label = qty > 1 ? $"{display} ({qty})" : display;
        // OCB's classifier reads the loot base's Category field. "Ritual" or
        // "Alchemical Formula" route to dedicated lists. Everything else is
        // a plain item.
        if (baseEl.Fields.TryGetValue("Category", out var category))
        {
            if (string.Equals(category, "Ritual", StringComparison.OrdinalIgnoreCase))
            { rituals.Add(label); return; }
            if (string.Equals(category, "Alchemical Formula", StringComparison.OrdinalIgnoreCase))
            { formulas.Add(label); return; }
        }
        items.Add(label);
    }

    private static void EmitList(System.Text.StringBuilder sb, string header, List<string> items)
    {
        if (items.Count == 0) return;
        sb.Append(header).Append(Newline);
        sb.Append(string.Join(", ", items)).Append(Newline);
    }

    public override bool TryRead(CharacterSession session, IRulesDatabase database, ref string input)
    {
        if (input.StartsWith(ItemsHeader + Newline, StringComparison.Ordinal))
        { input = input[(ItemsHeader.Length + Newline.Length)..]; return true; }
        if (input.StartsWith(RitualsHeader + Newline, StringComparison.Ordinal))
        { input = input[(RitualsHeader.Length + Newline.Length)..]; return true; }
        if (input.StartsWith(FormulasHeader + Newline, StringComparison.Ordinal))
        { input = input[(FormulasHeader.Length + Newline.Length)..]; return true; }

        // Data line: comma-list of items, optional " (N)" count suffix.
        int nl = input.IndexOf(Newline, StringComparison.Ordinal);
        if (nl == -1) return false;
        string line = input[..nl];
        if (line.Length == 0) return false;

        var tokens = SplitCommaTokens(line);
        bool any = false;
        foreach (var raw in tokens)
        {
            string name = raw;
            int qty = 1;
            if (name.EndsWith(")", StringComparison.Ordinal))
            {
                int paren = name.LastIndexOf('(');
                if (paren > 0 && int.TryParse(name.AsSpan(paren + 1, name.Length - paren - 2), out int parsedQty))
                {
                    qty = parsedQty;
                    name = name[..paren].TrimEnd();
                }
            }
            var element = database.FindByNameAndType(name, "Weapon")
                       ?? database.FindByNameAndType(name, "Armor")
                       ?? database.FindByNameAndType(name, "Gear")
                       ?? database.FindByNameAndType(name, "Magic Item")
                       ?? database.FindByNameAndType(name, "Ritual")
                       ?? database.FindByNameAndType(name, "Alchemical Formula");
            if (element is null) continue;

            session.AddInventoryItem(new Engine.Creation.LootItem { Base = element }, qty);
            any = true;
        }
        if (!any) return false;

        input = input[(nl + Newline.Length)..];
        return true;
    }
}
