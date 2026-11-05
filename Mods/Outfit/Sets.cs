using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GC.Shared;
using HarmonyLib;

namespace GC.Server.Outfit;

/// <summary>
/// Ausrüstungs-Sätze am Kleiderschrank (Gorduan, 02.11.2026: „4 Auswahlmöglichkeiten … wo man richtige Items wie Rüstung aber auch
/// Inventarleiste (1-8) + 24 Storage einfügen kann. So kann man … von einem Nahkämpfer zu einem Magier wechseln, ohne seinen
/// alten Build zu verändern“). Dazu bleibt der Reiter „Stil“, das Outfit, nur fürs Aussehen.
///
/// Vier Sätze mit Namen (umbenennbar), einer davon ist getragen: Er liegt am Spieler. Jeder andere Satz liegt im Charakter
/// (Player.m_customData wie das Outfit, kein Gewicht, bleibt beim Tod; jeder Schrank zeigt dieselben) als Inventar 8 × 11:
///  * Reihe 0, Ausrüstung: Kopf, Brust, Beine, Umhang, Gürtel, Schmuckstück, rechte und linke Hand. Was hier liegt, wird beim
///    Anlegen angezogen.
///  * Reihe 1, Schnellleiste (1–8): Valheims erste Inventarreihe, an denselben Plätzen. Ein Item, das beim Ablegen angezogen
///    war, ist markiert (m_equipped) und wird wieder angezogen.
///  * Reihen 2–9, Vorräte, nur mit GC_Vorratsleiste: getauscht mit der Vorratsleiste, Platz für Platz (SupplyLink; Gorduan: „dem
///    Spielerrucksack diese Leiste hinzufügen … und unser Schrank kann sie speichern“). Es gelten so viele Plätze wie in der
///    Leiste (Server-Regel, Standard 8, bis 64; Gorduan, 04.11.2026).
///  * Reihe 10, nur mit GC_Tiere: die fünf Plätze der Leinenleiste (LeashLink), getauscht mit der Leiste.
/// Anlegen von Satz k: Was der Spieler in der Schnellleiste, der Vorrats- und der Leinenleiste hat, und angezogene Ausrüstung
/// von weiter unten, wandern in den getragenen Satz; Satz k kommt an dieselben Plätze, seine Ausrüstung in freie Plätze darunter
/// (ValheimPlus: 9 Reihen), dann wird angezogen. Reicht der Platz nicht, passiert nichts. Weiter unten bleibt alles beim
/// Spieler (Material, Beute). Der angelegte Satz zeigt im Schrank live, was der Spieler hat (LiveSet).
/// Was hinein darf (Gorduan, 03.11.2026: „nur Nahrung, Tränke etc., also Konsumgüter, keine festen Items“, Schnellleiste auch Waffen
/// und Werkzeuge, Munition zählt zu den Vorräten): Schnellleiste Hand-Items und Vorräte, die Vorräte nur Essen, Tränke, Mets
/// und Munition, nie Material oder Trophäen; dazu nur, was durchs Portal darf. Sonst wäre jeder Schrank ein gewichtsloses
/// Lager, das an jedem Schrank bereitsteht. Was nicht hinein darf, bleibt beim Anlegen beim Spieler.
/// Server-Regel SetsEnabled (Standard an): aus = Sätze lassen sich nur noch leeren, nicht anlegen.
/// </summary>
internal static class Sets
{
	internal const int Count = 4;
	internal const int Width = 8;
	internal const int GearRow = 0;
	internal const int HotbarRow = 1;
	internal const int SupplyRow = 2;
	internal const int SupplyRows = 8; // wie die Vorratsleiste: bis 64 Plätze, acht je Reihe
	internal const int LeashRow = SupplyRow + SupplyRows;

	/// <summary>Reihen eines Satzes: mit GC_Tiere elf (Leinenleiste), sonst zehn.</summary>
	internal static int Height => LeashLink.Present ? LeashRow + 1 : LeashRow;
	internal const int GearSlots = 8;

	internal static bool IsSupplyRow(int y) => y >= SupplyRow && y < SupplyRow + SupplyRows;

