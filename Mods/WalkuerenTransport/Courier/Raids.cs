using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Courier;

/// <summary>
/// Raid nach dem Abholen (Gorduan, 19.10.2026): Ist die Walküre mit dem Karren fort, beginnt dort ein zufälliger Raid, der in
/// der Welt gerade möglich ist (Valheims eigene Regeln: Bosse besiegt bzw. bei „Raids je Spieler“ die Fortschritte der
/// Spieler am Karren, Biom), aber ohne Valheims Bedingung, dass Gebäude in der Nähe stehen. Mit Easteregg: Kampfhühner.
/// Valheim kennt nur einen Raid zugleich: Läuft schon einer, wartet dieser (höchstens 10 Minuten). Ist niemand mehr am
/// Karren, fällt er aus (ein Raid ohne Spieler bliebe in Valheim stehen und sperrte alle anderen). Läuft er, hält er nicht
/// an, wenn alle fortgehen: Er läuft seine Zeit ab, dann ist die Fracht frei.
/// Gorduan: Event 2 erst, wenn der Raid vorbei ist. Die Fracht bleibt so lange gesperrt (CargoState.Locked); entfällt der
/// Raid, wird sie sofort freigegeben.
/// </summary>
internal static class Raids
{
	private const float PlayerRange = 96f; // Valheims Raid-Umkreis

	private sealed class Pending
	{
		public Vector3 Position;
		public bool Easter;
		public double Since;
		public ZDOID Cargo;
	}

	/// <summary>Laufender Raid nach einem Abholen: welche Fracht gesperrt ist, bis er vorbei ist.</summary>
	private sealed class Running
	{
		public string Name = "";
		public Vector3 Position;
		public ZDOID Cargo;
	}

	private static readonly List<Pending> Queue = new();
	private static readonly List<Running> Active = new();
	private static double nextCheck;

	internal static void Reset()
	{
		Queue.Clear();
		Active.Clear();
		nextCheck = 0.0;
	}

	internal static void After(Vector3 position, bool easter, ZDOID cargo)
	{
		Queue.Add(new Pending { Position = position, Easter = easter, Since = Transport.Now, Cargo = cargo });
	}

	internal static void ServerTick(double now)
	{
		if ((Queue.Count == 0 && Active.Count == 0) || now < nextCheck)
		{
			return;
		}
		nextCheck = now + 2.0;
		RandEventSystem? events = RandEventSystem.instance;
		if (events == null)
		{
			return;
		}
		RandomEvent? currentEvent = events.GetCurrentRandomEvent();
		foreach (Running running in Active.ToArray())
		{
			// vorbei, wenn Valheims laufender Raid nicht mehr dieser ist (Zeit um oder abgebrochen)
			if (currentEvent == null || currentEvent.m_name != running.Name || Utils.DistanceXZ(currentEvent.m_pos, running.Position) > 1f)
			{
				Active.Remove(running);
				Transport.Unlock(running.Cargo, true);
			}
		}
		if (Queue.Count == 0)
		{
			return;
		}
		Pending raid = Queue[0];
		if (now - raid.Since > 600.0)
		{
			Queue.RemoveAt(0);
			ServerPlugin.Log.LogInfo("Walküren-Transport: Raid nach dem Abholen entfällt, es lief zu lange ein anderer.");
			Transport.Unlock(raid.Cargo, false);
			return;
		}
		if (events.GetCurrentRandomEvent() != null)
		{
			return; // Valheim kennt nur einen Raid zugleich
		}
		Queue.RemoveAt(0);
		bool started = false;
		try
		{
			started = Start(events, raid);
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Walküren-Transport: Raid nicht gestartet: {e}");
		}
		if (!started)
		{
			Transport.Unlock(raid.Cargo, false); // kein Raid: der Karren lässt sich gleich rufen
		}
	}

