using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using GC.Shared;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GC.Server.Leash;

/// <summary>
/// Leinenleiste (Gorduan, 24.10.2026): neben der Schnellleiste bis zu fünf eigene Plätze nur für Leinen, Schnellwahl Alt+1 bis
/// Alt+5 (Gorduan: Alt, nicht Strg, das ist Ducken). Es wirken immer eine Leine der Schnellleiste und alle Leinen der Leiste.
///  * Plätze nach Tierpflege: Stufe 0 keiner, ab 1 einer, ab 25 zwei, ab 50 drei, ab 75 vier, ab 90 fünf (Gorduan: „der letzte
///    zwischen 90 und 100“). Ist Tierpflege aus, sind alle fünf offen.
///  * Echte Plätze: Die Leine liegt in der Leiste, nicht im Inventar (eigenes Inventar 5×1, gespeichert mit dem Charakter in
///    Player.m_customData). Hineinziehen und Herausnehmen im Inventar-Bildschirm, rechts neben der Schnellleiste.
///  * Sinkt Tierpflege unter eine Stufe, bleibt die Leine im gesperrten Platz, hält dort aber kein Tier (Gorduan).
///  * In der Schnellleiste hält nur eine Leine ein Tier (die linke, wenn es doch zwei sind); eine zweite nimmt keines an, und
///    eine Leine mit Tier lässt sich nicht dazulegen.
///  * Beim Tod kommen die Leinen der Leiste mit ins Grab (Gorduan). Pfeifen gehören weiter in die Schnellleiste.
/// Ausrüsten aus der Leiste: Valheims EquipItem verlangt das Item im Spieler-Inventar; ein Transpiler lässt dort auch die
/// Leiste des eigenen Spielers zu.
/// </summary>
internal static class LeashBar
{
	internal const int Slots = 5;

	/// <summary>Tierpflege-Stufe je Platz (Gorduan, 24.10.2026).</summary>
	internal static readonly int[] Levels = { 1, 25, 50, 75, 90 };

	private const string DataKey = "gc_leashbar";
	private const float Gap = 18f;

