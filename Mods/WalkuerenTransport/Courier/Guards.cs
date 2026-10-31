using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GC.Server.Courier;

/// <summary>
/// Wächter des Eastereggs (Gorduan, 19.10.2026): „drei Bosse gleichzeitig: ein Grauzwerg, ein Berserker und ein Schamane,
/// jeweils mit der Stärke, dem Aussehen und den HP von zwei der Zweisterne-Version, aber in der Größe skaliert wie Bosse
/// und Bossbalken. Sind diese besiegt, endet Event 1, die Möwe kommt und anschließend der Hühner-Raid.“
///
/// Valheims Grauzwerg, Grauzwergen Berserker (Greydwarf_Elite) und Grauzwergen Schamane, je auf Stufe 3 (zwei Sterne, also
/// mit dem Aussehen der Zweisterne-Version). Lebenspunkte und Schaden sind doppelt so hoch wie bei der Zweisterne-Version.
/// Dazu kommen Boss-Größe und Bossbalken. Die Markierung steht im ZDO, so sieht jeder Spieler sie gleich, auch wer später
/// dazukommt. Sie bleiben, bis sie fallen (kein Verschwinden bei Tag, kein Raid-Tier). Der Server zählt mit, wann alle drei
/// tot sind.
/// </summary>
internal static class Guards
{
	internal static readonly string[] Prefabs = { "Greydwarf", "Greydwarf_Elite", "Greydwarf_Shaman" };
	internal const int Level = 3;
	internal const float HealthFactor = 2f;
	internal const float DamageFactor = 2f;
	internal const float Scale = 2.6f;
	internal const float Distance = 12f;
	internal static readonly int MarkHash = "gc_courier_guard".GetStableHashCode();
	internal static readonly int RagdollScaleHash = "gc_courier_guard_ragdoll".GetStableHashCode();

	/// <summary>Die drei Plätze im Dreieck um den Karren; bei allen Spielern gleich (vom Spieler abgeleitet), für die Effekte.</summary>
	internal static Vector3[] Spots(Vector3 center, long playerId)
	{
		float start = Mathf.Abs(playerId % 360);
		Vector3[] spots = new Vector3[Prefabs.Length];
		for (int i = 0; i < spots.Length; i++)
		{
			float angle = (start + i * 120f) * Mathf.Deg2Rad;
			Vector3 spot = center + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * Distance;
			if (ZoneSystem.instance != null)
			{
				spot.y = ZoneSystem.instance.GetGroundHeight(spot); // auf dem Gelände, nicht auf Bäumen oder Dächern
			}
			spots[i] = spot;
		}
		return spots;
	}

	/// <summary>Beim Spieler, der gepfiffen hat: die drei Wächter erscheinen lassen (Netzobjekte, wie Valheims eigene Gegner).</summary>
	internal static List<ZDOID> Spawn(Vector3 center, long playerId)
	{
		List<ZDOID> ids = new();
		Vector3[] spots = Spots(center, playerId);
		for (int i = 0; i < Prefabs.Length; i++)
		{
			GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(Prefabs[i]) : null;
			if (prefab == null)
			{
				ServerPlugin.Log.LogWarning($"Walküren-Transport: Wächter {Prefabs[i]} fehlt.");
				continue;
			}
			Vector3 look = center - spots[i];
			look.y = 0f;
			GameObject go = UnityEngine.Object.Instantiate(prefab, spots[i] + Vector3.up * 0.5f, look.sqrMagnitude > 0.01f ? Quaternion.LookRotation(look) : Quaternion.identity);
			Character? character = go.GetComponent<Character>();
			ZNetView? nview = go.GetComponent<ZNetView>();
			if (character == null || nview == null || !nview.IsValid())
			{
				UnityEngine.Object.Destroy(go);
				continue;
			}
			nview.GetZDO().Set(MarkHash, true); // zuerst: SetLevel ruft SetupMaxHealth, der Patch verdoppelt dann schon
			// gilt schon als gezählt: so zählt Valheim die Wächter nicht zu den aktiven Bossen (Portalsperre, Prüfung 2)
			nview.GetZDO().Set(ZDOVars.s_bossCount, true);
			character.SetLevel(Level);
			character.SetHealth(character.GetMaxHealth());
			Apply(character);
			if (character.GetBaseAI() is MonsterAI ai)
			{
				ai.SetDespawnInDay(false);
				ai.SetHuntPlayer(true);
				ai.Alert();
			}
			ids.Add(nview.GetZDO().m_uid);
		}
		ServerPlugin.Log.LogInfo($"Walküren-Transport: {ids.Count} Wächter erschienen.");
		return ids;
	}

	internal static bool Is(Character? character)
	{
		ZNetView? nview = character != null ? character.m_nview : null;
		return nview != null && nview.IsValid() && nview.GetZDO().GetBool(MarkHash);
	}

	/// <summary>Größe und Bossbalken (bei jedem Spieler, nur einmal je Figur: beim Erscheinen oder beim Laden aus dem ZDO).</summary>
	internal static void Apply(Character character)
	{
		character.transform.localScale *= Scale;
		character.m_boss = true;
		character.m_dontHideBossHud = true;
	}
}

