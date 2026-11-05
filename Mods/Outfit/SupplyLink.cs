using GC.Server.Supplies;

namespace GC.Server.Outfit;

/// <summary>
/// Partner GC_Vorratsleiste (Gorduan, 03.11.2026: „sollten wir auch dem Spielerrucksack diese Leiste hinzufügen … und unser Schrank
/// kann sie speichern“): Die Vorräte eines Satzes tauschen beim Anlegen mit der Vorratsleiste des Spielers, der angelegte
/// Satz zeigt sie live. Ohne die Mod haben die Sätze keine Vorräte-Reihe (Gorduan: sonst ließe sich die Regel unterlaufen); ist
/// die Leiste abgeschaltet, kommt, was im Satz liegt, beim Anlegen ins Inventar.
/// </summary>
internal static class SupplyLink
{
	/// <summary>GC_Vorratsleiste ist geladen und auf diesem Server erlaubt: Die Sätze zeigen ihre Vorräte.</summary>
	internal static bool Loaded => ModVorratsleiste.Plugin.Module != null && ModVorratsleiste.Plugin.Module.Allowed;

	internal static bool On => Loaded && SupplyBar.Enabled != null && SupplyBar.On;

	/// <summary>Geltende Vorratsplätze (Server-Regel der Vorratsleiste, 1–64, für Rucksack und Sätze gleichermaßen); ohne die Mod 8.</summary>
	internal static int Slots => Loaded ? SupplyBar.Count : SupplyBar.DefaultSlots;

	/// <summary>Gezeigte Vorratsplätze der Leiste: die geltenden und belegte dahinter.</summary>
	internal static int Shown(Inventory bar) => SupplyBar.Shown(bar);

	/// <summary>Vorratsleiste des eigenen Spielers, null ohne die Mod oder mit abgeschalteter Leiste.</summary>
	internal static Inventory? Bar(Player player) => On ? SupplyBar.Of(player) : null;

	internal static bool IsBar(Inventory? inventory) => Loaded && SupplyBar.IsBar(inventory);

	/// <summary>Raster der Vorratsleiste im Inventar-Bildschirm (null, solange es nicht angelegt ist).</summary>
	internal static InventoryGrid? BarGrid => On ? SupplyBar.Grid : null;

	/// <summary>Rechtsklick wie in der Leiste: essen, trinken, Munition anlegen.</summary>
	internal static void Use(ItemDrop.ItemData item) => SupplyBar.Use(item);
}
