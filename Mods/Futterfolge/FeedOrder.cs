using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Mods.Futterfolge;

/// <summary>
/// Futter-Reihenfolge (Gorduan, 19.10.2026: „Tiere sollen auf doppelte Reichweite, falls sie einen Weg dazu finden, erkennen ob
/// Futter zu sehen ist und es essen, aber es gibt eine Reihenfolge, in der Tiere essen: Das Tier der gleichen Sorte mit dem
/// höchsten Lvl sollte als erstes essen und dann das nächste und sich dann so fortpflanzen.“ Am 20.10.2026: „das ist ein
/// feature für jetzt“).
///
/// Valheim (MonsterAI.UpdateConsumeItem): Ein hungriges Tier sucht alle 10 s das nächste Futter im Umkreis
/// m_consumeSearchRange (FindClosestConsumableItem), prüft nur für dieses einen Weg und frisst ein Stück davon. Im Kampf
/// (alarmiert mit Ziel) und geritten frisst es nicht.
///
/// Hier, für zahme Tiere (wilde auf Wunsch nur mit der größeren Reichweite):
///  * Umkreis × RangeFactor (Standard 2). Ein Weg muss hinführen; führt zum nächsten Futter keiner, versucht es weitere
///    (je Suche höchstens drei neue Wegprüfungen; Ergebnisse gelten 20 s), statt wie Valheim aufzugeben.
///  * Reihenfolge: Alle hungrigen zahmen Tiere derselben Sorte (gleiches Prefab; Jungtiere sind eine eigene Sorte, Lokis
///    Trugbilder heißen anders und zählen nicht), die gerade fressen könnten (nicht im Kampf, nicht geritten), stellen sich
///    an: trächtige zuletzt (PregnantLast, Standard an: Für die Zucht brauchen sie kein Futter), sonst nach der Stufe aus Zucht
///    und Erfahrung von GC_Tiere („(Lvl X)“), dann nach Valheims Sternen, bei Gleichstand nach der ZDO-ID, damit jeder Spieler
///    dieselbe Reihenfolge rechnet. Der Reihe nach nimmt jedes Tier das nächste Futter, das es erreicht; ein Stapel reicht für
///    so viele Tiere, wie Stücke darin liegen. Was danach übrig bleibt und erreichbar ist, frisst dieses Tier; sonst wartet es.
///  * Satte Tiere stehen nicht an, dann frisst das nächste (Vorschlag vom 19.10.).
///  * Gewartet wird höchstens WaitSeconds (Standard 60), dann frisst das Tier trotzdem: Ein höheres hängt vielleicht hinter
///    einem Zaun, folgt einem Spieler oder hat einen Befehl.
///  * Tiere an einer Leine oder an einem Pfosten (GC_Tiere) zählen nur für Futter, das sie an der Leine erreichen, und suchen
///    selbst nur dort.
///  * Zucht: Wird ein Tier trächtig, merkt es sich die Stufe des besten satten Partners im Partner-Umkreis (Valheims
///    m_partnerCheckRange). GC_Tiere rechnet die Stufe des Jungen damit statt mit dem nächsten Tier (GcHooks.MateLevel).
///  * Valheim lässt alle Tiere ihre Suche gleichzeitig beginnen; hier startet jede zu einem zufälligen Zeitpunkt, damit die
///    Wegprüfungen eines großen Hofs sich verteilen.
///
/// Gerechnet wird wie in Valheim beim Besitzer des Tiers; die Werte gibt der Server vor. Code-Prüfung 20.10.2026 eingearbeitet.
/// </summary>
internal static class FeedOrder
{
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<float> RangeFactor = null!;
	internal static ConfigEntry<float> WaitSeconds = null!;
	internal static ConfigEntry<bool> PregnantLast = null!;
	internal static ConfigEntry<bool> BestPartner = null!;
	internal static ConfigEntry<bool> Wild = null!;
	internal static ConfigEntry<bool> ShowRank = null!;

	private static ManualLogSource log = null!;
	private static bool reported;

