using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Server.Supplies;

/// <summary>
/// Vorräte auffüllen und einlagern, mit Knöpfen an der Vorratsleiste (Gorduan, 04.11.2026: „die Buttons zum Auffüllen, Einlagern etc.
/// sollten auch eher an der Leiste sein und nicht an der Kiste. Auch muss hier die ValheimPlus-Funktion des Einziehens und
/// Einlagerns aus Truhen in Reichweite funktionieren“). Kisten in Reichweite sucht ValheimPlus selbst
/// (InventoryAssistant.GetNearbyChests, mit dessen Prüfungen: Zugriff, Ward, vom Spieler gebaut; GC_Schloesser nimmt mit Alt+L
/// gesperrte Kisten heraus):
///  * Auffüllen zieht wie V+ „Craft from Chest“ ([CraftFromChest] enabled, range, Karren und Schiffe): Die Stapel der Leiste
///    werden aus der offenen Kiste, dann aus den Kisten ringsum aufgefüllt, die nächste zuerst. Leere Plätze füllt nur die offene
///    Kiste (aus den Kisten ringsum wüsste niemand, was hinein soll).
///  * Einlagern legt wie V+ „Stapel ablegen“ ([AutoStack] enabled, autoStackAllRange): erst auf Stapel der offenen Kiste, dann in
///    Kisten ringsum, die dieses Stück schon haben (wie V+: nur dorthin, wo es schon liegt), der Rest in freie Plätze der offenen
///    Kiste. V+s ignoreFood/ignoreAmmo gelten für das Inventar, nicht für die Leiste (sonst ginge hier gar nichts).
/// Schlösser (Gorduan, 04.11.2026): Fixierte Items bleiben, wo sie sind, in der Leiste wie in den Kisten; auf rot fixierte Stapel
/// kommt nichts dazu (wie GC_Schloesser es Valheim vorgibt).
/// Ein Stück bleibt (Gorduan, 05.11.2026: „1 sollte immer in einer Truhe bleiben, für das Sortieren“, Server-Regel KeepOneInChest):
/// Auffüllen nimmt von jeder Sorte höchstens alles bis auf ein Stück aus jeder Kiste, auch aus der offenen; ein fixiertes
/// Stück der Sorte zählt als dieses eine.
/// Mehrspieler: Ändern darf eine Kiste nur ihr Besitzer. Wie V+ wird darum jede Kiste erst angefragt (Valheims RPC_RequestStack,
/// dieselbe Anfrage wie „Stapel ablegen“): Ihr Besitzer gibt sie ab, wenn sie niemand offen hat. Umgeräumt wird, wenn alle
/// geantwortet haben und uns gehören (höchstens zweimal V+ replyTimeout). Ohne ValheimPlus oder mit abgeschalteter Funktion
/// zählt nur die offene Kiste.
/// </summary>
internal static class SupplyChests
{
	private static MethodInfo? nearby;
	private static PropertyInfo? configCurrent;
	private static bool resolved;

	private static bool running;
	private static float runningUntil;
	private static int sweep;
	private static readonly HashSet<Container> Pending = new();
	private static readonly HashSet<Container> Granted = new();
	private static readonly HashSet<Container> Asked = new();
	private static float askedUntil;
	private static int refused;

	/// <summary>Eine Anfrage läuft (die Knöpfe sind so lange aus; endete der Charakter mittendrin, gibt die Frist sie wieder frei).</summary>
	internal static bool Busy => running && Time.time < runningUntil;

	private static void Resolve()
	{
		if (resolved)
		{
			return;
		}
		resolved = true;
		if (!Gc.VPlusLoaded)
		{
			return;
		}
		Type? assistant = AccessTools.TypeByName("ValheimPlus.InventoryAssistant");
		nearby = assistant?.GetMethods(BindingFlags.Public | BindingFlags.Static)
			.FirstOrDefault(m => m.Name == "GetNearbyChests" && m.GetParameters().Length >= 3 && m.GetParameters()[0].ParameterType == typeof(GameObject));
		configCurrent = AccessTools.Property(AccessTools.TypeByName("ValheimPlus.Configurations.Configuration"), "Current");
		if (nearby == null || configCurrent == null)
		{
			SupplyBar.Log.LogWarning("Vorratsleiste: Kistensuche von ValheimPlus nicht gefunden, Auffüllen und Einlagern nutzen nur die offene Kiste.");
		}
	}

