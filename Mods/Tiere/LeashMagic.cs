using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace GC.Server.Leash;

/// <summary>
/// EpicLoot-Anbindung der Tierleinen. Nur aktiv, wenn EpicLoot geladen ist; keine Voraussetzung für Leinen.
///
/// Über EpicLoots öffentliche Schnittstelle (EpicLoot.API, Daten als JSON, bleibt über jeden Server-Abgleich erhalten):
///  * eigene Effekte nur für Leinen (Bedingung „GC_IsLeash"): 31 würfelbare (14 davon nach dem Vorbild von EpicLoots
///    eigenen Effekten, mit deren Werten) und 10 Sondereffekte (NoRoll) nur für die legendären und mythischen Leinen,
///  * 10 legendäre und 5 mythische Leinen mit festen Effekten (je mit Boss und Mindest-Leine), dazu 4 legendäre und 2
///    mythische Tierpfeifen (seit 24.10.2026),
///  * seltene Leinen als Beute (je Biom-Stufe die passende Leine, 1 % pro Kreatur; wie bei jeder EpicLoot-Beute macht
///    EpicLoot nach seinen Einstellungen einen Teil davon zu Splittersteinen oder Material).
/// Damit EpicLoot eine Leine (Item-Typ „Sonstiges") überhaupt verzaubert, gibt ein Postfix auf
/// EpicLoot.CanBeMagicItem sie frei; danach gelten alle EpicLoot-Wege: Verzaubern, Aufwerten, Entzaubern, Opfern,
/// Runen, Härten, Sockel, Beute. Gelesen werden die Effekte direkt aus den Item-Daten (EpicLoot speichert sie als JSON in
/// m_customData), ohne harte Abhängigkeit.
/// </summary>
internal static partial class LeashMagic
{
	internal const string EpicLootGuid = "randyknapp.mods.epicloot";
	private const string MagicKey = "randyknapp.mods.epicloot#EpicLoot.MagicItemComponent";
	private const string Requirement = "GC_IsLeash";
	/// <summary>Tierpfeifen (Gorduan, 18.10.2026): Effekte, die auch auf Pfeifen würfeln; sie wirken beim Pfiff kurz auf die Tiere.</summary>
	private const string WhistleRequirement = "GC_IsLeashOrWhistle";
	/// <summary>
	/// Walkürenpfeife (Gorduan, 19.10.2026): Unzerstörbar gegen das Zerbrechen beim Abliefern. Seit 25.10.2026 zerbricht sie nicht
	/// mehr (Gorduan): Die Eigenschaft würfelt nicht mehr, legendäre und mythische Walkürenpfeifen bekommen andere.
	/// </summary>
	private const string ValkyrieRequirement = "GC_IsValkyrieWhistle";
	internal const string ValkyrieUnbreakable = "GC_LeashValkyrieUnbreakable";
	/// <summary>Sättel (Gorduan, 20.10.2026): eigene Sattel-Effekte (Ausdauer); die Pfeifen-Effekte würfeln auch auf Sätteln (außer NotOnSaddles).</summary>
	private const string SaddleRequirement = "GC_IsSaddle";
	/// <summary>
	/// Pfeifen-Effekte, die an Sätteln nicht wirken (Review 27.10.2026): Leuchten gibt es nur an der Leine, Angriffstempo nur,
	/// wenn das Tier selbst angreift (beim Reiten greift der Reiter an). Sie würfeln an Sätteln nicht mehr; vorhandene Sättel
	/// behalten ihre Werte (EpicLoot prüft die Bedingung nur, wenn ein Effekt neu dazukommt).
	/// </summary>
	private static readonly HashSet<string> NotOnSaddles = new() { "GC_LeashGlow", "GC_LeashAnimalAttackSpeed" };
	/// <summary>Legendäre und mythische Tierpfeifen (Gorduan, 24.10.2026): je Boss eine Bedingung „GC_IsWhistle_&lt;Boss&gt;“.</summary>
	private const string WhistleUniqueRequirement = "GC_IsWhistle";
	private static readonly HashSet<string> WhistleEffects = new()
	{
		"damage", "attackspeed", "health", "speed", "resist", "thorns", "fire", "frost", "lightning", "poison", "spirit",
		"lifesteal", "evade", "deter", "staggerpower", "berserk", "steadfast", "glow",
	};

	/// <summary>Eigene Items, an denen nur eigene Effekte würfeln: Leinen, Tierpfeifen und alle Sättel (Gorduan, 20.10.2026).</summary>
	private static bool Ours(ItemDrop.ItemData item) => Plugin.IsLeash(item, out _) || Whistles.IsWhistle(item, out _) || Saddles.IsSaddle(item);

	internal static bool Available { get; private set; }

	private static Type? api;
	private static readonly Dictionary<string, BondStats> Cache = new();
	private static readonly Regex EffectObject = new("\\{[^{}]*\"EffectType\"[^{}]*\\}", RegexOptions.Compiled);
	private static readonly Regex EffectType = new("\"EffectType\"\\s*:\\s*\"(?<v>[^\"]+)\"", RegexOptions.Compiled);
	private static readonly Regex EffectValue = new("\"EffectValue\"\\s*:\\s*(?<v>-?[0-9.]+(?:[eE][-+]?[0-9]+)?)", RegexOptions.Compiled);

	// Stufen in EpicLoot-Reihenfolge: Magisch, Selten, Episch, Legendär, Mythisch, Uralt
	private sealed class Effect
	{
		public string Type = "";
		public string Key = "";
		public float[,] Values = new float[6, 2];
		public float Increment = 1f;
		public float Weight = 1f;
		public bool NoRoll;
		public string[]? Rarities;
		/// <summary>Eigene Bedingung statt Leine/Pfeife (Walkürenpfeife).</summary>
		public string? RequirementName;
	}

	private static Effect Roll(string type, string key, float increment, params float[] minMax)
	{
		Effect effect = new() { Type = type, Key = key, Increment = increment };
		for (int i = 0; i < 6; i++)
		{
			effect.Values[i, 0] = minMax[i * 2];
			effect.Values[i, 1] = minMax[i * 2 + 1];
		}
		return effect;
	}

	/// <summary>Würfelbar mit eigenem Gewicht (seltener als die Grundeffekte) und optional nur ab bestimmten Seltenheiten.</summary>
	private static Effect Rare(string type, string key, float increment, float weight, string[]? rarities, params float[] minMax)
	{
		Effect effect = Roll(type, key, increment, minMax);
		effect.Weight = weight;
		effect.Rarities = rarities;
		return effect;
	}

	private static readonly string[] FromRare = { "Rare", "Epic", "Legendary", "Mythic", "Ancient" };
	private static readonly string[] FromEpic = { "Epic", "Legendary", "Mythic", "Ancient" };

	private static Effect Special(string type, string key, float value)
	{
		Effect effect = new() { Type = type, Key = key, Increment = 0f, NoRoll = true };
		for (int i = 0; i < 6; i++)
		{
			effect.Values[i, 0] = value;
			effect.Values[i, 1] = value;
		}
		return effect;
	}