	private static Player? owner;
	private static Inventory? bar;

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
		Inventory inventory = new("GC_LeashBar", null, Slots, 1);
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
				Plugin.Log.LogWarning($"Leinenleiste: gespeicherte Leinen nicht lesbar ({e.Message}), der alte Stand liegt unter {DataKey}_kaputt.");
			}
		}
		inventory.m_onChanged += () => Save(player, inventory);
		return inventory;
	}

	/// <summary>In den Charakter schreiben (bei jeder Änderung und vor jedem Speichern, wegen der Haltbarkeit).</summary>
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

	/// <summary>Offene Plätze nach Tierpflege; ohne den Skill (Tierpflege aus) alle.</summary>
	internal static int Unlocked(Player player)
	{
		if (!Care.Enabled.Value || Care.Skill == Skills.SkillType.None)
		{
			return Slots;
		}
		float level = player.GetSkillLevel(Care.Skill);
		int count = 0;
		foreach (int need in Levels)
		{
			if (level >= need)
			{
				count++;
			}
		}
		return count;
	}

	internal static bool Contains(Player player, ItemDrop.ItemData item) => Of(player) is Inventory inventory && inventory.ContainsItem(item);

	/// <summary>Die Leine liegt in einem offenen Platz der Leiste: dort hält sie ihr Tier.</summary>
	internal static bool Active(Player player, ItemDrop.ItemData item) => Contains(player, item) && item.m_gridPos.x < Unlocked(player);

	/// <summary>Inventar, in dem die Leine liegt: Spieler-Inventar oder Leiste, sonst null.</summary>
	internal static Inventory? InventoryWith(Player player, ItemDrop.ItemData item)
	{
		Inventory inventory = player.GetInventory();
		return inventory.ContainsItem(item) ? inventory : Contains(player, item) ? bar : null;
	}

	/// <summary>Leine mit dieser Leinen-ID in der Leiste.</summary>
	internal static ItemDrop.ItemData? Find(Player player, string id)
	{
		if (Of(player) is not Inventory inventory)
		{
			return null;
		}
		foreach (ItemDrop.ItemData item in inventory.GetAllItems())
		{
			if (item.m_customData != null && item.m_customData.TryGetValue(Plugin.KeyItemId, out string value) && value == id)
			{
				return item;
			}
		}
		return null;
	}

	/// <summary>Alle Leinen der Leiste in offenen Plätzen (für die Zähmhilfe).</summary>
	internal static IEnumerable<ItemDrop.ItemData> OpenItems(Player player)
	{
		if (Of(player) is not Inventory inventory)
		{
			yield break;
		}
		int open = Unlocked(player);
		foreach (ItemDrop.ItemData item in inventory.GetAllItems())
		{
			if (item.m_gridPos.x < open)
			{
				yield return item;
			}
		}
	}

	/// <summary>Die Leine der Schnellleiste, die gerade ein Tier hält (außer exclude): dort wirkt immer nur eine (Gorduan).</summary>
	internal static Link? HotbarLink(Player player, ItemDrop.ItemData? exclude)
	{
		Inventory inventory = player.GetInventory();
		foreach (Link link in Links.Active)
		{
			if (link.Holder == player && link.Item != null && link.Item != exclude && Links.InHotbar(link.Item) && inventory.ContainsItem(link.Item))
			{
				return link;
			}
		}
		return null;
	}

	/// <summary>Plätze für eine Leine vom Pfosten: offene freie Plätze der Leiste, dazu die Schnellleiste, solange dort keine Leine ein Tier hält.</summary>
	internal static int FreePlaces(Player player)
	{
		int free = 0;
		if (Of(player) is Inventory inventory)
		{
			int open = Unlocked(player);
			for (int x = 0; x < open; x++)
			{
				if (inventory.GetItemAt(x, 0) == null)
				{
					free++;
				}
			}
		}
		if (HotbarLink(player, null) == null && Posts.FreeHotbarSlots(player.GetInventory()) > 0)
		{
			free++;
		}
		return free;
	}

	/// <summary>Leine vom Pfosten einsortieren: zuerst ein offener freier Platz der Leiste, sonst die Schnellleiste (nur wenn dort keine Leine ein Tier hält).</summary>
	internal static bool Place(Player player, ItemDrop.ItemData item)
	{
		if (Of(player) is Inventory inventory)
		{
			int open = Unlocked(player);
			for (int x = 0; x < open; x++)
			{
				if (inventory.GetItemAt(x, 0) == null && inventory.AddItem(item, new Vector2i(x, 0)))
				{
					return true;
				}
			}
		}
		if (HotbarLink(player, null) != null)
		{
			return false;
		}
		Inventory main = player.GetInventory();
		for (int x = 0; x < Math.Min(8, main.GetWidth()); x++)
		{
			if (main.GetItemAt(x, 0) == null && main.AddItem(item, new Vector2i(x, 0)))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>Alt+Zahl: Leine des Platzes in die Hand nehmen oder weglegen.</summary>
	internal static void Use(Player player, int x)
	{
		if (Of(player) is not Inventory inventory)
		{
			return;
		}
		if (x >= Unlocked(player))
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gclb_locked", x + 1, Levels[x]));
			return;
		}
		ItemDrop.ItemData? item = inventory.GetItemAt(x, 0);
		if (item == null)
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gclb_empty", x + 1));
			return;
		}
		player.ToggleEquipped(item); // wie die Zahlentaste; UseItem rüstet nur aus dem Spieler-Inventar aus
	}

	/// <summary>Valheims EquipItem (Transpiler): auch Leinen aus der eigenen Leiste.</summary>
	internal static bool ContainsForEquip(Inventory inventory, ItemDrop.ItemData item)
	{
		return inventory.ContainsItem(item) || (owner != null && bar != null && inventory == owner.GetInventory() && bar.ContainsItem(item));
	}

	/// <summary>
	/// Ablegen im Inventar-Bildschirm prüfen (null = erlaubt): in die Leiste nur Leinen und nur in offene Plätze, kein Tausch,
	/// der etwas anderes hineinbrächte, und keine zweite Leine mit Tier in die Schnellleiste.
	/// </summary>
	internal static string? DropRefusal(Inventory target, Inventory from, ItemDrop.ItemData item, Vector2i pos)
	{
		Player? player = owner;
		if (player == null || bar == null || player != Player.m_localPlayer)
		{
			return null;
		}
		ItemDrop.ItemData? swapped = target.GetItemAt(pos.x, pos.y);
		if (swapped == item)
		{
			return null;
		}
		if (target == bar)
		{
			if (!Plugin.IsLeash(item, out _))
			{
				return GcLoc.Get("gclb_only_leash");
			}
			if (pos.x >= Unlocked(player))
			{
				return GcLoc.Get("gclb_locked", pos.x + 1, Levels[Mathf.Clamp(pos.x, 0, Slots - 1)]);
			}
		}
		if (from == bar && target != bar && swapped != null && !Plugin.IsLeash(swapped, out _))
		{
			return GcLoc.Get("gclb_only_leash"); // der Tausch legte etwas anderes in die Leiste
		}
		if (from == bar && swapped != null && item.m_gridPos.x >= Unlocked(player))
		{
			// der Tausch legte die verdrängte Leine in einen gesperrten Platz (Prüfung 24.10.2026)
			return GcLoc.Get("gclb_locked", item.m_gridPos.x + 1, Levels[Mathf.Clamp(item.m_gridPos.x, 0, Slots - 1)]);
		}
		Inventory main = player.GetInventory();
		// eine Leine mit Tier in die Schnellleiste, in der schon eine Leine ein Tier hält
		if (target == main && pos.y == 0 && pos.x < 8 && Links.ForItem(item) != null)
		{
			Link? holding = HotbarLink(player, item);
			if (holding != null && holding.Item != swapped)
			{
				return GcLoc.Get("gclb_hotbar_one");
			}
		}
		// Tausch in die Gegenrichtung: die verdrängte Leine mit Tier landet in der Schnellleiste
		if (from == main && item.m_gridPos.y == 0 && item.m_gridPos.x < 8 && swapped != null && Links.ForItem(swapped) != null)
		{
			Link? holding = HotbarLink(player, item);
			if (holding != null && holding.Item != swapped)
			{
				return GcLoc.Get("gclb_hotbar_one");
			}
		}
		return null;
	}

	// ------------------------------------------------------------------ Tod

	/// <summary>
	/// Vor dem Grab (Gorduan: die Leinen der Leiste kommen mit hinein), nach Valheims Weltregeln in CreateTombStone: „Inventar
	/// behalten“ lässt sie in der Leiste; „Items löschen“ bzw. „Nicht Ausgerüstetes löschen“ löscht wie dort (eine Leine in
	/// der Hand legt Valheim vorher ab, außer „Ausrüstung behalten“ oder „Nicht Ausgerüstetes löschen“). Ist das Inventar leer,
	/// entstünde gar kein Grab: dann die Leinen erst ins Inventar (Prüfung 24.10.2026).
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
		bool unequipFirst = !keepEquip && !deleteUnequipped; // so legt Valheim vor dem Löschen ab
		if (zones.GetGlobalKey(GlobalKeys.DeathDeleteItems) || deleteUnequipped)
		{
			foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(inventory.GetAllItems()))
			{
				if (!item.m_shared.m_questItem && (unequipFirst || !item.m_equipped))
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
					player.UnequipItem(item, false); // ginge sonst nicht mit ins Grab
				}
				if (!item.m_equipped && main.AddItem(item))
				{
					inventory.RemoveItem(item);
				}
			}
		}
		pendingGrave = player;
	}

	/// <summary>
	/// Umschalt-Klick in der Leiste: mit offener Kiste in die Kiste, sonst ins Inventar. Valheims eigener Weg nähme das
	/// Spieler-Inventar als Quelle, und die Leine stünde dann doppelt in Kiste und Leiste (Prüfung 24.10.2026).
	/// </summary>
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
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gclb_no_room"));
			return;
		}
		target.MoveItemToThis(inventory, item);
		gui.m_moveItemEffects.Create(gui.transform.position, Quaternion.identity);
	}

	internal static bool IsBar(Inventory? inventory) => inventory != null && inventory == bar;

	private static Player? pendingGrave;

	/// <summary>Ins Grab: was noch in der Leiste liegt (nicht in der Hand), notfalls bekommt das Grab eine Reihe mehr.</summary>
	internal static void IntoGrave(Inventory grave, Inventory original)
	{
		Player? player = pendingGrave;
		pendingGrave = null;
		if (player == null || original != player.GetInventory() || Of(player) is not Inventory inventory)
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
			if (grave.AddItem(item))
			{
				inventory.RemoveItem(item); // erst im Grab, dann aus der Leiste: verloren geht nichts
			}
		}
	}

	// ------------------------------------------------------------------ Anzeige im Spiel (rechts neben der Schnellleiste)

	private sealed class HudSlot
	{
		public GameObject Go = null!;
		public Image Icon = null!;
		public GuiBar Durability = null!;
		public TMP_Text Amount = null!;
		public GameObject Equiped = null!;
		public GameObject Queued = null!;
		public Image? Back;
	}

	private static RectTransform? hud;
	private static readonly List<HudSlot> HudSlots = new();

	/// <summary>Jedes Bild (Plugin.Update): Anzeige neben der Schnellleiste, im Inventar-Bildschirm das Raster.</summary>
	internal static void Tick()
	{
		Player? player = Player.m_localPlayer;
		bool show = player != null && !player.IsDead() && Plugin.On && Of(player) is Inventory inventory && (Unlocked(player) > 0 || inventory.NrOfItems() > 0);
		if (!show || player == null)
		{
			if (hud != null)
			{
				hud.gameObject.SetActive(false);
			}
			return;
		}
		if (!EnsureHud())
		{
			return;
		}
		bool inventoryOpen = InventoryGui.IsVisible();
		hud!.gameObject.SetActive(!inventoryOpen); // im Inventar zeigt das Raster dieselben Plätze
		if (!inventoryOpen)
		{
			UpdateHud(player);
		}
	}

	private static HotkeyBar? hotkeysCache;

	/// <summary>Valheims Schnellleiste im HUD (einmal gesucht, neu nach einem neuen HUD).</summary>
	private static HotkeyBar? Hotkeys()
	{
		if (hotkeysCache == null && Hud.instance != null)
		{
			hotkeysCache = Hud.instance.GetComponentInChildren<HotkeyBar>(true);
		}
		return hotkeysCache;
	}

	/// <summary>Lage rechts neben den acht Plätzen der Schnellleiste (die Schnellleiste selbst ist nur so lang wie belegt).</summary>
	private static Vector2 Position(HotkeyBar hotkeys)
	{
		RectTransform rect = (RectTransform)hotkeys.transform;
		return rect.anchoredPosition + new Vector2(8f * hotkeys.m_elementSpace + Gap, 0f);
	}

	private static bool EnsureHud()
	{
		if (hud != null)
		{
			return true;
		}
		HotkeyBar? hotkeys = Hotkeys();
		if (hotkeys == null || hotkeys.m_elementPrefab == null)
		{
			return false;
		}
		RectTransform source = (RectTransform)hotkeys.transform;
		GameObject root = new("GC_LeashBar", typeof(RectTransform));
		root.transform.SetParent(source.parent, false);
		hud = (RectTransform)root.transform;
		hud.anchorMin = source.anchorMin;
		hud.anchorMax = source.anchorMax;
		hud.pivot = source.pivot;
		hud.sizeDelta = new Vector2(Slots * hotkeys.m_elementSpace, source.sizeDelta.y);
		hud.anchoredPosition = Position(hotkeys);
		HudSlots.Clear();
		for (int i = 0; i < Slots; i++)
		{
			GameObject go = UnityEngine.Object.Instantiate(hotkeys.m_elementPrefab, hud);
			go.name = "GC_LeashSlot" + (i + 1);
			go.transform.localPosition = new Vector3(i * hotkeys.m_elementSpace, 0f, 0f);
			if (go.GetComponent<Button>() is Button button)
			{
				button.onClick.RemoveAllListeners();
			}
			HudSlot slot = new()
			{
				Go = go,
				Icon = go.transform.Find("icon").GetComponent<Image>(),
				Durability = go.transform.Find("durability").GetComponent<GuiBar>(),
				Amount = go.transform.Find("amount").GetComponent<TMP_Text>(),
				Equiped = go.transform.Find("equiped").gameObject,
				Queued = go.transform.Find("queued").gameObject,
				Back = go.transform.Find("bkg")?.GetComponent<Image>(), // Rahmen des Platzes (Valheims HotKeyElement)
			};
			StyleBinding(go.transform.Find("binding").GetComponent<TMP_Text>(), i + 1);
			go.transform.Find("selected")?.gameObject.SetActive(false);
			HudSlots.Add(slot);
		}
		return true;
	}

	private static void UpdateHud(Player player)
	{
		HotkeyBar? hotkeys = Hotkeys();
		if (hud == null || hotkeys == null || Of(player) is not Inventory inventory)
		{
			return;
		}
		hud.anchoredPosition = Position(hotkeys); // falls eine andere Mod die Schnellleiste verschiebt
		int open = Unlocked(player);
		for (int x = 0; x < HudSlots.Count; x++)
		{
			HudSlot slot = HudSlots[x];
			bool locked = x >= open;
			ItemDrop.ItemData? item = inventory.GetItemAt(x, 0);
			if (slot.Back != null)
			{
				slot.Back.color = locked ? new Color(1f, 1f, 1f, 0.35f) : Color.white;
			}
			slot.Icon.gameObject.SetActive(item != null);
			slot.Durability.gameObject.SetActive(false);
			slot.Equiped.SetActive(item != null && item.m_equipped);
			slot.Queued.SetActive(item != null && player.IsEquipActionQueued(item));
			string text = "";
			if (item != null)
			{
				slot.Icon.sprite = item.GetIcon();
				slot.Icon.color = locked ? new Color(0.5f, 0.5f, 0.5f, 0.8f) : Color.white;
				bool worn = item.m_shared.m_useDurability && item.m_durability < item.GetMaxDurability();
				slot.Durability.gameObject.SetActive(worn);
				if (worn)
				{
					if (item.m_durability <= 0f)
					{
						slot.Durability.SetValue(1f);
						slot.Durability.SetColor(Mathf.Sin(Time.time * 10f) > 0f ? Color.red : new Color(0f, 0f, 0f, 0f));
					}
					else
					{
						slot.Durability.SetValue(item.GetDurabilityPercentage());
						slot.Durability.ResetColor();
					}
				}
				Link? link = Links.ForItem(item);
				if (link != null && link.Tame != null)
				{
					text = Short(link.Tame.GetHoverName()); // das Tier an dieser Leine
				}
			}
			if (locked)
			{
				text = GcLoc.Get("gclb_from", Levels[x]); // gesperrt: ab welcher Tierpflege
			}
			slot.Amount.gameObject.SetActive(text.Length > 0);
			slot.Amount.text = text;
		}
	}

	/// <summary>
	/// „Alt+1“ in einer Zeile oben links im Platz. Valheims Feld ist für eine Ziffer gebaut (20 px breit, mit Umbruch): dort
	/// stand „Alt+“ umgebrochen über der Ziffer (Gorduan, 25.10.2026, Bildschirmfoto).
	/// </summary>
	private static void StyleBinding(TMP_Text binding, int slot)
	{
		binding.enabled = true;
		binding.text = "Alt+" + slot;
		binding.textWrappingMode = TextWrappingModes.NoWrap;
		binding.overflowMode = TextOverflowModes.Overflow;
		binding.fontSize = Mathf.Min(binding.fontSize, 15f);
		binding.alignment = TextAlignmentOptions.TopLeft;
		RectTransform rect = binding.rectTransform;
		rect.pivot = new Vector2(0f, 1f);
		rect.anchoredPosition = new Vector2(4f, -2f);
		rect.sizeDelta = new Vector2(56f, 18f);
	}

	private static string Short(string name)
	{
		string plain = Localization.instance != null ? Localization.instance.Localize(name) : name;
		return plain.Length <= 9 ? plain : plain.Substring(0, 8) + "…";
	}

	// ------------------------------------------------------------------ Raster im Inventar-Bildschirm

	private static InventoryGrid? grid;
	private static int gridElements;

	/// <summary>Jedes Bild, solange das Inventar offen ist (nach Valheims UpdateInventory): das Raster der Leiste.</summary>
	internal static void UpdateGrid(InventoryGui gui, Player player)
	{
		if (!Plugin.On || Of(player) is not Inventory inventory || (Unlocked(player) == 0 && inventory.NrOfItems() == 0))
		{
			if (grid != null)
			{
				grid.gameObject.SetActive(false);
			}
			return;
		}
		if (!EnsureGrid(gui))
		{
			return;
		}
		grid!.gameObject.SetActive(true);
		HotkeyBar? hotkeys = Hotkeys();
		if (hotkeys != null)
		{
			((RectTransform)grid.transform).anchoredPosition = Position(hotkeys);
		}
		grid.UpdateInventory(inventory, player, gui.m_dragItem);
		int open = Unlocked(player);
		List<InventoryElement> elements = grid.m_elements; // die eigenen Felder des Rasters (die der Vorlage verschwinden am Bildende)
		bool rebuilt = elements.Count != gridElements;
		gridElements = elements.Count;
		foreach (InventoryElement element in elements)
		{
			int x = element.Position.x;
			if (rebuilt && element.transform.Find("binding")?.GetComponent<TMP_Text>() is TMP_Text binding)
			{
				StyleBinding(binding, x + 1);
			}
			bool locked = x >= open;
			if (element.m_button != null && element.m_button.image != null)
			{
				element.m_button.image.color = locked ? new Color(1f, 1f, 1f, 0.35f) : Color.white;
			}
			if (locked)
			{
				element.m_icon.color = new Color(0.5f, 0.5f, 0.5f, 0.8f);
				if (!element.m_used)
				{
					element.m_tooltip.m_topic = GcLoc.Get("gclb_tooltip_topic");
					element.m_tooltip.m_text = GcLoc.Get("gclb_tooltip_locked", Levels[Mathf.Clamp(x, 0, Slots - 1)]);
				}
			}
		}
	}

	private static bool EnsureGrid(InventoryGui gui)
	{
		if (grid != null)
		{
			return true;
		}
		if (gui.m_playerGrid == null || gui.m_inventoryRoot == null)
		{
			return false;
		}
		GameObject go = UnityEngine.Object.Instantiate(gui.m_playerGrid.gameObject, gui.m_inventoryRoot, false);
		go.name = "GC_LeashBarGrid";
		InventoryGrid copy = go.GetComponent<InventoryGrid>();
		foreach (Transform child in copy.m_gridRoot)
		{
			UnityEngine.Object.Destroy(child.gameObject); // Felder der Vorlage: das Raster baut seine eigenen
		}
		RectTransform rect = (RectTransform)go.transform;
		rect.anchorMin = new Vector2(0f, 1f);
		rect.anchorMax = new Vector2(0f, 1f);
		rect.pivot = new Vector2(0f, 1f);
		rect.sizeDelta = new Vector2(Slots * copy.m_elementSpace, 64f); // so hoch wie die Anzeige im Spiel: der Kompass darunter springt nicht
		// eigene, nie aktive UI-Gruppe: sonst steuerte das Gamepad dieses Raster zusammen mit dem Inventar
		GameObject groupGo = new("GC_LeashBarGroup", typeof(RectTransform));
		groupGo.transform.SetParent(go.transform, false);
		UIGroupHandler group = groupGo.AddComponent<UIGroupHandler>();
		group.m_groupPriority = -1000;
		group.SetActive(false);
		copy.m_uiGroup = group;
		copy.m_onSelected = (g, item, pos, mod) => gui.OnSelectedItem(g, item, pos, mod);
		copy.m_onReleased = (g, item, pos) => gui.OnReleasedItem(g, item, pos);
		copy.m_onRightClick = (g, item, pos) =>
		{
			if (item != null && Player.m_localPlayer != null)
			{
				Player.m_localPlayer.ToggleEquipped(item); // Rechtsklick wie im Inventar: in die Hand nehmen oder weglegen
			}
		};
		// Rückrufe werden beim Kopieren nicht mitgenommen: Gamepad und Touch bleiben hier ohne Wirkung
		copy.m_onEnter = (g, pos) => { };
		copy.OnMoveToLowerInventoryGrid = pos => { };
		copy.OnMoveToUpperInventoryGrid = pos => { };
		copy.OnSetTouchSelection = g => { };
		copy.CanDropDragOntoItem = gui.CanDropDragOntoItem;
		grid = copy;
		gridElements = -1;
		return true;
	}

	/// <summary>
	/// Der Charakter wurde (neu) geladen: Leiste beim nächsten Zugriff aus seinen Daten lesen. Sonst schriebe eine vorher leer
	/// geladene Leiste beim Speichern über die gespeicherten Leinen.
	/// </summary>
	internal static void Forget(Player player)
	{
		if (owner == player || player == Player.m_localPlayer)
		{
			owner = null;
			bar = null;
		}
	}
}

