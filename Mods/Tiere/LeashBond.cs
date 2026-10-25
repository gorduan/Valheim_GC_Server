using System;
using System.Collections.Generic;
using System.Reflection;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Werte einer verzauberten Leine (aus EpicLoot, siehe LeashMagic). Ohne EpicLoot sind alle Werte 0 und nichts ändert
/// sich. Prozentwerte stehen als Prozent (50 = +50 %).
/// Gorduans Regel: Eine Leine wirkt nur, solange sie in der Schnellleiste liegt und ein Tier an ihr hängt. Angebundene
/// Leinen (Knoten) halten ihr Tier, verstärken aber nichts; nur „Unzerstörbar" und „Haltbarkeit" bleiben Eigenschaften
/// der Leine selbst.
/// </summary>
internal sealed class BondStats
{
	// ---- die Leine selbst
	public float Range;          // Leinenreichweite +m (Höchstlänge und Abrutschen; die Mindestlänge bleibt)
	public float Durability;     // Haltbarkeit +x
	public bool Unbreakable;     // verliert nie Haltbarkeit, Knoten unzerstörbar
	public int LoadReduction;    // Leinenlast −x (mindestens 1)
	public float Gentle;         // Verschleiß −x %
	public float Pull;           // Zugkraft +x %
	public float Tamer;          // Zähmen in 10 m +x % schneller (Leine in der Schnellleiste)

	// ---- das Tier an der aktiven Leine (Wiederkehr: sehr selten, würfelbar ab Legendär)
	public float Obedience;      // Gehorsam 0–100 %: läuft mit dir, greift unterwegs seltener an (100 %: nie)
	public float Damage;         // Schaden +x %
	public float AttackSpeed;    // Angriffstempo +x %
	public float Health;         // Leben +x % (als Schadensminderung: +100 % = halber Schaden)
	public float Speed;          // Lauftempo +x %
	public float Regen;          // heilt x % seines Lebens alle 10 s
	public float Resist;         // Elementarschaden −x % (Feuer, Frost, Blitz, Gift, Geist)
	public float NormalAfter;    // Angriffe: normaler Schaden ±x %, erst nach den Element-Zusätzen (Walkürenpfeife: −5, Gorduan)
	public float Thorns;         // Angreifer erleiden x % des Schadens
	public float Feed;           // wird x % langsamer hungrig
	public float Frost;          // Angriffe: zusätzlich x % als Frostschaden
	public float Lightning;      // Angriffe: zusätzlich x % als Blitzschaden
	public float Fire;           // Angriffe: zusätzlich x % als Feuerschaden
	public float Spirit;         // Angriffe: zusätzlich x % als Geisterschaden
	public float Poison;         // Angriffe: zusätzlich x % als Giftschaden
	public float LifeSteal;      // heilt sich um x % des verursachten Schadens
	public float Deter;          // wer das Tier trifft, taumelt mit x % Wahrscheinlichkeit
	public float StaggerPower;   // Angriffe bringen Gegner x % leichter ins Taumeln
	public float Berserk;        // unter 30 % Leben: Schaden +x %
	public float Light;          // leuchtet im Umkreis von x m
	public float Revive;         // stirbt nicht: x s Ohnmacht, dann volles Leben (Leine danach gelöst)
	public bool PoisonImmune;    // Gift schadet nicht
	public bool Steadfast;       // lässt sich weder zurückstoßen noch ins Taumeln bringen
	public bool Fearless;        // flieht nie, auch nicht vor Feuer
	public float Evade;          // x % der Treffer gehen vorbei (höchstens 75 %)
	public float Swim;           // Tier und Halter schwimmen x % schneller, der Halter braucht x % weniger Ausdauer im Wasser
	public float Youth;          // x s ohne Treffer: das Tier heilt vollständig
	public int Illusions;        // Loki: bis zu x Trugbilder des Tiers (höchstens 2)

	// ---- nur mythische Tierpfeifen (Gorduan, 24.10.2026), wirken beim Pfiff (Whistles.Buff, Whistles.Mythic)
	public bool Fireproof;       // Surts Lohe: Feuer schadet dem Tier nicht, solange der Pfiff wirkt
	public float Blaze;          // Surts Lohe: Gegner um jedes gehorchende Tier erleiden einmal x Feuerschaden
	public float Hall;           // Gjallarhorn: Gegner im Umkreis von x m um den Pfeifenden taumeln (keine Bosse)
	public float Rally;          // Gjallarhorn: jedes gehorchende Tier heilt sofort x % seines Lebens

	// ---- nur Sättel (Gorduan, 20.10.2026), wirken beim Reiten
	public float MountStamina;       // Ausdauer des Reittiers +x %
	public float MountStaminaRegen;  // Ausdauer-Erholung +x %
	public float MountStaminaSave;   // Rennen und Schwimmen kosten x % weniger Ausdauer

	// ---- der Halter, solange ein Tier an der aktiven Leine hängt
	public float HolderCarry;    // Tragkraft +x
	public float HolderSpeed;    // Lauftempo +x %
	public float HolderRegen;    // heilt x Leben pro Sekunde
	public float HolderReveal;   // Gegner im Umkreis von x m auf der Karte
	public float HolderHunt;     // Schaden +x %
	public float HolderWatch;    // Gegner im Umkreis von x m leuchten auf (nur für ihn)
	public float HolderStealth;  // beim Schleichen x % schwerer zu entdecken
	public float HolderSwim;     // nur in der Halter-Summe: höchstes „Swim" der aktiven Leinen

	public static readonly BondStats None = new();

	private BondStats? copy;

	public bool Any => Range > 0f || Durability > 0f || Unbreakable || LoadReduction > 0 || Gentle > 0f || Pull > 0f || Tamer > 0f
		|| AnimalAny || HolderAny;

	public bool AnimalAny => Obedience > 0f || Damage > 0f || AttackSpeed > 0f || Health > 0f || Speed > 0f || Regen > 0f || Resist > 0f
		|| NormalAfter != 0f || Thorns > 0f || Feed > 0f || Frost > 0f || Lightning > 0f || Fire > 0f || Spirit > 0f || Poison > 0f || LifeSteal > 0f
		|| Deter > 0f || StaggerPower > 0f || Berserk > 0f || Light > 0f
		|| Revive > 0f || PoisonImmune || Steadfast || Fearless || Evade > 0f || Swim > 0f || Youth > 0f || Illusions > 0
		|| Fireproof || Blaze > 0f || Hall > 0f || Rally > 0f;

	public bool HolderAny => HolderCarry > 0f || HolderSpeed > 0f || HolderRegen > 0f || HolderReveal > 0f || HolderHunt > 0f
		|| HolderWatch > 0f || HolderStealth > 0f || Swim > 0f;