	/// <summary>Würfelbare Effekte (Magisch … Uralt, jeweils von–bis).</summary>
	private static readonly Effect[] Rollable =
	{
		Roll("GC_LeashRange", "range", 1f, 1, 2, 1, 3, 2, 5, 3, 8, 5, 10, 8, 20),
		Roll("GC_LeashDurability", "durability", 5f, 5, 15, 10, 25, 20, 40, 30, 60, 50, 80, 70, 100),
		Roll("GC_LeashAnimalDamage", "damage", 5f, 5, 15, 10, 25, 20, 40, 35, 60, 50, 80, 70, 100),
		Roll("GC_LeashAnimalAttackSpeed", "attackspeed", 5f, 5, 15, 10, 25, 20, 40, 35, 60, 50, 80, 70, 100),
		Roll("GC_LeashAnimalHealth", "health", 5f, 5, 15, 10, 25, 20, 40, 35, 60, 50, 80, 70, 100),
		Roll("GC_LeashAnimalSpeed", "speed", 5f, 5, 15, 10, 25, 20, 40, 35, 60, 50, 80, 70, 100),
		Roll("GC_LeashAnimalRegen", "regen", 1f, 1, 1, 1, 2, 2, 3, 3, 4, 4, 6, 6, 8),
		Roll("GC_LeashAnimalResist", "resist", 5f, 5, 10, 10, 20, 15, 30, 25, 40, 35, 50, 45, 60),
		Roll("GC_LeashAnimalThorns", "thorns", 5f, 5, 10, 10, 20, 15, 30, 25, 40, 35, 50, 50, 75),
		Roll("GC_LeashAnimalFeed", "feed", 5f, 10, 20, 15, 30, 25, 50, 40, 75, 60, 100, 100, 150),
		Roll("GC_LeashLoadReduction", "load", 1f, 1, 1, 1, 1, 1, 2, 2, 3, 2, 4, 3, 5),
		Roll("GC_LeashGentle", "gentle", 5f, 10, 20, 15, 30, 25, 40, 35, 55, 50, 70, 60, 90),
		Roll("GC_LeashPull", "pull", 5f, 10, 25, 20, 40, 30, 60, 50, 80, 70, 100, 90, 150),
		Roll("GC_LeashTamer", "tamer", 5f, 10, 20, 15, 30, 25, 50, 40, 75, 60, 100, 100, 150),
		// Gehorsam (Gorduan): 0 bis 100 %, bei 100 % greift das Tier unterwegs niemanden an
		Roll("GC_LeashObedience", "obedience", 5f, 5, 15, 10, 25, 20, 40, 35, 60, 50, 80, 70, 100),
		new Effect { Type = "GC_LeashUnbreakable", Key = "unbreakable", Increment = 0f, Weight = 0.05f, Rarities = new[] { "Mythic", "Ancient" }, Values = Ones() },
		// Walkürenpfeife: Unzerstörbar war nur gegen das Zerbrechen beim Abliefern. Seit 25.10.2026 zerbricht sie nicht mehr (Gorduan),
		// es würfelt nicht mehr; die Definition bleibt, damit vorhandene Pfeifen ihre Eigenschaft behalten (ohne Wirkung)
		new Effect { Type = ValkyrieUnbreakable, Key = "valkyrie_unbreakable", Increment = 0f, Weight = 0.03f, RequirementName = ValkyrieRequirement, Values = Ones(), NoRoll = true },
		// Wiederkehr (Gorduan): sehr selten, nur ab Legendär; je besser die Leine, desto kürzer die Ohnmacht (Sekunden)
		new Effect { Type = "GC_LeashRevive", Key = "revive", Increment = 5f, Weight = 0.1f, Rarities = new[] { "Legendary", "Mythic", "Ancient" },
			Values = new float[,] { { 120, 120 }, { 120, 120 }, { 120, 120 }, { 90, 120 }, { 45, 90 }, { 15, 45 } } },

		// Nach EpicLoot-Vorbild (Gorduan, 13.10.2026): EpicLoots eigene Effekte, die auf ein angeleintes Tier oder seinen Halter
		// passen, mit EpicLoots Werten der Abstimmung „balanced" (Magisch bis Mythisch) und einer Stufe mehr für Uralt.
		// Bisse = AddFire/Frost/Lightning/Poison/SpiritDamage: +x % des Schadens als Element
		Rare("GC_LeashFireBite", "fire", 1f, 0.5f, null, 2, 4, 3, 6, 4, 8, 5, 10, 6, 12, 10, 15),
		Rare("GC_LeashFrostBite", "frost", 1f, 0.5f, null, 2, 4, 3, 6, 4, 8, 5, 10, 6, 12, 10, 15),
		Rare("GC_LeashThunder", "lightning", 1f, 0.5f, null, 2, 4, 3, 6, 4, 8, 5, 10, 6, 12, 10, 15),
		Rare("GC_LeashPoisonBite", "poison", 1f, 0.5f, null, 2, 4, 3, 6, 4, 8, 5, 10, 6, 12, 10, 15),
		Rare("GC_LeashSpiritBite", "spirit", 1f, 0.5f, null, 2, 4, 3, 6, 4, 8, 5, 10, 6, 12, 10, 15),
		// LifeSteal: Tier heilt sich um x % des verursachten Schadens (EpicLoot erst ab Selten)
		Rare("GC_LeashLifeSteal", "lifesteal", 0.5f, 0.5f, FromRare, 1, 1, 1.5f, 3, 2.5f, 5, 4.5f, 7, 6.5f, 10, 8, 12),
		// AvoidDamageTaken: x % der Treffer gehen am Tier vorbei
		Rare("GC_LeashEvade", "evade", 1f, 0.5f, null, 2, 4, 3, 6, 4, 8, 5, 10, 6, 12, 10, 15),
		// StaggerOnDamageTaken: wer das Tier trifft, taumelt mit x % Wahrscheinlichkeit
		Rare("GC_LeashDeter", "deter", 1f, 0.5f, null, 1, 2, 2, 4, 3, 6, 4, 8, 5, 10, 8, 12),
		// ModifyStaggerDamage: Angriffe des Tiers bringen Gegner x % leichter ins Taumeln
		Rare("GC_LeashStaggerPower", "staggerpower", 1f, 1f, null, 2, 6, 3, 9, 4, 12, 5, 15, 12, 18, 15, 25),
		// ModifyDamageLowHealth / Berserker: unter 30 % Leben mehr Schaden
		Rare("GC_LeashBerserk", "berserk", 1f, 0.5f, null, 4, 9, 7, 12, 9, 15, 12, 18, 15, 21, 20, 30),
		// Immovable: kein Zurückstoßen, kein Taumeln (Schalter, ab Episch)
		new Effect { Type = "GC_LeashSteadfast", Key = "steadfast", Increment = 0f, Weight = 0.3f, Rarities = FromEpic, Values = Ones() },
		// Glowing: das Tier leuchtet (8 m, für alle sichtbar)
		new Effect { Type = "GC_LeashGlow", Key = "glow", Increment = 0f, Weight = 0.5f, Values = Fixed(8f) },
		// AddCarryWeight: Tragkraft des Halters, solange ein Tier an der Leine hängt
		Rare("GC_LeashBondCarry", "bondcarry", 5f, 1f, null, 25, 50, 40, 75, 70, 100, 90, 125, 120, 150, 150, 200),
		// ModifyDiscoveryRadius → Späher: Gegner im Umkreis auf der Karte des Halters (Meter)
		Rare("GC_LeashBondReveal", "bondreveal", 1f, 1f, null, 10, 15, 15, 20, 20, 25, 25, 30, 30, 35, 35, 40),

		// Sättel (Gorduan, 20.10.2026): Ausdauer des Reittiers, wirkt nur beim Reiten
		Saddle("GC_SaddleStamina", "saddlestamina", 5f, 10, 20, 15, 30, 25, 45, 40, 60, 55, 80, 75, 100),
		Saddle("GC_SaddleStaminaRegen", "saddleregen", 5f, 10, 20, 15, 30, 25, 45, 40, 60, 55, 80, 75, 100),
		Saddle("GC_SaddleStaminaSave", "saddlesave", 5f, 5, 10, 10, 15, 15, 25, 20, 35, 30, 45, 40, 50),
	};

	private static Effect Saddle(string type, string key, float increment, params float[] minMax)
	{
		Effect effect = Roll(type, key, increment, minMax);
		effect.RequirementName = SaddleRequirement;
		return effect;
	}

	/// <summary>Sondereffekte nur auf legendären und mythischen Leinen (würfeln nie zufällig).</summary>
	private static readonly Effect[] Specials =
	{
		Special("GC_LeashBondSpeed", "bondspeed", 15),
		Special("GC_LeashBondRegen", "bondregen", 2),
		Special("GC_LeashPoisonImmune", "poisonimmune", 1),
		Special("GC_LeashFearless", "fearless", 1),
		Special("GC_LeashSwim", "swim", 50),
		Special("GC_LeashYouth", "youth", 30),
		Special("GC_LeashIllusions", "illusions", 2),
		Special("GC_LeashBondHunt", "bondhunt", 15),
		Special("GC_LeashBondWatch", "bondwatch", 30),
		Special("GC_LeashBondStealth", "bondstealth", 50),
		// Mythische Tierpfeifen (Gorduan, 24.10.2026: „mythisch darf mehr, aber eben nur spezielle einzigartige Effekte“), wirken beim Pfiff
		WhistleSpecial("GC_LeashWhistleFireproof", "whistle_fireproof", 1),
		WhistleSpecial("GC_LeashWhistleBlaze", "whistle_blaze", 80),
		WhistleSpecial("GC_LeashWhistleHall", "whistle_hall", 12),
		WhistleSpecial("GC_LeashWhistleRally", "whistle_rally", 20),
	};

	/// <summary>Sondereffekt nur der mythischen Tierpfeifen (würfelt nie zufällig, Bedingung: Leine oder Pfeife).</summary>
	private static Effect WhistleSpecial(string type, string key, float value)
	{
		Effect effect = Special(type, key, value);
		effect.RequirementName = WhistleRequirement;
		return effect;
	}

	private static float[,] Fixed(float value)
	{
		float[,] values = new float[6, 2];
		for (int i = 0; i < 6; i++)
		{
			values[i, 0] = value;
			values[i, 1] = value;
		}
		return values;
	}

	private static float[,] Ones()
	{
		float[,] values = new float[6, 2];
		for (int i = 0; i < 6; i++)
		{
			values[i, 0] = 1f;
			values[i, 1] = 1f;
		}
		return values;
	}

