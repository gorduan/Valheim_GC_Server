using System.Collections.Generic;
using BepInEx.Configuration;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Reiten (Gorduan, 02.11.2026), für alle Reittiere (Lox, Asksvin, Bär, Aasbär):
/// - Linksklick: leichter Angriff des Reittiers, Rechtsklick: schwerer (Bär und Aasbär Biss und Sprung mit Aufschlag, Lox Biss
///   und Stampfen, Asksvin Biss und Ansprung), mittlere Maustaste (Gorduan, 03.11.2026: „Angriffe für Maustaste 1–3“): dritter
///   Angriff (Bär und Aasbär Prankenhiebe, Asksvin Kopfstoß; der Lox hat nur zwei). Alle beißen links, springen rechts (Gorduan,
///   04.11.2026: Bär getauscht, Asksvin „soll sich am Bär orientieren“). Ziel ist der Gegner in Blickrichtung (wie
///   beim Kriegsreittier), sonst greift das Tier dorthin an, wohin der Reiter schaut. Abklingzeit wie bei der KI des Tiers
///   (m_aiAttackInterval), jeder Angriff kostet Ausdauer des Reittiers.
/// - Absteigen nur mit Benutzen (E) (Gorduan, 03.11.2026): Valheim stieg bei Linksklick, mittlerer Maustaste, Sprung und
///   Ausweichen ab. Sättel mit Sprungkraft springen weiter mit der Sprungtaste (SaddleUniqueEffects).
/// - A/D: im Stand auf der Stelle drehen, in der Fahrt lenken (Valheim fährt nach einmal W weiter). Bisher drehte die rechte
///   Maustaste (Valheims Blocken) das Tier, die gehört jetzt dem schweren Angriff.
/// - Bär und Aasbär sind geritten wendiger: 25 % schneller und wenden dreimal so schnell. Der Lox bleibt, wie er ist.
/// Der Kriegsreittier-Sattel (Hildisvíni) greift seitdem ohne Ausdauer an und ist doppelt so schnell wieder bereit.
///
/// Wer aufsitzt, besitzt das Reittier (Sadle.RPC_RequestControl, siehe SaddleUniqueEffects): Angriff, Wenden und Tempo rechnet
/// deshalb der Reiter selbst.
/// </summary>
internal static class RideCombat
{
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<float> LightStamina = null!;
	internal static ConfigEntry<float> HeavyStamina = null!;
	internal static ConfigEntry<float> AgileSpeed = null!;
	internal static ConfigEntry<float> AgileTurn = null!;

	/// <summary>
	/// Angriffe je Reittier (Prefab-Hash, Waffen-Items der Vorlage): leicht auf Linksklick, schwer auf Rechtsklick, dritter auf
	/// der mittleren Maustaste ("" = keiner). Bär und Aasbär seit dem Test am 04.11.2026 (Gorduan: „Linksklick sollte der aktuelle
	/// Mittlere-Maustaste-Angriff sein“): Biss links, Prankenhiebe auf der mittleren Maustaste.
	/// </summary>
	private static readonly Dictionary<int, string[]> Attacks = new()
	{
		["Bjorn".GetStableHashCode()] = new[] { "bjorn_bite", "bjorn_slam", "bjorn_swipe_combo" },
		["Unbjorn".GetStableHashCode()] = new[] { "unbjorn_bite", "unbjorn_slam", "unbjorn_swipe_combo" },
		["Lox".GetStableHashCode()] = new[] { "lox_bite", "lox_stomp", "" },
		["Asksvin".GetStableHashCode()] = new[] { "Asksvin_Bite", "Asksvin_Pounce", "Asksvin_Headbutt" },
	};

	private const int Light = 0;
	private const int Heavy = 1;
	private const int Third = 2;

	private const string BearName = "$enemy_bjorn";
	private const string UnbjornName = "$enemy_unbjorn";

	/// <summary>Kürzeste Pause zwischen zwei Angriffen (sonst feuerte gehaltene Taste jedes Bild).</summary>
	private const float MinPause = 0.8f;

	/// <summary>So weit vor der Blickrichtung des Tiers liegt beim Drehen mit A/D das Ziel (es dreht, solange die Taste gehalten wird).</summary>
	private const float TurnLead = 45f;

