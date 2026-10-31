using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using GC.Shared;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Wirkung der Sondereffekte einzigartiger Sättel (Werte: LeashUniques.cs, BondStats.Mount*). Gorduan, 27.10.2026: „bisher gedachte
/// Fähigkeiten sollen für alle Reittiere funktionieren“, also für Lox, Asksvin, Bär und Unbjørn. Herdenfrieden, Funkenflug,
/// Berserkerritt und Totenritt gibt es nur auf dem Sattel ihres Tiers (Bedingung der Stücke in LeashUniques).
///
/// Mehrspieler (geprüft in Valheim 1.0.20): Wer aufsitzt, besitzt das Reittier. Sadle.RPC_RequestControl gibt das ZDO dem
/// Reiter (SetOwner), und ZDOMan nimmt es ihm erst wieder, wenn das Tier aus seinem Gebiet fällt. Sprung, Gleiten, Angriff,
/// Niederreiten, Funkenflug und Ausdauer rechnet deshalb der Reiter; Treffer gegen Gegner gehen per RPC an deren Besitzer.
/// Schaden an Reiter und Reittier rechnet ihr Besitzer, also wieder der Reiter. Das Reittier wird trotzdem über sein ZDO geprüft
/// (Sattel-Verzauberung, Reiter in der Nähe laut Sadle), damit es auch bei einem anderen Besitzer stimmt. Wilde Lox gehören oft
/// anderen Spielern oder dem Server: Deren Besitzer liest den Herdenfrieden aus dem Spieler-ZDO des Reiters und dem ZDO des
/// Reittiers.
/// </summary>
internal static class SaddleUniqueEffects
{
	/// <summary>Im Spieler-ZDO: reitet gerade mit Herdenfrieden (der Besitzer eines wilden Lox liest es in BaseAI.IsEnemy).</summary>
	internal static readonly int HerdHash = "gc_saddle_herd".GetStableHashCode();

	/// <summary>Effekt und EpicLoot-Fähigkeit tragen denselben Namen (wie EpicLoots „Bulwark“).</summary>
	private const string SprintAbility = "GC_SaddleSprint";
	private const string SprintIcon = "GC_SaddleSprintIcon";
	private const string ProviderId = "GC_Tiere.Saddle";

	/// <summary>Funkenflug: doppelt so schnell (Gorduan).</summary>
	internal const float SprintFactor = 2f;

	/// <summary>Funkenflug: Abklingzeit in Sekunden (Gorduan).</summary>
	private const float SprintCooldown = 20f;

	/// <summary>Himmelsritt: so schnell sinkt das Reittier beim Gleiten höchstens (m/s).</summary>
	private const float GlideFall = 2f;

	/// <summary>Kriegsreittier: kurze Sperre nach einem Angriff auf Tastendruck, damit er nicht jedes Bild feuert.</summary>
	private const float AttackLock = 0.8f;

	/// <summary>Kriegsreittier: so lange schaut das Tier nach dem Angriff zum Ziel, auch wenn der Reiter weiterlenkt.</summary>
	private const float FacingTime = 0.6f;

	/// <summary>Niederreiten: jeder Gegner höchstens alle 2 s (Gorduan).</summary>
	private const float TrampleRepeat = 2f;

	/// <summary>Sprungkraft: nach dem Absteigen höchstens so lange kein Sturzschaden (endet mit der Landung).</summary>
	private const float FallGrace = 10f;

	private static readonly int LoxHash = "Lox".GetStableHashCode();
	private static readonly int LoxCalfHash = "Lox_Calf".GetStableHashCode();

	/// <summary>Funkenflug ohne EpicLoots Fähigkeiten-Leiste (ältere EpicLoot-Fassung): eigene Taste.</summary>
	internal static ConfigEntry<KeyboardShortcut> SprintKey = null!;

	/// <summary>Aus Saddles.Init (Awake): Ersatztaste, EpicLoot-Fähigkeit (sobald Valheims Vorlagen da sind), Beute-Sperre.</summary>
	internal static void Init(ConfigFile config)
	{
		SprintKey = Gc.Bind(config, "9 - Saddles", "SprintKey", new KeyboardShortcut(KeyCode.G),
			"Funkensattel (EpicLoot, nur Asksvin): Taste für den Funkenflug, nur wenn EpicLoot keine Fähigkeiten-Leiste hat (ältere Fassung). Sonst gilt die Taste in EpicLoots Leiste.");
		PrefabManager.OnVanillaPrefabsAvailable += SetupEpicLoot;
		PatchLoot();
	}

	// ------------------------------------------------------------------ Ritt des eigenen Spielers (einmal je Bild ermittelt)

	private static int frame = -1;
	private static Character? mount;
	private static Sadle? saddle;
	private static BondStats ride = BondStats.None;

	private static void Refresh()
	{
		if (frame == Time.frameCount)
		{
			return;
		}
		frame = Time.frameCount;
		mount = null;
		saddle = null;
		ride = BondStats.None;
		Player? player = Player.m_localPlayer;
		if (!LeashMagic.Available || player == null || player.m_doodadController is not Sadle sadle || !sadle.IsValid())
		{
			return;
		}
		Character? animal = sadle.GetCharacter();
		if (animal == null)
		{
			return;
		}
		mount = animal;
		saddle = sadle;
		ride = Saddles.Bond(animal);
	}

	/// <summary>Das Tier, auf dem der eigene Spieler gerade reitet.</summary>
	internal static Character? LocalMount
	{
		get
		{
			Refresh();
			return mount;
		}
	}

	/// <summary>Sattel-Werte des eigenen Ritts (sonst BondStats.None).</summary>
	internal static BondStats LocalRide
	{
		get
		{
			Refresh();
			return ride;
		}
	}

	/// <summary>
	/// Sattel-Werte eines Reittiers, solange es geritten wird. Gilt bei jedem Spieler und auf dem Server gleich: Die
	/// Verzauberung steht im Tier-ZDO, den Reiter prüft Valheims Sadle bei allen (Nutzer aus dem ZDO, in 10 m).
	/// </summary>
	internal static BondStats MountBond(Character? animal)
	{
		if (animal == null || !LeashMagic.Available || animal.IsPlayer() || !animal.IsTamed())
		{
			return BondStats.None;
		}
		BaseAI? ai = animal.m_baseAI;
		Tameable? tame = ai != null ? ai.m_tamable : null;
		Sadle? sadle = tame != null ? tame.m_saddle : null;
		return sadle != null && sadle.isActiveAndEnabled && sadle.HaveValidUser() ? Saddles.Bond(animal) : BondStats.None;
	}

	/// <summary>Sattel-Werte, die gerade für diesen Charakter gelten: der eigene Spieler beim Reiten oder ein gerittenes Reittier.</summary>
	internal static BondStats RideBond(Character? character)
	{
		if (character == null)
		{
			return BondStats.None;
		}
		return character == Player.m_localPlayer ? LocalRide : MountBond(character);
	}