	/// <summary>Nummer eines Vorratsplatzes im Satz (wie in der Vorratsleiste, Reihe für Reihe).</summary>
	internal static int SupplyIndex(Vector2i pos) => (pos.y - SupplyRow) * Width + pos.x;

	/// <summary>Art einer Reihe für Texte: 0 Ausrüstung, 1 Schnellleiste, 2 Vorräte, 3 Leinen.</summary>
	internal static int Kind(int y) => y == GearRow ? 0 : y == HotbarRow ? 1 : IsSupplyRow(y) ? 2 : 3;

	internal const int Helmet = 0;
	internal const int Chest = 1;
	internal const int Legs = 2;
	internal const int Cape = 3;
	internal const int Belt = 4;
	internal const int Trinket = 5;
	internal const int RightHand = 6;
	internal const int LeftHand = 7;

	/// <summary>Valheims erste Inventarreihe (Schnellleiste) gehört zum Satz; die Vorräte kommen aus der Vorratsleiste.</summary>
	internal const int PlayerRows = 1;

	private const string DataKey = "gc_sets";
	private const string BrokenKey = "gc_sets_kaputt";

	/// <summary>
	/// 3 seit 04.11.2026 (Vorräte in den Reihen 2–9, Leinenleiste in Reihe 10); 2 hatte acht Vorräte in Reihe 2 und die
	/// Leinenleiste in Reihe 3; 1 hatte 24 Vorräte in den Reihen 2–4 und die Leinenleiste in Reihe 5.
	/// </summary>
	private const int Version = 3;

	internal static ConfigEntry<bool> Enabled = null!;

	private static Player? owner;
	private static Inventory[]? stored;
	private static string[] names = new string[Count];
	private static int worn;

	internal static bool On => Enabled.Value && ModOutfit.Plugin.Module.Allowed;

	internal static void Init(ConfigFile config)
	{
		Enabled = Gc.BindSynced(config, "1 - Outfit", "SetsEnabled", true, "Gorduan, 02.11.2026: vier Ausrüstungs-Sätze am Kleiderschrank (Rüstung, Schnellleiste, Vorräte und Leinenleiste), mit einem Klick angelegt. Aus: Sätze lassen sich nur noch leeren.");
	}

	// ------------------------------------------------------------------ Daten

	/// <summary>Sätze des eigenen Spielers, beim ersten Zugriff aus dem Charakter geladen.</summary>
	internal static Inventory[]? Of(Player? player)
	{
		if (player == null || player != Player.m_localPlayer)
		{
			return null;
		}
		if (owner != player || stored == null)
		{
			owner = player;
			stored = Load(player);
		}
		return stored;
	}

	internal static int Worn => worn;

	/// <summary>Name des Satzes; ohne eigenen Namen „Alltag“ für den ersten (Gorduan, 03.11.2026), sonst „Satz 2“ usw.</summary>
	internal static string Name(int set) => set >= 0 && set < Count && !string.IsNullOrEmpty(names[set]) ? names[set]
		: set == 0 ? GcLoc.Get("gcof_set_first") : GcLoc.Get("gcof_set_default", (set + 1).ToString());

	internal static int IndexOf(Inventory? inventory)
	{
		if (inventory == null || stored == null)
		{
			return -1;
		}
		return Array.IndexOf(stored, inventory);
	}

	internal static bool IsSet(Inventory? inventory) => IndexOf(inventory) >= 0;

