using System;
using BepInEx.Configuration;
using GC.Shared;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GC.Server.Outfit;

/// <summary>
/// Outfit-Fenster am Kleiderschrank: Valheims Inventar-Bildschirm, an der Stelle der Kiste das Outfit (Titel „Outfit“).
/// Gorduan, 31.10.2026: Tabellen statt Raster mit Beschriftung im Platz („(aus) ist zu lang, genauso wie einige Platznamen“):
/// drei Tabellen nebeneinander (Rüstung, Einhandwaffen, Zweihand und Fernkampf), je Zeile ausgeschriebener Platzname,
/// darunter klein das eingelegte Teil, dahinter der Platz und ein Auge zum Aus- und Einblenden. Die 18 Plätze
/// bleiben Valheims Kistenraster (6 × 3: Spalte des Rasters = Zeile der Tabelle, Reihe = Tabelle), nur anders angeordnet;
/// Ziehen und Ablegen wie bei einer Kiste, Umschalt-Klick legt ein Teil in seinen Platz bzw. zurück ins Inventar (ohne Kiste
/// würfe Valheim es sonst auf den Boden), Rechtsklick auf einen Platz blendet das Teil aus oder wieder ein. Beim
/// Schließen bekommt das Kistenfeld Größe und Anordnung zurück (auch der Laderaum des Langschiffs ist 6 × 3).
/// Gorduan: Wechseln nur am Schrank. Entfernt sich der Spieler, schließt sich das Fenster; die Türen gehen dann wieder zu.
///
/// Gorduan, 02.11.2026: Reiter oben im Feld, „Stil“ (das Outfit, nur fürs Aussehen) und vier Ausrüstungs-Sätze (Sets), dazu die
/// Knöpfe „Anlegen“ und „Umbenennen“ (Valheims Texteingabe). Seit 03.11.2026 (Gorduan: „hübscheres und besseres Design“) zeigt
/// ein Satz Tabellen wie der Stil-Reiter (Rüstung, Hände und Zubehör, Leinenleiste), darunter Schnellleiste und Vorräte als
/// Leisten; leere Plätze in beiden Reitern zeigen ein blasses Bild ihres Platzes.
/// </summary>
internal static class WardrobeWindow
{
	private const float MaxDistance = 4f;
	private const float RowScale = 0.85f; // Plätze etwas kleiner, damit sechs Zeilen ins Kistenfeld passen
	private const float LabelWidth = 150f;
	private const float EyeWidth = 28f;
	private const float HeaderHeight = 30f;
	private const float TableGap = 12f;
	private const float Margin = 12f;
	private const float TabsHeight = 36f;
	private const float TabGap = 6f;
	private const float SetHeaderHeight = 38f;
	private const float SectionHeight = 26f;
	private const float BarGap = 10f;
	private const float SetButtonWidth = 160f;

	/// <summary>
	/// Satz als Tabellen wie der Stil-Reiter (Gorduan, 03.11.2026: „hübscheres und besseres Design“): Tabelle und Zeile je Platz der
	/// Ausrüstungsreihe. Rüstung: Kopf, Brust, Beine, Umhang; Hände und Zubehör: rechte und linke Hand, Gürtel, Schmuckstück.
	/// Mit GC_Tiere als dritte Tabelle die Leinenleiste; darunter Schnellleiste und Vorräte als Leisten.
	/// </summary>
	private static readonly int[] GearTable = { 0, 0, 0, 0, 1, 1, 1, 1 };
	private static readonly int[] GearLine = { 0, 1, 2, 3, 2, 3, 0, 1 };

	private static int SetTables => LeashLink.Loaded ? 3 : 2;

	private static int SetTableRows => LeashLink.Loaded ? LeashLink.Slots : 4;

	/// <summary>Tooltip eines leeren Platzes je Reihe des Satzes (Ausrüstung: der Name des Platzes).</summary>
	private static readonly string[] RowTopics = { "", "gcof_set_hotbar", "gcof_set_inventory", "gcof_set_leashes" }; // je Sets.Kind
	private static readonly string[] RowHelps = { "gcof_set_help_gear", "gcof_set_help_hotbar", "gcof_set_help_supplies", "gcof_set_help_leashes" };
	private static readonly string[] LiveHelps = { "gcof_set_live_gear", "gcof_set_live_row", "gcof_set_live_supplies", "gcof_set_live_leashes" };

	/// <summary>Leisten unter den Tabellen: Schnellleiste, mit GC_Vorratsleiste dazu die Vorräte.</summary>
	private static int SetBars => SupplyLink.Loaded ? 2 : 1;

	/// <summary>
	/// Reihen der Vorräte (Gorduan, 04.11.2026: bis 64 Plätze, acht je Reihe): so viele, wie Plätze gelten, und belegte dahinter, über
	/// alle Sätze und die Leiste, damit das Feld beim Umschalten nicht springt. 0 ohne GC_Vorratsleiste.
	/// </summary>
	private static int SupplyRows(Player? player)
	{
		if (!SupplyLink.Loaded || player == null)
		{
			return SupplyLink.Loaded ? 1 : 0;
		}
		int shown = SupplyLink.Slots;
		if (Sets.Of(player) is Inventory[] sets)
		{
			foreach (Inventory set in sets)
			{
				foreach (ItemDrop.ItemData item in set.GetAllItems())
				{
					if (Sets.IsSupplyRow(item.m_gridPos.y))
					{
						shown = Math.Max(shown, Sets.SupplyIndex(item.m_gridPos) + 1);
					}
				}
			}
		}
		if (SupplyLink.Bar(player) is Inventory bar)
		{
			shown = Math.Max(shown, SupplyLink.Shown(bar));
		}
		return Mathf.Clamp((shown + Sets.Width - 1) / Sets.Width, 1, Sets.SupplyRows);
	}

	/// <summary>Blasse Bilder leerer Plätze (Gorduan, 03.11.2026): Valheims eigene Items, je Platz der Ausrüstung bzw. des Outfits.</summary>
	private static readonly string[] SetGhostItems = { "HelmetLeather", "ArmorLeatherChest", "ArmorLeatherLegs", "CapeDeerHide", "BeltStrength", "", "SwordBronze", "ShieldWood" };
	private static readonly string[] OutfitGhostItems =
	{
		"HelmetLeather", "ArmorLeatherChest", "ArmorLeatherLegs", "CapeDeerHide", "ShieldWood", "BeltStrength",
		"SwordBronze", "AxeBronze", "Club", "KnifeFlint", "SpearFlint", "AtgeirBronze",
		"THSwordKrom", "Battleaxe", "SledgeStagbreaker", "Bow", "CrossbowArbalest", "StaffFireball",
	};
	private const string LeashGhostItem = "GC_Leash_DeerHide";
	private static readonly Color GhostColor = new(0.6f, 0.56f, 0.5f, 0.3f);
	private static readonly System.Collections.Generic.Dictionary<string, Sprite?> ghosts = new();

	/// <summary>Offener Reiter: −1 Stil (Outfit), 0 bis 3 die Sätze. Bleibt zwischen zwei Besuchen am Schrank.</summary>
	internal static int Tab = -1;

	/// <summary>
	/// Verschiebung des Schrankfelds (Gorduan, 04.11.2026, Idee aus EpicLoot 0.15.6, dessen Splitterfeld sich ziehen lässt): am Rahmen
	/// ziehen, Doppelklick legt es wieder rechts neben das Inventar. Nur auf diesem Rechner (keine Server-Regel).
	/// </summary>
	internal static ConfigEntry<float> OffsetX = null!;
	internal static ConfigEntry<float> OffsetY = null!;
	private static Vector2 offset;
	private static bool offsetRead;
	private static Leash.PanelDrag? drag;

	internal static void Init(ConfigFile config)
	{
		OffsetX = Gc.Bind(config, "1 - Outfit", "WindowOffsetX", 0f, "Kleiderschrank-Feld: so weit nach rechts verschoben (Einheiten der Oberfläche). Am Rahmen des Felds ziehen, Doppelklick setzt zurück.");
		OffsetY = Gc.Bind(config, "1 - Outfit", "WindowOffsetY", 0f, "Kleiderschrank-Feld: so weit nach oben verschoben (Einheiten der Oberfläche).");
	}