	private static bool Start(RandEventSystem events, Pending raid)
	{
		if (!AnyPlayerNear(raid.Position))
		{
			ServerPlugin.Log.LogInfo("Walküren-Transport: Raid nach dem Abholen entfällt, niemand ist mehr am Karren.");
			return false;
		}
		if (Game.m_eventRate <= 0f)
		{
			ServerPlugin.Log.LogInfo("Walküren-Transport: kein Raid, die Welt-Einstellung schaltet Raids aus.");
			return false;
		}
		RandomEvent? chosen = raid.Easter ? events.GetEvent(RaidHens.EventName) : Choose(events, raid.Position);
		if (chosen == null)
		{
			ServerPlugin.Log.LogInfo($"Walküren-Transport: kein Raid möglich ({(raid.Easter ? "Kampfhühner fehlen" : "Weltstand, Einstellungen")}).");
			return false;
		}
		events.SetRandomEvent(chosen, raid.Position);
		RandomEvent? running = events.GetCurrentRandomEvent();
		if (running != null)
		{
			// Valheim hielte den Raid an, solange niemand dort ist, und die Fracht bliebe gesperrt (Prüfung 2). Das ist die
			// Kopie des Servers, die Spieler achten nicht darauf.
			running.m_pauseIfNoPlayerInArea = false;
		}
		Transport.AnnounceRaid(raid.Position, chosen.m_name);
		Active.Add(new Running { Name = chosen.m_name, Position = raid.Position, Cargo = raid.Cargo });
		ServerPlugin.Log.LogInfo($"Walküren-Transport: Raid „{chosen.m_name}“ bei {raid.Position:F0}, der Karren lässt sich danach rufen.");
		return true;
	}

	/// <summary>Zufällig einer der Raids, die Valheim gerade wählen könnte, ohne die Gebäude-Bedingung; lieber einer fürs Biom.</summary>
	private static RandomEvent? Choose(RandEventSystem events, Vector3 position)
	{
		RandEventSystem.RefreshPlayerEventData();
		List<RandEventSystem.PlayerEventData> players = RandEventSystem.s_playerEventDatas;
		bool perPlayer = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.PlayerEvents);
		List<RandomEvent> inBiome = new();
		foreach (RandomEvent ev in events.m_events)
		{
			if (ev == null || !ev.m_enabled || ev.m_devDisabled || !ev.m_random || ev.m_name == RaidHens.EventName)
			{
				continue;
			}
			if (!events.HaveGlobalKeys(ev, players))
			{
				continue;
			}
			if (perPlayer && !players.Any(p => Utils.DistanceXZ(p.position, position) < 150f && p.possibleEvents != null && p.possibleEvents.Contains(ev.m_name)))
			{
				continue; // „Raids je Spieler“: nur was die Spieler am Karren schon freigeschaltet haben
			}
			if (events.InValidBiome(ev, position))
			{
				inBiome.Add(ev);
			}
		}
		// nur Raids fürs Biom: Valheim lässt die Gegner eines Raids nur in seinen Biomen erscheinen (Prüfung 19.10.)
		return inBiome.Count > 0 ? inBiome[UnityEngine.Random.Range(0, inBiome.Count)] : null;
	}

	private static bool AnyPlayerNear(Vector3 position)
	{
		if (Player.m_localPlayer != null && Utils.DistanceXZ(Player.m_localPlayer.transform.position, position) < PlayerRange)
		{
			return true;
		}
		foreach (ZDO zdo in ZNet.instance.GetAllCharacterZDOS())
		{
			if (Utils.DistanceXZ(zdo.GetPosition(), position) < PlayerRange && zdo.GetPosition().y < 3000f)
			{
				return true;
			}
		}
		return false;
	}
}

/// <summary>
/// Kampfhühner (Gorduan, 19.10.2026, Easteregg): ein besonderer Raid aus Hühnern, die mit dem Schaden von Wildschweinen
/// kämpfen, nicht zähmbar sind und keine Beute hinterlassen. Ein eigenes Tier „GC_RaidHen“ (Valheims Henne mit der
/// Angriffs-KI und den Werten des Wildschweins, ohne Zähmen, Eier und Beute) und ein eigener Raid „gc_hen_raid“, den nur
/// der Walküren-Transport startet. Hennen haben keine Angriffs-Animation: Der Angriff des Wildschweins wird beim Huhn als
/// Pick-Sprung ausgeführt (RaidHenAttackPatch).
/// </summary>
internal static class RaidHens
{
	internal const string Prefab = "GC_RaidHen";
	internal const string EventName = "gc_hen_raid";
	private static GameObject? prefab;
	private static readonly Dictionary<int, float> LastCluck = new();

