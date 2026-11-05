using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GC.Server.Supplies;

/// <summary>
/// Vorratsleiste (Gorduan, 03.11.2026: „um sicherzugehen, dass diese Leiste immer funktioniert, sollten wir auch dem
/// Spielerrucksack diese Leiste hinzufügen, so hat jeder Spieler immer eine Leiste, wo er nur diese Gegenstände einfügen
/// kann“): acht eigene Plätze im Inventar-Bildschirm direkt unter dem Inventar, nur für Vorräte (Essen, Tränke und Mets,
/// Pfeile, Bolzen und Köder). Gorduans Antworten vom selben Tag:
///  * Benutzen per Rechtsklick (essen, trinken, Munition anlegen); Bogen und Armbrust nehmen Munition auch aus der Leiste,
///    wenn im Inventar keine passende liegt. Keine eigenen Tasten (Alt+1–5 hat die Leinenleiste, Strg ist Ducken).
///  * Zählt zum Tragegewicht (sonst ein Gratis-Lager).
///  * Beim Tod ins Grab, beim Einsammeln zurück an den alten Platz (wie die Leinenleiste).
///  * Rezepte nehmen nichts aus der Leiste (wie Valheim nur aus dem Inventar).
///  * Nur im Inventar-Bildschirm zu sehen, nicht im Spielbild.
/// Echte Plätze wie die Leinenleiste: eigenes Inventar 8 × 8 im Charakter (Player.m_customData), Platz n liegt in Reihe n / 8.
/// Wie viele davon gelten, setzt die Server-Regel Slots (Gorduan, 04.11.2026: „max. Größe von 64, Standard ist aber 8“). GC_Outfit
/// tauscht beim Anlegen eines Satzes dessen Vorräte mit dieser Leiste. Server-Regel Enabled (Standard an): aus = die Leiste
/// nimmt nichts mehr an und gibt keine Munition, Vorhandenes lässt sich herausnehmen. Auffüllen und Einlagern: SupplyChests.
/// </summary>
internal static class SupplyBar
{
	/// <summary>Plätze je Reihe (wie Valheims Inventar), höchstens acht Reihen.</summary>
	internal const int Width = 8;
	internal const int Rows = 8;
	internal const int MaxSlots = Width * Rows;
	internal const int DefaultSlots = 8;

	private const string DataKey = "gc_supplybar";
	private const float Gap = 6f;

	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<int> SlotCount = null!;
	internal static ConfigEntry<bool> KeepOne = null!;
	internal static ManualLogSource Log = null!;

	private static Player? owner;
	private static Inventory? bar;

	internal static bool On => Enabled.Value && ModVorratsleiste.Plugin.Module.Allowed;

	internal static void Init(ConfigFile config, ManualLogSource log)
	{
		Log = log;
		Enabled = Gc.BindSynced(config, "1 - Supply bar", "Enabled", true, "Gorduan, 03.11.2026: Plätze unter dem Inventar nur für Vorräte (Essen, Tränke, Mets, Munition), zählt zum Gewicht, kommt beim Tod mit ins Grab. Aus: nimmt nichts mehr an, Vorhandenes lässt sich herausnehmen.");
		SlotCount = Gc.BindSynced(config, "1 - Supply bar", "Slots", DefaultSlots, "Gorduan, 03./04.11.2026: so viele Vorratsplätze hat jeder Spieler, im Rucksack und in den Sätzen am Kleiderschrank gleichermaßen (1–64, Standard 8; je acht eine Reihe). Weniger: was in den übrigen Plätzen liegt, lässt sich nur noch herausnehmen.", new AcceptableValueRange<int>(1, MaxSlots));
		KeepOne = Gc.BindSynced(config, "1 - Supply bar", "KeepOneInChest", true, "Gorduan, 05.11.2026: Auffüllen nimmt von jeder Sorte höchstens alles bis auf ein Stück aus jeder Kiste (auch der offenen), damit die Kiste weiter weiß, was in sie gehört (Sortieren, Einlagern).");
	}

	/// <summary>Nummer eines Platzes (Reihe für Reihe), wie sie die Server-Regel Slots zählt.</summary>
	internal static int Index(Vector2i pos) => pos.y * Width + pos.x;

	internal static Vector2i Pos(int index) => new(index % Width, index / Width);

	// ------------------------------------------------------------------ Inventar der Leiste

	/// <summary>Leiste des eigenen Spielers (andere Spieler haben hier keine), beim ersten Zugriff aus dem Charakter geladen.</summary>
	internal static Inventory? Of(Player? player)
	{
		if (player == null || player != Player.m_localPlayer)
		{
			return null;
		}
		if (owner != player || bar == null)
		{
			owner = player;
			bar = Load(player);
		}
		return bar;
	}

	private static Inventory Load(Player player)
	{
		Inventory inventory = new("GC_SupplyBar", null, Width, Rows); // bis 03.11. gespeichert 8 × 1: dieselben Plätze in Reihe 0
		if (player.m_customData.TryGetValue(DataKey, out string data) && data.Length > 0)
		{
			try
			{
				inventory.Load(new ZPackage(Convert.FromBase64String(data)));
				foreach (ItemDrop.ItemData item in inventory.GetAllItems())
				{
					item.m_equipped = false; // Valheim rüstet beim Laden nur aus dem Inventar aus
				}
			}
			catch (Exception e)
			{
				player.m_customData[DataKey + "_kaputt"] = data; // nichts verlieren: der alte Stand bleibt daneben liegen
				Log.LogWarning($"Vorratsleiste: gespeicherter Stand nicht lesbar ({e.Message}), er liegt unter {DataKey}_kaputt.");
			}
		}
		inventory.m_onChanged += () => Save(player, inventory);
		return inventory;
	}

