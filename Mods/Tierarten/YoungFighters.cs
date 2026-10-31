using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using GC.Shared;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Jungtiere kämpfen mit (Gorduan, 27.10.2026: „Jungtiere können auch Gegner angreifen, sie sind etwa ein Viertel so stark“).
/// Gilt für Ferkel, Wolfswelpe, Loxkalb, Asksvin-Junges, Elchkalb und das Bärenjunge; Küken nicht (Hühner kämpfen nicht).
///
/// In Valheim haben die Jungtiere die Flucht-KI (AnimalAI) und keine Angriffe (nur das Elchkalb trägt Horn und Huf, nutzt sie
/// aber nie; das Asksvin-Junge hat Kampf-KI ohne Angriffe). Beim Start bekommen ihre Vorlagen deshalb:
///  * die Kampf-KI des erwachsenen Tiers (MonsterAI-Werte vom Erwachsenen, Bewegung und Wahrnehmung vom Jungtier selbst,
///    kein Fressen: Jungtiere bleiben aus der Fütterung heraus; das Asksvin-Junge behält seine eigene KI samt Futter),
///  * die Angriffe des Erwachsenen (Schaden beim Treffer auf DamagePercent gesenkt, Standard 25 %; das Bärenjunge hatte
///    bisher fest ein Drittel),
///  * beim Welpen den Animator des Wolfs (gleiches Skelett; sein eigener kennt keine Angriffe).
/// Geprüft an den Vorlagen (Review 27.10.2026): Alle fünf sind Humanoid mit CharacterAnimEvent; Ferkel, Elchkalb und
/// Asksvin-Junges nutzen den Animator des Erwachsenen, das Loxkalb einen eigenen mit denselben Angriffs-Auslösern.
/// Damit greifen sie wie zahme Erwachsene an, auch auf Befehl von Leine und Pfeife (GC_Tiere erkennt Kampf-KI mit Angriffen).
/// Nur zahme Jungtiere kämpfen, nie gegen Spieler oder zahme Tiere (Valheims Feindschaft der Zahmen). Ist „Jungtiere
/// kämpfen“ aus (vom Server vorgegeben), suchen sie sich keine Gegner und lassen jedes Ziel fallen, auch einen Befehl.
/// </summary>
internal static class YoungFighters
{
	private static readonly (string Young, string Adult)[] Pairs =
	{
		("Boar_piggy", "Boar"), ("Wolf_cub", "Wolf"), ("Lox_Calf", "Lox"), ("Asksvin_hatchling", "Asksvin"), ("Moose_calf", "Moose"),
	};

	/// <summary>Gorduan: ein Viertel. Gilt auch, solange Init nicht gelaufen ist (die Patches sind dann schon eingespielt).</summary>
	private const float DefaultPercent = 25f;

	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<float> DamagePercent = null!;

	/// <summary>
	/// Vorlagen der kämpfenden Jungtiere (mit dem Bärenjungen) als Prefab-Hash wie im ZDO, für Schaden und Zielsuche (Review
	/// 27.10.2026: vorher der Objektname, je Treffer und Zielsuche ein neuer Text).
	/// </summary>
	private static readonly HashSet<int> Fighters = new() { Species.CubPrefab.GetStableHashCode() };
	/// <summary>Jungtiere, die hier ihre Kampf-KI bekamen (ohne Futter): Zahm sind sie nie hungrig.</summary>
	private static readonly HashSet<int> Converted = new();
	/// <summary>Jungtier → Befehlbarkeit des Erwachsenen (Folgen/Bleiben wie beim großen Tier).</summary>
	private static readonly Dictionary<int, bool> Commandable = new();