	/// <summary>Für Lokis Trugbilder: die Tier-Werte der Leine ohne Wiederkehr, Licht, Hunger und weitere Trugbilder.</summary>
	public BondStats ForCopy()
	{
		if (copy == null)
		{
			BondStats c = (BondStats)MemberwiseClone();
			c.Revive = 0f;
			c.Light = 0f;
			c.Feed = 0f;
			c.Illusions = 0;
			c.copy = null;
			copy = c;
		}
		return copy;
	}

	/// <summary>
	/// Tierpfeife (Gorduan, 18.10.2026): die Tier-Werte, die ein kurzer Pfiff tragen kann (Kampf, Tempo, Schutz, Licht), aus den
	/// Verzauberungen der Pfeife, dazu ihr eigenes Tempo. Hunger, Heilung alle 10 s, Wiederkehr und Leinen-Werte nicht.
	/// </summary>
	public BondStats ForWhistle(float speed)
	{
		return new BondStats
		{
			Damage = Damage, AttackSpeed = AttackSpeed, Health = Health, Speed = Speed + speed, Resist = Resist, NormalAfter = NormalAfter, Thorns = Thorns,
			Frost = Frost, Lightning = Lightning, Fire = Fire, Spirit = Spirit, Poison = Poison, LifeSteal = LifeSteal,
			Deter = Deter, StaggerPower = StaggerPower, Berserk = Berserk, Evade = Evade, Light = Light,
			PoisonImmune = PoisonImmune, Steadfast = Steadfast, Fearless = Fearless, Fireproof = Fireproof,
		};
	}

	/// <summary>Werte der Leine plus die eines Pfiffs (Prozente addiert, Schalter oder, Licht der größere Umkreis).</summary>
	public BondStats With(BondStats whistle)
	{
		BondStats c = (BondStats)MemberwiseClone();
		c.copy = null;
		c.Damage += whistle.Damage;
		c.AttackSpeed += whistle.AttackSpeed;
		c.Health += whistle.Health;
		c.Speed += whistle.Speed;
		c.Resist += whistle.Resist;
		c.NormalAfter += whistle.NormalAfter;
		c.Thorns += whistle.Thorns;
		c.Frost += whistle.Frost;
		c.Lightning += whistle.Lightning;
		c.Fire += whistle.Fire;
		c.Spirit += whistle.Spirit;
		c.Poison += whistle.Poison;
		c.LifeSteal += whistle.LifeSteal;
		c.Deter += whistle.Deter;
		c.StaggerPower += whistle.StaggerPower;
		c.Berserk += whistle.Berserk;
		c.Evade += whistle.Evade;
		c.Light = Mathf.Max(c.Light, whistle.Light);
		c.PoisonImmune |= whistle.PoisonImmune;
		c.Steadfast |= whistle.Steadfast;
		c.Fearless |= whistle.Fearless;
		c.Fireproof |= whistle.Fireproof;
		return c;
	}

	/// <summary>Halter-Werte auf 0 (die Halter-Summe wird jedes Bild neu gebildet, ohne neues Objekt).</summary>
	public void ClearHolder()
	{
		HolderCarry = 0f;
		HolderSpeed = 0f;
		HolderRegen = 0f;
		HolderReveal = 0f;
		HolderHunt = 0f;
		HolderWatch = 0f;
		HolderStealth = 0f;
		HolderSwim = 0f;
	}
}

/// <summary>
/// Wirkung der Leinen-Verzauberungen. Das Tier an einer aktiven Leine gehört dem Halter (ZDO-Besitz), deshalb rechnet
/// alles hier beim Halter: Werte je Tier aus den eigenen Leinen, Halter-Boni als Summe. Licht und Ohnmacht sehen alle (ZDO).
/// </summary>
internal static class Bonds
{
	internal static readonly int LightHash = "gc_leash_light".GetStableHashCode();
	/// <summary>Ohnmacht bis (Weltzeit in Ticks), im Tier; alle Spieler blenden es so lange aus.</summary>
	internal static readonly int FaintHash = "gc_faint_until".GetStableHashCode();
	/// <summary>Zähmhilfe des Spielers (in seinem Spieler-ZDO): Der Besitzer des Tiers, das gerade zahm wird, liest sie dort.</summary>
	internal static readonly int TamerHash = "gc_leash_tamer".GetStableHashCode();

	/// <summary>Tiere an eigenen aktiven Leinen und Lokis Trugbilder, mit ihren Werten (nur beim Halter).</summary>
	internal static readonly Dictionary<Character, BondStats> Animals = new();

	/// <summary>Halter-Boni der eigenen aktiven Leinen (Summe, Umkreise als Höchstwert).</summary>
	internal static readonly BondStats Holder = new();

	/// <summary>Zähmhilfe: höchster Wert einer Leine in der Schnellleiste.</summary>
	internal static float Tamer;

	/// <summary>Ist gerade ein Tier ohnmächtig? Schnelle Vorprüfung für Patches, die jedes Bild laufen.</summary>
	internal static bool AnyFainted;

	private struct Fear
	{
		public float LowHealth;
		public bool Unreachable;
		public bool NotAlerted;
		public bool Fire;
	}

	private static readonly Dictionary<Tameable, float> OriginalFed = new();
	private static readonly HashSet<Tameable> FedThisFrame = new();
	private static readonly Dictionary<MonsterAI, Fear> OriginalFear = new();
	private static readonly HashSet<MonsterAI> BraveThisFrame = new();
	private static readonly Dictionary<Character, List<Renderer>> Hidden = new();
	private static readonly Dictionary<BaseAI, bool> PausedAi = new();
	private static readonly Dictionary<Character, bool> OriginalSkip = new();
	private static readonly HashSet<Character> Fainted = new();
	private static readonly Dictionary<Tameable, Light> Lights = new();
	private static readonly HashSet<Character> Watched = new();
	private static readonly HashSet<Character> WatchedNow = new();
	private static readonly List<Character> Nearby = new();
	private static readonly List<object> RevealPins = new();
	private static readonly List<Tameable> DeadTames = new();
	private static readonly List<MonsterAI> DeadMonsters = new();
	private static readonly List<Character> DeadCharacters = new();
	private static readonly List<BaseAI> DeadAis = new();
	private static readonly Color WatchColor = new(1f, 0.8f, 0.3f);
	private static float nextReveal;
	private static float nextHotbarScan;
	private static float nextScan;
	private static float nextWatch;
	private static float healTimer;
	private static MethodInfo? addPin;
	private static MethodInfo? removePin;
	private static bool pinsResolved;

	/// <summary>Werte einer Leine lesen (gecacht über die Link-Auffrischung, nicht jedes Bild).</summary>
	public static BondStats Of(ItemDrop.ItemData? item) => item != null && LeashMagic.Available ? LeashMagic.Read(item) : BondStats.None;

