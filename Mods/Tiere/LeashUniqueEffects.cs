using System;
using System.Collections.Generic;
using System.Globalization;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Wirkung der Sondereffekte der einzigartigen Leinen (Gorduan, 27.10.2026: „setze es um“). Werte, Effekte und Stücke stehen in
/// LeashUniques.cs. Gerechnet wird wie bei allen Leinen-Verzauberungen beim Halter, der sein Tier besitzt (Bonds). Was andere
/// Rechner brauchen, steht im Tier (Mímir, Hel), im Spieler (Rudelfrieden) oder geht als Nachricht an den Besitzer eines
/// Gegners (Bärenwacht).
///  * Draupnir (Fertility): Liebespunkte zählen doppelt und die Trächtigkeit dauert halb so lang (zusammen doppelt so schnell),
///    mit x % kommt gleich danach ein zweites Junges. Auch am Pfosten: Die Leine steckt dann im Knoten.
///  * Ratatöskr (Fetch): Hängt das Tier über 4 s hinter der Zuggrenze fest oder rutschte die Leine gleich aus der Hand, springt
///    es neben den Halter (TickLink).
///  * Tyr (Guard): x % jedes Treffers gegen den Halter nimmt das Tier (bis 15 m), vor Rüstung wie in Valheims RPC_Damage.
///  * Mímir (Wisdom): Tier-Erfahrung und die Tierpflege des Halters aus diesem Tier +x % (AnimalExperience, Care).
///  * Hel (Reap): Tötet das Tier einen Gegner, heilt der Halter x % und das Tier 2x % seines Höchstlebens.
///  * Nornen (Fate): Ein tödlicher Treffer gegen den Halter geht ins Leere, ein Tier (bis 30 m) fällt dafür 30 s in Ohnmacht;
///    10 Minuten Abklingzeit, im Spieler gespeichert.
///  * Arten-Leinen: Leben und Schaden +x % nur bei ihrer Art (LeashBond), dazu Rudelfrieden (Wolf), Wühler (Eber), Lasttier
///    (Lox, LeashBond), Feuerschreiter (Asksvin), Geweihstoß (Elch) und Bärenwacht (Bär, Bärenjunges, Unbjørn).
/// </summary>
internal static class LeashUniqueEffects
{
	/// <summary>Im Tier (schreibt der Halter): Mímirs Weisheit seiner Leine in %, damit auch der Besitzer eines Opfers sie kennt.</summary>
	internal static readonly int WisdomHash = "gc_leash_wisdom".GetStableHashCode();
	/// <summary>Im Tier (schreibt der Halter): Hels Ernte seiner Leine in %.</summary>
	internal static readonly int ReapHash = "gc_leash_reap".GetStableHashCode();
	/// <summary>Im Spieler: Ein Wolf mit Rudelfrieden hängt an seiner aktiven Leine (die Besitzer wilder Wölfe lesen es).</summary>
	private static readonly int PeaceHash = "gc_leash_packpeace".GetStableHashCode();
	/// <summary>Im wilden Wolf: gereizt bis (Weltzeit in Ticks), so lange gilt der Rudelfrieden für ihn nicht.</summary>
	private static readonly int ProvokedHash = "gc_leash_provoked".GetStableHashCode();
	/// <summary>Im Muttertier: Die nächste Geburt ist Draupnirs Zwilling (würfelt nicht noch einmal).</summary>
	private static readonly int TwinHash = "gc_leash_twin".GetStableHashCode();
	/// <summary>Im Muttertier: die Trächtigkeit (Zeitpunkt in Ticks), die Draupnir schon verkürzt hat.</summary>
	private static readonly int HasteHash = "gc_leash_haste".GetStableHashCode();
	/// <summary>Im Spieler (m_customData, mit dem Charakter gespeichert): Die Nornen helfen wieder ab (Weltzeit in s).</summary>
	private const string FateKey = "gc_leash_fate";
	private const string BearGuardRpc = "GC_LeashBearGuard";

	// Arten wie in LeashMagic.SpeciesOf
	private const string Wolf = "Wolf";
	private const string Boar = "Boar";
	private const string Lox = "Lox";
	private const string Asksvin = "Asksvin";
	private const string Bear = "Bjorn";
	private const string Moose = "Moose";

	private static readonly int WolfPrefab = "Wolf".GetStableHashCode();
	private static readonly int WolfCubPrefab = "Wolf_cub".GetStableHashCode();

	private const float StuckSeconds = 4f;        // Ratatöskr: so lange ohne Fortschritt hinter der Zuggrenze
	private const float StuckProgress = 0.5f;     // so viel näher muss es kommen, damit es nicht als festgehangen gilt
	private const float SlipMargin = 1.5f;        // spätestens so kurz, bevor die Leine aus der Hand rutschen würde
	private const float FetchPause = 3f;          // kein Hin und Her zwischen zwei Sprüngen
	private const float GuardRange = 15f;
	private const float GuardCap = 75f;           // alle Tiere zusammen höchstens so viel % (wie ApplyUnique je Leine)
	private const float FateRange = 30f;
	private const float FateFaint = 30f;          // Wiederkehr-Zeit (Gorduan, auch ohne eigene Wiederkehr-Verzauberung)
	private const double FateCooldown = 600.0;    // 10 Minuten je Halter
	private const float FateGrace = 1f;           // derselbe Augenblick: weitere tödliche Treffer gehen auch ins Leere
	private const float PeaceRange = 30f;
	private const double ProvokedSeconds = 60.0;
	private const float BearGuardInterval = 2f;
	private const float WisdomMax = 100f;

	/// <summary>Größter Faktor aus Mímirs Weisheit (für die Obergrenze fremder Erfahrungs-Nachrichten, Care.MaxGrant).</summary>
	internal const float WisdomFactorMax = 1f + WisdomMax / 100f;

	/// <summary>Bei jedem Spielstart (GameStartPatch): Bärenwacht für Gegner, die wir besitzen.</summary>
	internal static void RegisterRpcs() => ZRoutedRpc.instance?.Register<ZDOID, ZDOID, ZDOID>(BearGuardRpc, RPC_BearGuard);

	/// <summary>Neuer Spielstart: alles Gemerkte vergessen.</summary>
	internal static void Reset()
	{
		KnotBonds.Clear();
		PeaceHolders.Clear();
		nextPeaceScan = 0f;
		OriginalTolerate.Clear();
		FireproofNow.Clear();
		fateAnimal = null;
		fateCleanse = false;
		fateGraceUntil = 0f;
	}

	// ------------------------------------------------------------------ Jedes Bild beim Halter (nur eigene Leinen)