// ---------------------------------------------------------------------- Valheim-Haken

/// <summary>EquipItem verlangt das Item im Spieler-Inventar: auch Leinen aus der Leiste des eigenen Spielers zulassen.</summary>
[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
internal static class LeashBarEquipPatch
{
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo contains = AccessTools.Method(typeof(Inventory), nameof(Inventory.ContainsItem));
		MethodInfo ours = AccessTools.Method(typeof(LeashBar), nameof(LeashBar.ContainsForEquip));
		bool replaced = false;
		foreach (CodeInstruction code in instructions)
		{
			if (!replaced && code.Calls(contains))
			{
				replaced = true;
				yield return new CodeInstruction(OpCodes.Call, ours) { labels = code.labels, blocks = code.blocks };
				continue;
			}
			yield return code;
		}
		if (!replaced)
		{
			Plugin.Log.LogWarning("Leinenleiste: Prüfung in Humanoid.EquipItem nicht gefunden, Leinen aus der Leiste lassen sich nicht in die Hand nehmen.");
		}
	}
}

/// <summary>Alt+1 bis Alt+5: Leine aus der Leiste statt aus der Schnellleiste (Gorduan, 24.10.2026).</summary>
[HarmonyPatch(typeof(Player), nameof(Player.UseHotbarItem))]
internal static class LeashBarKeyPatch
{
	private static bool Prefix(Player __instance, int index)
	{
		if (!Plugin.On || __instance != Player.m_localPlayer || index < 1 || index > LeashBar.Slots || !Care.InfoKeyHeld())
		{
			return true;
		}
		LeashBar.Use(__instance, index - 1);
		return false;
	}
}

