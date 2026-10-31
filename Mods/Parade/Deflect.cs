using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Server;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Server.Deflect;

/// <summary>
/// GC_Deflect: Eine perfekte Parade schleudert feste Geschosse zum Werfer zurück.
///
/// „Fest" heißt: das Geschoss macht nur physischen Schaden (Wucht, Schnitt, Stich, Hack, Spitzhacke) und trägt
/// keinen Status-Effekt. Feuerbälle, Gift, Frost, Blitz und Geist-Magie werden nie zurückgeworfen.
/// Das neue Geschoss startet am Schild und fliegt auf einer ballistischen Bahn dorthin, wo der Werfer im
/// Moment der Parade stand. Es ist nicht zielsuchend, wer ausweicht, wird nicht getroffen.
/// Perfekte Parade = Vanilla-Regel aus Humanoid.BlockAttack: Block innerhalb von 0,25 s mit einem Schild oder
/// einer Waffe, deren Parier-Bonus größer als 1 ist. Der Block muss gelingen (Ausdauer, keine gebrochene Deckung).
/// Im Mehrspieler schickt der Client, dem das Geschoss gehört, Prefab und Tempo an den getroffenen Spieler.
/// Deshalb brauchen Server und alle Spieler die Mod.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_deflect";
	public const string ModName = "GC_Deflect";
	public const string Version = "1.0.0";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked;
	internal static ConfigEntry<float> DamageFactor = null!;
	internal static ConfigEntry<float> FallbackSpeed = null!;
	internal static ConfigEntry<string> ExtraAllowed = null!;
	internal static ConfigEntry<string> NeverList = null!;
	internal static ConfigEntry<bool> DeflectArrows = null!;
	internal static ConfigEntry<bool> LogProjectiles = null!;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ServerPlugin.Log;
		Enabled = Gc.BindSynced(Config, "3 - Deflect", "Enabled", true, "Funktion ein- oder ausschalten (wird vom Server vorgegeben, auch im Menü GC Option).");
		DamageFactor = Gc.BindSynced(Config, "3 - Deflect", "DamageFactor", 1f, "Schaden des zurückgeworfenen Geschosses relativ zum Originalschaden.", new AcceptableValueRange<float>(0f, 5f));
		FallbackSpeed = Gc.BindSynced(Config, "3 - Deflect", "FallbackSpeed", 25f, "Fluggeschwindigkeit in m/s, falls die Originalgeschwindigkeit unbekannt ist.", new AcceptableValueRange<float>(5f, 80f));
		ExtraAllowed = Gc.BindSynced(Config, "3 - Deflect - Filter", "AlwaysDeflect", "", "Kommagetrennte Geschoss-Prefabs, die immer zurückgeworfen werden (auch wenn sie nicht als fest erkannt werden).");
		NeverList = Gc.BindSynced(Config, "3 - Deflect - Filter", "NeverDeflect", "", "Kommagetrennte Geschoss-Prefabs, die nie zurückgeworfen werden.");
		DeflectArrows = Gc.BindSynced(Config, "3 - Deflect - Filter", "DeflectArrows", true, "Pfeile und Bolzen immer zurückwerfen, auch mit Feuer, Gift oder Frost (Bogen- und Armbrust-Schüsse).");
		LogProjectiles = Gc.Bind(Config, "3 - Deflect - Debug", "LogProjectiles", false, "Bei jeder perfekten Parade gegen ein Geschoss den Prefab-Namen ins Log schreiben (für die Filterlisten).");
	}

	internal static bool IsSolid(Projectile projectile, string prefabName, Skills.SkillType skill)
	{
		if (Gc.ParseList(NeverList.Value).Contains(prefabName))
		{
			return false;
		}
		if (Gc.ParseList(ExtraAllowed.Value).Contains(prefabName))
		{
			return true;
		}
		if (DeflectArrows.Value && (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows))
		{
			return true; // Pfeile und Bolzen sind feste Körper, auch wenn sie brennen oder vergiftet sind
		}
		HitData.DamageTypes damage = projectile.m_damage;
		bool elemental = damage.m_fire > 0f || damage.m_frost > 0f || damage.m_lightning > 0f || damage.m_poison > 0f || damage.m_spirit > 0f;
		bool physical = damage.m_blunt > 0f || damage.m_slash > 0f || damage.m_pierce > 0f || damage.m_chop > 0f || damage.m_pickaxe > 0f;
		return physical && !elemental && string.IsNullOrEmpty(projectile.m_statusEffect);
	}

	/// <summary>Anfangsgeschwindigkeit, um mit Tempo v unter Schwerkraft g das Ziel zu treffen (flache Bahn).</summary>
	internal static Vector3 Aim(Vector3 from, Vector3 to, float speed, float gravity)
	{
		Vector3 delta = to - from;
		Vector3 flat = new(delta.x, 0f, delta.z);
		float x = flat.magnitude;
		float y = delta.y;
		if (gravity <= 0.01f || x < 0.1f)
		{
			return delta.normalized * speed;
		}
		float v2 = speed * speed;
		float root = v2 * v2 - gravity * (gravity * x * x + 2f * y * v2);
		float angle = root < 0f ? Mathf.PI / 4f : Mathf.Atan((v2 - Mathf.Sqrt(root)) / (gravity * x));
		Vector3 dir = flat.normalized;
		return (dir * Mathf.Cos(angle) + Vector3.up * Mathf.Sin(angle)) * speed;
	}
}