	internal static void Save(Player player, Inventory inventory)
	{
		if (inventory.NrOfItems() == 0)
		{
			player.m_customData.Remove(DataKey);
			return;
		}
		ZPackage pkg = new();
		inventory.Save(pkg);
		player.m_customData[DataKey] = Convert.ToBase64String(pkg.GetArray());
	}

	internal static void Forget(Player player)
	{
		if (owner == player || player == Player.m_localPlayer)
		{
			owner = null;
			bar = null;
		}
	}

	internal static bool IsBar(Inventory? inventory) => inventory != null && inventory == bar;

	private static bool IsPlayerInventory(Inventory inventory) => owner != null && bar != null && inventory == owner.m_inventory;

	/// <summary>Was in die Leiste passt: Vorräte (Essen, Tränke und Mets, Munition), keine Quest-Items.</summary>
	internal static bool Fits(ItemDrop.ItemData? item)
	{
		if (item?.m_shared == null || item.m_shared.m_questItem)
		{
			return false;
		}
		ItemDrop.ItemData.ItemType type = item.m_shared.m_itemType;
		return type == ItemDrop.ItemData.ItemType.Consumable || type == ItemDrop.ItemData.ItemType.Ammo || type == ItemDrop.ItemData.ItemType.AmmoNonEquipable;
	}

	/// <summary>Ablegen prüfen (null = erlaubt): in die Leiste nur Vorräte, und ein Tausch darf nichts anderes hineinlegen.</summary>
	internal static string? DropRefusal(Inventory target, Inventory from, ItemDrop.ItemData item, Vector2i pos)
	{
		if (bar == null || (target != bar && from != bar))
		{
			return null;
		}
		ItemDrop.ItemData? swapped = target.GetItemAt(pos.x, pos.y);
		if (swapped == item)
		{
			return null;
		}
		if (target == bar && target != from)
		{
			if (!On)
			{
				return GcLoc.Get("gcvl_off");
			}
			if (Index(pos) >= Count)
			{
				return GcLoc.Get("gcvl_locked");
			}
			if (!Fits(item))
			{
				return GcLoc.Get("gcvl_only");
			}
		}
		if (target == bar && target == from && Index(pos) >= Count && pos != item.m_gridPos)
		{
			return GcLoc.Get("gcvl_locked"); // innerhalb der Leiste nur in geltende Plätze verschieben
		}
		if (from == bar && target != bar && swapped != null && !Fits(swapped))
		{
			return GcLoc.Get("gcvl_only"); // der Tausch legte etwas anderes in die Leiste
		}
		return null;
	}