/// <summary>Vor dem Speichern des Charakters: Leiste mit aktueller Haltbarkeit hineinschreiben.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.Save))]
internal static class LeashBarSavePatch
{
	private static void Prefix(Player __instance)
	{
		if (__instance == Player.m_localPlayer && LeashBar.Of(__instance) is Inventory inventory)
		{
			LeashBar.Save(__instance, inventory);
		}
	}
}

/// <summary>Neuer Spieler (Laden des Charakters): Leiste aus dessen Daten neu lesen.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.Load))]
internal static class LeashBarLoadPatch
{
	private static void Postfix(Player __instance) => LeashBar.Forget(__instance);
}

[HarmonyPatch(typeof(Player), nameof(Player.CreateTombStone))]
internal static class LeashBarGravePatch
{
	private static void Prefix(Player __instance)
	{
		if (__instance == Player.m_localPlayer)
		{
			LeashBar.BeforeGrave(__instance);
		}
	}
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveInventoryToGrave))]
internal static class LeashBarGraveFillPatch
{
	private static void Postfix(Inventory __instance, Inventory original) => LeashBar.IntoGrave(__instance, original);
}

/// <summary>
/// Inventar-Bildschirm, vor Valheim: Ablegen nach den Regeln der Leiste prüfen, bevor Valheim beide Items ab- und hinterher
/// wieder ausrüstet (eine Ablehnung erst in DropItem verschöbe sonst die Ausrüstung). Umschalt-Klick in der Leiste über
/// LeashBar.QuickMove: Valheim nähme das Spieler-Inventar als Quelle (Prüfung 24.10.2026).
/// </summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnSelectedItem))]
internal static class LeashBarSelectPatch
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
			string? refusal = LeashBar.DropRefusal(grid.GetInventory(), __instance.m_dragInventory, __instance.m_dragItem, pos);
			if (refusal == null)
			{
				return true;
			}
			Player.m_localPlayer?.Message(MessageHud.MessageType.Center, refusal);
			return false;
		}
		if (mod == InventoryGrid.Modifier.Move && item != null && LeashBar.IsBar(grid.GetInventory()))
		{
			LeashBar.QuickMove(__instance, item);
			return false;
		}
		return true;
	}
}

