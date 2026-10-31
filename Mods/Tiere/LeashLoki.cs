using System;
using System.Collections.Generic;
using System.Globalization;
using GC.Shared;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GC.Server.Leash;

/// <summary>
/// Lokis Blendwerk (mythische Leine, nur mit EpicLoot). Gorduan (13.10.2026): Wie viele Trugbilder die Leine ruft (1 oder 2),
/// würfelt EpicLoot einmal beim Erschaffen der Leine (Effekt GC_LeashIllusions 1–2). Beim Anleinen wird nicht mehr
/// gewürfelt: Es kommen immer so viele. Ein getötetes Trugbild kehrt nach einer Abklingzeit (IllusionCooldown, Standard
/// 5 Minuten) von selbst zurück, solange das Tier an der Leine hängt. Die Abklingzeit steht in der Leine (Weltzeit), gilt
/// also auch nach neuem Anleinen, an einem anderen Tier oder nach dem Einloggen. Beim Anleinen und beim Tod meldet Loki,
/// wie lange es noch dauert.
///
/// Ein Trugbild ist eine echte Kopie des Tiers (gleiche Art, Sterne und Name), gezähmt, folgt dem Halter und kämpft mit
/// den Tier-Werten der Leine. Es wird nie gespeichert (nicht dauerhaftes ZDO): Valheim entfernt es selbst, wenn der
/// Halter den Bereich verlässt oder sich abmeldet. Es lässt nichts fallen (kein Ragdoll, keine Beute), vermehrt sich
/// nicht, wächst nicht, frisst nicht und lässt sich weder anleinen noch befehligen, satteln oder umbenennen. Nach
/// Teleports und Ohnmacht erscheinen die Trugbilder beim Tier neu (ohne Abklingzeit).
/// </summary>
internal static class Illusions
{
	/// <summary>Im ZDO: Sitzung des Halters, der das Trugbild erschaffen hat (alle Spieler erkennen es daran).</summary>
	internal static readonly int IllusionHash = "gc_illusion".GetStableHashCode();
	/// <summary>In der Leine: bis wann getötete Trugbilder fehlen (Weltzeit in Ticks, je getötetem ein Eintrag, „;“-getrennt).</summary>
	internal const string CooldownKey = "gc_loki_back";
	private const int MaxCopies = 2;
	private static readonly Color Tint = new(0.55f, 1f, 0.85f);
	private static readonly string[] PoofEffects = { "vfx_odin_despawn", "vfx_spawn_small", "vfx_spawn" };

	private sealed class Mirage
	{
		public int Count;            // so viele Trugbilder gibt die Leine (1 oder 2, beim Erschaffen gewürfelt)
		public int Allowed;          // davon gerade erlaubt (ohne die in der Abklingzeit)
		public int CoolingBefore;    // so viele waren bei der letzten Prüfung in der Abklingzeit (für „ist zurück“)
		public float NextCoolCheck;
		public float NextSpawn;
		public readonly List<Character> Copies = new();
	}

	private static readonly Dictionary<Link, Mirage> Mirages = new();
	private static readonly HashSet<Character> Known = new();
	private static readonly HashSet<Character> Dying = new();
	private static readonly List<Character> DyingNow = new();
	private static readonly List<Link> Ended = new();
	private static readonly List<Character> Gone = new();
	private static GameObject? poof;
	private static bool poofResolved;
	private static float nextWatchdog;
	private static int groundMask;

	internal static bool Any => Known.Count > 0;

	internal static bool IsIllusion(Character? character) => character != null && Known.Count > 0 && Known.Contains(character);

	internal static bool IsIllusion(ZDO? zdo) => zdo != null && zdo.GetLong(IllusionHash, 0L) != 0L;