	/// <summary>Umschalt-Klick in der Leiste: mit offener Kiste in die Kiste, sonst ins Inventar (Valheim nähme das Inventar als Quelle).</summary>
	internal static void QuickMove(InventoryGui gui, ItemDrop.ItemData item)
	{
		Player? player = Player.m_localPlayer;
		if (player == null || Of(player) is not Inventory inventory || !inventory.ContainsItem(item))
		{
			return;
		}
		player.RemoveEquipAction(item);
		player.UnequipItem(item);
		Inventory target = gui.m_currentContainer != null ? gui.m_currentContainer.GetInventory() : player.GetInventory();
		if (!target.HaveEmptySlot())
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcvl_no_room"));
			return;
		}
		target.MoveItemToThis(inventory, item);
		gui.m_moveItemEffects.Create(gui.transform.position, Quaternion.identity);
	}

	/// <summary>Rechtsklick: essen und trinken wie im Inventar; Munition anlegen oder ablegen.</summary>
	internal static void Use(ItemDrop.ItemData? item)
	{
		Player? player = Player.m_localPlayer;
		if (item == null || player == null || Of(player) is not Inventory inventory || !inventory.ContainsItem(item))
		{
			return;
		}
		ItemDrop.ItemData.ItemType type = item.m_shared.m_itemType;
		if (type == ItemDrop.ItemData.ItemType.Ammo)
		{
			AmmoScope.Depth++;
			try
			{
				player.ToggleEquipped(item);
			}
			finally
			{
				AmmoScope.Depth--;
			}
			return;
		}
		player.UseItem(inventory, item, true);
	}

	// ------------------------------------------------------------------ Gewicht und Munition

	/// <summary>Tragegewicht: die Leiste zählt zum Inventar des eigenen Spielers (Gorduan: kein Gratis-Lager).</summary>
	internal static float ExtraWeight(Inventory inventory)
	{
		Player? player = Player.m_localPlayer;
		if (player == null || inventory != player.m_inventory || Of(player) is not Inventory own)
		{
			return 0f;
		}
		return own.GetTotalWeight();
	}

	/// <summary>
	/// Solange Valheim Munition sucht, anlegt oder verbraucht (Attack.HaveAmmo, FindAmmo, EquipAmmoItem, UseAmmo) oder ein Item
	/// der Leiste angelegt wird, gilt die Leiste als Teil des Inventars: ContainsItem, GetAmmoItem (erst das Inventar, dann die
	/// Leiste), RemoveItem und RemoveOneItem. Außerhalb davon bleibt sie getrennt (Rezepte, Kisten, Ziehen und Ablegen).
	/// </summary>
	internal static class AmmoScope
	{
		internal static int Depth;

		internal static bool Enter(Character? character)
		{
			if (character == null || character != Player.m_localPlayer || !On || Of(Player.m_localPlayer) == null)
			{
				return false;
			}
			Depth++;
			return true;
		}

		internal static void Leave(bool entered)
		{
			if (entered)
			{
				Depth = Math.Max(0, Depth - 1);
			}
		}

		internal static bool Active(Inventory inventory) => Depth > 0 && IsPlayerInventory(inventory);

		internal static bool InBar(ItemDrop.ItemData? item) => item != null && bar != null && bar.m_inventory.Contains(item);
	}

	// ------------------------------------------------------------------ Tod (wie die Leinenleiste in GC_Tiere)

	/// <summary>
	/// Vor dem Grab, nach Valheims Weltregeln in CreateTombStone: „Inventar behalten“ lässt die Vorräte in der Leiste; „Items
	/// löschen“ bzw. „Nicht Ausgerüstetes löschen“ löscht wie dort. Ist das Inventar leer, entstünde gar kein Grab: dann die
	/// Vorräte erst ins Inventar.
	/// </summary>
	internal static void BeforeGrave(Player player)
	{
		ZoneSystem? zones = ZoneSystem.instance;
		if (Of(player) is not Inventory inventory || inventory.NrOfItems() == 0 || zones == null || zones.GetGlobalKey(GlobalKeys.DeathKeepInventory))
		{
			return;
		}
		bool keepEquip = zones.GetGlobalKey(GlobalKeys.DeathKeepEquip);
		bool deleteUnequipped = zones.GetGlobalKey(GlobalKeys.DeathDeleteUnequipped);
		bool unequipFirst = !keepEquip && !deleteUnequipped;
		if (zones.GetGlobalKey(GlobalKeys.DeathDeleteItems) || deleteUnequipped)
		{
			foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(inventory.GetAllItems()))
			{
				if (unequipFirst || !item.m_equipped)
				{
					if (item.m_equipped)
					{
						player.UnequipItem(item, false);
					}
					inventory.RemoveItem(item);
				}
			}
		}
		Inventory main = player.GetInventory();
		if (main.NrOfItems() == 0)
		{
			foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(inventory.GetAllItems()))
			{
				if (item.m_equipped && !keepEquip)
				{
					player.UnequipItem(item, false);
				}
				if (!item.m_equipped && main.AddItem(item))
				{
					inventory.RemoveItem(item);
				}
			}
		}
		pendingGrave = player;
	}

	private static Player? pendingGrave;

	/// <summary>
	/// Platz in der Leiste vor dem Tod (Nummer, Reihe für Reihe; bis 03.11. nur x, das ist in Reihe 0 dasselbe), am Item im Grab
	/// (Item-Daten, wandern beim Einsammeln mit).
	/// </summary>
	private const string GraveSlotKey = "gc_sb_slot";

	/// <summary>Ins Grab: was noch in der Leiste liegt (nicht angelegt), notfalls bekommt das Grab eine Reihe mehr.</summary>
	internal static void IntoGrave(Inventory grave, Inventory original)
	{
		Player? player = pendingGrave;
		pendingGrave = null;
		if (player == null || original != player.GetInventory())
		{
			return;
		}
		foreach (ItemDrop.ItemData item in grave.GetAllItems())
		{
			item.m_customData?.Remove(GraveSlotKey);
		}
		if (Of(player) is not Inventory inventory)
		{
			return;
		}
		foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(inventory.GetAllItems()))
		{
			if (item.m_equipped || item.m_shared.m_questItem)
			{
				continue;
			}
			if (!grave.HaveEmptySlot())
			{
				grave.m_height++;
			}
			item.m_customData ??= new Dictionary<string, string>();
			item.m_customData[GraveSlotKey] = Index(item.m_gridPos).ToString(System.Globalization.CultureInfo.InvariantCulture);
			if (grave.AddItem(item))
			{
				inventory.RemoveItem(item); // erst im Grab, dann aus der Leiste: verloren geht nichts
			}
			else
			{
				item.m_customData.Remove(GraveSlotKey);
			}
		}
	}

	internal static void ForgetSlot(ItemDrop.ItemData? item) => item?.m_customData?.Remove(GraveSlotKey);

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Inventory, TombStone> Graves = new();

	internal static void RememberGrave(TombStone tomb)
	{
		if (tomb.m_container != null && tomb.m_container.GetInventory() is Inventory inventory)
		{
			Graves.Remove(inventory);
			Graves.Add(inventory, tomb);
		}
	}

	/// <summary>
	/// Nach jedem Umräumen aus dem eigenen Grab ins Inventar: Vorräte kehren in ihren alten Platz der Leiste zurück; was dort
	/// inzwischen liegt, kommt an die Stelle im Inventar, die das zurückgekehrte Stück frei macht.
	/// </summary>
	internal static void AfterMove(Inventory target, Inventory from)
	{
		Player? player = Player.m_localPlayer;
		if (player == null || !Graves.TryGetValue(from, out TombStone tomb) || tomb == null || Of(player) is not Inventory inventory)
		{
			return;
		}
		Inventory main = player.GetInventory();
		if (target != main && target != inventory)
		{
			return;
		}
		bool mine = tomb.IsOwner();
		foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(target.GetAllItems()))
		{
			if (item.m_customData == null || !item.m_customData.TryGetValue(GraveSlotKey, out string value))
			{
				continue;
			}
			item.m_customData.Remove(GraveSlotKey);
			if (!mine || target != main || !int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int index) || index < 0 || index >= MaxSlots)
			{
				continue;
			}
			Vector2i pos = item.m_gridPos;
			Vector2i home = Pos(index);
			ItemDrop.ItemData? occupant = inventory.GetItemAt(home.x, home.y);
			if (occupant != null && !inventory.RemoveItem(occupant))
			{
				continue;
			}
			main.RemoveItem(item);
			if (occupant != null && !main.AddItem(occupant, pos))
			{
				inventory.AddItem(occupant, home); // kann nicht passieren (pos ist eben frei geworden), aber nichts verlieren
				main.AddItem(item, pos);
				continue;
			}
			inventory.AddItem(item, home);
		}
	}

	// ------------------------------------------------------------------ Raster im Inventar-Bildschirm

	private const float LabelSlots = 1.3f; // Breite der Überschrift „Vorräte:“ in Plätzen
	private const float MaxScale = 0.85f;
	private const float MinScale = 0.35f;
	private const float ButtonHeight = 30f;

	private static InventoryGrid? grid;
	private static RectTransform? line; // Zeile im Inventar-Feld: Überschrift, Raster und Knöpfe
	private static TMP_Text? label;
	private static Button? fillButton;
	private static Button? storeButton;
	private static UITooltip? fillTip;
	private static UITooltip? storeTip;
	private static float tipsUntil;
	private static bool panelGrown;
	private static Vector2 playerOffsetMin; // Größe des Inventar-Felds ohne die Zeile (wie Valheim bzw. ValheimPlus sie setzen)
	private static Vector2 playerGridOffsetMin;
	private static Vector2 playerOffsetSet; // zuletzt hier gesetzt: weicht das Feld davon ab, hat jemand anderes es neu gesetzt
	private static Vector2 playerGridOffsetSet;
	private static bool containerShifted;
	private static Vector2 containerPos;

	/// <summary>Das Raster (für GC_Outfit: Klicks auf die Vorräte des angelegten Satzes landen hier).</summary>
	internal static InventoryGrid? Grid => grid;

	/// <summary>Plätze, die gerade gelten (Server-Regel Slots, 1–64; Gorduan, 03.11.2026: für Rucksack und Schrank gleichermaßen).</summary>
	internal static int Count => Mathf.Clamp(SlotCount.Value, 1, MaxSlots);

	/// <summary>Gezeigte Plätze: die geltenden, dazu belegte dahinter (nach dem Verkleinern lassen sie sich nur noch leeren).</summary>
	internal static int Shown(Inventory inventory)
	{
		int shown = Count;
		foreach (ItemDrop.ItemData item in inventory.GetAllItems())
		{
			shown = Math.Max(shown, Index(item.m_gridPos) + 1);
		}
		return Math.Min(shown, MaxSlots);
	}

	/// <summary>Jedes Bild, solange das Inventar offen ist (nach Valheims UpdateInventory): die Zeile unter dem Inventar.</summary>
	internal static void UpdateGrid(InventoryGui gui, Player player)
	{
		if (Of(player) is not Inventory inventory || (!On && inventory.NrOfItems() == 0))
		{
			Hide(gui);
			return;
		}
		if (!EnsureGrid(gui))
		{
			return;
		}
		line!.gameObject.SetActive(true);
		grid!.UpdateInventory(inventory, player, gui.m_dragItem);
		int shown = Shown(inventory);
		foreach (InventoryElement element in grid.m_elements)
		{
			int index = Index(element.Position);
			bool visible = index < shown;
			if (element.gameObject.activeSelf != visible)
			{
				element.gameObject.SetActive(visible);
			}
			if (element.transform.Find("binding")?.GetComponent<TMP_Text>() is TMP_Text binding && binding.enabled)
			{
				binding.enabled = false; // die Kopie von Valheims Raster zeigte sonst die Zahlentasten der Schnellleiste
			}
			bool locked = !On || index >= Count;
			if (element.m_button != null && element.m_button.image != null)
			{
				element.m_button.image.color = locked ? new Color(1f, 1f, 1f, 0.35f) : Color.white;
			}
			if (!element.m_used)
			{
				element.m_tooltip.m_topic = GcLoc.Get("gcvl_title");
				element.m_tooltip.m_text = GcLoc.Get(!On ? "gcvl_off" : locked ? "gcvl_locked" : "gcvl_help");
			}
		}
		Place(gui, shown);
		ShiftContainer(gui);
		UpdateButtons(inventory);
	}

	/// <summary>
	/// Zeile unter der letzten Reihe des Inventars, im selben Feld (Gorduan, 03.11.2026: „nach den Rucksackplätzen eine Zeile
	/// ‚Vorräte:‘ und anschließend die Inhalte“): links die Überschrift, dahinter die Plätze, acht je Reihe, darunter die Knöpfe
	/// Auffüllen und Einlagern (Gorduan, 04.11.2026: „an der Leiste und nicht an der Kiste“). Die Plätze sind so klein, dass sie neben
	/// der Überschrift in die Breite des Inventars passen und die Zeile samt einer offenen Kiste darunter auf den Bildschirm
	/// (bis 64 Plätze, also acht Reihen). Valheims Feld wird dafür nach unten verlängert. ValheimPlus setzt die Größe des Felds
	/// für mehr Reihen selbst (auch nach dem Öffnen): Weicht das Feld von dem ab, was hier zuletzt gesetzt wurde, gilt sein neuer
	/// Stand als Grundlage. Erster Versuch (03.11.) merkte sich die Größe beim Öffnen und setzte jedes Bild darauf zurück: Das Feld
	/// endete dann nach vier Reihen (Gorduans Bildschirmfoto).
	/// </summary>
	private static void Place(InventoryGui gui, int shown)
	{
		RectTransform panel = gui.m_player;
		RectTransform playerGrid = (RectTransform)gui.m_playerGrid.transform;
		if (!panelGrown || panel.offsetMin != playerOffsetSet)
		{
			playerOffsetMin = panel.offsetMin;
		}
		if (!panelGrown || playerGrid.offsetMin != playerGridOffsetSet)
		{
			playerGridOffsetMin = playerGrid.offsetMin;
		}
		panelGrown = true;
		panel.offsetMin = playerOffsetMin;
		playerGrid.offsetMin = playerGridOffsetMin;

		// Ränder aller Inventar-Plätze in Weltmaß (ValheimPlus: die Plätze reichen über das Raster hinaus)
		Vector3[] c = new Vector3[4];
		float left = float.MaxValue;
		float right = float.MinValue;
		float bottom = float.MaxValue;
		foreach (InventoryElement element in gui.m_playerGrid.m_elements)
		{
			if (!element.gameObject.activeInHierarchy)
			{
				continue;
			}
			((RectTransform)element.transform).GetWorldCorners(c);
			left = Mathf.Min(left, c[0].x);
			right = Mathf.Max(right, c[2].x);
			bottom = Mathf.Min(bottom, c[0].y);
		}
		if (left == float.MaxValue)
		{
			playerOffsetSet = panel.offsetMin;
			playerGridOffsetSet = playerGrid.offsetMin;
			return;
		}

		RectTransform lineRect = line!;
		float unit = Mathf.Max(0.0001f, lineRect.lossyScale.x);
		float space = grid!.m_elementSpace;
		float spaceWorld = space * unit;
		float labelWorld = LabelSlots * spaceWorld;
		int columns = Math.Min(shown, Width);
		int rows = (shown + Width - 1) / Width;
		float top = bottom - Gap * unit;
		float scale = Mathf.Min(MaxScale, (right - left - labelWorld) / (columns * spaceWorld));
		if (ScreenBottom(panel) is float screen)
		{
			float below = (Gap * 3f + ButtonHeight) * unit + ContainerHeight(gui);
			scale = Mathf.Min(scale, (top - below - screen) / (rows * spaceWorld)); // viele Reihen: kleiner statt aus dem Bild
		}
		scale = Mathf.Clamp(scale, MinScale, MaxScale);
		float gridHeight = rows * space * scale; // in Einheiten der Zeile
		float height = gridHeight + Gap + ButtonHeight;
		lineRect.position = new Vector3(left, top, lineRect.position.z);
		lineRect.sizeDelta = new Vector2((right - left) / unit, height);
		RectTransform labelRect = label!.rectTransform;
		labelRect.anchoredPosition = Vector2.zero;
		labelRect.sizeDelta = new Vector2(LabelSlots * space - Gap, space * scale);
		RectTransform gridRect = (RectTransform)grid.transform;
		gridRect.localScale = new Vector3(scale, scale, 1f);
		gridRect.position = new Vector3(left + labelWorld, top, gridRect.position.z);
		float buttonsLeft = LabelSlots * space;
		float buttonWidth = ((right - left) / unit - buttonsLeft - Gap) / 2f;
		PlaceButton(fillButton, buttonsLeft, -(gridHeight + Gap), buttonWidth);
		PlaceButton(storeButton, buttonsLeft + buttonWidth + Gap, -(gridHeight + Gap), buttonWidth);

		// Feld nach unten verlängern, bis die Zeile mit etwas Rand hineinpasst
		panel.GetWorldCorners(c);
		float extra = c[0].y - (top - height * unit - Gap * 2f * unit);
		if (extra > 0f)
		{
			float parentScale = Mathf.Max(0.0001f, panel.parent != null ? panel.parent.lossyScale.y : 1f);
			panel.offsetMin = playerOffsetMin - new Vector2(0f, extra / parentScale);
			if (!Mathf.Approximately(playerGrid.anchorMin.y, playerGrid.anchorMax.y))
			{
				float gridParentScale = Mathf.Max(0.0001f, playerGrid.parent != null ? playerGrid.parent.lossyScale.y : 1f);
				playerGrid.offsetMin = playerGridOffsetMin + new Vector2(0f, extra / gridParentScale); // das Raster wächst nicht mit
			}
		}
		playerOffsetSet = panel.offsetMin;
		playerGridOffsetSet = playerGrid.offsetMin;
	}

	private static void PlaceButton(Button? button, float x, float y, float width)
	{
		if (button == null)
		{
			return;
		}
		RectTransform rect = (RectTransform)button.transform;
		rect.anchoredPosition = new Vector2(x, y);
		rect.sizeDelta = new Vector2(Mathf.Max(40f, width), ButtonHeight);
	}

	/// <summary>Unterer Bildschirmrand (mit etwas Rand) in Weltmaß; die oberste Transform ist bei Valheim nicht der Bildschirm.</summary>
	private static float? ScreenBottom(RectTransform panel)
	{
		Canvas? canvas = panel.GetComponentInParent<Canvas>();
		if (canvas == null || canvas.rootCanvas.transform is not RectTransform screen)
		{
			return null;
		}
		Vector3[] c = new Vector3[4];
		screen.GetWorldCorners(c);
		return c[0].y + Gap * panel.lossyScale.y;
	}

	/// <summary>Höhe einer offenen Kiste samt Abstand (sie rückt unter das verlängerte Feld), sonst 0.</summary>
	private static float ContainerHeight(InventoryGui gui)
	{
		RectTransform container = gui.m_container;
		if (gui.m_currentContainer == null || container == null || !container.gameObject.activeInHierarchy)
		{
			return 0f;
		}
		Vector3[] c = new Vector3[4];
		container.GetWorldCorners(c);
		return c[1].y - c[0].y + Gap * container.lossyScale.y;
	}

	/// <summary>
	/// Eine offene Kiste steht in Valheim direkt unter dem Inventar: so weit nach unten, dass sie das verlängerte Feld nicht
	/// verdeckt, aber nie unter den Bildschirmrand; zurück, sobald sie zu ist (Feld des Kleiderschranks: keine Kiste, GC_Outfit
	/// ordnet es selbst an).
	/// </summary>
	private static void ShiftContainer(InventoryGui gui)
	{
		RectTransform container = gui.m_container;
		if (gui.m_currentContainer == null || !container.gameObject.activeInHierarchy)
		{
			RestoreContainer(gui);
			return;
		}
		if (!containerShifted)
		{
			containerShifted = true;
			containerPos = container.anchoredPosition;
		}
		container.anchoredPosition = containerPos;
		Vector3[] c = new Vector3[4];
		container.GetWorldCorners(c);
		Vector3[] p = new Vector3[4];
		gui.m_player.GetWorldCorners(p);
		float overlap = c[1].y - (p[0].y - Gap * gui.m_player.lossyScale.y);
		if (ScreenBottom(container) is float screen)
		{
			overlap = Mathf.Min(overlap, c[0].y - screen);
		}
		if (overlap > 0f)
		{
			container.position -= new Vector3(0f, overlap, 0f);
		}
	}

	private static void RestoreContainer(InventoryGui gui)
	{
		if (containerShifted)
		{
			containerShifted = false;
			gui.m_container.anchoredPosition = containerPos;
		}
	}

	internal static void Hide(InventoryGui? gui)
	{
		if (line != null)
		{
			line.gameObject.SetActive(false);
		}
		if (gui == null)
		{
			return;
		}
		RestoreContainer(gui);
		if (panelGrown)
		{
			panelGrown = false;
			gui.m_player.offsetMin = playerOffsetMin;
			((RectTransform)gui.m_playerGrid.transform).offsetMin = playerGridOffsetMin;
		}
	}

	private static bool EnsureGrid(InventoryGui gui)
	{
		if (grid != null && line != null)
		{
			return true;
		}
		if (gui.m_playerGrid == null || gui.m_player == null)
		{
			return false;
		}
		// Halter im Inventar-Feld (über dessen Hintergrund), links die Überschrift
		GameObject holder = new("GC_SupplyBar", typeof(RectTransform));
		holder.transform.SetParent(gui.m_player, false);
		line = (RectTransform)holder.transform;
		line.anchorMin = line.anchorMax = new Vector2(0f, 1f);
		line.pivot = new Vector2(0f, 1f);
		GameObject text = new("GC_SupplyBarTitle", typeof(RectTransform));
		text.transform.SetParent(line, false);
		TextMeshProUGUI title = text.AddComponent<TextMeshProUGUI>();
		TMP_Text font = gui.m_containerName;
		title.font = font.font;
		title.fontSharedMaterial = font.fontSharedMaterial;
		title.color = font.color;
		title.enableAutoSizing = true;
		title.fontSizeMax = 22f;
		title.fontSizeMin = 12f;
		title.alignment = TextAlignmentOptions.Left;
		title.textWrappingMode = TextWrappingModes.NoWrap;
		title.raycastTarget = false;
		title.text = GcLoc.Get("gcvl_title") + ":";
		RectTransform titleRect = title.rectTransform;
		titleRect.anchorMin = titleRect.anchorMax = new Vector2(0f, 1f);
		titleRect.pivot = new Vector2(0f, 1f);
		label = title;

		GameObject go = UnityEngine.Object.Instantiate(gui.m_playerGrid.gameObject, line, false);
		go.name = "GC_SupplyBarGrid";
		InventoryGrid copy = go.GetComponent<InventoryGrid>();
		foreach (Transform child in copy.m_gridRoot)
		{
			UnityEngine.Object.Destroy(child.gameObject); // Felder der Vorlage: das Raster baut seine eigenen
		}
		RectTransform rect = (RectTransform)go.transform;
		rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
		rect.pivot = new Vector2(0f, 1f);
		// so breit wie die Plätze, bevor das Raster sie anlegt: Valheim zentriert sie nach der Breite beim Anlegen
		// (vorher erst danach gesetzt: die Plätze lagen verschoben über der Überschrift, Gorduans Bildschirmfoto 03.11.)
		rect.sizeDelta = new Vector2(Width * copy.m_elementSpace, Rows * copy.m_elementSpace);
		// eigene, nie aktive UI-Gruppe: sonst steuerte das Gamepad dieses Raster zusammen mit dem Inventar
		GameObject groupGo = new("GC_SupplyBarGroup", typeof(RectTransform));
		groupGo.transform.SetParent(go.transform, false);
		UIGroupHandler group = groupGo.AddComponent<UIGroupHandler>();
		group.m_groupPriority = -1000;
		group.SetActive(false);
		copy.m_uiGroup = group;
		copy.m_onSelected = (g, item, pos, mod) => gui.OnSelectedItem(g, item, pos, mod);
		copy.m_onReleased = (g, item, pos) => gui.OnReleasedItem(g, item, pos);
		copy.m_onRightClick = (g, item, pos) => Use(item);
		copy.m_onEnter = (g, pos) => { };
		copy.OnMoveToLowerInventoryGrid = pos => { };
		copy.OnMoveToUpperInventoryGrid = pos => { };
		copy.OnSetTouchSelection = g => { };
		copy.CanDropDragOntoItem = gui.CanDropDragOntoItem;
		grid = copy;

		// Knöpfe unter den Plätzen (Gorduan, 04.11.2026; Idee aus EpicLoot 0.15.6, dessen Verzauberungstisch „Alle Materialien
		// nehmen“ hat), mit Tooltip wie die Plätze
		if (gui.m_takeAllButton != null)
		{
			UITooltip? template = gui.m_playerGrid.m_elementPrefab != null ? gui.m_playerGrid.m_elementPrefab.GetComponentInChildren<UITooltip>(true) : null;
			fillButton = MakeButton(gui, "GC_SupplyFill", "gcvl_fill", true, template, out fillTip);
			storeButton = MakeButton(gui, "GC_SupplyStore", "gcvl_store", false, template, out storeTip);
		}
		return true;
	}

	private static Button MakeButton(InventoryGui gui, string name, string key, bool fill, UITooltip? template, out UITooltip? tooltip)
	{
		Button button = GcUi.CloneButton(gui.m_takeAllButton, line!, name, GcLoc.Get(key), new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero,
			new Vector2(120f, ButtonHeight), () => SupplyChests.Start(fill));
		tooltip = null;
		if (template != null && template.m_tooltipPrefab != null)
		{
			tooltip = button.GetComponent<UITooltip>();
			if (tooltip == null)
			{
				tooltip = button.gameObject.AddComponent<UITooltip>();
			}
			tooltip.m_tooltipPrefab = template.m_tooltipPrefab;
			tooltip.m_topic = GcLoc.Get(key + "_topic");
		}
		return button;
	}

	/// <summary>Knöpfe aus, solange eine Anfrage läuft; Auffüllen nur mit eingeschalteter Leiste. Tooltip mit V+-Reichweite.</summary>
	private static void UpdateButtons(Inventory inventory)
	{
		bool fill = On && !SupplyChests.Busy;
		bool store = !SupplyChests.Busy && inventory.NrOfItems() > 0;
		if (fillButton != null && fillButton.interactable != fill)
		{
			fillButton.interactable = fill;
		}
		if (storeButton != null && storeButton.interactable != store)
		{
			storeButton.interactable = store;
		}
		if (Time.unscaledTime < tipsUntil)
		{
			return;
		}
		tipsUntil = Time.unscaledTime + 2f; // V+-Einstellungen nur alle zwei Sekunden lesen
		if (fillTip != null)
		{
			fillTip.m_text = GcLoc.Get("gcvl_fill_help", SupplyChests.Reach(true));
		}
		if (storeTip != null)
		{
			storeTip.m_text = GcLoc.Get("gcvl_store_help", SupplyChests.Reach(false));
		}
	}
}

