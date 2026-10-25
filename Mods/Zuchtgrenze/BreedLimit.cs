using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Zuchtgrenze (Gorduan, 18.10.2026). Valheim lässt ein zahmes Tier nur trächtig werden, solange im Umkreis m_totalCheckRange weniger
/// als m_maxCreatures Tiere seiner Art stehen (Procreation.Procreate mit SpawnSystem.GetNrOfInstances): Erwachsene und Junge,
/// auch wilde, im Raum gemessen; bei Hühnern Hennen und gelegte Eier, Küken nicht. Beispiel Wolf: 4 in 10 m.
///
/// Hier: Grenze × LimitFactor (Standard 1,5), Umkreis × RadiusFactor (Standard 1), und Tiere an einer Leine oder an einem Pfosten
/// zählen nur LeashedCount (Standard 0,5). Hühner (Henne) haben eigene Werte, Standard wie Valheim. Gerechnet wie in Valheim beim
/// Besitzer des Tiers, deshalb gibt der Server die Werte vor (Admin, synchronisiert).
///
/// Umsetzung: Ein Prefix zählt selbst (gewichtet) und setzt m_maxCreatures nur für diesen Aufruf so, dass Valheims eigene Prüfung
/// „Anzahl ≥ m_maxCreatures“ genau dann sperrt, wenn die gewichtete Zahl die neue Grenze erreicht; der Finalizer stellt den Wert
/// zurück. Der Rest von Procreate (Zufall, Hunger, Partner, Liebespunkte, Geburt) bleibt Valheims Code.
/// </summary>
internal static class BreedLimit
{
	private const string ChickenPrefab = "Hen";

	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<float> LimitFactor = null!;
	internal static ConfigEntry<float> RadiusFactor = null!;
	internal static ConfigEntry<float> LeashedCount = null!;
	internal static ConfigEntry<float> ChickenLimitFactor = null!;
	internal static ConfigEntry<float> ChickenRadiusFactor = null!;
	internal static ConfigEntry<float> ChickenLeashedCount = null!;
	private static bool reported;

	internal static void Init(ConfigFile Config)
	{
		const string section = "9 - Breeding";
		Enabled = Gc.BindSynced(Config, section, "Enabled", true, "Eigene Zuchtgrenze: Valheim lässt Tiere nur Junge bekommen, solange im Umkreis nicht zu viele ihrer Art stehen (z. B. 4 Wölfe in 10 m; Junge und wilde Tiere zählen mit). Aus: wie Valheim.");
		LimitFactor = Gc.BindSynced(Config, section, "LimitFactor", 1.5f, "Faktor auf Valheims Grenze je Tierart (1,5: aus 4 Wölfen werden 6). Nicht für Hühner.", new AcceptableValueRange<float>(0.5f, 5f));
		RadiusFactor = Gc.BindSynced(Config, section, "RadiusFactor", 1f, "Faktor auf Valheims Umkreis, in dem gezählt wird (1 = wie Valheim, z. B. 10 m bei Wölfen). Nicht für Hühner.", new AcceptableValueRange<float>(0.25f, 3f));
		LeashedCount = Gc.BindSynced(Config, section, "LeashedCount", 0.5f, "So viel zählt ein Tier an einer Leine oder an einem Pfosten (0,5 = halb, 1 = voll wie Valheim). Nicht für Hühner.", new AcceptableValueRange<float>(0f, 1f));
		ChickenLimitFactor = Gc.BindSynced(Config, section, "ChickenLimitFactor", 1f, "Hühner: Faktor auf Valheims Grenze (gezählt werden Hennen und gelegte Eier). 1 = wie Valheim.", new AcceptableValueRange<float>(0.5f, 5f));
		ChickenRadiusFactor = Gc.BindSynced(Config, section, "ChickenRadiusFactor", 1f, "Hühner: Faktor auf Valheims Umkreis. 1 = wie Valheim.", new AcceptableValueRange<float>(0.25f, 3f));
		ChickenLeashedCount = Gc.BindSynced(Config, section, "ChickenLeashedCount", 1f, "Hühner: so viel zählt eine Henne an einer Leine oder an einem Pfosten. 1 = wie Valheim.", new AcceptableValueRange<float>(0f, 1f));
	}