	/// <summary>Werte, die gerade auf das Tier wirken: die der Leine, nach einem Pfiff dazu die der Pfeife (Whistles).</summary>
	public static BondStats Current(Link link)
	{
		if (Time.time >= link.WhistleUntil)
		{
			return link.Bond;
		}
		if (link.WhistleCombined == null || !ReferenceEquals(link.WhistleBase, link.Bond))
		{
			link.WhistleBase = link.Bond; // einmal je Pfiff (oder neuer Verzauberung) bilden, nicht jedes Bild
			link.WhistleCombined = link.Bond.With(link.WhistleBond);
		}
		return link.WhistleCombined;
	}

	/// <summary>Jedes Bild beim Spieler: Tiere und Halter-Boni aus den aktiven Leinen, Heilung, Licht, Ohnmacht, Gegner.</summary>
	public static void Tick()
	{
		Player? local = Player.m_localPlayer;
		Animals.Clear();
		Holder.ClearHolder();
		foreach (Link link in Links.Active)
		{
			if (link.Holder != local || link.Animal == null || link.Traveling)
			{
				continue;
			}
			BondStats bond = Current(link);
			if (bond.AnimalAny)
			{
				Animals[link.Animal] = bond;
			}
			Holder.HolderCarry += bond.HolderCarry;
			Holder.HolderSpeed += bond.HolderSpeed;
			Holder.HolderRegen += bond.HolderRegen;
			Holder.HolderHunt += bond.HolderHunt;
			Holder.HolderReveal = Mathf.Max(Holder.HolderReveal, bond.HolderReveal);
			Holder.HolderWatch = Mathf.Max(Holder.HolderWatch, bond.HolderWatch);
			Holder.HolderStealth = Mathf.Max(Holder.HolderStealth, bond.HolderStealth);
			Holder.HolderSwim = Mathf.Max(Holder.HolderSwim, bond.Swim);
			ApplyFeed(link.Tame, bond.Feed);
			ApplyLight(link.View, bond.Light);
		}
		// Sattel (Gorduan, 20.10.2026): seine Verzauberung wirkt nur beim Reiten, auf das Reittier (Leine und Sattel addiert)
		Character? mount = Saddles.RiddenMount(local);
		if (mount != null)
		{
			BondStats saddle = Saddles.Bond(mount);
			if (saddle.AnimalAny)
			{
				Animals[mount] = Animals.TryGetValue(mount, out BondStats leash) ? leash.With(saddle) : saddle;
			}
		}
		Illusions.AddBonds(Animals); // Lokis Trugbilder kämpfen mit den Tier-Werten ihrer Leine
		foreach (KeyValuePair<Character, BondStats> entry in Animals)
		{
			if (entry.Value.Fearless && entry.Key != null && entry.Key.m_baseAI is MonsterAI monster)
			{
				ApplyFearless(monster);
			}
		}
		RestoreFeed();
		RestoreFearless();
		Heal(local);
		if (Time.time >= nextHotbarScan)
		{
			nextHotbarScan = Time.time + 1f;
			Tamer = local != null ? HotbarTamer(local) : 0f;
			PublishTamer(local);
		}
	}

	/// <summary>Anzeigen beim Halter (eigener Fehlerschutz): Gegner auf der Karte, leuchtende Gegner.</summary>
	public static void TickVisuals()
	{
		Player? local = Player.m_localPlayer;
		UpdateReveal(local, Holder.HolderReveal);
		UpdateWatch(local, Holder.HolderWatch);
	}

	/// <summary>Licht und Ohnmacht aller Tiere (eigener Fehlerschutz, damit niemand in der Ohnmacht hängen bleibt).</summary>
	public static void TickFaint()
	{
		UpdateScan();
		HoldFainted();
	}

	private static float HotbarTamer(Player player)
	{
		float best = 0f;
		Inventory inventory = player.GetInventory();
		for (int x = 0; x < Math.Min(8, inventory.GetWidth()); x++)
		{
			ItemDrop.ItemData? item = inventory.GetItemAt(x, 0);
			if (item != null && Plugin.IsLeash(item, out _))
			{
				best = Mathf.Max(best, Of(item).Tamer);
			}
		}
		foreach (ItemDrop.ItemData item in LeashBar.OpenItems(player))
		{
			if (Plugin.IsLeash(item, out _))
			{
				best = Mathf.Max(best, Of(item).Tamer); // auch aus der Leinenleiste (Gorduan, 24.10.2026)
			}
		}
		return best;
	}

	/// <summary>Zähmen läuft beim Besitzer des Tiers, das zahm wird: Die eigene Zähmhilfe steht deshalb im Spieler-ZDO.</summary>
	private static void PublishTamer(Player? local)
	{
		ZNetView? view = local != null ? local.m_nview : null;
		if (view == null || !view.IsValid() || !view.IsOwner())
		{
			return;
		}
		ZDO zdo = view.GetZDO();
		if (Mathf.Abs(zdo.GetFloat(TamerHash, 0f) - Tamer) > 0.01f)
		{
			zdo.Set(TamerHash, Tamer);
		}
	}

	// ------------------------------------------------------------------ Heilung (einmal pro Sekunde, nicht jedes Bild ins ZDO)

	private static void Heal(Player? local)
	{
		healTimer += Time.deltaTime;
		if (healTimer < 1f)
		{
			return;
		}
		float seconds = Mathf.Min(healTimer, 2f);
		healTimer = 0f;
		foreach (KeyValuePair<Character, BondStats> entry in Animals)
		{
			Character animal = entry.Key;
			BondStats bond = entry.Value;
			if (animal == null || (bond.Regen <= 0f && bond.Youth <= 0f) || animal.IsDead())
			{
				continue;
			}
			float max = animal.GetMaxHealth();
			float health = animal.GetHealth();
			if (health >= max)
			{
				continue;
			}
			BaseAI? ai = animal.m_baseAI;
			if (bond.Youth > 0f && ai != null && ai.m_timeSinceHurt >= bond.Youth)
			{
				animal.Heal(max - health, true); // Iduns Apfel: eine Weile unversehrt, dann wieder ganz heil
			}
			else if (bond.Regen > 0f)
			{
				animal.Heal(max * bond.Regen / 100f / 10f * seconds, false);
			}
		}
		if (local != null && Holder.HolderRegen > 0f && !local.IsDead() && local.GetHealth() < local.GetMaxHealth())
		{
			local.Heal(Holder.HolderRegen * seconds, false);
		}
	}

	// ------------------------------------------------------------------ Hunger

	private static void ApplyFeed(Tameable tame, float feed)
	{
		if (tame == null || feed <= 0f)
		{
			return;
		}
		if (!OriginalFed.ContainsKey(tame))
		{
			OriginalFed[tame] = tame.m_fedDuration;
		}
		tame.m_fedDuration = OriginalFed[tame] * (1f + feed / 100f);
		FedThisFrame.Add(tame);
	}