	internal static void Tick()
	{
		Player? local = Player.m_localPlayer;
		ResolveFate(local); // zuerst: hängt nicht davon ab, dass der Rest dieses Bildes klappt
		bool peace = false;
		if (local != null && Links.Active.Count > 0)
		{
			float now = Time.time;
			foreach (Link link in Links.Active)
			{
				if (link.Holder != local || link.Animal == null || link.View == null || !link.View.IsValid())
				{
					continue;
				}
				BondStats bond = link.Bond;
				Publish(link.View, bond);
				Character animal = link.Animal;
				if (link.Traveling || !bond.UniqueAnimalAny || animal.IsDead())
				{
					continue;
				}
				string species = LeashMagic.LeashSpecies(animal);
				if (bond.PackPeace && species == Wolf)
				{
					peace = true;
				}
				if (bond.FireWalker && species == Asksvin && link.View.IsOwner())
				{
					Fireproof(animal);
				}
				if (bond.Forage > 0f && species == Boar)
				{
					Forage(link);
				}
				if (bond.BearGuard > 0f && species == Bear && now >= link.NextBearGuard && !Bonds.IsFainted(animal))
				{
					link.NextBearGuard = now + BearGuardInterval;
					Taunt(local, animal, bond.BearGuard);
				}
			}
		}
		RestoreFireproof();
		PublishPeace(local, peace);
	}

	/// <summary>Mímir und Hel ins Tier schreiben (nur bei Änderung), damit auch der Besitzer eines Opfers sie kennt.</summary>
	private static void Publish(ZNetView view, BondStats bond)
	{
		if (!view.IsOwner())
		{
			return;
		}
		ZDO zdo = view.GetZDO();
		Write(zdo, WisdomHash, Mathf.Clamp(bond.Wisdom, 0f, WisdomMax));
		Write(zdo, ReapHash, Mathf.Clamp(bond.Reap, 0f, 100f));
	}

	private static void Write(ZDO zdo, int hash, float value)
	{
		if (Mathf.Abs(zdo.GetFloat(hash, 0f) - value) > 0.01f)
		{
			zdo.Set(hash, value);
		}
	}

	/// <summary>Das Tier hängt an keiner Leine mehr (Links.ClearLengths, beim Besitzer): Mímir und Hel gehören zur Leine.</summary>
	internal static void ClearAnimal(ZDO zdo)
	{
		Write(zdo, WisdomHash, 0f);
		Write(zdo, ReapHash, 0f);
	}

	// ------------------------------------------------------------------ Arten-Leinen

	/// <summary>Arten-Leine: Leben und Schaden +x %, nur wenn das Tier zur Art der Leine passt (sonst 0).</summary>
	internal static float Kin(BondStats bond, Character? animal)
	{
		return bond.KinBonus > 0f && bond.KinSpecies.Length > 0 && LeashMagic.LeashSpecies(animal) == bond.KinSpecies ? bond.KinBonus : 0f;
	}

	/// <summary>Auðhumla (Lasttier): Tragkraft für den Halter, solange ein Lox an dieser Leine hängt (Bonds.Tick).</summary>
	internal static float Carry(Character? animal, BondStats bond) => bond.Carrier > 0f && LeashMagic.LeashSpecies(animal) == Lox ? bond.Carrier : 0f;

	/// <summary>Níðhögg (Feuerschreiter): gilt nur an einem Asksvin.</summary>
	internal static bool FireWalker(BondStats bond, Character animal) => bond.FireWalker && LeashMagic.LeashSpecies(animal) == Asksvin;

	private static readonly Dictionary<Character, bool> OriginalTolerate = new();
	private static readonly HashSet<Character> FireproofNow = new();
	private static readonly List<Character> Restore = new();

	/// <summary>
	/// Feuerschreiter: Valheims eigener Schutz der Aschlande-Bewohner (m_tolerateFire: keine Hitze durch Lava und heißes Meer, also
	/// auch kein Brennen davon), solange das Asksvin an der Leine hängt; ein Brand vom Kampf erlischt. Treffer: LeashBond.
	/// </summary>
	private static void Fireproof(Character animal)
	{
		if (!OriginalTolerate.ContainsKey(animal))
		{
			OriginalTolerate[animal] = animal.m_tolerateFire;
		}
		animal.m_tolerateFire = true;
		FireproofNow.Add(animal);
		SEMan seman = animal.GetSEMan();
		if (seman != null && seman.HaveStatusEffect(SEMan.s_statusEffectBurning))
		{
			seman.RemoveStatusEffect(SEMan.s_statusEffectBurning, true); // hier außerhalb von Valheims Effekt-Schleife
		}
	}

	private static void RestoreFireproof()
	{
		if (OriginalTolerate.Count == 0)
		{
			FireproofNow.Clear();
			return;
		}
		Restore.Clear();
		foreach (KeyValuePair<Character, bool> entry in OriginalTolerate)
		{
			if (entry.Key == null || !FireproofNow.Contains(entry.Key))
			{
				Restore.Add(entry.Key!);
			}
		}
		foreach (Character animal in Restore)
		{
			if (animal != null && OriginalTolerate.TryGetValue(animal, out bool tolerate))
			{
				animal.m_tolerateFire = tolerate;
			}
			OriginalTolerate.Remove(animal!);
		}
		FireproofNow.Clear();
	}

	/// <summary>Eikþyrnir (Geweihstoß, Character.Damage beim Angreifer): Ein Treffer des Elchs wirft den Gegner mit x % um, keine Bosse.</summary>
	internal static void Antlers(Character victim, HitData hit)
	{
		if (hit == null || Bonds.Animals.Count == 0 || victim.IsPlayer() || victim.IsTamed() || victim.IsBoss() || victim.IsDead())
		{
			return;
		}
		Character? attacker = hit.GetAttacker();
		if (attacker == null || attacker == victim || !Bonds.TryGet(attacker, out BondStats bond) || bond.Antlers <= 0f || hit.GetTotalDamage() <= 0f)
		{
			return;
		}
		if (LeashMagic.LeashSpecies(attacker) == Moose && UnityEngine.Random.value * 100f < Mathf.Min(bond.Antlers, 75f))
		{
			hit.m_staggerMultiplier = Mathf.Max(hit.m_staggerMultiplier, 100f); // ab 100 taumelt der Getroffene sofort (Valheims RPC_Damage)
		}
	}

	// ------------------------------------------------------------------ Sæhrimnir: Wühler (Eber)