/// <summary>
/// Ohne offene Kiste bricht Valheim in UpdateContainer jedes Ziehen ab, das nicht aus dem Spieler-Inventar kommt; aus der
/// Leiste soll man aber auch ohne Kiste herausziehen können (Prüfung 24.10.2026).
/// </summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateContainer))]
internal static class LeashBarContainerPatch
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
internal static class LeashBarDragPatch
{
	private static bool Prefix(InventoryGui __instance, ItemDrop.ItemData item) =>
		!(LeashBarContainerPatch.Inside && item == null && LeashBar.IsBar(__instance.m_dragInventory));
}

/// <summary>Reparieren an der Werkbank: Valheim nimmt nur beschädigte Items des Spieler-Inventars, dazu die der Leiste.</summary>
[HarmonyPatch(typeof(Inventory), nameof(Inventory.GetWornItems))]
internal static class LeashBarRepairPatch
{
	private static void Postfix(Inventory __instance, List<ItemDrop.ItemData> worn)
	{
		Player? player = Player.m_localPlayer;
		if (player == null || __instance != player.GetInventory() || LeashBar.Of(player) is not Inventory bar)
		{
			return;
		}
		foreach (ItemDrop.ItemData item in bar.GetAllItems())
		{
			if (item.m_shared.m_useDurability && item.m_durability < item.GetMaxDurability())
			{
				worn.Add(item);
			}
		}
	}
}

/// <summary>Ablegen im Inventar-Bildschirm: Regeln der Leiste (nur Leinen, offene Plätze, eine Leine mit Tier in der Schnellleiste).</summary>
[HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.DropItem))]
internal static class LeashBarDropPatch
{
	private static bool Prefix(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item, Vector2i pos, ref bool __result)
	{
		string? refusal = __instance.GetInventory() != null && fromInventory != null && item != null
			? LeashBar.DropRefusal(__instance.GetInventory(), fromInventory, item, pos)
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

/// <summary>Inventar-Bildschirm: nach Valheims eigenem Raster das der Leiste.</summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateInventory))]
internal static class LeashBarGridPatch
{
	private static void Postfix(InventoryGui __instance, Player player)
	{
		try
		{
			LeashBar.UpdateGrid(__instance, player);
		}
		catch (Exception e)
		{
			if (!reported)
			{
				reported = true;
				Plugin.Log.LogWarning($"Leinenleiste: Raster im Inventar: {e}");
			}
		}
	}

	private static bool reported;
}