	private static void RestoreFeed()
	{
		if (OriginalFed.Count == 0)
		{
			FedThisFrame.Clear();
			return;
		}
		DeadTames.Clear();
		foreach (KeyValuePair<Tameable, float> entry in OriginalFed)
		{
			if (entry.Key == null || !FedThisFrame.Contains(entry.Key))
			{
				DeadTames.Add(entry.Key!);
			}
		}
		foreach (Tameable tame in DeadTames)
		{
			if (tame != null)
			{
				tame.m_fedDuration = OriginalFed[tame];
			}
			OriginalFed.Remove(tame!);
		}
		FedThisFrame.Clear();
	}

	// ------------------------------------------------------------------ Furchtlos (nur beim Besitzer, solange gebunden)

	private static void ApplyFearless(MonsterAI ai)
	{
		if (!OriginalFear.ContainsKey(ai))
		{
			OriginalFear[ai] = new Fear
			{
				LowHealth = ai.m_fleeIfLowHealth,
				Unreachable = ai.m_fleeIfHurtWhenTargetCantBeReached,
				NotAlerted = ai.m_fleeIfNotAlerted,
				Fire = ai.m_afraidOfFire,
			};
		}
		ai.m_fleeIfLowHealth = 0f;
		ai.m_fleeIfHurtWhenTargetCantBeReached = false;
		ai.m_fleeIfNotAlerted = false;
		ai.m_afraidOfFire = false;
		BraveThisFrame.Add(ai);
	}

	private static void RestoreFearless()
	{
		if (OriginalFear.Count == 0)
		{
			BraveThisFrame.Clear();
			return;
		}
		DeadMonsters.Clear();
		foreach (KeyValuePair<MonsterAI, Fear> entry in OriginalFear)
		{
			if (entry.Key == null || !BraveThisFrame.Contains(entry.Key))
			{
				DeadMonsters.Add(entry.Key!);
			}
		}
		foreach (MonsterAI ai in DeadMonsters)
		{
			if (ai != null && OriginalFear.TryGetValue(ai, out Fear fear))
			{
				ai.m_fleeIfLowHealth = fear.LowHealth;
				ai.m_fleeIfHurtWhenTargetCantBeReached = fear.Unreachable;
				ai.m_fleeIfNotAlerted = fear.NotAlerted;
				ai.m_afraidOfFire = fear.Fire;
			}
			OriginalFear.Remove(ai!);
		}
		BraveThisFrame.Clear();
	}

	// ------------------------------------------------------------------ Licht (für alle sichtbar)

	private static void ApplyLight(ZNetView view, float radius)
	{
		if (view == null || !view.IsValid() || !view.IsOwner())
		{
			return;
		}
		ZDO zdo = view.GetZDO();
		if (Mathf.Abs(zdo.GetFloat(LightHash, 0f) - radius) > 0.01f)
		{
			zdo.Set(LightHash, radius);
		}
	}

	/// <summary>Leuchtende Tiere bei allen Spielern (Wert im Tier, gesetzt vom Halter). Nur solange der Halter da ist.</summary>
	private static void UpdateLight(Tameable tame, ZDO zdo)
	{
		float radius = zdo.GetFloat(LightHash, 0f);
		if (radius > 0f && Links.HolderOf(zdo) == null)
		{
			radius = 0f; // Halter weg (z. B. abgestürzt, bevor er lösen konnte): kein Leuchten für immer
		}
		Lights.TryGetValue(tame, out Light existing);
		if (radius <= 0f)
		{
			if (existing != null)
			{
				UnityEngine.Object.Destroy(existing.gameObject);
			}
			Lights.Remove(tame);
			return;
		}
		if (existing == null)
		{
			GameObject go = new("GC_LeashLight");
			go.transform.SetParent(tame.transform, false);
			go.transform.localPosition = Vector3.up * 1.2f;
			existing = go.AddComponent<Light>();
			existing.type = LightType.Point;
			existing.color = new Color(1f, 0.82f, 0.45f); // goldenes Schimmern
			existing.intensity = 1.4f;
			existing.shadows = LightShadows.None;
			Lights[tame] = existing;
		}
		existing.range = radius;
	}

	/// <summary>Licht im Tier löschen, wenn die Leine es nicht mehr hält (beim Lösen, Anbinden).</summary>
	internal static void ClearLight(ZNetView? view)
	{
		if (view != null && view.IsValid() && view.IsOwner() && view.GetZDO().GetFloat(LightHash, 0f) > 0f)
		{
			view.GetZDO().Set(LightHash, 0f);
		}
	}

	// ------------------------------------------------------------------ Gegner auf der Karte (Halter)

	private static void UpdateReveal(Player? local, float radius)
	{
		if (Time.time < nextReveal)
		{
			return;
		}
		nextReveal = Time.time + 2f;
		Minimap? map = Minimap.instance;
		if (map == null)
		{
			return;
		}
		ResolvePins();
		foreach (object pin in RevealPins)
		{
			removePin?.Invoke(map, new[] { pin });
		}
		RevealPins.Clear();
		if (local == null || radius <= 0f || addPin == null)
		{
			return;
		}
		bool[]? visible = map.m_visibleIconTypes;
		int type = (int)Minimap.PinType.RandomEvent;
		if (visible != null && type < visible.Length && !visible[type])
		{
			return; // der Spieler hat diese Symbole ausgeblendet: nicht wieder einschalten (AddPin täte das)
		}
		Nearby.Clear();
		Character.GetCharactersInRange(local.transform.position, radius, Nearby);
		ParameterInfo[] parameters = addPin.GetParameters();
		foreach (Character character in Nearby)
		{
			if (character == null || character.IsPlayer() || character.IsTamed() || character.IsDead() || !BaseAI.IsEnemy(local, character))
			{
				continue;
			}
			object?[] args = new object?[parameters.Length];
			args[0] = character.transform.position;
			args[1] = Minimap.PinType.RandomEvent;
			args[2] = "";
			args[3] = false;
			args[4] = false;
			for (int i = 5; i < parameters.Length; i++)
			{
				args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue ?? (parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null)
					: (parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null);
			}
			object? pin = addPin.Invoke(map, args);
			if (pin != null)
			{
				RevealPins.Add(pin);
			}
		}
	}