	// Was ein Eber je Biom aufwühlt: nur Vanilla-Prefabs aus Valheims Pflanzen (Namen geprüft an der Pflückliste von ValheimPlus
	// 0.10.3 und den Futterlisten von GC_Tierarten); fehlt eines im Spiel, entfällt es. Tiefer Norden und Meer: nichts.
	private static readonly string[] MeadowFinds = { "Mushroom", "Raspberry", "Dandelion" };
	private static readonly string[] ForestFinds = { "Mushroom", "MushroomYellow", "Blueberries", "Thistle" };
	private static readonly string[] SwampFinds = { "MushroomYellow", "Thistle" };
	private static readonly string[] MountainFinds = { "MushroomBlue" };
	private static readonly string[] PlainsFinds = { "Cloudberry" };
	private static readonly string[] MistFinds = { "MushroomJotunPuffs", "MushroomMagecap" };
	private static readonly string[] AshFinds = { "MushroomSmokePuff", "Vineberry", "Fiddleheadfern" };

	private static string[] FindsIn(Heightmap.Biome biome) => biome switch
	{
		Heightmap.Biome.Meadows => MeadowFinds,
		Heightmap.Biome.BlackForest => ForestFinds,
		Heightmap.Biome.Swamp => SwampFinds,
		Heightmap.Biome.Mountain => MountainFinds,
		Heightmap.Biome.Plains => PlainsFinds,
		Heightmap.Biome.Mistlands => MistFinds,
		Heightmap.Biome.AshLands => AshFinds,
		_ => Array.Empty<string>(),
	};