	// Schlüssel von GC_Tiere am Tier (ohne GC_Tiere nie gesetzt)
	private static readonly int CareLevelHash = GcKeys.CareLevel.GetStableHashCode();
	private static readonly int HolderHash = GcKeys.LeashHolder.GetStableHashCode();
	private static readonly int TiedHash = GcKeys.LeashTied.GetStableHashCode();
	private static readonly int PostHash = GcKeys.LeashPost.GetStableHashCode();
	private static readonly int MaxLengthHash = GcKeys.LeashMax.GetStableHashCode();

	/// <summary>Im Muttertier: Stufe des Partners beim Trächtigwerden, −1 oder fehlt = keiner gemerkt.</summary>
	private static readonly int MateLevelHash = "gc_ff_mate_level".GetStableHashCode();

	/// <summary>Neue Wegprüfungen je Suche: für das suchende Tier und je ranghöherem Tier (Gemerktes zählt nicht).</summary>
	private const int OwnPathChecks = 3;
	private const int RivalPathChecks = 2;

	/// <summary>Zwischen zwei Suchen liegen 10 s; länger nicht gewartet heißt: Das Warten beginnt neu.</summary>
	private const float WaitGap = 25f;

	/// <summary>So lange gilt eine Wegprüfung (Tier → Futter), damit nicht jede Suche dieselbe Prüfung wiederholt.</summary>
	private const float PathMemory = 20f;

	/// <summary>So lange gilt der Rang im Hinweistext (den Valheim jedes Bild neu abfragt).</summary>
	private const float RankMemory = 1f;

	/// <summary>Leine ohne gespeicherte Länge: so weit reicht sie (Standardlänge der Leinen).</summary>
	private const float DefaultLeashLength = 10f;

	private static int itemMask;
	private static float nextCleanup;

	private sealed class Wait
	{
		public float Since;
		public float Last;
	}

	private sealed class Shown
	{
		public float Until;
		public int Place;
		public int Count;
	}

	/// <summary>Eigene Tiere, die gerade ranghöheren den Vortritt lassen.</summary>
	private static readonly Dictionary<Character, Wait> Waiting = new();

	/// <summary>Wegprüfung Tier → Futter: Ergebnis und gültig bis (Time.time).</summary>
	private static readonly Dictionary<long, KeyValuePair<bool, float>> Paths = new();

	/// <summary>Rang im Hinweistext je Tier.</summary>
	private static readonly Dictionary<Character, Shown> Ranks = new();

	private sealed class Food
	{
		public ItemDrop Item = null!;
		public Vector3 Position;
		public float Distance; // zum suchenden Tier
		public int Left;       // Stücke, die noch keinem ranghöheren Tier zustehen
	}

	private sealed class Rival
	{
		public Character Animal = null!;
		public MonsterAI AI = null!;
		public float Level;
		public int Stars;
		public bool Pregnant;
		public ZDOID Id;
	}

	/// <summary>Wohin ein Tier an der Leine kommt (GC_Tiere): am Pfosten um den Pfosten, in der Hand um den Halter.</summary>
	private readonly struct Reach
	{
		private readonly Vector3 center;
		private readonly float radius;
		private readonly bool limited;

		public Reach(Vector3 center, float radius)
		{
			this.center = center;
			this.radius = radius;
			limited = true;
		}

		public bool Covers(Vector3 point) => !limited || (point - center).sqrMagnitude <= radius * radius;
	}