	private static Vector2 Offset
	{
		get
		{
			if (!offsetRead && OffsetX != null)
			{
				offsetRead = true;
				offset = new Vector2(OffsetX.Value, OffsetY.Value);
			}
			return offset;
		}
	}

	/// <summary>Einmal am Kistenfeld: Rahmen und Ziehen (wirkt nur, solange das Schrankfenster offen ist).</summary>
	private static void EnsureDrag(InventoryGui gui)
	{
		if (drag != null || gui.m_container == null)
		{
			return;
		}
		Leash.PanelDrag.Frame(gui.m_container, 0f);
		drag = gui.m_container.gameObject.AddComponent<Leash.PanelDrag>();
		drag.Allowed = () => Active;
		drag.Moved = delta =>
		{
			_ = Offset;
			offset += delta;
		};
		drag.Done = () =>
		{
			OffsetX.Value = offset.x;
			OffsetY.Value = offset.y;
		};
		drag.Reset = () =>
		{
			offset = Vector2.zero;
			offsetRead = true;
			OffsetX.Value = 0f;
			OffsetY.Value = 0f;
		};
	}

	private static GameObject? tabBar;
	private static Button[]? tabButtons;
	private static TMP_Text[]? tabTexts;
	private static GameObject? setOverlay;
	private static TMP_Text? setHint;
	private static TMP_Text[]? setHeaders;
	private static TMP_Text[]? setNames; // 8 Plätze der Ausrüstung, dann 5 der Leinenleiste
	private static TMP_Text[]? setItems;
	private static TMP_Text[]? barHeaders;
	private static Button? wearButton;
	private static TMP_Text? wearText;
	private static Button? renameButton;
	private static TMP_Text? renameText;
	private static readonly Color Selected = new(1f, 0.82f, 0.45f);

	private static readonly Color NameColor = new(0.93f, 0.89f, 0.80f);
	private static readonly Color ItemColor = new(0.68f, 0.64f, 0.58f);
	private static readonly Color Dim = new(0.48f, 0.46f, 0.43f);
	private static readonly Color WornColor = new(0.56f, 0.82f, 0.56f);

	internal static Wardrobe? Open { get; private set; }

	internal static bool Active => Open != null;

	private static bool panelTouched;
	private static bool takeAllWasActive;
	private static bool stackAllWasActive;
	private static readonly System.Collections.Generic.List<GameObject> otherButtons = new();
	private static string[]? names;
	private static string[]? helps;
	private static string[]? tables;
	private static string empty = "";

	private static GameObject? overlay;
	private static TMP_Text[]? headers;
	private static TMP_Text[]? rowNames;
	private static TMP_Text[]? rowItems;
	private static Image?[]? eyes;
	private static Sprite? eyeOpen;
	private static Sprite? eyeClosed;
	private static bool grown;
	private static Vector2 panelSize;
	private static Vector2 panelPos;
	private static Vector2 gridSize;
	private static Vector2 gridPos;
	private static Vector2 targetSize;

	internal static void Begin(Wardrobe wardrobe)
	{
		InventoryGui? gui = InventoryGui.instance;
		Player? player = Player.m_localPlayer;
		if (gui == null || player == null || Outfits.Of(player) == null)
		{
			return;
		}
		if (Open != null && Open != wardrobe)
		{
			End();
		}
		Open = wardrobe;
		gui.Show(null);
		wardrobe.SetDoors(true);
	}

	internal static void End()
	{
		Wardrobe? wardrobe = Open;
		Open = null;
		if (wardrobe != null)
		{
			wardrobe.SetDoors(false);
		}
		Restore(InventoryGui.instance);
	}

	/// <summary>Jedes Bild (Plugin.Update): Fenster zu oder zu weit weg → Schrank schließen.</summary>
	internal static void Tick()
	{
		if (Open == null)
		{
			if (panelTouched)
			{
				Restore(InventoryGui.instance); // der Schrank verschwand (abgerissen) während das Fenster offen war
			}
			return;
		}
		Player? player = Player.m_localPlayer;
		bool far = player == null || Vector3.Distance(player.transform.position, Open.transform.position) > MaxDistance;
		if (player == null || player.IsDead() || !Open.Valid || !InventoryGui.IsVisible() || far)
		{
			if (far && InventoryGui.IsVisible())
			{
				InventoryGui.instance.Hide();
			}
			End();
		}
	}

	private static void Texts()
	{
		if (names != null)
		{
			return;
		}
		names = new string[Outfits.Slots];
		helps = new string[Outfits.Slots];
		for (int i = 0; i < Outfits.Slots; i++)
		{
			names[i] = GcLoc.Get("gcof_slot_" + i);
			helps[i] = GcLoc.Get(Outfits.IsArmor(i) ? "gcof_help_armor" : "gcof_help_weapon", names[i]);
		}
		tables = new[] { GcLoc.Get("gcof_table_armor"), GcLoc.Get("gcof_table_onehand"), GcLoc.Get("gcof_table_twohand") };
		empty = GcLoc.Get("gcof_empty");
	}

	internal static string SlotName(int slot)
	{
		Texts();
		return slot >= 0 && slot < Outfits.Slots ? names![slot] : "";
	}

	/// <summary>Statt Valheims UpdateContainer, solange das Fenster am Schrank offen ist: das Outfit als Tabellen im Kistenfeld.</summary>
	internal static bool Draw(InventoryGui gui, Player player)
	{
		if (Open == null || gui.m_currentContainer != null || Outfits.Of(player) is not Inventory outfit)
		{
			Restore(gui);
			return true;
		}
		if (!gui.m_animator.GetBool("visible"))
		{
			return true;
		}
		Texts();
		if (!panelTouched)
		{
			panelTouched = true;
			takeAllWasActive = gui.m_takeAllButton.gameObject.activeSelf;
			stackAllWasActive = gui.m_stackAllButton.gameObject.activeSelf;
			// weitere Knöpfe am Kistenfeld (z. B. „Sortieren“ einer anderen Mod): Sortieren würfe Teile in fremde Plätze
			otherButtons.Clear();
			foreach (Button button in gui.m_container.GetComponentsInChildren<Button>())
			{
				GameObject go = button.gameObject;
				if (!button.transform.IsChildOf(gui.m_containerGrid.transform) && go != gui.m_takeAllButton.gameObject && go != gui.m_stackAllButton.gameObject)
				{
					otherButtons.Add(go);
				}
			}
		}
		foreach (GameObject go in otherButtons)
		{
			if (go != null && go.activeSelf)
			{
				go.SetActive(false);
			}
		}
		gui.m_container.gameObject.SetActive(true);
		gui.m_takeAllButton.gameObject.SetActive(false);
		gui.m_stackAllButton.gameObject.SetActive(false);
		Inventory[]? sets = Sets.Of(player);
		if (Tab >= Sets.Count || (Tab >= 0 && sets == null))
		{
			Tab = -1;
		}
		Inventory shown = Tab < 0 ? outfit : Tab == Sets.Worn ? LiveSet.Of(player) : sets![Tab];
		gui.m_containerGrid.UpdateInventory(shown, null, gui.m_dragItem);
		gui.m_containerName.text = Tab < 0 ? GcLoc.Get(Outfits.On ? "gcof_title" : "gcof_title_off") : Sets.Name(Tab);
		gui.m_containerWeight.text = "";
		Layout(gui, outfit, shown);
		return false;
	}