	/// <summary>
	/// Wühler: Nur die Zeit, in der der Halter unterwegs ist und der Eber ihm folgt (nicht kämpft), zählt (Gorduan: nicht in der Basis
	/// spammen). Alle x s wirft der Eber ein Fundstück aus dem Biom ab; in einer Basis wartet der Fund, bis er draußen ist.
	/// </summary>
	private static void Forage(Link link)
	{
		if (link.Ridden || link.GoTo != null || !Behaviour.Moving(link.Holder) || (link.Ai != null && link.Ai.m_targetCreature != null))
		{
			return; // wartet an einem Befehlsziel, kämpft oder der Halter steht: läuft nicht mit
		}
		link.ForageTime += Time.deltaTime;
		if (link.ForageTime < link.Bond.Forage)
		{
			return;
		}
		Vector3 at = link.Animal.transform.position;
		if (EffectArea.IsPointInsideArea(at, EffectArea.Type.PlayerBase) != null)
		{
			link.ForageTime = link.Bond.Forage; // in der Basis nichts finden, gleich draußen
			return;
		}
		link.ForageTime = 0f;
		if (WorldGenerator.instance == null || ObjectDB.instance == null)
		{
			return;
		}
		string[] finds = FindsIn(WorldGenerator.instance.GetBiome(at));
		GameObject? prefab = finds.Length > 0 ? ObjectDB.instance.GetItemPrefab(finds[UnityEngine.Random.Range(0, finds.Length)]) : null;
		if (prefab == null)
		{
			return;
		}
		// wie Valheims Pflanzen (Pickable.Drop): Item erscheint und springt leicht hoch
		GameObject go = UnityEngine.Object.Instantiate(prefab, at + Vector3.up * 0.6f, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
		if (go.GetComponent<ItemDrop>() is ItemDrop drop)
		{
			ItemDrop.OnCreateNew(drop);
		}
		if (go.GetComponent<Rigidbody>() is Rigidbody body)
		{
			body.linearVelocity = Vector3.up * 3f;
		}
	}

	// ------------------------------------------------------------------ Bjarki: Bärenwacht

	private static readonly List<Character> Nearby = new();

	/// <summary>
	/// Alle 2 s: Gegner (keine Bosse) bis x m um den Halter, die ihn angreifen, nehmen den Bären als Ziel. Das Ziel kennt nur der
	/// Besitzer des Gegners: eigene direkt, fremde per Nachricht an ihn (dort wird geprüft, ob er wirklich den Halter angreift).
	/// </summary>
	private static void Taunt(Player holder, Character bear, float radius)
	{
		Nearby.Clear();
		Character.GetCharactersInRange(holder.transform.position, radius, Nearby);
		foreach (Character enemy in Nearby)
		{
			if (enemy == null || enemy == bear || enemy.IsPlayer() || enemy.IsTamed() || enemy.IsDead() || enemy.IsBoss() || enemy.GetBaseAI() is not MonsterAI ai || !ai.HaveTarget())
			{
				continue; // HaveTarget steht im ZDO: Gegner ohne Ziel bekommen keine Nachricht
			}
			ZNetView? view = enemy.m_nview;
			if (view == null || !view.IsValid() || !BaseAI.IsEnemy(enemy, bear))
			{
				continue;
			}
			if (view.IsOwner())
			{
				Redirect(enemy, holder, bear);
			}
			else if (ZRoutedRpc.instance != null && view.GetZDO().GetOwner() != 0L)
			{
				ZRoutedRpc.instance.InvokeRoutedRPC(view.GetZDO().GetOwner(), BearGuardRpc, view.GetZDO().m_uid, holder.GetZDOID(), bear.GetZDOID());
			}
		}
	}

	private static void RPC_BearGuard(long sender, ZDOID enemyId, ZDOID holderId, ZDOID bearId)
	{
		ZNetScene? scene = ZNetScene.instance;
		GameObject? enemy = scene != null ? scene.FindInstance(enemyId) : null;
		GameObject? holder = scene != null ? scene.FindInstance(holderId) : null;
		GameObject? bear = scene != null ? scene.FindInstance(bearId) : null;
		if (enemy != null && holder != null && bear != null && enemy.GetComponent<Character>() is Character e && holder.GetComponent<Character>() is Character h
			&& bear.GetComponent<Character>() is Character b)
		{
			Redirect(e, h, b);
		}
	}

	/// <summary>Beim Besitzer des Gegners: Greift er den Halter an, wird der Bär sein Ziel (Valheims Zielsuche wartet so lange).</summary>
	private static void Redirect(Character enemy, Character holder, Character bear)
	{
		ZNetView? view = enemy.m_nview;
		if (view == null || !view.IsValid() || !view.IsOwner() || enemy.IsBoss() || enemy.IsDead() || enemy.GetBaseAI() is not MonsterAI ai)
		{
			return;
		}
		if (ai.m_targetCreature != holder || bear.IsDead() || !bear.IsTamed() || Bonds.IsFainted(bear) || !BaseAI.IsEnemy(enemy, bear))
		{
			return;
		}
		ai.m_targetCreature = bear;
		ai.m_targetStatic = null;
		ai.m_lastKnownTargetPos = bear.transform.position;
		ai.m_beenAtLastPos = false;
		ai.m_timeSinceSensedTargetCreature = 0f;
		ai.m_updateTargetTimer = Mathf.Max(ai.m_updateTargetTimer, BearGuardInterval + 0.5f); // sonst nähme die Zielsuche gleich wieder den Halter
		ai.SetTargetInfo(bear.GetZDOID());
	}

	// ------------------------------------------------------------------ Geri und Freki: Rudelfrieden (Wolf)

	private static readonly List<Player> PeaceHolders = new();
	private static float nextPeaceScan;

	/// <summary>Hält gerade ein Spieler einen Wolf mit Rudelfrieden? Schnelle Vorprüfung, die Liste wird höchstens jede Sekunde neu gelesen.</summary>
	internal static bool PeaceActive
	{
		get
		{
			if (Time.time >= nextPeaceScan)
			{
				ScanPeace();
			}
			return PeaceHolders.Count > 0;
		}
	}

	/// <summary>Auf jedem Rechner (auch dem Server, der wilde Wölfe besitzen kann): Spieler mit Rudelfrieden aus ihren Spieler-ZDOs.</summary>
	private static void ScanPeace()
	{
		nextPeaceScan = Time.time + 1f;
		PeaceHolders.Clear();
		foreach (Player player in Player.GetAllPlayers())
		{
			ZNetView? view = player != null ? player.m_nview : null;
			if (view != null && view.IsValid() && view.GetZDO().GetBool(PeaceHash))
			{
				PeaceHolders.Add(player!);
			}
		}
	}

	private static void PublishPeace(Player? local, bool peace)
	{
		ZNetView? view = local != null ? local.m_nview : null;
		if (view == null || !view.IsValid() || !view.IsOwner())
		{
			return;
		}
		ZDO zdo = view.GetZDO();
		if (zdo.GetBool(PeaceHash) != peace)
		{
			zdo.Set(PeaceHash, peace);
			nextPeaceScan = 0f; // gleich neu lesen
		}
	}

	/// <summary>
	/// Rudelfrieden (BaseAI.IsEnemy, beim Besitzer des wilden Wolfs): Ist „wolf“ ein wilder Wolf bis 30 m um einen Halter mit
	/// Rudelfrieden und „other“ dieser Halter oder ein Tier an seiner Leine, sind sie keine Feinde. Ein Wolf, den der Halter oder
	/// seine Tiere angreifen, wehrt sich 60 s lang (sonst ließe er sich ohne Gegenwehr erlegen).
	/// </summary>
	internal static bool Peaceful(Character wolf, Character other, bool evenProvoked = false)
	{
		if (!IsWildWolf(wolf))
		{
			return false;
		}
		Player? holder = PeaceHolderOf(other);
		if (holder == null || (wolf.transform.position - holder.transform.position).sqrMagnitude > PeaceRange * PeaceRange)
		{
			return false;
		}
		return evenProvoked || !Provoked(wolf);
	}

	private static bool IsWildWolf(Character? character)
	{
		if (character == null || character.IsPlayer() || character.IsTamed())
		{
			return false;
		}
		ZNetView? view = character.m_nview;
		if (view == null || !view.IsValid())
		{
			return false;
		}
		int prefab = view.GetZDO().GetPrefab();
		return prefab == WolfPrefab || prefab == WolfCubPrefab;
	}

	/// <summary>Der Halter mit Rudelfrieden zu einem Spieler oder zahmen Tier (an seiner Leine oder sein Trugbild), sonst null.</summary>
	private static Player? PeaceHolderOf(Character? character)
	{
		if (character == null)
		{
			return null;
		}
		if (character is Player player)
		{
			return PeaceHolders.Contains(player) ? player : null;
		}
		if (!character.IsTamed())
		{
			return null;
		}
		ZNetView? view = character.m_nview;
		if (view == null || !view.IsValid())
		{
			return null;
		}
		ZDO zdo = view.GetZDO();
		long session = zdo.GetLong(Plugin.HolderHash, 0L);
		if (session == 0L)
		{
			session = zdo.GetLong(Illusions.IllusionHash, 0L);
		}
		if (session == 0L)
		{
			return null;
		}
		foreach (Player holder in PeaceHolders)
		{
			if (holder != null && Links.Session(holder) == session)
			{
				return holder;
			}
		}
		return null;
	}

	private static bool Provoked(Character wolf)
	{
		long until = wolf.m_nview.GetZDO().GetLong(ProvokedHash, 0L);
		return until != 0L && ZNet.instance != null && ZNet.instance.GetTime().Ticks < until;
	}

	/// <summary>Beim Besitzer des wilden Wolfs (RPC_Damage): Trifft ihn der Halter oder eines seiner Tiere, ist der Frieden für ihn 60 s vorbei.</summary>
	internal static void Provoke(Character victim, HitData hit)
	{
		ZNetView? view = victim.m_nview;
		if (view == null || !view.IsValid() || !view.IsOwner() || ZNet.instance == null || !IsWildWolf(victim))
		{
			return;
		}
		Character? attacker = hit.GetAttacker();
		if (attacker != null && Peaceful(victim, attacker, evenProvoked: true))
		{
			view.GetZDO().Set(ProvokedHash, ZNet.instance.GetTime().AddSeconds(ProvokedSeconds).Ticks);
		}
	}

	// ------------------------------------------------------------------ Mímir und Hel

	/// <summary>Mímir: Faktor für Erfahrung aus diesem Tier (1 = keine Weisheit), nur solange es an einer Leine hängt.</summary>
	internal static float Wisdom(ZDO? zdo)
	{
		return zdo != null && zdo.GetLong(Plugin.HolderHash, 0L) != 0L ? 1f + Mathf.Clamp(zdo.GetFloat(WisdomHash, 0f), 0f, WisdomMax) / 100f : 1f;
	}

	/// <summary>Mímir für die Tierpflege eines Spielers (Sitzung): nur, wenn er gerade der Halter dieses Tiers ist.</summary>
	internal static float WisdomFor(ZDO? zdo, long session)
	{
		return zdo != null && session != 0L && zdo.GetLong(Plugin.HolderHash, 0L) == session ? Wisdom(zdo) : 1f;
	}

	/// <summary>
	/// Hels Ernte (Character.OnDeath beim Besitzer des Opfers, Kill-Erkennung wie die Kill-Erfahrung): Hat ein Tier an einer Leine
	/// mit Hel einen Gegner getötet, heilt der Halter x % seines Höchstlebens und das Tier 2x % (Heal geht an den jeweiligen
	/// Besitzer). Den Wert kennt der Besitzer des Opfers aus dem Tier (Publish).
	/// </summary>
	internal static void Reap(Character victim)
	{
		ZNetView? view = victim.m_nview;
		if (victim.IsPlayer() || victim.IsTamed() || view == null || !view.IsValid() || !view.IsOwner())
		{
			return;
		}
		Character? attacker = victim.m_lastHit?.GetAttacker();
		ZNetView? attackerView = attacker != null ? attacker.m_nview : null;
		if (attacker == null || attackerView == null || !attackerView.IsValid() || attacker.IsPlayer() || !attacker.IsTamed() || attacker.IsDead())
		{
			return;
		}
		ZDO zdo = attackerView.GetZDO();
		long session = zdo.GetLong(Plugin.HolderHash, 0L);
		float reap = Mathf.Clamp(zdo.GetFloat(ReapHash, 0f), 0f, 100f);
		if (session == 0L || reap <= 0f)
		{
			return;
		}
		Player? holder = Links.FindBySession(session);
		if (holder != null && !holder.IsDead())
		{
			holder.Heal(holder.GetMaxHealth() * reap / 100f, true);
		}
		attacker.Heal(attacker.GetMaxHealth() * 2f * reap / 100f, true);
	}

	// ------------------------------------------------------------------ Tyr: Tyrs Pfand

	private static readonly List<(Character Animal, float Share)> Guards = new();

	/// <summary>
	/// Tyrs Pfand (RPC_Damage des eigenen Spielers, nach den übrigen Mods und vor Rüstung und Widerständen wie Valheim): Jedes
	/// Tier mit Tyr an einer aktiven Leine (lebt, nicht ohnmächtig, höchstens 15 m entfernt) nimmt seinen Anteil des Treffers
	/// selbst (über seine eigenen Widerstände und Leinen-Werte), der Halter entsprechend weniger. Nur Treffer eines Gegners,
	/// nichts, was Valheim ohnehin verwirft (Ausweichen, Schild, Teleport). Das Tier ist kein Spieler: keine Rekursion.
	/// </summary>
	internal static void Guard(Player player, HitData hit)
	{
		ZNetView? view = player.m_nview;
		if (view == null || !view.IsValid() || !view.IsOwner() || player.IsDead() || player.GetHealth() <= 0f || player.IsTeleporting() || player.InCutscene()
			|| player.IsDebugFlying() || (hit.m_dodgeable && player.IsDodgeInvincible()) || (hit.m_blockable && player.IsBlocking()))
		{
			return;
		}
		Character? attacker = hit.GetAttacker();
		if (attacker == null || attacker == player || attacker.IsPlayer() || hit.GetTotalDamage() <= 0f)
		{
			return;
		}
		Guards.Clear();
		float total = 0f;
		Vector3 at = player.transform.position;
		foreach (Link link in Links.Active)
		{
			if (total >= GuardCap || link.Holder != player || link.Traveling || link.Animal == null || link.Animal == attacker || link.Bond.Guard <= 0f)
			{
				continue;
			}
			Character animal = link.Animal;
			if (animal.IsDead() || animal.GetHealth() <= 0f || Bonds.IsFainted(animal) || (animal.transform.position - at).sqrMagnitude > GuardRange * GuardRange)
			{
				continue;
			}
			float share = Mathf.Min(link.Bond.Guard, GuardCap - total);
			Guards.Add((animal, share));
			total += share;
		}
		if (Guards.Count == 0)
		{
			return;
		}
		foreach ((Character animal, float share) in Guards)
		{
			HitData part = hit.Clone();
			Scale(part, share / 100f);
			part.m_damage.m_nonPlayer = 0f; // trifft nur Nicht-Spieler: beim Halter wirkungslos, das Tier bekäme ihn voll
			part.m_point = animal.GetCenterPoint();
			part.m_pushForce = 0f;
			part.m_staggerMultiplier = Mathf.Min(part.m_staggerMultiplier, 1f);
			part.m_statusEffectHash = 0; // Brand, Gift usw. des Treffers bleiben beim Halter
			part.m_blockable = false;
			part.m_dodgeable = false;
			part.m_backstabBonus = 1f;
			part.m_healthReturn = 0f;
			part.m_eitrAdd = 0f;
			part.m_hitCollider = null;
			animal.Damage(part); // der Angreifer bleibt: das Tier wehrt sich gegen ihn
		}
		Scale(hit, 1f - total / 100f);
	}

	private static void Scale(HitData hit, float factor)
	{
		hit.ApplyModifier(factor);
		hit.m_damage.m_damage *= factor; // ApplyModifier lässt den allgemeinen Schaden aus (wie in BondDamagedPatch)
		hit.m_damage.m_nonPlayer *= factor;
	}

	// ------------------------------------------------------------------ Nornen: Schicksalstausch

	private static float fateGraceUntil;
	private static Character? fateAnimal;
	private static bool fateCleanse;

	/// <summary>
	/// Nornen (ApplyDamage des eigenen Spielers, nach Rüstung und Widerständen): Würde der Treffer ihn töten und hängt ein Tier
	/// mit Fate an einer aktiven Leine (lebt, nicht ohnmächtig, höchstens 30 m), geht der Treffer ins Leere. Das Tier fällt beim
	/// nächsten Bild in Ohnmacht (ResolveFate): ApplyDamage kommt auch aus Valheims Effekt-Schleife (Brand, Gift), dort keine
	/// Statuseffekte und Leinen anfassen. True: Treffer verwerfen.
	/// </summary>
	internal static bool Fate(Player player, HitData hit)
	{
		bool grace = Time.time < fateGraceUntil;
		if (!grace && Links.Active.Count == 0)
		{
			return false;
		}
		ZNetView? view = player.m_nview;
		if (view == null || !view.IsValid() || !view.IsOwner() || player.IsDead() || player.InGodMode() || player.InGhostMode() || player.IsDebugFlying())
		{
			return false;
		}
		HitData probe = hit.Clone();
		probe.ApplyModifier(Game.m_localDamgeTakenRate); // so rechnet ApplyDamage beim Spieler
		float damage = probe.GetTotalDamage();
		float health = player.GetHealth();
		if (damage <= 0.1f || health <= 0f || health - damage > 0f)
		{
			return false;
		}
		if (grace)
		{
			fateCleanse = true;
			return true; // derselbe Augenblick (mehrere Geschosse, Flächenschaden, Brand dazu): auch ins Leere
		}
		Link? best = null;
		float bestDistance = FateRange * FateRange;
		foreach (Link link in Links.Active)
		{
			if (link.Holder != player || link.Traveling || link.Ridden || link.Animal == null || !link.Bond.Fate)
			{
				continue; // ein gerittenes Tier nicht: es fiele unsichtbar unter dem Reiter in Ohnmacht
			}
			Character animal = link.Animal;
			if (animal.IsDead() || animal.GetHealth() <= 0f || Bonds.IsFainted(animal))
			{
				continue;
			}
			float distance = (animal.transform.position - player.transform.position).sqrMagnitude;
			if (distance <= bestDistance)
			{
				bestDistance = distance;
				best = link;
			}
		}
		if (best == null || !FateReady(player))
		{
			return false;
		}
		SetFateCooldown(player);
		fateGraceUntil = Time.time + FateGrace;
		fateAnimal = best.Animal;
		fateCleanse = true;
		return true;
	}

	/// <summary>Abklingzeit der Nornen vorbei? Ein Wert weit in der Zukunft stammt aus einer anderen Welt (wie Care.ReadPets).</summary>
	private static bool FateReady(Player player)
	{
		if (ZNet.instance == null || player.m_customData == null)
		{
			return false;
		}
		if (!player.m_customData.TryGetValue(FateKey, out string text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double until))
		{
			return true;
		}
		double now = ZNet.instance.GetTimeSeconds();
		return now >= until || until > now + FateCooldown + 1.0;
	}

	private static void SetFateCooldown(Player player)
	{
		if (ZNet.instance != null && player.m_customData != null)
		{
			player.m_customData[FateKey] = (ZNet.instance.GetTimeSeconds() + FateCooldown).ToString("0", CultureInfo.InvariantCulture);
		}
	}

	/// <summary>
	/// Nach einem Schicksalstausch (nächstes Bild): Das Tier fällt 30 s in Ohnmacht (vorhandenes Ohnmacht-System, die Leine ist
	/// frei), Meldung an den Halter. Brand, Gift und Geist vom abgewehrten Treffer erlöschen, sonst tötete ihn der Rest gleich danach.
	/// </summary>
	private static void ResolveFate(Player? local)
	{
		Character? animal = fateAnimal;
		fateAnimal = null;
		if (animal != null && !animal.IsDead() && Bonds.FaintFor(animal, FateFaint) && local != null)
		{
			local.Message(MessageHud.MessageType.Center, GcLoc.Get("gc_me_fate"));
			Gc.TopLeft(GcLoc.Get("gcls_fainted", animal.GetHoverName()));
		}
		if (!fateCleanse || local == null)
		{
			return;
		}
		fateCleanse = false;
		SEMan seman = local.GetSEMan();
		if (seman != null)
		{
			seman.RemoveStatusEffect(SEMan.s_statusEffectBurning, true);
			seman.RemoveStatusEffect(SEMan.s_statusEffectPoison, true);
			seman.RemoveStatusEffect(SEMan.s_statusEffectSpirit, true);
		}
	}

	// ------------------------------------------------------------------ Ratatöskr: Sprung zum Halter

	private static readonly float[] FetchAngles = { 180f, 135f, -135f, 90f, -90f, 45f, -45f, 0f };
	private static int blockMask;

	/// <summary>
	/// Ratatöskr (TickLink, vor dem Abrutschen): Hängt das Tier länger als 4 s hinter der Zuggrenze fest, ohne näherzukommen, oder
	/// würde die Leine gleich aus der Hand rutschen, springt es neben den Halter (hinter oder neben ihn, wo Boden und Platz sind).
	/// Findet sich kein sicherer Platz, bleibt alles wie ohne Ratatöskr. True: gesprungen.
	/// </summary>
	internal static bool Fetch(Link link, float distance)
	{
		if (distance <= link.DragFrom)
		{
			link.StuckSince = -1f;
			return false;
		}
		float now = Time.time;
		bool fighting = link.Ai != null && link.Ai.m_targetCreature != null;
		if (link.StuckSince < 0f || fighting || distance < link.StuckAt - StuckProgress)
		{
			link.StuckSince = now; // (wieder) hinter der Zuggrenze, kämpft (hängt nicht fest) oder ein Stück nähergekommen: neu zählen
			link.StuckAt = distance;
		}
		bool slipping = distance > link.SlipFrom - SlipMargin;
		if ((!slipping && now - link.StuckSince < StuckSeconds) || now < link.NextFetch)
		{
			return false;
		}
		Character animal = link.Animal;
		if (animal.IsDead() || Bonds.IsFainted(animal) || animal.HaveRider())
		{
			return false;
		}
		link.NextFetch = now + 1f; // kein Platz: in 1 s wieder versuchen
		if (!FetchSpot(link.Holder, animal, out Vector3 spot))
		{
			return false;
		}
		Jump(link, spot);
		link.NextFetch = now + FetchPause;
		link.StuckSince = -1f;
		return true;
	}

	/// <summary>
	/// Sicherer Platz neben dem Halter: Boden wie bei Valheims Teleport (Strahl knapp über ihm, im Haus nicht aufs Dach), höchstens
	/// 2 m über oder unter seinen Füßen, Platz für den Körper des Tiers und kein fremder Schutzbereich.
	/// </summary>
	private static bool FetchSpot(Player holder, Character animal, out Vector3 spot)
	{
		spot = Vector3.zero;
		ZoneSystem? zones = ZoneSystem.instance;
		if (zones == null)
		{
			return false;
		}
		if (blockMask == 0)
		{
			blockMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece");
		}
		Vector3 center = holder.transform.position;
		Vector3 ahead = holder.transform.forward;
		ahead.y = 0f;
		ahead = ahead.sqrMagnitude > 0.001f ? ahead.normalized : Vector3.forward;
		float away = 1.5f + Links.Reach(animal); // wie beim Portal (Links.Place)
		float radius = Mathf.Clamp(animal.GetRadius(), 0.3f, 2.5f);
		float height = Mathf.Max(animal.GetHeight(), radius * 2f);
		foreach (float angle in FetchAngles)
		{
			Vector3 point = center + Quaternion.Euler(0f, angle, 0f) * ahead * away;
			if (!zones.FindFloor(point + Vector3.up * 0.8f, out float floor) || Mathf.Abs(floor - center.y) > 2f)
			{
				continue;
			}
			point.y = floor + 0.1f;
			Vector3 bottom = point + Vector3.up * (radius + 0.2f);
			Vector3 top = point + Vector3.up * Mathf.Max(radius + 0.2f, height - radius);
			if (Physics.CheckCapsule(bottom, top, radius * 0.9f, blockMask, QueryTriggerInteraction.Ignore) || !PrivateArea.CheckAccess(point, 0f, false, false))
			{
				continue; // Wand, Baum, Bauteil im Weg oder fremder Schutzbereich (SDK: Teleport)
			}
			spot = point;
			return true;
		}
		return false;
	}

	/// <summary>Wie Valheims Teleport: Position, Körper und ZDO setzen, m_maxAirAltitude auf die neue Höhe (kein Sturz).</summary>
	private static void Jump(Link link, Vector3 spot)
	{
		Character animal = link.Animal;
		Vector3 look = link.Holder.transform.position - spot;
		look.y = 0f;
		Quaternion rotation = look.sqrMagnitude > 0.01f ? Quaternion.LookRotation(look.normalized) : animal.transform.rotation;
		animal.transform.SetPositionAndRotation(spot, rotation);
		Rigidbody? body = animal.m_body;
		if (body != null)
		{
			body.position = spot;
			body.rotation = rotation;
			if (!body.isKinematic)
			{
				body.linearVelocity = Vector3.zero;
				body.angularVelocity = Vector3.zero;
			}
		}
		animal.m_maxAirAltitude = spot.y;
		link.View.GetZDO().SetPosition(spot);
		link.Stretched = 0f;
		link.DragUntil = 0f;
		link.ClearCommands(); // ein Befehl führte es womöglich dorthin, wo es festhing
		if (link.Ai != null)
		{
			link.Ai.ResetRandomMovement();
		}
	}

	// ------------------------------------------------------------------ Draupnir: Zucht

	/// <summary>Zustand vor Valheims Procreate (Prefix), für Postfix und Finalizer.</summary>
	internal struct Breeding
	{
		public bool Active;
		public bool Pregnant;
		public int Love;
		public int Required;
		public float Fertility;
	}

	private static readonly Dictionary<string, (uint Revision, BondStats Bond)> KnotBonds = new();

	/// <summary>Draupnir an diesem Tier: an der eigenen aktiven Leine (beim Halter) oder am Pfosten (Leine im Knoten), sonst 0.</summary>
	private static float Fertility(Character? animal, ZDO zdo)
	{
		if (animal != null && Bonds.TryGet(animal, out BondStats bond) && bond.Fertility > 0f)
		{
			return bond.Fertility;
		}
		return Posts.IsTied(zdo) ? TiedBond(zdo).Fertility : 0f;
	}

	/// <summary>
	/// Werte der Leine eines angebundenen Tiers. Sie steckt ganz im Knoten (Posts.Tie speichert das Item samt Verzauberung), den
	/// jeder Rechner kennt, der das Tier geladen hat. Neu gelesen nur, wenn sich der Knoten geändert hat (DataRevision).
	/// </summary>
	internal static BondStats TiedBond(ZDO zdo)
	{
		if (!LeashMagic.Available)
		{
			return BondStats.None;
		}
		string id = zdo.GetString(Plugin.LeashIdHash);
		if (id.Length == 0 || !LeashKnot.ById.TryGetValue(id, out LeashKnot knot) || knot == null || !knot.Ready)
		{
			return BondStats.None;
		}
		ZDO knotZdo = knot.View!.GetZDO();
		uint revision = knotZdo.DataRevision;
		if (KnotBonds.TryGetValue(id, out (uint Revision, BondStats Bond) cached) && cached.Revision == revision)
		{
			return cached.Bond;
		}
		ItemDrop.ItemData? item = Posts.LoadItem(knotZdo.GetByteArray(Posts.ItemDataHash));
		BondStats bond = item != null ? Bonds.Of(item) : BondStats.None;
		if (KnotBonds.Count > 256)
		{
			KnotBonds.Clear();
		}
		KnotBonds[id] = (revision, bond);
		return bond;
	}

	/// <summary>
	/// Vor Valheims Procreate (nur beim Besitzer, mit Draupnir): Trächtig, aber noch nicht verkürzt (etwa erst jetzt angeleint):
	/// die restliche Zeit halbieren. Sonst: Bringt ein Liebespunkt mit dem doppelten Wert die Trächtigkeit, gilt die Schwelle für
	/// diesen Aufruf schon beim nächsten Punkt; so macht Valheim das Tier selbst trächtig (andere Mods sehen es im selben Aufruf).
	/// </summary>
	internal static Breeding BeforeProcreate(Procreation procreation)
	{
		Breeding state = default;
		ZNetView? view = procreation.m_nview;
		if (!Plugin.On || view == null || !view.IsValid() || !view.IsOwner() || ZNet.instance == null || procreation.m_tameable == null || !procreation.m_tameable.IsTamed())
		{
			return state;
		}
		ZDO zdo = view.GetZDO();
		long pregnant = zdo.GetLong(ZDOVars.s_pregnant, 0L);
		if (pregnant == 0L && zdo.GetInt(TwinHash, 0) != 0)
		{
			zdo.Set(TwinHash, 0); // der Zwilling kam schon (etwa nachdem Draupnir abgenommen war): Merker weg
		}
		float fertility = Fertility(procreation.m_character, zdo);
		if (fertility <= 0f)
		{
			return state;
		}
		state.Active = true;
		state.Fertility = fertility;
		state.Required = procreation.m_requiredLovePoints;
		state.Pregnant = pregnant != 0L;
		if (state.Pregnant)
		{
			Hasten(procreation, zdo, pregnant);
			return state;
		}
		state.Love = procreation.GetLovePoints();
		if (state.Love + 2 >= state.Required)
		{
			procreation.m_requiredLovePoints = Mathf.Min(state.Required, state.Love + 1); // Finalizer stellt den Wert zurück
		}
		return state;
	}

	/// <summary>
	/// Nach Valheims Procreate: Liebespunkt dazu bekommen → noch einer (Draupnir zählt doppelt). Gerade trächtig geworden → die
	/// Trächtigkeit halb so lang (der Zeitpunkt im Tier wird vorverlegt, so zählen auch die Countdowns aller Spieler richtig).
	/// Gerade geboren → mit x % ein Zwilling: Die Trächtigkeit ist gleich wieder fällig, Procreate läuft 1 s später noch einmal
	/// (mit Stufe, Sternen und Züchter wie beim ersten Jungen); der Zwilling würfelt nicht noch einmal.
	/// </summary>
	internal static void AfterProcreate(Procreation procreation, Breeding state)
	{
		ZNetView? view = procreation.m_nview;
		if (!state.Active || view == null || !view.IsValid() || !view.IsOwner() || ZNet.instance == null)
		{
			return;
		}
		ZDO zdo = view.GetZDO();
		long pregnant = zdo.GetLong(ZDOVars.s_pregnant, 0L);
		if (!state.Pregnant)
		{
			if (pregnant != 0L)
			{
				Hasten(procreation, zdo, pregnant);
				return;
			}
			if (procreation.GetLovePoints() == state.Love + 1)
			{
				zdo.Set(ZDOVars.s_lovePoints, state.Love + 2);
			}
			return;
		}
		if (pregnant != 0L)
		{
			return; // noch nicht so weit
		}
		if (zdo.GetInt(TwinHash, 0) != 0)
		{
			zdo.Set(TwinHash, 0); // das war der Zwilling
			return;
		}
		if (UnityEngine.Random.value * 100f >= Mathf.Min(state.Fertility, 100f))
		{
			return;
		}
		zdo.Set(TwinHash, 1);
		zdo.Set(ZDOVars.s_pregnant, ZNet.instance.GetTime().AddSeconds(-(procreation.m_pregnancyDuration + 1f)).Ticks); // gleich wieder fällig
		procreation.Invoke(nameof(Procreation.Procreate), 1f); // nicht erst nach m_updateInterval; sonst holt es die nächste Prüfung nach
	}

	/// <summary>Restliche Trächtigkeit halbieren (einmal je Trächtigkeit): den Zeitpunkt im Tier um die halbe Restzeit vorverlegen.</summary>
	private static void Hasten(Procreation procreation, ZDO zdo, long pregnant)
	{
		if (zdo.GetLong(HasteHash, 0L) == pregnant)
		{
			return;
		}
		double remaining = procreation.m_pregnancyDuration - (ZNet.instance.GetTime() - new DateTime(pregnant)).TotalSeconds;
		if (remaining <= 0.0)
		{
			return;
		}
		long hastened = pregnant - TimeSpan.FromSeconds(remaining * 0.5).Ticks;
		zdo.Set(ZDOVars.s_pregnant, hastened);
		zdo.Set(HasteHash, hastened);
	}
}

internal static partial class LeashMagic
{
	private static Dictionary<int, string>? leashSpeciesByHash;

