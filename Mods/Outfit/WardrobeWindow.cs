using System;
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

	private static readonly Color NameColor = new(0.93f, 0.89f, 0.80f);
	private static readonly Color ItemColor = new(0.68f, 0.64f, 0.58f);
	private static readonly Color Dim = new(0.48f, 0.46f, 0.43f);

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
		gui.m_containerGrid.UpdateInventory(outfit, null, gui.m_dragItem);
		gui.m_containerName.text = GcLoc.Get(Outfits.On ? "gcof_title" : "gcof_title_off");
		gui.m_containerWeight.text = "";
		Layout(gui, outfit);
		return false;
	}

	/// <summary>
	/// Plätze als drei Tabellen anordnen, Zeilentexte und Augen setzen; jedes Bild, denn Valheim setzt die Lage der Plätze nur
	/// beim Anlegen. Die Texte liegen im selben Bezugssystem wie die Plätze (Anker der Plätze in Valheims Rasterwurzel).
	/// </summary>
	private static void Layout(InventoryGui gui, Inventory outfit)
	{
		InventoryGrid grid = gui.m_containerGrid;
		if (grid.m_elements.Count == 0)
		{
			return;
		}
		float row = grid.m_elementSpace * RowScale;
		float tableWidth = LabelWidth + row + EyeWidth;
		float contentWidth = 3 * tableWidth + 2 * TableGap;
		float contentHeight = HeaderHeight + Outfits.Width * row;
		Arrange(gui, contentWidth + 2 * Margin, contentHeight + Margin);
		if (overlay == null)
		{
			BuildOverlay(grid, (RectTransform)grid.m_elements[0].transform, gui.m_containerName);
		}
		if (!overlay!.activeSelf)
		{
			overlay.SetActive(true);
		}
		float originX = ((RectTransform)grid.transform).rect.width / 2f - contentWidth / 2f;
		for (int t = 0; t < 3; t++)
		{
			Place(headers![t].rectTransform, originX + t * (tableWidth + TableGap), 0f, tableWidth, HeaderHeight - 4f);
		}
		foreach (InventoryElement element in grid.m_elements)
		{
			int slot = Outfits.SlotAt(element.Position);
			if (slot < 0)
			{
				continue;
			}
			float x = originX + element.Position.y * (tableWidth + TableGap);
			float y = -(HeaderHeight + element.Position.x * row);
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
			if (!element.m_used)
			{
				element.m_tooltip.m_topic = names[slot];
				element.m_tooltip.m_text = helps![slot];
			}
		}
		grid.m_gridRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight);
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
		Vector3 target = RightOfInventory(gui, panel);
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
		if (overlay != null)
		{
			overlay.SetActive(false);
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
		bool into = Outfits.IsOutfit(target);
		bool outOf = Outfits.IsOutfit(from);
		if (!into && !outOf)
		{
			return null;
		}
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
		if (mod == InventoryGrid.Modifier.Move && item != null && (Outfits.IsOutfit(grid.GetInventory()) || grid == __instance.m_playerGrid))
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
		if (!WardrobeWindow.Active || grid == null || !Outfits.IsOutfit(grid.GetInventory()))
		{
			return true;
		}
		WardrobeWindow.ToggleHide(pos);
		return false;
	}
}
