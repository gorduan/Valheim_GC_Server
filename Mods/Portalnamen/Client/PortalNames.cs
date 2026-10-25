using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Client;
using GC.Shared;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GC.Client.PortalNames;

/// <summary>
/// Portalnamen-Liste (neu in GC_Client 1.1.0): Beim Umbenennen eines Portals erscheint neben dem Eingabefeld eine
/// Liste gemerkter Namen. Klick auf einen Namen benennt das Portal sofort um (einstellbar: nur ins Feld eintragen).
/// „+ merken" übernimmt den Namen aus dem Eingabefeld, „×" löscht einen Eintrag. Leere Namen sind erlaubt.
///
/// Eigene Liste: Datei BepInEx/config/gorduan.gc_client.portalnames.txt (eine Zeile pro Name), nur bei dir.
/// Weitere Listen kommen von anderen GC-Mods, wenn sie installiert sind (über <see cref="GcPortalNames"/>):
/// GC_Server bringt die Server-Liste mit, GC_Guilds die Liste deiner Gilde. Ohne diese Mods gibt es nur die eigene Liste.
/// </summary>
internal static class Plugin
{
	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<bool> ClickRenames = null!;
	internal static bool On => Enabled.Value && ClientPlugin.Allowed;

	private static readonly List<string> Own = new();
	private static int ownVersion;

	private static string FilePath => Path.Combine(Paths.ConfigPath, "gorduan.gc_client.portalnames.txt");

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ClientPlugin.Log;
		Enabled = Gc.Bind(Config, "10 - PortalNames", "Enabled", true, "Liste gemerkter Portalnamen neben dem Umbenennen-Dialog.");
		ClickRenames = Gc.Bind(Config, "10 - PortalNames", "ClickRenames", true, "Klick auf einen Namen benennt das Portal sofort um. Aus = der Name wird nur ins Eingabefeld geschrieben.");
		LoadOwn();
		GcPortalNames.Register("own", 0, () => GcLoc.Get("gcpn_own"), () => true, () => new List<string>(Own), () => true, AddOwn, RemoveOwn, () => { }, () => ownVersion);
	}

	internal static void Update()
	{
		PortalNameWindow.Tick();
	}

	private static void LoadOwn()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				Own.Clear();
				Own.AddRange(GcPortalNames.Normalize(File.ReadAllLines(FilePath, Encoding.UTF8)));
			}
		}
		catch (Exception e)
		{
			Log.LogWarning($"Portalnamen-Liste konnte nicht gelesen werden: {e.Message}");
		}
	}

	private static void SaveOwn()
	{
		try
		{
			File.WriteAllLines(FilePath, Own, Encoding.UTF8);
		}
		catch (Exception e)
		{
			Log.LogWarning($"Portalnamen-Liste konnte nicht gespeichert werden: {e.Message}");
		}
		ownVersion++;
	}

	private static void AddOwn(string name)
	{
		string clean = GcPortalNames.Clean(name);
		if (Own.Contains(clean))
		{
			return;
		}
		Own.Add(clean);
		List<string> sorted = GcPortalNames.Normalize(Own);
		Own.Clear();
		Own.AddRange(sorted);
		SaveOwn();
	}

	private static void RemoveOwn(string name)
	{
		if (Own.Remove(name))
		{
			SaveOwn();
		}
	}
}

/// <summary>Fenster neben dem Vanilla-Dialog „Portal benennen" (TextInput mit einem TeleportWorld als Empfänger).</summary>
internal static class PortalNameWindow
{
	private static GameObject? panel;
	private static RectTransform? content;
	private static TeleportWorld? openedFor;
	private static string signature = "";