// ---------------------------------------------------------------------- Valheim-Haken

/// <summary>Inventar-Bildschirm: nach Valheims eigenem Raster das der Vorratsleiste.</summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateInventory))]
internal static class SupplyBarGridPatch
{
	private static bool reported;

	private static void Postfix(InventoryGui __instance, Player player)
	{
		try
		{
			SupplyBar.UpdateGrid(__instance, player);
		}
		catch (Exception e)
		{
			if (!reported)
			{
				reported = true;
				SupplyBar.Log.LogWarning($"Vorratsleiste: Raster im Inventar: {e}");
			}
		}
	}
}

/// <summary>Inventar zu: Leiste aus, eine verschobene Kiste zurück.</summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
internal static class SupplyBarHidePatch
{
	private static void Postfix(InventoryGui __instance) => SupplyBar.Hide(__instance);
}

/// <summary>Inventar-Bildschirm, vor Valheim: Ablegen nach den Regeln der Leiste; Umschalt-Klick in der Leiste eigener Weg.</summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnSelectedItem))]
internal static class SupplyBarSelectPatch
{
	private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, Vector2i pos, InventoryGrid.Modifier mod)
	{
		if (grid == null || grid.GetInventory() == null)
		{
			return true;
		}
		if (__instance.m_dragGo != null)
		{
			if (__instance.m_dragItem == null || __instance.m_dragInventory == null)
			{
				return true;
			}
			string? refusal = SupplyBar.DropRefusal(grid.GetInventory(), __instance.m_dragInventory, __instance.m_dragItem, pos);
			if (refusal == null)
			{
				return true;
			}
			Player.m_localPlayer?.Message(MessageHud.MessageType.Center, refusal);
			return false;
		}
		if (mod == InventoryGrid.Modifier.Move && item != null && SupplyBar.IsBar(grid.GetInventory()))
		{
			SupplyBar.QuickMove(__instance, item);
			return false;
		}
		return true;
	}
}