	internal static void Init(ConfigFile config)
	{
		const string section = "14 - Animal Species";
		Enabled = Gc.BindSynced(config, section, "YoungFight", true, "Zahme Jungtiere (Ferkel, Welpe, Loxkalb, Asksvin-Junges, Elchkalb, Bärenjunges) greifen Gegner an wie zahme erwachsene Tiere, auch auf Befehl von Leine und Pfeife; nie Spieler oder zahme Tiere.");
		DamagePercent = Gc.BindSynced(config, section, "YoungDamagePercent", DefaultPercent, "Schaden der Jungtiere in Prozent des erwachsenen Tiers gleicher Stufe (Gorduan: ein Viertel). Gilt auch für das Bärenjunge.", new AcceptableValueRange<float>(0f, 100f));
		PrefabManager.OnVanillaPrefabsAvailable += Prepare;
		Young.FightAllowed = animal => Fighting || !IsYoung(animal); // Leine und Pfeife: kein Angriffsbefehl, wenn Jungtiere nicht kämpfen
	}

	/// <summary>Schadensanteil der Jungtiere (0–1); vor Init der Standard.</summary>
	internal static float Share => Mathf.Clamp01((DamagePercent != null ? DamagePercent.Value : DefaultPercent) / 100f);

	/// <summary>Dürfen Jungtiere gerade kämpfen (Einstellung und Stufe des Servers)?</summary>
	internal static bool Fighting => Enabled != null && Enabled.Value && ServerPlugin.Allowed;

	/// <summary>Ist Init gelaufen? Vorher lassen die Patches alles wie in Valheim (Review 27.10.2026).</summary>
	internal static bool Ready => Enabled != null;

	internal static bool IsYoung(Character? character) => character != null && Fighters.Contains(PrefabOf(character.m_nview));

	/// <summary>Zahmes Jungtier mit neuer Kampf-KI: frisst nicht, ist also nie hungrig.</summary>
	internal static bool NeverHungry(Tameable tame)
	{
		Character? character = tame.m_character;
		return Converted.Count > 0 && character != null && Converted.Contains(PrefabOf(tame.m_nview)) && character.IsTamed();
	}

	private static int PrefabOf(ZNetView? view)
	{
		ZDO? zdo = view != null && view.IsValid() ? view.GetZDO() : null;
		return zdo != null ? zdo.GetPrefab() : 0;
	}