/// <summary>Wächter aus dem ZDO (andere Spieler, später geladen): Größe und Bossbalken.</summary>
[HarmonyPatch(typeof(Character), "Awake")]
internal static class GuardAwakePatch
{
	private static void Postfix(Character __instance)
	{
		try
		{
			if (Guards.Is(__instance))
			{
				Guards.Apply(__instance);
			}
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Walküren-Transport: Wächter nicht eingerichtet: {e.Message}");
		}
	}
}

/// <summary>
/// Lebenspunkte der Wächter: doppelt so viel wie die Zweisterne-Version (Valheim rechnet Grundwert × Stufe). Statt Valheims
/// Rechnung, nicht danach (Prüfung 2): Valheim kappt beim Neuladen mit vollem Leben sonst erst auf die einfache Zahl, und
/// der Wächter stünde danach nur noch bei halbem Leben.
/// </summary>
[HarmonyPatch(typeof(Character), "SetupMaxHealth")]
internal static class GuardHealthPatch
{
	private static bool Prefix(Character __instance)
	{
		if (!Guards.Is(__instance))
		{
			return true;
		}
		__instance.SetMaxHealth(__instance.GetMaxHealthBase() * __instance.GetLevel() * Guards.HealthFactor);
		return false;
	}
}

/// <summary>
/// Wächter zählen nicht als aktive Bosse (Prüfung 2): Mit der Welt-Einstellung „Keine Portale bei Bossen“ sperrte Valheims
/// Zähler sonst alle Portale, und bei drei Bossen im selben Bild verzählt er sich. Beim Erscheinen gilt jeder Wächter schon
/// als gezählt (Valheim zählt ihn nicht hinzu), beim Tod wird das zurückgenommen (Valheim zieht nichts ab).
/// </summary>
[HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
internal static class GuardDeathPatch
{
	private static void Prefix(Character __instance)
	{
		if (Guards.Is(__instance))
		{
			__instance.m_nview.GetZDO().Set(ZDOVars.s_bossCount, false);
		}
	}
}

/// <summary>Die Leiche eines Wächters behält die Boss-Größe (Prüfung 2: sonst schrumpft er beim Sterben auf normale Größe).</summary>
[HarmonyPatch(typeof(Humanoid), "OnRagdollCreated")]
internal static class GuardRagdollPatch
{
	private static void Postfix(Humanoid __instance, Ragdoll ragdoll)
	{
		if (ragdoll == null || !Guards.Is(__instance))
		{
			return;
		}
		Transform body = ragdoll.transform;
		if (body.localScale.x < __instance.transform.localScale.x * 0.9f)
		{
			body.localScale *= Guards.Scale; // nur, wenn Valheim die Größe der Figur nicht schon mitgegeben hat
		}
		ZNetView? nview = ragdoll.GetComponent<ZNetView>();
		if (nview != null && nview.IsValid())
		{
			nview.GetZDO().Set(Guards.RagdollScaleHash, body.localScale.x); // für die anderen Spieler
		}
	}
}

// Leiche eines Wächters bei den anderen Spielern: setzt GC_Kern (Kern\Features\RagdollScale.cs, gleicher Schlüssel);
// der frühere eigene Ragdoll.Awake-Patch tat dasselbe doppelt (Review 27.10.2026).

/// <summary>
/// Drei Bossbalken untereinander (Prüfung 2): Valheim legt jeden Bossbalken an dieselbe Stelle oben, dann wäre nur der
/// oberste lesbar. Solange ein Wächter dabei ist, rückt jeder weitere Bossbalken eine Zeile tiefer.
/// </summary>
[HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
internal static class GuardHudPatch
{
	private static readonly List<EnemyHud.HudData> Bosses = new();
	private static bool shifted;

	private static void Postfix(EnemyHud __instance)
	{
		Bosses.Clear();
		bool guard = false;
		foreach (EnemyHud.HudData hud in __instance.m_huds.Values)
		{
			if (hud.m_gui != null && hud.m_character != null && hud.m_character.IsBoss())
			{
				Bosses.Add(hud);
				guard |= Guards.Is(hud.m_character);
			}
		}
		if (!guard && !shifted)
		{
			return;
		}
		RectTransform? home = __instance.m_baseHudBoss != null ? __instance.m_baseHudBoss.transform as RectTransform : null;
		if (home == null)
		{
			return;
		}
		float row = home.rect.height > 10f ? Mathf.Min(home.rect.height, 120f) + 8f : 60f;
		Bosses.Sort((a, b) => a.m_character.GetInstanceID().CompareTo(b.m_character.GetInstanceID()));
		for (int i = 0; i < Bosses.Count; i++)
		{
			if (Bosses[i].m_gui.transform is RectTransform rect)
			{
				rect.anchoredPosition = home.anchoredPosition + Vector2.down * (guard ? row * i : 0f);
			}
		}
		shifted = guard;
	}
}

/// <summary>Schaden der Wächter: doppelt so viel wie die Zweisterne-Version (beim Angreifer, bevor der Treffer verschickt wird).</summary>
[HarmonyPatch(typeof(Character), nameof(Character.Damage))]
internal static class GuardDamagePatch
{
	private static void Prefix(Character __instance, HitData hit)
	{
		if (hit == null)
		{
			return;
		}
		Character? attacker = hit.GetAttacker();
		if (attacker != null && attacker != __instance && Guards.Is(attacker))
		{
			hit.ApplyModifier(Guards.DamageFactor);
		}
	}
}