/// <summary>Ablegen im Raster (auch auf anderen Wegen als dem Klick): dieselben Regeln.</summary>
[HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.DropItem))]
internal static class SupplyBarDropPatch
{
	private static bool Prefix(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item, Vector2i pos, ref bool __result)
	{
		string? refusal = __instance.GetInventory() != null && fromInventory != null && item != null
			? SupplyBar.DropRefusal(__instance.GetInventory(), fromInventory, item, pos)
			: null;
		if (refusal == null)
		{
			return true;
		}
		Player.m_localPlayer?.Message(MessageHud.MessageType.Center, refusal);
		__result = false;
		return false;
	}
}

/// <summary>
/// Ohne offene Kiste bricht Valheim in UpdateContainer jedes Ziehen ab, das nicht aus dem Spieler-Inventar kommt; aus der
/// Leiste soll man aber auch ohne Kiste herausziehen können (wie bei der Leinenleiste).
/// </summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateContainer))]
internal static class SupplyBarContainerPatch
{
	internal static bool Inside;

	private static void Prefix() => Inside = true;

	private static Exception? Finalizer(Exception? __exception)
	{
		Inside = false;
		return __exception;
	}
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupDragItem))]
internal static class SupplyBarDragPatch
{
	private static bool Prefix(InventoryGui __instance, ItemDrop.ItemData item) =>
		!(SupplyBarContainerPatch.Inside && item == null && SupplyBar.IsBar(__instance.m_dragInventory));
}