	/// <summary>
	/// Reiter oben, darunter das Outfit als drei Tabellen oder ein Satz als Raster mit Abschnitten; jedes Bild, denn Valheim setzt
	/// die Lage der Plätze nur beim Anlegen. Die Texte liegen im selben Bezugssystem wie die Plätze (Anker der Plätze in Valheims
	/// Rasterwurzel). Das Feld ist für beide Ansichten gleich groß, damit es beim Umschalten nicht springt.
	/// </summary>
	private static void Layout(InventoryGui gui, Inventory outfit, Inventory shown)
	{
		InventoryGrid grid = gui.m_containerGrid;
		if (grid.m_elements.Count == 0)
		{
			return;
		}
		float row = grid.m_elementSpace * RowScale;
		float tableWidth = LabelWidth + row + EyeWidth;
		float outfitWidth = 3 * tableWidth + 2 * TableGap;
		float outfitHeight = HeaderHeight + Outfits.Width * row;
		float setWidth = outfitWidth;
		int supplyRows = SupplyRows(Player.m_localPlayer);
		float setHeight = SetHeaderHeight + HeaderHeight + SetTableRows * row + BarGap + SetBars * (SectionHeight + row) + Mathf.Max(0, supplyRows - 1) * row;
		float contentWidth = Mathf.Max(outfitWidth, setWidth);
		float contentHeight = TabsHeight + Mathf.Max(outfitHeight, setHeight);
		Arrange(gui, contentWidth + 2 * Margin, contentHeight + Margin);
		RectTransform anchor = (RectTransform)grid.m_elements[0].transform;
		if (overlay == null)
		{
			BuildOverlay(grid, anchor, gui.m_containerName);
		}
		if (tabBar == null)
		{
			BuildTabs(gui, grid, anchor);
		}
		float originX = ((RectTransform)grid.transform).rect.width / 2f - contentWidth / 2f;
		LayoutTabs(originX, contentWidth);
		if (!tabBar!.activeSelf)
		{
			tabBar.SetActive(true);
		}
		if (Tab >= 0)
		{
			overlay!.SetActive(false);
			LayoutSet(grid, shown, originX + (contentWidth - setWidth) / 2f, setWidth, tableWidth, row);
		}
		else
		{
			setOverlay!.SetActive(false);
			LayoutOutfit(grid, outfit, originX + (contentWidth - outfitWidth) / 2f, tableWidth, row);
		}
		grid.m_gridRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight);
	}

	/// <summary>Das Outfit als drei Tabellen unter den Reitern.</summary>
	private static void LayoutOutfit(InventoryGrid grid, Inventory outfit, float originX, float tableWidth, float row)
	{
		if (!overlay!.activeSelf)
		{
			overlay.SetActive(true);
		}
		for (int t = 0; t < 3; t++)
		{
			Place(headers![t].rectTransform, originX + t * (tableWidth + TableGap), -TabsHeight, tableWidth, HeaderHeight - 4f);
		}
		foreach (InventoryElement element in grid.m_elements)
		{
			int slot = Outfits.SlotAt(element.Position);
			if (slot < 0)
			{
				continue;
			}
			float x = originX + element.Position.y * (tableWidth + TableGap);
			float y = -(TabsHeight + HeaderHeight + element.Position.x * row);
			RectTransform rt = (RectTransform)element.transform;
			rt.localScale = new Vector3(RowScale, RowScale, 1f);
			Vector2 size = rt.rect.size * RowScale;
			rt.anchoredPosition = new Vector2(x + LabelWidth + rt.pivot.x * size.x, y - (1f - rt.pivot.y) * size.y);
			if (element.transform.Find("binding")?.GetComponent<TMP_Text>() is TMP_Text binding && binding.enabled)
			{
				binding.enabled = false; // die Platznamen stehen jetzt in der Tabelle
			}
			bool hidden = Outfits.IsHidden(slot);
			ItemDrop.ItemData? item = outfit.GetItemAt(element.Position.x, element.Position.y);
			SetText(rowNames![slot], names![slot], hidden ? Dim : NameColor);
			SetText(rowItems![slot], item != null ? Localization.instance.Localize(item.m_shared.m_name) : empty, hidden ? Dim : ItemColor);
			Place(rowNames[slot].rectTransform, x, y - 3f, LabelWidth - 8f, row * 0.5f);
			Place(rowItems[slot].rectTransform, x, y - row * 0.5f, LabelWidth - 8f, row * 0.38f);
			if (eyes![slot] is Image eye)
			{
				eye.sprite = hidden ? eyeClosed : eyeOpen;
				eye.color = hidden ? Dim : NameColor;
				Place(eye.rectTransform, x + LabelWidth + row + 6f, y - (row - 16f) / 2f, 24f, 16f);
			}
			if (element.m_button != null && element.m_button.image != null)
			{
				element.m_button.image.color = hidden ? Dim : Color.white;
			}
			Ghost(element, OutfitGhost(slot));
			if (!element.m_used)
			{
				element.m_tooltip.m_topic = names[slot];
				element.m_tooltip.m_text = helps![slot];
			}
		}
	}

	// ------------------------------------------------------------------ Reiter und Sätze

	/// <summary>Reiter „Stil“ und die vier Sätze, einmal angelegt in Valheims Rasterwurzel (wie die Tabellen).</summary>
	private static void BuildTabs(InventoryGui gui, InventoryGrid grid, RectTransform anchor)
	{
		TMP_Text font = gui.m_containerName;
		Button template = gui.m_takeAllButton;
		tabBar = Root("GC_WardrobeTabs", grid, anchor);
		tabButtons = new Button[Sets.Count + 1];
		tabTexts = new TMP_Text[Sets.Count + 1];
		for (int i = 0; i <= Sets.Count; i++)
		{
			int tab = i - 1;
			tabButtons[i] = MakeButton(tabBar.transform, template, font, () => SelectTab(tab), out tabTexts[i]);
		}
		setOverlay = Root("GC_WardrobeSet", grid, anchor);
		Transform root = setOverlay.transform;
		string[] tableKeys = { "gcof_table_armor", "gcof_set_table_hands", "gcof_set_leashes" };
		setHeaders = new TMP_Text[tableKeys.Length];
		for (int t = 0; t < tableKeys.Length; t++)
		{
			setHeaders[t] = Text(root, font, 19f, TextAlignmentOptions.BottomLeft);
			setHeaders[t].text = GcLoc.Get(tableKeys[t]);
			setHeaders[t].color = font.color;
		}
		setNames = new TMP_Text[Sets.GearSlots + LeashLink.Slots];
		setItems = new TMP_Text[setNames.Length];
		for (int i = 0; i < setNames.Length; i++)
		{
			setNames[i] = Text(root, font, 17f, TextAlignmentOptions.BottomLeft);
			setItems[i] = Text(root, font, 13f, TextAlignmentOptions.TopLeft);
			setNames[i].text = i < Sets.GearSlots ? Sets.SlotName(i) : "Alt+" + (i - Sets.GearSlots + 1);
		}
		// Überschrift der Leiste, dahinter klein, was hinein darf
		string caption = "#" + ColorUtility.ToHtmlStringRGB(ItemColor);
		string[,] barKeys = { { "gcof_set_hotbar", "gcof_set_help_hotbar" }, { "gcof_set_inventory", "gcof_set_help_supplies" } };
		barHeaders = new TMP_Text[2];
		for (int b = 0; b < 2; b++)
		{
			barHeaders[b] = Text(root, font, 19f, TextAlignmentOptions.BottomLeft);
			barHeaders[b].text = GcLoc.Get(barKeys[b, 0]) + "   <size=70%><color=" + caption + ">" + GcLoc.Get(barKeys[b, 1]).TrimEnd('.') + "</color></size>";
			barHeaders[b].color = font.color;
		}
		setHint = Text(root, font, 15f, TextAlignmentOptions.Left);
		setHint.color = ItemColor;
		renameButton = MakeButton(root, template, font, Rename, out renameText);
		renameText.text = GcLoc.Get("gcof_set_rename");
		wearButton = MakeButton(root, template, font, Wear, out wearText);
	}

	private static GameObject Root(string name, InventoryGrid grid, RectTransform anchor)
	{
		GameObject go = new(name, typeof(RectTransform));
		go.transform.SetParent(grid.m_gridRoot, false);
		RectTransform root = (RectTransform)go.transform;
		root.anchorMin = anchor.anchorMin;
		root.anchorMax = anchor.anchorMin;
		root.pivot = new Vector2(0f, 1f);
		root.sizeDelta = Vector2.zero;
		root.anchoredPosition = Vector2.zero;
		return go;
	}

	/// <summary>Knopf im Stil von Valheims „Alles nehmen“ (Bild und Farben übernommen, ohne dessen Klick-Aufruf).</summary>
	private static Button MakeButton(Transform parent, Button template, TMP_Text font, Action onClick, out TMP_Text label)
	{
		GameObject go = new("GC_Button", typeof(RectTransform));
		go.transform.SetParent(parent, false);
		Image image = go.AddComponent<Image>();
		if (template.targetGraphic is Image source)
		{
			image.sprite = source.sprite;
			image.type = source.type;
			image.color = source.color;
			image.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
		}
		Button button = go.AddComponent<Button>();
		button.targetGraphic = image;
		button.transition = template.transition;
		button.colors = template.colors;
		button.spriteState = template.spriteState;
		button.navigation = new Navigation { mode = Navigation.Mode.None };
		button.onClick.AddListener(() => onClick());
		RectTransform rt = (RectTransform)go.transform;
		rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
		rt.pivot = new Vector2(0f, 1f);
		label = Text(go.transform, font, 16f, TextAlignmentOptions.Center);
		RectTransform lt = label.rectTransform;
		lt.anchorMin = Vector2.zero;
		lt.anchorMax = Vector2.one;
		lt.pivot = new Vector2(0.5f, 0.5f);
		lt.offsetMin = new Vector2(4f, 2f);
		lt.offsetMax = new Vector2(-4f, -2f);
		return button;
	}

	private static void LayoutTabs(float originX, float contentWidth)
	{
		int count = Sets.Count + 1;
		float width = (contentWidth - (count - 1) * TabGap) / count;
		for (int i = 0; i < count; i++)
		{
			int tab = i - 1;
			Place((RectTransform)tabButtons![i].transform, originX + i * (width + TabGap), 0f, width, TabsHeight - TabGap);
			string text = tab < 0 ? GcLoc.Get("gcof_tab_style") : Sets.Name(tab);
			SetText(tabTexts![i], text, tab == Tab ? Selected : tab >= 0 && tab == Sets.Worn ? WornColor : ItemColor);
		}
	}

	/// <summary>
	/// Ein Satz (Gorduan, 03.11.2026, Entwurf „wie der Stil-Reiter“): Kopfzeile mit Hinweis und Knöpfen, darunter die Tabellen
	/// Rüstung, Hände und Zubehör, mit GC_Tiere die Leinenleiste (Platzname ausgeschrieben, darunter das Teil, dahinter der
	/// Platz), darunter Schnellleiste und Vorräte als Leisten, acht Plätze je Reihe (Vorräte bis acht Reihen). Leere Plätze zeigen ein
	/// blasses Bild.
	/// </summary>
	private static void LayoutSet(InventoryGrid grid, Inventory set, float originX, float width, float tableWidth, float row)
	{
		if (!setOverlay!.activeSelf)
		{
			setOverlay.SetActive(true);
		}
		bool worn = Tab == Sets.Worn;
		float top = -TabsHeight;
		Place((RectTransform)wearButton!.transform, originX + width - SetButtonWidth, top - 2f, SetButtonWidth, SetHeaderHeight - 8f);
		Place((RectTransform)renameButton!.transform, originX + width - 2 * SetButtonWidth - TabGap, top - 2f, SetButtonWidth, SetHeaderHeight - 8f);
		Place(setHint!.rectTransform, originX, top - 2f, width - 2 * SetButtonWidth - 2 * TabGap, SetHeaderHeight - 8f);
		SetText(wearText!, GcLoc.Get(worn ? "gcof_set_worn" : "gcof_set_wear"), worn ? Dim : NameColor);
		wearButton.interactable = !worn && Sets.On;
		string hint = worn ? GcLoc.Get("gcof_set_hint_worn") : Sets.On ? GcLoc.Get("gcof_set_hint", Sets.Name(Sets.Worn)) : GcLoc.Get("gcof_sets_off");
		SetText(setHint, hint, ItemColor);

		// Tabellen (ohne GC_Tiere zwei, mittig), darunter die zwei Leisten, linksbündig mit der ersten Tabelle
		int tables = SetTables;
		float tablesLeft = originX + (width - (tables * tableWidth + (tables - 1) * TableGap)) / 2f;
		float tablesTop = top - SetHeaderHeight;
		for (int t = 0; t < setHeaders!.Length; t++)
		{
			bool shown = t < tables;
			if (setHeaders[t].gameObject.activeSelf != shown)
			{
				setHeaders[t].gameObject.SetActive(shown);
			}
			Place(setHeaders[t].rectTransform, tablesLeft + t * (tableWidth + TableGap), tablesTop, tableWidth, HeaderHeight - 4f);
		}
		float[] barTop = { tablesTop - HeaderHeight - SetTableRows * row - BarGap, 0f };
		barTop[1] = barTop[0] - SectionHeight - row;
		for (int b = 0; b < barHeaders!.Length; b++)
		{
			bool shown = b < SetBars; // Vorräte nur mit GC_Vorratsleiste
			if (barHeaders[b].gameObject.activeSelf != shown)
			{
				barHeaders[b].gameObject.SetActive(shown);
			}
			Place(barHeaders[b].rectTransform, tablesLeft, barTop[b], width - (tablesLeft - originX), SectionHeight - 2f);
		}
		for (int i = 0; i < setNames!.Length; i++)
		{
			bool leash = i >= Sets.GearSlots;
			bool shown = !leash || LeashLink.Loaded;
			if (setNames[i].gameObject.activeSelf != shown)
			{
				setNames[i].gameObject.SetActive(shown);
				setItems![i].gameObject.SetActive(shown);
			}
		}

		foreach (InventoryElement element in grid.m_elements)
		{
			Vector2i pos = element.Position;
			// die Leinenleiste hat nur fünf Plätze; Vorräte gibt es nur mit GC_Vorratsleiste
			bool slot = (pos.y != Sets.LeashRow || (LeashLink.Loaded && pos.x < LeashLink.Slots))
				&& (!Sets.IsSupplyRow(pos.y) || (SupplyLink.Loaded && (Sets.SupplyIndex(pos) < SupplyLink.Slots || set.GetItemAt(pos.x, pos.y) != null)));
			if (element.gameObject.activeSelf != slot)
			{
				element.gameObject.SetActive(slot);
			}
			if (!slot)
			{
				continue;
			}
			float x;
			float y;
			int line = pos.y == Sets.GearRow ? pos.x : pos.y == Sets.LeashRow ? Sets.GearSlots + pos.x : -1; // Zeile mit Text
			if (line >= 0)
			{
				int table = pos.y == Sets.GearRow ? GearTable[pos.x] : 2;
				int index = pos.y == Sets.GearRow ? GearLine[pos.x] : pos.x;
				float left = tablesLeft + table * (tableWidth + TableGap);
				float rowTop = tablesTop - HeaderHeight - index * row;
				x = left + LabelWidth;
				y = rowTop;
				ItemDrop.ItemData? item = set.GetItemAt(pos.x, pos.y);
				SetText(setNames[line], setNames[line].text, NameColor);
				SetText(setItems![line], item != null ? Localization.instance.Localize(item.m_shared.m_name) : empty, ItemColor);
				Place(setNames[line].rectTransform, left, rowTop - 3f, LabelWidth - 8f, row * 0.5f);
				Place(setItems[line].rectTransform, left, rowTop - row * 0.5f, LabelWidth - 8f, row * 0.38f);
			}
			else
			{
				x = tablesLeft + pos.x * row;
				y = pos.y == Sets.HotbarRow ? barTop[0] - SectionHeight : barTop[1] - SectionHeight - (pos.y - Sets.SupplyRow) * row;
			}
			RectTransform rt = (RectTransform)element.transform;
			rt.localScale = new Vector3(RowScale, RowScale, 1f);
			Vector2 size = rt.rect.size * RowScale;
			rt.anchoredPosition = new Vector2(x + rt.pivot.x * size.x, y - (1f - rt.pivot.y) * size.y);
			string label = pos.y == Sets.HotbarRow ? (pos.x + 1).ToString() : ""; // Zahlentaste wie in Valheims Schnellleiste
			if (element.transform.Find("binding")?.GetComponent<TMP_Text>() is TMP_Text binding)
			{
				binding.enabled = label.Length > 0;
				if (binding.text != label)
				{
					binding.text = label;
				}
			}
			if (element.m_button != null && element.m_button.image != null)
			{
				element.m_button.image.color = Color.white;
			}
			Ghost(element, pos.y == Sets.GearRow ? SetGhost(pos.x) : pos.y == Sets.LeashRow ? ItemGhost(LeashGhostItem, LeashGhostItem, null) : null);
			if (!element.m_used)
			{
				int kind = Sets.Kind(pos.y);
				element.m_tooltip.m_topic = pos.y == Sets.GearRow ? Sets.SlotName(pos.x) : GcLoc.Get(RowTopics[kind]);
				element.m_tooltip.m_text = GcLoc.Get(worn ? LiveHelps[kind] : RowHelps[kind]);
			}
		}
	}

	// ------------------------------------------------------------------ Blasse Bilder leerer Plätze

	private static readonly string[] SetGhostKeys = { "set0", "set1", "set2", "set3", "set4", "set5", "set6", "set7" };

	private static Sprite? SetGhost(int gearSlot) => ghosts.TryGetValue(SetGhostKeys[gearSlot], out Sprite? known) ? known
		: ItemGhost(SetGhostKeys[gearSlot], SetGhostItems[gearSlot], item => Sets.GearSlot(item) == gearSlot);

	internal static Sprite? OutfitGhost(int slot) => slot >= 0 && slot < OutfitGhostItems.Length ? ItemGhost(OutfitGhostItems[slot], OutfitGhostItems[slot], null) : null;

	/// <summary>
	/// Bild eines Valheim-Items (einmal gesucht, unter key gemerkt): das genannte, sonst das erste, das passt (Schmuckstück:
	/// Valheim hat mehrere, welches es gibt, hängt von der Version ab). Ohne ObjectDB noch keins.
	/// </summary>
	private static Sprite? ItemGhost(string key, string prefab, Func<ItemDrop.ItemData, bool>? fits)
	{
		if (ghosts.TryGetValue(key, out Sprite? known))
		{
			return known;
		}
		ObjectDB? db = ObjectDB.instance;
		if (db == null || db.m_items.Count == 0)
		{
			return null;
		}
		ItemDrop? found = prefab.Length > 0 && db.GetItemPrefab(prefab) is GameObject go ? go.GetComponent<ItemDrop>() : null;
		if (found == null && fits != null)
		{
			foreach (GameObject candidate in db.m_items)
			{
				if (candidate != null && candidate.TryGetComponent(out ItemDrop drop) && drop.m_itemData?.m_shared != null && fits(drop.m_itemData))
				{
					found = drop;
					break;
				}
			}
		}
		Sprite? sprite = found != null && found.m_itemData.m_shared.m_icons != null && found.m_itemData.m_shared.m_icons.Length > 0 ? found.m_itemData.GetIcon() : null;
		ghosts[key] = sprite;
		return sprite;
	}

	/// <summary>Blasses Bild hinter Valheims Symbol des Platzes, nur solange der Platz leer ist; null = keins.</summary>
	internal static void Ghost(InventoryElement element, Sprite? sprite)
	{
		bool show = sprite != null && !element.m_used;
		Transform? existing = element.transform.Find("GC_Ghost");
		Image? ghost = existing != null ? existing.GetComponent<Image>() : null;
		if (ghost == null)
		{
			if (!show || element.m_icon == null)
			{
				return;
			}
			GameObject go = new("GC_Ghost", typeof(RectTransform));
			go.transform.SetParent(element.transform, false);
			go.transform.SetSiblingIndex(element.m_icon.transform.GetSiblingIndex());
			ghost = go.AddComponent<Image>();
			ghost.raycastTarget = false;
			ghost.preserveAspect = true;
			ghost.color = GhostColor;
			RectTransform rt = ghost.rectTransform;
			RectTransform icon = element.m_icon.rectTransform;
			rt.anchorMin = icon.anchorMin;
			rt.anchorMax = icon.anchorMax;
			rt.pivot = icon.pivot;
			rt.sizeDelta = icon.sizeDelta;
			rt.anchoredPosition = icon.anchoredPosition;
		}
		if (ghost.enabled != show)
		{
			ghost.enabled = show;
		}
		if (show && ghost.sprite != sprite)
		{
			ghost.sprite = sprite;
		}
	}

	private static void SelectTab(int tab)
	{
		Tab = tab;
		InventoryGui.instance?.m_moveItemEffects.Create(InventoryGui.instance.transform.position, Quaternion.identity);
	}

	private static void Wear()
	{
		Player? player = Player.m_localPlayer;
		if (player == null || Tab < 0)
		{
			return;
		}
		string message = Sets.Wear(player, Tab);
		if (message.Length > 0)
		{
			player.Message(MessageHud.MessageType.Center, message);
		}
	}

	private static void Rename()
	{
		if (Tab < 0 || TextInput.instance == null)
		{
			return;
		}
		TextInput.instance.RequestText(new SetName(Tab), GcLoc.Get("gcof_set_rename_topic", Sets.Name(Tab)), 24);
	}

	/// <summary>Valheims Texteingabe für den Namen eines Satzes.</summary>
	private sealed class SetName : TextReceiver
	{
		private readonly int set;

		internal SetName(int set) => this.set = set;

		public string GetText() => Sets.Name(set);

		public void SetText(string text)
		{
			if (Player.m_localPlayer is Player player)
			{
				Sets.Rename(player, set, text);
			}
		}
	}

	private static void SetText(TMP_Text text, string value, Color color)
	{
		if (text.text != value)
		{
			text.text = value;
		}
		text.color = color;
	}

	/// <summary>Linke obere Ecke an (x, y) im Bezugssystem der Plätze.</summary>
	private static void Place(RectTransform rt, float x, float y, float width, float height)
	{
		rt.sizeDelta = new Vector2(width, height);
		rt.anchoredPosition = new Vector2(x, y);
	}

	/// <summary>Überschriften, Zeilentexte und Augen, einmal angelegt in Valheims Rasterwurzel (am Anker der Plätze).</summary>
	private static void BuildOverlay(InventoryGrid grid, RectTransform element, TMP_Text template)
	{
		overlay = new GameObject("GC_OutfitTables", typeof(RectTransform));
		overlay.transform.SetParent(grid.m_gridRoot, false);
		RectTransform root = (RectTransform)overlay.transform;
		root.anchorMin = element.anchorMin;
		root.anchorMax = element.anchorMin;
		root.pivot = new Vector2(0f, 1f);
		root.sizeDelta = Vector2.zero;
		root.anchoredPosition = Vector2.zero;
		eyeOpen ??= EyeSprite(true);
		eyeClosed ??= EyeSprite(false);
		headers = new TMP_Text[3];
		for (int t = 0; t < 3; t++)
		{
			headers[t] = Text(root, template, 19f, TextAlignmentOptions.BottomLeft);
			headers[t].text = tables![t];
			headers[t].color = template.color;
		}
		rowNames = new TMP_Text[Outfits.Slots];
		rowItems = new TMP_Text[Outfits.Slots];
		eyes = new Image?[Outfits.Slots];
		for (int slot = 0; slot < Outfits.Slots; slot++)
		{
			rowNames[slot] = Text(root, template, 17f, TextAlignmentOptions.BottomLeft);
			rowItems[slot] = Text(root, template, 13f, TextAlignmentOptions.TopLeft);
			eyes[slot] = Eye(root, slot);
		}
	}

	private static TMP_Text Text(Transform parent, TMP_Text template, float size, TextAlignmentOptions alignment)
	{
		GameObject go = new("Text", typeof(RectTransform));
		go.transform.SetParent(parent, false);
		TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
		text.font = template.font;
		text.fontSharedMaterial = template.fontSharedMaterial;
		text.enableAutoSizing = true;
		text.fontSizeMax = size;
		text.fontSizeMin = size * 0.65f;
		text.alignment = alignment;
		text.textWrappingMode = TextWrappingModes.NoWrap;
		text.overflowMode = TextOverflowModes.Ellipsis;
		text.raycastTarget = false;
		RectTransform rt = text.rectTransform;
		rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
		rt.pivot = new Vector2(0f, 1f);
		return text;
	}

	/// <summary>Auge zum Aus- und Einblenden eines Platzes (wie Rechtsklick auf den Platz; Gorduan 31.10.2026: auch bei Waffen).</summary>
	private static Image Eye(Transform parent, int slot)
	{
		GameObject go = new("Eye", typeof(RectTransform));
		go.transform.SetParent(parent, false);
		Image image = go.AddComponent<Image>();
		image.sprite = eyeOpen;
		image.preserveAspect = true;
		Button button = go.AddComponent<Button>();
		button.targetGraphic = image;
		button.onClick.AddListener(() => ToggleHide(Outfits.Pos(slot)));
		RectTransform rt = image.rectTransform;
		rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
		rt.pivot = new Vector2(0f, 1f);
		return image;
	}

	/// <summary>Auge 24 × 16 px: Lidrand und Pupille; geschlossen mit Strich statt Pupille.</summary>
	private static Sprite EyeSprite(bool open)
	{
		const int w = 24;
		const int h = 16;
		Texture2D texture = new(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
		float halfW = w / 2f - 1f;
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				float dx = x + 0.5f - w / 2f;
				float dy = y + 0.5f - h / 2f;
				float lid = (h / 2f - 1.5f) * (1f - dx * dx / (halfW * halfW));
				bool rim = Mathf.Abs(dx) < halfW && Mathf.Abs(Mathf.Abs(dy) - lid) < 1f;
				bool pupil = open && dx * dx + dy * dy < 9f;
				bool slash = !open && Mathf.Abs(dy - dx * h / w) < 1f;
				texture.SetPixel(x, y, rim || pupil || slash ? Color.white : new Color(1f, 1f, 1f, 0f));
			}
		}
		texture.Apply();
		return Sprite.Create(texture, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f);
	}

	/// <summary>
	/// Das Outfit-Feld bekommt die Größe der Tabellen und steht rechts neben dem Inventar samt Rüstungs- und Gewichtskästchen,
	/// unter der Leinenleiste (Gorduan, 31.10.2026: unter einem großen Inventar war kein Platz; die Handwerksleiste sitzt links).
	/// Jedes Bild, denn ValheimPlus setzt die Breite des Kistenfelds bei jedem Aufbau des Rasters zurück
	/// (InventoryGrid_UpdateGui_Patch) und die Leinenleiste wird jedes Bild neu gesetzt. Ragt das Feld rechts oder unten aus
	/// dem Bild, rückt es zurück. Restore stellt alles zurück.
	/// </summary>
	private static void Arrange(InventoryGui gui, float width, float height)
	{
		RectTransform panel = gui.m_container;
		RectTransform grid = (RectTransform)gui.m_containerGrid.transform;
		Vector3[] corners = new Vector3[4];
		if (!grown)
		{
			grown = true;
			panelSize = panel.sizeDelta;
			panelPos = panel.anchoredPosition;
			gridSize = grid.sizeDelta;
			gridPos = grid.anchoredPosition;
			float dw = Mathf.Max(0f, width - grid.rect.width);
			float dh = Mathf.Max(0f, height - grid.rect.height);
			targetSize = panel.sizeDelta + new Vector2(dw, dh);
			// ein nicht mitwachsendes Raster selbst vergrößern, seine linke obere Ecke bleibt im Feld, wo sie war
			panel.GetWorldCorners(corners);
			Vector3 panelTopLeft = corners[1];
			grid.GetWorldCorners(corners);
			Vector3 offset = corners[1] - panelTopLeft;
			grid.sizeDelta += new Vector2(Stretched(grid, 0) ? 0f : dw, Stretched(grid, 1) ? 0f : dh);
			panel.sizeDelta = targetSize;
			panel.GetWorldCorners(corners);
			panelTopLeft = corners[1];
			grid.GetWorldCorners(corners);
			grid.position += panelTopLeft + offset - corners[1];
		}
		panel.sizeDelta = targetSize;
		panel.GetWorldCorners(corners);
		EnsureDrag(gui);
		Vector2 moved = Offset * panel.lossyScale.x;
		Vector3 target = RightOfInventory(gui, panel) + new Vector3(moved.x, moved.y, 0f);
		Vector3 shift = target - corners[1];
		shift.z = 0f;
		Canvas? canvas = panel.GetComponentInParent<Canvas>();
		Vector3[] edges = new Vector3[4];
		if (canvas != null && canvas.rootCanvas.transform is RectTransform screen)
		{
			screen.GetWorldCorners(edges); // die oberste Transform ist bei Valheim nicht der bildschirmgroße Canvas
			float gap = Margin * panel.lossyScale.x;
			float right = corners[2].x + shift.x - (edges[2].x - gap);
			if (right > 0f)
			{
				shift.x -= right;
			}
			float bottom = edges[0].y + gap - (corners[0].y + shift.y);
			if (bottom > 0f)
			{
				shift.y += bottom;
			}
		}
		if (!logged)
		{
			logged = true;
			gui.m_player.GetWorldCorners(edges);
			Outfits.Log.LogInfo($"Outfit-Fenster: Inventar {edges[1]}–{edges[3]}, Ziel {target}, Feld {corners[1]}–{corners[3]} → Verschiebung {shift}, Canvas {(canvas != null ? canvas.rootCanvas.name : "-")}");
		}
		panel.position += shift;
	}

	private static bool logged;

	/// <summary>
	/// Linke obere Ecke rechts neben dem Inventar und seinen Kästchen für Rüstung und Gewicht; unter der Leinenleiste
	/// (GC_LeashBarGrid, Mod Tierleinen), wenn sie dort über die Stelle ragt.
	/// </summary>
	private static Vector3 RightOfInventory(InventoryGui gui, RectTransform panel)
	{
		Vector3[] c = new Vector3[4];
		gui.m_player.GetWorldCorners(c);
		float right = c[2].x;
		float top = c[1].y;
		float gap = Margin * panel.lossyScale.x;
		float inventoryWidth = c[2].x - c[1].x;
		foreach (TMP_Text text in new[] { gui.m_armor, gui.m_weight })
		{
			if (text == null || !text.gameObject.activeInHierarchy)
			{
				continue;
			}
			// das Kästchen um den Text, aber nur wenn es klein ist (sonst wäre es ein ganzes Feld)
			RectTransform box = text.transform.parent is RectTransform parent && parent.rect.width * parent.lossyScale.x < inventoryWidth / 3f ? parent : text.rectTransform;
			box.GetWorldCorners(c);
			if (c[2].x < right + inventoryWidth / 2f)
			{
				right = Mathf.Max(right, c[2].x);
			}
		}
		Transform? bar = gui.m_inventoryRoot.Find("GC_LeashBarGrid");
		if (bar != null && bar.gameObject.activeInHierarchy)
		{
			foreach (InventoryElement element in bar.GetComponentsInChildren<InventoryElement>())
			{
				((RectTransform)element.transform).GetWorldCorners(c);
				if (c[2].x > right)
				{
					top = Mathf.Min(top, c[0].y - gap);
				}
			}
		}
		return new Vector3(right + gap, top, 0f);
	}

	private static bool Stretched(RectTransform rt, int axis) => !Mathf.Approximately(rt.anchorMin[axis], rt.anchorMax[axis]);

	/// <summary>
	/// Kistenfeld wieder wie bei Valheim: Größe, Knöpfe, Lage und Größe der Plätze (Valheims Formel aus InventoryGrid.UpdateGui),
	/// keine Platznamen, keine Tooltips leerer Plätze, keine Färbung, Tabellen aus.
	/// </summary>
	internal static void Restore(InventoryGui? gui)
	{
		if (!panelTouched || gui == null)
		{
			return;
		}
		panelTouched = false;
		gui.m_takeAllButton.gameObject.SetActive(takeAllWasActive);
		gui.m_stackAllButton.gameObject.SetActive(stackAllWasActive);
		foreach (GameObject go in otherButtons)
		{
			if (go != null)
			{
				go.SetActive(true);
			}
		}
		otherButtons.Clear();
		InventoryGrid grid = gui.m_containerGrid;
		if (grown)
		{
			grown = false;
			gui.m_container.sizeDelta = panelSize;
			gui.m_container.anchoredPosition = panelPos;
			((RectTransform)grid.transform).sizeDelta = gridSize;
			((RectTransform)grid.transform).anchoredPosition = gridPos;
		}
		foreach (GameObject? go in new[] { overlay, tabBar, setOverlay })
		{
			if (go != null)
			{
				go.SetActive(false);
			}
		}
		int width = 0;
		foreach (InventoryElement element in grid.m_elements)
		{
			width = Mathf.Max(width, element.Position.x + 1);
		}
		float space = grid.m_elementSpace;
		Vector2 origin = new(((RectTransform)grid.transform).rect.width / 2f - width * space / 2f, 0f);
		foreach (InventoryElement element in grid.m_elements)
		{
			if (!element.gameObject.activeSelf)
			{
				element.gameObject.SetActive(true); // im Satz ausgeblendete Felder neben der Leinenleiste: eine Kiste gleicher Größe nutzt sie
			}
			if (element.transform.Find("GC_Ghost") is Transform ghost)
			{
				UnityEngine.Object.Destroy(ghost.gameObject); // blasse Bilder leerer Plätze gehören nur zum Schrank
			}
			RectTransform rt = (RectTransform)element.transform;
			rt.localScale = Vector3.one;
			rt.anchoredPosition = origin + new Vector2(element.Position.x * space, -element.Position.y * space);
			if (element.transform.Find("binding")?.GetComponent<TMP_Text>() is TMP_Text binding)
			{
				binding.enabled = false; // so legt Valheim Kistenfelder an
			}
			if (element.m_button != null && element.m_button.image != null)
			{
				element.m_button.image.color = Color.white;
			}
			element.m_tooltip.m_topic = "";
			element.m_tooltip.m_text = "";
		}
	}

	/// <summary>Ablegen prüfen (null = erlaubt): ins Outfit nur Passendes, und ein Tausch darf nichts Unpassendes hineinlegen.</summary>
	internal static string? Refusal(Inventory target, Inventory from, ItemDrop.ItemData item, Vector2i pos)
	{
		if (LiveSet.IsLive(target) || LiveSet.IsLive(from))
		{
			return GcLoc.Get("gcof_set_hint_worn"); // der Spiegel nimmt nie etwas an (Klicks gehen ans echte Raster)
		}
		int intoSet = Sets.IndexOf(target);
		int outOfSet = Sets.IndexOf(from);
		if (intoSet >= 0 || outOfSet >= 0)
		{
			ItemDrop.ItemData? other = target.GetItemAt(pos.x, pos.y);
			bool lockedSupply = Sets.IsSupplyRow(pos.y) && Sets.SupplyIndex(pos) >= SupplyLink.Slots;
			if (other == item || (target == from && intoSet >= 0 && pos.y != Sets.GearRow && !lockedSupply && Sets.SameArea(pos.y, item.m_gridPos.y)))
			{
				return null; // innerhalb der Schnellleiste oder der Vorräte eines Satzes frei verschieben
			}
			if (intoSet >= 0 && Sets.Refuse(intoSet, item, pos) is string refused)
			{
				return refused;
			}
			// ein Tausch legt das verdrängte Teil an die alte Stelle des gezogenen
			return outOfSet >= 0 && other != null ? Sets.Refuse(outOfSet, other, item.m_gridPos) : null;
		}
		bool intoOutfit = Outfits.IsOutfit(target);
		bool outOf = Outfits.IsOutfit(from);
		if (!intoOutfit && !outOf)
		{
			return null;
		}
		bool into = intoOutfit;
		ItemDrop.ItemData? swapped = target.GetItemAt(pos.x, pos.y);
		if (swapped == item)
		{
			return null;
		}
		if (into)
		{
			if (!Outfits.On)
			{
				return GcLoc.Get("gcof_off");
			}
			int slot = Outfits.SlotAt(pos);
			if (!Outfits.Fits(slot, item))
			{
				return Outfits.SlotOf(item) < 0 ? GcLoc.Get("gcof_no_slot") : GcLoc.Get("gcof_not_fit", SlotName(slot));
			}
		}
		if (outOf && swapped != null)
		{
			int back = Outfits.SlotAt(item.m_gridPos); // der Tausch legt das verdrängte Teil in diesen Platz des Outfits
			if (!Outfits.On)
			{
				return GcLoc.Get("gcof_off");
			}
			if (!Outfits.Fits(back, swapped))
			{
				return GcLoc.Get("gcof_not_fit", SlotName(back));
			}
		}
		return null;
	}

	/// <summary>Umschalt-Klick: aus dem Inventar in den passenden Platz (ein Teil dort tauscht den Platz), aus dem Outfit ins Inventar.</summary>
	internal static void QuickMove(InventoryGui gui, InventoryGrid grid, ItemDrop.ItemData item)
	{
		Player? player = Player.m_localPlayer;
		if (player == null || Outfits.Of(player) is not Inventory outfit)
		{
			return;
		}
		Inventory main = player.GetInventory();
		Inventory source = grid.GetInventory();
		if (Tab >= 0 && Tab == Sets.Worn)
		{
			// angelegter Satz (live): Umschalt-Klick zieht an oder aus, liegt ja schon bei dir
			if (source == main && item.IsEquipable())
			{
				player.ToggleEquipped(item);
				gui.m_moveItemEffects.Create(gui.transform.position, Quaternion.identity);
			}
			return;
		}
		if (Tab >= 0 && Sets.Of(player) is Inventory[] sets)
		{
			QuickMoveSet(gui, player, main, sets[Tab], source, item);
			return;
		}
		if (source == outfit)
		{
			if (!main.HaveEmptySlot())
			{
				player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcof_no_room"));
				return;
			}
			outfit.RemoveItem(item);
			if (!main.AddItem(item))
			{
				outfit.AddItem(item, item.m_gridPos); // kann nicht passieren (Platz geprüft), aber nichts verlieren
				return;
			}
			gui.m_moveItemEffects.Create(gui.transform.position, Quaternion.identity);
			return;
		}
		if (source != main)
		{
			return;
		}
		if (!Outfits.On)
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcof_off"));
			return;
		}
		int slot = Outfits.SlotOf(item);
		if (slot < 0 || item.m_shared.m_questItem)
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcof_no_slot"));
			return;
		}
		Vector2i from = item.m_gridPos;
		Vector2i to = Outfits.Pos(slot);
		ItemDrop.ItemData? old = outfit.GetItemAt(to.x, to.y);
		player.RemoveEquipAction(item);
		player.UnequipItem(item, false);
		main.RemoveItem(item);
		if (old != null)
		{
			outfit.RemoveItem(old);
			main.AddItem(old, from); // an den Platz, den das neue Teil frei gemacht hat
		}
		outfit.AddItem(item, to);
		gui.m_moveItemEffects.Create(gui.transform.position, Quaternion.identity);
	}

	/// <summary>
	/// Umschalt-Klick im Satz: aus dem Satz ins Inventar; aus dem Inventar in den Satz (Ausrüstung in ihren Platz, wenn er frei
	/// ist, Waffen, Werkzeuge und Vorräte in den ersten freien Platz, an den sie dürfen).
	/// </summary>
	private static void QuickMoveSet(InventoryGui gui, Player player, Inventory main, Inventory set, Inventory source, ItemDrop.ItemData item)
	{
		if (source == set)
		{
			if (!main.HaveEmptySlot())
			{
				player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcof_no_room"));
				return;
			}
			set.RemoveItem(item);
			if (!main.AddItem(item))
			{
				set.AddItem(item, item.m_gridPos);
				return;
			}
			gui.m_moveItemEffects.Create(gui.transform.position, Quaternion.identity);
			return;
		}
		if (source != main)
		{
			return;
		}
		// Leinen und Pfeifen zuerst in die Leinenleiste des Satzes, Ausrüstung in ihren Platz, sonst der erste freie Platz, an den es darf
		Vector2i? to = null;
		int gear = Sets.GearSlot(item);
		bool armour = gear >= 0 && gear < Sets.RightHand;
		bool leash = Sets.Allows(item, Sets.LeashRow);
		if (leash)
		{
			to = FreeIn(set, Sets.LeashRow, LeashLink.Slots);
		}
		else if (gear >= 0 && set.GetItemAt(gear, Sets.GearRow) == null)
		{
			to = new Vector2i(gear, Sets.GearRow);
		}
		// Vorräte zuerst in die Vorräte, die Schnellleiste bleibt frei für Waffen und Werkzeuge
		int[] rows = Sets.IsSupply(item) ? new[] { Sets.SupplyRow, Sets.HotbarRow } : new[] { Sets.HotbarRow, Sets.LeashRow };
		foreach (int y in rows)
		{
			if (to == null && !armour && y < Sets.Height && Sets.Allows(item, y))
			{
				to = y == Sets.SupplyRow ? FreeSupply(set) : FreeIn(set, y, y == Sets.LeashRow ? LeashLink.Slots : Sets.Width);
			}
		}
		if (to is not Vector2i pos)
		{
			// kein freier Platz, an den es darf: erst der Grund (Material, Regel aus …), sonst belegt oder voll
			string? why = Sets.Refuse(Tab, item, armour ? new Vector2i(gear, Sets.GearRow) : new Vector2i(0, Sets.HotbarRow));
			player.Message(MessageHud.MessageType.Center, why ?? (armour ? GcLoc.Get("gcof_set_slot_taken", Sets.SlotName(gear)) : GcLoc.Get("gcof_set_full")));
			return;
		}
		if (Sets.Refuse(Tab, item, pos) is string refusal)
		{
			player.Message(MessageHud.MessageType.Center, refusal);
			return;
		}
		player.RemoveEquipAction(item);
		player.UnequipItem(item, false);
		main.RemoveItem(item);
		set.AddItem(item, pos);
		gui.m_moveItemEffects.Create(gui.transform.position, Quaternion.identity);
	}

	/// <summary>Erster freier geltender Vorratsplatz eines Satzes (Reihe für Reihe, wie in der Vorratsleiste).</summary>
	private static Vector2i? FreeSupply(Inventory set)
	{
		for (int index = 0; index < SupplyLink.Slots; index++)
		{
			Vector2i pos = new(index % Sets.Width, Sets.SupplyRow + index / Sets.Width);
			if (pos.y < set.GetHeight() && set.GetItemAt(pos.x, pos.y) == null)
			{
				return pos;
			}
		}
		return null;
	}

	private static Vector2i? FreeIn(Inventory set, int y, int count)
	{
		for (int x = 0; x < count; x++)
		{
			if (set.GetItemAt(x, y) == null)
			{
				return new Vector2i(x, y);
			}
		}
		return null;
	}

	/// <summary>Rechtsklick auf einen Platz oder Klick aufs Auge: ausblenden oder wieder zeigen.</summary>
	internal static void ToggleHide(Vector2i pos)
	{
		Player? player = Player.m_localPlayer;
		int slot = Outfits.SlotAt(pos);
		if (player == null || slot < 0)
		{
			return;
		}
		Outfits.ToggleHidden(player, slot);
		player.Message(MessageHud.MessageType.TopLeft, GcLoc.Get(Outfits.IsHidden(slot) ? "gcof_hide_on" : "gcof_hide_off", SlotName(slot)));
	}
}