	internal static void Init(ConfigFile config, ManualLogSource logger)
	{
		log = logger;
		const string food = "1 - Futter";
		Enabled = Gc.BindSynced(config, food, "Enabled", true, "Futter-Reihenfolge: Zahme Tiere sehen Futter weiter (wenn ein Weg hinführt), und das Tier mit der höchsten Stufe einer Sorte frisst zuerst. Aus: wie Valheim.");
		RangeFactor = Gc.BindSynced(config, food, "RangeFactor", 2f, "Faktor auf Valheims Umkreis, in dem ein Tier Futter sucht (2 = doppelt so weit). Es frisst nur, wenn ein Weg hinführt.", new AcceptableValueRange<float>(1f, 4f));
		WaitSeconds = Gc.BindSynced(config, food, "WaitSeconds", 60f, "So viele Sekunden lässt ein Tier ranghöheren den Vortritt; danach frisst es trotzdem (falls ein höheres nicht hinkommt). 0 = nie warten.", new AcceptableValueRange<float>(0f, 600f));
		PregnantLast = Gc.BindSynced(config, food, "PregnantLast", true, "Trächtige Tiere stellen sich hinten an: Für die Zucht brauchen sie kein Futter, so werden zuerst die Tiere satt, die sich paaren können.");
		Wild = Gc.BindSynced(config, food, "WildAnimals", false, "Auch wilde Tiere sehen Futter so weit (beim Zähmen). Eine Reihenfolge gibt es bei wilden Tieren nicht. Standard aus, damit das Zähmen nicht leichter wird.");
		BestPartner = Gc.BindSynced(config, "2 - Zucht", "BestPartner", true, "Mit GC_Tiere: Für die Stufe des Jungen zählt der beste satte Partner beim Trächtigwerden (höchste Stufe im Partner-Umkreis), nicht das nächste Tier.");
		ShowRank = Gc.Bind(config, "3 - Anzeige", "ShowRank", true, "Am hungrigen zahmen Tier anzeigen, an welcher Stelle es beim Fressen steht, und ob es gerade wartet.");
	}

	// ------------------------------------------------------------------ Futter suchen

	/// <summary>Sucht dieses Tier sein Futter nach diesen Regeln? Sonst bleibt es bei Valheims eigener Suche.</summary>
	internal static bool Handles(MonsterAI ai)
	{
		if (!Enabled.Value || !Plugin.Module.Allowed)
		{
			return false;
		}
		Character? self = ai.m_character;
		if (self == null || ai.m_consumeItems == null || ai.m_consumeItems.Count == 0)
		{
			return false;
		}
		return self.IsTamed() || Wild.Value;
	}

	/// <summary>Ersatz für Valheims FindClosestConsumableItem: beim Besitzer, alle 10 s, solange das Tier hungrig ist.</summary>
	internal static ItemDrop? Find(MonsterAI ai, float maxRange)
	{
		Cleanup();
		Character self = ai.m_character;
		float range = maxRange * RangeFactor.Value;
		List<Food> foods = FoodAround(ai, self, range);
		bool reserved = false;
		ItemDrop? target = self.IsTamed()
			? Reachable(ai, self, LeftAfterHigher(self, foods, range, out reserved))
			: Reachable(ai, self, foods); // wilde Tiere: nur weiter, ohne Reihenfolge
		if (target == null && reserved)
		{
			if (KeepWaiting(self))
			{
				return null; // ranghöhere Tiere zuerst
			}
			return Reachable(ai, self, foods); // lange genug gewartet: trotzdem fressen (das Warten endet beim Fressen)
		}
		StopWaiting(self);
		return target;
	}

	/// <summary>Futter im Umkreis, das dieses Tier fressen darf und an seiner Leine erreicht, das nächste zuerst.</summary>
	private static List<Food> FoodAround(MonsterAI ai, Character self, float range)
	{
		if (itemMask == 0)
		{
			itemMask = LayerMask.GetMask("item");
		}
		List<Food> foods = new();
		HashSet<ItemDrop> seen = new();
		Vector3 center = self.transform.position;
		Reach reach = ReachOf(self, ai.m_consumeRange);
		foreach (Collider collider in Physics.OverlapSphere(center, range, itemMask))
		{
			ItemDrop? item = collider.attachedRigidbody != null ? collider.attachedRigidbody.GetComponent<ItemDrop>() : null;
			if (item == null || !seen.Add(item) || item.m_nview == null || !item.m_nview.IsValid() || !ai.CanConsume(item.m_itemData))
			{
				continue;
			}
			Vector3 position = item.transform.position;
			if (!reach.Covers(position))
			{
				continue;
			}
			foods.Add(new Food { Item = item, Position = position, Distance = Vector3.Distance(position, center), Left = Mathf.Max(1, item.m_itemData.m_stack) });
		}
		foods.Sort((a, b) => a.Distance.CompareTo(b.Distance));
		return foods;
	}

