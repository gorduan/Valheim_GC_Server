using System.Collections.Generic;
using GC.Shared;

namespace GC.Server.Outfit;

/// <summary>
/// Der angelegte Satz, live (Gorduan, 03.11.2026: in den getragenen Satz ließ sich nichts legen, „die Meldung ist immer bereits
/// ausgerüstet“): Sein Reiter zeigt, was der Spieler gerade hat, als Spiegel aus Kopien: die angezogene Ausrüstung in den
/// Tabellen, die Schnellleiste, die Vorrats- und die Leinenleiste. Jeder Klick darauf geht an das echte Raster (Inventar,
/// Vorrats- bzw. Leinenleiste) an derselben Stelle, so gelten dort alle Regeln wie sonst; Hineingelegtes in die Ausrüstung
/// wird angezogen. Die Kopien selbst werden nie benutzt, weggeworfen oder verschoben (sonst entstünden Items doppelt).
/// </summary>
internal static class LiveSet
{
	private static Inventory? mirror;
	private static ItemDrop.ItemData?[] shown = new ItemDrop.ItemData?[0];
	private static int[] stamps = new int[0];

	internal static bool IsLive(Inventory? inventory) => inventory != null && inventory == mirror;

	/// <summary>Spiegel des Spielers; neu aufgebaut, sobald sich an einem der gezeigten Items etwas ändert.</summary>
	internal static Inventory Of(Player player)
	{
		int cells = Sets.Width * Sets.Height;
		if (mirror == null || mirror.GetHeight() != Sets.Height)
		{
			mirror = new Inventory("GC_SetLive", null, Sets.Width, Sets.Height);
			shown = new ItemDrop.ItemData?[cells];
			stamps = new int[cells];
			stamps[0] = -1; // erster Aufbau
		}
		bool changed = false;
		for (int i = 0; i < cells; i++)
		{
			ItemDrop.ItemData? real = RealAt(player, i % Sets.Width, i / Sets.Width);
			int stamp = real == null ? 0 : Stamp(real);
			changed |= shown[i] != real || stamps[i] != stamp;
			shown[i] = real;
			stamps[i] = stamp;
		}
		if (changed)
		{
			mirror.m_inventory.Clear();
			for (int i = 0; i < cells; i++)
			{
				if (shown[i] is ItemDrop.ItemData real)
				{
					ItemDrop.ItemData copy = real.Clone();
					copy.m_gridPos = new Vector2i(i % Sets.Width, i / Sets.Width);
					mirror.m_inventory.Add(copy);
				}
			}
			mirror.Changed();
		}
		return mirror;
	}

	private static int Stamp(ItemDrop.ItemData item) =>
		1 + item.m_stack * 31 + (int)(item.m_durability * 10f) * 7 + item.m_quality * 3 + item.m_variant * 5 + (item.m_equipped ? 1 : 0);

	/// <summary>Das echte Item hinter einem Platz des Spiegels.</summary>
	internal static ItemDrop.ItemData? RealAt(Player player, int x, int y)
	{
		if (Sets.IsSupplyRow(y))
		{
			return Sets.SupplyIndex(new Vector2i(x, y)) < SupplyLink.Slots && SupplyLink.Bar(player) is Inventory supplies ? supplies.GetItemAt(x, y - Sets.SupplyRow) : null;
		}
		switch (y)
		{
			case Sets.GearRow:
				return x switch
				{
					Sets.Helmet => player.m_helmetItem,
					Sets.Chest => player.m_chestItem,
					Sets.Legs => player.m_legItem,
					Sets.Cape => player.m_shoulderItem,
					Sets.Belt => player.m_utilityItem,
					Sets.Trinket => player.m_trinketItem,
					Sets.RightHand => player.m_rightItem,
					Sets.LeftHand => player.m_leftItem,
					_ => null,
				};
			case Sets.HotbarRow:
				Inventory main = player.GetInventory();
				return x < main.GetWidth() ? main.GetItemAt(x, 0) : null;
			case Sets.LeashRow:
				return x < LeashLink.Slots && LeashLink.Bar(player) is Inventory bar ? bar.GetItemAt(x, 0) : null;
		}
		return null;
	}

	/// <summary>Raster und Stelle, für die ein Platz des Spiegels steht (Ausrüstung: keins).</summary>
	private static (InventoryGrid Grid, Vector2i Pos)? Target(InventoryGui gui, Vector2i pos)
	{
		if (pos.y == Sets.HotbarRow)
		{
			return (gui.m_playerGrid, new Vector2i(pos.x, 0));
		}
		if (Sets.IsSupplyRow(pos.y) && Sets.SupplyIndex(pos) < SupplyLink.Slots && SupplyLink.BarGrid is InventoryGrid supplies)
		{
			return (supplies, new Vector2i(pos.x, pos.y - Sets.SupplyRow));
		}
		if (pos.y == Sets.LeashRow && pos.x < LeashLink.Slots && LeashLink.BarGrid is InventoryGrid leashes)
		{
			return (leashes, new Vector2i(pos.x, 0));
		}
		return null;
	}