/// <summary>
/// Geschoss-Infos für den getroffenen Spieler.
/// Gehört das Geschoss diesem Client, läuft der Schaden synchron und das Geschoss steht in <see cref="Current"/>.
/// Gehört der getroffene Spieler einem anderen Client, schickt der Besitzer des Geschosses VOR dem Schadens-RPC
/// Prefab-Name und Geschwindigkeit an diesen Spieler (gleicher Weg über den Server, Reihenfolge bleibt erhalten).
/// </summary>
[HarmonyPatch(typeof(Projectile), nameof(Projectile.OnHit))]
internal static class ProjectileOnHitPatch
{
	internal const string Rpc = "GC_Deflect_Info";
	internal static Projectile? Current;
	internal static string RemotePrefab = "";
	internal static Vector3 RemoteVelocity;
	internal static float RemoteTime = -10f;

	private static void Prefix(Projectile __instance, Collider collider)
	{
		Current = __instance;
		if (!Plugin.On || collider == null || ZRoutedRpc.instance == null)
		{
			return;
		}
		GameObject? hit = Projectile.FindHitObject(collider);
		Player? target = hit != null ? hit.GetComponent<Player>() : null;
		if (target == null || target == Player.m_localPlayer || target.m_nview == null || !target.m_nview.IsValid())
		{
			return;
		}
		long owner = target.m_nview.GetZDO().GetOwner();
		if (owner != 0L && owner != ZNet.GetUID())
		{
			ZRoutedRpc.instance.InvokeRoutedRPC(owner, Rpc, Utils.GetPrefabName(__instance.gameObject), __instance.m_vel);
		}
	}

	private static void Finalizer() => Current = null;

	internal static void OnInfo(long sender, string prefab, Vector3 velocity)
	{
		RemotePrefab = prefab;
		RemoteVelocity = velocity;
		RemoteTime = Time.time;
	}
}

[HarmonyPatch(typeof(Game), nameof(Game.Start))]
internal static class GameStartPatch
{
	private static void Postfix()
	{
		if (Plugin.Blocked)
		{
			return; // die alte Einzelmod registriert denselben RPC
		}
		ZRoutedRpc.instance.Register<string, Vector3>(ProjectileOnHitPatch.Rpc, ProjectileOnHitPatch.OnInfo);
	}
}

[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.BlockAttack))]
[HarmonyAfter("randyknapp.mods.epicloot")] // EpicLoots „Paradefenster“ kürzt m_blockTimer im eigenen Prefix: erst danach lesen (Review 27.10.2026)
internal static class BlockAttackPatch
{
	private struct State
	{
		public bool Perfect;
		public HitData.DamageTypes Damage;
		public float Blockable;
	}