	/// <summary>Minimap.AddPin hat in 1.0.17 einen Plattform-Parameter aus einer weiteren Assembly: deshalb per Reflection.</summary>
	private static void ResolvePins()
	{
		if (pinsResolved)
		{
			return;
		}
		pinsResolved = true;
		foreach (MethodInfo method in typeof(Minimap).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
		{
			ParameterInfo[] p = method.GetParameters();
			if (method.Name == "AddPin" && p.Length >= 5 && p[0].ParameterType == typeof(Vector3) && p[1].ParameterType == typeof(Minimap.PinType))
			{
				addPin = method;
			}
			else if (method.Name == "RemovePin" && p.Length == 1 && p[0].ParameterType == typeof(Minimap.PinData))
			{
				removePin = method;
			}
		}
	}

	// ------------------------------------------------------------------ Heimdalls Wacht: Gegner leuchten (nur beim Halter)

	private static void UpdateWatch(Player? local, float radius)
	{
		if (Time.time < nextWatch || (radius <= 0f && Watched.Count == 0))
		{
			return;
		}
		nextWatch = Time.time + 0.5f;
		WatchedNow.Clear();
		if (local != null && radius > 0f)
		{
			Nearby.Clear();
			Character.GetCharactersInRange(local.transform.position, radius, Nearby);
			foreach (Character character in Nearby)
			{
				if (character == null || character.IsPlayer() || character.IsTamed() || character.IsDead() || !BaseAI.IsEnemy(local, character))
				{
					continue;
				}
				WatchedNow.Add(character);
				Gc.Highlight(Visual(character), WatchColor);
			}
		}
		foreach (Character character in Watched)
		{
			if (character != null && !WatchedNow.Contains(character))
			{
				Gc.ResetHighlight(Visual(character));
			}
		}
		Watched.Clear();
		Watched.UnionWith(WatchedNow);
	}

	internal static GameObject Visual(Character character) => character.m_visual != null ? character.m_visual : character.gameObject;

	// ------------------------------------------------------------------ Kampf, Wiederkehr und Ohnmacht

	internal static bool TryGet(Character character, out BondStats bond)
	{
		bond = BondStats.None;
		return Animals.Count > 0 && character != null && Animals.TryGetValue(character, out bond!);
	}

	/// <summary>
	/// Ohnmächtig? Gilt, solange die Markierung im Tier steht: Erst der Besitzer löscht sie, im selben Schritt, in dem er
	/// das Tier voll heilt. So gibt es keinen Augenblick mit 1 Leben und ohne Schutz.
	/// </summary>
	internal static bool IsFainted(Character character)
	{
		ZNetView? view = character != null ? character.m_nview : null;
		return view != null && view.IsValid() && view.GetZDO().GetLong(FaintHash, 0L) != 0L;
	}

	/// <summary>
	/// Wiederkehr (sehr seltene Verzauberung), beim Besitzer in Character.SetHealth: Jeder Weg zum Tod (Treffer, Brand,
	/// Gift, Lava, Ertrinken) endet hier. Das Tier bleibt bei 1 Leben, fällt x Sekunden in Ohnmacht (unsichtbar,
	/// unverwundbar, reglos, kein Ziel) und steht dann mit vollem Leben wieder auf. Die Leine ist danach frei.
	/// Hier keine Statuseffekte anfassen: SetHealth kann mitten aus Valheims Effekt-Schleife kommen (Brand, Gift).
	/// </summary>
	internal static void PreventDeath(Character animal, ref float health)
	{
		ZNetView? view = animal.m_nview;
		if (view == null || !view.IsValid() || !view.IsOwner() || ZNet.instance == null || !animal.IsTamed())
		{
			return;
		}
		if (IsFainted(animal))
		{
			health = 1f; // ohnmächtig: unverwundbar
			return;
		}
		if (!TryGet(animal, out BondStats bond) || bond.Revive <= 0f)
		{
			return;
		}
		health = 1f; // stirbt nicht: bleibt bei 1 Leben, bis es aufwacht
		view.GetZDO().Set(FaintHash, ZNet.instance.GetTime().AddSeconds(bond.Revive).Ticks);
		EnterFaint(animal);
		Link? link = Links.ForAnimal(animal);
		if (link != null)
		{
			Links.Detach(link, "gcls_fainted"); // „nicht mehr mit einer Leine verbunden"
		}
	}

	/// <summary>Alle 0,25 s für alle geladenen Tiere: Licht und Ohnmacht anzeigen (nicht jedes Bild, das kostet bei vielen Tieren).</summary>
	private static void UpdateScan()
	{
		if (Time.time < nextScan)
		{
			return;
		}
		nextScan = Time.time + 0.25f;
		foreach (Tameable tame in Rope.All)
		{
			if (tame == null || tame.m_nview == null || !tame.m_nview.IsValid())
			{
				continue;
			}
			ZDO zdo = tame.m_nview.GetZDO();
			UpdateLight(tame, zdo);
			Character? animal = tame.m_character;
			if (animal == null)
			{
				continue;
			}
			if (zdo.GetLong(FaintHash, 0L) == 0L)
			{
				LeaveFaint(animal);
				continue;
			}
			Posts.ClaimFromServer(tame.m_nview); // besitzt der Server das Tier, übernimmt ein Spieler in der Nähe
			EnterFaint(animal);
		}
		AnyFainted = Fainted.Count > 0;
	}

	private static readonly List<Character> Waking = new();

	/// <summary>
	/// Jedes Bild beim Besitzer: ohnmächtige Tiere festhalten (KI aus, keine Bewegung, sonst liefe es unsichtbar weiter),
	/// nachwirkenden Schaden entfernen (Brand, Gift, Geist; hier außerhalb von Valheims Effekt-Schleife) und nach Ablauf
	/// wecken: erst voll heilen, dann die Markierung löschen.
	/// </summary>
	private static void HoldFainted()
	{
		if (Fainted.Count == 0 || ZNet.instance == null)
		{
			return;
		}
		long now = ZNet.instance.GetTime().Ticks;
		Waking.Clear();
		foreach (Character animal in Fainted)
		{
			if (animal == null || animal.m_nview == null || !animal.m_nview.IsValid() || !animal.m_nview.IsOwner())
			{
				continue;
			}
			BaseAI? ai = animal.m_baseAI;
			if (ai != null && ai.enabled)
			{
				PausedAi[ai] = true;
				ai.enabled = false; // reglos
			}
			animal.SetMoveDir(Vector3.zero);
			animal.SetRun(false);
			ClearLingering(animal);
			long until = animal.m_nview.GetZDO().GetLong(FaintHash, 0L);
			if (until != 0L && now >= until)
			{
				Waking.Add(animal);
			}
		}
		foreach (Character animal in Waking)
		{
			animal.SetHealth(animal.GetMaxHealth()); // wieder da, mit vollem Leben (noch geschützt)
			animal.m_nview.GetZDO().Set(FaintHash, 0L);
			LeaveFaint(animal);
			Player? local = Player.m_localPlayer;
			if (local != null && Vector3.Distance(local.transform.position, animal.transform.position) < 40f)
			{
				Gc.TopLeft(GcLoc.Get("gcls_revived", animal.GetHoverName()));
			}
		}
		AnyFainted = Fainted.Count > 0;
	}

	/// <summary>Brand, Gift und Geist von einem ohnmächtigen Tier nehmen (Valheim gibt sie nach einem Treffer neu dazu).</summary>
	private static void ClearLingering(Character animal)
	{
		SEMan seman = animal.GetSEMan();
		if (seman == null)
		{
			return;
		}
		seman.RemoveStatusEffect(SEMan.s_statusEffectBurning, true);
		seman.RemoveStatusEffect(SEMan.s_statusEffectPoison, true);
		seman.RemoveStatusEffect(SEMan.s_statusEffectSpirit, true);
	}

	private static void EnterFaint(Character animal)
	{
		if (!Fainted.Add(animal))
		{
			return;
		}
		AnyFainted = true;
		SetHidden(animal, true);
		if (!OriginalSkip.ContainsKey(animal))
		{
			OriginalSkip[animal] = animal.m_aiSkipTarget;
		}
		animal.m_aiSkipTarget = true; // Gegner suchen es nicht mehr als Ziel
	}

	private static void LeaveFaint(Character animal)
	{
		if (!Fainted.Remove(animal))
		{
			return;
		}
		SetHidden(animal, false);
		if (OriginalSkip.TryGetValue(animal, out bool skip))
		{
			animal.m_aiSkipTarget = skip;
			OriginalSkip.Remove(animal);
		}
		BaseAI? ai = animal.m_baseAI;
		if (ai != null && PausedAi.Remove(ai))
		{
			ai.enabled = true;
		}
	}

	private static void SetHidden(Character animal, bool hide)
	{
		if (hide)
		{
			if (!Hidden.ContainsKey(animal) && animal.m_visual != null)
			{
				List<Renderer> renderers = new();
				foreach (Renderer renderer in animal.m_visual.GetComponentsInChildren<Renderer>())
				{
					if (renderer.enabled)
					{
						renderer.enabled = false; // nur die Modelle; der Animator läuft weiter
						renderers.Add(renderer);
					}
				}
				Hidden[animal] = renderers;
			}
			return;
		}
		if (Hidden.TryGetValue(animal, out List<Renderer> shown))
		{
			foreach (Renderer renderer in shown)
			{
				if (renderer != null)
				{
					renderer.enabled = true;
				}
			}
			Hidden.Remove(animal);
		}
	}

	/// <summary>Aufräumen zerstörter Tiere.</summary>
	internal static void Cleanup()
	{
		if (Lights.Count > 0)
		{
			DeadTames.Clear();
			foreach (Tameable tame in Lights.Keys)
			{
				if (tame == null)
				{
					DeadTames.Add(tame!);
				}
			}
			foreach (Tameable tame in DeadTames)
			{
				Lights.Remove(tame);
			}
		}
		if (Hidden.Count > 0 || Fainted.Count > 0 || OriginalSkip.Count > 0 || Watched.Count > 0)
		{
			DeadCharacters.Clear();
			foreach (Character character in Hidden.Keys)
			{
				if (character == null)
				{
					DeadCharacters.Add(character!);
				}
			}
			foreach (Character character in Fainted)
			{
				if (character == null)
				{
					DeadCharacters.Add(character!);
				}
			}
			foreach (Character character in OriginalSkip.Keys)
			{
				if (character == null)
				{
					DeadCharacters.Add(character!);
				}
			}
			foreach (Character character in Watched)
			{
				if (character == null)
				{
					DeadCharacters.Add(character!);
				}
			}
			foreach (Character character in DeadCharacters)
			{
				Hidden.Remove(character);
				Fainted.Remove(character);
				OriginalSkip.Remove(character);
				Watched.Remove(character);
			}
		}
		if (PausedAi.Count > 0)
		{
			DeadAis.Clear();
			foreach (BaseAI ai in PausedAi.Keys)
			{
				if (ai == null)
				{
					DeadAis.Add(ai!);
				}
			}
			foreach (BaseAI ai in DeadAis)
			{
				PausedAi.Remove(ai);
			}
		}
	}

	/// <summary>Neuer Spielstart: alles Gemerkte vergessen.</summary>
	internal static void Reset()
	{
		Animals.Clear();
		Holder.ClearHolder();
		OriginalFed.Clear();
		FedThisFrame.Clear();
		OriginalFear.Clear();
		BraveThisFrame.Clear();
		Hidden.Clear();
		PausedAi.Clear();
		OriginalSkip.Clear();
		Fainted.Clear();
		Lights.Clear();
		Watched.Clear();
		RevealPins.Clear();
		AnyFainted = false;
		Tamer = 0f;
	}
}

// ------------------------------------------------------------------ Spiel-Anbindung der Verzauberungen

/// <summary>Lauftempo und Schwimmen: Tier an der Leine und Halter. Wer ein Tier hinter sich herzieht, ist halb so schnell (Gorduan).</summary>
[HarmonyPatch(typeof(SEMan), nameof(SEMan.ApplyStatusEffectSpeedMods))]
internal static class BondSpeedPatch
{
	private static void Postfix(SEMan __instance, ref float speed)
	{
		Character character = __instance.m_character;
		if (character == null)
		{
			return;
		}
		if (Bonds.TryGet(character, out BondStats bond))
		{
			if (bond.Speed > 0f)
			{
				speed *= 1f + bond.Speed / 100f;
			}
			if (bond.Swim > 0f && character.IsSwimming())
			{
				speed *= 1f + bond.Swim / 100f;
			}
			return;
		}
		if (character != Player.m_localPlayer)
		{
			return;
		}
		if (Bonds.Holder.HolderSpeed > 0f)
		{
			speed *= 1f + Bonds.Holder.HolderSpeed / 100f;
		}
		if (Bonds.Holder.HolderSwim > 0f && character.IsSwimming())
		{
			speed *= 1f + Bonds.Holder.HolderSwim / 100f;
		}
		if (Links.Dragging)
		{
			speed *= Links.DragSpeedFactor;
		}
	}
}

/// <summary>Weniger Ausdauer beim Schwimmen (Njörds Seil), solange ein Tier an der Leine hängt.</summary>
[HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifySwimStaminaUsage))]
internal static class BondSwimStaminaPatch
{
	private static void Postfix(SEMan __instance, ref float staminaUse)
	{
		if (Bonds.Holder.HolderSwim > 0f && __instance.m_character == Player.m_localPlayer)
		{
			staminaUse *= Mathf.Clamp01(1f - Bonds.Holder.HolderSwim / 100f);
		}
	}
}