	/// <summary>Raster, in dem ein echtes Item liegt (Inventar, Vorrats- oder Leinenleiste).</summary>
	private static InventoryGrid? HomeOf(InventoryGui gui, Player player, ItemDrop.ItemData item)
	{
		if (player.GetInventory().ContainsItem(item))
		{
			return gui.m_playerGrid;
		}
		if (SupplyLink.Bar(player) is Inventory supplies && supplies.ContainsItem(item))
		{
			return SupplyLink.BarGrid;
		}
		return LeashLink.Bar(player) is Inventory bar && bar.ContainsItem(item) ? LeashLink.BarGrid : null;
	}

	/// <summary>Klick auf den Spiegel (Valheims OnSelectedItem, vorher abgefangen): an das echte Raster weiterreichen.</summary>
	internal static void Click(InventoryGui gui, Vector2i pos, InventoryGrid.Modifier mod)
	{
		Player? player = Player.m_localPlayer;
		if (player == null)
		{
			return;
		}
		if (gui.m_dragGo != null)
		{
			if (gui.m_dragItem == null || gui.m_dragInventory == null)
			{
				return;
			}
			if (pos.y == Sets.GearRow)
			{
				PutOn(gui, player, pos.x);
				return;
			}
			if (Target(gui, pos) is (InventoryGrid grid, Vector2i at))
			{
				gui.OnSelectedItem(grid, grid.GetInventory().GetItemAt(at.x, at.y), at, mod);
			}
			return;
		}
		ItemDrop.ItemData? real = RealAt(player, pos.x, pos.y);
		if (real != null && HomeOf(gui, player, real) is InventoryGrid home)
		{
			gui.OnSelectedItem(home, real, real.m_gridPos, mod); // aufnehmen, teilen, wegwerfen oder Umschalt-Klick: dort, wo es liegt
		}
	}

	/// <summary>Rechtsklick auf den Spiegel: das echte Item benutzen (essen, anziehen …).</summary>
	internal static void Use(Vector2i pos)
	{
		Player? player = Player.m_localPlayer;
		if (player == null || RealAt(player, pos.x, pos.y) is not ItemDrop.ItemData real)
		{
			return;
		}
		if (SupplyLink.Bar(player) is Inventory supplies && supplies.ContainsItem(real))
		{
			SupplyLink.Use(real); // wie Rechtsklick in der Vorratsleiste (Munition anlegen inklusive)
			return;
		}
		Inventory? inventory = player.GetInventory().ContainsItem(real) ? player.GetInventory() : LeashLink.Bar(player);
		if (inventory != null)
		{
			player.UseItem(inventory, real, true);
		}
	}

	/// <summary>
	/// In einen Platz der Ausrüstung gelegt: anziehen, wenn es dort hinpasst. Aus einem Satz oder dem Stil kommt es dafür erst
	/// in einen freien Platz des Inventars (über Valheims Raster, mit allen Prüfungen).
	/// </summary>
	private static void PutOn(InventoryGui gui, Player player, int slot)
	{
		ItemDrop.ItemData item = gui.m_dragItem;
		int fits = Sets.GearSlot(item);
		bool hand = slot >= Sets.RightHand;
		if (fits < 0 || (hand ? fits < Sets.RightHand : fits != slot))
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcof_set_not_fit", Sets.SlotName(slot)));
			return;
		}
		Inventory main = player.GetInventory();
		if (gui.m_dragInventory != main && !LeashLink.IsBar(gui.m_dragInventory))
		{
			Vector2i free = main.FindEmptySlot(true);
			if (free.x < 0)
			{
				player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcof_no_room"));
				return;
			}
			gui.OnSelectedItem(gui.m_playerGrid, null, free, InventoryGrid.Modifier.Select);
			if (main.GetItemAt(free.x, free.y) is not ItemDrop.ItemData moved)
			{
				return; // abgelehnt, die Meldung kam vom Raster
			}
			item = moved;
		}
		else
		{
			gui.SetupDragItem(null, null, 1);
		}
		if (!player.IsItemEquiped(item))
		{
			player.EquipItem(item);
		}
		gui.m_moveItemEffects.Create(gui.transform.position, UnityEngine.Quaternion.identity);
	}

	/// <summary>Neuer Charakter: Spiegel vergessen (er hielte sonst Kopien des alten).</summary>
	internal static void Forget()
	{
		mirror = null;
		shown = new ItemDrop.ItemData?[0];
		stamps = new int[0];
	}
}