	/// <summary>
	/// Art eines Tiers wie SpeciesOf, aber über den Prefab-Hash im ZDO (ohne neuen Text je Abfrage, für Prüfungen in jedem Bild
	/// und bei jedem Treffer). Ohne gültiges ZDO über den Namen.
	/// </summary>
	internal static string LeashSpecies(Character? animal)
	{
		if (animal == null)
		{
			return "";
		}
		ZNetView? view = animal.m_nview;
		if (view == null || !view.IsValid())
		{
			return SpeciesOf(animal);
		}
		if (leashSpeciesByHash == null)
		{
			leashSpeciesByHash = new Dictionary<int, string>();
			foreach (KeyValuePair<string, string> entry in SpeciesByPrefab)
			{
				leashSpeciesByHash[entry.Key.GetStableHashCode()] = entry.Value;
			}
		}
		return leashSpeciesByHash.TryGetValue(view.GetZDO().GetPrefab(), out string species) ? species : "";
	}
}

// ---------------------------------------------------------------------- Spiel-Anbindung

/// <summary>Draupnir: Liebespunkte doppelt, Trächtigkeit halb so lang, Zwillinge (LeashUniqueEffects.BeforeProcreate/AfterProcreate).</summary>
[HarmonyPatch(typeof(Procreation), "Procreate")]
internal static class LeashFertilityPatch
{
	private static void Prefix(Procreation __instance, out LeashUniqueEffects.Breeding __state) => __state = LeashUniqueEffects.BeforeProcreate(__instance);