	/// <summary>Bereit ab (Time.time) je Reittier und Angriff (Instanz-ID * 3 + Angriff).</summary>
	private static readonly Dictionary<int, float> Ready = new();
	private static float nextCleanup;

	internal static void Init(ConfigFile config)
	{
		const string section = "9 - Saddles";
		Enabled = Gc.BindSynced(config, section, "RideAttacks", true, "Gorduan, 02.11.2026: Beim Reiten greift das Reittier mit Linksklick (leicht), Rechtsklick (schwer) und mittlerer Maustaste (dritter Angriff) an, A/D dreht es auf der Stelle bzw. lenkt in der Fahrt. Absteigen nur mit E. Aus: wie Valheim (Linksklick, mittlere Maustaste, Sprung und Ausweichen steigen ab, rechte Maustaste wendet).");
		LightStamina = Gc.BindSynced(config, section, "RideAttackStamina", 3f, "Ausdauer des Reittiers für einen leichten Angriff (Linksklick). Ein Sattel hat 240 und erholt 0,2 je Sekunde.", new AcceptableValueRange<float>(0f, 100f));
		HeavyStamina = Gc.BindSynced(config, section, "RideAttackStaminaHeavy", 6f, "Ausdauer des Reittiers für einen schweren Angriff (Rechtsklick).", new AcceptableValueRange<float>(0f, 100f));
		AgileSpeed = Gc.BindSynced(config, section, "RideBearSpeed", 25f, "Bär und Aasbär geritten: so viel Prozent schneller (Gorduan: 25 %).", new AcceptableValueRange<float>(0f, 200f));
		AgileTurn = Gc.BindSynced(config, section, "RideBearTurn", 3f, "Bär und Aasbär geritten: so viel mal schneller wenden (Gorduan: dreimal).", new AcceptableValueRange<float>(1f, 10f));
	}

	// ------------------------------------------------------------------ Angriffe

	/// <summary>
	/// Player.SetControls des eigenen Spielers beim Reiten: Linksklick, Rechtsklick und mittlere Maustaste greifen an statt
	/// abzusteigen bzw. zu wenden. Gehalten greift das Tier wieder an, sobald es bereit ist.
	/// </summary>
	internal static void OnControls(Player player, ref bool attack, ref bool attackHold, ref bool secondaryAttack, ref bool secondaryAttackHold, ref bool block, ref bool blockHold)
	{
		if (!Enabled.Value || player.m_doodadController is not Sadle sadle || !sadle.IsValid())
		{
			return;
		}
		int kind = attack || attackHold ? Light : block || blockHold ? Heavy : secondaryAttack || secondaryAttackHold ? Third : -1;
		attack = false; // sonst stiege Valheim ab
		attackHold = false; // kein Angriff des Reiters selbst
		secondaryAttack = false; // mittlere Maustaste: sonst stiege Valheim ab
		secondaryAttackHold = false;
		block = false;
		blockHold = false; // sonst wendete Valheim das Tier (Sadle.ApplyControlls), das macht jetzt A/D
		Character? animal = sadle.GetCharacter();
		if (kind >= 0 && animal != null)
		{
			try
			{
				Attack(player, sadle, animal, kind);
			}
			catch (System.Exception e)
			{
				ServerPlugin.Log.LogWarning($"Reiten: Angriff fehlgeschlagen: {e}");
			}
		}
	}

	/// <summary>Nach allen Tasten des Reitens: Sprung und Ausweichen steigen nicht mehr ab, nur Benutzen (E) (Gorduan, 03.11.2026).</summary>
	internal static void KeepSeat(Player player, ref bool jump, ref bool dodge)
	{
		if (Enabled.Value && player.m_doodadController is Sadle sadle && sadle.IsValid())
		{
			jump = false;
			dodge = false;
		}
	}