	private static float lastDeflect;

	private static void Prefix(Humanoid __instance, HitData hit, out State __state)
	{
		__state = default;
		if (!Plugin.On || __instance != Player.m_localPlayer || !hit.m_ranged)
		{
			return;
		}
		ItemDrop.ItemData? blocker = __instance.GetCurrentBlocker();
		__state.Perfect = blocker != null && blocker.m_shared.m_timedBlockBonus > 1f && __instance.m_blockTimer != -1f && __instance.m_blockTimer < 0.25f
			&& Vector3.Dot(hit.m_dir, __instance.transform.forward) <= 0f;
		__state.Damage = hit.m_damage.Clone();
		__state.Blockable = hit.GetTotalBlockableDamage();
	}

	private static void Postfix(Humanoid __instance, HitData hit, Character attacker, State __state)
	{
		if (!__state.Perfect || attacker == null || attacker.IsDead() || Time.time - lastDeflect < 0.15f)
		{
			return;
		}
		Player player = (Player)__instance;
		// Vanilla wertet den Block nur als gelungen, wenn Ausdauer da ist und die Deckung nicht bricht. Dann wird der
		// blockbare Schaden reduziert (HitData.BlockDamage). Ohne Reduktion: kein Zurückwerfen.
		if (__state.Blockable > 0f && hit.GetTotalBlockableDamage() >= __state.Blockable - 0.01f)
		{
			return;
		}

		GameObject? prefab = null;
		float speed = Plugin.FallbackSpeed.Value;
		Projectile? live = ProjectileOnHitPatch.Current;
		if (live != null)
		{
			prefab = ZNetScene.instance.GetPrefab(Utils.GetPrefabName(live.gameObject));
			speed = Mathf.Max(5f, live.m_vel.magnitude);
		}
		else if (Time.time - ProjectileOnHitPatch.RemoteTime < 1.5f && ProjectileOnHitPatch.RemotePrefab.Length > 0)
		{
			prefab = ZNetScene.instance.GetPrefab(ProjectileOnHitPatch.RemotePrefab);
			speed = Mathf.Max(5f, ProjectileOnHitPatch.RemoteVelocity.magnitude);
			ProjectileOnHitPatch.RemoteTime = -10f;
		}
		if (prefab == null && attacker is Humanoid humanoid)
		{
			Attack? attack = humanoid.m_currentAttack ?? humanoid.m_previousAttack;
			if (attack?.m_attackProjectile != null)
			{
				prefab = attack.m_attackProjectile;
				speed = Mathf.Max(5f, attack.m_projectileVel);
			}
		}
		Projectile? template = prefab != null ? prefab.GetComponent<Projectile>() : null;
		if (template == null)
		{
			return;
		}
		string name = prefab!.name;
		if (Plugin.LogProjectiles.Value)
		{
			Plugin.Log.LogInfo($"Perfekte Parade gegen Geschoss '{name}' von {attacker.m_name}.");
		}
		if (!Plugin.IsSolid(template, name, hit.m_skill))
		{
			return;
		}

		lastDeflect = Time.time;
		Vector3 target = attacker.GetCenterPoint();
		Vector3 start = hit.m_point + (target - hit.m_point).normalized * 0.8f + Vector3.up * 0.3f;
		Vector3 velocity = Plugin.Aim(start, target, speed, template.m_gravity);

		HitData data = new()
		{
			m_damage = __state.Damage,
			m_pushForce = 30f,
			m_blockable = true,
			m_dodgeable = true,
			m_skill = Skills.SkillType.Blocking,
			m_hitType = HitData.HitType.PlayerHit
		};
		data.m_damage.Modify(Plugin.DamageFactor.Value);

		GameObject go = UnityEngine.Object.Instantiate(prefab, start, Quaternion.LookRotation(velocity));
		Projectile projectile = go.GetComponent<Projectile>();
		projectile.Setup(player, velocity, -1f, data, null, null);
		Gc.TopLeft(GcLoc.Get("gcd_deflected"));
	}
}