	/// <summary>
	/// Erstes Futter der Liste (das nächste zuerst), zu dem ein Weg führt. Gemerkte Ergebnisse kosten nichts; neu geprüft
	/// werden höchstens OwnPathChecks, danach zählt nur noch Gemerktes.
	/// </summary>
	private static ItemDrop? Reachable(MonsterAI ai, Character self, List<Food> foods)
	{
		int fresh = 0;
		foreach (Food food in foods)
		{
			if (Known(self, food, out bool known))
			{
				if (known)
				{
					return food.Item;
				}
				continue;
			}
			if (fresh >= OwnPathChecks)
			{
				continue;
			}
			fresh++;
			if (Check(ai, self, food))
			{
				return food.Item;
			}
		}
		return null;
	}

	/// <summary>
	/// Was nach den ranghöheren Tieren derselben Sorte übrig bleibt: Der Reihe nach nimmt jedes das nächste Futter, das es
	/// erreicht, ein Stück je Tier. Alle Tiere einer Sorte fressen dasselbe, deshalb gilt die Futterliste des suchenden.
	/// reserved: Mindestens ein Stück steht einem ranghöheren Tier zu.
	/// </summary>
	private static List<Food> LeftAfterHigher(Character self, List<Food> foods, float range, out bool reserved)
	{
		reserved = false;
		if (foods.Count == 0)
		{
			return foods;
		}
		List<Rival> rivals = Rivals(self, 2f * range);
		if (rivals.Count <= 1)
		{
			return foods;
		}
		rivals.Sort(Higher);
		foreach (Rival rival in rivals)
		{
			if (rival.Animal == self)
			{
				break;
			}
			Food? pick = PickFor(rival, foods, range);
			if (pick != null)
			{
				pick.Left--;
				reserved = true;
			}
		}
		return reserved ? foods.FindAll(food => food.Left > 0) : foods;
	}

	/// <summary>
	/// Zahme Tiere derselben Sorte im Umkreis, die gerade fressen würden, dazu das Tier selbst. Zuerst über den Prefab-Hash im
	/// ZDO (Unity legt für jedes Lesen von gameObject.name einen neuen Text an), dann über den Namen wie Valheims Zählung: so
	/// fallen Lokis Trugbilder („…(GC_Illusion)“) heraus. Die Futterliste anderer Tiere zählt nicht: Sie ist nur beim Besitzer
	/// aktuell (GC_Tierarten setzt sie dort), gleiches Prefab frisst ohnehin dasselbe.
	/// </summary>
	private static List<Rival> Rivals(Character self, float around)
	{
		List<Rival> rivals = new();
		ZNetView? view = self.m_nview;
		if (view == null || !view.IsValid())
		{
			return rivals;
		}
		int prefab = view.GetZDO().GetPrefab();
		string? name = null;
		Vector3 center = self.transform.position;
		float max = around * around;
		bool hasSelf = false;
		foreach (BaseAI other in BaseAI.BaseAIInstances)
		{
			if (other is not MonsterAI ai || ai.m_character == null)
			{
				continue;
			}
			Character animal = ai.m_character;
			if (animal == self)
			{
				hasSelf = true;
				rivals.Add(Make(animal, ai));
				continue;
			}
			ZNetView? otherView = ai.m_nview;
			if (otherView == null || !otherView.IsValid())
			{
				continue;
			}
			ZDO zdo = otherView.GetZDO();
			if (zdo.GetPrefab() != prefab || (animal.transform.position - center).sqrMagnitude > max)
			{
				continue;
			}
			name ??= self.gameObject.name;
			if (other.gameObject.name != name || !WouldEat(animal, ai, zdo))
			{
				continue;
			}
			rivals.Add(Make(animal, ai));
		}
		if (!hasSelf && self.GetBaseAI() is MonsterAI own)
		{
			rivals.Add(Make(self, own));
		}
		return rivals;
	}

	/// <summary>Würde dieses Tier gerade fressen? Zahm, am Leben, hungrig, nicht im Kampf und nicht geritten (dann frisst Valheim nicht).</summary>
	private static bool WouldEat(Character animal, MonsterAI ai, ZDO zdo)
	{
		if (animal.IsDead() || !animal.IsTamed() || zdo.GetBool(ZDOVars.s_alert))
		{
			return false;
		}
		Tameable? tame = ai.m_tamable;
		if (tame == null || !tame.IsHungry())
		{
			return false;
		}
		return tame.m_saddle == null || !tame.m_saddle.HaveValidUser();
	}