	private static readonly HashSet<string> Reported = new();

	/// <summary>Fehler eines Teils je Sitzung nur einmal ins Log (die Patches laufen in Valheims eigenen Abläufen).</summary>
	private static void Report(string part, Exception e)
	{
		if (Reported.Add(part))
		{
			ServerPlugin.Log.LogWarning($"Sondersättel ({part}): {e}");
		}
	}

	// ------------------------------------------------------------------ Jedes Bild beim eigenen Spieler

	private static readonly Action TickAction = TickLocal;
	private static bool wasRiding;
	private static Character? lastMount;
	private static bool lastJumpRide;
	private static float graceFrom;
	private static float graceUntil;
	private static float nextCleanup;

	internal static void Tick() => ServerPlugin.Module?.Guard("Sondersättel", TickAction);

	private static void TickLocal()
	{
		Player? player = Player.m_localPlayer;
		if (player == null)
		{
			return;
		}
		Refresh();
		Character? animal = mount;
		BondStats bond = ride;
		bool riding = animal != null;
		if (wasRiding && (!riding || animal != lastMount))
		{
			OnDismount(player);
		}
		wasRiding = riding;
		lastMount = animal;
		lastJumpRide = riding && bond.MountJump;
		if (graceUntil > 0f && !riding && (Time.time >= graceUntil || (Time.time > graceFrom + 0.5f && player.IsOnGround())))
		{
			graceUntil = 0f; // gelandet (oder abgelaufen): ab jetzt wieder Sturzschaden
		}
		SetHerdFlag(player, riding && bond.MountHerdPeace);
		if (riding)
		{
			// Brand und Gift, die schon vor dem Aufsitzen liefen, enden beim Reiten (neue verhindern die Patches)
			if (bond.MountFireproof)
			{
				Cleanse(player, SEMan.s_statusEffectBurning);
				Cleanse(animal!, SEMan.s_statusEffectBurning);
			}
			if (bond.MountDeathRide > 0f)
			{
				Cleanse(player, SEMan.s_statusEffectPoison);
				Cleanse(animal!, SEMan.s_statusEffectPoison);
			}
		}
		if (providerReady && UpdateProvided(animal))
		{
			RefreshAbilities(player);
		}
		if (riding && !abilityReady && bond.MountSprint > 0f && Gc.Down(SprintKey) && player.TakeInput())
		{
			SprintByKey();
		}
		if (Time.time >= nextCleanup)
		{
			nextCleanup = Time.time + 5f;
			CleanTrampled();
		}
	}

	/// <summary>
	/// Abgestiegen (oder das Reittier ist weg). Mit Sprungkraft gibt es bis zur Landung keinen Sturzschaden, und der Abstieg
	/// selbst zählt wie Valheims Teleport nicht als Sturz (m_maxAirAltitude auf die jetzige Höhe).
	/// </summary>
	private static void OnDismount(Player player)
	{
		if (lastJumpRide)
		{
			graceFrom = Time.time;
			graceUntil = Time.time + FallGrace;
			player.m_maxAirAltitude = player.transform.position.y;
		}
		airJumpUsed = false;
		facing = null;
		sprinting = null;
	}

	/// <summary>Herdenfrieden im eigenen Spieler-ZDO (nur bei Änderung schreiben).</summary>
	private static void SetHerdFlag(Player player, bool on)
	{
		ZNetView? view = player.m_nview;
		if (view == null || !view.IsValid() || !view.IsOwner())
		{
			return;
		}
		ZDO zdo = view.GetZDO();
		if (zdo.GetBool(HerdHash) != on)
		{
			zdo.Set(HerdHash, on);
		}
	}

	/// <summary>Einen laufenden Statuseffekt beenden (nur beim Besitzer, dort laufen Valheims Statuseffekte).</summary>
	private static void Cleanse(Character character, int hash)
	{
		ZNetView? view = character.m_nview;
		SEMan? seman = character.GetSEMan();
		if (view != null && view.IsValid() && view.IsOwner() && seman != null && seman.HaveStatusEffect(hash))
		{
			seman.RemoveStatusEffect(hash, true);
		}
	}

	// ------------------------------------------------------------------ Tasten beim Reiten

	/// <summary>
	/// Player.SetControls (nur der eigene Spieler): Valheim steigt bei Sprung, Angriff, Zweitangriff und Ausweichen ab. Mit
	/// Sprungkraft springt stattdessen das Reittier, mit dem Kriegsreittier greift es an. Absteigen geht weiter mit Benutzen (E).
	/// </summary>
	internal static void OnControls(Player player, ref bool attack, ref bool attackHold, ref bool jump)
	{
		Refresh();
		Character? animal = mount;
		if (animal == null)
		{
			return;
		}
		BondStats bond = ride;
		try
		{
			if (bond.MountJump && jump)
			{
				jump = false; // sonst stiege Valheim ab
				Jump(animal, bond);
			}
			if (bond.MountAttack && (attack || attackHold))
			{
				if (attack)
				{
					Attack(player, animal);
				}
				attack = false; // sonst stiege Valheim ab
				attackHold = false; // gehalten: kein Angriff des Reiters selbst (Player.PlayerAttackInput)
			}
		}
		catch (Exception e)
		{
			Report("Sattel-Tasten", e);
		}
	}

	// ------------------------------------------------------------------ Sprungkraft und Himmelsritt

	private static bool airJumpUsed;

	/// <summary>
	/// Sprung beim Besitzer (dem Reiter), nur vom Boden; mit dem Sattel des Allvaters einmal je Sprung ein zweiter in der Luft.
	/// Character.Jump nähme die Sprungkraft der Vorlage (bei Lox und Bär nicht für die Größe gedacht) und löste die Animation
	/// „jump“ aus, die ihnen fehlt (Warnung im Log): deshalb ForceJump ohne Animation, mit der Prüfung aus Character.Jump.
	/// </summary>
	private static void Jump(Character animal, BondStats bond)
	{
		ZNetView? view = animal.m_nview;
		if (view == null || !view.IsValid() || !view.IsOwner() || animal.IsDead() || animal.IsSwimming() || animal.InAttack()
			|| animal.IsStaggering() || animal.IsKnockedBack() || animal.InDodge())
		{
			return;
		}
		bool ground = animal.IsOnGround();
		if (!ground && (!bond.MountSkyRide || airJumpUsed))
		{
			return;
		}
		Vector3 velocity = animal.m_body.linearVelocity;
		velocity.y = JumpSpeed(animal);
		animal.ForceJump(velocity, false);
		animal.m_jumpEffects.Create(animal.transform.position, animal.transform.rotation, animal.transform, 1f, -1, animal.GetZDOID());
		if (!ground)
		{
			airJumpUsed = true;
		}
	}