	/// <summary>Ein Abschnitt der V+-Einstellungen (CraftFromChest, AutoStack), null ohne V+.</summary>
	private static object? Section(string name)
	{
		Resolve();
		try
		{
			object? current = configCurrent?.GetValue(null);
			return current == null ? null : AccessTools.Property(current.GetType(), name)?.GetValue(current);
		}
		catch
		{
			return null;
		}
	}

	private static T Read<T>(object section, string name, T fallback) => AccessTools.Property(section.GetType(), name)?.GetValue(section) is T value ? value : fallback;

	/// <summary>
	/// V+-Regeln für diese Richtung: Auffüllen wie „Craft from Chest“, Einlagern wie „Auto Stack“. false = V+ fehlt oder die
	/// Funktion ist aus, dann zählt nur die offene Kiste.
	/// </summary>
	internal static bool Rules(bool fill, out float range, out bool checkWard, out bool vehicles)
	{
		range = fill ? 20f : 10f;
		checkWard = true;
		vehicles = fill;
		object? section = Section(fill ? "CraftFromChest" : "AutoStack");
		if (section == null || nearby == null)
		{
			return false;
		}
		try
		{
			range = Mathf.Clamp(Read(section, fill ? "range" : "autoStackAllRange", range), 1f, 50f);
			checkWard = !Read(section, fill ? "ignorePrivateAreaCheck" : "autoStackAllIgnorePrivateAreaCheck", false);
			return Read(section, "IsEnabled", false);
		}
		catch
		{
			return false;
		}
	}

	/// <summary>Wartezeit auf Antworten (V+ [AutoStack] replyTimeout, 0,25–5 s).</summary>
	private static float Timeout()
	{
		object? section = Section("AutoStack");
		try
		{
			return section != null ? Mathf.Clamp(Read(section, "replyTimeout", 1f), 0.25f, 5f) : 1f;
		}
		catch
		{
			return 1f;
		}
	}

	/// <summary>Für den Tooltip der Knöpfe: Reichweite, oder dass nur die offene Kiste zählt.</summary>
	internal static string Reach(bool fill) => Rules(fill, out float range, out _, out _)
		? GcLoc.Get(fill ? "gcvl_reach_fill" : "gcvl_reach_store", range.ToString("0"))
		: GcLoc.Get(fill ? "gcvl_reach_fill_off" : "gcvl_reach_store_off");

	// ------------------------------------------------------------------ Ablauf

	/// <summary>Knopf gedrückt: Kisten in Reichweite anfragen, umgeräumt wird, sobald sie uns gehören.</summary>
	internal static void Start(bool fill)
	{
		Player? player = Player.m_localPlayer;
		if (Busy || player == null || player.IsDead() || SupplyBar.Of(player) is not Inventory bar || (fill && !SupplyBar.On))
		{
			return;
		}
		InventoryGui? gui = InventoryGui.instance;
		if (gui != null && gui.m_dragGo != null)
		{
			gui.SetupDragItem(null, null, 1);
		}
		Container? open = OpenChest(gui);
		List<Container> chests = new();
		if (Rules(fill, out float range, out bool checkWard, out bool vehicles))
		{
			chests = Candidates(player, bar, open, fill, range, checkWard, vehicles);
		}
		sweep++;
		running = true;
		refused = 0;
		Pending.Clear();
		Granted.Clear();
		Asked.Clear();
		float timeout = Timeout();
		runningUntil = Time.time + 2f * timeout + 1f;
		askedUntil = Time.time + 3f * timeout + 5f;
		foreach (Container chest in chests)
		{
			if (Owned(chest))
			{
				continue;
			}
			Pending.Add(chest);
			Asked.Add(chest);
			try
			{
				chest.StackAll(); // Anfrage beim Besitzer; die Antwort fängt ReplyPatch ab
			}
			catch (Exception e)
			{
				Pending.Remove(chest);
				Asked.Remove(chest);
				refused++;
				SupplyBar.Log.LogWarning($"Vorratsleiste: Kiste {chest.name} nicht angefragt: {e.Message}");
			}
		}
		player.StartCoroutine(Run(sweep, player, fill, open, chests, timeout));
	}