	internal static void Tick()
	{
		TextInput? input = TextInput.instance;
		bool show = Plugin.On && !Gc.IsHeadless && input != null && input.m_panel != null && input.m_panel.activeSelf && input.m_queuedSign is TeleportWorld;
		if (!show)
		{
			if (panel != null && panel.activeSelf)
			{
				panel.SetActive(false);
			}
			openedFor = null;
			return;
		}
		if (panel == null && !Build())
		{
			return;
		}
		TeleportWorld portal = (TeleportWorld)input!.m_queuedSign;
		if (!panel!.activeSelf)
		{
			panel.SetActive(true);
			panel.transform.SetAsLastSibling();
		}
		if (openedFor != portal)
		{
			openedFor = portal;
			foreach (Dictionary<string, object> provider in Providers())
			{
				((Action)provider["refresh"])(); // Server- und Gildenlisten frisch anfragen
			}
			signature = "";
		}
		string current = Signature();
		if (current != signature)
		{
			signature = current;
			Fill();
		}
	}

	private static List<Dictionary<string, object>> Providers()
	{
		List<object> list = GcPortalNames.Providers();
		lock (list)
		{
			return list.OfType<Dictionary<string, object>>().OrderBy(p => (int)p["order"]).ToList();
		}
	}

	private static string Signature()
	{
		StringBuilder sb = new();
		sb.Append(Previous(out _)).Append('|');
		foreach (Dictionary<string, object> provider in Providers())
		{
			sb.Append(provider["id"]).Append(':').Append(((Func<bool>)provider["available"])()).Append(((Func<bool>)provider["canEdit"])()).Append(((Func<int>)provider["version"])()).Append('|');
		}
		return sb.ToString();
	}