	/// <summary>Sprungkraft passend zur Größe: halbe Körperhöhe (Kollider, Welt), 1,2 bis 2,5 m hoch; v = √(2·g·h).</summary>
	private static float JumpSpeed(Character animal)
	{
		Collider? collider = animal.m_collider;
		float height = collider != null ? collider.bounds.size.y : 2f;
		float rise = Mathf.Clamp(height * 0.5f, 1.2f, 2.5f);
		float gravity = Physics.gravity.magnitude;
		return Mathf.Sqrt(2f * (gravity > 1f ? gravity : 20f) * rise);
	}

	/// <summary>Sprungtaste gehalten (Gleiten), nur wenn das Spiel gerade Eingaben nimmt.</summary>
	private static bool JumpHeld()
	{
		Player? player = Player.m_localPlayer;
		return player != null && player.TakeInput() && (ZInput.GetButton("Jump") || ZInput.GetButton("JoyJump"));
	}

	/// <summary>Jeder Physikschritt des eigenen Reittiers (Sadle.FixedUpdate): Doppelsprung freigeben, Gleiten, Niederreiten.</summary>
	internal static void FixedTick(Sadle sadle)
	{
		Refresh();
		Character? animal = mount;
		if (animal == null || sadle != saddle || !ride.UniqueMountAny)
		{
			return;
		}
		ZNetView? view = animal.m_nview;
		if (view == null || !view.IsValid() || !view.IsOwner())
		{
			return;
		}
		try
		{
			bool ground = animal.IsOnGround();
			if (ground)
			{
				airJumpUsed = false;
			}
			else if (ride.MountSkyRide && !animal.IsSwimming() && JumpHeld())
			{
				// Gleiten: Sprungtaste in der Luft gehalten bremst das Fallen stark (das Steigen nach einem Sprung bleibt)
				Rigidbody body = animal.m_body;
				Vector3 velocity = body.linearVelocity;
				if (velocity.y < -GlideFall)
				{
					velocity.y = -GlideFall;
					body.linearVelocity = velocity;
				}
			}
			if (ride.MountTrample > 0f)
			{
				Trample(animal, sadle, ride.MountTrample);
			}
		}
		catch (Exception e)
		{
			Report("Sattel-Physik", e);
		}
	}

	/// <summary>Sturzschaden des eigenen Spielers: keiner beim Reiten mit Sprungkraft und bis zur Landung nach dem Absteigen.</summary>
	internal static bool NoFallDamage(Character? character)
	{
		return character != null && character == Player.m_localPlayer && (LocalRide.MountJump || Time.time < graceUntil);
	}

	// ------------------------------------------------------------------ Kriegsreittier

	private static float nextAttack;
	private static Character? facing;
	private static Character? facingMount;
	private static float facingUntil;

	/// <summary>
	/// Angriff auf Tastendruck: wie MonsterAI mit den eigenen Waffen des Tiers (EquipBestWeapon nach Abstand, CanUseAttack,
	/// StartAttack). Ziel ist der Gegner, den der Reiter anvisiert (Blickrichtung ±60°, in Reichweite der Waffen); ohne Ziel
	/// greift das Tier nach vorn an.
	/// </summary>
	private static void Attack(Player rider, Character animal)
	{
		if (Time.time < nextAttack || animal is not Humanoid beast)
		{
			return;
		}
		ZNetView? view = animal.m_nview;
		BaseAI? ai = animal.m_baseAI;
		if (view == null || !view.IsValid() || !view.IsOwner() || ai == null || animal.IsDead() || animal.InAttack() || animal.IsStaggering())
		{
			return;
		}
		Character? target = AimTarget(rider, beast);
		if (target != null || beast.GetCurrentWeapon() == null)
		{
			beast.EquipBestWeapon(target, null, null, null);
		}
		ItemDrop.ItemData? weapon = beast.GetCurrentWeapon();
		if (weapon == null || !ai.CanUseAttack(weapon))
		{
			return;
		}
		if (target != null)
		{
			Face(animal, target.transform.position);
		}
		if (beast.StartAttack(target, false))
		{
			nextAttack = Time.time + AttackLock;
			facing = target;
			facingMount = animal;
			facingUntil = Time.time + FacingTime;
		}
	}

	/// <summary>Bestes Ziel vor dem Reiter: kleiner Winkel zur Blickrichtung und kurzer Abstand.</summary>
	private static Character? AimTarget(Player rider, Humanoid animal)
	{
		float reach = 2f;
		foreach (ItemDrop.ItemData item in animal.GetInventory().GetAllItems())
		{
			if (item != null && item.IsWeapon() && item.m_shared.m_aiTargetType == ItemDrop.ItemData.AiTarget.Enemy)
			{
				reach = Mathf.Max(reach, item.m_shared.m_aiAttackRange);
			}
		}
		reach += animal.GetRadius() + 1f;
		Vector3 look = rider.GetLookDir();
		look.y = 0f;
		if (look.sqrMagnitude < 0.01f)
		{
			look = animal.transform.forward;
			look.y = 0f;
		}
		look.Normalize();
		Vector3 origin = animal.transform.position;
		Character? best = null;
		float bestScore = float.MaxValue;
		foreach (Character other in Character.GetAllCharacters())
		{
			if (other == null || other == animal || other == rider || other.IsDead() || !BaseAI.IsEnemy(animal, other))
			{
				continue;
			}
			Vector3 to = other.transform.position - origin;
			if (Mathf.Abs(to.y) > 4f)
			{
				continue;
			}
			to.y = 0f;
			float distance = to.magnitude - other.GetRadius();
			if (distance > reach)
			{
				continue;
			}
			float angle = to.sqrMagnitude > 0.01f ? Vector3.Angle(look, to) : 0f;
			if (angle > 60f)
			{
				continue;
			}
			float score = angle / 60f + Mathf.Max(0f, distance) / reach;
			if (score < bestScore)
			{
				bestScore = score;
				best = other;
			}
		}
		return best;
	}

	/// <summary>Zum Ziel drehen (Valheims Nahkampf trifft in Blickrichtung des Körpers, Attack.GetMeleeAttackDir).</summary>
	private static void Face(Character animal, Vector3 point)
	{
		Vector3 dir = point - animal.transform.position;
		dir.y = 0f;
		if (dir.sqrMagnitude < 0.01f)
		{
			return;
		}
		dir.Normalize();
		Quaternion rotation = Quaternion.LookRotation(dir);
		animal.transform.rotation = rotation;
		animal.m_body.rotation = rotation;
		animal.SetLookDir(dir);
	}

	/// <summary>Sadle.UpdateRiding lenkt das Tier in Blickrichtung des Reiters: kurz nach dem Angriff bleibt der Blick beim Ziel.</summary>
	internal static void KeepFacing(Sadle sadle)
	{
		if (facing == null || Time.time >= facingUntil)
		{
			return;
		}
		Character? animal = sadle.m_character;
		if (animal == null || animal != facingMount)
		{
			return;
		}
		if (facing.IsDead())
		{
			facing = null;
			return;
		}
		Vector3 dir = facing.transform.position - animal.transform.position;
		dir.y = 0f;
		if (dir.sqrMagnitude > 0.01f)
		{
			animal.SetLookDir(dir.normalized);
		}
	}