	internal static void Init()
	{
		PrefabManager.OnVanillaPrefabsAvailable += CreatePrefab;
	}

	private static void CreatePrefab()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= CreatePrefab;
		try
		{
			GameObject? boar = PrefabManager.Instance.GetPrefab("Boar");
			GameObject? hen = PrefabManager.Instance.CreateClonedPrefab(Prefab, "Hen");
			if (hen == null || boar == null)
			{
				ServerPlugin.Log.LogWarning("Kampfhühner: Henne oder Wildschwein fehlt, der Hühner-Raid entfällt.");
				return;
			}
			// nicht zähmbar, keine Eier, keine Beute (Gorduan)
			foreach (Type type in new[] { typeof(Tameable), typeof(Procreation), typeof(CharacterDrop), typeof(Growup), typeof(EggGrow) })
			{
				foreach (Component part in hen.GetComponents(type))
				{
					UnityEngine.Object.DestroyImmediate(part);
				}
			}
			Character? boarCharacter = boar.GetComponent<Character>();
			Humanoid? boarHumanoid = boar.GetComponent<Humanoid>();
			MonsterAI? boarAi = boar.GetComponent<MonsterAI>();
			Humanoid? humanoid = MakeHumanoid(hen);
			MonsterAI? ai = hen.GetComponent<MonsterAI>();
			if (humanoid == null || ai == null || boarHumanoid == null || boarAi == null)
			{
				ServerPlugin.Log.LogWarning("Kampfhühner: Henne lässt sich nicht kampffähig machen, der Hühner-Raid entfällt.");
				UnityEngine.Object.DestroyImmediate(hen);
				return;
			}
			humanoid.m_name = "$gc_raidhen";
			humanoid.m_faction = boarCharacter != null ? boarCharacter.m_faction : Character.Faction.ForestMonsters;
			humanoid.m_health = boarCharacter != null ? Mathf.Max(humanoid.m_health, boarCharacter.m_health) : humanoid.m_health;
			humanoid.m_defaultItems = boarHumanoid.m_defaultItems;
			humanoid.m_randomWeapon = Array.Empty<GameObject>();
			humanoid.m_randomArmor = Array.Empty<GameObject>();
			humanoid.m_randomShield = Array.Empty<GameObject>();
			humanoid.m_randomSets = Array.Empty<Humanoid.ItemSet>();
			humanoid.m_tolerateWater = true;
			ai.m_enableHuntPlayer = true;
			ai.m_attackPlayerObjects = false;
			ai.m_viewRange = Mathf.Max(boarAi.m_viewRange, 30f);
			ai.m_hearRange = Mathf.Max(boarAi.m_hearRange, 30f);
			ai.m_alertRange = Mathf.Max(boarAi.m_alertRange, 20f);
			ai.m_fleeIfLowHealth = 0f;
			ai.m_fleeIfNotAlerted = false;
			ai.m_fleeIfHurtWhenTargetCantBeReached = false;
			ai.m_avoidFire = false;
			ai.m_afraidOfFire = false;
			ai.m_consumeItems = new List<ItemDrop>();
			ai.m_circleTargetInterval = boarAi.m_circleTargetInterval;
			ai.m_circleTargetDuration = boarAi.m_circleTargetDuration;
			ai.m_circleTargetDistance = boarAi.m_circleTargetDistance;
			ai.m_interceptTimeMin = boarAi.m_interceptTimeMin;
			ai.m_interceptTimeMax = boarAi.m_interceptTimeMax;
			ai.m_maxChaseDistance = boarAi.m_maxChaseDistance;
			ai.m_minAttackInterval = boarAi.m_minAttackInterval;
			PrefabManager.Instance.AddPrefab(new CustomPrefab(hen, false));
			prefab = hen;
			ServerPlugin.Log.LogInfo($"Kampfhühner: {Prefab} angelegt (Leben {humanoid.m_health:0}, Angriff des Wildschweins).");
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Kampfhühner: nicht angelegt: {e}");
		}
	}

	/// <summary>Die Henne braucht einen Humanoid (Waffen-Wahl der Monster-KI). Ist sie nur ein Character, wird er zum Humanoid mit denselben Werten.</summary>
	private static Humanoid? MakeHumanoid(GameObject hen)
	{
		Character? character = hen.GetComponent<Character>();
		if (character == null)
		{
			return null;
		}
		if (character is Humanoid humanoid)
		{
			return humanoid;
		}
		Humanoid made = hen.AddComponent<Humanoid>();
		for (Type? type = typeof(Character); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
		{
			foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
			{
				field.SetValue(made, field.GetValue(character));
			}
		}
		UnityEngine.Object.DestroyImmediate(character);
		return made;
	}

	/// <summary>Valheims Raid-Liste bekommt den Hühner-Raid (auf Server und Spielern, damit alle ihn beim Namen kennen).</summary>
	internal static void AddEvent(RandEventSystem events)
	{
		if (prefab == null || events.m_events.Any(e => e != null && e.m_name == EventName))
		{
			return;
		}
		RandomEvent raid = new()
		{
			m_name = EventName,
			m_enabled = true,
			m_random = false, // nur der Walküren-Transport startet ihn
			m_duration = 120f,
			m_nearBaseOnly = false,
			m_pauseIfNoPlayerInArea = true,
			m_eventRange = 96f,
			m_spawnerDelay = 2f,
			m_biome = (Heightmap.Biome)0x7FFFFFFF,
			m_startMessage = "$gc_henraid_start",
			m_endMessage = "$gc_henraid_end",
			m_forceMusic = Sounds.MusicBattle,
			m_forceEnvironment = "",
		};
		raid.m_spawn.Add(new SpawnSystem.SpawnData
		{
			m_name = "GC_RaidHens",
			m_enabled = true,
			m_prefab = prefab,
			m_biome = (Heightmap.Biome)0x7FFFFFFF,
			m_biomeArea = Heightmap.BiomeArea.Everything,
			m_maxSpawned = 10,
			m_spawnInterval = 3f,
			m_spawnChance = 100f,
			m_spawnDistance = 4f,
			m_spawnRadiusMin = 8f,
			m_spawnRadiusMax = 25f,
			m_groupSizeMin = 3,
			m_groupSizeMax = 6,
			m_groupRadius = 4f,
			m_spawnAtDay = true,
			m_spawnAtNight = true,
			m_minAltitude = 0f,
			m_maxAltitude = 1000f,
			m_maxTilt = 45f,
			m_inForest = true,
			m_outsideForest = true,
			m_canSpawnCloseToPlayer = true,
			m_insidePlayerBase = true,
			m_huntPlayer = true,
			m_groundOffset = 0.5f,
			m_minLevel = 1,
			m_maxLevel = 3,
		});
		events.m_events.Add(raid);
	}

	private static readonly int PrefabHash = Prefab.GetStableHashCode();

	/// <summary>Kampfhuhn? Über das ZDO, ohne den Objektnamen zu lesen (läuft bei jeder Feind-Prüfung, Prüfung 2).</summary>
	internal static bool Is(Character character)
	{
		ZNetView? nview = character != null ? character.m_nview : null;
		ZDO? zdo = nview != null ? nview.GetZDO() : null;
		return zdo != null && zdo.GetPrefab() == PrefabHash;
	}

	/// <summary>
	/// Angriff des Kampfhuhns (beim Besitzer, wie Valheims KI): Steht das Ziel in Reichweite des Wildschwein-Angriffs, trifft
	/// es mit dessen Schaden (mal Stufe, wie Valheims Angriffe), dazu ein Sprung nach vorn und Gegacker.
	/// </summary>
	internal static bool Peck(Humanoid hen, Character? target)
	{
		ItemDrop.ItemData? weapon = hen.GetCurrentWeapon();
		MonsterAI? ai = hen.GetBaseAI() as MonsterAI;
		Character? victim = target != null ? target : ai?.m_targetCreature;
		if (weapon == null || victim == null || !victim.IsPlayer() || victim.IsDead() || hen.IsDead())
		{
			return false; // Gorduan: Kampfhühner treffen nur Spieler
		}
		float reach = weapon.m_shared.m_aiAttackRange + victim.GetRadius() + hen.GetRadius() + 0.4f;
		Vector3 offset = victim.transform.position - hen.transform.position;
		if (offset.magnitude > reach)
		{
			return false;
		}
		weapon.m_lastAttackTime = Time.time;
		hen.m_timeSinceLastAttack = 0f;
		hen.m_lastCombatTimer = 0f;
		HitData hit = new();
		hit.m_damage = weapon.GetDamage();
		hit.m_damage.Modify(1f + Mathf.Max(0, hen.GetLevel() - 1) * 0.5f);
		hit.m_point = victim.GetCenterPoint();
		hit.m_dir = offset.normalized;
		hit.m_pushForce = weapon.m_shared.m_attackForce;
		hit.m_backstabBonus = weapon.m_shared.m_backstabBonus;
		// wie Valheims Angriff (Attack): blockbar, ausweichbar, Stufe, Taumeln, Statuseffekt
		hit.m_toolTier = (short)weapon.m_shared.m_toolTier;
		hit.m_staggerMultiplier = 1f;
		hit.m_blockable = weapon.m_shared.m_blockable;
		hit.m_dodgeable = weapon.m_shared.m_dodgeable;
		hit.m_skill = weapon.m_shared.m_skillType;
		hit.m_skillLevel = hen.GetSkillLevel(weapon.m_shared.m_skillType);
		hit.m_itemLevel = (short)weapon.m_quality;
		hit.m_statusEffectHash = weapon.m_shared.m_attackStatusEffect != null && (weapon.m_shared.m_attackStatusEffectChance >= 1f || UnityEngine.Random.value < weapon.m_shared.m_attackStatusEffectChance) ? weapon.m_shared.m_attackStatusEffect.NameHash() : 0;
		hit.m_hitType = HitData.HitType.EnemyHit;
		hit.SetAttacker(hen);
		victim.Damage(hit);
		Rigidbody? body = hen.m_body;
		if (body != null)
		{
			body.AddForce(Vector3.up * 3.2f + offset.normalized * 2.2f, ForceMode.VelocityChange); // flatternder Pick-Sprung
		}
		int id = hen.GetInstanceID();
		if (!LastCluck.TryGetValue(id, out float last) || Time.time - last > 1.5f)
		{
			LastCluck[id] = Time.time;
			Show.Local(UnityEngine.Random.value < 0.5f ? "sfx_chicken_idle_wingflap" : "sfx_chicken_hurt", hen.transform.position, Quaternion.identity);
		}
		if (LastCluck.Count > 200)
		{
			LastCluck.Clear();
		}
		return true;
	}
}