	private static IEnumerator Run(int id, Player player, bool fill, Container? open, List<Container> chests, float timeout)
	{
		float until = Time.time + timeout;
		while (Pending.Count > 0 && Time.time < until)
		{
			yield return null;
		}
		refused += Pending.Count; // keine Antwort: diese Kisten bleiben außen vor (eine späte Antwort fängt ReplyPatch noch ab)
		Pending.Clear();
		until = Time.time + timeout;
		while (Time.time < until && Granted.Any(chest => !Owned(chest)))
		{
			yield return null; // der Besitzwechsel kommt mit den Weltdaten, kurz nach der Antwort
		}
		if (id != sweep)
		{
			yield break;
		}
		running = false;
		if (player == null || player != Player.m_localPlayer || player.IsDead() || SupplyBar.Of(player) is not Inventory bar)
		{
			yield break;
		}
		List<Container> owned = chests.Where(chest => chest != null && Owned(chest)).ToList();
		refused += Granted.Count(chest => !Owned(chest)); // zugesagt, aber nie unsere geworden: dort kommt nichts hin
		Granted.Clear();
		if (open != null && (!Owned(open) || !Gc.LinkedViewHealthy()))
		{
			open = null; // die offene Kiste gehört uns nicht mehr (Fenster zu, jemand anderes hat sie)
		}
		try
		{
			if (fill)
			{
				Fill(player, bar, open, owned);
			}
			else
			{
				Store(player, bar, open, owned);
			}
		}
		catch (Exception e)
		{
			SupplyBar.Log.LogWarning($"Vorratsleiste: {(fill ? "Auffüllen" : "Einlagern")} abgebrochen: {e}");
		}
	}

	/// <summary>Antwort eines Besitzers auf unsere Anfrage (true = verbraucht, Valheim und V+ sollen damit nichts tun).</summary>
	internal static bool Reply(Container chest, bool granted)
	{
		if (Time.time > askedUntil || !Asked.Remove(chest))
		{
			return false;
		}
		if (Pending.Remove(chest))
		{
			if (granted)
			{
				Granted.Add(chest);
			}
			else
			{
				refused++; // jemand hat sie offen oder sie ist nicht unsere
			}
		}
		return true; // auch eine späte Antwort: sonst legte Valheim das Inventar dorthin ab
	}

	private static bool Owned(Container? chest) => chest != null && chest.m_nview != null && chest.m_nview.IsValid() && chest.m_nview.IsOwner();

	/// <summary>Die offene Kiste, wenn sie uns gehört (nicht der Kleiderschrank, der ist keine Kiste).</summary>
	private static Container? OpenChest(InventoryGui? gui)
	{
		Container? chest = gui != null && gui.IsContainerOpen() ? gui.m_currentContainer : null;
		return chest != null && Owned(chest) && Gc.LinkedViewHealthy() ? chest : null;
	}

	/// <summary>Kisten ringsum (V+s Suche, nächste zuerst), in denen es für diese Richtung etwas zu tun gibt.</summary>
	private static List<Container> Candidates(Player player, Inventory bar, Container? open, bool fill, float range, bool checkWard, bool vehicles)
	{
		List<Container> found;
		try
		{
			ParameterInfo[] parameters = nearby!.GetParameters();
			object?[] args = new object?[parameters.Length];
			for (int i = 0; i < parameters.Length; i++)
			{
				args[i] = i == 0 ? player.gameObject : i == 1 ? range : i == 2 ? checkWard : i == 3 ? vehicles : parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
			}
			found = nearby.Invoke(null, args) as List<Container> ?? new List<Container>();
		}
		catch (Exception e)
		{
			SupplyBar.Log.LogWarning($"Vorratsleiste: Kistensuche von ValheimPlus fehlgeschlagen: {e.Message}");
			return new List<Container>();
		}
		List<ItemDrop.ItemData> wanted = new();
		foreach (ItemDrop.ItemData item in bar.GetAllItems())
		{
			if (fill ? SupplyBar.Index(item.m_gridPos) < SupplyBar.Count && TakesNew(item) && item.m_stack < item.m_shared.m_maxStackSize : !Gc.IsLocked(item))
			{
				wanted.Add(item);
			}
		}
		List<Container> result = new();
		if (wanted.Count == 0)
		{
			return result;
		}
		foreach (Container chest in found)
		{
			if (chest == null || chest == open || result.Contains(chest) || chest.m_nview == null || !chest.m_nview.IsValid())
			{
				continue;
			}
			try
			{
				chest.Load();
			}
			catch
			{
				continue;
			}
			Inventory? inventory = chest.GetInventory();
			if (inventory == null)
			{
				continue;
			}
			bool useful = fill
				? inventory.GetAllItems().Any(item => !Gc.IsLocked(item) && wanted.Any(have => have.IsSameType(item)) && Spare(inventory, item.m_shared.m_name) > 0)
				: wanted.Any(item => inventory.GetAllItems().Any(stack => stack.m_shared.m_name == item.m_shared.m_name));
			if (useful)
			{
				result.Add(chest);
			}
		}
		return result;
	}