	// ------------------------------------------------------------------ Niederreiten

	private static readonly Dictionary<Character, float> Trampled = new();
	private static readonly List<Character> TrampledOld = new();
	private static float nextTrample;

	/// <summary>
	/// Im vollen Lauf (Reiter rennt, Tier am Boden mit mindestens 60 % seines Renntempos): Gegner direkt vor dem Tier taumeln und
	/// nehmen Stumpfschaden. Keine Bosse, keine Spieler, keine zahmen Tiere, nur Kreaturen mit Monster-KI, jeder höchstens alle
	/// 2 s. Der Treffer geht per RPC an den Besitzer des Gegners; erst der Schaden, dann das Taumeln (im Taumeln nähme er sonst
	/// doppelten Schaden, Character.RPC_Damage).
	/// </summary>
	private static void Trample(Character animal, Sadle sadle, float damage)
	{
		if (Time.time < nextTrample)
		{
			return;
		}
		nextTrample = Time.time + 0.1f;
		if (sadle.m_speed != Sadle.Speed.Run || !animal.IsOnGround() || animal.IsSwimming())
		{
			return;
		}
		Vector3 forward = animal.transform.forward;
		forward.y = 0f;
		Vector3 velocity = animal.GetVelocity();
		velocity.y = 0f;
		if (forward.sqrMagnitude < 0.01f || velocity.magnitude < Mathf.Max(2f, animal.m_runSpeed * 0.6f))
		{
			return;
		}
		forward.Normalize();
		Vector3 origin = animal.transform.position;
		float radius = animal.GetRadius();
		foreach (Character other in Character.GetAllCharacters())
		{
			if (other == null || other == animal || other.m_nview == null || !other.m_nview.IsValid() || other.IsDead() || other.IsPlayer() || other.IsTamed() || other.IsBoss() || other.m_baseAI is not MonsterAI
				|| !BaseAI.IsEnemy(animal, other))
			{
				continue;
			}
			Vector3 to = other.transform.position - origin;
			if (Mathf.Abs(to.y) > 3f)
			{
				continue;
			}
			to.y = 0f;
			float size = other.GetRadius();
			float ahead = Vector3.Dot(to, forward);
			if (ahead <= 0f || ahead > radius + size + 1f || (to - forward * ahead).magnitude > radius + size * 0.5f)
			{
				continue; // nur direkt vor dem Reittier
			}
			if (Trampled.TryGetValue(other, out float last) && Time.time - last < TrampleRepeat)
			{
				continue;
			}
			Trampled[other] = Time.time;
			HitData hit = new()
			{
				m_point = other.GetCenterPoint(),
				m_dir = forward,
				m_pushForce = 30f,
				m_hitType = HitData.HitType.EnemyHit,
			};
			hit.m_damage.m_blunt = damage;
			hit.SetAttacker(animal);
			other.Damage(hit);
			other.Stagger(forward);
		}
	}

	private static void CleanTrampled()
	{
		if (Trampled.Count == 0)
		{
			return;
		}
		TrampledOld.Clear();
		foreach (KeyValuePair<Character, float> entry in Trampled)
		{
			if (entry.Key == null || Time.time - entry.Value >= TrampleRepeat)
			{
				TrampledOld.Add(entry.Key!);
			}
		}
		foreach (Character old in TrampledOld)
		{
			Trampled.Remove(old);
		}
		TrampledOld.Clear();
	}

	// ------------------------------------------------------------------ Ausdauerlauf

	private static Character? freeRunMount;
	private static float freeRunCheck;
	private static bool freeRunClear;

	/// <summary>
	/// Sadle.UseStamina (beim Besitzer, dem Reiter): Rennen kostet nichts, solange kein Gegner im Umkreis ist (alle 0,5 s
	/// geprüft). Schwimmen kostet weiter. Ohne Verbrauch läuft auch Valheims Erholung weiter (UseStamina setzt dann keine Pause).
	/// </summary>
	internal static bool FreeRun(Sadle sadle)
	{
		Character? animal = sadle.m_character;
		if (animal == null || animal.IsSwimming())
		{
			return false;
		}
		BondStats bond = Saddles.Bond(animal);
		if (bond.MountFreeRun <= 0f)
		{
			return false;
		}
		if (animal != freeRunMount || Time.time >= freeRunCheck)
		{
			freeRunMount = animal;
			freeRunCheck = Time.time + 0.5f;
			freeRunClear = !EnemyNear(animal, bond.MountFreeRun);
		}
		return freeRunClear;
	}

	/// <summary>Gegner im Umkreis: wilde Kreaturen mit Monster-KI, die für das Reittier Feinde sind (Hirsch und Hase zählen nicht).</summary>
	private static bool EnemyNear(Character animal, float range)
	{
		float range2 = range * range;
		Vector3 origin = animal.transform.position;
		foreach (Character other in Character.GetAllCharacters())
		{
			if (other != null && other != animal && !other.IsDead() && !other.IsPlayer() && !other.IsTamed() && other.m_baseAI is MonsterAI
				&& (other.transform.position - origin).sqrMagnitude < range2 && BaseAI.IsEnemy(animal, other))
			{
				return true;
			}
		}
		return false;
	}

	// ------------------------------------------------------------------ Feuerschreiter und Totenritt (Schutz)

	/// <summary>
	/// Character.RPC_Damage (beim Besitzer des Getroffenen): Feuerschreiter lässt Lava und kochendes Meer gar nicht treffen
	/// (deren Schaden ist allgemeiner Schaden, kein Feuer) und nimmt Feuer und Brennen weg; Totenritt nimmt Gift und Vergiften weg.
	/// false: kein Treffer.
	/// </summary>
	internal static bool OnHit(Character target, HitData hit)
	{
		if (!LeashMagic.Available)
		{
			return true;
		}
		BondStats bond = RideBond(target);
		if (bond.MountFireproof)
		{
			if (hit.m_hitType == HitData.HitType.AshlandsLava || hit.m_hitType == HitData.HitType.AshlandsOcean)
			{
				return false;
			}
			hit.m_damage.m_fire = 0f;
			if (hit.m_statusEffectHash == SEMan.s_statusEffectBurning)
			{
				hit.m_statusEffectHash = 0;
			}
		}
		if (bond.MountDeathRide > 0f)
		{
			hit.m_damage.m_poison = 0f;
			if (hit.m_statusEffectHash == SEMan.s_statusEffectPoison)
			{
				hit.m_statusEffectHash = 0;
			}
		}
		return true;
	}