	/// <summary>Einmal, wenn Valheims Vorlagen da sind: Kampf-KI, Angriffe und (beim Welpen) Animator der Erwachsenen.</summary>
	private static void Prepare()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= Prepare;
		List<string> done = new();
		foreach ((string youngName, string adultName) in Pairs)
		{
			try
			{
				if (PrepareOne(youngName, adultName))
				{
					done.Add(youngName);
				}
			}
			catch (Exception e)
			{
				ServerPlugin.Log.LogWarning($"Tierarten: {youngName} kämpft nicht, Vorbereitung fehlgeschlagen: {e}");
			}
		}
		ServerPlugin.Log.LogInfo($"Tierarten: Jungtiere kämpfen mit ({string.Join(", ", done)}, dazu das Bärenjunge).");
	}

	private static bool PrepareOne(string youngName, string adultName)
	{
		GameObject? young = PrefabManager.Instance.GetPrefab(youngName);
		GameObject? adult = PrefabManager.Instance.GetPrefab(adultName);
		Humanoid? youngBody = young != null ? young.GetComponent<Humanoid>() : null;
		Humanoid? adultBody = adult != null ? adult.GetComponent<Humanoid>() : null;
		MonsterAI? adultAi = adult != null ? adult.GetComponent<MonsterAI>() : null;
		if (young == null || adult == null || youngBody == null || adultBody == null || adultAi == null || adultBody.m_defaultItems == null || adultBody.m_defaultItems.Length == 0)
		{
			ServerPlugin.Log.LogWarning($"Tierarten: {youngName} oder {adultName} nicht wie erwartet, das Jungtier kämpft nicht.");
			return false;
		}
		bool converted = young.GetComponent<MonsterAI>() == null;
		if (!GiveFightingAi(young, adultAi))
		{
			ServerPlugin.Log.LogWarning($"Tierarten: Die Flucht-KI von {youngName} ließ sich nicht ersetzen, das Jungtier kämpft nicht.");
			return false;
		}
		youngBody.m_defaultItems = adultBody.m_defaultItems.ToArray();
		MatchAnimator(young, adult);
		int hash = youngName.GetStableHashCode();
		Tameable? adultTame = adult.GetComponent<Tameable>();
		Commandable[hash] = adultTame != null && adultTame.m_commandable;
		Fighters.Add(hash);
		if (converted)
		{
			Converted.Add(hash);
		}
		return true;
	}

	/// <summary>
	/// Flucht-KI gegen Kampf-KI tauschen: Felder der MonsterAI vom Erwachsenen, die von BaseAI (Bewegung, Wahrnehmung, Pfad,
	/// Töne) vom Jungtier. Ein Jungtier, das schon Kampf-KI hat (Asksvin), behält sie samt Futter (es frisst in Valheim
	/// Moltebeeren, Gerste und Flachs). False: Die alte KI ließ sich nicht entfernen, das Jungtier bleibt wie in Valheim.
	/// </summary>
	private static bool GiveFightingAi(GameObject young, MonsterAI adultAi)
	{
		if (young.GetComponent<MonsterAI>() != null)
		{
			return true;
		}
		AnimalAI? animal = young.GetComponent<AnimalAI>();
		MonsterAI ai = young.AddComponent<MonsterAI>();
		CopySerialized(typeof(MonsterAI), adultAi, ai);
		CopySerialized(typeof(BaseAI), animal != null ? animal : adultAi, ai);
		ai.m_consumeItems = new List<ItemDrop>(); // Jungtiere fressen nicht (mit der Tier-KI können sie es in Valheim nicht)
		if (animal != null)
		{
			// Valheims Vorlage stammt aus einem Asset-Bundle: DestroyImmediate ohne „true“ darf Unity dort ablehnen
			// („Destroying assets is not permitted“), dann hätte das Jungtier zwei KIs (Review 27.10.2026)
			UnityEngine.Object.DestroyImmediate(animal, true);
		}
		if (young.GetComponent<AnimalAI>() != null)
		{
			UnityEngine.Object.DestroyImmediate(ai, true); // lieber die alte Flucht-KI als zwei KIs nebeneinander
			return false;
		}
		return true;
	}

	/// <summary>Welpe: sein Animator kennt keine Angriffe → den des Erwachsenen nehmen (gleiches Skelett).</summary>
	private static void MatchAnimator(GameObject young, GameObject adult)
	{
		Animator? youngAnimator = young.GetComponentInChildren<Animator>(true);
		Animator? adultAnimator = adult.GetComponentInChildren<Animator>(true);
		RuntimeAnimatorController? own = youngAnimator != null ? youngAnimator.runtimeAnimatorController : null;
		if (youngAnimator == null || adultAnimator == null || own == null || adultAnimator.runtimeAnimatorController == null || own == adultAnimator.runtimeAnimatorController)
		{
			return;
		}
		if (!own.animationClips.Any(clip => clip != null && clip.name.IndexOf("attack", StringComparison.OrdinalIgnoreCase) >= 0))
		{
			youngAnimator.runtimeAnimatorController = adultAnimator.runtimeAnimatorController;
		}
	}

	/// <summary>
	/// Felder, die type selbst deklariert und Unity speichert (öffentlich oder [SerializeField]), von from nach to. Ohne
	/// Ereignisse (Delegates wie m_onConsumedItem) und Schreibgeschütztes: Das speichert Unity nicht (Review 27.10.2026).
	/// </summary>
	private static void CopySerialized(Type type, object from, object to)
	{
		foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
		{
			if (field.IsNotSerialized || field.IsLiteral || field.IsInitOnly || typeof(Delegate).IsAssignableFrom(field.FieldType)
				|| (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null))
			{
				continue;
			}
			field.SetValue(to, field.GetValue(from));
		}
	}

	/// <summary>Befehle (Folgen/Bleiben) wie beim erwachsenen Tier.</summary>
	internal static void CopyCommandable(Tameable tameable)
	{
		if (Commandable.Count > 0 && Commandable.TryGetValue(PrefabOf(tameable.m_nview), out bool commandable))
		{
			tameable.m_commandable = commandable;
		}
	}
}