	/// <summary>Das echte Tier zu einem Trugbild (nur beim Halter bekannt, der es erschaffen hat und besitzt), sonst null.</summary>
	internal static Character? RealOf(Character copy)
	{
		foreach (KeyValuePair<Link, Mirage> entry in Mirages)
		{
			if (entry.Value.Copies.Contains(copy))
			{
				return entry.Key.Tame != null ? entry.Key.Tame.m_character : null;
			}
		}
		return null;
	}

	/// <summary>
	/// So nah folgt ein Trugbild: die gerade geltende Mindestlänge seiner Leine wie beim echten Tier (Gorduan, 16.10.2026:
	/// mit festen 2 m sah das Kürzen der Loki-Leine aus, als greife es nicht).
	/// </summary>
	internal static float FollowLength(Character copy)
	{
		foreach (KeyValuePair<Link, Mirage> entry in Mirages)
		{
			if (entry.Value.Copies.Contains(copy))
			{
				return entry.Key.SlackUntil;
			}
		}
		return Plugin.IllusionFollowLength;
	}

	/// <summary>Beim Laden jedes Tiers (alle Spieler): Trugbilder erkennen und entschärfen.</summary>
	internal static void OnAwake(Tameable tame)
	{
		ZNetView? view = tame.m_nview;
		if (view != null && view.IsValid() && tame.m_character != null && IsIllusion(view.GetZDO()))
		{
			Strip(tame);
		}
	}

	/// <summary>Was ein Trugbild nicht darf: Beute, Vermehrung, Wachsen, Fressen; als Artgenosse zählen.</summary>
	private static void Strip(Tameable tame)
	{
		Known.Add(tame.m_character);
		GameObject go = tame.gameObject;
		go.name = Utils.GetPrefabName(go) + "(GC_Illusion)"; // zählt nicht als Partner oder Artgenosse (SpawnSystem.GetNrOfInstances)
		if (go.GetComponent<Procreation>() is Procreation procreation)
		{
			Object.Destroy(procreation);
		}
		if (go.GetComponent<Growup>() is Growup growup)
		{
			Object.Destroy(growup);
		}
		if (go.GetComponent<CharacterDrop>() is CharacterDrop drop)
		{
			drop.SetDropsEnabled(false);
		}
		if (tame.m_monsterAI != null)
		{
			tame.m_monsterAI.m_consumeItems = new List<ItemDrop>();
		}
	}

	/// <summary>Jedes Bild: Trugbilder der eigenen Loki-Leinen erscheinen lassen, halten und aufräumen; alle Trugbilder schimmern.</summary>
	internal static void Tick()
	{
		Player? local = Player.m_localPlayer;
		if (local != null && LeashMagic.Available)
		{
			foreach (Link link in Links.Active)
			{
				if (link.Holder != local || link.Bond.Illusions <= 0 || link.Animal == null || link.View == null || !link.View.IsValid())
				{
					continue;
				}
				if (!Mirages.TryGetValue(link, out Mirage mirage))
				{
					mirage = new Mirage { Count = Mathf.Clamp(link.Bond.Illusions, 0, MaxCopies) };
					Mirages[link] = mirage;
					Announce(link, mirage);
				}
				Keep(link, mirage);
			}
		}
		if (Dying.Count > 0)
		{
			DyingNow.Clear();
			DyingNow.AddRange(Dying);
			Dying.Clear();
			foreach (Character copy in DyingNow)
			{
				OnKilled(copy); // ein Bild nach dem tödlichen Treffer, außerhalb von Valheims Schadens-Ablauf
			}
		}
		if (Mirages.Count > 0)
		{
			Ended.Clear();
			foreach (KeyValuePair<Link, Mirage> entry in Mirages)
			{
				Link link = entry.Key;
				if (link.Holder != local || link.Bond.Illusions <= 0 || !Links.Active.Contains(link))
				{
					Ended.Add(link);
				}
			}
			foreach (Link link in Ended)
			{
				DespawnAll(Mirages[link]); // Leine gelöst, angebunden, gerissen oder aus der Schnellleiste: Trugbilder verblassen
				Mirages.Remove(link);
			}
		}
		if (Known.Count > 0)
		{
			UpdateTint();
			if (Time.time >= nextWatchdog)
			{
				nextWatchdog = Time.time + 1f;
				Watchdog();
			}
		}
	}