	/// <summary>Character.ApplyDamage: Brand und Gift ticken an Valheims RPC_Damage vorbei (SE_Burning, SE_Poison) und kommen hier an.</summary>
	internal static bool OnDamage(Character target, HitData hit)
	{
		if (!LeashMagic.Available)
		{
			return true;
		}
		BondStats bond = RideBond(target);
		if (bond.MountFireproof)
		{
			if (hit.m_hitType == HitData.HitType.AshlandsLava || hit.m_hitType == HitData.HitType.AshlandsOcean)
			{
				return false;
			}
			hit.m_damage.m_fire = 0f;
		}
		if (bond.MountDeathRide > 0f)
		{
			hit.m_damage.m_poison = 0f;
		}
		return true;
	}

	/// <summary>StatusEffect.CanAdd: kein Brennen (auch nicht von der Lava-Hitze) mit Feuerschreiter, kein Gift mit Totenritt.</summary>
	internal static bool Blocks(StatusEffect effect, Character? character)
	{
		if (character == null || !LeashMagic.Available)
		{
			return false;
		}
		int hash = effect.NameHash();
		if (hash != SEMan.s_statusEffectBurning && hash != SEMan.s_statusEffectPoison)
		{
			return false;
		}
		BondStats bond = RideBond(character);
		return hash == SEMan.s_statusEffectBurning ? bond.MountFireproof : bond.MountDeathRide > 0f;
	}

	/// <summary>
	/// Totenritt: Gegner bemerken den Reiter schwerer. Valheims Sicht (BaseAI.CanSeeTarget, MonsterAI-Alarm) rechnet mit dem
	/// Schleich-Faktor des Spielers; SEMan.ModifyStealth wirkt nur beim Schleichen, deshalb hier: gleich nach jeder Neuberechnung
	/// (alle 0,5 s, Timer dann genau 0) das Ziel einmal senken. Der Wert geht wie bei Valheim ins Spieler-ZDO (s_stealth).
	/// </summary>
	internal static void Stealth(Player player)
	{
		if (player != Player.m_localPlayer || player.m_stealthFactorUpdateTimer != 0f)
		{
			return;
		}
		float deathRide = LocalRide.MountDeathRide;
		if (deathRide > 0f)
		{
			player.m_stealthFactorTarget *= Mathf.Clamp01(1f - deathRide / 100f);
		}
	}

	// ------------------------------------------------------------------ Berserkerritt

	/// <summary>SEMan.ModifyAttack (beim Besitzer des Angreifers): unter 50 % Leben mehr Schaden, für Reiter und Reittier je für sich.</summary>
	internal static void Berserk(Character attacker, HitData hit)
	{
		if (!LeashMagic.Available)
		{
			return;
		}
		BondStats bond = RideBond(attacker);
		if (bond.MountBerserk > 0f && attacker.GetHealthPercentage() < 0.5f)
		{
			hit.m_damage.Modify(1f + bond.MountBerserk / 100f);
		}
	}

	// ------------------------------------------------------------------ Herdenfrieden

	private static float herdCheck;
	private static bool herdAnywhere;

	/// <summary>
	/// BaseAI.IsEnemy (beim Besitzer der wilden Lox): Wilde Lox und Reiter samt Reittier mit Herdenfrieden sind keine Feinde, in
	/// beide Richtungen (das Reittier fängt auch keinen Streit an). Schnelle Vorprüfung: Reitet überhaupt jemand so?
	/// </summary>
	internal static bool HerdPeace(Character a, Character b)
	{
		if (!HerdAnywhere() || a == null || b == null)
		{
			return false;
		}
		return IsWildLox(a) ? InHerd(b) : IsWildLox(b) && InHerd(a);
	}

	private static bool HerdAnywhere()
	{
		if (Time.time >= herdCheck)
		{
			herdCheck = Time.time + 0.5f;
			herdAnywhere = false;
			foreach (Player player in Player.GetAllPlayers())
			{
				ZNetView? view = player != null ? player.m_nview : null;
				if (view != null && view.IsValid() && view.GetZDO().GetBool(HerdHash))
				{
					herdAnywhere = true;
					break;
				}
			}
		}
		return herdAnywhere;
	}

	private static bool IsWildLox(Character character)
	{
		if (character.IsPlayer() || character.IsTamed())
		{
			return false;
		}
		ZNetView? view = character.m_nview;
		ZDO? zdo = view != null ? view.GetZDO() : null;
		int prefab = zdo != null ? zdo.GetPrefab() : 0;
		return prefab == LoxHash || prefab == LoxCalfHash;
	}

	private static bool InHerd(Character character)
	{
		if (character is Player player)
		{
			ZNetView? view = player.m_nview;
			return view != null && view.IsValid() && view.GetZDO().GetBool(HerdHash);
		}
		return MountBond(character).MountHerdPeace;
	}

	// ------------------------------------------------------------------ Funkenflug

	private static Character? sprinting;
	private static float sprintUntil;
	private static float sprintReady;

	/// <summary>SEMan.ApplyStatusEffectSpeedMods (beim Besitzer des Reittiers, dem Reiter): doppelt so schnell, solange der Funkenflug läuft.</summary>
	internal static bool Sprinting(Character? character)
	{
		return Time.time < sprintUntil && character != null && character == sprinting && character == LocalMount; // erst der billige Zeitvergleich: läuft für jede Kreatur
	}

	private static bool CanSprint()
	{
		Refresh();
		return mount != null && ride.MountSprint > 0f && !mount.IsDead();
	}

	private static void StartSprint()
	{
		Refresh();
		if (mount == null || ride.MountSprint <= 0f)
		{
			return;
		}
		sprinting = mount;
		sprintUntil = Time.time + ride.MountSprint;
	}

	/// <summary>Ersatz ohne EpicLoots Fähigkeiten: eigene Taste, eigene Abklingzeit.</summary>
	private static void SprintByKey()
	{
		if (Time.time < sprintReady)
		{
			Gc.TopLeft(GcLoc.Get("gcsd_sprint_wait", Mathf.CeilToInt(sprintReady - Time.time)));
			return;
		}
		if (CanSprint())
		{
			StartSprint();
			sprintReady = Time.time + SprintCooldown;
		}
	}

	// ------------------------------------------------------------------ EpicLoot: Fähigkeit und gerittener Sattel als ausgerüstet

	private static bool abilityReady;
	private static bool providerReady;
	private static MethodInfo? invalidateCache;
	private static Type? abilityController;
	private static MethodInfo? updateAbilities;
	private static readonly List<ItemDrop.ItemData> RiddenItems = new();
	private static readonly List<ItemDrop.ItemData> NoItems = new();
	private static string? providedData;