	/// <summary>Legendäre (L) und mythische (M) Leinen: feste Effekte (Typ, von, bis).</summary>
	private static readonly (string Id, string Kind, string Key, (string Type, float Min, float Max)[] Effects)[] Uniques =
	{
		("GC_SleipnirsReins", "Legendary", "sleipnir", new[] { ("GC_LeashAnimalSpeed", 60f, 100f), ("GC_LeashRange", 4f, 8f), ("GC_LeashBondSpeed", 15f, 15f) }),
		("GC_FreyjasHarness", "Legendary", "freyja", new[] { ("GC_LeashAnimalAttackSpeed", 40f, 80f), ("GC_LeashDurability", 40f, 60f), ("GC_LeashBondCarry", 100f, 100f) }),
		("GC_TanngrisnirsRope", "Legendary", "tanngrisnir", new[] { ("GC_LeashAnimalDamage", 50f, 80f), ("GC_LeashAnimalHealth", 50f, 80f), ("GC_LeashRevive", 30f, 60f) }),
		("GC_SkadisHuntingLeash", "Legendary", "skadi", new[] { ("GC_LeashAnimalDamage", 40f, 70f), ("GC_LeashRange", 3f, 6f), ("GC_LeashFrostBite", 25f, 25f) }),
		("GC_HuginnAndMuninn", "Legendary", "huginn", new[] { ("GC_LeashRange", 5f, 10f), ("GC_LeashDurability", 40f, 60f), ("GC_LeashBondReveal", 40f, 40f) }),
		("GC_Gleipnir", "Mythic", "gleipnir", new[] { ("GC_LeashUnbreakable", 1f, 1f), ("GC_LeashRange", 15f, 20f), ("GC_LeashAnimalDamage", 80f, 100f), ("GC_LeashAnimalHealth", 80f, 100f), ("GC_LeashBerserk", 100f, 100f) }),
		("GC_Gullinbursti", "Mythic", "gullinbursti", new[] { ("GC_LeashAnimalSpeed", 80f, 100f), ("GC_LeashAnimalRegen", 5f, 8f), ("GC_LeashGlow", 12f, 12f), ("GC_LeashBondRegen", 2f, 2f), ("GC_LeashThunder", 25f, 25f) }),
		// zweite Runde (Gorduan: 5 weitere legendäre, 3 weitere mythische, eine davon Lokis)
		("GC_HeimdallsWatch", "Legendary", "heimdall", new[] { ("GC_LeashBondWatch", 30f, 30f), ("GC_LeashRange", 4f, 8f), ("GC_LeashAnimalHealth", 40f, 70f) }),
		("GC_GarmsChain", "Legendary", "garm", new[] { ("GC_LeashSpiritBite", 30f, 30f), ("GC_LeashFearless", 1f, 1f), ("GC_LeashAnimalDamage", 40f, 70f) }),
		("GC_UllrsStrap", "Legendary", "ullr", new[] { ("GC_LeashBondHunt", 15f, 15f), ("GC_LeashAnimalAttackSpeed", 30f, 60f), ("GC_LeashRange", 3f, 6f) }),
		("GC_NjordsRope", "Legendary", "njord", new[] { ("GC_LeashSwim", 50f, 50f), ("GC_LeashAnimalSpeed", 40f, 70f), ("GC_LeashDurability", 40f, 60f) }),
		("GC_IdunsApple", "Legendary", "idun", new[] { ("GC_LeashYouth", 30f, 30f), ("GC_LeashAnimalRegen", 3f, 4f), ("GC_LeashBondRegen", 1f, 1f) }),
		// Gorduan (13.10.2026): Zahl der Trugbilder (1 oder 2) würfelt EpicLoot beim Erschaffen der Leine, nicht beim Anleinen
		("GC_LokisMirage", "Mythic", "loki", new[] { ("GC_LeashIllusions", 1f, 2f), ("GC_LeashEvade", 25f, 25f), ("GC_LeashBondStealth", 50f, 50f), ("GC_LeashAnimalSpeed", 50f, 80f), ("GC_LeashRange", 5f, 10f) }),
		("GC_JormungandsRing", "Mythic", "jormungand", new[] { ("GC_LeashPoisonBite", 40f, 40f), ("GC_LeashPoisonImmune", 1f, 1f), ("GC_LeashRange", 10f, 15f), ("GC_LeashAnimalHealth", 80f, 100f), ("GC_LeashAnimalThorns", 40f, 60f) }),
		("GC_YggdrasilsRoot", "Mythic", "yggdrasil", new[] { ("GC_LeashSteadfast", 1f, 1f), ("GC_LeashRevive", 15f, 15f), ("GC_LeashAnimalRegen", 5f, 8f), ("GC_LeashBondRegen", 2f, 2f), ("GC_LeashAnimalHealth", 60f, 90f) }),
	};

	/// <summary>
	/// Gorduan: Jede legendäre und mythische Leine braucht einen besiegten Boss (Weltschlüssel). Erst dann kann EpicLoot sie
	/// als Beute oder beim Verzaubern wählen; vorher nimmt es eine andere freie Leine oder würfelt Zufallseffekte.
	/// Zwei Leinen je Boss in Spielreihenfolge, die mythischen erst ab der Königin. Schlüssel: Spieldaten 1.0.17
	/// (Eikthyr bis Yagluth) und EpicLoots Biom-Daten (Königin, Fader, Tiefer Norden).
	/// </summary>
	private static readonly Dictionary<string, string> BossFor = new()
	{
		["GC_SleipnirsReins"] = "defeated_eikthyr",
		["GC_HuginnAndMuninn"] = "defeated_eikthyr",
		["GC_NjordsRope"] = "defeated_gdking", // Der Älteste: danach beginnt die Seefahrt
		["GC_FreyjasHarness"] = "defeated_gdking",
		["GC_GarmsChain"] = "defeated_bonemass", // Geisterbiss gegen die Untoten des Sumpfs
		["GC_IdunsApple"] = "defeated_bonemass",
		["GC_SkadisHuntingLeash"] = "defeated_dragon", // Moder: Berge, Frost
		["GC_UllrsStrap"] = "defeated_dragon",
		["GC_TanngrisnirsRope"] = "defeated_goblinking", // Yagluth
		["GC_HeimdallsWatch"] = "defeated_goblinking",
		["GC_Gullinbursti"] = "defeated_queen",
		["GC_YggdrasilsRoot"] = "defeated_queen", // in den Nebellanden wachsen Yggdrasils Wurzeln
		["GC_JormungandsRing"] = "defeated_fader",
		["GC_LokisMirage"] = "defeated_fader",
		["GC_Gleipnir"] = "defeated_frozenking_p3", // Endboss des Tiefen Nordens
	};

	/// <summary>
	/// Mindest-Leine je Boss (Gorduan, 24.10.2026: seit es eine Leine je Biom gibt, „ab Biom 3 legendäre Leinen“, also ab dem
	/// Sumpf): die Leine aus dem Biom des Bosses, nie unter der Trollleder-Leine (Rang 2, auch die Leine des Sumpfs). Eine
	/// legendäre oder mythische Leine entsteht nur aus dieser Leine oder einer besseren; Gleipnir nur aus der Elchhaut-Leine.
	/// </summary>
	private static readonly Dictionary<string, int> LeashRankAfter = new()
	{
		["defeated_eikthyr"] = 2,
		["defeated_gdking"] = 2,
		["defeated_bonemass"] = 2, // Sumpf: Trollleder
		["defeated_dragon"] = 3, // Berge: Wolfspelz
		["defeated_goblinking"] = 4, // Ebenen: Lox-Pelz
		["defeated_queen"] = 5, // Nebellande: Schuppenhaut
		["defeated_fader"] = 6, // Aschlande: Asksvin-Haut
		["defeated_frozenking_p3"] = 7, // Tiefer Norden: Elchhaut
	};

	/// <summary>
	/// Legendäre und mythische Tierpfeifen (Gorduan, 24.10.2026), wie die Leinen mit Boss und Mindest-Pfeife; ihre Effekte wirken
	/// wie alle Pfeifen-Verzauberungen beim Pfiff (Whistles.Buff, Whistles.Mythic). Legendär: ein Drittel der ersten
	/// Vorschlagswerte (Gorduan); mythisch: nur eigene, einzigartige Effekte (Specials „whistle_…“).
	/// </summary>
	private static readonly (string Id, string Kind, string Key, string Boss, (string Type, float Min, float Max)[] Effects)[] WhistleUniques =
	{
		("GC_BragisLay", "Legendary", "bragi", "defeated_bonemass", new[] { ("GC_LeashAnimalAttackSpeed", 13f, 20f), ("GC_LeashAnimalHealth", 10f, 17f), ("GC_LeashLifeSteal", 3f, 3f) }),
		("GC_SkollsHuntingCall", "Legendary", "skoll", "defeated_dragon", new[] { ("GC_LeashAnimalDamage", 13f, 23f), ("GC_LeashFrostBite", 8f, 8f), ("GC_LeashStaggerPower", 5f, 5f) }),
		("GC_HatisHowl", "Legendary", "hati", "defeated_goblinking", new[] { ("GC_LeashAnimalSpeed", 17f, 27f), ("GC_LeashBerserk", 17f, 17f), ("GC_LeashDeter", 3f, 3f) }),
		("GC_BrynhildsCall", "Legendary", "brynhild", "defeated_goblinking", new[] { ("GC_LeashThunder", 7f, 7f), ("GC_LeashAnimalHealth", 13f, 20f), ("GC_LeashSteadfast", 1f, 1f) }),
		("GC_SurtsBlaze", "Mythic", "surt", "defeated_fader", new[] { ("GC_LeashWhistleFireproof", 1f, 1f), ("GC_LeashWhistleBlaze", 80f, 80f) }),
		("GC_Gjallarhorn", "Mythic", "gjallarhorn", "defeated_frozenking_p3", new[] { ("GC_LeashWhistleHall", 12f, 12f), ("GC_LeashWhistleRally", 20f, 20f) }),
	};

	/// <summary>
	/// Mindest-Pfeife je Boss: die Pfeife aus dem Biom des Bosses, nie unter Eisen (Stufe 2, die Pfeife des Sumpfs); für Berge
	/// und Tiefen Norden gibt es keine eigene, dort gilt die nächstniedrigere.
	/// </summary>
	private static readonly Dictionary<string, int> WhistleLevelAfter = new()
	{
		["defeated_bonemass"] = 2, // Sumpf: Eisen
		["defeated_dragon"] = 2, // Berge: noch Eisen
		["defeated_goblinking"] = 3, // Ebenen: Walküre (Schwarzmetall)
		["defeated_queen"] = 3, // Nebellande: keine eigene Pfeife, noch die Walküre (27.10.2026: Huldras Lockruf)
		["defeated_fader"] = 4, // Aschlande: Flammenmetall
		["defeated_frozenking_p3"] = 4, // Tiefer Norden: noch Flammenmetall
	};