/// <summary>Valheims Raid-Liste: den Hühner-Raid ergänzen (Server und Spieler).</summary>
[HarmonyPatch(typeof(RandEventSystem), "Awake")]
internal static class RaidHenEventPatch
{
	private static void Postfix(RandEventSystem __instance)
	{
		try
		{
			RaidHens.AddEvent(__instance);
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Kampfhühner: Raid nicht eingetragen: {e.Message}");
		}
	}
}

/// <summary>Kampfhühner greifen ohne Animation an: der Wildschwein-Angriff als Pick-Sprung (RaidHens.Peck).</summary>
[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
internal static class RaidHenAttackPatch
{
	[HarmonyPriority(Priority.High)]
	private static bool Prefix(Humanoid __instance, Character target, ref bool __result)
	{
		if (!RaidHens.Is(__instance))
		{
			return true;
		}
		try
		{
			__result = RaidHens.Peck(__instance, target);
		}
		catch (Exception e)
		{
			__result = false;
			ServerPlugin.Log.LogWarning($"Kampfhühner: Angriff: {e.Message}");
		}
		return false;
	}
}

/// <summary>
/// Kampfhühner sehen nur Spieler als Gegner (Gorduan: Schaden an Spielern, nicht an der Umgebung, an Tieren oder an Gebäuden),
/// und andere Tiere lassen sie in Ruhe. Gebäude greifen sie ohnehin nicht an (m_attackPlayerObjects aus).
/// </summary>
[HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IsEnemy), new Type[] { typeof(Character), typeof(Character) })]
internal static class RaidHenEnemyPatch
{
	private static void Postfix(Character a, Character b, ref bool __result)
	{
		if (!__result || a == null || b == null)
		{
			return;
		}
		if (RaidHens.Is(a))
		{
			__result = b.IsPlayer();
		}
		else if (RaidHens.Is(b))
		{
			__result = a.IsPlayer();
		}
	}
}