	/// <summary>
	/// EpicLoot 0.15.2 (Gorduan, 27.10.2026: „alle aktuellen Tools und Fertigkeiten für das aktuelle EpicLoot umsetzen“):
	///  * API.RegisterEquipmentProvider: Der gerittene Sattel gilt als ausgerüstet. EpicLoots AbilityController sucht
	///    Fähigkeiten in den aktiven Effekten aller ausgerüsteten Items (GetAllActiveMagicEffects, samt Anbietern).
	///  * MagicItemEffectDefinition.Ability: Der Effekt GC_SaddleSprint nennt die Fähigkeit. LeashMagic meldet den Effekt als
	///    JSON ohne dieses Feld an; das Definitions-Objekt bleibt bei jedem Neuladen dasselbe (API.ReloadExternalMagicEffects),
	///    deshalb wird das Feld einmal hier gesetzt.
	///  * API.RegisterProxyAbility: die Fähigkeit selbst („Activated“, 20 s Abklingzeit, Aktion „Custom“); EpicLoots Leiste
	///    zeigt Symbol, Taste und Abklingzeit, die Abklingzeit steht im Spieler-ZDO (bleibt beim Ab- und Aufsitzen).
	/// Nur beim Spieler (die Leiste gibt es nur dort). Fehlt etwas davon, gilt die eigene Taste (SprintKey).
	/// </summary>
	private static void SetupEpicLoot()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= SetupEpicLoot;
		if (!LeashMagic.Available || Gc.IsHeadless || providerReady)
		{
			return;
		}
		try
		{
			Type? api = AccessTools.TypeByName("EpicLoot.API");
			MethodInfo? provider = ApiMethod(api, "RegisterEquipmentProvider", 2);
			MethodInfo? proxy = ApiMethod(api, "RegisterProxyAbility", 2);
			MethodInfo? asset = ApiMethod(api, "RegisterAsset", 2);
			invalidateCache = ApiMethod(api, "InvalidatePlayerEffectCache", 1);
			abilityController = AccessTools.TypeByName("EpicLoot.Abilities.AbilityController");
			updateAbilities = abilityController != null ? AccessTools.Method(abilityController, "UpdatePlayerAbilities") : null;
			if (provider == null)
			{
				ServerPlugin.Log.LogInfo($"Sättel: EpicLoot ohne Ausrüstungs-Anbieter, der Funkenflug geht über die eigene Taste ({SprintKey.Value}).");
				return;
			}
			providerReady = provider.Invoke(null, new object[] { ProviderId, new Func<Player, List<ItemDrop.ItemData>>(Provided) }) is true;
			abilityReady = providerReady && proxy != null && updateAbilities != null && BindSprintAbility() && RegisterSprint(proxy, asset);
			ServerPlugin.Log.LogInfo(abilityReady
				? "Sättel: EpicLoot-Fähigkeit Funkenflug angelegt (Taste und Abklingzeit in EpicLoots Fähigkeiten-Leiste), gerittene Sättel gelten als ausgerüstet."
				: $"Sättel: EpicLoot ohne Fähigkeiten-Schnittstelle, der Funkenflug geht über die eigene Taste ({SprintKey.Value}).");
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Sättel: EpicLoot-Fähigkeit nicht angelegt, der Funkenflug geht über die eigene Taste: {e.GetBaseException().Message}");
		}
	}

	private static MethodInfo? ApiMethod(Type? api, string name, int parameters)
	{
		if (api == null)
		{
			return null;
		}
		foreach (MethodInfo method in api.GetMethods(BindingFlags.Public | BindingFlags.Static))
		{
			if (method.Name == name && method.GetParameters().Length == parameters)
			{
				return method;
			}
		}
		return null;
	}

	/// <summary>Effekt GC_SaddleSprint → Fähigkeit GC_SaddleSprint (MagicItemEffectDefinition.Ability).</summary>
	private static bool BindSprintAbility()
	{
		Type? definitions = AccessTools.TypeByName("EpicLoot.MagicItemEffectDefinitions");
		Type? definition = AccessTools.TypeByName("EpicLoot.MagicItemEffectDefinition");
		FieldInfo? all = definitions != null ? AccessTools.Field(definitions, "AllDefinitions") : null;
		FieldInfo? ability = definition != null ? AccessTools.Field(definition, "Ability") : null;
		if (ability == null || all?.GetValue(null) is not IDictionary dictionary || !dictionary.Contains(SprintAbility) || dictionary[SprintAbility] is not object sprint)
		{
			return false;
		}
		ability.SetValue(sprint, SprintAbility);
		return true;
	}

	/// <summary>Symbol (das des Asksvin-Sattels) und die Fähigkeit mit Rückrufen: nur beim Reiten mit Funkenflug, dann losrennen.</summary>
	private static bool RegisterSprint(MethodInfo proxy, MethodInfo? asset)
	{
		string icon = "BerserkerIcon"; // Ersatz: EpicLoots eigenes Symbol (im Asset-Bundle); ein leerer Name ließe die Leiste jedes Bild neu laden und Fehler loggen
		GameObject? prefab = PrefabManager.Instance.GetPrefab("SaddleAsksvin");
		ItemDrop? item = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
		Sprite? sprite = item != null ? item.m_itemData.GetIcon() : null;
		if (asset != null && sprite != null)
		{
			Sprite named = sprite;
			try
			{
				// eigener Name: EpicLoots Leiste lädt das Symbol neu, solange Name und Asset-Name verschieden sind
				named = Sprite.Create(sprite.texture, sprite.textureRect, new Vector2(0.5f, 0.5f), sprite.pixelsPerUnit);
				named.name = SprintIcon;
			}
			catch (Exception)
			{
				named = sprite;
			}
			if (asset.Invoke(null, new object[] { SprintIcon, named }) is true)
			{
				icon = SprintIcon;
			}
		}
		string json = "{\"ID\":\"" + SprintAbility + "\",\"IconAsset\":\"" + icon + "\",\"ActivationMode\":\"Activated\",\"Cooldown\":"
			+ SprintCooldown.ToString("0.###", CultureInfo.InvariantCulture) + ",\"Action\":\"Custom\",\"ActionParams\":[]}";
		Dictionary<string, Delegate> callbacks = new()
		{
			["CanActivate"] = new Func<bool>(CanSprint),
			["ActivateCustomAction"] = new Action(StartSprint),
		};
		return proxy.Invoke(null, new object[] { json, callbacks }) != null;
	}

	/// <summary>Anbieter für EpicLoot: der gerittene Sattel des eigenen Spielers (ein Item aus dem Tier-ZDO, gecacht).</summary>
	private static List<ItemDrop.ItemData> Provided(Player player) => player != null && player == Player.m_localPlayer ? RiddenItems : NoItems;

	/// <summary>Sattel-Item aus dem Tier-ZDO (gc_saddle_data, wie beim Absatteln) neu bilden, wenn sich Ritt oder Sattel ändern.</summary>
	private static bool UpdateProvided(Character? animal)
	{
		string data = "";
		Tameable? tame = null;
		BaseAI? ai = animal != null ? animal.m_baseAI : null;
		ZNetView? view = animal != null ? animal.m_nview : null;
		if (ai != null && ai.m_tamable != null && ai.m_tamable.m_saddleItem != null && view != null && view.IsValid())
		{
			tame = ai.m_tamable;
			data = view.GetZDO().GetString(Saddles.DataHash);
		}
		if (providedData != null && data == providedData)
		{
			return false;
		}
		providedData = data;
		RiddenItems.Clear();
		if (data.Length > 0 && tame != null)
		{
			ItemDrop.ItemData item = tame.m_saddleItem.m_itemData.Clone();
			item.m_dropPrefab = tame.m_saddleItem.gameObject;
			Saddles.Unpack(data, item);
			RiddenItems.Add(item);
		}
		return true;
	}

	/// <summary>EpicLoot neu rechnen lassen: Effekt-Zwischenspeicher und Fähigkeiten (sonst nur beim An- und Ablegen von Ausrüstung).</summary>
	private static void RefreshAbilities(Player player)
	{
		invalidateCache?.Invoke(null, new object[] { player });
		if (abilityController != null && updateAbilities != null && player.GetComponent(abilityController) is Component controller && controller != null)
		{
			updateAbilities.Invoke(controller, null);
		}
	}

	// ------------------------------------------------------------------ Sattel-Beute: Bärensättel nur, wenn Bären zähmbar sind

	private static FieldInfo? leveledField;
	private static FieldInfo? flatLootField;
	private static FieldInfo? levelLootField;
	private static FieldInfo? itemField;
	private static readonly ConditionalWeakTable<object, object> BearTables = new();
	private static int rolling;

	/// <summary>
	/// Bär- und Unbjørn-Sättel fallen nur, wenn Bären zähmbar sind (Einstellung vom Server, LeashMagic.SpeciesEnabled, zur
	/// Laufzeit). EpicLoot 0.15.2 würfelt jede Tabelle für sich (LootRoller.RollLootTableInternal, private Fassung mit einer
	/// Tabelle; Kreaturen, Kisten, Schatzkarten, Glücksspiel, Konsole) und überspringt dort Einträge seiner Sperrliste
	/// (LootDenyList.IsDenied), bevor es Splitter, Material oder Items daraus macht. Zwei Haken:
	///  * eigene Tabellen nur mit Bärensätteln (AppendSaddleLoot) gar nicht würfeln;
	///  * während gewürfelt wird, stehen die Bärensättel auf der Sperrliste (falls sie über Item-Sets oder fremde Tabellen kommen).
	///    Nur während des Würfelns: EpicLoot räumt beim Laden gesperrte Einträge aus seiner Beute-Config (AutoAddEnchantableItems).
	/// API.AddLootGeneratedListener kann nichts verwerfen: Es meldet das Item erst, wenn es schon in der Welt liegt.
	/// </summary>
	private static void PatchLoot()
	{
		if (!Chainloader.PluginInfos.ContainsKey(LeashMagic.EpicLootGuid))
		{
			return;
		}
		try
		{
			Type? roller = AccessTools.TypeByName("EpicLoot.LootRoller");
			Type? table = AccessTools.TypeByName("EpicLoot.LootTable");
			Type? level = AccessTools.TypeByName("EpicLoot.LeveledLootDef");
			Type? drop = AccessTools.TypeByName("EpicLoot.LootDrop");
			Type? denyList = AccessTools.TypeByName("EpicLoot.LootDenyList");
			leveledField = table != null ? AccessTools.Field(table, "LeveledLoot") : null;
			flatLootField = table != null ? AccessTools.Field(table, "Loot") : null;
			levelLootField = level != null ? AccessTools.Field(level, "Loot") : null;
			itemField = drop != null ? AccessTools.Field(drop, "Item") : null;
			MethodInfo? isDenied = denyList != null ? AccessTools.Method(denyList, "IsDenied", new[] { typeof(string) }) : null;
			MethodInfo? rollOne = null;
			if (roller != null && table != null)
			{
				foreach (MethodInfo method in roller.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
				{
					ParameterInfo[] parameters = method.GetParameters();
					if (method.Name == "RollLootTableInternal" && parameters.Length == 5 && parameters[0].ParameterType == table)
					{
						rollOne = method;
					}
				}
			}
			Harmony harmony = new(ModTiere.GcMod.Guid);
			// zuerst die Sperrliste: Ein gepatchter Aufruf wird danach nicht mehr eingebettet, auch nicht in RollLootTableInternal
			bool denied = TryPatch(harmony, isDenied, postfix: nameof(DeniedPostfix));
			bool rolled = rollOne != null && leveledField != null && levelLootField != null && itemField != null
				&& TryPatch(harmony, rollOne, prefix: nameof(RollPrefix), finalizer: nameof(RollFinalizer));
			if (denied && rolled)
			{
				ServerPlugin.Log.LogInfo("Sättel: Bär- und Unbjørn-Sättel fallen nur, wenn Bären zähmbar sind (EpicLoot-Beute).");
			}
			else
			{
				ServerPlugin.Log.LogWarning($"Sättel: Beute-Sperre für Bärensättel nur teilweise angebunden (Sperrliste {denied}, Tabellen {rolled}); sie könnten fallen, obwohl Bären nicht zähmbar sind.");
			}
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Sättel: Beute-Sperre für Bärensättel nicht angebunden: {e.GetBaseException().Message}");
		}
	}

	private static bool TryPatch(Harmony harmony, MethodBase? original, string? prefix = null, string? postfix = null, string? finalizer = null)
	{
		if (original == null)
		{
			return false;
		}
		try
		{
			harmony.Patch(original,
				prefix: prefix != null ? new HarmonyMethod(typeof(SaddleUniqueEffects), prefix) : null,
				postfix: postfix != null ? new HarmonyMethod(typeof(SaddleUniqueEffects), postfix) : null,
				finalizer: finalizer != null ? new HarmonyMethod(typeof(SaddleUniqueEffects), finalizer) : null);
			return true;
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Sättel: EpicLoot-Haken {original.DeclaringType?.Name}.{original.Name} nicht angebunden: {e.GetBaseException().Message}");
			return false;
		}
	}

	private static bool BearsTameable() => LeashMagic.SpeciesEnabled == null || LeashMagic.SpeciesEnabled("Bjorn");

	private static bool IsBearSaddle(string? prefab) => prefab == Saddles.BearSaddle || prefab == Saddles.UnbjornSaddle;

	/// <summary>Vor jeder Tabelle: mitzählen (Sperrliste gilt nur dabei); eine Tabelle nur mit Bärensätteln ohne zähmbare Bären auslassen.</summary>
	private static bool RollPrefix(object lootTable)
	{
		rolling++;
		return BearsTameable() || !OnlyBearSaddles(lootTable);
	}

	private static void RollFinalizer()
	{
		if (rolling > 0)
		{
			rolling--;
		}
	}

	private static void DeniedPostfix(string prefabName, ref bool __result)
	{
		if (!__result && rolling > 0 && IsBearSaddle(prefabName) && !BearsTameable())
		{
			__result = true;
		}
	}

	/// <summary>Enthält die Tabelle nur Bärensättel (je Tabellen-Objekt einmal geprüft; EpicLoot legt beim Neuladen neue an)?</summary>
	private static bool OnlyBearSaddles(object? lootTable)
	{
		if (lootTable == null || leveledField == null || levelLootField == null || itemField == null)
		{
			return false;
		}
		if (BearTables.TryGetValue(lootTable, out object known))
		{
			return known is true;
		}
		int bear = 0;
		int other = 0;
		Count(flatLootField?.GetValue(lootTable) as IEnumerable, ref bear, ref other); // alte flache Form, falls noch nicht umgestellt
		if (leveledField.GetValue(lootTable) is IEnumerable levels)
		{
			foreach (object? level in levels)
			{
				if (level != null)
				{
					Count(levelLootField.GetValue(level) as IEnumerable, ref bear, ref other);
				}
			}
		}
		bool only = bear > 0 && other == 0;
		BearTables.Add(lootTable, only);
		return only;
	}

	private static void Count(IEnumerable? drops, ref int bear, ref int other)
	{
		if (drops == null)
		{
			return;
		}
		foreach (object? drop in drops)
		{
			if (drop == null)
			{
				continue;
			}
			if (IsBearSaddle(itemField!.GetValue(drop) as string))
			{
				bear++;
			}
			else
			{
				other++;
			}
		}
	}
}