// ---------------------------------------------------------------------- Valheim-Haken

/// <summary>Solange das Fenster am Schrank offen ist, zeigt das Kistenfeld das Outfit (statt Valheims UpdateContainer).</summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateContainer))]
internal static class WardrobeContainerPatch
{
	private static bool Prefix(InventoryGui __instance, Player player)
	{
		try
		{
			return !WardrobeWindow.Active || player == null || WardrobeWindow.Draw(__instance, player);
		}
		catch (Exception e)
		{
			if (!reported)
			{
				reported = true;
				Outfits.Log.LogWarning($"Kleiderschrank: Fenster: {e}");
			}
			return true;
		}
	}

	private static bool reported;
}

/// <summary>Inventar zu: Schrank zu. Öffnet sich eine Kiste, endet das Fenster am Schrank ebenfalls.</summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
internal static class WardrobeHidePatch
{
	private static void Postfix()
	{
		if (WardrobeWindow.Active)
		{
			WardrobeWindow.End();
		}
	}
}

[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
internal static class WardrobeShowPatch
{
	private static void Prefix(Container container)
	{
		if (container != null && WardrobeWindow.Active)
		{
			WardrobeWindow.End();
		}
	}
}

/// <summary>
/// Am Schrank, vor Valheim: Ablegen nach den Regeln des Outfits prüfen (bevor Valheim beide Items ab- und wieder ausrüstet),
/// Umschalt-Klick legt ins Outfit bzw. zurück ins Inventar (Valheim würfe ohne offene Kiste auf den Boden).
/// </summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnSelectedItem))]
internal static class WardrobeSelectPatch
{
	private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, Vector2i pos, InventoryGrid.Modifier mod)
	{
		if (!WardrobeWindow.Active || grid == null || grid.GetInventory() == null)
		{
			return true;
		}
		if (LiveSet.IsLive(grid.GetInventory()))
		{
			LiveSet.Click(__instance, pos, mod); // angelegter Satz: an Valheims echtes Raster weiterreichen
			return false;
		}
		if (__instance.m_dragGo != null)
		{
			if (__instance.m_dragItem == null || __instance.m_dragInventory == null)
			{
				return true;
			}
			string? refusal = WardrobeWindow.Refusal(grid.GetInventory(), __instance.m_dragInventory, __instance.m_dragItem, pos);
			if (refusal == null)
			{
				return true;
			}
			Player.m_localPlayer?.Message(MessageHud.MessageType.Center, refusal);
			return false;
		}
		if (mod == InventoryGrid.Modifier.Move && item != null && (Outfits.IsOutfit(grid.GetInventory()) || Sets.IsSet(grid.GetInventory()) || grid == __instance.m_playerGrid))
		{
			WardrobeWindow.QuickMove(__instance, grid, item);
			return false;
		}
		return true;
	}
}