	private static Inventory[] Load(Player player)
	{
		Inventory[] sets = new Inventory[Count];
		for (int i = 0; i < Count; i++)
		{
			sets[i] = new Inventory("GC_Set" + i, null, Width, Height);
		}
		names = new string[Count];
		worn = 0;
		if (player.m_customData.TryGetValue(DataKey, out string data) && data.Length > 0)
		{
			try
			{
				ZPackage pkg = new(Convert.FromBase64String(data));
				int version = pkg.ReadInt();
				worn = Math.Max(0, Math.Min(Count - 1, pkg.ReadInt()));
				int saved = 0;
				int homeless = 0;
				for (int i = 0; i < Count && version >= 1; i++)
				{
					names[i] = pkg.ReadString();
					ZPackage items = pkg.ReadPackage();
					int expected = CountIn(items);
					if (version < Version)
					{
						// alte Reihen: Leinenleiste in Reihe 5 (Version 1) bzw. 3 (Version 2), davor alles an seinem Platz
						int oldLeash = version == 1 ? 5 : 3;
						Inventory old = new("GC_SetOld", null, Width, oldLeash + 1);
						old.Load(new ZPackage(items.GetArray()));
						saved += expected - old.NrOfItems();
						homeless += FromOlder(old, sets[i], player, oldLeash);
					}
					else
					{
						sets[i].Load(new ZPackage(items.GetArray()));
						saved += expected - sets[i].NrOfItems();
					}
				}
				if (saved > 0 || homeless > 0)
				{
					player.m_customData[BrokenKey] = data;
					Outfits.Log.LogWarning($"Sätze: {saved} Teil(e) unbekannt, {homeless} ohne Platz; der alte Stand liegt unter {BrokenKey}.");
				}
			}
			catch (Exception e)
			{
				player.m_customData[BrokenKey] = data;
				Outfits.Log.LogWarning($"Sätze: gespeicherter Stand nicht lesbar ({e.Message}), er liegt unter {BrokenKey}.");
			}
		}
		foreach (Inventory set in sets)
		{
			set.m_onChanged += () => Save(player);
		}
		return sets;
	}