	private static Rival Make(Character animal, MonsterAI ai)
	{
		ZNetView? view = animal.m_nview;
		ZDO? zdo = view != null && view.IsValid() ? view.GetZDO() : null;
		return new Rival
		{
			Animal = animal,
			AI = ai,
			Level = zdo != null ? zdo.GetFloat(CareLevelHash, 0f) : 0f,
			Stars = animal.GetLevel(),
			Pregnant = zdo != null && zdo.GetLong(ZDOVars.s_pregnant, 0L) != 0L,
			Id = animal.GetZDOID(),
		};
	}

	/// <summary>Wer zuerst frisst: nicht trächtige vor trächtigen (PregnantLast), dann Stufe, dann Sterne, dann die ZDO-ID (bei allen gleich).</summary>
	private static int Higher(Rival a, Rival b)
	{
		if (PregnantLast.Value && a.Pregnant != b.Pregnant)
		{
			return a.Pregnant ? 1 : -1;
		}
		int byLevel = b.Level.CompareTo(a.Level);
		if (byLevel != 0)
		{
			return byLevel;
		}
		int byStars = b.Stars.CompareTo(a.Stars);
		return byStars != 0 ? byStars : a.Id.CompareTo(b.Id);
	}

	/// <summary>Das Futter, das ein ranghöheres Tier nehmen würde: das nächste mit Stücken übrig, in Reichweite, an seiner Leine, mit Weg.</summary>
	private static Food? PickFor(Rival rival, List<Food> foods, float range)
	{
		Vector3 from = rival.Animal.transform.position;
		Reach reach = ReachOf(rival.Animal, rival.AI.m_consumeRange);
		List<Food> near = foods.FindAll(food => food.Left > 0 && (food.Position - from).sqrMagnitude <= range * range && reach.Covers(food.Position));
		near.Sort((a, b) => (a.Position - from).sqrMagnitude.CompareTo((b.Position - from).sqrMagnitude));
		int fresh = 0;
		foreach (Food food in near)
		{
			if (Known(rival.Animal, food, out bool known))
			{
				if (known)
				{
					return food;
				}
				continue;
			}
			if (fresh >= RivalPathChecks)
			{
				continue;
			}
			fresh++;
			if (Check(rival.AI, rival.Animal, food))
			{
				return food;
			}
		}
		return null;
	}

	private static long PathKey(Character animal, Food food) => ((long)animal.GetInstanceID() << 32) ^ (uint)food.Item.GetInstanceID();

	/// <summary>Gemerkte Wegprüfung, falls noch gültig.</summary>
	private static bool Known(Character animal, Food food, out bool ok)
	{
		if (Paths.TryGetValue(PathKey(animal, food), out KeyValuePair<bool, float> known) && known.Value > Time.time)
		{
			ok = known.Key;
			return true;
		}
		ok = false;
		return false;
	}

	/// <summary>Neue Wegprüfung (Valheims BaseAI.HavePath vom Tier aus), wird gemerkt.</summary>
	private static bool Check(BaseAI ai, Character animal, Food food)
	{
		bool ok = ai.HavePath(food.Position);
		Paths[PathKey(animal, food)] = new KeyValuePair<bool, float>(ok, Time.time + PathMemory);
		return ok;
	}

	/// <summary>Wohin ein Tier an der Leine kommt (GC_Tiere); ohne Leine überallhin.</summary>
	private static Reach ReachOf(Character animal, float eatRange)
	{
		ZNetView? view = animal.m_nview;
		if (view == null || !view.IsValid())
		{
			return default;
		}
		ZDO zdo = view.GetZDO();
		float length = zdo.GetFloat(MaxLengthHash, 0f);
		if (length <= 0f)
		{
			length = DefaultLeashLength;
		}
		if (zdo.GetInt(TiedHash, 0) == 1)
		{
			return new Reach(zdo.GetVec3(PostHash, animal.transform.position), length + eatRange);
		}
		long holder = zdo.GetLong(HolderHash, 0L);
		if (holder != 0L)
		{
			Player? player = PlayerBySession(holder);
			return new Reach(player != null ? player.transform.position : animal.transform.position, length + eatRange);
		}
		return default;
	}