/// <summary>Ablegen im Raster (auch auf anderen Wegen als dem Klick): dieselben Regeln.</summary>
[HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.DropItem))]
internal static class WardrobeDropPatch
{
	private static bool Prefix(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item, Vector2i pos, ref bool __result)
	{
		string? refusal = __instance.GetInventory() != null && fromInventory != null && item != null
			? WardrobeWindow.Refusal(__instance.GetInventory(), fromInventory, item, pos)
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

/// <summary>Rechtsklick im Outfit: Platz aus- oder einblenden (Valheim würde das Teil sonst benutzen wollen).</summary>
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnRightClickItem))]
internal static class WardrobeRightClickPatch
{
	private static bool Prefix(InventoryGrid grid, Vector2i pos)
	{
		if (!WardrobeWindow.Active || grid == null)
		{
			return true;
		}
		if (LiveSet.IsLive(grid.GetInventory()))
		{
			LiveSet.Use(pos); // das echte Item benutzen, nie die Kopie
			return false;
		}
		if (Sets.IsSet(grid.GetInventory()))
		{
			return false; // im Satz nichts benutzen oder anziehen; angezogen wird beim Anlegen
		}
		if (!Outfits.IsOutfit(grid.GetInventory()))
		{
			return true;
		}
		WardrobeWindow.ToggleHide(pos);
		return false;
	}
}