	/// <summary>
	/// Satz einer älteren Version in die Reihen von heute: Ausrüstung, Schnellleiste und Vorräte bleiben an ihrem Platz (die
	/// Vorräte von Version 1 in den Reihen 2–4 wie die von Version 2 in Reihe 2 liegen heute in denselben Plätzen), die
	/// Leinenleiste rückt von Reihe oldLeash nach Reihe 10. Was keinen Platz hat (ohne GC_Tiere keine Leinenleiste), kommt ins
	/// Inventar des Spielers. Ergebnis: Teile, die auch dort keinen Platz fanden (der alte Stand bleibt dann gesichert).
	/// </summary>
	private static int FromOlder(Inventory old, Inventory set, Player player, int oldLeash)
	{
		int homeless = 0;
		foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(old.GetAllItems()))
		{
			Vector2i p = item.m_gridPos;
			int row = p.y == oldLeash ? LeashRow : p.y;
			Vector2i? to = row < set.GetHeight() && set.GetItemAt(p.x, row) == null ? new Vector2i(p.x, row)
				: IsSupplyRow(row) ? FreeInRow(set, SupplyRow) : null;
			if (to is Vector2i at)
			{
				item.m_gridPos = at;
				set.m_inventory.Add(item);
				continue;
			}
			item.m_equipped = false;
			if (!player.GetInventory().AddItem(item))
			{
				homeless++;
			}
		}
		return homeless;
	}

	private static Vector2i? FreeInRow(Inventory inventory, int y)
	{
		for (int x = 0; x < inventory.GetWidth(); x++)
		{
			if (inventory.GetItemAt(x, y) == null)
			{
				return new Vector2i(x, y);
			}
		}
		return null;
	}

	private static int CountIn(ZPackage items)
	{
		try
		{
			ZPackage copy = new(items.GetArray());
			copy.ReadInt();
			return copy.ReadUShort();
		}
		catch
		{
			return 0;
		}
	}

	/// <summary>In den Charakter schreiben (bei jeder Änderung und vor jedem Speichern).</summary>
	internal static void Save(Player player)
	{
		if (stored == null || owner != player)
		{
			return;
		}
		bool empty = worn == 0;
		foreach (Inventory set in stored)
		{
			empty &= set.NrOfItems() == 0;
		}
		foreach (string name in names)
		{
			empty &= string.IsNullOrEmpty(name);
		}
		if (empty)
		{
			player.m_customData.Remove(DataKey);
			return;
		}
		ZPackage pkg = new();
		pkg.Write(Version);
		pkg.Write(worn);
		for (int i = 0; i < Count; i++)
		{
			pkg.Write(names[i] ?? "");
			ZPackage items = new();
			stored[i].Save(items);
			pkg.Write(items);
		}
		player.m_customData[DataKey] = Convert.ToBase64String(pkg.GetArray());
	}

	internal static void Forget(Player player)
	{
		if (owner == player || player == Player.m_localPlayer)
		{
			owner = null;
			stored = null;
			names = new string[Count];
			worn = 0;
		}
	}

	internal static void Rename(Player player, int set, string name)
	{
		if (set < 0 || set >= Count || Of(player) == null)
		{
			return;
		}
		name = name.Trim();
		names[set] = name.Length > 24 ? name.Substring(0, 24) : name;
		Save(player);
	}

	// ------------------------------------------------------------------ Welche Plätze?

	/// <summary>Ausrüstungsplatz eines Items in Reihe 0 (−1 = keiner): Rüstung nach Art, sonst Hand.</summary>
	internal static int GearSlot(ItemDrop.ItemData? item, Player? player = null)
	{
		if (item?.m_shared == null)
		{
			return -1;
		}
		switch (item.m_shared.m_itemType)
		{
			case ItemDrop.ItemData.ItemType.Helmet: return Helmet;
			case ItemDrop.ItemData.ItemType.Chest: return Chest;
			case ItemDrop.ItemData.ItemType.Legs: return Legs;
			case ItemDrop.ItemData.ItemType.Shoulder: return Cape;
			case ItemDrop.ItemData.ItemType.Utility: return Belt;
			case ItemDrop.ItemData.ItemType.Trinket: return Trinket;
			case ItemDrop.ItemData.ItemType.Ammo:
			case ItemDrop.ItemData.ItemType.AmmoNonEquipable:
				return -1; // Munition bleibt in der Leiste oder beim Spieler
		}
		if (!item.IsEquipable())
		{
			return -1;
		}
		if (player != null && player.m_leftItem == item)
		{
			return LeftHand;
		}
		ItemDrop.ItemData.ItemType type = item.m_shared.m_itemType;
		return type == ItemDrop.ItemData.ItemType.Shield || type == ItemDrop.ItemData.ItemType.Bow || type == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ? LeftHand : RightHand;
	}

	/// <summary>Vorräte: Essen, Tränke und Mets, Munition (Pfeile, Bolzen, Köder).</summary>
	internal static bool IsSupply(ItemDrop.ItemData item)
	{
		ItemDrop.ItemData.ItemType type = item.m_shared.m_itemType;
		return type == ItemDrop.ItemData.ItemType.Consumable || type == ItemDrop.ItemData.ItemType.Ammo || type == ItemDrop.ItemData.ItemType.AmmoNonEquipable;
	}

	/// <summary>
	/// Darf das Item in diese Reihe unter der Ausrüstung? Schnellleiste: Hand-Items und Vorräte; Vorräte: nur Vorräte;
	/// Leinenleiste: nur Leinen und Pfeifen.
	/// </summary>
	internal static bool Allows(ItemDrop.ItemData item, int row)
	{
		if (row <= GearRow || row >= Height)
		{
			return false;
		}
		if (row == LeashRow)
		{
			return LeashLink.Fits(item);
		}
		if (IsSupplyRow(row))
		{
			return SupplyLink.Loaded && IsSupply(item);
		}
		return IsSupply(item) || (row == HotbarRow && GearSlot(item) >= RightHand);
	}

	internal static bool SameArea(int rowA, int rowB) => rowA == rowB || (IsSupplyRow(rowA) && IsSupplyRow(rowB));

	/// <summary>
	/// Darf das Item überhaupt aus Inventar oder Leiste in einen Satz (Portal, Quest, Leine mit Tier)? Fixierte Items (GC_Schloesser)
	/// dürfen mit (Gorduan, 04.11.2026).
	/// </summary>
	private static bool MayLeave(ItemDrop.ItemData item) => item.m_shared.m_teleportable && !item.m_shared.m_questItem && !LeashLink.HoldsAnimal(item);

	/// <summary>Darf das Item an diese Stelle eines Satzes? null = ja, sonst der Grund.</summary>
	internal static string? Refuse(int set, ItemDrop.ItemData item, Vector2i pos)
	{
		if (set == worn)
		{
			return GcLoc.Get("gcof_set_worn_drop", Name(set));
		}
		if (!On)
		{
			return GcLoc.Get("gcof_sets_off");
		}
		if (item.m_shared.m_questItem || !item.m_shared.m_teleportable)
		{
			return GcLoc.Get("gcof_set_no_portal");
		}
		if (LeashLink.HoldsAnimal(item))
		{
			return GcLoc.Get("gcof_set_leash_animal");
		}
		if (!IsSupply(item) && GearSlot(item) < 0)
		{
			return GcLoc.Get("gcof_set_no_material");
		}
		if (IsSupplyRow(pos.y) && SupplyIndex(pos) >= SupplyLink.Slots)
		{
			return GcLoc.Get("gcof_set_supplies_locked"); // weniger Vorratsplätze eingestellt: nur noch leeren
		}
		if (pos.y == LeashRow)
		{
			if (pos.x >= LeashLink.Slots || !Allows(item, pos.y))
			{
				return GcLoc.Get("gcof_set_leash_no");
			}
		}
		else if (pos.y == GearRow)
		{
			int slot = GearSlot(item);
			bool hand = pos.x == RightHand || pos.x == LeftHand;
			if (slot < 0 || (hand ? slot < RightHand : slot != pos.x))
			{
				return GcLoc.Get("gcof_set_not_fit", SlotName(pos.x));
			}
		}
		else if (!Allows(item, pos.y))
		{
			return GcLoc.Get(pos.y == HotbarRow ? "gcof_set_hotbar_no" : "gcof_set_supplies_no");
		}
		return null;
	}

	internal static string SlotName(int slot) => GcLoc.Get("gcof_set_slot_" + slot);

	// ------------------------------------------------------------------ Anlegen

	/// <summary>
	/// Satz k anlegen: Der getragene Satz wandert aus den ersten zwei Reihen (und angezogene Ausrüstung von weiter unten) und aus
	/// der Leinenleiste in seinen Speicher, Satz k an den Spieler. Reicht der Platz nicht, passiert nichts. Ergebnis: Meldung
	/// für den Spieler.
	/// </summary>
	internal static string Wear(Player player, int k)
	{
		Inventory[]? sets = Of(player);
		if (sets == null || k < 0 || k >= Count)
		{
			return "";
		}
		if (!On)
		{
			return GcLoc.Get("gcof_sets_off");
		}
		if (k == worn)
		{
			return GcLoc.Get("gcof_set_already", Name(k));
		}
		Inventory main = player.GetInventory();
		Inventory away = sets[worn];
		Inventory next = sets[k];
		if (away.NrOfItems() > 0)
		{
			return GcLoc.Get("gcof_set_worn_full", Name(worn)); // kann nur durch alte Fehler entstehen; nichts überschreiben
		}
		// Leisten neben dem Inventar, die ein Satz in eigenen Reihen tauscht: Vorräte (GC_Vorratsleiste) und Leinen (GC_Tiere);
		// ohne die Mod oder abgeschaltet ist Bar null, dann kommt der Inhalt des Satzes ins Inventar
		SideBar[] sides =
		{
			new(SupplyRow, SupplyRows, SupplyLink.Slots, SupplyLink.Bar(player), IsSupply),
			new(LeashRow, 1, LeashLink.Slots, LeashLink.Bar(player), LeashLink.Fits),
		};
		int width = Math.Min(main.GetWidth(), Width);
		int rows = Math.Min(main.GetHeight(), PlayerRows);

		// was geht weg: aus der Schnellleiste, was dort in den Satz darf; angezogene Ausrüstung (auch von weiter unten) in Reihe 0;
		// der Rest der Schnellleiste bleibt beim Spieler; aus Vorrats- und Leinenleiste, was passt (keine Leine mit Tier)
		List<ItemDrop.ItemData> leaveRows = new();
		List<ItemDrop.ItemData> stay = new();
		List<(ItemDrop.ItemData Item, int Slot)> leaveGear = new();
		bool[] gearTaken = new bool[GearSlots];
		int gearBelow = 0;
		foreach (ItemDrop.ItemData item in main.GetAllItems())
		{
			Vector2i p = item.m_gridPos;
			bool inRows = p.y < rows && p.x < width;
			bool mayLeave = MayLeave(item);
			if (inRows && mayLeave && Allows(item, p.y + 1))
			{
				leaveRows.Add(item);
				continue;
			}
			int slot = item.m_equipped && mayLeave ? GearSlot(item, player) : -1;
			if (slot >= 0 && !gearTaken[slot])
			{
				gearTaken[slot] = true;
				leaveGear.Add((item, slot));
				gearBelow += inRows ? 0 : 1;
			}
			else if (inRows)
			{
				stay.Add(item);
			}
		}
		foreach (SideBar side in sides)
		{
			if (side.Bar == null)
			{
				continue;
			}
			foreach (ItemDrop.ItemData item in side.Bar.GetAllItems())
			{
				int index = side.BarIndex(item.m_gridPos);
				if (index < 0 || index >= side.Slots)
				{
					continue;
				}
				if (MayLeave(item) && side.Fits(item))
				{
					side.Leave.Add(item);
				}
				else
				{
					side.Stays[index] = true;
				}
			}
		}

		// was kommt: die Schnellleiste an dieselben Plätze, Vorräte und Leinen an dieselben Plätze ihrer Leiste, die Ausrüstung
		// aus Reihe 0 und was keinen Platz hat (Leiste belegt oder aus) in freie Plätze des Inventars
		bool[,] incoming = new bool[width, rows];
		List<ItemDrop.ItemData> nextRows = new();
		List<ItemDrop.ItemData> nextGear = new();
		List<ItemDrop.ItemData> nextLoose = new();
		foreach (ItemDrop.ItemData item in next.GetAllItems())
		{
			Vector2i p = item.m_gridPos;
			if (p.y == GearRow)
			{
				nextGear.Add(item);
			}
			else if (Array.Find(sides, side => side.Holds(p.y)) is SideBar side)
			{
				int index = side.SetIndex(p);
				(side.Bar != null && index < side.Slots && !side.Stays[index] ? side.Incoming : nextLoose).Add(item);
			}
			else if (p.y - 1 < rows && p.x < width)
			{
				incoming[p.x, p.y - 1] = true;
				nextRows.Add(item);
			}
			else
			{
				nextLoose.Add(item); // Satz aus einem breiteren Inventar: in einen freien Platz
			}
		}

		// Platz zählen: frei unten (plus was dort an Ausrüstung weggeht), frei oben, wo Satz k nichts hinlegt
		int free = gearBelow;
		for (int y = rows; y < main.GetHeight(); y++)
		{
			for (int x = 0; x < main.GetWidth(); x++)
			{
				if (main.GetItemAt(x, y) == null)
				{
					free++;
				}
			}
		}
		int need = nextGear.Count + nextLoose.Count;
		bool[,] stays = new bool[width, rows];
		foreach (ItemDrop.ItemData item in stay)
		{
			stays[item.m_gridPos.x, item.m_gridPos.y] = true;
			if (incoming[item.m_gridPos.x, item.m_gridPos.y])
			{
				need++; // muss Satz k Platz machen
			}
		}
		for (int y = 0; y < rows; y++)
		{
			for (int x = 0; x < width; x++)
			{
				if (!incoming[x, y] && !stays[x, y])
				{
					free++;
				}
			}
		}
		if (need > free)
		{
			return GcLoc.Get("gcof_set_no_room", Name(k), (need - free).ToString());
		}

		// 1. Weg in den getragenen Satz (markiert, was angezogen war)
		foreach (ItemDrop.ItemData item in leaveRows)
		{
			bool equipped = player.IsItemEquiped(item);
			Vector2i at = new(item.m_gridPos.x, item.m_gridPos.y + 1);
			TakeOff(player, item);
			main.RemoveItem(item);
			item.m_equipped = equipped;
			Put(away, item, at, main);
		}
		foreach ((ItemDrop.ItemData item, int slot) in leaveGear)
		{
			TakeOff(player, item);
			main.RemoveItem(item);
			item.m_equipped = true;
			Put(away, item, new Vector2i(slot, GearRow), main);
		}
		foreach (SideBar side in sides)
		{
			foreach (ItemDrop.ItemData item in side.Leave)
			{
				bool equipped = player.IsItemEquiped(item);
				Vector2i at = new(item.m_gridPos.x, side.Row + item.m_gridPos.y);
				TakeOff(player, item);
				side.Bar!.RemoveItem(item);
				item.m_equipped = equipped;
				Put(away, item, at, side.Bar);
			}
		}

		// 2. Was bleibt (darf nicht in den Satz), macht Satz k Platz
		foreach (ItemDrop.ItemData item in stay)
		{
			if (incoming[item.m_gridPos.x, item.m_gridPos.y] && FreeBelow(main, rows) is Vector2i to)
			{
				bool equipped = player.IsItemEquiped(item);
				main.RemoveItem(item);
				Put(main, item, to, main);
				item.m_equipped = equipped; // bleibt angezogen (es ist nur umgezogen)
			}
		}

		// 3. Satz k an den Spieler
		List<ItemDrop.ItemData> wear = new();
		foreach (ItemDrop.ItemData item in nextRows)
		{
			bool equip = item.m_equipped;
			Vector2i at = new(item.m_gridPos.x, item.m_gridPos.y - 1);
			next.RemoveItem(item);
			item.m_equipped = false;
			if (Put(main, item, at, next) && equip)
			{
				wear.Add(item);
			}
		}
		foreach (SideBar side in sides)
		{
			foreach (ItemDrop.ItemData item in side.Incoming)
			{
				bool equip = item.m_equipped;
				Vector2i at = new(item.m_gridPos.x, item.m_gridPos.y - side.Row);
				next.RemoveItem(item);
				item.m_equipped = false;
				if (Put(side.Bar!, item, at, next) && equip)
				{
					wear.Add(item);
				}
			}
		}
		foreach (ItemDrop.ItemData item in nextGear)
		{
			next.RemoveItem(item);
			item.m_equipped = false;
			Vector2i? to = FreeBelow(main, rows) ?? FreeAnywhere(main);
			if (to is Vector2i at && Put(main, item, at, next))
			{
				wear.Add(item);
			}
		}
		foreach (ItemDrop.ItemData item in nextLoose)
		{
			bool equip = item.m_equipped;
			next.RemoveItem(item);
			item.m_equipped = false;
			Vector2i? to = FreeBelow(main, rows) ?? FreeAnywhere(main);
			if (to is Vector2i at && Put(main, item, at, next) && equip)
			{
				wear.Add(item);
			}
		}
		foreach (ItemDrop.ItemData item in wear)
		{
			player.EquipItem(item, false);
		}
		int from = worn;
		worn = k;
		Save(player);
		Outfits.Refresh(player);
		return GcLoc.Get("gcof_set_worn_now", Name(k), Name(from));
	}

	/// <summary>
	/// Eine Leiste neben dem Inventar (Vorräte, Leinen), die ein Satz in eigenen Reihen tauscht: Reihe y der Leiste liegt im Satz
	/// in Reihe Row + y, Platz für Platz (beide acht breit). Slots zählt die geltenden Plätze Reihe für Reihe.
	/// </summary>
	private sealed class SideBar
	{
		internal readonly int Row;
		internal readonly int Rows;
		internal readonly int Slots;
		internal readonly Inventory? Bar;
		internal readonly Func<ItemDrop.ItemData, bool> Fits;
		internal readonly List<ItemDrop.ItemData> Leave = new();
		internal readonly List<ItemDrop.ItemData> Incoming = new();
		internal readonly bool[] Stays;

		internal SideBar(int row, int rows, int slots, Inventory? bar, Func<ItemDrop.ItemData, bool> fits)
		{
			Row = row;
			Rows = rows;
			Slots = Math.Min(slots, rows * Width);
			Bar = bar;
			Fits = fits;
			Stays = new bool[Slots];
		}

		internal bool Holds(int setRow) => setRow >= Row && setRow < Row + Rows;

		internal int BarIndex(Vector2i pos) => pos.y < 0 || pos.y >= Rows || pos.x < 0 || pos.x >= Width ? -1 : pos.y * Width + pos.x;

		internal int SetIndex(Vector2i pos) => (pos.y - Row) * Width + pos.x;
	}

	private static void TakeOff(Player player, ItemDrop.ItemData item)
	{
		if (player.IsItemEquiped(item))
		{
			player.RemoveEquipAction(item);
			player.UnequipItem(item, false);
		}
	}

	/// <summary>
	/// An eine Stelle legen; ist sie belegt, an die nächste freie; geht gar nichts, zurück, woher es kam (nichts verlieren).
	/// Genau an die Stelle, ohne Valheims AddItem(item, pos): Das verteilte einen Stapel erst auf gleiche Stapel anderswo.
	/// </summary>
	private static bool Put(Inventory into, ItemDrop.ItemData item, Vector2i at, Inventory back)
	{
		if (at.x >= 0 && at.y >= 0 && at.x < into.GetWidth() && at.y < into.GetHeight() && into.GetItemAt(at.x, at.y) == null)
		{
			item.m_gridPos = at;
			into.m_inventory.Add(item);
			into.Changed();
			return true;
		}
		if (into.AddItem(item))
		{
			return true;
		}
		back.AddItem(item);
		Outfits.Log.LogWarning($"Sätze: {item.m_shared.m_name} fand keinen Platz und blieb, wo es war.");
		return false;
	}

	private static Vector2i? FreeBelow(Inventory inventory, int rows)
	{
		for (int y = rows; y < inventory.GetHeight(); y++)
		{
			for (int x = 0; x < inventory.GetWidth(); x++)
			{
				if (inventory.GetItemAt(x, y) == null)
				{
					return new Vector2i(x, y);
				}
			}
		}
		return null;
	}

	private static Vector2i? FreeAnywhere(Inventory inventory)
	{
		Vector2i pos = inventory.FindEmptySlot(false);
		return pos.x >= 0 ? pos : null;
	}
}