	private static void Attack(Player rider, Sadle sadle, Character animal, int kind)
	{
		if (animal is not Humanoid beast)
		{
			return;
		}
		ZNetView? view = animal.m_nview;
		if (view == null || !view.IsValid() || !view.IsOwner() || animal.IsDead() || animal.InAttack() || animal.IsStaggering())
		{
			return;
		}
		float now = Time.time;
		int key = animal.GetInstanceID() * 3 + kind;
		if (Ready.TryGetValue(key, out float ready) && now < ready)
		{
			return;
		}
		bool warMount = SaddleUniqueEffects.LocalRide.MountAttack; // Kriegsreittier: ohne Ausdauer, doppelt so schnell bereit
		float cost = warMount ? 0f : kind == Heavy ? HeavyStamina.Value : LightStamina.Value;
		if (cost > 0f && sadle.GetStamina() < cost)
		{
			Ready[key] = now + MinPause;
			rider.Message(MessageHud.MessageType.TopLeft, GcLoc.Get("gcs_ride_tired"));
			return;
		}
		Character? target = SaddleUniqueEffects.AimTarget(rider, beast);
		ItemDrop.ItemData? weapon = Weapon(beast, view.GetZDO().GetPrefab(), kind, target);
		if (weapon == null)
		{
			return;
		}
		if (target != null)
		{
			SaddleUniqueEffects.Face(animal, target.transform.position);
		}
		else
		{
			Vector3 look = rider.GetLookDir();
			look.y = 0f;
			if (look.sqrMagnitude > 0.01f)
			{
				SaddleUniqueEffects.Face(animal, animal.transform.position + look.normalized);
			}
		}
		if (beast.GetCurrentWeapon() != weapon)
		{
			beast.EquipItem(weapon, false);
		}
		if (!beast.StartAttack(target, false))
		{
			return;
		}
		weapon.m_lastAttackTime = now;
		Ready[key] = now + Mathf.Max(MinPause, weapon.m_shared.m_aiAttackInterval) * (warMount ? 0.5f : 1f);
		if (cost > 0f)
		{
			sadle.UseStamina(cost);
		}
		SaddleUniqueEffects.HoldFacing(animal, target);
		Cleanup(now);
	}

	/// <summary>
	/// Waffe des Angriffs: die feste aus Attacks (der Lox hat keinen dritten); Reittiere anderer Mods leicht wie die KI (beste
	/// für das Ziel), schwer die mit der längsten Abklingzeit (meist der große Angriff), keinen dritten.
	/// </summary>
	private static ItemDrop.ItemData? Weapon(Humanoid beast, int prefab, int kind, Character? target)
	{
		List<ItemDrop.ItemData> items = beast.GetInventory().GetAllItems();
		if (Attacks.TryGetValue(prefab, out string[] names))
		{
			string wanted = names[kind];
			if (wanted.Length == 0)
			{
				return null;
			}
			foreach (ItemDrop.ItemData item in items)
			{
				if (item?.m_dropPrefab != null && item.m_dropPrefab.name == wanted)
				{
					return item;
				}
			}
		}
		if (kind == Third)
		{
			return null;
		}
		if (kind == Light)
		{
			beast.EquipBestWeapon(target, null, null, null);
			return beast.GetCurrentWeapon();
		}
		ItemDrop.ItemData? best = null;
		foreach (ItemDrop.ItemData item in items)
		{
			if (item != null && item.IsWeapon() && item.m_shared.m_aiTargetType == ItemDrop.ItemData.AiTarget.Enemy
				&& (best == null || item.m_shared.m_aiAttackInterval > best.m_shared.m_aiAttackInterval))
			{
				best = item;
			}
		}
		return best;
	}

	private static void Cleanup(float now)
	{
		if (now < nextCleanup)
		{
			return;
		}
		nextCleanup = now + 60f;
		List<int> old = new();
		foreach (KeyValuePair<int, float> entry in Ready)
		{
			if (entry.Value < now)
			{
				old.Add(entry.Key);
			}
		}
		foreach (int key in old)
		{
			Ready.Remove(key);
		}
	}

	// ------------------------------------------------------------------ Wenden mit A/D