/// <summary>Schleichen (Lokis Blendwerk): kleinerer Faktor = Gegner sehen den Halter erst später (BaseAI: Sichtweite mal Faktor).</summary>
[HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyStealth))]
internal static class BondStealthPatch
{
	private static void Postfix(SEMan __instance, ref float stealth)
	{
		if (Bonds.Holder.HolderStealth > 0f && __instance.m_character == Player.m_localPlayer)
		{
			stealth *= Mathf.Clamp01(1f - Bonds.Holder.HolderStealth / 100f);
		}
	}
}

/// <summary>Angriffe: Tier (Schaden, Raserei, Frost, Blitz, Geist, Gift) und Halter (Ullrs Jagdriemen).</summary>
[HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyAttack))]
internal static class BondAttackPatch
{
	private static void Postfix(SEMan __instance, Skills.SkillType skill, ref HitData hitData)
	{
		Character character = __instance.m_character;
		if (character == null)
		{
			return;
		}
		if (character == Player.m_localPlayer)
		{
			if (Bonds.Holder.HolderHunt > 0f && skill != Skills.SkillType.WoodCutting && skill != Skills.SkillType.Pickaxes)
			{
				hitData.m_damage.Modify(1f + Bonds.Holder.HolderHunt / 100f); // mit einem Tier an der Leine trifft der Halter härter (nur im Kampf)
			}
			// Sattel (Gorduan, 20.10.2026): beim Reiten treffen die Angriffe des Reiters mit den Kampf-Werten des Sattels
			if (skill != Skills.SkillType.WoodCutting && skill != Skills.SkillType.Pickaxes && Saddles.RiderBond(character, out BondStats saddle))
			{
				Apply(character, saddle, ref hitData);
			}
			return;
		}
		if (Bonds.TryGet(character, out BondStats bond))
		{
			Apply(character, bond, ref hitData);
		}
	}

