using System;
using System.Collections.Generic;
using System.Text;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Neue Sondereffekte der einzigartigen Leinen, Pfeifen und Sättel (Gorduan, 27.10.2026: „für jede Sorte min. 3 legendäre,
/// 2 mythische, 1 uraltes, diese sollten jeweils sehr besonders sein“, „mehr gute Leinen“, „Sättel für alle Tiere, falls
/// eingestellt“). Nur die Werte; die Wirkung steht in LeashUniqueEffects, WhistleUniqueEffects und SaddleUniqueEffects.
/// </summary>
internal sealed partial class BondStats
{
	// ---- Leinen (das Tier an der aktiven Leine bzw. der Halter)
	public float Fertility;      // Draupnir: doppelt so schnell trächtig, x % Zwillinge (auch am Pfosten)
	public bool Fetch;           // Ratatöskr: bleibt das Tier hängen, springt es zum Halter
	public float Guard;          // Tyr: x % des Schadens, den der Halter nimmt, trägt das Tier
	public float Wisdom;         // Mímir: Tier-Erfahrung und Tierpflege +x %
	public float Reap;           // Hel: tötet das Tier einen Gegner, heilt der Halter x %, das Tier 2x %
	public bool Fate;            // Nornen: tödlicher Treffer gegen den Halter geht ins Leere, das Tier fällt in Ohnmacht
	public string KinSpecies = ""; // Arten-Leinen: für welche Art (Wolf, Boar, Lox, Asksvin, Moose, Bjorn)
	public float KinBonus;       // Arten-Leinen: Leben und Schaden +x %, nur bei dieser Art
	public bool PackPeace;       // Wolf: wilde Wölfe bleiben friedlich
	public float Forage;         // Eber: findet alle x s etwas (Pilze)
	public float Carrier;        // Lox: Halter trägt +x
	public bool FireWalker;      // Asksvin: Feuer und Lava schaden dem Tier nicht
	public float Antlers;        // Elch: x % Chance, dass ein Treffer den Gegner umwirft
	public float BearGuard;      // Bär: Gegner im Umkreis von x m greifen lieber den Bären an

	// ---- Pfeifen (beim Pfiff)
	public float Lure;           // Huldra: Gegner im Umkreis von x m greifen 5 s lang die Tiere an statt den Pfeifenden
	public float Song;           // Eir: Spieler in 10 m und die Tiere heilen über 5 s x % ihres Lebens
	public float Net;            // Rán: Gegner im Umkreis von 12 m 3 s lang x % langsamer
	public float Pack;           // Fenrir: jedes Tier +x % Schaden und Tempo je weiterem Tier an deinen Leinen (höchstens 5)
	public bool Wake;            // Hermóðr: ohnmächtige Tiere stehen sofort voll geheilt auf (Abklingzeit 60 s)
	public float Void;           // Ginnungagap: Tiere x s unverwundbar, Gegner in 15 m taumeln, Befehlspausen enden (60 s)

	// ---- Sättel (beim Reiten)
	public bool MountJump;       // Grani: Reittier springt, kein Sturzschaden für Reittier und Reiter
	public float MountFreeRun;   // Gullfaxi: Rennen kostet nichts, solange kein Gegner in x m ist
	public bool MountFireproof;  // Blóðughófi: Reittier und Reiter nehmen keinen Feuer- und Lavaschaden
	public bool MountAttack;     // Hildisvíni: Angriffstaste lässt das Reittier angreifen
	public float MountTrample;   // Svaðilfari: im vollen Lauf Gegner umwerfen, x Stumpfschaden
	public bool MountSkyRide;    // Allvater: Doppelsprung, Gleiten, kein Sturzschaden
	public bool MountHerdPeace;  // Lox-Sattel: wilde Lox greifen nicht an
	public float MountSprint;    // Asksvin-Sattel: x s doppelt so schnell (Fähigkeit, 20 s Abklingzeit)
	public float MountBerserk;   // Bären-Sattel: unter 50 % Leben +x % Schaden für Reiter und Bär
	public float MountDeathRide; // Unbjørn-Sattel: Gegner bemerken den Reiter x % schwerer, Gift schadet beiden nicht

	public bool UniqueAnimalAny => Fertility > 0f || Fetch || Wisdom > 0f || Reap > 0f || KinBonus > 0f || PackPeace || Forage > 0f
		|| FireWalker || Antlers > 0f || BearGuard > 0f || Lure > 0f || Song > 0f || Net > 0f || Pack > 0f || Wake || Void > 0f;

	public bool UniqueHolderAny => Guard > 0f || Fate || Carrier > 0f;

	public bool UniqueMountAny => MountJump || MountFreeRun > 0f || MountFireproof || MountAttack || MountTrample > 0f || MountSkyRide
		|| MountHerdPeace || MountSprint > 0f || MountBerserk > 0f || MountDeathRide > 0f;

	/// <summary>Gilt der Arten-Bonus dieser Leine für dieses Tier (Bjorn umfasst Bärenjunges und Unbjørn)?</summary>
	public bool KinMatches(Character? animal) => KinSpecies.Length > 0 && animal != null && LeashMagic.SpeciesOf(animal) == KinSpecies;
}

internal static partial class LeashMagic
{
	/// <summary>
	/// GC_Tierarten meldet, ob eine Art gerade aktiviert ist (Bär, Unbjørn: nur wenn zähmbar). Null = alle (ohne Tierarten
	/// gibt es keine Bären an der Leine, ihre Arten-Leine wirkt dann nie).
	/// </summary>
	internal static Func<string, bool>? SpeciesEnabled;

	/// <summary>Arten der Arten-Leinen, Prefab → Art.</summary>
	private static readonly Dictionary<string, string> SpeciesByPrefab = new()
	{
		["Wolf"] = "Wolf", ["Wolf_cub"] = "Wolf",
		["Boar"] = "Boar", ["Boar_piggy"] = "Boar",
		["Lox"] = "Lox", ["Lox_Calf"] = "Lox",
		["Asksvin"] = "Asksvin", ["Asksvin_hatchling"] = "Asksvin",
		["Moose"] = "Moose", ["Moose_calf"] = "Moose",
		["Bjorn"] = "Bjorn", ["GC_Bjorn_Cub"] = "Bjorn", ["Unbjorn"] = "Bjorn",
	};

	internal static string SpeciesOf(Character animal) => SpeciesByPrefab.TryGetValue(Ranks.PrefabName(animal.gameObject), out string species) ? species : "";

	private static Effect LeashSpecial(string type, string key, float value) => Special(type, key, value);

	private static Effect SaddleSpecial(string type, string key, float value)
	{
		Effect effect = Special(type, key, value);
		effect.RequirementName = SaddleRequirement;
		return effect;
	}

	/// <summary>Sondereffekte der neuen einzigartigen Stücke (würfeln nie zufällig).</summary>
	private static readonly Effect[] UniqueSpecials =
	{
		LeashSpecial("GC_LeashFertility", "fertility", 25),
		LeashSpecial("GC_LeashFetch", "fetch", 1),
		LeashSpecial("GC_LeashGuard", "guard", 25),
		LeashSpecial("GC_LeashWisdom", "wisdom", 100),
		LeashSpecial("GC_LeashReap", "reap", 10),
		LeashSpecial("GC_LeashFate", "fate", 1),
		LeashSpecial("GC_LeashKinWolf", "kin_wolf", 50),
		LeashSpecial("GC_LeashKinBoar", "kin_boar", 50),
		LeashSpecial("GC_LeashKinLox", "kin_lox", 50),
		LeashSpecial("GC_LeashKinAsksvin", "kin_asksvin", 50),
		LeashSpecial("GC_LeashKinMoose", "kin_moose", 50),
		LeashSpecial("GC_LeashKinBear", "kin_bear", 60),
		LeashSpecial("GC_LeashPackPeace", "packpeace", 1),
		LeashSpecial("GC_LeashForage", "forage", 300),
		LeashSpecial("GC_LeashCarrier", "carrier", 200),
		LeashSpecial("GC_LeashFireWalker", "firewalker", 1),
		LeashSpecial("GC_LeashAntlers", "antlers", 30),
		LeashSpecial("GC_LeashBearGuard", "bearguard", 10),
		WhistleSpecial("GC_LeashWhistleLure", "whistle_lure", 15),
		WhistleSpecial("GC_LeashWhistleSong", "whistle_song", 15),
		WhistleSpecial("GC_LeashWhistleNet", "whistle_net", 60),
		WhistleSpecial("GC_LeashWhistlePack", "whistle_pack", 8),
		WhistleSpecial("GC_LeashWhistleWake", "whistle_wake", 1),
		WhistleSpecial("GC_LeashWhistleVoid", "whistle_void", 6),
		SaddleSpecial("GC_SaddleJump", "saddle_jump", 1),
		SaddleSpecial("GC_SaddleFreeRun", "saddle_freerun", 25),
		SaddleSpecial("GC_SaddleFireproof", "saddle_fireproof", 1),
		SaddleSpecial("GC_SaddleWarMount", "saddle_warmount", 1),
		SaddleSpecial("GC_SaddleTrample", "saddle_trample", 60),
		SaddleSpecial("GC_SaddleSkyRide", "saddle_skyride", 1),
		SaddleSpecial("GC_SaddleHerdPeace", "saddle_herd", 1),
		SaddleSpecial("GC_SaddleSprint", "saddle_sprint", 3),
		SaddleSpecial("GC_SaddleBerserk", "saddle_berserk", 30),
		SaddleSpecial("GC_SaddleDeathRide", "saddle_deathride", 50),
	};

	/// <summary>
	/// Neue Leinen (Gorduan, 27.10.2026, Antwort B: je Sorte 3 legendäre, 2 mythische, 1 uraltes, dazu „mehr gute Leinen“: je
	/// zahmer Art eine legendäre Arten-Leine). Boss und Mindest-Leine wie bisher (LeashRankAfter); Art: nur, wenn sie aktiviert ist.
	/// </summary>
	private static readonly (string Id, string Kind, string Key, string Boss, string Species, (string Type, float Min, float Max)[] Effects)[] MoreUniques =
	{
		("GC_DraupnirsChain", "Legendary", "draupnir", "defeated_queen", "", new[] { ("GC_LeashFertility", 25f, 25f), ("GC_LeashAnimalHealth", 40f, 70f), ("GC_LeashAnimalRegen", 3f, 4f) }),
		("GC_RatatoskrsBand", "Legendary", "ratatoskr", "defeated_fader", "", new[] { ("GC_LeashFetch", 1f, 1f), ("GC_LeashAnimalSpeed", 50f, 80f), ("GC_LeashRange", 4f, 8f) }),
		("GC_TyrsPledge", "Legendary", "tyr", "defeated_frozenking_p3", "", new[] { ("GC_LeashGuard", 25f, 25f), ("GC_LeashAnimalHealth", 50f, 80f), ("GC_LeashAnimalResist", 30f, 40f) }),
		("GC_MimirsWell", "Mythic", "mimir", "defeated_queen", "", new[] { ("GC_LeashWisdom", 100f, 100f), ("GC_LeashAnimalHealth", 60f, 90f), ("GC_LeashAnimalDamage", 50f, 80f), ("GC_LeashBondReveal", 35f, 35f), ("GC_LeashRange", 8f, 12f) }),
		("GC_HelsChain", "Mythic", "hel", "defeated_frozenking_p3", "", new[] { ("GC_LeashReap", 10f, 10f), ("GC_LeashSpiritBite", 12f, 12f), ("GC_LeashAnimalDamage", 60f, 90f), ("GC_LeashLifeSteal", 8f, 8f), ("GC_LeashFearless", 1f, 1f) }),
		("GC_NornsThread", "Ancient", "norns", "defeated_frozenking_p3", "", new[] { ("GC_LeashFate", 1f, 1f), ("GC_LeashUnbreakable", 1f, 1f), ("GC_LeashRange", 19f, 25f), ("GC_LeashRevive", 12f, 12f), ("GC_LeashEvade", 31f, 31f), ("GC_LeashObedience", 100f, 100f) }),
		// Arten-Leinen: wirken nur bei ihrer Art (Arten-Bonus und Sonderfähigkeit), sonst nur die übrigen Werte
		("GC_GeriAndFreki", "Legendary", "geri", "defeated_dragon", "Wolf", new[] { ("GC_LeashKinWolf", 40f, 60f), ("GC_LeashPackPeace", 1f, 1f), ("GC_LeashAnimalAttackSpeed", 30f, 50f) }),
		("GC_SaehrimnirsRope", "Legendary", "saehrimnir", "defeated_bonemass", "Boar", new[] { ("GC_LeashKinBoar", 40f, 60f), ("GC_LeashForage", 300f, 300f), ("GC_LeashAnimalRegen", 2f, 3f) }),
		("GC_AudhumlasTether", "Legendary", "audhumla", "defeated_goblinking", "Lox", new[] { ("GC_LeashKinLox", 40f, 60f), ("GC_LeashCarrier", 200f, 200f), ("GC_LeashAnimalHealth", 30f, 50f) }),
		("GC_NidhoggsReins", "Legendary", "nidhogg", "defeated_fader", "Asksvin", new[] { ("GC_LeashKinAsksvin", 40f, 60f), ("GC_LeashFireWalker", 1f, 1f), ("GC_LeashFireBite", 8f, 10f) }),
		("GC_EikthyrnirsAntlers", "Legendary", "eikthyrnir", "defeated_frozenking_p3", "Moose", new[] { ("GC_LeashKinMoose", 40f, 60f), ("GC_LeashAntlers", 30f, 30f), ("GC_LeashStaggerPower", 15f, 20f) }),
		("GC_BjarkisFetter", "Legendary", "bjarki", "defeated_bonemass", "Bjorn", new[] { ("GC_LeashKinBear", 50f, 80f), ("GC_LeashBearGuard", 10f, 10f), ("GC_LeashAnimalHealth", 30f, 50f) }),
	};

	/// <summary>Neue Pfeifen: legendär ein Sondereffekt und ein Drittel üblicher Werte, mythisch und uralt nur eigene Effekte.</summary>
	private static readonly (string Id, string Kind, string Key, string Boss, (string Type, float Min, float Max)[] Effects)[] MoreWhistleUniques =
	{
		("GC_HuldrasLure", "Legendary", "huldra", "defeated_queen", new[] { ("GC_LeashWhistleLure", 15f, 15f), ("GC_LeashAnimalHealth", 13f, 20f), ("GC_LeashDeter", 3f, 3f) }),
		("GC_EirsSong", "Legendary", "eir", "defeated_fader", new[] { ("GC_LeashWhistleSong", 15f, 15f), ("GC_LeashLifeSteal", 3f, 3f) }),
		("GC_RansNet", "Legendary", "ran", "defeated_frozenking_p3", new[] { ("GC_LeashWhistleNet", 60f, 60f), ("GC_LeashFrostBite", 8f, 8f) }),
		("GC_FenrirsHowl", "Mythic", "fenrir", "defeated_fader", new[] { ("GC_LeashWhistlePack", 8f, 8f) }),
		("GC_HermodsCall", "Mythic", "hermod", "defeated_frozenking_p3", new[] { ("GC_LeashWhistleWake", 1f, 1f), ("GC_LeashWhistleRally", 25f, 25f) }),
		("GC_GinnungagapsEcho", "Ancient", "ginnungagap", "defeated_frozenking_p3", new[] { ("GC_LeashWhistleVoid", 6f, 6f), ("GC_LeashWhistleHall", 15f, 15f) }),
	};

	/// <summary>
	/// Sättel (Gorduan, 27.10.2026: „Loxsattel, aber auch Sättel für alle anderen Tiere, falls eingestellt“): sechs für jeden Sattel,
	/// dazu je Reittier ein eigenes (nur auf seinem Sattel; Bär und Unbjørn gibt es nur, wenn Bären zähmbar sind).
	/// </summary>
	private static readonly (string Id, string Kind, string Key, string Boss, string Saddle, (string Type, float Min, float Max)[] Effects)[] SaddleUniques =
	{
		("GC_GranisSaddle", "Legendary", "grani", "defeated_dragon", "", new[] { ("GC_SaddleJump", 1f, 1f), ("GC_SaddleStamina", 40f, 60f) }),
		("GC_GullfaxisSaddle", "Legendary", "gullfaxi", "defeated_goblinking", "", new[] { ("GC_SaddleFreeRun", 25f, 25f), ("GC_SaddleStaminaRegen", 40f, 60f) }),
		("GC_BlodughofisSaddle", "Legendary", "blodughofi", "defeated_fader", "", new[] { ("GC_SaddleFireproof", 1f, 1f), ("GC_SaddleStaminaSave", 20f, 35f) }),
		("GC_HildisvinisSaddle", "Mythic", "hildisvini", "defeated_queen", "", new[] { ("GC_SaddleWarMount", 1f, 1f), ("GC_SaddleStamina", 55f, 80f), ("GC_LeashAnimalHealth", 50f, 80f) }),
		("GC_SvadilfarisSaddle", "Mythic", "svadilfari", "defeated_fader", "", new[] { ("GC_SaddleTrample", 60f, 60f), ("GC_LeashSteadfast", 1f, 1f), ("GC_SaddleStaminaSave", 30f, 45f) }),
		("GC_AllfathersSaddle", "Ancient", "allfather", "defeated_frozenking_p3", "", new[] { ("GC_SaddleSkyRide", 1f, 1f), ("GC_LeashAnimalSpeed", 30f, 30f), ("GC_SaddleStamina", 75f, 100f) }),
		("GC_HerdSaddle", "Legendary", "herdsaddle", "defeated_goblinking", "SaddleLox", new[] { ("GC_SaddleHerdPeace", 1f, 1f), ("GC_SaddleStamina", 40f, 60f) }),
		("GC_SparkSaddle", "Legendary", "sparksaddle", "defeated_fader", "SaddleAsksvin", new[] { ("GC_SaddleSprint", 3f, 3f), ("GC_SaddleStaminaRegen", 40f, 60f) }),
		("GC_BerserkerSaddle", "Legendary", "berserksaddle", "defeated_bonemass", Saddles.BearSaddle, new[] { ("GC_SaddleBerserk", 30f, 30f), ("GC_LeashAnimalHealth", 30f, 50f) }),
		("GC_DeathRideSaddle", "Legendary", "deathsaddle", "defeated_goblinking", Saddles.UnbjornSaddle, new[] { ("GC_SaddleDeathRide", 50f, 50f), ("GC_SaddleStaminaSave", 20f, 35f) }),
	};

	/// <summary>Sattel-Beute (Gorduan, 27.10.2026; Vorschlag 0,5 % je Kreatur): Tabellen wie die Leine des Bioms.</summary>
	private static readonly (string Saddle, string[] Tables)[] SaddleLoot =
	{
		("SaddleLox", new[] { "Tier5Mob", "Tier6EliteMob" }),
		("SaddleAsksvin", new[] { "Tier7Mob", "Tier8EliteMob" }),
		(Saddles.BearSaddle, new[] { "Tier3Mob", "Tier3EliteMob" }),
		(Saddles.UnbjornSaddle, new[] { "Tier5Mob", "Tier6EliteMob" }),
	};

	private static string KinRequirement(string boss, string species) => Requirement + "_" + boss + "_" + species;

	private static string SaddleBossRequirement(string boss, string saddle) => SaddleRequirement + "_" + boss + (saddle.Length > 0 ? "_" + saddle : "");

	/// <summary>Bedingungen, Effekte und Stücke anmelden (aus Register). Zählt in die Zahlen der Logzeile.</summary>
	private static void RegisterUniques(ref int effects, ref int uniques, ref int whistles, out int saddles)
	{
		foreach ((string _, string _, string _, string boss, string species, (string, float, float)[] _) in MoreUniques)
		{
			if (species.Length == 0)
			{
				continue;
			}
			int rank = LeashRankAfter.TryGetValue(boss, out int min) ? min : 2;
			string needed = species;
			Func<ItemDrop.ItemData, object, string, bool, bool, bool, bool> kin = (item, magicItem, effectType, lootRoll, augmentRoll, runeRoll)
				=> item != null && Plugin.IsLeash(item, out Tier tier) && tier.Rank >= rank && BossDefeated(boss) && (SpeciesEnabled == null || SpeciesEnabled(needed));
			Call("RegisterMagicEffectRequirement", KinRequirement(boss, species), kin);
		}
		foreach ((string _, string _, string _, string boss, string saddle, (string, float, float)[] _) in SaddleUniques)
		{
			string bossKey = boss;
			string saddleName = saddle;
			Func<ItemDrop.ItemData, object, string, bool, bool, bool, bool> afterBoss = (item, magicItem, effectType, lootRoll, augmentRoll, runeRoll)
				=> item != null && Saddles.IsSaddle(item) && BossDefeated(bossKey) && (saddleName.Length == 0 || (item.m_dropPrefab != null && item.m_dropPrefab.name == saddleName));
			Call("RegisterMagicEffectRequirement", SaddleBossRequirement(boss, saddle), afterBoss);
		}
		foreach (Effect effect in UniqueSpecials)
		{
			effects += Call("AddMagicEffect", EffectJson(effect)) != null ? 1 : 0;
		}
		foreach ((string id, string kind, string key, string boss, string species, (string Type, float Min, float Max)[] list) in MoreUniques)
		{
			string requirement = species.Length > 0 ? KinRequirement(boss, species) : BossRequirement(boss);
			uniques += Call("AddLegendaryItem", kind, UniqueJson(id, key, requirement, list)) != null ? 1 : 0;
		}
		foreach ((string id, string kind, string key, string boss, (string Type, float Min, float Max)[] list) in MoreWhistleUniques)
		{
			whistles += Call("AddLegendaryItem", kind, UniqueJson(id, key, WhistleBossRequirement(boss), list, list.Length)) != null ? 1 : 0;
		}
		saddles = 0;
		foreach ((string id, string kind, string key, string boss, string saddle, (string Type, float Min, float Max)[] list) in SaddleUniques)
		{
			saddles += Call("AddLegendaryItem", kind, UniqueJson(id, key, SaddleBossRequirement(boss, saddle), list)) != null ? 1 : 0;
		}
	}

	/// <summary>Werte der neuen Sondereffekte; false: kein neuer Effekt.</summary>
	private static bool ApplyUnique(BondStats s, string type, float v)
	{
		switch (type)
		{
			case "GC_LeashFertility": s.Fertility = Math.Max(s.Fertility, v); return true;
			case "GC_LeashFetch": s.Fetch = true; return true;
			case "GC_LeashGuard": s.Guard = Math.Min(75f, s.Guard + v); return true;
			case "GC_LeashWisdom": s.Wisdom += v; return true;
			case "GC_LeashReap": s.Reap += v; return true;
			case "GC_LeashFate": s.Fate = true; return true;
			case "GC_LeashKinWolf": s.KinSpecies = "Wolf"; s.KinBonus += v; return true;
			case "GC_LeashKinBoar": s.KinSpecies = "Boar"; s.KinBonus += v; return true;
			case "GC_LeashKinLox": s.KinSpecies = "Lox"; s.KinBonus += v; return true;
			case "GC_LeashKinAsksvin": s.KinSpecies = "Asksvin"; s.KinBonus += v; return true;
			case "GC_LeashKinMoose": s.KinSpecies = "Moose"; s.KinBonus += v; return true;
			case "GC_LeashKinBear": s.KinSpecies = "Bjorn"; s.KinBonus += v; return true;
			case "GC_LeashPackPeace": s.PackPeace = true; return true;
			case "GC_LeashForage": s.Forage = s.Forage > 0f ? Math.Min(s.Forage, v) : v; return true;
			case "GC_LeashCarrier": s.Carrier += v; return true;
			case "GC_LeashFireWalker": s.FireWalker = true; return true;
			case "GC_LeashAntlers": s.Antlers = Math.Min(75f, s.Antlers + v); return true;
			case "GC_LeashBearGuard": s.BearGuard = Math.Max(s.BearGuard, v); return true;
			case "GC_LeashWhistleLure": s.Lure = Math.Max(s.Lure, v); return true;
			case "GC_LeashWhistleSong": s.Song += v; return true;
			case "GC_LeashWhistleNet": s.Net = Math.Min(90f, Math.Max(s.Net, v)); return true;
			case "GC_LeashWhistlePack": s.Pack += v; return true;
			case "GC_LeashWhistleWake": s.Wake = true; return true;
			case "GC_LeashWhistleVoid": s.Void = Math.Max(s.Void, v); return true;
			case "GC_SaddleJump": s.MountJump = true; return true;
			case "GC_SaddleFreeRun": s.MountFreeRun = Math.Max(s.MountFreeRun, v); return true;
			case "GC_SaddleFireproof": s.MountFireproof = true; return true;
			case "GC_SaddleWarMount": s.MountAttack = true; return true;
			case "GC_SaddleTrample": s.MountTrample += v; return true;
			case "GC_SaddleSkyRide": s.MountSkyRide = true; s.MountJump = true; return true;
			case "GC_SaddleHerdPeace": s.MountHerdPeace = true; return true;
			case "GC_SaddleSprint": s.MountSprint = Math.Max(s.MountSprint, v); return true;
			case "GC_SaddleBerserk": s.MountBerserk += v; return true;
			case "GC_SaddleDeathRide": s.MountDeathRide = Math.Min(90f, s.MountDeathRide + v); return true;
			default: return false;
		}
	}

	private static IEnumerable<string> UniqueLocalizationKeys()
	{
		foreach (Effect effect in UniqueSpecials)
		{
			yield return "gc_me_" + effect.Key;
			yield return "gc_me_" + effect.Key + "_desc";
		}
		foreach ((string _, string _, string key, string _, string _, (string, float, float)[] _) in MoreUniques)
		{
			yield return "gc_leg_" + key;
			yield return "gc_leg_" + key + "_desc";
		}
		foreach ((string _, string _, string key, string _, (string, float, float)[] _) in MoreWhistleUniques)
		{
			yield return "gc_leg_" + key;
			yield return "gc_leg_" + key + "_desc";
		}
		foreach ((string _, string _, string key, string _, string _, (string, float, float)[] _) in SaddleUniques)
		{
			yield return "gc_leg_" + key;
			yield return "gc_leg_" + key + "_desc";
		}
	}

	/// <summary>
	/// Sättel als Beute (0,5 % je Kreatur, eigene Tabelle neben der Leinen-Tabelle). Nur Sättel, die es gibt: Bär und Unbjørn
	/// legt GC_Tierarten an; ob Bären gerade zähmbar sind, prüft SaddleUniqueEffects beim Fallen (Einstellung vom Server).
	/// </summary>
	private static void AppendSaddleLoot(StringBuilder sb, ref int tables)
	{
		foreach ((string saddle, string[] creatureTables) in SaddleLoot)
		{
			if (PrefabManager.Instance.GetPrefab(saddle) == null)
			{
				continue;
			}
			foreach (string table in creatureTables)
			{
				string rarity = NoLegendaryLoot.Contains(table) ? "[70,22,8,0,0,0]" : "[70,22,7,1,0,0]";
				sb.Append(tables++ > 0 ? "," : "").Append("{\"Object\":\"").Append(table)
					.Append("\",\"LeveledLoot\":[{\"Level\":1,\"Drops\":[[0,199],[1,1]],\"Loot\":[{\"Item\":\"").Append(saddle)
					.Append("\",\"Weight\":1,\"Rarity\":").Append(rarity).Append("}]}],\"StarScaling\":{\"DropChance\":0,\"BonusDrops\":0,\"RarityShift\":0}}");
			}
		}
	}
}