	/// <summary>Halter einer Leine: GC_Tiere speichert die Benutzer-ID seines Spieler-ZDO.</summary>
	private static Player? PlayerBySession(long session)
	{
		foreach (Player player in Player.GetAllPlayers())
		{
			if (player != null && player.GetZDOID().UserID == session)
			{
				return player;
			}
		}
		return null;
	}

	internal static float LevelOf(Character animal)
	{
		ZNetView? view = animal.m_nview;
		return view != null && view.IsValid() ? view.GetZDO().GetFloat(CareLevelHash, 0f) : 0f;
	}

	// ------------------------------------------------------------------ Warten

	/// <summary>Ranghöhere sind dran: weiter warten? Nach WaitSeconds nicht mehr.</summary>
	private static bool KeepWaiting(Character self)
	{
		float now = Time.time;
		if (!Waiting.TryGetValue(self, out Wait wait) || now - wait.Last > WaitGap)
		{
			Waiting[self] = wait = new Wait { Since = now };
		}
		wait.Last = now;
		return now - wait.Since < WaitSeconds.Value;
	}

	/// <summary>Gefressen, eigenes Futter gefunden oder nichts für Ranghöhere reserviert: Das Warten ist vorbei.</summary>
	internal static void StopWaiting(Character? animal)
	{
		if (animal != null)
		{
			Waiting.Remove(animal);
		}
	}

	/// <summary>Wartet dieses Tier gerade auf ranghöhere? Nur beim Besitzer bekannt, der die Suche rechnet.</summary>
	private static bool IsWaiting(Character animal)
	{
		float now = Time.time;
		return Waiting.TryGetValue(animal, out Wait wait) && now - wait.Last <= WaitGap && now - wait.Since < WaitSeconds.Value;
	}

	private static void Cleanup()
	{
		float now = Time.time;
		if (now < nextCleanup)
		{
			return;
		}
		nextCleanup = now + 30f;
		List<Character> gone = new();
		foreach (KeyValuePair<Character, Wait> entry in Waiting)
		{
			if (entry.Key == null || now - entry.Value.Last > 4f * WaitGap)
			{
				gone.Add(entry.Key!);
			}
		}
		foreach (Character animal in gone)
		{
			Waiting.Remove(animal);
		}
		gone.Clear();
		foreach (KeyValuePair<Character, Shown> entry in Ranks)
		{
			if (entry.Key == null || entry.Value.Until < now)
			{
				gone.Add(entry.Key!);
			}
		}
		foreach (Character animal in gone)
		{
			Ranks.Remove(animal);
		}
		List<long> old = new();
		foreach (KeyValuePair<long, KeyValuePair<bool, float>> entry in Paths)
		{
			if (entry.Value.Value <= now)
			{
				old.Add(entry.Key);
			}
		}
		foreach (long key in old)
		{
			Paths.Remove(key);
		}
	}

	// ------------------------------------------------------------------ Zucht: der beste Partner

	private static bool Owned(Procreation procreation) => procreation.m_nview != null && procreation.m_nview.IsValid() && procreation.m_nview.IsOwner();

	/// <summary>Vor Valheims Procreate: War das Tier schon trächtig? (nur beim Besitzer, der die Zucht rechnet)</summary>
	internal static bool PregnantBefore(Procreation procreation) => Owned(procreation) && procreation.IsPregnant();