	/// <summary>
	/// Sadle.ApplyControlls beim eigenen Spieler: A/D ohne W und ohne Rennen. Fährt das Tier (Valheim fährt nach einmal W weiter),
	/// lenkt es nur; steht es, dreht es auf der Stelle. false = Valheims Steuerung fällt für dieses Bild weg.
	/// </summary>
	internal static bool Turn(Sadle sadle, Vector3 moveDir, bool run)
	{
		if (!Enabled.Value || Player.m_localPlayer == null)
		{
			return true;
		}
		bool turnKey = !run && Mathf.Abs(moveDir.z) <= 0.5f && Mathf.Abs(moveDir.x) >= 0.5f;
		if (!turnKey && !turning)
		{
			return true;
		}
		Character? animal = sadle.GetCharacter();
		ZNetView? view = sadle.m_nview;
		Vector3 forward = animal != null ? animal.transform.forward : Vector3.zero;
		forward.y = 0f;
		if (view == null || !view.IsValid() || forward.sqrMagnitude < 0.01f)
		{
			turning = false;
			return true;
		}
		forward.Normalize();
		bool moving = sadle.m_speed == Sadle.Speed.Walk || sadle.m_speed == Sadle.Speed.Run;
		float skill = Player.m_localPlayer.GetSkills().GetSkillFactor(Skills.SkillType.Ride);
		if (!turnKey)
		{
			// losgelassen: geradeaus weiter (sonst drehte es noch bis zum letzten Ziel, TurnLead weiter); im Stand hält es an
			turning = false;
			view.InvokeRPC("Controls", forward, (int)Sadle.Speed.NoChange, skill);
			return true;
		}
		turning = true;
		Vector3 dir = Quaternion.Euler(0f, Mathf.Sign(moveDir.x) * TurnLead, 0f) * forward;
		view.InvokeRPC("Controls", dir, (int)(moving ? Sadle.Speed.NoChange : Sadle.Speed.Turn), skill);
		return false;
	}

	/// <summary>Im letzten Bild wurde mit A/D gedreht (beim Loslassen einmal geradeaus).</summary>
	private static bool turning;

	// ------------------------------------------------------------------ Bär und Aasbär wendiger

	/// <summary>Ein Bär oder Aasbär, der gerade geritten wird (bei allen gleich: Valheims Sadle prüft den Reiter über das ZDO).</summary>
	internal static bool AgileRidden(Character? animal)
	{
		if (animal == null || (animal.m_name != BearName && animal.m_name != UnbjornName) || !Enabled.Value)
		{
			return false;
		}
		BaseAI? ai = animal.m_baseAI;
		Tameable? tame = ai != null ? ai.m_tamable : null;
		Sadle? sadle = tame != null ? tame.m_saddle : null;
		return sadle != null && sadle.isActiveAndEnabled && sadle.HaveValidUser();
	}

	/// <summary>Tempo (Valheim rechnet Lauf- und Wendetempo durch ApplyStatusEffectSpeedMods).</summary>
	internal static float SpeedFactor => 1f + AgileSpeed.Value / 100f;

	/// <summary>Wendetempo zusätzlich zum Tempo, damit es zusammen AgileTurn ergibt.</summary>
	internal static float TurnFactor => AgileTurn.Value / SpeedFactor;
}

/// <summary>A/D beim Reiten: im Stand auf der Stelle drehen, in der Fahrt lenken.</summary>
[HarmonyPatch(typeof(Sadle), nameof(Sadle.ApplyControlls))]
internal static class RideTurnPatch
{
	private static bool Prefix(Sadle __instance, Vector3 moveDir, bool run) => RideCombat.Turn(__instance, moveDir, run);
}

/// <summary>Bär und Aasbär geritten schneller (gilt auch fürs Wenden, Character.UpdateWalking).</summary>
[HarmonyPatch(typeof(SEMan), nameof(SEMan.ApplyStatusEffectSpeedMods))]
internal static class RideAgileSpeedPatch
{
	private static void Postfix(SEMan __instance, ref float speed)
	{
		if (RideCombat.AgileRidden(__instance.m_character))
		{
			speed *= RideCombat.SpeedFactor;
		}
	}
}

/// <summary>Bär und Aasbär geritten: wenden insgesamt dreimal so schnell.</summary>
[HarmonyPatch(typeof(Character), "UpdateRotation")]
internal static class RideAgileTurnPatch
{
	private static void Prefix(Character __instance, ref float turnSpeed)
	{
		if (RideCombat.AgileRidden(__instance))
		{
			turnSpeed *= RideCombat.TurnFactor;
		}
	}
}