	private static void Postfix(Procreation __instance, LeashUniqueEffects.Breeding __state) => LeashUniqueEffects.AfterProcreate(__instance, __state);

	/// <summary>Schwelle der Liebespunkte immer zurückstellen, auch wenn Procreate wirft.</summary>
	private static void Finalizer(Procreation __instance, LeashUniqueEffects.Breeding __state)
	{
		if (__state.Active)
		{
			__instance.m_requiredLovePoints = __state.Required;
		}
	}
}

/// <summary>
/// Treffer (beim Besitzer des Getroffenen): Tyrs Pfand für den eigenen Spieler, Rudelfrieden-Reizung für wilde Wölfe. Zuletzt
/// (nach EpicLoot und den übrigen Mods): Hat ein anderer Prefix den Treffer verworfen (z. B. EpicLoots Ausweichen), trägt das
/// Tier nichts.
/// </summary>
[HarmonyPatch(typeof(Character), nameof(Character.RPC_Damage))]
internal static class LeashUniqueDamagePatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Prefix(Character __instance, HitData hit, bool __runOriginal)
	{
		if (!__runOriginal || hit == null)
		{
			return;
		}
		if (__instance == Player.m_localPlayer)
		{
			if (Links.Active.Count > 0)
			{
				LeashUniqueEffects.Guard((Player)__instance, hit);
			}
			return;
		}
		if (LeashUniqueEffects.PeaceActive)
		{
			LeashUniqueEffects.Provoke(__instance, hit);
		}
	}
}