/// <summary>Tragegewicht: die Leiste zählt mit (Inventar-Anzeige, Überladen, Aufheben).</summary>
[HarmonyPatch(typeof(Inventory), nameof(Inventory.GetTotalWeight))]
internal static class SupplyBarWeightPatch
{
	private static void Postfix(Inventory __instance, ref float __result) => __result += SupplyBar.ExtraWeight(__instance);
}

/// <summary>Vor dem Speichern des Charakters: Leiste hineinschreiben.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.Save))]
internal static class SupplyBarSavePatch
{
	private static void Prefix(Player __instance)
	{
		if (__instance == Player.m_localPlayer && SupplyBar.Of(__instance) is Inventory inventory)
		{
			SupplyBar.Save(__instance, inventory);
		}
	}
}

/// <summary>Neuer Spieler (Laden des Charakters): Leiste aus dessen Daten neu lesen.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.Load))]
internal static class SupplyBarLoadPatch
{
	private static void Postfix(Player __instance) => SupplyBar.Forget(__instance);
}

// --- Munition aus der Leiste (Bogen, Armbrust, Angel) und Anlegen aus der Leiste

[HarmonyPatch(typeof(Attack), nameof(Attack.HaveAmmo))]
internal static class SupplyAmmoHavePatch
{
	private static void Prefix(Humanoid character, out bool __state) => __state = SupplyBar.AmmoScope.Enter(character);