// ---------------------------------------------------------------------- Patches

/// <summary>Jedes Bild beim eigenen Spieler: Absteigen, Herdenfrieden im ZDO, Brand und Gift, EpicLoot-Fähigkeit, Ersatztaste.</summary>
[HarmonyPatch(typeof(Player), "Update")]
internal static class SaddleUniqueTickPatch
{
	private static void Postfix(Player __instance)
	{
		if (__instance == Player.m_localPlayer)
		{
			SaddleUniqueEffects.Tick();
		}
	}
}

/// <summary>Sprungkraft und Kriegsreittier: Sprung- und Angriffstaste beim Reiten lassen das Reittier springen bzw. angreifen.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
internal static class SaddleUniqueControlsPatch
{
	private static void Prefix(Player __instance, ref bool attack, ref bool attackHold, ref bool jump)
	{
		if ((jump || attack || attackHold) && __instance == Player.m_localPlayer)
		{
			SaddleUniqueEffects.OnControls(__instance, ref attack, ref attackHold, ref jump);
		}
	}
}

/// <summary>Himmelsritt (Doppelsprung, Gleiten) und Niederreiten: jeder Physikschritt des eigenen Reittiers.</summary>
[HarmonyPatch(typeof(Sadle), "FixedUpdate")]
internal static class SaddleUniqueFixedPatch
{
	private static void Postfix(Sadle __instance) => SaddleUniqueEffects.FixedTick(__instance);
}