// ---------------------------------------------------------------------- Valheim-Haken

/// <summary>
/// Ein Item verlässt einen Satz (Umschalt-Klick, Wegwerfen, Tausch): Die Markierung „wird angezogen“ (m_equipped) gilt nur im
/// Satz, sonst hielte Valheim es für angezogen. Verschieben innerhalb eines Satzes behält sie (SetMoveWithinPatch).
/// </summary>
[HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(ItemDrop.ItemData))]
internal static class SetLeavePatch
{
	internal static bool Within;

	private static void Prefix(Inventory __instance, ItemDrop.ItemData item)
	{
		if (item != null && !Within && Sets.IsSet(__instance))
		{
			item.m_equipped = false;
		}
	}
}

/// <summary>Valheims Verschieben legt eine Kopie ab (Inventory.AddItem klont): Verlässt sie einen Satz, ohne Markierung.</summary>
[HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveItemToThis), typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int))]
internal static class SetMoveOutPatch
{
	private static void Postfix(Inventory __instance, Inventory fromInventory, int x, int y)
	{
		if (__instance != fromInventory && Sets.IsSet(fromInventory) && !Sets.IsSet(__instance) && __instance.GetItemAt(x, y) is ItemDrop.ItemData moved
			&& !(Player.m_localPlayer != null && Player.m_localPlayer.IsItemEquiped(moved)))
		{
			moved.m_equipped = false;
		}
	}
}