/// <summary>Nornen: Ein tödlicher Treffer gegen den eigenen Spieler geht ins Leere (zuletzt, nach den übrigen Schadens-Prefixen).</summary>
[HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
internal static class LeashFatePatch
{
	[HarmonyPriority(Priority.Last)]
	private static bool Prefix(Character __instance, HitData hit, bool __runOriginal)
	{
		return !__runOriginal || hit == null || __instance != Player.m_localPlayer || !LeashUniqueEffects.Fate((Player)__instance, hit);
	}
}

/// <summary>Hels Ernte: Ein Tier an einer Leine mit Hel hat einen Gegner getötet (beim Besitzer des Opfers).</summary>
[HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
internal static class LeashReapPatch
{
	private static void Prefix(Character __instance) => LeashUniqueEffects.Reap(__instance);
}

/// <summary>Geweihstoß: Treffer eines Elchs an der Leine (beim Angreifer, bevor Valheim den Treffer an den Besitzer des Ziels schickt).</summary>
[HarmonyPatch(typeof(Character), nameof(Character.Damage))]
internal static class LeashAntlersPatch
{
	private static void Prefix(Character __instance, HitData hit) => LeashUniqueEffects.Antlers(__instance, hit);
}

/// <summary>Rudelfrieden: Wilde Wölfe und ein Halter mit Rudelfrieden (samt seinen Tieren) sind keine Feinde.</summary>
[HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IsEnemy), typeof(Character), typeof(Character))]
internal static class LeashPackPeacePatch
{
	private static void Postfix(Character a, Character b, ref bool __result)
	{
		if (__result && LeashUniqueEffects.PeaceActive && (LeashUniqueEffects.Peaceful(a, b) || LeashUniqueEffects.Peaceful(b, a)))
		{
			__result = false;
		}
	}
}
