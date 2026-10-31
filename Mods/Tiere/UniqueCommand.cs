using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace GC.Server.Leash;

internal static partial class LeashMagic
{
	/// <summary>Ein einzigartiges Stück für gc_einzigartig: Grund-Item, aus dem es auch als Beute entstehen kann.</summary>
	internal sealed class UniqueInfo
	{
		public string Id = "";
		public string Kind = "";
		public string Key = "";
		public string Prefab = "";
		public (string Type, float Min, float Max)[] Effects = Array.Empty<(string, float, float)>();
	}

	/// <summary>Alle einzigartigen Leinen, Pfeifen und Sättel (Reihenfolge wie in den Listen).</summary>
	internal static IEnumerable<UniqueInfo> AllUniques()
	{
		foreach ((string id, string kind, string key, (string, float, float)[] list) in Uniques)
		{
			int rank = BossFor.TryGetValue(id, out string boss) && LeashRankAfter.TryGetValue(boss, out int min) ? min : 2;
			yield return new UniqueInfo { Id = id, Kind = kind, Key = key, Prefab = LeashPrefab(rank), Effects = list };
		}
		foreach ((string id, string kind, string key, string boss, string _, (string, float, float)[] list) in MoreUniques)
		{
			yield return new UniqueInfo { Id = id, Kind = kind, Key = key, Prefab = LeashPrefab(LeashRankAfter.TryGetValue(boss, out int min) ? min : 2), Effects = list };
		}
		foreach ((string id, string kind, string key, string boss, (string, float, float)[] list) in WhistleUniques.Concat(MoreWhistleUniques))
		{
			yield return new UniqueInfo { Id = id, Kind = kind, Key = key, Prefab = WhistlePrefab(WhistleLevelAfter.TryGetValue(boss, out int min) ? min : 2), Effects = list };
		}
		foreach ((string id, string kind, string key, string _, string saddle, (string, float, float)[] list) in SaddleUniques)
		{
			yield return new UniqueInfo { Id = id, Kind = kind, Key = key, Prefab = saddle.Length > 0 ? saddle : "SaddleLox", Effects = list };
		}
	}

	private static string LeashPrefab(int rank) => (Plugin.Tiers.FirstOrDefault(t => t.Rank == rank) ?? Plugin.Tiers[Plugin.Tiers.Length - 1]).PrefabName;

	private static string WhistlePrefab(int level) => (Whistles.Tiers.FirstOrDefault(t => t.Level == level) ?? Whistles.Tiers[Whistles.Tiers.Length - 1]).PrefabName;

	/// <summary>
	/// Stück über EpicLoot.API.CreateMagicItem anlegen (EpicLoot 0.15.4): Seltenheit des Stücks, seine festen Effekte, Werte
	/// würfelt EpicLoot im Bereich des Stücks. Null und error, wenn es nicht geht.
	/// </summary>
	internal static ItemDrop.ItemData? CreateUnique(UniqueInfo unique, int quality, out string error)
	{
		error = "";
		MethodInfo? create = Available && api != null ? api.GetMethod("CreateMagicItem", BindingFlags.Public | BindingFlags.Static) : null;
		if (create == null)
		{
			error = "EpicLoot 0.15.4 oder neuer fehlt (EpicLoot.API.CreateMagicItem).";
			return null;
		}
		int rarity = unique.Kind switch { "Mythic" => 4, "Ancient" => 5, _ => 3 };
		StringBuilder json = new();
		json.Append("{\"Prefab\":\"").Append(unique.Prefab).Append("\",\"Rarity\":").Append(rarity)
			.Append(",\"LegendaryID\":\"").Append(unique.Id).Append("\",\"Effects\":[");
		for (int i = 0; i < unique.Effects.Length; i++)
		{
			json.Append(i > 0 ? "," : "").Append("{\"EffectType\":\"").Append(unique.Effects[i].Type).Append("\"}");
		}
		json.Append("]}");
		object?[] args = { json.ToString(), "" };
		if (create.Invoke(null, args) is not ItemDrop.ItemData item)
		{
			error = args[1] as string ?? "unbekannter Fehler";
			return null;
		}
		item.m_quality = Mathf.Clamp(quality, 1, Math.Max(1, item.m_shared.m_maxQuality));
		item.m_durability = item.GetMaxDurability();
		return item;
	}
}

/// <summary>
/// Zum Testen (nur mit devcommands, Gorduan 30.10.2026): ein einzigartiges Stück mit seinen festen Effekten ins Inventar, z. B.
/// „gc_einzigartig GC_Gleipnir“ oder kurz „gc_einzigartig gleipnir 4“ (Qualität 4). Ohne Angabe: Liste aller Stücke.
/// Grund-Item ist die Leine, Pfeife oder der Sattel, aus dem das Stück auch als Beute entstehen kann.
/// </summary>
internal sealed class UniqueCommand : Jotunn.Entities.ConsoleCommand
{
	public override string Name => "gc_einzigartig";

	public override string Help => "GC_Server: einzigartige Leine, Pfeife oder Sattel ins Inventar (Test), ohne Angabe Liste aller Stücke";

	public override bool IsCheat => true;

	public override List<string> CommandOptionList() => LeashMagic.AllUniques().Select(u => u.Id).ToList();

	public override void Run(string[] args)
	{
		global::Console console = global::Console.instance;
		Player? player = Player.m_localPlayer;
		if (args.Length == 0)
		{
			foreach (LeashMagic.UniqueInfo info in LeashMagic.AllUniques())
			{
				console.Print($"{info.Id} ({info.Kind}, {Localize("$gc_leg_" + info.Key)}) auf {info.Prefab}");
			}
			console.Print("Aufruf: gc_einzigartig <ID oder Name> [Qualität 1–4]");
			return;
		}
		string wanted = args[0].Trim().ToLowerInvariant();
		LeashMagic.UniqueInfo? unique = LeashMagic.AllUniques().FirstOrDefault(u => u.Id.ToLowerInvariant() == wanted || u.Key == wanted
			|| u.Id.ToLowerInvariant() == "gc_" + wanted);
		if (unique == null)
		{
			console.Print($"gc_einzigartig: „{args[0]}“ gibt es nicht. Ohne Angabe zeigt der Befehl alle Stücke.");
			return;
		}
		if (player == null)
		{
			console.Print("gc_einzigartig: Erst in die Welt.");
			return;
		}
		int quality = args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int q) ? q : 1;
		ItemDrop.ItemData? item = LeashMagic.CreateUnique(unique, quality, out string error);
		if (item == null)
		{
			console.Print($"gc_einzigartig: {unique.Id} nicht angelegt: {error}");
			return;
		}
		if (!player.GetInventory().AddItem(item))
		{
			Transform t = player.transform;
			ItemDrop.DropItem(item, item.m_stack, t.position + t.forward + Vector3.up, t.rotation);
			console.Print($"gc_einzigartig: Inventar voll, {unique.Id} liegt vor dir.");
			return;
		}
		console.Print($"gc_einzigartig: {Localize("$gc_leg_" + unique.Key)} ({unique.Kind}, Qualität {item.m_quality}) liegt im Inventar.");
	}

	private static string Localize(string text) => Localization.instance != null ? Localization.instance.Localize(text) : text;
}