/// <summary>Ziehen innerhalb eines Satzes (auch Tauschen): Die Markierung „wird angezogen“ wandert mit.</summary>
[HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.DropItem))]
internal static class SetMoveWithinPatch
{
	private static void Prefix(InventoryGrid __instance, Inventory fromInventory)
	{
		SetLeavePatch.Within = fromInventory != null && fromInventory == __instance.GetInventory() && Sets.IsSet(fromInventory);
	}

	private static void Finalizer() => SetLeavePatch.Within = false;
}

/// <summary>Wegwerfen aus einem Satz: ohne Markierung auf den Boden.</summary>
[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DropItem))]
internal static class SetDropPatch
{
	private static void Prefix(Inventory inventory, ItemDrop.ItemData item)
	{
		if (item != null && Sets.IsSet(inventory))
		{
			item.m_equipped = false;
		}
	}
}

/// <summary>Vor dem Speichern des Charakters: Sätze hineinschreiben.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.Save))]
internal static class SetSavePatch
{
	private static void Prefix(Player __instance)
	{
		if (__instance == Player.m_localPlayer && Sets.Of(__instance) != null)
		{
			Sets.Save(__instance);
		}
	}
}

/// <summary>Neuer Spieler (Laden des Charakters): Sätze aus dessen Daten neu lesen.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.Load))]
internal static class SetLoadPatch
{
	private static void Postfix(Player __instance)
	{
		Sets.Forget(__instance);
		LiveSet.Forget();
	}
}