	/// <summary>Nach Valheims Procreate: gerade trächtig geworden → besten Partner merken; gerade geboren → vergessen.</summary>
	internal static void AfterProcreate(Procreation procreation, bool before)
	{
		if (!Owned(procreation))
		{
			return;
		}
		bool now = procreation.IsPregnant();
		ZDO zdo = procreation.m_nview.GetZDO();
		if (!before && now)
		{
			Character? mate = Enabled.Value && BestPartner.Value && Plugin.Module.Allowed && GcKern.ModLoaded("tiere") ? BestMate(procreation, readyOnly: true) : null;
			float level = mate != null ? LevelOf(mate) : -1f;
			if (level >= 0f || zdo.GetFloat(MateLevelHash, -1f) >= 0f)
			{
				zdo.Set(MateLevelHash, level); // immer neu setzen: kein alter Wert aus einer früheren Trächtigkeit
			}
		}
		else if (before && !now && zdo.GetFloat(MateLevelHash, -1f) >= 0f)
		{
			zdo.Set(MateLevelHash, -1f); // das Junge ist da, GC_Tiere hat seine Stufe schon gerechnet
		}
	}

	/// <summary>
	/// Bester Partner im Partner-Umkreis (Valheims m_partnerCheckRange, mindestens 3 m wie in GC_Tiere): zahm und wie bei
	/// Valheims Partnerzählung bereit zur Zucht (satt, nicht trächtig); davon die höchste Stufe, dann die meisten Sterne, dann
	/// das nächste. Ohne readyOnly zählen auch hungrige und trächtige.
	/// </summary>
	private static Character? BestMate(Procreation procreation, bool readyOnly)
	{
		Character? mother = procreation.m_character;
		if (mother == null)
		{
			return null;
		}
		string name = (procreation.m_seperatePartner != null ? procreation.m_seperatePartner.name : Utils.GetPrefabName(procreation.gameObject)) + "(Clone)";
		float range = Mathf.Max(procreation.m_partnerCheckRange, 3f);
		Vector3 center = mother.transform.position;
		Character? best = null;
		float bestLevel = -1f;
		int bestStars = -1;
		float bestDistance = float.MaxValue;
		foreach (BaseAI other in BaseAI.BaseAIInstances)
		{
			Character? animal = other != null ? other.m_character : null;
			if (animal == null || animal == mother || animal.IsDead() || !animal.IsTamed())
			{
				continue;
			}
			float distance = Vector3.Distance(animal.transform.position, center);
			if (distance > range || other!.gameObject.name != name)
			{
				continue;
			}
			if (readyOnly && other.GetComponent<Procreation>() is Procreation partner && !partner.ReadyForProcreation())
			{
				continue;
			}
			float level = LevelOf(animal);
			int stars = animal.GetLevel();
			bool better = level > bestLevel || (level == bestLevel && (stars > bestStars || (stars == bestStars && distance < bestDistance)));
			if (better)
			{
				best = animal;
				bestLevel = level;
				bestStars = stars;
				bestDistance = distance;
			}
		}
		return best;
	}

	/// <summary>
	/// Für GC_Tiere bei der Geburt (GcHooks.MateLevel): die gemerkte Stufe des Partners. Ohne Merker (trächtig schon vor dieser
	/// Mod) der beste Partner, der jetzt im Umkreis steht, satt oder nicht. Null: GC_Tiere nimmt wie bisher das nächste Tier.
	/// </summary>
	internal static float? MateLevelForBirth(Procreation parent)
	{
		if (!Enabled.Value || !BestPartner.Value || !Plugin.Module.Allowed || parent.m_nview == null || !parent.m_nview.IsValid())
		{
			return null;
		}
		float stored = parent.m_nview.GetZDO().GetFloat(MateLevelHash, -1f);
		if (stored >= 0f)
		{
			return stored;
		}
		Character? mate = BestMate(parent, readyOnly: false);
		return mate != null ? LevelOf(mate) : null;
	}

	// ------------------------------------------------------------------ Anzeige