	private static bool Build()
	{
		if (GUIManager.Instance == null || GUIManager.CustomGUIFront == null)
		{
			return false;
		}
		GUIManager gui = GUIManager.Instance;
		panel = gui.CreateWoodpanel(GUIManager.CustomGUIFront.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-230f, 0f), 380f, 600f, false);
		panel.name = "GC_PortalNames"; // am rechten Rand, damit es den mittigen Vanilla-Dialog nicht verdeckt
		Text title = GcUi.CreateLabel(panel.transform, GcLoc.Get("gcpn_title"), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(20f, -62f), new Vector2(-20f, -18f), 26, TextAnchor.MiddleCenter, gui.ValheimOrange, true);
		title.raycastTarget = false;
		GameObject innerGo = new("Inner", typeof(RectTransform));
		innerGo.transform.SetParent(panel.transform, false);
		RectTransform inner = (RectTransform)innerGo.transform;
		inner.anchorMin = Vector2.zero;
		inner.anchorMax = Vector2.one;
		inner.offsetMin = new Vector2(18f, 22f);
		inner.offsetMax = new Vector2(-18f, -66f);
		content = GcUi.CreateScroll(inner, 330f, 500f);
		panel.SetActive(false);
		return true;
	}

	private static void Fill()
	{
		if (content == null)
		{
			return;
		}
		GcUi.ClearChildren(content);
		string previous = Previous(out bool hasPrevious);
		if (hasPrevious)
		{
			// Vorheriger Name (GC_PortalGuard): hier immer erreichbar, auch wenn Umschalt+E am Portal eine andere Mod nutzt
			Label(GcLoc.Get("gcpn_previous"), 21, new Color(1f, 0.75f, 0.4f), 32f);
			RectTransform row = GcUi.CreateRow(content!, 36f);
			Button back = GcUi.CreateButton(row, "« " + previous, new Vector2(0f, 0.5f), new Vector2(145f, 0f), 286f, 32f, () => Apply(previous));
			back.GetComponentInChildren<Text>(true).alignment = TextAnchor.MiddleCenter;
		}
		foreach (Dictionary<string, object> provider in Providers())
		{
			if (!((Func<bool>)provider["available"])())
			{
				continue;
			}
			bool canEdit = ((Func<bool>)provider["canEdit"])();
			List<string> names = ((Func<List<string>>)provider["names"])() ?? new List<string>();
			Label(((Func<string>)provider["title"])() + (canEdit ? "" : " " + GcLoc.Get("gcpn_readonly")), 21, new Color(1f, 0.75f, 0.4f), 32f);
			if (names.Count == 0)
			{
				Label(GcLoc.Get("gcpn_none"), 15, new Color(0.78f, 0.72f, 0.62f), 26f);
			}
			foreach (string name in names)
			{
				EntryRow(provider, name, canEdit);
			}
			if (canEdit)
			{
				Action<string> add = (Action<string>)provider["add"];
				ButtonRow(GcLoc.Get("gcpn_add"), () =>
				{
					TextInput? input = TextInput.instance;
					if (input != null && input.m_inputField != null)
					{
						add(GcPortalNames.Clean(input.m_inputField.text));
					}
					Refocus();
				});
			}
		}
	}

	/// <summary>Vorheriger Name des Portals im Dialog, wenn GC_PortalGuard ihn kennt und er sich vom aktuellen unterscheidet.</summary>
	private static string Previous(out bool has)
	{
		has = false;
		TextInput? input = TextInput.instance;
		if (!GcHooks.IsPortalGuardActive() || input == null || input.m_queuedSign is not TeleportWorld portal || portal.m_nview == null || !portal.m_nview.IsValid())
		{
			return "";
		}
		ZDO zdo = portal.m_nview.GetZDO();
		string previous = zdo.GetString(GcKeys.PortalPrevious) ?? "";
		has = previous.Length > 0 && previous != (zdo.GetString(ZDOVars.s_tag) ?? ""); // roh mit roh vergleichen, ohne jedes Bild den Wortfilter
		return previous;
	}

	private static void Label(string text, int size, Color color, float height)
	{
		RectTransform row = GcUi.CreateRow(content!, height);
		Text label = GcUi.CreateLabel(row, text, Vector2.zero, Vector2.one, new Vector2(4f, 0f), Vector2.zero, size, TextAnchor.LowerLeft, color);
		label.raycastTarget = false;
	}

	private static void EntryRow(Dictionary<string, object> provider, string name, bool canEdit)
	{
		RectTransform row = GcUi.CreateRow(content!, 36f);
		string shown = name.Length == 0 ? GcLoc.Get("gcpn_empty") : name;
		Button pick = GcUi.CreateButton(row, shown, new Vector2(0f, 0.5f), new Vector2(122f, 0f), 240f, 32f, () => Apply(name));
		pick.GetComponentInChildren<Text>(true).alignment = TextAnchor.MiddleCenter;
		if (canEdit)
		{
			Action<string> remove = (Action<string>)provider["remove"];
			GcUi.CreateButton(row, "×", new Vector2(1f, 0.5f), new Vector2(-24f, 0f), 36f, 32f, () =>
			{
				remove(name);
				Refocus();
			});
		}
	}

	private static void ButtonRow(string text, Action onClick)
	{
		RectTransform row = GcUi.CreateRow(content!, 40f);
		GcUi.CreateButton(row, text, new Vector2(0.5f, 0.5f), Vector2.zero, 290f, 34f, onClick);
	}

	/// <summary>Fokus zurück ins Eingabefeld des Vanilla-Dialogs, sonst würde Enter den zuletzt geklickten Knopf erneut auslösen.</summary>
	private static void Refocus()
	{
		TextInput? input = TextInput.instance;
		if (input == null || input.m_inputField == null)
		{
			return;
		}
		EventSystem.current?.SetSelectedGameObject(input.m_inputField.gameObject);
		input.m_inputField.ActivateInputField();
	}

	/// <summary>Name übernehmen: sofort umbenennen (wie mit Enter bestätigt, ohne Rückfrage) oder nur ins Feld schreiben.</summary>
	private static void Apply(string name)
	{
		TextInput? input = TextInput.instance;
		if (input == null || input.m_queuedSign is not TeleportWorld portal)
		{
			return;
		}
		if (!Plugin.ClickRenames.Value)
		{
			input.m_inputField.text = name;
			Refocus();
			return;
		}
		input.m_queuedSign = null;
		input.Hide();
		if ((portal.GetText() ?? "") == name)
		{
			return; // gleicher Name: nichts tun, damit die Verbindung bleibt
		}
		if (GcHooks.IsPortalGuardActive() && GcHooks.PortalRename != null)
		{
			GcHooks.PortalRename(portal, name); // Klick in der Liste ist eine bewusste Wahl: keine zweite Rückfrage
		}
		else
		{
			portal.SetText(name);
		}
		Gc.TopLeft(GcLoc.Get("gcpn_renamed", name.Length == 0 ? GcLoc.Get("gcpn_empty") : name));
	}
}