	/// <summary>
	/// Wie viel von dieser Sorte aus der Kiste darf: alles Freie, mit der Regel KeepOneInChest bis auf ein Stück (ein fixiertes
	/// Stück der Sorte zählt als dieses).
	/// </summary>
	private static int Spare(Inventory chest, string name)
	{
		int free = 0;
		bool locked = false;
		foreach (ItemDrop.ItemData item in chest.GetAllItems())
		{
			if (item.m_shared.m_name != name)
			{
				continue;
			}
			if (Gc.IsLocked(item))
			{
				locked = true;
			}
			else
			{
				free += item.m_stack;
			}
		}
		return SupplyBar.KeepOne.Value && !locked ? Math.Max(0, free - 1) : free;
	}

	/// <summary>Darf auf diesen Stapel noch etwas dazu? Rot fixiert nicht (Vorgabe von GC_Schloesser), weiß fixiert und frei ja.</summary>
	private static bool TakesNew(ItemDrop.ItemData stack) => !Gc.IsHardLocked(stack) && stack.m_shared.m_maxStackSize > 1;

	private static int Amount(Inventory inventory) => inventory.GetAllItems().Sum(item => item.m_stack);

	// ------------------------------------------------------------------ Umräumen

	/// <summary>Stapel der Leiste aus der offenen Kiste und den Kisten ringsum auffüllen, leere Plätze aus der offenen Kiste.</summary>
	private static void Fill(Player player, Inventory bar, Container? open, List<Container> chests)
	{
		int before = Amount(bar);
		List<(Container Chest, Inventory Items)> sources = new();
		if (open != null)
		{
			sources.Add((open, open.GetInventory()));
		}
		foreach (Container chest in chests)
		{
			chest.Load();
			sources.Add((chest, chest.GetInventory()));
		}
		HashSet<Container> used = new();
		foreach (ItemDrop.ItemData have in new List<ItemDrop.ItemData>(bar.GetAllItems()))
		{
			if (SupplyBar.Index(have.m_gridPos) >= SupplyBar.Count || !TakesNew(have))
			{
				continue;
			}
			foreach ((Container chest, Inventory from) in sources)
			{
				foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(from.GetAllItems()))
				{
					int room = have.m_shared.m_maxStackSize - have.m_stack;
					if (room <= 0)
					{
						break;
					}
					int take = Math.Min(Math.Min(room, item.m_stack), Spare(from, item.m_shared.m_name));
					if (!Gc.IsLocked(item) && have.IsSameType(item) && take > 0)
					{
						bar.MoveItemToThis(from, item, take, have.m_gridPos.x, have.m_gridPos.y);
						used.Add(chest);
					}
				}
			}
		}
		if (open != null)
		{
			Inventory from = open.GetInventory();
			for (int index = 0; index < SupplyBar.Count; index++)
			{
				Vector2i pos = SupplyBar.Pos(index);
				if (bar.GetItemAt(pos.x, pos.y) != null)
				{
					continue;
				}
				ItemDrop.ItemData? item = from.GetAllItems().FirstOrDefault(i => SupplyBar.Fits(i) && !Gc.IsLocked(i) && Spare(from, i.m_shared.m_name) > 0);
				if (item == null)
				{
					break;
				}
				bar.MoveItemToThis(from, item, Math.Min(item.m_stack, Spare(from, item.m_shared.m_name)), pos.x, pos.y);
				used.Add(open);
			}
		}
		Report(player, Amount(bar) - before, used, "gcvl_filled");
	}

	/// <summary>Aus der Leiste: auf Stapel der offenen Kiste, in Kisten ringsum, die das Stück schon haben, der Rest in die offene Kiste.</summary>
	private static void Store(Player player, Inventory bar, Container? open, List<Container> chests)
	{
		int before = Amount(bar);
		HashSet<Container> used = new();
		foreach (Container chest in chests)
		{
			chest.Load();
		}
		foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(bar.GetAllItems()))
		{
			if (Gc.IsLocked(item))
			{
				continue; // fixierte Items (GC_Schloesser) bleiben in der Leiste
			}
			if (item.m_equipped)
			{
				player.RemoveEquipAction(item);
				player.UnequipItem(item, false);
			}
			if (open != null)
			{
				TopUp(open, open.GetInventory(), bar, item, used);
			}
			foreach (Container chest in chests)
			{
				Inventory to = chest.GetInventory();
				if (!bar.ContainsItem(item) || !to.GetAllItems().Any(stack => stack.m_shared.m_name == item.m_shared.m_name))
				{
					continue; // wie V+: nur in Kisten, in denen dieses Stück schon liegt
				}
				TopUp(chest, to, bar, item, used);
				IntoFree(chest, to, bar, item, used);
			}
			if (open != null)
			{
				IntoFree(open, open.GetInventory(), bar, item, used);
			}
		}
		Report(player, before - Amount(bar), used, "gcvl_stored");
	}

	/// <summary>Auf gleiche Stapel (nicht rot fixiert) legen, so viel draufpasst.</summary>
	private static void TopUp(Container chest, Inventory to, Inventory bar, ItemDrop.ItemData item, HashSet<Container> used)
	{
		foreach (ItemDrop.ItemData stack in new List<ItemDrop.ItemData>(to.GetAllItems()))
		{
			if (!bar.ContainsItem(item) || item.m_stack <= 0)
			{
				return;
			}
			int room = stack.m_shared.m_maxStackSize - stack.m_stack;
			if (room > 0 && TakesNew(stack) && stack.IsSameType(item))
			{
				to.MoveItemToThis(bar, item, Math.Min(room, item.m_stack), stack.m_gridPos.x, stack.m_gridPos.y);
				used.Add(chest);
			}
		}
	}

	/// <summary>Den Rest in einen freien Platz.</summary>
	private static void IntoFree(Container chest, Inventory to, Inventory bar, ItemDrop.ItemData item, HashSet<Container> used)
	{
		if (!bar.ContainsItem(item) || item.m_stack <= 0)
		{
			return;
		}
		Vector2i free = to.FindEmptySlot(false);
		if (free.x < 0)
		{
			return; // Kiste voll (Valheims AddItem meldete sonst „Trying to add item to occupied slot -1, -1“)
		}
		to.MoveItemToThis(bar, item, item.m_stack, free.x, free.y);
		used.Add(chest);
	}

	private static void Report(Player player, int moved, HashSet<Container> used, string key)
	{
		string text = moved > 0 ? GcLoc.Get(key, moved.ToString(), used.Count.ToString()) : GcLoc.Get("gcvl_nothing");
		if (refused > 0)
		{
			text += " " + GcLoc.Get("gcvl_refused", refused.ToString());
		}
		player.Message(MessageHud.MessageType.Center, text);
		if (moved > 0)
		{
			InventoryGui.instance?.m_moveItemEffects.Create(player.transform.position, Quaternion.identity);
		}
	}
}

/// <summary>Antwort auf unsere Anfrage an eine Kiste ringsum: abfangen, sonst legte Valheim das ganze Inventar dort ab.</summary>
[HarmonyPatch(typeof(Container), nameof(Container.RPC_StackResponse))]
internal static class SupplyChestReplyPatch
{
	[HarmonyPriority(Priority.First)]
	private static bool Prefix(Container __instance, bool granted) => !SupplyChests.Reply(__instance, granted);
}