	/// <summary>
	/// Beim Anleinen: Loki ruft die Trugbilder der Leine; fehlen getötete noch, sagt er, wann sie zurückkehren.
	/// </summary>
	private static void Announce(Link link, Mirage mirage)
	{
		long now = WorldNow();
		List<long> cooling = Cooling(link.Item, now);
		int ready = Mathf.Max(0, mirage.Count - cooling.Count);
		mirage.Allowed = ready;
		mirage.CoolingBefore = cooling.Count;
		mirage.NextCoolCheck = Time.time + 0.5f;
		if (mirage.Count <= 0)
		{
			return;
		}
		Plugin.Log.LogDebug($"Lokis Trugbilder: Leine gibt {mirage.Count}, davon {cooling.Count} in der Abklingzeit, jetzt {ready}.");
		string name = NameOf(link);
		string text = cooling.Count == 0 ? GcLoc.Get(mirage.Count >= 2 ? "gcls_loki_two" : "gcls_loki_one", name)
			: ready > 0 ? GcLoc.Get("gcls_loki_one_wait", name, Remaining(cooling[0], now))
			: GcLoc.Get(cooling.Count >= 2 ? "gcls_loki_wait_two" : "gcls_loki_wait_one", name, Times(cooling, now));
		Gc.TopLeft(text);
	}

	private static string NameOf(Link link) => link.Tame != null ? link.Tame.GetHoverName() : "";

	private static readonly HashSet<string> ReportedSpawnErrors = new();

	/// <summary>Ein Trugbild konnte nicht erscheinen: einmal je Grund ins Log (Loki versucht es alle 5 s erneut).</summary>
	private static void SpawnFailed(string why)
	{
		if (ReportedSpawnErrors.Add(why))
		{
			Plugin.Log.LogWarning("Lokis Trugbild konnte nicht erscheinen: " + why);
		}
	}

	/// <summary>Weltzeit in Ticks (beim Server für alle gleich), 0 ohne Verbindung.</summary>
	private static long WorldNow() => ZNet.instance != null ? ZNet.instance.GetTime().Ticks : 0L;