	private static string BossRequirement(string boss) => Requirement + "_" + boss;

	private static string WhistleBossRequirement(string boss) => WhistleUniqueRequirement + "_" + boss;

	/// <summary>
	/// Boss besiegt? Über Valheims Schlüsselabfrage: Mit World Advancement Progression und privaten Schlüsseln (so auf
	/// dem GC-Server) leitet WAP diese Abfrage auf die Schlüssel des eigenen Spielers um (Patch auf
	/// ZoneSystem.GetGlobalKey), sonst gilt der Weltschlüssel. Keine eigene Abfrage der Spieler-Schlüssel: die kämen auch
	/// aus anderen Welten mit (Review).
	/// </summary>
	internal static bool BossDefeated(string key) => ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(key);

	/// <summary>
	/// Beute: Tier-Tabellen von EpicLoot → passende Leine (1 % pro Kreatur, eigene unabhängige Tabelle). Seit 19.10.2026 eine
	/// Leine je Biom (Gorduan): Tabellen mit Kreaturen aus zwei Biomen (Tier2: Schwarzwald, Tier8: Aschlande und Tiefer Norden)
	/// nennen beide Leinen. Seit EpicLoot 0.15.0 (so auch 0.15.2) stehen Björn, Lox, Fenris, Elch, Jötunn und weitere in den
	/// Elite-Tabellen; damit sie weiter Leinen fallen lassen, tragen auch die Elite-Tabellen die Leine ihres Bioms.
	/// </summary>
	private static readonly (string Table, string[] Leashes)[] Loot =
	{
		("Tier0Mob", new[] { "GC_Leash_DeerHide" }),
		("Tier1Mob", new[] { "GC_Leash_DeerHide" }),
		("Tier2Mob", new[] { "GC_Leash_DeerHide", "GC_Leash_TrollHide" }),
		("Tier3Mob", new[] { "GC_Leash_TrollHide" }),
		("Tier4Mob", new[] { "GC_Leash_WolfPelt" }),
		("Tier5Mob", new[] { "GC_Leash_LoxPelt" }),
		("Tier6Mob", new[] { "GC_Leash_ScaleHide" }),
		("Tier7Mob", new[] { "GC_Leash_AskHide" }),
		("Tier8Mob", new[] { "GC_Leash_AskHide", "GC_Leash_MooseHide" }),
		("Tier9Mob", new[] { "GC_Leash_MooseHide" }),
		("Tier3EliteMob", new[] { "GC_Leash_TrollHide" }),
		("Tier4EliteMob", new[] { "GC_Leash_TrollHide" }),
		("Tier5EliteMob", new[] { "GC_Leash_WolfPelt" }),
		("Tier6EliteMob", new[] { "GC_Leash_LoxPelt" }),
		("Tier7EliteMob", new[] { "GC_Leash_ScaleHide" }),
		("Tier8EliteMob", new[] { "GC_Leash_AskHide" }),
		("Tier9EliteMob", new[] { "GC_Leash_MooseHide" }),
	};

	/// <summary>
	/// Hirsch und Robbe haben seit EpicLoot 0.15.0 (auch 0.15.2) gar keine EpicLoot-Beute mehr (friedliche Tiere): je eine
	/// eigene Tabelle nur mit ihrer Leine, aber nur, solange EpicLoot für sie keine Tabelle kennt (eine eigene Tabelle ersetzte
	/// EpicLoots Verweis).
	/// Entschieden wird einmal beim Start mit der eigenen Config; EpicLoot trägt die Tabelle nach jedem Neuladen wieder ein.
	/// Kennte erst die Config eines Servers Hirsch oder Robbe, verlören sie dort ihre übrige Beute (Prüfung 24.10.2026; mit
	/// EpicLoots Standard-Config nie der Fall).
	/// </summary>
	private static readonly (string Creature, string Leash)[] OwnTables =
	{
		("Deer", "GC_Leash_DeerHide"),
		("Seal", "GC_Leash_MooseHide"),
	};

	/// <summary>
	/// Tabellen aus Wiesen und Schwarzwald (EpicLoot 0.15.0 und 0.15.2: Tier0–2Mob, die Schwarzwald-Elite Björn/Troll in
	/// Tier3EliteMob, dazu der Hirsch): Leinen fallen dort höchstens episch (Gorduan, 24.10.2026: legendär erst ab dem Sumpf).
	/// </summary>
	private static readonly HashSet<string> NoLegendaryLoot = new() { "Tier0Mob", "Tier1Mob", "Tier2Mob", "Tier3EliteMob", "Deer" };

	/// <summary>Alle Text-Schlüssel, die Valheim übersetzen muss (Effekt- und Leinen-Namen).</summary>
	internal static IEnumerable<string> LocalizationKeys()
	{
		foreach (Effect effect in Rollable)
		{
			yield return "gc_me_" + effect.Key;
			yield return "gc_me_" + effect.Key + "_desc";
		}
		foreach (Effect effect in Specials)
		{
			yield return "gc_me_" + effect.Key;
			yield return "gc_me_" + effect.Key + "_desc";
		}
		foreach ((string _, string _, string key, (string, float, float)[] _) in Uniques)
		{
			yield return "gc_leg_" + key;
			yield return "gc_leg_" + key + "_desc";
		}
		foreach ((string _, string _, string key, string _, (string, float, float)[] _) in WhistleUniques)
		{
			yield return "gc_leg_" + key;
			yield return "gc_leg_" + key + "_desc";
		}
		foreach (string key in UniqueLocalizationKeys())
		{
			yield return key;
		}
	}

	// ------------------------------------------------------------------ Start

	/// <summary>
	/// Haken in EpicLoot (Review 27.10.2026): Jeder wird für sich angebunden. Ändert EpicLoot eine Methode, fehlt nur dieser
	/// Haken (Warnung im Log), nicht die übrigen und nicht der Rest von GC_Tiere.
	/// </summary>
	[Flags]
	internal enum Hooks
	{
		None = 0,
		/// <summary>EpicLoot.CanBeMagicItem: Leinen, Pfeifen und Sättel dürfen magisch werden.</summary>
		Magic = 1,
		/// <summary>MagicItemEffectDefinition.CheckRequirements: an eigenen Items nur eigene Effekte (Würfeln, Verzaubern).</summary>
		EffectLock = 2,
		/// <summary>MagicItemEffectRequirements.CheckRequirements: dasselbe bei direkter Prüfung (Legendäre, Sockel, Übertragen).</summary>
		RequirementLock = 4,
		/// <summary>Shards.GetShardEffect: eigene Splitter-Effekte in eigenen Items.</summary>
		Shards = 8,
		/// <summary>ShardSocketManager.CanSocket: Splitter ohne eigenen Effekt nicht in eigene Items (seit 0.15.2 auch von EpicLoot geprüft).</summary>
		Sockets = 16,
		/// <summary>MagicEffectRarity.EnsureAnchor und GetTier: Seltenheits-Sterne getrennt nach EpicLoots und eigenen Effekten.</summary>
		Flare = 32,
	}

	/// <summary>Angebundene Haken (Fähigkeiten der EpicLoot-Anbindung).</summary>
	internal static Hooks Active { get; private set; }

	internal static bool Has(Hooks hook) => (Active & hook) == hook;

