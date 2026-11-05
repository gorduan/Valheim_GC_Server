using GC.Server.Leash;

namespace GC.Server.Outfit;

/// <summary>
/// Partner GC_Tiere (Gorduan, 03.11.2026: „für die Leinen und Pfeifen Leiste braucht es extra Plätze und Regeln, falls diese Mods
/// aktiviert sind“): Jeder Satz hat eine sechste Reihe mit den fünf Plätzen der Leinenleiste, nur für Leinen und Pfeifen.
/// Ohne GC_Tiere gibt es die Reihe nicht. Sind die Leinen abgeschaltet, bleibt die Leiste beim Anlegen unberührt und was in
/// der Reihe liegt, kommt ins Inventar. Eine Leine, die gerade ein Tier hält, kommt in keinen Satz (das Tier wäre sonst los).
/// </summary>
internal static class LeashLink
{
	/// <summary>GC_Tiere ist geladen (nicht im Menü abgeschaltet): Die Sätze haben eine Reihe für die Leinenleiste im Speicher.</summary>
	internal static bool Present => ModTiere.Plugin.Module != null;

	/// <summary>
	/// GC_Tiere läuft auf diesem Server (geladen und von dessen Spieler-Stufe erlaubt, Leinen auch abgeschaltet): Der Schrank
	/// zeigt die Leinenleiste. Gorduan, 03.11.2026: die Leinenplätze nur, wenn die Mod da ist.
	/// </summary>
	internal static bool Loaded => Present && ModTiere.Plugin.Module.Allowed;

	/// <summary>Leinen an: Anlegen tauscht die Reihe mit der Leinenleiste.</summary>
	internal static bool On => Loaded && Leash.Plugin.Enabled != null && Leash.Plugin.On;

	internal static int Slots => LeashBar.Slots;

	internal static bool Fits(ItemDrop.ItemData item) => Loaded && LeashBar.Fits(item);

	internal static bool HoldsAnimal(ItemDrop.ItemData item) => Present && Links.ForItem(item) != null;

	/// <summary>Leinenleiste des eigenen Spielers, null ohne GC_Tiere oder mit abgeschalteten Leinen.</summary>
	internal static Inventory? Bar(Player player) => On ? LeashBar.Of(player) : null;

	internal static bool IsBar(Inventory? inventory) => Present && LeashBar.IsBar(inventory);

	/// <summary>Raster der Leinenleiste im Inventar-Bildschirm (null, solange es nicht angelegt ist).</summary>
	internal static InventoryGrid? BarGrid => On ? LeashBar.Grid : null;
}