/// <summary>Treffer eines Jungtiers: nur DamagePercent des Schadens (beim Angreifer, bevor der Treffer verschickt wird).</summary>
[HarmonyPatch(typeof(Character), nameof(Character.Damage))]
internal static class YoungDamagePatch
{
	private static void Prefix(HitData hit)
	{
		if (hit == null || hit.m_attacker.IsNone())
		{
			return;
		}
		if (YoungFighters.IsYoung(hit.GetAttacker()))
		{
			hit.ApplyModifier(YoungFighters.Share);
		}
	}
}

/// <summary>
/// Zielsuche: Nur zahme Jungtiere kämpfen, und nur mit „Jungtiere kämpfen“ (Valheims eigener Schalter m_aiCannotTargetOthers).
/// Sonst kein altes Ziel und keine Unruhe; der Postfix läuft nach dem der Leinen und verwirft dann auch einen Befehl
/// „angreifen“. Vor Init wie in Valheim (Review 27.10.2026: sonst NullReferenceException beim Bärenjungen).
/// </summary>
[HarmonyPatch(typeof(MonsterAI), "UpdateTarget")]
internal static class YoungTargetPatch
{
	private static void Prefix(MonsterAI __instance, Humanoid humanoid, out bool __state)
	{
		__state = false;
		if (!YoungFighters.Ready || !YoungFighters.IsYoung(humanoid))
		{
			return;
		}
		bool fighting = YoungFighters.Fighting && humanoid.IsTamed();
		humanoid.m_aiCannotTargetOthers = !fighting;
		if (!fighting)
		{
			__state = true;
			Calm(__instance);
		}
	}

	[HarmonyPriority(Priority.Last)]
	private static void Postfix(MonsterAI __instance, bool __state, ref bool canHearTarget, ref bool canSeeTarget)
	{
		if (__state && (__instance.m_targetCreature != null || __instance.m_targetStatic != null))
		{
			Calm(__instance);
			canHearTarget = false;
			canSeeTarget = false;
		}
	}

	private static void Calm(MonsterAI ai)
	{
		if (ai.m_targetCreature != null || ai.m_targetStatic != null)
		{
			ai.m_targetCreature = null;
			ai.m_targetStatic = null;
			ai.SetTargetInfo(ZDOID.None);
		}
		if (ai.IsAlerted())
		{
			ai.SetAlerted(false); // Valheims Aufgeben in UpdateTarget läuft ohne Zielsuche nicht mehr
		}
	}
}

/// <summary>Jungtiere lassen sich befehlen wie ihr erwachsenes Tier (Tameable kommt von GC_Tiere, ohne Befehle).</summary>
[HarmonyPatch(typeof(Tameable), "Awake")]
internal static class YoungCommandPatch
{
	private static void Postfix(Tameable __instance) => YoungFighters.CopyCommandable(__instance);
}

/// <summary>
/// Jungtiere mit neuer Kampf-KI fressen nicht (leere Futterliste): Zahm sind sie nie hungrig. Sonst stünde am Tier „hungrig“
/// statt „glücklich“ (GC_Tiere zeigt „glücklich“ nur bei Tieren ohne Kampf-KI), und GC_Futterfolge reihte sie beim Fressen
/// ein. Wilde Jungtiere bleiben hungrig, damit sie sich nicht ohne Futter zähmen (Tameable.TamingUpdate). Review 27.10.2026.
/// </summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.IsHungry))]
internal static class YoungHungerPatch
{
	private static void Postfix(Tameable __instance, ref bool __result)
	{
		if (__result && YoungFighters.NeverHungry(__instance))
		{
			__result = false;
		}
	}
}