	/// <summary>Kampf-Werte auf einen Treffer: Schaden, Berserker, Wucht, Elemente, Walkürenpfeife.</summary>
	internal static void Apply(Character character, BondStats bond, ref HitData hitData)
	{
		float total = hitData.GetTotalDamage();
		float factor = 1f + bond.Damage / 100f;
		if (bond.Berserk > 0f && character.GetHealthPercentage() < 0.3f)
		{
			factor *= 1f + bond.Berserk / 100f;
		}
		if (factor != 1f)
		{
			hitData.m_damage.Modify(factor);
		}
		float part = total * factor / 100f;
		if (bond.StaggerPower > 0f)
		{
			// Wucht (nur Nahkampf: Geschosse übernehmen den Wert nicht); unter 100 bleiben, ab 100 taumelt jeder Getroffene sofort
			hitData.m_staggerMultiplier = Mathf.Min(hitData.m_staggerMultiplier * (1f + bond.StaggerPower / 100f), 99f);
		}
		if (bond.Fire > 0f)
		{
			hitData.m_damage.m_fire += part * bond.Fire;
		}
		if (bond.Frost > 0f)
		{
			hitData.m_damage.m_frost += part * bond.Frost;
		}
		if (bond.Lightning > 0f)
		{
			hitData.m_damage.m_lightning += part * bond.Lightning;
		}
		if (bond.Spirit > 0f)
		{
			hitData.m_damage.m_spirit += part * bond.Spirit;
		}
		if (bond.Poison > 0f)
		{
			hitData.m_damage.m_poison += part * bond.Poison;
		}
		if (bond.NormalAfter != 0f)
		{
			// Walkürenpfeife (Gorduan): erst der Blitzschaden oben, danach −5 % auf den normalen Schaden (nicht auf die Elemente):
			// der Gesamtschaden wird kaum höher, ein Teil davon ist Blitz
			float more = Mathf.Max(0f, 1f + bond.NormalAfter / 100f);
			hitData.m_damage.m_damage *= more;
			hitData.m_damage.m_blunt *= more;
			hitData.m_damage.m_slash *= more;
			hitData.m_damage.m_pierce *= more;
			hitData.m_damage.m_chop *= more;
			hitData.m_damage.m_pickaxe *= more;
		}
	}
}

