using System.Collections.Generic;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Jungtiere an der Leine (Gorduan, 15.10.2026): Ferkel, Welpe, Lox-Kalb, Küken, Asksvin-Junges und Elchkalb haben in Valheim
/// keine Zähm-Komponente (nur Growup), lassen sich also weder streicheln noch anleinen.
///  * Beim Start bekommen alle Vorlagen mit Growup und Character, aber ohne Tameable, eine. Valheims Tameable prüft überall,
///    ob es eine MonsterAI gibt; Befehle (Folgen/Bleiben) bleiben aus.
///  * Jungtiere mit der einfachen Tier-KI (AnimalAI) folgen von selbst niemandem: An der Leine übernimmt dieselbe
///    Folge-Logik wie bei den großen Tieren (LeashFollowPatch: Mindestlänge, freies Umherlaufen, Zögern, „hingehen“).
///  * Wächst ein Jungtier an der Leine oder am Pfosten auf, hängt die Leine danach am ausgewachsenen Tier, mit Namen
///    (CopyState beim Entstehen des großen Tiers, Relink danach).
///  * Leinenlast: die Hälfte ihres Tiers (Ranks).
/// </summary>
internal static class Young
{
	/// <summary>Bei jedem Laden der Vorlagen (ZNetScene): Jungtieren eine Zähm-Komponente geben (nur einmal je Vorlage).</summary>
	internal static void AddTameable()
	{
		if (ZNetScene.instance == null)
		{
			return;
		}
		List<string> names = new();
		foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
		{
			if (prefab != null && prefab.GetComponent<Growup>() != null && prefab.GetComponent<Character>() != null && prefab.GetComponent<Tameable>() == null)
			{
				prefab.AddComponent<Tameable>();
				names.Add(prefab.name);
			}
		}
		if (names.Count > 0)
		{
			Plugin.Log.LogInfo("Tierleinen: Jungtiere lassen sich anleinen und streicheln: " + string.Join(", ", names));
		}
	}

	/// <summary>Kann das Tier angreifen? Jungtiere mit Tier-KI greifen nie an, andere nur mit Angriffen.</summary>
	internal static bool CanAttack(Link link) => link.Ai != null && link.Animal is Humanoid humanoid && humanoid.m_defaultItems != null && humanoid.m_defaultItems.Length > 0;

	/// <summary>
	/// Das große Tier entsteht (Growup, im Awake): Name, Fütterer, „entwischt“ und eine Anbindung am Pfosten vom Jungtier
	/// übernehmen. Das Jungtier ist zu diesem Zeitpunkt noch da.
	/// </summary>
	internal static void CopyState(Character young, Character adult)
	{
		ZNetView? from = young.m_nview;
		ZNetView? to = adult.m_nview;
		if (from == null || !from.IsValid() || to == null || !to.IsValid())
		{
			return;
		}
		ZDO a = from.GetZDO();
		ZDO b = to.GetZDO();
		string name = a.GetString(ZDOVars.s_tamedName);
		if (name.Length > 0)
		{
			b.Set(ZDOVars.s_tamedName, name);
			string author = a.GetString(ZDOVars.s_tamedNameAuthor);
			if (author.Length > 0)
			{
				b.Set(ZDOVars.s_tamedNameAuthor, author);
			}
		}
		long feeder = a.GetLong(Care.FeederHash, 0L);
		if (feeder != 0L)
		{
			b.Set(Care.FeederHash, feeder);
			b.Set(Care.FeederSkillHash, a.GetFloat(Care.FeederSkillHash, -1f)); // Stufe aus dem Futter gehört zum Fütterer
		}
		if (a.GetBool(Plugin.EscapedHash))
		{
			b.Set(Plugin.EscapedHash, true);
		}
		if (Posts.IsTied(a))
		{
			b.Set(Plugin.TiedHash, 1);
			b.Set(Plugin.LeashIdHash, a.GetString(Plugin.LeashIdHash));
			b.Set(Plugin.PostHash, a.GetVec3(Plugin.PostHash, adult.transform.position));
			b.Set(Plugin.TierHash, a.GetInt(Plugin.TierHash));
			b.Set(Plugin.MinLengthHash, a.GetFloat(Plugin.MinLengthHash, 0f));
			b.Set(Plugin.MaxLengthHash, a.GetFloat(Plugin.MaxLengthHash, 0f));
			b.Set(Plugin.BonusHash, a.GetFloat(Plugin.BonusHash, 0f));
		}
	}

	/// <summary>
	/// Nach dem Aufwachsen (oder der Verwandlung eines Bären in einen Unbjørn): Hing das alte Tier an einer eigenen Leine,
	/// hängt jetzt das neue daran. message: Text-Schlüssel der Meldung oben links.
	/// </summary>
	internal static void Relink(Character? young, Character adult, string message = "gcls_grown")
	{
		if (young == null || Links.Active.Count == 0)
		{
			return;
		}
		Link? link = Links.ForAnimal(young);
		Tameable? tame = adult.GetComponent<Tameable>();
		if (link == null || link.Holder == null || tame == null)
		{
			return;
		}
		Links.Forget(link);
		Link? moved = Links.Attach(link.Holder, tame, link.Item, link.Tier, link.PrevFollow, link.PrevPatrol);
		if (moved != null && link.Holder == Player.m_localPlayer)
		{
			Gc.TopLeft(GcLoc.Get(message, tame.GetHoverName()));
		}
	}
}

/// <summary>Jungtiere mit der einfachen Tier-KI (AnimalAI) folgen an der Leine wie die großen Tiere (LeashFollowPatch).</summary>
[HarmonyPatch(typeof(AnimalAI), nameof(AnimalAI.UpdateAI))]
internal static class LeashAnimalAIPatch
{
	private static void Postfix(AnimalAI __instance, float dt, bool __result)
	{
		if (!__result || Links.Active.Count == 0 || __instance.m_character == null)
		{
			return;
		}
		Link? link = Links.ForAnimal(__instance.m_character);
		if (link == null || link.Traveling || link.Holder == null)
		{
			return;
		}
		__instance.Follow(link.Holder.gameObject, dt); // ersetzt Umherlaufen und Flucht dieses Bildes
	}
}

/// <summary>Jungtiere ohne MonsterAI können nicht fressen: im Hinweis nicht „hungrig“, sondern „glücklich“.</summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.GetStatusString))]
internal static class YoungStatusPatch
{
	private static void Postfix(Tameable __instance, ref string __result)
	{
		if (__instance.m_monsterAI == null && __instance.m_character != null && __instance.m_character.IsTamed())
		{
			__result = "$hud_tamehappy";
		}
	}
}