	/// <summary>Hinweise am zahmen Tier: Platz beim Fressen (hungrig, nicht allein), Warten, Partner der Trächtigkeit.</summary>
	internal static string Hover(Tameable tame, string text)
	{
		Character? animal = tame.m_character;
		ZNetView? view = tame.m_nview;
		if (string.IsNullOrEmpty(text) || animal == null || view == null || !view.IsValid() || !tame.IsTamed() || !Enabled.Value || !Plugin.Module.Allowed)
		{
			return text;
		}
		MonsterAI? ai = tame.m_monsterAI;
		if (ShowRank.Value && ai != null && tame.IsHungry())
		{
			float now = Time.time;
			if (!Ranks.TryGetValue(animal, out Shown shown) || shown.Until <= now)
			{
				List<Rival> rivals = Rivals(animal, 2f * ai.m_consumeSearchRange * RangeFactor.Value);
				rivals.Sort(Higher);
				Ranks[animal] = shown = new Shown { Until = now + RankMemory, Place = rivals.FindIndex(rival => rival.Animal == animal) + 1, Count = rivals.Count };
			}
			if (shown.Count > 1)
			{
				text += "\n" + GcLoc.Get("gcff_hover_rank", shown.Place, shown.Count);
			}
			if (IsWaiting(animal))
			{
				text += "\n" + GcLoc.Get("gcff_hover_waiting");
			}
		}
		ZDO zdo = view.GetZDO();
		float mate = zdo.GetFloat(MateLevelHash, -1f);
		if (mate >= 0f && zdo.GetLong(ZDOVars.s_pregnant, 0L) != 0L && GcKern.ModLoaded("tiere"))
		{
			text += "\n" + GcLoc.Get("gcff_hover_mate", Mathf.FloorToInt(mate));
		}
		return text;
	}

	internal static void Report(Exception e)
	{
		if (!reported)
		{
			reported = true;
			log.LogWarning($"Futter-Reihenfolge: {e}");
		}
	}
}

// ---------------------------------------------------------------------- Patches

/// <summary>Futtersuche zahmer Tiere: weiter, mit Weg, in der Reihenfolge ihrer Stufe.</summary>
[HarmonyPatch(typeof(MonsterAI), "FindClosestConsumableItem")]
internal static class FeedSearchPatch
{
	private static bool Prefix(MonsterAI __instance, float maxRange, ref ItemDrop? __result)
	{
		try
		{
			if (!FeedOrder.Handles(__instance))
			{
				return true;
			}
			__result = FeedOrder.Find(__instance, maxRange);
			return false;
		}
		catch (Exception e)
		{
			FeedOrder.Report(e);
			return true; // im Zweifel Valheims eigene Suche
		}
	}
}

/// <summary>
/// Suche gestreut: Valheim beginnt den 10-s-Takt bei jedem Tier mit 0, alle zusammen geladenen Tiere suchten sonst im selben
/// Augenblick (Code-Prüfung 20.10.2026: Wegprüfungen eines großen Hofs auf einen Schlag).
/// </summary>
[HarmonyPatch(typeof(MonsterAI), "Awake")]
internal static class FeedStaggerPatch
{
	private static void Postfix(MonsterAI __instance)
	{
		__instance.m_consumeSearchTimer = UnityEngine.Random.Range(0f, __instance.m_consumeSearchInterval);
	}
}

/// <summary>Gefressen: Das Warten ist vorbei.</summary>
[HarmonyPatch(typeof(Tameable), "OnConsumedItem")]
internal static class FeedEatenPatch
{
	private static void Postfix(Tameable __instance) => FeedOrder.StopWaiting(__instance.m_character);
}

/// <summary>Zucht: beim Trächtigwerden den besten satten Partner merken, nach der Geburt vergessen.</summary>
[HarmonyPatch(typeof(Procreation), "Procreate")]
internal static class FeedMatePatch
{
	private static void Prefix(Procreation __instance, out bool __state)
	{
		__state = false;
		try
		{
			__state = FeedOrder.PregnantBefore(__instance);
		}
		catch (Exception e)
		{
			FeedOrder.Report(e);
		}
	}

	private static void Postfix(Procreation __instance, bool __state)
	{
		try
		{
			FeedOrder.AfterProcreate(__instance, __state);
		}
		catch (Exception e)
		{
			FeedOrder.Report(e);
		}
	}
}

/// <summary>Hinweise am zahmen Tier (vor GC_Tiere' „[Alt + E] Boni“, das als Letztes kommt).</summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.GetHoverText))]
internal static class FeedHoverPatch
{
	private static void Postfix(Tameable __instance, ref string __result)
	{
		try
		{
			__result = FeedOrder.Hover(__instance, __result);
		}
		catch (Exception e)
		{
			FeedOrder.Report(e);
		}
	}
}
