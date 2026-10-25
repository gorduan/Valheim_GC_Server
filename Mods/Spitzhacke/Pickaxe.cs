using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Server;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Server.Pickaxe;

/// <summary>
/// Spitzhacke als Waffe (früher GC_Pickaxe, seit GC_Server mit Streitkolben-Schlag statt Speer-Stich).
///
/// Mittlere Maustaste (Zweitangriff) führt mit jeder Spitzhacke den Kampfschlag des Streitkolbens aus: Animation,
/// Schlagkette, Reichweite, Winkel und Ausdauer kommen vom Hauptangriff des eingestellten Streitkolbens (Standard
/// MaceBronze). Der Schaden ist der Schaden der Spitzhacke, gegen Gegner 125 %, gegen Erz und Gestein 75 %, damit
/// der Kampfschlag nicht zum schnelleren Abbauen taugt. Der Linksklick bleibt der normale Abbau-Schlag.
/// Es gibt keine Wurf-Animation. Werte gibt der Server vor, damit alle Spieler dieselben Zahlen haben.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_pickaxe";
	public const string ModName = "GC_Pickaxe";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<string> AnimationSource = null!;
	internal static ConfigEntry<int> EnemyPercent = null!;
	internal static ConfigEntry<int> OrePercent = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked;

	/// <summary>Zweitangriff jeder Spitzhacke vor der Änderung (Vanilla: keiner), zum Zurücksetzen beim Abschalten.</summary>
	private static readonly Dictionary<string, Attack> OriginalSecondary = new();
	private static string lastReport = "";

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ServerPlugin.Log;
		Enabled = Gc.BindSynced(Config, "2 - Pickaxe", "Enabled", true, "Streitkolben-Schlag mit der Spitzhacke auf der mittleren Maustaste (wird vom Server vorgegeben, auch im Menü GC Option).");
		AnimationSource = Gc.BindSynced(Config, "2 - Pickaxe", "AnimationSource", "MaceBronze", "Streitkolben, dessen Hauptangriff (Animation, Schlagkette, Reichweite, Ausdauer) übernommen wird, z. B. MaceBronze, MaceIron, MaceSilver, MaceNeedle.");
		EnemyPercent = Gc.BindSynced(Config, "2 - Pickaxe", "EnemyDamagePercent", 125, "Schaden des Kampfschlags gegen Gegner in Prozent des Spitzhacken-Schadens.", new AcceptableValueRange<int>(10, 300));
		OrePercent = Gc.BindSynced(Config, "2 - Pickaxe", "OreDamagePercent", 75, "Schaden des Kampfschlags gegen Erz und Gestein in Prozent des Spitzhacken-Schadens.", new AcceptableValueRange<int>(0, 300));
		// Auch nach der Synchronisierung vom Server und nach dem Umschalten im Mod-Menü neu anwenden
		Enabled.SettingChanged += (_, _) => Apply(ObjectDB.instance);
		AnimationSource.SettingChanged += (_, _) => Apply(ObjectDB.instance);
	}

	internal static void Apply(ObjectDB? db)
	{
		if (Blocked || db == null || db.m_items == null || db.m_items.Count == 0)
		{
			return; // läuft die alte Einzelmod GC_Pickaxe noch, gehört ihr der Zweitangriff
		}
		GameObject? sourcePrefab = db.GetItemPrefab(AnimationSource.Value);
		ItemDrop? sourceDrop = sourcePrefab != null ? sourcePrefab.GetComponent<ItemDrop>() : null;
		Attack? swing = sourceDrop != null ? sourceDrop.m_itemData.m_shared.m_attack : null;
		int count = 0;
		foreach (GameObject prefab in db.m_items)
		{
			ItemDrop? drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
			if (drop == null)
			{
				continue;
			}
			ItemDrop.ItemData.SharedData shared = drop.m_itemData.m_shared;
			if (shared.m_skillType != Skills.SkillType.Pickaxes)
			{
				continue;
			}
			if (!OriginalSecondary.ContainsKey(prefab.name))
			{
				OriginalSecondary[prefab.name] = shared.m_secondaryAttack;
			}
			shared.m_secondaryAttack = On && swing != null ? swing.Clone() : OriginalSecondary[prefab.name];
			count++;
		}
		// Aufgehobene Items haben eine eigene Kopie der Item-Daten (Unity kopiert sie beim Erzeugen in der Welt):
		// Spitzhacken im eigenen Inventar beim Umschalten im Mod-Menü mitziehen.
		Player? player = Player.m_localPlayer;
		if (player != null)
		{
			foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
			{
				ItemDrop? prefabDrop = item.m_dropPrefab != null ? item.m_dropPrefab.GetComponent<ItemDrop>() : null;
				if (item.m_shared.m_skillType == Skills.SkillType.Pickaxes && prefabDrop != null && item.m_shared != prefabDrop.m_itemData.m_shared)
				{
					item.m_shared.m_secondaryAttack = prefabDrop.m_itemData.m_shared.m_secondaryAttack;
				}
			}
		}
		string report = !On ? $"Kampfschlag aus, {count} Spitzhacken wie Vanilla."
			: swing == null ? $"Streitkolben '{AnimationSource.Value}' nicht gefunden, kein Kampfschlag."
			: $"Kampfschlag von {AnimationSource.Value} (Animation {swing.m_attackAnimation}, {Mathf.Max(1, swing.m_attackChainLevels)} Schläge, Ausdauer {swing.m_attackStamina:0.#}) für {count} Spitzhacken.";
		if (report != lastReport)
		{
			lastReport = report;
			if (On && swing == null)
			{
				Log.LogWarning(report);
			}
			else
			{
				Log.LogInfo(report);
			}
		}
	}

	/// <summary>Läuft gerade der Kampfschlag einer Spitzhacke (Zweitangriff)? Spitzhacken haben in Vanilla keinen Zweitangriff.</summary>
	internal static bool IsCombatSwing(Attack attack)
	{
		ItemDrop.ItemData? weapon = attack.m_weapon;
		return weapon != null && weapon.m_shared.m_skillType == Skills.SkillType.Pickaxes
			&& attack.m_character is Humanoid humanoid && humanoid.m_currentAttackIsSecondary;
	}

	/// <summary>Erz und Gestein: alles, was man mit der Spitzhacke abbaut (MineRock, MineRock5, Destructible mit Spitzhacken-Schaden).</summary>
	internal static bool IsOre(GameObject target)
	{
		if (target.GetComponent<MineRock>() != null || target.GetComponent<MineRock5>() != null)
		{
			return true;
		}
		Destructible? destructible = target.GetComponent<Destructible>();
		if (destructible == null || target.GetComponent<WearNTear>() != null)
		{
			return false;
		}
		HitData.DamageModifier pickaxe = destructible.m_damages.m_pickaxe;
		return pickaxe != HitData.DamageModifier.Immune && pickaxe != HitData.DamageModifier.Ignore;
	}
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
internal static class ObjectDBAwakePatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(ObjectDB __instance) => Plugin.Apply(__instance);
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
internal static class ObjectDBCopyPatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(ObjectDB __instance) => Plugin.Apply(__instance);
}