	private static Exception? Finalizer(Exception? __exception, bool __state)
	{
		SupplyBar.AmmoScope.Leave(__state);
		return __exception;
	}
}

[HarmonyPatch(typeof(Attack), nameof(Attack.FindAmmo))]
internal static class SupplyAmmoFindPatch
{
	private static void Prefix(Humanoid character, out bool __state) => __state = SupplyBar.AmmoScope.Enter(character);

	private static Exception? Finalizer(Exception? __exception, bool __state)
	{
		SupplyBar.AmmoScope.Leave(__state);
		return __exception;
	}
}

[HarmonyPatch(typeof(Attack), nameof(Attack.EquipAmmoItem))]
internal static class SupplyAmmoEquipPatch
{
	private static void Prefix(Humanoid character, out bool __state) => __state = SupplyBar.AmmoScope.Enter(character);

	private static Exception? Finalizer(Exception? __exception, bool __state)
	{
		SupplyBar.AmmoScope.Leave(__state);
		return __exception;
	}
}

[HarmonyPatch(typeof(Attack), nameof(Attack.UseAmmo))]
internal static class SupplyAmmoUsePatch
{
	private static void Prefix(Attack __instance, out bool __state) => __state = SupplyBar.AmmoScope.Enter(__instance.m_character);

	private static Exception? Finalizer(Exception? __exception, bool __state)
	{
		SupplyBar.AmmoScope.Leave(__state);
		return __exception;
	}
}