	/// <summary>
	/// Abklingzeiten getöteter Trugbilder aus der Leine, nur die noch laufenden, früheste zuerst. Einträge, die weiter in der
	/// Zukunft liegen, als die Abklingzeit reicht (etwa aus einer anderen Welt mit anderer Weltzeit), zählen nicht.
	/// </summary>
	private static List<long> Cooling(ItemDrop.ItemData? item, long now)
	{
		List<long> result = new();
		if (now <= 0L || item?.m_customData == null || !item.m_customData.TryGetValue(CooldownKey, out string text) || string.IsNullOrEmpty(text))
		{
			return result;
		}
		long limit = now + TimeSpan.FromSeconds(Plugin.IllusionCooldown.Value).Ticks;
		foreach (string part in text.Split(';'))
		{
			if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out long until) && until > now && until <= limit)
			{
				result.Add(until);
			}
		}
		result.Sort();
		return result;
	}

	private static void SaveCooling(ItemDrop.ItemData item, List<long> cooling)
	{
		if (cooling.Count == 0)
		{
			item.m_customData.Remove(CooldownKey);
			return;
		}
		string[] parts = new string[cooling.Count];
		for (int i = 0; i < cooling.Count; i++)
		{
			parts[i] = cooling[i].ToString(CultureInfo.InvariantCulture);
		}
		item.m_customData[CooldownKey] = string.Join(";", parts);
	}

	/// <summary>Restzeit als „4:35 min“ oder „45 s“.</summary>
	private static string Remaining(long until, long now)
	{
		int seconds = Mathf.Max(1, Mathf.CeilToInt((float)TimeSpan.FromTicks(until - now).TotalSeconds));
		return seconds >= 60 ? $"{seconds / 60}:{seconds % 60:00} min" : $"{seconds} s";
	}

	private static string Times(List<long> cooling, long now)
	{
		return cooling.Count >= 2 ? Remaining(cooling[0], now) + " " + GcLoc.Get("gcls_loki_and") + " " + Remaining(cooling[1], now) : Remaining(cooling[0], now);
	}

	private static void Keep(Link link, Mirage mirage)
	{
		for (int i = mirage.Copies.Count - 1; i >= 0; i--)
		{
			if (mirage.Copies[i] == null)
			{
				mirage.Copies.RemoveAt(i); // mit dem Bereich entladen (z. B. nach einem Teleport): erscheint neu
			}
		}
		if (link.Traveling || link.Holder.IsDead() || link.Holder.IsTeleporting() || link.Animal.IsDead() || Bonds.IsFainted(link.Animal))
		{
			DespawnAll(mirage); // unterwegs oder ohnmächtig: Trugbilder verblassen und kommen danach wieder
			mirage.NextSpawn = Time.time + 1f;
			return;
		}
		Vector3 home = link.Animal.transform.position;
		Vector3 holder = link.Holder.transform.position;
		for (int i = mirage.Copies.Count - 1; i >= 0; i--)
		{
			Character copy = mirage.Copies[i];
			// Kopien folgen dem Halter: nur wenn eine weder bei ihm noch beim Tier ist (lange Leinen lassen das Tier über 40 m
			// zurück, dann neu zu erscheinen hieße ständig verpuffen und wiederkommen)
			if (Vector3.Distance(copy.transform.position, holder) > 40f && Vector3.Distance(copy.transform.position, home) > 40f)
			{
				Despawn(copy); // abgehängt oder hängen geblieben: beim Tier neu erscheinen lassen
				mirage.Copies.RemoveAt(i);
				continue;
			}
			if (copy.m_nview != null && copy.m_nview.IsValid() && copy.m_nview.IsOwner() && copy.m_baseAI is MonsterAI ai && ai.GetFollowTarget() != link.Holder.gameObject)
			{
				ai.SetFollowTarget(link.Holder.gameObject);
			}
		}
		if (Time.time >= mirage.NextCoolCheck)
		{
			// Abklingzeit getöteter Trugbilder abgelaufen: Sie kehren von selbst zurück (Gorduan)
			mirage.NextCoolCheck = Time.time + 0.5f;
			int cooling = Cooling(link.Item, WorldNow()).Count;
			if (cooling < mirage.CoolingBefore)
			{
				Gc.TopLeft(GcLoc.Get("gcls_loki_back", NameOf(link)));
			}
			mirage.CoolingBefore = cooling;
			mirage.Allowed = Mathf.Max(0, mirage.Count - cooling);
		}
		if (mirage.Copies.Count < mirage.Allowed && Time.time >= mirage.NextSpawn)
		{
			Character? copy = Spawn(link);
			mirage.NextSpawn = Time.time + (copy != null ? 0.4f : 5f);
			if (copy != null)
			{
				mirage.Copies.Add(copy);
			}
		}
	}

	/// <summary>Kopie des Tiers neben ihm erschaffen (nicht dauerhaft, gezähmt, folgt dem Halter).</summary>
	private static Character? Spawn(Link link)
	{
		ZNetScene? scene = ZNetScene.instance;
		if (scene == null)
		{
			return null;
		}
		ZDO source = link.View.GetZDO();
		GameObject? prefab = scene.GetPrefab(source.GetPrefab());
		if (prefab == null)
		{
			SpawnFailed("kein Prefab für das Tier " + source.GetPrefab());
			return null;
		}
		Vector3 position = Place(link.Animal);
		GameObject go = Object.Instantiate(prefab, position, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
		ZNetView? view = go.GetComponent<ZNetView>();
		Character? copy = go.GetComponent<Character>();
		Tameable? tame = go.GetComponent<Tameable>();
		if (view == null || !view.IsValid() || copy == null || tame == null)
		{
			SpawnFailed($"Kopie von {prefab.name} unvollständig (Netz {(view != null && view.IsValid() ? "ok" : "fehlt")}, Figur {(copy != null ? "ok" : "fehlt")}, zähmbar {(tame != null ? "ok" : "fehlt")})");
			if (view != null && view.IsValid())
			{
				scene.Destroy(go);
			}
			else
			{
				Object.Destroy(go);
			}
			return null;
		}
		ZDO zdo = view.GetZDO();
		zdo.Persistent = false; // nie speichern: Valheim entfernt es mit dem Bereich oder beim Abmelden des Halters
		zdo.Set(IllusionHash, Links.Session(link.Holder));
		string name = source.GetString(ZDOVars.s_tamedName);
		if (name.Length > 0)
		{
			zdo.Set(ZDOVars.s_tamedName, name);
		}
		Strip(tame);
		copy.SetLevel(link.Animal.GetLevel());
		if (tame.m_monsterAI != null)
		{
			tame.m_monsterAI.MakeTame();
			tame.m_monsterAI.SetFollowTarget(link.Holder.gameObject);
		}
		else
		{
			copy.SetTamed(true);
		}
		zdo.Set(ZDOVars.s_follow, link.Holder.GetPlayerName());
		Poof(position);
		return copy;
	}

	private static Vector3 Place(Character animal)
	{
		Vector3 center = animal.transform.position;
		float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
		float radius = animal.GetRadius() * 2f + UnityEngine.Random.Range(1f, 2f);
		Vector3 point = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
		if (groundMask == 0)
		{
			groundMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
		}
		if (Physics.Raycast(point + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 8f, groundMask, QueryTriggerInteraction.Ignore) && Mathf.Abs(hit.point.y - center.y) < 2.5f)
		{
			return hit.point + Vector3.up * 0.1f;
		}
		return center + Vector3.up * 0.2f; // kein guter Platz daneben: am Tier selbst
	}

	private static void DespawnAll(Mirage mirage)
	{
		foreach (Character copy in mirage.Copies)
		{
			Despawn(copy);
		}
		mirage.Copies.Clear();
	}

	private static void Despawn(Character? copy)
	{
		if (copy == null)
		{
			return;
		}
		ZNetView? view = copy.m_nview;
		if (view == null || !view.IsValid() || !view.IsOwner() || ZNetScene.instance == null)
		{
			return; // nicht unseres: der Besitzer räumt es auf (oder Valheim, wenn er sich abmeldet)
		}
		Poof(copy.transform.position);
		ZNetScene.instance.Destroy(copy.gameObject);
	}

	/// <summary>Tödlicher Treffer an einem Trugbild (beim Besitzer, in SetHealth): im nächsten Bild zerstieben lassen.</summary>
	internal static void MarkKilled(Character copy)
	{
		ZNetView? view = copy.m_nview;
		if (view != null && view.IsValid() && view.IsOwner())
		{
			Dying.Add(copy);
		}
	}

	/// <summary>
	/// Ein Trugbild wurde getötet: zerstiebt ohne Beute und kehrt nach der Abklingzeit zurück (in der Leine vermerkt, damit
	/// neues Anleinen sie nicht aufhebt). Loki sagt, wann.
	/// </summary>
	internal static void OnKilled(Character copy)
	{
		if (copy == null)
		{
			return;
		}
		foreach (KeyValuePair<Link, Mirage> entry in Mirages)
		{
			Mirage mirage = entry.Value;
			if (!mirage.Copies.Remove(copy))
			{
				continue;
			}
			Link link = entry.Key;
			long now = WorldNow();
			if (now > 0L && link.Item?.m_customData != null)
			{
				List<long> cooling = Cooling(link.Item, now);
				long until = now + TimeSpan.FromSeconds(Plugin.IllusionCooldown.Value).Ticks;
				cooling.Add(until);
				cooling.Sort();
				SaveCooling(link.Item, cooling);
				mirage.CoolingBefore = cooling.Count;
				mirage.Allowed = Mathf.Max(0, mirage.Count - cooling.Count); // sofort, nicht erst bei der nächsten Prüfung neu rufen
				Gc.TopLeft(GcLoc.Get("gcls_loki_killed", NameOf(link), Remaining(until, now)));
			}
			break;
		}
		Despawn(copy);
	}

	private static void Poof(Vector3 position)
	{
		if (!poofResolved)
		{
			poofResolved = true;
			foreach (string effect in PoofEffects)
			{
				GameObject? prefab = PrefabManager.Cache.GetPrefab<GameObject>(effect);
				if (prefab != null && prefab.GetComponent<ZNetView>() == null)
				{
					poof = prefab;
					break;
				}
			}
		}
		if (poof != null)
		{
			Object.Instantiate(poof, position, Quaternion.identity);
		}
	}

	/// <summary>Geisterhaftes Schimmern (bei allen Spielern), damit man Trugbilder vom echten Tier unterscheidet.</summary>
	private static void UpdateTint()
	{
		float pulse = 0.8f + 0.2f * Mathf.Sin(Time.time * 2.5f);
		Color color = new(Tint.r * pulse, Tint.g * pulse, Tint.b * pulse, 1f);
		foreach (Character copy in Known)
		{
			if (copy != null)
			{
				Gc.Highlight(Bonds.Visual(copy), color);
			}
		}
	}

	/// <summary>Jede Sekunde: zerstörte vergessen; eigene Trugbilder ohne Loki-Leine verschwinden lassen.</summary>
	private static void Watchdog()
	{
		Gone.Clear();
		foreach (Character copy in Known)
		{
			if (copy == null)
			{
				Gone.Add(copy!);
				continue;
			}
			ZNetView? view = copy.m_nview;
			if (view != null && view.IsValid() && view.IsOwner() && !Owned(copy))
			{
				Gone.Add(copy);
			}
		}
		foreach (Character copy in Gone)
		{
			Known.Remove(copy);
			Despawn(copy);
		}
	}

	private static bool Owned(Character copy)
	{
		foreach (Mirage mirage in Mirages.Values)
		{
			if (mirage.Copies.Contains(copy))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>Lokis Trugbilder kämpfen mit den Tier-Werten ihrer Leine (ohne Wiederkehr, Licht und Hunger).</summary>
	internal static void AddBonds(Dictionary<Character, BondStats> animals)
	{
		foreach (KeyValuePair<Link, Mirage> entry in Mirages)
		{
			BondStats bond = Bonds.Current(entry.Key).ForCopy(); // nach einem Pfiff auch mit den Werten der Pfeife
			if (!bond.AnimalAny)
			{
				continue;
			}
			foreach (Character copy in entry.Value.Copies)
			{
				if (copy != null)
				{
					animals[copy] = bond;
				}
			}
		}
	}

	/// <summary>Neuer Spielstart: alles Gemerkte vergessen (die Trugbilder der alten Sitzung entfernt Valheim selbst).</summary>
	internal static void Reset()
	{
		Mirages.Clear();
		Known.Clear();
		Dying.Clear();
	}
}

/// <summary>Tod eines Trugbilds: kein Ragdoll, keine Beute, keine Zählung; es zerstiebt einfach.</summary>
[HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
internal static class IllusionDeathPatch
{
	[HarmonyPriority(Priority.First)]
	private static bool Prefix(Character __instance)
	{
		if (!Illusions.IsIllusion(__instance))
		{
			return true;
		}
		if (__instance.m_nview != null && __instance.m_nview.IsValid() && __instance.m_nview.IsOwner())
		{
			Illusions.OnKilled(__instance);
		}
		return false;
	}
}