/// <summary>Kriegsreittier: Nach einem Angriff schaut das Tier kurz zum Ziel, auch wenn der Reiter weiterlenkt.</summary>
[HarmonyPatch(typeof(Sadle), nameof(Sadle.UpdateRiding))]
internal static class SaddleUniqueFacingPatch
{
	private static void Postfix(Sadle __instance) => SaddleUniqueEffects.KeepFacing(__instance);
}

/// <summary>Ausdauerlauf: Rennen kostet nichts, solange kein Gegner in der Nähe ist.</summary>
[HarmonyPatch(typeof(Sadle), "UseStamina")]
internal static class SaddleUniqueFreeRunPatch
{
	private static void Prefix(Sadle __instance, ref float v)
	{
		if (v > 0f && SaddleUniqueEffects.FreeRun(__instance))
		{
			v = 0f;
		}
	}
}

/// <summary>Funkenflug: das Reittier doppelt so schnell.</summary>
[HarmonyPatch(typeof(SEMan), nameof(SEMan.ApplyStatusEffectSpeedMods))]
internal static class SaddleUniqueSprintPatch
{
	private static void Postfix(SEMan __instance, ref float speed)
	{
		if (SaddleUniqueEffects.Sprinting(__instance.m_character))
		{
			speed *= SaddleUniqueEffects.SprintFactor;
		}
	}
}

/// <summary>Berserkerritt: Angriffe von Reiter und Reittier unter 50 % Leben.</summary>
[HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyAttack))]
internal static class SaddleUniqueBerserkPatch
{
	private static void Postfix(SEMan __instance, ref HitData hitData)
	{
		Character character = __instance.m_character;
		if (character != null && hitData != null)
		{
			SaddleUniqueEffects.Berserk(character, hitData);
		}
	}
}

/// <summary>Sprungkraft: kein Sturzschaden für den Reiter (Valheim 1.0.20 gibt Kreaturen gar keinen Sturzschaden).</summary>
[HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyFallDamage))]
internal static class SaddleUniqueFallPatch
{
	private static void Postfix(SEMan __instance, ref float damage)
	{
		if (damage > 0f && SaddleUniqueEffects.NoFallDamage(__instance.m_character))
		{
			damage = 0f;
		}
	}
}

/// <summary>Feuerschreiter und Totenritt: Treffer gegen Reiter und Reittier (nach den anderen Prefixen, falls einer etwas dazugibt).</summary>
[HarmonyPatch(typeof(Character), nameof(Character.RPC_Damage))]
internal static class SaddleUniqueHitPatch
{
	[HarmonyPriority(Priority.Last)]
	private static bool Prefix(Character __instance, HitData hit) => hit == null || SaddleUniqueEffects.OnHit(__instance, hit);
}

/// <summary>Feuerschreiter und Totenritt: Brand und Gift, die ticken (an RPC_Damage vorbei).</summary>
[HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
internal static class SaddleUniqueDamagePatch
{
	[HarmonyPriority(Priority.Last)]
	private static bool Prefix(Character __instance, HitData hit) => hit == null || SaddleUniqueEffects.OnDamage(__instance, hit);
}

/// <summary>Feuerschreiter und Totenritt: kein Brennen (auch nicht von der Lava-Hitze) bzw. kein Gift als Statuseffekt.</summary>
[HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.CanAdd))]
internal static class SaddleUniqueStatusPatch
{
	private static void Postfix(StatusEffect __instance, Character character, ref bool __result)
	{
		if (__result && SaddleUniqueEffects.Blocks(__instance, character))
		{
			__result = false;
		}
	}
}

/// <summary>Totenritt: Gegner bemerken den Reiter schwerer (Schleich-Faktor, auch ohne zu schleichen).</summary>
[HarmonyPatch(typeof(Player), nameof(Player.UpdateStealth))]
internal static class SaddleUniqueStealthPatch
{
	private static void Postfix(Player __instance) => SaddleUniqueEffects.Stealth(__instance);
}

/// <summary>Herdenfrieden: wilde Lox und wer auf einem Lox mit diesem Sattel reitet, sind keine Feinde.</summary>
[HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IsEnemy), typeof(Character), typeof(Character))]
internal static class SaddleUniqueHerdPatch
{
	private static void Postfix(Character a, Character b, ref bool __result)
	{
		if (__result && SaddleUniqueEffects.HerdPeace(a, b))
		{
			__result = false;
		}
	}
}