/// <summary>Angriffstempo: Das Tier darf früher wieder angreifen (Takt der Tier-Angriffe).</summary>
[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
internal static class BondAttackSpeedPatch
{
	private static void Postfix(Humanoid __instance, bool __result)
	{
		if (!__result || !Bonds.TryGet(__instance, out BondStats bond) || bond.AttackSpeed <= 0f)
		{
			return;
		}
		ItemDrop.ItemData? weapon = __instance.GetCurrentWeapon();
		if (weapon?.m_shared != null)
		{
			weapon.m_lastAttackTime -= weapon.m_shared.m_aiAttackInterval * (1f - 1f / (1f + bond.AttackSpeed / 100f));
		}
	}
}

/// <summary>
/// Treffer am Tier (beim Besitzer): Ohnmacht (kein Treffer), Trugschleier (Treffer geht vorbei), Leben (weniger Schaden,
/// auch der allgemeine Schaden, den ApplyModifier auslässt), Elementarschutz, Giftfest, Feuerfest (Surts Lohe), Standfest, Dornen.
/// </summary>
[HarmonyPatch(typeof(Character), nameof(Character.RPC_Damage))]
internal static class BondDamagedPatch
{
	private static bool Prefix(Character __instance, HitData hit)
	{
		if (hit == null)
		{
			return true;
		}
		if (__instance.IsTamed() && Bonds.IsFainted(__instance))
		{
			return false; // ohnmächtig: kein Treffer, auch kein Grund- oder Weltstufen-Schaden
		}
		if (!Bonds.TryGet(__instance, out BondStats bond))
		{
			return true;
		}
		if (bond.Evade > 0f && UnityEngine.Random.value * 100f < Mathf.Min(bond.Evade, 75f))
		{
			if (DamageText.instance != null)
			{
				DamageText.instance.ShowText(DamageText.TextType.Blocked, hit.m_point, GcLoc.Get("gcls_evaded"));
			}
			return false; // Trugschleier: der Treffer geht ins Leere
		}
		float before = hit.GetTotalDamage();
		if (bond.Health > 0f)
		{
			float keep = 1f / (1f + bond.Health / 100f);
			hit.ApplyModifier(keep);
			hit.m_damage.m_damage *= keep;
		}
		if (bond.Resist > 0f)
		{
			float keep = Mathf.Clamp01(1f - bond.Resist / 100f);
			hit.m_damage.m_fire *= keep;
			hit.m_damage.m_frost *= keep;
			hit.m_damage.m_lightning *= keep;
			hit.m_damage.m_poison *= keep;
			hit.m_damage.m_spirit *= keep;
		}
		if (bond.PoisonImmune)
		{
			hit.m_damage.m_poison = 0f;
			if (hit.m_statusEffectHash == SEMan.s_statusEffectPoison)
			{
				hit.m_statusEffectHash = 0;
			}
		}
		if (bond.Fireproof)
		{
			hit.m_damage.m_fire = 0f; // Surts Lohe: ohne Feuerschaden auch kein Brennen
			if (hit.m_statusEffectHash == SEMan.s_statusEffectBurning)
			{
				hit.m_statusEffectHash = 0;
			}
		}
		if (bond.Steadfast)
		{
			hit.m_pushForce = 0f;
			hit.m_staggerMultiplier = 0f;
		}
		Character? attacker = hit.GetAttacker();
		if (bond.Thorns > 0f && attacker != null && attacker != __instance && !attacker.IsPlayer() && before > 0f)
		{
			HitData reflect = new();
			reflect.m_damage.m_damage = before * bond.Thorns / 100f;
			reflect.m_point = attacker.GetCenterPoint();
			reflect.m_dir = (attacker.transform.position - __instance.transform.position).normalized;
			attacker.Damage(reflect);
		}
		if (bond.Deter > 0f && attacker != null && attacker != __instance && !attacker.IsPlayer() && attacker.m_staggerWhenBlocked && !attacker.IsBoss()
			&& UnityEngine.Random.value * 100f < bond.Deter) // wie Valheims Parade: nur wer überhaupt taumeln kann, keine Bosse
		{
			attacker.Stagger((attacker.transform.position - __instance.transform.position).normalized); // Abschreckung
		}
		return true;
	}
}

/// <summary>
/// Lebensraub (nach EpicLoots LifeSteal): Treffer des Tiers heilen es um x % des Schadens. Läuft beim Angreifer, also
/// beim Halter, der das Tier besitzt (Character.Damage schickt den Treffer von dort an den Besitzer des Ziels).
/// </summary>
[HarmonyPatch(typeof(Character), nameof(Character.Damage))]
internal static class BondLifeStealPatch
{
	private static void Prefix(Character __instance, HitData hit)
	{
		if (hit == null || (Bonds.Animals.Count == 0 && Saddles.RiddenMount(Player.m_localPlayer) == null))
		{
			return;
		}
		Character? attacker = hit.GetAttacker();
		if (attacker == null || attacker == __instance || attacker.IsDead())
		{
			return;
		}
		// Tier an der Leine, oder der Reiter mit einem verzauberten Sattel (Gorduan, 20.10.2026)
		if ((!Bonds.TryGet(attacker, out BondStats bond) && !Saddles.RiderBond(attacker, out bond)) || bond.LifeSteal <= 0f)
		{
			return;
		}
		float damage = hit.GetTotalDamage() - hit.m_damage.m_chop - hit.m_damage.m_pickaxe; // Hack- und Spitzhackenschaden treffen Kreaturen nicht (wie bei EpicLoot)
		if (damage > 0f && attacker.GetHealth() < attacker.GetMaxHealth())
		{
			attacker.Heal(damage * bond.LifeSteal / 100f, false);
		}
	}
}

/// <summary>Ohnmächtige Tiere nehmen gar keinen Schaden: auch nicht durch Brand, Gift, Lava oder Ertrinken (die laufen nicht über RPC_Damage).</summary>
[HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
internal static class BondFaintShieldPatch
{
	private static bool Prefix(Character __instance) => !(__instance.IsTamed() && Bonds.IsFainted(__instance));
}

/// <summary>
/// Surts Lohe (Feuerfest): auch ein schon laufender Brand schadet nicht. Sein Schaden geht über ApplyDamage, nicht über
/// RPC_Damage (Prüfung 24.10.2026). Lava macht in Valheim allgemeinen Schaden, kein Feuer: dagegen hilft es nicht.
/// </summary>
[HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
internal static class BondFireproofPatch
{
	private static void Prefix(Character __instance, HitData hit)
	{
		if (hit != null && hit.m_damage.m_fire > 0f && __instance.IsTamed() && Bonds.TryGet(__instance, out BondStats bond) && bond.Fireproof)
		{
			hit.m_damage.m_fire = 0f;
		}
	}
}

/// <summary>Wiederkehr: Jeder Weg zum Tod schreibt das Leben über SetHealth; dort bleibt das Tier bei 1 Leben (siehe Bonds.PreventDeath).</summary>
[HarmonyPatch(typeof(Character), nameof(Character.SetHealth))]
internal static class BondUndyingPatch
{
	private static void Prefix(Character __instance, ref float health)
	{
		if (health > 0f || __instance.IsPlayer())
		{
			return;
		}
		if (Illusions.IsIllusion(__instance))
		{
			health = 1f; // ein Trugbild stirbt nicht wie ein Tier (kein Tod-Ereignis, keine Beute, keine Zählung): es zerstiebt
			Illusions.MarkKilled(__instance);
			return;
		}
		Bonds.PreventDeath(__instance, ref health);
	}
}

/// <summary>Ein ohnmächtiges (unsichtbares) Tier lässt sich nicht reiten.</summary>
[HarmonyPatch(typeof(Sadle), nameof(Sadle.Interact))]
internal static class BondFaintSaddlePatch
{
	private static bool Prefix(Sadle __instance, ref bool __result)
	{
		if (!Bonds.AnyFainted || __instance.m_character == null || !Bonds.IsFainted(__instance.m_character))
		{
			return true;
		}
		__result = false;
		return false;
	}
}

/// <summary>Tragkraft des Halters.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.GetMaxCarryWeight))]
internal static class BondCarryPatch
{
	private static void Postfix(Player __instance, ref float __result)
	{
		if (Bonds.Holder.HolderCarry > 0f && __instance == Player.m_localPlayer)
		{
			__result += Bonds.Holder.HolderCarry * Game.m_carryWeightRate;
		}
	}
}

/// <summary>
/// Zähmhilfe: Tiere in 10 m um einen Spieler mit so einer Leine in der Schnellleiste werden schneller zahm. Das Zähmen
/// läuft beim Besitzer des Tiers; die Werte der Spieler in der Nähe stehen in ihren Spieler-ZDOs (höchster zählt).
/// </summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.DecreaseRemainingTime))]
internal static class BondTamingPatch
{
	private static readonly List<Player> Nearby = new();

	private static void Prefix(Tameable __instance, ref float time) => time *= Factor(__instance.transform.position);

	/// <summary>Zähmtempo aus der Zähmhilfe an dieser Stelle (1 = keine), auch für den Zähm-Countdown (Countdown).</summary>
	internal static float Factor(Vector3 position)
	{
		Nearby.Clear();
		Player.GetPlayersInRange(position, 10f, Nearby);
		float best = 0f;
		foreach (Player player in Nearby)
		{
			ZNetView? view = player != null ? player.m_nview : null;
			if (view != null && view.IsValid())
			{
				best = Mathf.Max(best, view.GetZDO().GetFloat(Bonds.TamerHash, 0f));
			}
		}
		return 1f + best / 100f;
	}
}

/// <summary>Mehr Haltbarkeit durch Verzauberung (auch für Reparatur und Anzeige).</summary>
[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetMaxDurability), new Type[] { typeof(int) })]
internal static class BondDurabilityPatch
{
	private static void Postfix(ItemDrop.ItemData __instance, ref float __result)
	{
		if (LeashMagic.Available && Plugin.IsLeash(__instance, out _))
		{
			__result += LeashMagic.Read(__instance).Durability;
		}
	}
}