/// <summary>Anlegen eines Items aus der Leiste (Munition): Valheims EquipItem verlangt es im Inventar.</summary>
[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
internal static class SupplyEquipPatch
{
	private static void Prefix(Humanoid __instance, ItemDrop.ItemData item, out bool __state) =>
		__state = SupplyBar.AmmoScope.InBar(item) && SupplyBar.AmmoScope.Enter(__instance);

	private static Exception? Finalizer(Exception? __exception, bool __state)
	{
		SupplyBar.AmmoScope.Leave(__state);
		return __exception;
	}
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.ContainsItem))]
internal static class SupplyContainsPatch
{
	private static void Postfix(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
	{
		if (!__result && SupplyBar.AmmoScope.Active(__instance) && SupplyBar.AmmoScope.InBar(item))
		{
			__result = true;
		}
	}
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.GetAmmoItem))]
internal static class SupplyGetAmmoPatch
{
	private static void Postfix(Inventory __instance, string ammoName, string matchPrefabName, ref ItemDrop.ItemData __result)
	{
		if (__result == null && SupplyBar.AmmoScope.Active(__instance) && SupplyBar.Of(Player.m_localPlayer) is Inventory own)
		{
			__result = own.GetAmmoItem(ammoName, matchPrefabName); // erst das Inventar (wie Valheim), dann die Leiste
		}
	}
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(ItemDrop.ItemData), typeof(int))]
internal static class SupplyRemovePatch
{
	private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, int amount, ref bool __result)
	{
		if (!SupplyBar.AmmoScope.Active(__instance) || __instance.m_inventory.Contains(item) || !SupplyBar.AmmoScope.InBar(item))
		{
			return true;
		}
		__result = SupplyBar.Of(Player.m_localPlayer)!.RemoveItem(item, amount);
		return false;
	}
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveOneItem))]
internal static class SupplyRemoveOnePatch
{
	private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
	{
		if (!SupplyBar.AmmoScope.Active(__instance) || __instance.m_inventory.Contains(item) || !SupplyBar.AmmoScope.InBar(item))
		{
			return true;
		}
		__result = SupplyBar.Of(Player.m_localPlayer)!.RemoveOneItem(item);
		return false;
	}
}

// --- Tod und Grab

[HarmonyPatch(typeof(Player), nameof(Player.CreateTombStone))]
internal static class SupplyBarGravePatch
{
	private static void Prefix(Player __instance)
	{
		if (__instance == Player.m_localPlayer)
		{
			SupplyBar.BeforeGrave(__instance);
		}
	}
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveInventoryToGrave))]
internal static class SupplyBarGraveFillPatch
{
	private static void Postfix(Inventory __instance, Inventory original) => SupplyBar.IntoGrave(__instance, original);
}

[HarmonyPatch(typeof(TombStone), nameof(TombStone.Start))]
internal static class SupplyBarGraveKnownPatch
{
	private static void Postfix(TombStone __instance) => SupplyBar.RememberGrave(__instance);
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveAll))]
internal static class SupplyBarGraveTakeAllPatch
{
	private static void Postfix(Inventory __instance, Inventory fromInventory) => SupplyBar.AfterMove(__instance, fromInventory);
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveItemToThis), new[] { typeof(Inventory), typeof(ItemDrop.ItemData) })]
internal static class SupplyBarGraveMovePatch
{
	private static void Postfix(Inventory __instance, Inventory fromInventory) => SupplyBar.AfterMove(__instance, fromInventory);
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveItemToThis), new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int) })]
internal static class SupplyBarGraveDragPatch
{
	private static void Postfix(Inventory __instance, Inventory fromInventory) => SupplyBar.AfterMove(__instance, fromInventory);
}

/// <summary>Aus dem Grab auf den Boden geworfen und wieder aufgehoben: Die Markierung des Platzes gilt nicht mehr.</summary>
[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.Pickup))]
internal static class SupplyBarGravePickupPatch
{
	private static void Prefix(GameObject go)
	{
		if (go != null && go.TryGetComponent(out ItemDrop drop))
		{
			SupplyBar.ForgetSlot(drop.m_itemData);
		}
	}
}