	/// <summary>Vor Valheims Prüfung: selbst gewichtet zählen und die Grenze für diesen Aufruf festlegen (offen oder gesperrt).</summary>
	internal static void Apply(Procreation procreation)
	{
		try
		{
			if (!Enabled.Value || ZNetScene.instance == null)
			{
				return;
			}
			ZNetView view = procreation.m_nview;
			if (view == null || !view.IsValid() || !view.IsOwner() || procreation.m_tameable == null || !procreation.m_tameable.IsTamed() || procreation.IsPregnant())
			{
				return; // nur der Zweig „noch nicht trächtig“ fragt die Grenze ab
			}
			GameObject mine = ZNetScene.instance.GetPrefab(view.GetZDO().GetPrefab());
			if (mine == null)
			{
				return;
			}
			bool chicken = mine.name == ChickenPrefab;
			float limitFactor = chicken ? ChickenLimitFactor.Value : LimitFactor.Value;
			float radiusFactor = chicken ? ChickenRadiusFactor.Value : RadiusFactor.Value;
			float leashed = chicken ? ChickenLeashedCount.Value : LeashedCount.Value;
			if (Mathf.Approximately(limitFactor, 1f) && Mathf.Approximately(radiusFactor, 1f) && Mathf.Approximately(leashed, 1f))
			{
				return; // genau wie Valheim (Standard bei Hühnern)
			}
			GameObject? offspring = procreation.m_offspring != null ? ZNetScene.instance.GetPrefab(global::Utils.GetPrefabName(procreation.m_offspring)) : null;
			Vector3 center = procreation.transform.position;
			float range = procreation.m_totalCheckRange * radiusFactor;
			float count = Count(mine, center, range, leashed) + Count(offspring, center, range, leashed);
			procreation.m_maxCreatures = count < procreation.m_maxCreatures * limitFactor ? int.MaxValue : 0;
		}
		catch (Exception e)
		{
			if (!reported)
			{
				reported = true;
				ServerPlugin.Log.LogWarning($"Zuchtgrenze: {e}");
			}
		}
	}

	/// <summary>
	/// Wie SpawnSystem.GetNrOfInstances: Tiere mit genau dem Namen „Prefab(Clone)“ im Abstand (im Raum), wilde eingeschlossen; Tiere an
	/// einer Leine (gehalten) oder an einem Pfosten zählen anteilig. Was keine KI hat (Eier), zählt Valheim selbst.
	/// </summary>
	private static float Count(GameObject? prefab, Vector3 center, float range, float leashed)
	{
		if (prefab == null)
		{
			return 0f;
		}
		if (prefab.GetComponent<BaseAI>() == null)
		{
			return SpawnSystem.GetNrOfInstances(prefab, center, range);
		}
		string name = prefab.name + "(Clone)";
		float count = 0f;
		foreach (BaseAI ai in BaseAI.BaseAIInstances)
		{
			if (ai == null || ai.gameObject.name != name || (range > 0f && Vector3.Distance(center, ai.transform.position) > range))
			{
				continue;
			}
			count += OnLeash(ai) ? leashed : 1f;
		}
		return count;
	}

	private static readonly int LeashHolderHash = GcKeys.LeashHolder.GetStableHashCode();
	private static readonly int LeashTiedHash = GcKeys.LeashTied.GetStableHashCode();

	private static bool OnLeash(BaseAI ai)
	{
		ZNetView view = ai.m_nview;
		if (view == null || !view.IsValid())
		{
			return false;
		}
		ZDO zdo = view.GetZDO();
		return zdo.GetLong(LeashHolderHash) != 0L || zdo.GetInt(LeashTiedHash) == 1; // Schlüssel von GC_Tiere, ohne GC_Tiere nie gesetzt
	}

	/// <summary>Einmal beim Start ins Log: Valheims Grenzen je Tierart und was daraus wird (Doku und Kontrolle).</summary>
	internal static void LogLimits(ZNetScene scene)
	{
		List<string> parts = new();
		foreach (GameObject prefab in scene.m_prefabs)
		{
			Procreation? procreation = prefab != null ? prefab.GetComponent<Procreation>() : null;
			if (procreation == null)
			{
				continue;
			}
			bool chicken = prefab!.name == ChickenPrefab;
			float factor = chicken ? ChickenLimitFactor.Value : LimitFactor.Value;
			float radius = chicken ? ChickenRadiusFactor.Value : RadiusFactor.Value;
			parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} in {2:0.#} m → {3:0.#} in {4:0.#} m", prefab.name, procreation.m_maxCreatures,
				procreation.m_totalCheckRange, procreation.m_maxCreatures * factor, procreation.m_totalCheckRange * radius));
		}
		ServerPlugin.Log.LogInfo($"Zuchtgrenze ({(Enabled.Value ? "an" : "aus")}, an der Leine zählt ein Tier {LeashedCount.Value:0.##}, eine Henne {ChickenLeashedCount.Value:0.##}): {string.Join(", ", parts)}");
	}
}

[HarmonyPatch(typeof(Procreation), nameof(Procreation.Procreate))]
internal static class BreedLimitPatch
{
	private static void Prefix(Procreation __instance, out int __state)
	{
		__state = __instance.m_maxCreatures;
		BreedLimit.Apply(__instance);
	}

	/// <summary>Valheims Wert immer zurück, auch wenn Procreate scheitert.</summary>
	private static Exception? Finalizer(Procreation __instance, int __state, Exception? __exception)
	{
		__instance.m_maxCreatures = __state;
		return __exception;
	}
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class BreedLimitLogPatch
{
	private static bool logged;

	private static void Postfix(ZNetScene __instance)
	{
		if (logged)
		{
			return;
		}
		logged = true;
		try
		{
			BreedLimit.LogLimits(__instance);
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Zuchtgrenze: Liste nicht geschrieben: {e.Message}");
		}
	}
}