/// <summary>
/// Schaden je Ziel: Attack.ModifyDamage läuft bei jedem Treffer direkt vor IDestructible.Damage, das getroffene Objekt
/// steckt in hitData.m_hitCollider. Der Ketten-Bonus des letzten Schlags (Vanilla ×2) kommt danach noch dazu.
/// </summary>
[HarmonyPatch(typeof(Attack), nameof(Attack.ModifyDamage))]
internal static class ModifyDamagePatch
{
	private static void Postfix(Attack __instance, HitData hitData)
	{
		if (!Plugin.On || hitData == null || hitData.m_hitCollider == null || !Plugin.IsCombatSwing(__instance))
		{
			return;
		}
		GameObject target = Projectile.FindHitObject(hitData.m_hitCollider);
		if (target == null)
		{
			return;
		}
		if (target.GetComponent<Character>() != null)
		{
			hitData.m_damage.Modify(Plugin.EnemyPercent.Value / 100f);
		}
		else if (Plugin.IsOre(target))
		{
			float factor = Plugin.OrePercent.Value / 100f;
			if (__instance.m_attackChainLevels > 1 && __instance.m_currentAttackCainLevel == __instance.m_attackChainLevels - 1)
			{
				factor *= 0.5f; // Vanilla verdoppelt danach den letzten Schlag der Kette; gegen Erz soll jeder Schlag gleich zählen
			}
			hitData.m_damage.Modify(factor);
		}
	}
}