	internal static void Init(Harmony harmony)
	{
		if (!Chainloader.PluginInfos.TryGetValue(EpicLootGuid, out BepInEx.PluginInfo info))
		{
			return; // ohne EpicLoot: Leinen wie immer
		}
		string version = info?.Metadata?.Version?.ToString() ?? "?";
		try
		{
			api = AccessTools.TypeByName("EpicLoot.API");
			Type? core = AccessTools.TypeByName("EpicLoot.EpicLoot");
			MethodInfo? canBeMagic = core != null ? AccessTools.Method(core, "CanBeMagicItem", new[] { typeof(ItemDrop.ItemData) }) : null;
			if (api == null || canBeMagic == null)
			{
				Plugin.Log.LogWarning($"Tierleinen: EpicLoot {version} gefunden, aber die Schnittstelle fehlt. Leinen bleiben ohne Verzauberung.");
				return;
			}
			TryPatch(harmony, canBeMagic, Hooks.Magic, postfix: nameof(CanBeMagicPostfix));
			PatchShards(harmony);
			PatchFlare(harmony);
			LockWeaponEffects(harmony);
			Available = true;
			Register();
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"Tierleinen: EpicLoot-Anbindung nicht vollständig: {e.GetBaseException().Message}");
		}
		Hooks missing = (Hooks.Magic | Hooks.EffectLock | Hooks.RequirementLock | Hooks.Shards | Hooks.Sockets | Hooks.Flare) & ~Active;
		Plugin.Log.LogInfo($"Tierleinen: EpicLoot {version}, Haken aktiv: {Active}" + (missing != Hooks.None ? $"; fehlen: {missing}" : "") + ".");
	}

	/// <summary>Einen EpicLoot-Haken anbinden; schlägt es fehl, nur eine Warnung (Review 27.10.2026).</summary>
	private static bool TryPatch(Harmony harmony, MethodBase? original, Hooks hook, string? prefix = null, string? postfix = null)
	{
		if (original == null)
		{
			return false;
		}
		try
		{
			harmony.Patch(original,
				prefix: prefix != null ? new HarmonyMethod(typeof(LeashMagic), prefix) : null,
				postfix: postfix != null ? new HarmonyMethod(typeof(LeashMagic), postfix) : null);
			Active |= hook;
			return true;
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"Tierleinen: EpicLoot-Haken {hook} ({original.DeclaringType?.Name}.{original.Name}) nicht angebunden: {e.GetBaseException().Message}");
			return false;
		}
	}

	private static FieldInfo? externalRequirements;

	/// <summary>
	/// Seit die Leine ein Einhandgegenstand ist (in der Hand wie eine Waffe), hielte EpicLoot sie für eine Waffe. Zwei
	/// Sperren lassen nur die eigenen Leinen-Effekte und legendären Leinen zu, keine Waffen-Verzauberungen, Runen oder
	/// legendären Waffen: beim Würfeln und Verzaubern (MagicItemEffectDefinition) und überall, wo EpicLoot die Bedingungen
	/// direkt prüft (legendäre Items, Sockel, Übertragen beim Herstellen; MagicItemEffectRequirements).
	/// </summary>
	private static void LockWeaponEffects(Harmony harmony)
	{
		MethodInfo? byDefinition = null;
		MethodInfo? byRequirements = null;
		try
		{
			Type? definition = AccessTools.TypeByName("EpicLoot.MagicItemEffectDefinition");
			Type? requirements = AccessTools.TypeByName("EpicLoot.MagicItemEffectRequirements");
			if (definition != null)
			{
				definitionType ??= AccessTools.Property(definition, "Type"); // für DefinitionLockPostfix (sonst aus PatchFlare)
				foreach (MethodInfo method in definition.GetMethods(BindingFlags.Public | BindingFlags.Instance))
				{
					if (method.Name == "CheckRequirements" && method.ReturnType == typeof(bool) && method.GetParameters().Length > 0 && method.GetParameters()[0].Name == "itemData")
					{
						byDefinition = method;
					}
				}
			}
			if (requirements != null)
			{
				externalRequirements = AccessTools.Field(requirements, "ExternalRequirements");
				foreach (MethodInfo method in requirements.GetMethods(BindingFlags.Public | BindingFlags.Instance))
				{
					ParameterInfo[] parameters = method.GetParameters();
					if (method.Name == "CheckRequirements" && method.ReturnType == typeof(bool) && parameters.Length > 2 && parameters[0].Name == "itemData" && parameters[2].IsOut)
					{
						byRequirements = method; // die Fassung mit „failure" (out), bei der alle anderen landen
					}
				}
			}
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"Tierleinen: EpicLoot-Sperre für Waffen-Effekte nicht gefunden: {e.GetBaseException().Message}");
		}
		bool locked = definitionType != null && TryPatch(harmony, byDefinition, Hooks.EffectLock, postfix: nameof(DefinitionLockPostfix));
		locked &= externalRequirements != null && TryPatch(harmony, byRequirements, Hooks.RequirementLock, postfix: nameof(RequirementsLockPostfix));
		if (!locked)
		{
			Plugin.Log.LogWarning("Tierleinen: EpicLoot-Sperre für Waffen-Effekte nicht vollständig; Leinen könnten Waffen-Verzauberungen bekommen.");
		}
	}

	/// <summary>
	/// Effekt-Auswahl (Würfeln, Verzaubern, Aufwerten): an Leinen nur die eigenen Leinen-Effekte. Den Typ liest die
	/// vorhandene PropertyInfo (Review 27.10.2026: vorher je Aufruf eine neue Traverse).
	/// </summary>
	private static void DefinitionLockPostfix(object __instance, ItemDrop.ItemData itemData, ref bool __result)
	{
		if (__result && itemData != null && Ours(itemData) && definitionType?.GetValue(__instance) is string type
			&& !type.StartsWith("GC_Leash", StringComparison.Ordinal) && !type.StartsWith("GC_Saddle", StringComparison.Ordinal))
		{
			__result = false;
		}
	}

	/// <summary>
	/// Direkte Bedingungsprüfung: an Leinen nur, was eine Leinen-Bedingung (GC_IsLeash…) trägt, an Tierpfeifen nur die
	/// Pfeifen-Bedingungen (GC_IsLeashOrWhistle, Walküre, legendäre/mythische Pfeifen GC_IsWhistle_…).
	/// </summary>
	private static void RequirementsLockPostfix(object __instance, ItemDrop.ItemData itemData, ref bool __result)
	{
		if (!__result || itemData == null)
		{
			return;
		}
		bool whistle = Whistles.IsWhistle(itemData, out _);
		bool saddle = !whistle && Saddles.IsSaddle(itemData);
		if (!whistle && !saddle && !Plugin.IsLeash(itemData, out _))
		{
			return;
		}
		if (externalRequirements?.GetValue(__instance) is List<string> list)
		{
			foreach (string requirement in list)
			{
				if (requirement == null)
				{
					continue;
				}
				bool fits = whistle ? requirement == WhistleRequirement || requirement == ValkyrieRequirement || requirement.StartsWith(WhistleUniqueRequirement + "_", StringComparison.Ordinal)
					: saddle ? requirement == WhistleRequirement || requirement == SaddleRequirement
					: requirement.StartsWith(Requirement, StringComparison.Ordinal);
				if (fits)
				{
					return;
				}
			}
		}
		__result = false;
	}

	/// <summary>Leinen und Tierpfeifen (nicht stapelbar) dürfen magisch werden, auch falls EpicLoot ihren Item-Typ nicht zulässt.</summary>
	private static void CanBeMagicPostfix(ItemDrop.ItemData item, ref bool __result)
	{
		if (!__result && item?.m_shared != null && item.m_shared.m_maxStackSize == 1 && Ours(item))
		{
			__result = true;
		}
	}

	private static object? Call(string method, params object[] args)
	{
		MethodInfo? info = null;
		foreach (MethodInfo candidate in api!.GetMethods(BindingFlags.Public | BindingFlags.Static))
		{
			if (candidate.Name == method && candidate.GetParameters().Length == args.Length)
			{
				info = candidate;
				break;
			}
		}
		if (info == null)
		{
			Plugin.Log.LogWarning($"Tierleinen: EpicLoot.API.{method} fehlt.");
			return null;
		}
		return info.Invoke(null, args);
	}

	private static void Register()
	{
		Func<ItemDrop.ItemData, object, string, bool, bool, bool, bool> isLeash = (item, magicItem, effectType, lootRoll, augmentRoll, runeRoll) => item != null && Plugin.IsLeash(item, out _);
		Call("RegisterMagicEffectRequirement", Requirement, isLeash);
		Func<ItemDrop.ItemData, object, string, bool, bool, bool, bool> isLeashOrWhistle = (item, magicItem, effectType, lootRoll, augmentRoll, runeRoll) => item != null && Ours(item)
			&& !(effectType != null && NotOnSaddles.Contains(effectType) && Saddles.IsSaddle(item));
		Call("RegisterMagicEffectRequirement", WhistleRequirement, isLeashOrWhistle);
		Func<ItemDrop.ItemData, object, string, bool, bool, bool, bool> isValkyrie = (item, magicItem, effectType, lootRoll, augmentRoll, runeRoll) => item != null && Whistles.IsWhistle(item, out WhistleTier tier) && tier.Valkyrie;
		Call("RegisterMagicEffectRequirement", ValkyrieRequirement, isValkyrie);
		Func<ItemDrop.ItemData, object, string, bool, bool, bool, bool> isSaddle = (item, magicItem, effectType, lootRoll, augmentRoll, runeRoll) => item != null && Saddles.IsSaddle(item);
		Call("RegisterMagicEffectRequirement", SaddleRequirement, isSaddle);
		foreach (string boss in new HashSet<string>(BossFor.Values))
		{
			// je Boss eine eigene Bedingung: Leine aus seinem Biom oder besser und Boss besiegt (EpicLoot prüft sie bei der Auswahl legendärer/mythischer Items)
			int rank = LeashRankAfter.TryGetValue(boss, out int min) ? min : 2;
			Func<ItemDrop.ItemData, object, string, bool, bool, bool, bool> afterBoss = (item, magicItem, effectType, lootRoll, augmentRoll, runeRoll) => item != null && Plugin.IsLeash(item, out Tier tier) && tier.Rank >= rank && BossDefeated(boss);
			Call("RegisterMagicEffectRequirement", BossRequirement(boss), afterBoss);
		}
		foreach (string boss in WhistleLevelAfter.Keys)
		{
			// Tierpfeifen (Gorduan, 24.10.2026): Pfeife aus dem Biom des Bosses oder besser und Boss besiegt
			int level = WhistleLevelAfter[boss];
			Func<ItemDrop.ItemData, object, string, bool, bool, bool, bool> whistleAfterBoss = (item, magicItem, effectType, lootRoll, augmentRoll, runeRoll) => item != null && Whistles.IsWhistle(item, out WhistleTier tier) && tier.Level >= level && BossDefeated(boss);
			Call("RegisterMagicEffectRequirement", WhistleBossRequirement(boss), whistleAfterBoss);
		}
		int effects = 0;
		foreach (Effect effect in Rollable)
		{
			effects += Call("AddMagicEffect", EffectJson(effect)) != null ? 1 : 0;
		}
		foreach (Effect effect in Specials)
		{
			effects += Call("AddMagicEffect", EffectJson(effect)) != null ? 1 : 0;
		}
		int uniques = 0;
		foreach ((string id, string kind, string key, (string Type, float Min, float Max)[] list) in Uniques)
		{
			string requirement = BossFor.TryGetValue(id, out string boss) ? BossRequirement(boss) : Requirement;
			uniques += Call("AddLegendaryItem", kind, UniqueJson(id, key, requirement, list)) != null ? 1 : 0;
		}
		int whistles = 0;
		foreach ((string id, string kind, string key, string boss, (string Type, float Min, float Max)[] list) in WhistleUniques)
		{
			// nur die festen Effekte, keine Zufallseffekte dazu (Gorduan: legendär ein Drittel, mythisch nur die eigenen; Prüfung 24.10.2026)
			whistles += Call("AddLegendaryItem", kind, UniqueJson(id, key, WhistleBossRequirement(boss), list, list.Length)) != null ? 1 : 0;
		}
		RegisterUniques(ref effects, ref uniques, ref whistles, out int saddles);
		Plugin.Log.LogInfo($"Tierleinen: EpicLoot erkannt, {effects} Leinen-Effekte, {uniques} einzigartige Leinen, {whistles} Pfeifen und {saddles} Sättel angelegt.");
	}

	/// <summary>Beute anmelden, sobald die Leinen-Items existieren (nur Stufen, deren Leine angelegt wurde).</summary>
	internal static void RegisterLoot()
	{
		if (!Available || lootRegistered)
		{
			return;
		}
		lootRegistered = true;
		try
		{
			string json = LootJson();
			bool loot = json.Length > 2 && Call("AddLootTables", json) != null;
			Plugin.Log.LogInfo(loot ? "Tierleinen: EpicLoot-Beute angelegt." : "Tierleinen: keine EpicLoot-Beute angelegt.");
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"Tierleinen: EpicLoot-Beute nicht angelegt: {e.Message}");
		}
	}

	private static bool lootRegistered;

	// ------------------------------------------------------------------ JSON für EpicLoot

	private static string Num(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

	private static string EffectJson(Effect effect)
	{
		string[] rarities = { "Magic", "Rare", "Epic", "Legendary", "Mythic", "Ancient" };
		StringBuilder sb = new();
		sb.Append("{\"Type\":\"").Append(effect.Type).Append("\",");
		sb.Append("\"DisplayText\":\"$gc_me_").Append(effect.Key).Append("\",");
		sb.Append("\"Description\":\"$gc_me_").Append(effect.Key).Append("_desc\",");
		bool forWhistles = !effect.NoRoll && WhistleEffects.Contains(effect.Key); // auch auf Tierpfeifen (wirkt beim Pfiff)
		sb.Append("\"Requirements\":{\"ExternalRequirements\":[\"").Append(effect.RequirementName ?? (forWhistles ? WhistleRequirement : Requirement)).Append("\"]");
		if (effect.NoRoll)
		{
			sb.Append(",\"NoRoll\":true");
		}
		if (effect.Rarities != null)
		{
			sb.Append(",\"AllowedRarities\":[");
			for (int i = 0; i < effect.Rarities.Length; i++)
			{
				sb.Append(i > 0 ? "," : "").Append('"').Append(effect.Rarities[i]).Append('"');
			}
			sb.Append(']');
		}
		sb.Append("},\"ValuesPerRarity\":{");
		for (int i = 0; i < 6; i++)
		{
			sb.Append(i > 0 ? "," : "").Append('"').Append(rarities[i]).Append("\":{\"MinValue\":").Append(Num(effect.Values[i, 0]))
				.Append(",\"MaxValue\":").Append(Num(effect.Values[i, 1])).Append(",\"Increment\":").Append(Num(effect.Increment)).Append('}');
		}
		sb.Append("},\"SelectionWeight\":").Append(Num(effect.Weight));
		bool rollable = !effect.NoRoll;
		sb.Append(",\"CanBeAugmented\":").Append(rollable ? "true" : "false");
		sb.Append(",\"CanBeDisenchanted\":true");
		// feste Werte (Unzerstörbar, Standfest, Goldborsten) nicht als Rune von besonderen Leinen auf andere übertragen
		sb.Append(",\"CanBeRunified\":").Append(rollable && effect.Increment > 0f ? "true" : "false");
		sb.Append(",\"Comment\":\"GC_Server Tierleinen\"}");
		return sb.ToString();
	}

	/// <summary>effectCount: Zahl der Effekte; -1 = so viele, wie EpicLoot für die Seltenheit würfelt (der Rest zufällig).</summary>
	private static string UniqueJson(string id, string key, string requirement, (string Type, float Min, float Max)[] list, int effectCount = -1)
	{
		StringBuilder sb = new();
		sb.Append("{\"ID\":\"").Append(id).Append("\",");
		sb.Append("\"Name\":\"$gc_leg_").Append(key).Append("\",");
		sb.Append("\"Description\":\"$gc_leg_").Append(key).Append("_desc\",");
		sb.Append("\"Requirements\":{\"ExternalRequirements\":[\"").Append(requirement).Append("\"]},");
		sb.Append("\"GuaranteedMagicEffects\":[");
		for (int i = 0; i < list.Length; i++)
		{
			(string type, float min, float max) = list[i];
			float increment = max > min ? (type == "GC_LeashRange" || max - min < 10f ? 1f : 5f) : 0f; // Leinenlänge in 1-m-Schritten
			sb.Append(i > 0 ? "," : "").Append("{\"Type\":\"").Append(type).Append("\",\"Values\":{\"MinValue\":").Append(Num(min))
				.Append(",\"MaxValue\":").Append(Num(max)).Append(",\"Increment\":").Append(Num(increment)).Append("}}");
		}
		sb.Append("],\"GuaranteedEffectCount\":").Append(effectCount).Append(",\"SelectionWeight\":1,\"IsSetItem\":false}");
		return sb.ToString();
	}

	private static string LootJson()
	{
		List<(string Object, string[] Leashes)> all = new(Loot);
		foreach ((string creature, string leash) in OwnTables)
		{
			if (!HasEpicLootTable(creature))
			{
				all.Add((creature, new[] { leash }));
			}
		}
		StringBuilder sb = new();
		sb.Append('[');
		int tables = 0;
		foreach ((string table, string[] leashes) in all)
		{
			List<string> present = new();
			foreach (string leash in leashes)
			{
				if (Exists(leash))
				{
					present.Add(leash); // nur Leinen, die es wirklich gibt (fehlt ein Material im Spiel, fehlt auch die Leine)
				}
			}
			if (present.Count == 0)
			{
				continue;
			}
			sb.Append(tables++ > 0 ? "," : "").Append("{\"Object\":\"").Append(table).Append("\",\"LeveledLoot\":[{\"Level\":1,\"Drops\":[[0,99],[1,1]],\"Loot\":[");
			// Wiesen und Schwarzwald (Gorduan, 24.10.2026): höchstens episch, legendär erst ab dem Sumpf
			string rarity = NoLegendaryLoot.Contains(table) ? "[70,22,8,0,0,0]" : "[70,22,7,1,0,0]";
			for (int i = 0; i < present.Count; i++)
			{
				sb.Append(i > 0 ? "," : "").Append("{\"Item\":\"").Append(present[i]).Append("\",\"Weight\":1,\"Rarity\":").Append(rarity).Append('}');
			}
			// EpicLoot steigert seit 0.15.0 (auch 0.15.2) Beute je Stern (DefaultStarScaling: +14,9 % Chance, bessere
			// Seltenheit); die Leinen bleiben bei 1 % für jede Kreatur, wie vorher
			sb.Append("]}],\"StarScaling\":{\"DropChance\":0,\"BonusDrops\":0,\"RarityShift\":0}}");
		}
		AppendSaddleLoot(sb, ref tables); // Sättel (0,5 % je Kreatur, LeashUniques.cs)
		sb.Append(']');
		return sb.ToString();
	}

	/// <summary>Kennt EpicLoot schon eine Beutetabelle für diese Kreatur (eigene oder Verweis auf eine Stufe)?</summary>
	private static bool HasEpicLootTable(string creature)
	{
		try
		{
			MethodInfo? get = AccessTools.Method(AccessTools.TypeByName("EpicLoot.LootRoller"), "GetLootTable", new[] { typeof(string) });
			return get == null || get.Invoke(null, new object[] { creature }) is not System.Collections.ICollection { Count: 0 };
		}
		catch (Exception)
		{
			return true; // im Zweifel keine eigene Tabelle: sie ersetzte EpicLoots Verweis
		}
	}

	private static bool Exists(string prefabName)
	{
		foreach (Tier tier in Plugin.Tiers)
		{
			if (tier.PrefabName == prefabName)
			{
				return tier.Prefab != null;
			}
		}
		return false;
	}

	// ------------------------------------------------------------------ Splitter in eigenen Items (Gorduan, 20.10.2026)

	/// <summary>
	/// Gorduan: „Alle EpicLoot-Items unserer eigenen Kreation können auch eigene Sockel oder Splitterplätze haben.“ EpicLoot gibt
	/// einem Splitter je Farbe einen Effekt nach der Art des Items (Waffe, Rüstung …); in Leinen, Pfeifen und Sätteln gibt
	/// jede Farbe stattdessen einen eigenen Effekt in derselben Richtung (Rot Leben, Orange Feuer, Dunkelblau Frost …).
	/// Runen mit eigenen Effekten passen ohnehin. Werte je Seltenheit: Mitte des Würfelbereichs des Effekts.
	/// </summary>
	private static readonly Dictionary<string, string> ShardEffects = new()
	{
		["Red"] = "GC_LeashAnimalHealth", ["Yellow"] = "GC_LeashAnimalSpeed", ["Cyan"] = "GC_LeashAnimalRegen", ["Orange"] = "GC_LeashFireBite",
		["Pink"] = "GC_LeashEvade", ["Black"] = "GC_LeashAnimalDamage", ["White"] = "GC_LeashAnimalResist", ["Green"] = "GC_LeashAnimalAttackSpeed",
		["Purple"] = "GC_LeashLifeSteal", ["Grey"] = "GC_LeashAnimalFeed", ["DarkRed"] = "GC_LeashBerserk", ["DarkGreen"] = "GC_LeashPoisonBite",
		["DarkBlue"] = "GC_LeashFrostBite", ["DarkPurple"] = "GC_LeashAnimalThorns", ["Golden"] = "GC_LeashStaggerPower", ["LightBlue"] = "GC_LeashThunder",
		["LightGreen"] = "GC_LeashDeter", ["Peach"] = "GC_LeashBondCarry",
	};

	private static readonly Dictionary<string, object?> ShardDefinitions = new();
	private static Type? shardEffectType;
	private static Type? rarityType;
	private static MethodInfo? resolveSocketed;

	private static void PatchShards(Harmony harmony)
	{
		try
		{
			Type? shards = AccessTools.TypeByName("EpicLoot.ShardStones.Shards");
			Type? manager = AccessTools.TypeByName("EpicLoot.ShardStones.ShardSocketManager");
			shardEffectType = AccessTools.TypeByName("EpicLoot.ShardStones.ShardEffectDefinition");
			rarityType = AccessTools.TypeByName("EpicLoot.ItemRarity");
			MethodInfo? getEffect = shards != null ? AccessTools.Method(shards, "GetShardEffect") : null;
			MethodInfo? canSocket = manager != null ? AccessTools.Method(manager, "CanSocket") : null;
			resolveSocketed = manager != null ? AccessTools.Method(manager, "ResolveSocketedEffect") : null;
			if (getEffect == null || canSocket == null || shardEffectType == null || rarityType == null)
			{
				Plugin.Log.LogInfo("Tierleinen: EpicLoot ohne Splitter-Sockel (ältere Fassung), eigene Items bekommen keine Splitter-Effekte.");
				return;
			}
			TryPatch(harmony, getEffect, Hooks.Shards, postfix: nameof(ShardEffectPostfix));
			TryPatch(harmony, canSocket, Hooks.Sockets, postfix: nameof(CanSocketPostfix));
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"Tierleinen: Splitter für eigene Items nicht angebunden: {e.Message}");
		}
	}

	/// <summary>Splitter in einem eigenen Item: der eigene Effekt der Farbe (oder keiner, dann lässt CanSocket ihn nicht zu).</summary>
	private static void ShardEffectPostfix(ItemDrop.ItemData item, object color, ref object? __result)
	{
		if (item == null || !Ours(item))
		{
			return;
		}
		string name = color.ToString();
		if (!ShardDefinitions.TryGetValue(name, out object? definition))
		{
			definition = MakeShardDefinition(name);
			ShardDefinitions[name] = definition;
		}
		__result = definition;
	}

	private static object? MakeShardDefinition(string color)
	{
		if (!ShardEffects.TryGetValue(color, out string type) || shardEffectType == null || rarityType == null)
		{
			return null;
		}
		Effect? effect = null;
		foreach (Effect candidate in Rollable)
		{
			if (candidate.Type == type)
			{
				effect = candidate;
			}
		}
		if (effect == null)
		{
			return null;
		}
		object definition = Activator.CreateInstance(shardEffectType);
		AccessTools.Field(shardEffectType, "EffectType").SetValue(definition, type);
		if (AccessTools.Field(shardEffectType, "ValuesPerRarity").GetValue(definition) is System.Collections.IDictionary values)
		{
			string[] rarities = { "Magic", "Rare", "Epic", "Legendary", "Mythic", "Ancient" };
			for (int i = 0; i < rarities.Length; i++)
			{
				if (Enum.IsDefined(rarityType, rarities[i]))
				{
					float value = effect.Increment > 0f ? (float)Math.Round((effect.Values[i, 0] + effect.Values[i, 1]) * 0.5f / effect.Increment) * effect.Increment : effect.Values[i, 1];
					values[Enum.Parse(rarityType, rarities[i])] = value;
				}
			}
		}
		return definition;
	}

	/// <summary>
	/// Ein Splitter ohne eigenen Effekt (besondere und Boss-Splitter) passt nicht in eigene Items. Seit EpicLoot 0.15.2 lehnt
	/// EpicLoot selbst solche Splitter ab (ShardSocketManager.ResolvePlacementEffect: „shardunsupported“); der Haken bleibt als
	/// Sicherung für ältere Fassungen, schadet aber nicht (Review 27.10.2026).
	/// </summary>
	private static void CanSocketPostfix(ItemDrop.ItemData equipment, ItemDrop.ItemData input, ref bool __result)
	{
		if (!__result || equipment == null || input == null || !Ours(equipment) || resolveSocketed == null)
		{
			return;
		}
		object?[] args = { equipment, input, null, null, null };
		if (resolveSocketed.Invoke(null, args) is bool ok && ok && args[2] == null)
		{
			__result = false;
		}
	}

	// ------------------------------------------------------------------ Seltenheits-Sterne (seit EpicLoot 0.15.0, so auch 0.15.2)

	private static FieldInfo? anchorField;
	private static FieldInfo? rungsField;
	private static FieldInfo? allDefinitions;
	private static FieldInfo? selectionWeight;
	private static PropertyInfo? definitionType;
	private static MethodInfo? canRoll;
	private static bool flareReported;

	/// <summary>
	/// Eigene Sternstaffel (Gorduan, 30.10.2026, Antwort A): ★ die zehn seltenen Effekte, ★★ Standfest, ★★★ Wiederkehr und
	/// Unzerstörbar (auch das alte der Walkürenpfeife). Alle anderen GC-Effekte tragen keine Sterne.
	/// </summary>
	private static readonly Dictionary<string, int> OwnStars = new()
	{
		["GC_LeashFireBite"] = 1, ["GC_LeashFrostBite"] = 1, ["GC_LeashThunder"] = 1, ["GC_LeashPoisonBite"] = 1, ["GC_LeashSpiritBite"] = 1,
		["GC_LeashLifeSteal"] = 1, ["GC_LeashEvade"] = 1, ["GC_LeashDeter"] = 1, ["GC_LeashBerserk"] = 1, ["GC_LeashGlow"] = 1,
		["GC_LeashSteadfast"] = 2,
		["GC_LeashRevive"] = 3, ["GC_LeashUnbreakable"] = 3, [ValkyrieUnbreakable] = 3,
	};

	/// <summary>
	/// Seit EpicLoot 0.15.0 (unverändert bis 0.15.4, MagicEffectRarity) tragen seltene Effekte ★ bis ★★★: die drei kleinsten
	/// Auswahlgewichte unter dem Median aller würfelbaren Effekte. Unsere Effekte würfeln nur an eigenen Items; zählten sie
	/// mit, nähmen ihre kleinen Gewichte (Unzerstörbar 0,05, Walküre 0,03, Wiederkehr 0,1) EpicLoots seltenen Effekten die Sterne. Darum zählt EpicLoots
	/// Rechnung nur EpicLoots Effekte; unsere (GC_…) tragen die feste eigene Staffel OwnStars (Gorduan, 30.10.2026).
	/// </summary>
	private static void PatchFlare(Harmony harmony)
	{
		MethodInfo? ensure;
		MethodInfo? getTier;
		try
		{
			Type? flare = AccessTools.TypeByName("EpicLoot.MagicEffectRarity");
			if (flare == null)
			{
				return; // EpicLoot vor 0.15.0: keine Sterne
			}
			Type? definition = AccessTools.TypeByName("EpicLoot.MagicItemEffectDefinition");
			Type? definitions = AccessTools.TypeByName("EpicLoot.MagicItemEffectDefinitions");
			ensure = AccessTools.Method(flare, "EnsureAnchor");
			getTier = definition != null ? AccessTools.Method(flare, "GetTier", new[] { definition }) : null;
			canRoll = AccessTools.Method(flare, "CanRoll");
			anchorField = AccessTools.Field(flare, "_anchorWeight");
			rungsField = AccessTools.Field(flare, "_rungsBelowAnchor");
			allDefinitions = definitions != null ? AccessTools.Field(definitions, "AllDefinitions") : null;
			selectionWeight = definition != null ? AccessTools.Field(definition, "SelectionWeight") : null;
			definitionType = definition != null ? AccessTools.Property(definition, "Type") : null;
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"Tierleinen: EpicLoots Seltenheits-Sterne nicht gefunden ({e.GetBaseException().Message}); sie zählen die Leinen-Effekte mit.");
			return;
		}
		if (ensure == null || getTier == null || canRoll == null || anchorField == null || rungsField == null || allDefinitions == null || selectionWeight == null || definitionType == null)
		{
			Plugin.Log.LogWarning("Tierleinen: EpicLoots Seltenheits-Sterne nicht gefunden; sie zählen die Leinen-Effekte mit.");
			return;
		}
		// getrennte Sterne nur mit beiden Haken; fehlt einer, rechnet EpicLoot an dieser Stelle selbst (keine Fehler)
		bool anchors = TryPatch(harmony, ensure, Hooks.None, prefix: nameof(EnsureAnchorPrefix));
		bool tiers = TryPatch(harmony, getTier, Hooks.None, prefix: nameof(GetTierPrefix));
		if (anchors && tiers)
		{
			Active |= Hooks.Flare;
		}
	}

	private static bool IsOwnEffect(object definition) => definitionType!.GetValue(definition) is string type && type.StartsWith("GC_", StringComparison.Ordinal);

	/// <summary>Median und Stufen darunter nur aus EpicLoots Effekten, in EpicLoots Feldern.</summary>
	private static void Anchors()
	{
		List<float> theirs = new();
		if (allDefinitions!.GetValue(null) is System.Collections.IDictionary all)
		{
			foreach (object? definition in all.Values)
			{
				if (definition == null || IsOwnEffect(definition) || canRoll!.Invoke(null, new[] { definition }) is not true || selectionWeight!.GetValue(definition) is not float weight || weight <= 0f)
				{
					continue;
				}
				theirs.Add(weight);
			}
		}
		(float anchor, float[] rungs) = Anchor(theirs);
		rungsField!.SetValue(null, rungs);
		anchorField!.SetValue(null, anchor);
	}

	private static (float Anchor, float[] Rungs) Anchor(List<float> weights)
	{
		weights.Sort();
		float anchor = weights.Count > 0 ? weights[weights.Count / 2] : 0f;
		List<float> rungs = new();
		foreach (float weight in weights)
		{
			if (weight < anchor && (rungs.Count == 0 || rungs[rungs.Count - 1] != weight))
			{
				rungs.Add(weight);
			}
		}
		return (anchor, rungs.ToArray());
	}

	private static bool AnchorsStale() => anchorField!.GetValue(null) is float anchor && float.IsNaN(anchor);

	/// <summary>Statt EpicLoots EnsureAnchor (alle Effekte): nur EpicLoots Effekte. Bei einem Fehler EpicLoots Rechnung.</summary>
	private static bool EnsureAnchorPrefix()
	{
		try
		{
			if (AnchorsStale())
			{
				Anchors();
			}
			return false;
		}
		catch (Exception e)
		{
			ReportFlare(e);
			return true;
		}
	}

	/// <summary>Sterne unserer Effekte: die feste eigene Staffel (OwnStars), unabhängig von Gewicht und Seltenheit.</summary>
	private static bool GetTierPrefix(object effectDef, ref int __result)
	{
		if (effectDef == null || !IsOwnEffect(effectDef))
		{
			return true; // EpicLoots Effekte: EpicLoots Rechnung, mit dem Median ohne unsere
		}
		__result = definitionType!.GetValue(effectDef) is string type && OwnStars.TryGetValue(type, out int stars) ? stars : 0;
		return false;
	}

	private static void ReportFlare(Exception e)
	{
		if (!flareReported)
		{
			flareReported = true;
			Plugin.Log.LogWarning($"Tierleinen: Seltenheits-Sterne mit EpicLoots eigener Rechnung: {e.GetBaseException().Message}");
		}
	}

	// ------------------------------------------------------------------ Lesen

	/// <summary>EpicLoots gespeicherte Verzauberung einer Leine (derselbe Text, bis EpicLoot sie ändert), sonst null.</summary>
	internal static string? JsonOf(ItemDrop.ItemData item)
	{
		return Available && item.m_customData != null && item.m_customData.TryGetValue(MagicKey, out string json) && !string.IsNullOrEmpty(json) ? json : null;
	}

	/// <summary>Effekte aus EpicLoots gespeichertem Text (Sattel im Tier-ZDO), gecacht wie Read.</summary>
	internal static BondStats ReadJson(string json) => ByReference.GetValue(json, ReadCallback);

	/// <summary>Effekte einer Leine aus EpicLoots Item-Daten (gecacht nach Text-Objekt, dann nach Inhalt).</summary>
	internal static BondStats Read(ItemDrop.ItemData item)
	{
		if (item.m_customData == null || !item.m_customData.TryGetValue(MagicKey, out string json) || string.IsNullOrEmpty(json))
		{
			return BondStats.None;
		}
		return ByReference.GetValue(json, ReadCallback); // dasselbe Text-Objekt (z. B. bei jeder Haltbarkeits-Abfrage): sofort
	}

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, BondStats> ByReference = new();
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, BondStats>.CreateValueCallback ReadCallback = ReadText;

	private static BondStats ReadText(string json)
	{
		if (Cache.TryGetValue(json, out BondStats cached))
		{
			return cached;
		}
		BondStats stats = new();
		foreach (Match match in EffectObject.Matches(json))
		{
			Match type = EffectType.Match(match.Value);
			Match value = EffectValue.Match(match.Value);
			if (!type.Success || !value.Success || !float.TryParse(value.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
			{
				continue;
			}
			Apply(stats, type.Groups["v"].Value, v);
		}
		if (Cache.Count > 512)
		{
			Cache.Clear();
		}
		Cache[json] = stats;
		return stats;
	}

	private static void Apply(BondStats s, string type, float v)
	{
		switch (type)
		{
			case "GC_LeashRange": s.Range += v; break;
			case "GC_LeashDurability": s.Durability += v; break;
			case "GC_LeashUnbreakable": s.Unbreakable = true; break;
			case "GC_LeashLoadReduction": s.LoadReduction += (int)Math.Round(v); break;
			case "GC_LeashGentle": s.Gentle += v; break;
			case "GC_LeashPull": s.Pull += v; break;
			case "GC_LeashTamer": s.Tamer += v; break;
			case "GC_LeashObedience": s.Obedience = Math.Min(100f, s.Obedience + v); break;
			case "GC_LeashAnimalDamage": s.Damage += v; break;
			case "GC_LeashAnimalAttackSpeed": s.AttackSpeed += v; break;
			case "GC_LeashAnimalHealth": s.Health += v; break;
			case "GC_LeashAnimalSpeed": s.Speed += v; break;
			case "GC_LeashAnimalRegen": s.Regen += v; break;
			case "GC_LeashAnimalResist": s.Resist += v; break;
			case "GC_LeashAnimalThorns": s.Thorns += v; break;
			case "GC_LeashAnimalFeed": s.Feed += v; break;
			case "GC_LeashFrostBite": s.Frost += v; break;
			case "GC_LeashThunder": s.Lightning += v; break;
			case "GC_LeashBerserk": s.Berserk += v; break;
			case "GC_LeashGlow": s.Light = Math.Max(s.Light, v); break;
			case "GC_LeashRevive": s.Revive = s.Revive > 0f ? Math.Min(s.Revive, v) : v; break; // kürzeste Ohnmacht gilt
			case "GC_LeashBondCarry": s.HolderCarry += v; break;
			case "GC_LeashBondSpeed": s.HolderSpeed += v; break;
			case "GC_LeashBondRegen": s.HolderRegen += v; break;
			case "GC_LeashBondReveal": s.HolderReveal = Math.Max(s.HolderReveal, v); break;
			case "GC_LeashFireBite": s.Fire += v; break;
			case "GC_LeashLifeSteal": s.LifeSteal += v; break;
			case "GC_LeashDeter": s.Deter += v; break;
			case "GC_LeashStaggerPower": s.StaggerPower += v; break;
			case "GC_LeashSpiritBite": s.Spirit += v; break;
			case "GC_LeashPoisonBite": s.Poison += v; break;
			case "GC_LeashPoisonImmune": s.PoisonImmune = true; break;
			case "GC_LeashSteadfast": s.Steadfast = true; break;
			case "GC_LeashFearless": s.Fearless = true; break;
			case "GC_LeashEvade": s.Evade += v; break;
			case "GC_LeashSwim": s.Swim += v; break;
			case "GC_LeashYouth": s.Youth = s.Youth > 0f ? Math.Min(s.Youth, v) : v; break; // kürzeste Wartezeit gilt
			case "GC_LeashIllusions": s.Illusions = Math.Min(2, Math.Max(s.Illusions, (int)Math.Round(v))); break; // Gorduan: höchstens 2 Kopien
			case "GC_LeashBondHunt": s.HolderHunt += v; break;
			case "GC_LeashBondWatch": s.HolderWatch = Math.Max(s.HolderWatch, v); break;
			case "GC_LeashBondStealth": s.HolderStealth = Math.Max(s.HolderStealth, v); break;
			case "GC_SaddleStamina": s.MountStamina += v; break;
			case "GC_SaddleStaminaRegen": s.MountStaminaRegen += v; break;
			case "GC_SaddleStaminaSave": s.MountStaminaSave = Math.Min(90f, s.MountStaminaSave + v); break;
			case "GC_LeashWhistleFireproof": s.Fireproof = true; break;
			case "GC_LeashWhistleBlaze": s.Blaze += v; break;
			case "GC_LeashWhistleHall": s.Hall = Math.Max(s.Hall, v); break;
			case "GC_LeashWhistleRally": s.Rally += v; break;
			default: ApplyUnique(s, type, v); break; // neue einzigartige Stücke (LeashUniques.cs)
		}
	}
}
