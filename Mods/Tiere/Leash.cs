using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Server;
using GC.Shared;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace GC.Server.Leash;

/// <summary>
/// Eine Leine: Haut des Tiers, ab der Wolf-Leine Barren, Werkbank-Stufe und Farbe. Haltbarkeit = Rang mal
/// DurabilityPerLeashTier.
/// </summary>
internal sealed class Tier
{
	/// <summary>Feste Nummer der Leine (steht in Tieren und Knoten); seit es eine Leine je Biom gibt nicht mehr die Stärke.</summary>
	public int Level;
	/// <summary>Stärke: 1 = schwächste Leine (Leinenbonus, Haltbarkeit).</summary>
	public int Rank;
	public string Material = "";
	public int Amount;
	public string Bar = "";
	public int BarAmount;
	public int Station;
	public float MinLength; // Mindestlänge in m (so nah hält sich das Tier, wenn der Halter läuft), Qualität 1
	public float MaxLength; // Höchstlänge in m (darüber gedehnt und Verschleiß, ab +15 % zieht die Leine), Qualität 1
	public float MaxLength4; // Höchstlänge auf Qualität 4; dazwischen gleichmäßig
	public float Durability = 1f; // Haltbarkeit in Vielfachen von DurabilityPerLeashTier (Qualität 1)
	public Color Color;
	public ItemDrop? Prefab;
	public CustomItem? Item;

	public string PrefabName => "GC_Leash_" + Material;

	/// <summary>Hash des Prefab-Namens (so steht das Item in der Hand in der Ausrüstung jedes Spielers).</summary>
	public int PrefabHash => prefabHash != 0 ? prefabHash : prefabHash = PrefabName.GetStableHashCode();
	private int prefabHash;
}

/// <summary>
/// GC_Leash (Teil von GC_Server): Tierleinen, ähnlich wie in Minecraft.
///
/// Eine Leine ist ein Item (ein Inventarplatz, nicht stapelbar). Bedienung (Gorduan, 13.10.2026, zweite Fassung): Man nimmt
/// die Leine mit ihrer Taste 1–8 in die Hand wie eine Waffe, sie bleibt ausgewählt. E an einem gezähmten Tier leint es
/// an, E am angeleinten Tier löst die Leine, E an einem Pfosten bindet das Tier dort an. Mit der Leine in der Hand
/// schickt Linksklick das Tier an den angezielten Ort, Rechtsklick lässt es das anvisierte Ziel angreifen (Control). Um
/// ein anderes Tier zu steuern, nimmt man dessen Leine in die Hand. Pro Leine ein Tier; aktive Leinen liegen in der
/// Schnellleiste. Bei Teleports des Halters kommt das Tier mit: durch Portale und in Dungeons.
///
/// Leinenbonus (Gorduan): Angeleinte und angebundene Tiere haben mehr Leben und machen mehr Schaden, von +10 % (kleinste
/// Leine) bis +120 % (größte Leine auf Qualität 4, seit der Regel ×1,25 vom 27.10.2026); Aufwerten erhöht ihn. Angewendet
/// zusammen mit der Tierpflege (Care).
///
/// Längen und Zonen (Gorduan, 13.10.2026): Jede Stufe hat eine Mindestlänge (2 bis 10 m) und eine Höchstlänge (5 bis 20 m),
/// Aufwerten verlängert beide leicht.
///  * Läuft der Halter, kommt das Tier auf die Mindestlänge heran und hält sie (Behaviour). Steht er, bewegt es sich frei.
///  * Bis 90 % der Höchstlänge wirkt die Leine nicht. Bis zur Höchstlänge ist sie straff und führt sanft.
///  * Bis 15 % über die Höchstlänge ist sie gedehnt und verliert je Sekunde (CheckInterval) so viel Haltbarkeit, wie das
///    Tier Leinenlast hat. Kämpfen in Reichweite kostet nichts.
///  * Darüber zieht die Leine das Tier mit (der Halter ist dabei halb so schnell) und verschleißt weiter, bis sie reißt:
///    Das Tier ist entwischt, läuft frei herum und gehorcht erst wieder, wenn es mit einer anderen oder reparierten Leine
///    eingefangen wird. Die Leine bleibt im Inventar und lässt sich an der Werkbank reparieren.
///  * Kann die Leine das Tier nicht nachziehen (tiefe Kante), rutscht sie 12 m hinter der Zuggrenze aus der Hand.
///
/// Leinenlast (siehe Ranks): Je größer das Tier und je später es ins Spiel kommt, desto höher, immer um 1 pro Stufe
/// (Wildschwein 1 … Elch 10), auch für Tiere, die erst andere Mods zähmbar machen.
///
/// Eine Leine je Biom (Gorduan, 19.10.2026: „behalte immer die Teuerste pro Biom“), jede Stufe +30 Haltbarkeit (die stärkste
/// etwa doppelt so haltbar wie vorher): Hirschleder (Wiesen), Trollleder (Schwarzwald; Gorduan: dort die Haut des Tiers, das
/// leichter zu besiegen ist, der Troll statt des Bjørn), Wolfspelz (+2 Eisen, Berge), Lox-Pelz (+2 Schwarzmetall, Ebenen),
/// Schuppenhaut (+2 Schwarzmetall, Nebellande, ohne eigenen Barren), Asksvin-Haut (+2 Flammenmetall, Aschlande),
/// Elchhaut (+2 Blutgold, Tiefer Norden). Der Sumpf hat keine Haut. Die Leinen aus Lederfetzen, Bjørn-Haut und Robbenhaut
/// gibt es nicht mehr: Vorhandene werden beim Laden zur Leine ihres Bioms (RetiredLeashPatch), angeleinte Tiere und
/// Knoten zählen ebenso (TierAt). Jede Leine hält jedes Tier.
///
/// Mehrspieler: Der Halter übernimmt das Tier (ZDO-Besitz wie beim Reiten), damit Folgen und Ziehen bei ihm laufen. Im
/// Tier stehen die Sitzung des Halters (UserID seiner Spielfigur, neu bei jeder Anmeldung) und die Stufe, damit alle
/// das Seil sehen. Ist der Halter nicht mehr da, gilt die Leine für alle als gelöst, der Besitzer räumt sie auf.
/// </summary>
internal static class Plugin
{
	internal const string KeyHolder = "gc_leash_holder";
	internal const string KeyTier = "gc_leash_tier";
	internal const string KeyItemId = "gc_leash_id";
	internal static readonly int HolderHash = KeyHolder.GetStableHashCode();
	internal static readonly int TierHash = KeyTier.GetStableHashCode();

	// Am Pfosten angebunden: Die Leine steckt dann im Tier (Stufe, Haltbarkeit, ID), der Pfosten wird über seine Position gefunden
	internal static readonly int TiedHash = "gc_leash_tied".GetStableHashCode();
	internal static readonly int PostHash = "gc_leash_post".GetStableHashCode();
	internal static readonly int DurabilityHash = "gc_leash_dura".GetStableHashCode();
	internal static readonly int LeashIdHash = "gc_leash_lid".GetStableHashCode();

	/// <summary>Im Tier: Die Leine ist gerissen, es läuft frei herum und gehorcht erst wieder an einer Leine (Gorduan).</summary>
	internal static readonly int EscapedHash = "gc_leash_escaped".GetStableHashCode();

	/// <summary>Im Tier: Leinenbonus (Anteil, 0,1 = +10 % Leben und Schaden) der Leine, an der es hängt oder angebunden ist.</summary>
	internal static readonly int BonusHash = "gc_leash_bonus".GetStableHashCode();

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<float> SlipDistance = null!;
	internal static ConfigEntry<float> CheckInterval = null!;
	internal static ConfigEntry<int> DurabilityStep = null!;
	internal static ConfigEntry<bool> PortalTravel = null!;
	internal static ConfigEntry<float> ChaseDistance = null!;
	internal static ConfigEntry<int> CommandMinLevel = null!;
	internal static ConfigEntry<float> CommandCooldown = null!;
	internal static ConfigEntry<float> IllusionCooldown = null!;
	internal static ConfigEntry<string> AnimalRules = null!;
	internal static ConfigEntry<string> PostPieces = null!;
	internal static ConfigEntry<bool> WhistlesEnabled = null!;
	internal static bool On => Enabled.Value;

	/// <summary>Abstand, den Lokis Trugbilder zum Halter halten (sie hängen an keiner Leine).</summary>
	internal const float IllusionFollowLength = 2f;

	/// <summary>
	/// Umkreis, in dem für einen Knoten alles geladen sein muss, bevor ein fehlendes Tier oder Bauteil zählt: so weit, wie
	/// die längste angebundene Leine das Tier lässt (Zuggrenze), plus Rand.
	/// </summary>
	internal static float KnotCheckRadius => DragFrom(LongestMaxLength) + 2f;

	// Werkbank höchstens Stufe 4: Valheim repariert nur bis Stufe 4 (InventoryGui.CanRepair)
	// Längen (Gorduan, 13.10.2026, zweite Fassung): Mindestlänge 2 m (kleinste Leine) bis 10 m (größte), Höchstlänge 5 m bis
	// 20 m; Aufwerten verlängert nur wenig (+0,2 m / +0,5 m je Qualität), deshalb erreicht erst die Elch-Leine auf
	// Qualität 4 genau 10 m und 20 m. Seit es eine Leine je Biom gibt (Gorduan, 19.10.2026, sieben Leinen): Höchstlänge in
	// gleichen Schritten von 2,25 m, Mindestlänge gleichmäßig auf 0,1 m gerundet. Level bleibt die alte Nummer (gespeichert).
	// Gorduan, 27.10.2026: „von der kleinsten Leine an min. ×1,25 pro Leine bei Haltbarkeit, Reichweite etc., aktuelle Werte
	// niemals reduziert“: Höchstlänge auf Qualität 1 und 4 je für sich (×1,25 zur vorigen Leine, auf 0,25 m aufgerundet),
	// Haltbarkeit Asksvin 6⅓ und Elch 8 statt 6 und 7 Stufen. Die Mindestlänge bleibt: Sie ist nur der Folgeabstand.
	internal static readonly Tier[] Tiers =
	{
		new() { Level = 2, Rank = 1, Material = "DeerHide", Amount = 5, Station = 1, MinLength = 2.0f, MaxLength = 5.0f, MaxLength4 = 6.5f, Durability = 1f, Color = new Color(0.72f, 0.54f, 0.34f) },
		new() { Level = 3, Rank = 2, Material = "TrollHide", Amount = 4, Station = 2, MinLength = 3.2f, MaxLength = 7.25f, MaxLength4 = 8.75f, Durability = 2f, Color = new Color(0.40f, 0.50f, 0.62f) },
		new() { Level = 5, Rank = 3, Material = "WolfPelt", Amount = 4, Bar = "Iron", BarAmount = 2, Station = 3, MinLength = 4.5f, MaxLength = 9.5f, MaxLength4 = 11.0f, Durability = 3f, Color = new Color(0.86f, 0.86f, 0.90f) },
		new() { Level = 6, Rank = 4, Material = "LoxPelt", Amount = 3, Bar = "BlackMetal", BarAmount = 2, Station = 3, MinLength = 5.7f, MaxLength = 12.0f, MaxLength4 = 13.75f, Durability = 4f, Color = new Color(0.64f, 0.50f, 0.32f) },
		new() { Level = 7, Rank = 5, Material = "ScaleHide", Amount = 4, Bar = "BlackMetal", BarAmount = 2, Station = 4, MinLength = 6.9f, MaxLength = 15.0f, MaxLength4 = 17.25f, Durability = 5f, Color = new Color(0.45f, 0.64f, 0.56f) },
		new() { Level = 8, Rank = 6, Material = "AskHide", Amount = 4, Bar = "FlametalNew", BarAmount = 2, Station = 4, MinLength = 8.2f, MaxLength = 18.75f, MaxLength4 = 21.75f, Durability = 19f / 3f, Color = new Color(0.66f, 0.30f, 0.24f) },
		new() { Level = 10, Rank = 7, Material = "MooseHide", Amount = 4, Bar = "Gold", BarAmount = 2, Station = 4, MinLength = 9.4f, MaxLength = 23.5f, MaxLength4 = 27.25f, Durability = 8f, Color = new Color(0.50f, 0.38f, 0.26f) },
	};

	/// <summary>
	/// Entfallene Leinen (Gorduan, 19.10.2026: eine je Biom) → die Leine ihres Bioms: Lederfetzen → Hirschleder (Wiesen),
	/// Bjørn-Haut → Trollleder (Schwarzwald), Robbenhaut → Elchhaut (Tiefer Norden). Nummer und Prefab.
	/// </summary>
	private static readonly Dictionary<int, int> RetiredLevels = new() { [1] = 2, [4] = 3, [9] = 10 };
	internal static readonly Dictionary<string, string> RetiredPrefabs = new()
	{
		["GC_Leash_LeatherScraps"] = "GC_Leash_DeerHide",
		["GC_Leash_BjornHide"] = "GC_Leash_TrollHide",
		["GC_Leash_SealHide"] = "GC_Leash_MooseHide",
	};

	/// <summary>Aufwerten verlängert nur wenig: Mindestlänge +0,2 m je Qualitätsstufe (Höchstlänge: Tier.MaxLength4).</summary>
	internal const float MinLengthPerQuality = 0.2f;

	/// <summary>Gorduan: Bis 90 % der Höchstlänge bewegt sich das Tier frei, die Leine wirkt nicht.</summary>
	internal const float FreePart = 0.9f;

	/// <summary>Gorduan: Bis 15 % über die Höchstlänge lässt sich die Leine dehnen (verschleißt); darüber zieht sie das Tier mit.</summary>
	internal const float StretchPart = 1.15f;

	/// <summary>Bis hier frei (90 % der Höchstlänge).</summary>
	internal static float FreeUntil(float maxLength) => maxLength * FreePart;

	/// <summary>Ab hier zieht die Leine das Tier mit (Höchstlänge + 15 %), verschleißt weiter, bis sie reißt.</summary>
	internal static float DragFrom(float maxLength) => maxLength * StretchPart;

	/// <summary>
	/// Mindestlänge einer Leine dieser Stufe und Qualität (so nah hält sich das Tier, wenn der Halter läuft). Die Qualität
	/// ist auf 1 bis 4 begrenzt: Die Werkbank fragt beim Aufwerten nach Qualität + 1, und fremde Items dürfen nicht über
	/// die längste Leine (20 m) hinaus.
	/// </summary>
	internal static float MinLength(Tier tier, int quality) => tier.MinLength + MinLengthPerQuality * (ClampQuality(quality) - 1);

	/// <summary>
	/// Höchstlänge einer Leine dieser Stufe und Qualität (darüber zieht der Halter, die Leine verschleißt): von Qualität 1
	/// bis 4 gleichmäßig. Erfüllen beide Enden die ×1,25-Regel, erfüllen sie auch die Stufen dazwischen.
	/// </summary>
	internal static float MaxLength(Tier tier, int quality) => Mathf.Max(MinLength(tier, quality) + 1f, Mathf.Lerp(tier.MaxLength, tier.MaxLength4, (ClampQuality(quality) - 1) / (float)(MaxQuality - 1)));

	/// <summary>In der Leine: eingestellte Mindestlänge in m (Umschalt + Mausrad), fehlt = normale Mindestlänge.</summary>
	internal const string KeyLength = "gc_leash_len";

	/// <summary>Gorduan: Kürzer als 1 m lässt sich keine Leine einstellen.</summary>
	internal const float ShortestLength = 1f;

	/// <summary>So viel ändert ein Schritt am Mausrad.</summary>
	internal const float LengthStep = 0.5f;

	/// <summary>
	/// Eingestellte Mindestlänge einer Leine (Gorduan: mit Umschalt + Mausrad kürzer, höchstens zurück auf die normale
	/// Mindestlänge ihrer Stufe und Qualität, nie unter 1 m). Die Leine merkt sie sich, auch am Pfosten.
	/// </summary>
	internal static float SetLength(Tier tier, ItemDrop.ItemData? item, int quality)
	{
		float normal = MinLength(tier, quality);
		return item?.m_customData != null && item.m_customData.TryGetValue(KeyLength, out string text)
			&& float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float set)
			? Mathf.Clamp(set, ShortestLength, normal) : normal;
	}

	/// <summary>
	/// Anteil der eingestellten an der normalen Mindestlänge. Höchstlänge und alle Bereiche (frei, gedehnt, Mitziehen)
	/// schrumpfen im selben Verhältnis (Gorduan: „die max. Reichweite wird prozentual angeglichen“).
	/// </summary>
	internal static float LengthRatio(Tier tier, ItemDrop.ItemData? item, int quality) => SetLength(tier, item, quality) / MinLength(tier, quality);

	/// <summary>Wie weit die Leine von 1 m (0) bis zur normalen Mindestlänge (1) eingestellt ist.</summary>
	private static float LengthShare(Tier tier, ItemDrop.ItemData? item, int quality)
	{
		float normal = MinLength(tier, quality);
		return normal > ShortestLength ? Mathf.Clamp01((SetLength(tier, item, quality) - ShortestLength) / (normal - ShortestLength)) : 1f;
	}

	/// <summary>
	/// Gorduan (15.10.2026): Verschleiß beim Zerren nach eingestellter Länge. Auf 1 m gekürzt +50 %, volle Länge −10 %,
	/// dazwischen gleichmäßig. Gilt in der Hand, am Pfosten zerrt die Leine nicht.
	/// </summary>
	internal static float WearFactor(Tier tier, ItemDrop.ItemData? item, int quality) => WearFactorAt(tier, SetLength(tier, item, quality), quality);

	/// <summary>Verschleiß-Faktor für eine bestimmte Mindestlänge (auch die nach einem Befehl nachgegebene).</summary>
	internal static float WearFactorAt(Tier tier, float minLength, int quality)
	{
		float normal = MinLength(tier, quality);
		float share = normal > ShortestLength ? Mathf.Clamp01((minLength - ShortestLength) / (normal - ShortestLength)) : 1f;
		return Mathf.Lerp(1.5f, 0.9f, share);
	}

	/// <summary>
	/// Gorduan: Am Pfosten nimmt eine gekürzte Leine bis zu 50 % weniger Schaden (1 m: halber Schaden, dann gleichmäßig weniger
	/// Abzug); ungekürzt bleibt alles wie bisher. Seit 18.10.2026 kommt das Viertel aus KnotDamagePatch.Share dazu.
	/// </summary>
	internal static float KnotDamageFactor(Tier tier, ItemDrop.ItemData? item, int quality) => Mathf.Lerp(0.5f, 1f, LengthShare(tier, item, quality));

	private static int ClampQuality(int quality) => Mathf.Clamp(quality, 1, MaxQuality);

	/// <summary>
	/// Leinenbonus für Leben und Schaden des Tiers (Gorduan): ab +10 % bei der kleinsten Leine, je Leine +12,9 % (90 % ÷ sieben
	/// Leinen), je Aufwertung ein Drittel davon. Seit 27.10.2026 mit Gorduans ×1,25-Regel (BonusQ1/BonusQ4): Asksvin-Haut
	/// +76,8 %, Elchhaut +96 % (Qualität 4: +120 %); dazwischen gleichmäßig.
	/// </summary>
	internal static float LeashBonus(Tier tier, int quality)
	{
		int rank = Mathf.Clamp(tier.Rank, 1, Tiers.Length) - 1;
		return Mathf.Lerp(BonusQ1[rank], BonusQ4[rank], (ClampQuality(quality) - 1) / (float)(MaxQuality - 1));
	}

	private static readonly float[] BonusQ1 = BonusChain(0);
	private static readonly float[] BonusQ4 = BonusChain(MaxQuality - 1);

	/// <summary>Bonus je Stärke für eine Qualität: der bisherige Wert, mindestens das 1,25-Fache der vorigen Leine (Gorduan, 27.10.2026).</summary>
	private static float[] BonusChain(int upgrades)
	{
		float step = 0.90f / Tiers.Length;
		float[] chain = new float[Tiers.Length];
		for (int i = 0; i < chain.Length; i++)
		{
			float old = 0.10f + step * i + step / 3f * upgrades;
			chain[i] = i == 0 ? old : Mathf.Max(old, chain[i - 1] * 1.25f);
		}
		return chain;
	}

	/// <summary>
	/// Ab hier rutscht die Leine aus der Hand (Sicherung, wenn sie das Tier nicht nachziehen kann, etwa über eine Kante):
	/// SlipDistance (Standard 12 m) hinter der Zuggrenze.
	/// </summary>
	internal static float SlipFrom(float maxLength) => DragFrom(maxLength) + SlipDistance.Value;

	/// <summary>Größte Höchstlänge einer gewöhnlichen Leine (Elch-Leine, Qualität 4: 27,25 m), für Prüf-Umkreise.</summary>
	internal static readonly float LongestMaxLength = Tiers.Max(t => t.MaxLength4);

	/// <summary>Im Tier: Mindest- und Höchstlänge der Leine, an der es hängt (aktiv oder angebunden), damit alle Spieler und der Besitzer sie kennen.</summary>
	internal static readonly int MinLengthHash = "gc_leash_min".GetStableHashCode();
	internal static readonly int MaxLengthHash = "gc_leash_max".GetStableHashCode();

	private static readonly Dictionary<string, Tier> ByItemName = new();

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ServerPlugin.Log;
		const string section = "7 - Leash";
		Enabled = Gc.BindSynced(Config, section, "Enabled", true, "Tierleinen: Leine mit 1–8 in die Hand nehmen, E am gezähmten Tier leint an oder löst, E am Pfosten bindet an; Linksklick schickt das Tier hin, Rechtsklick lässt es angreifen (wird vom Server vorgegeben, auch im Menü GC Option).");
		SlipDistance = Gc.BindSynced(Config, section, "SlipDistance", 12f, "Sicherung: So viele Meter hinter der Zuggrenze (Höchstlänge + 15 %) rutscht die Leine aus der Hand, das Tier ist frei (wie in Minecraft). Das passiert nur, wenn die Leine das Tier nicht nachziehen kann, etwa über eine tiefe Kante.", new AcceptableValueRange<float>(5f, 50f));
		CheckInterval = Gc.BindSynced(Config, section, "CheckInterval", 1f, "Je so viele Sekunden, die die Leine über ihre Höchstlänge gedehnt ist, verliert sie so viel Haltbarkeit, wie das Tier Leinenlast hat (gezählt wird die Zeit, nicht einzelne Stichproben). Kämpfen in Reichweite kostet nichts.", new AcceptableValueRange<float>(0.25f, 10f));
		// Gorduan, 19.10.2026: eine Leine je Biom, die stärkste etwa doppelt so haltbar. Neuer Schlüssel, damit der neue Wert auch dort
		// gilt, wo „DurabilityPerTier = 10“ schon in der Config steht
		DurabilityStep = Gc.BindSynced(Config, section, "DurabilityPerLeashTier", 30, "Haltbarkeit pro Leinenstufe: Hirschleder 30, Trollleder 60, Wolfspelz 90 … Asksvin-Haut 190, Elchhaut 240 (Gorduan, 19.10.2026: eine Leine je Biom; 27.10.2026: je Leine mindestens ×1,25). Löst DurabilityPerTier ab.", new AcceptableValueRange<int>(1, 100));
		PortalTravel = Gc.BindSynced(Config, section, "PortalTravel", true, "Angeleinte Tiere kommen bei Teleports mit: durch Portale und in Dungeons (hinein und hinaus).");
		ChaseDistance = Gc.BindSynced(Config, section, "ChaseDistance", 20f, "Angeleinte Tiere kämpfen, bis der Gegner tot oder weiter als so viele Meter von ihnen weg ist; dann folgen sie wieder dem Spieler.", new AcceptableValueRange<float>(5f, 60f));
		CommandMinLevel = Gc.BindSynced(Config, section, "CommandMinLevel", 10, "Befehle mit der Leine in der Hand (Linksklick hinschicken, Rechtsklick angreifen) nimmt ein Tier erst ab dieser Stufe an (Stufe gezüchteter Tiere, Tierpflege; gezähmte Tiere haben Stufe 0). Gilt nur, wenn die Tierpflege an ist.", new AcceptableValueRange<int>(0, 150));
		CommandCooldown = Gc.BindSynced(Config, section, "CommandCooldown", 60f, "Pause in Sekunden nach einem Befehl, je Tier. Sie wird um den Durchschnitt aus Tier-Stufe und halber Tierpflege-Stufe des Spielers in Prozent kürzer (Gorduan, 19.10.2026: Tierpflege zählt halb; 20 und 20: 15 % kürzer; ab 100 %: keine Pause).", new AcceptableValueRange<float>(0f, 600f));
		IllusionCooldown = Gc.BindSynced(Config, section, "IllusionCooldown", 300f, "Lokis Blendwerk (EpicLoot): Ein getötetes Trugbild kehrt nach so vielen Sekunden von selbst zurück, solange das Tier an der Leine hängt. Die Zeit läuft in der Leine weiter, auch wenn man neu anleint.", new AcceptableValueRange<float>(10f, 3600f));
		AnimalRules = Gc.BindSynced(Config, section, "AnimalRules", "", "Eigene Leinenlast (0,5 bis 10, Kommazahl mit Punkt) für Tiere, z. B. aus anderen Mods: Prefab=Leinenlast, durch Komma getrennt, * als Platzhalter. Beispiel: MyBear=2.5, MyMod_*=3. Gilt vor allen eingebauten Regeln und vor Regeln anderer Mods.");
		PostPieces = Gc.BindSynced(Config, section, "PostPieces", Posts.DefaultPieces, "Bauteile, an die man Tiere anbinden kann: Pfosten, Zäune, Mauern (Prefab-Namen, durch Komma getrennt, * als Platzhalter). Bauteile mit eigener Benutzen-Funktion (Tore, Fackeln) werden ausgelassen. Gilt für Bauteile, die danach geladen werden.");
		Enabled.SettingChanged += (_, _) =>
		{
			ApplyRecipes();
			Whistles.ApplyRecipes(); // Pfeifen hängen an den Leinen (Whistles.On)
		};
		DurabilityStep.SettingChanged += (_, _) => ApplyDurability();
		AnimalRules.SettingChanged += (_, _) => Ranks.Reload();
		PostPieces.SettingChanged += (_, _) => Posts.RulesChanged();
		WhistlesEnabled = Gc.BindSynced(Config, section, "Whistles", true, "Tierpfeifen (vier: Edelholz an der Werkbank, Eisen, Walkürenpfeife und Flammenmetall an der Schmiede): Linksklick schickt alle Tiere an deinen Leinen hin, Rechtsklick lässt alle angreifen. Bessere Pfeifen verkürzen die Befehlspause (bis −50 %), nach jedem Pfiff ist die Pfeife 5 s gesperrt, jeder Pfiff gibt den gehorchenden Tieren 5 s mehr Tempo (bis +25 %) und ihre Leinen nehmen so lange keinen Schaden; EpicLoot-Verzauberungen der Pfeife wirken dann mit.");
		WhistlesEnabled.SettingChanged += (_, _) => Whistles.ApplyRecipes();
		Ranks.Reload();
		AddLocalization();
		PrefabManager.OnVanillaPrefabsAvailable += CreateItems;
		PrefabManager.OnPrefabsRegistered += Young.AddTameable; // vor dem Log: Jungtiere zählen dann mit
		PrefabManager.OnPrefabsRegistered += Ranks.LogTameable;
		LeashMagic.Init(harmony); // nur mit EpicLoot
		LeashTooltipEditor.Register(); // EpicLoot 0.15.2: Länge und Bonus hinter die Werte
	}

	internal static void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Guard("Leinenlänge", Control.TickWheel); // vor der Kamera (LateUpdate): Umschalt + Mausrad verstellt die Leine
		Guard("Leinen", Links.Tick);
		Guard("Tierpflege", Care.Tick); // nach den Leinen: Leinenbonus steht dann schon im Tier
		Guard("Countdown", Countdown.Tick); // Zähmen, Geburt, Heranwachsen, Schlüpfen
		Guard("Trugbilder", Illusions.Tick);
		Guard("Verzauberungen", Bonds.Tick);
		Guard("Sondereffekte", LeashUniqueEffects.Tick); // einzigartige Leinen (Gorduan, 27.10.2026)
		Guard("Anzeigen", Bonds.TickVisuals);
		Guard("Ohnmacht", Bonds.TickFaint); // eigener Schutz: sonst bliebe ein Tier bei einem Fehler oben in der Ohnmacht hängen
		Guard("Seile", Rope.Tick);
		Guard("Menü", LeashMenu.Update); // auch nach einem Fehler oben: das Menü gibt die Eingabe sicher wieder frei
		Guard("Leinenleiste", LeashBar.Tick); // Anzeige neben der Schnellleiste (Gorduan, 24.10.2026)
		Guard("Aufräumen", Bonds.Cleanup);
		Guard("Verhalten", Behaviour.Cleanup);
	}

	private static readonly HashSet<string> ReportedErrors = new();

	/// <summary>Ein Teilsystem darf werfen, ohne die anderen in diesem Bild mitzureißen (Fehler je Teil einmal im Log).</summary>
	private static void Guard(string part, Action action)
	{
		try
		{
			action();
		}
		catch (Exception e)
		{
			if (ReportedErrors.Add(part))
			{
				Log.LogWarning($"Tierleinen ({part}): {e}");
			}
		}
	}

	internal static int MaxDurability(Tier tier) => Mathf.RoundToInt(tier.Durability * Math.Max(1, DurabilityStep.Value)); // Gorduan 27.10.2026: ×1,25-Regel (Tier.Durability)

	/// <summary>Gorduan: Leinen lassen sich an der Werkbank aufwerten (Qualität 1 bis 4, wie Waffen und Rüstung).</summary>
	internal const int MaxQuality = 4;

	/// <summary>Jede Aufwertung gibt die Hälfte der Grund-Haltbarkeit dazu (Qualität 4 = 250 %).</summary>
	internal static float DurabilityPerQuality(Tier tier) => MaxDurability(tier) * 0.5f;

	/// <summary>Material je Aufwertung: die halbe Herstellmenge (aufgerundet), Barren 1 (Valheim: Stufe 2 einfach, 3 doppelt, 4 vierfach).</summary>
	internal static int MaterialPerQuality(Tier tier) => Math.Max(1, (tier.Amount + 1) / 2);

	/// <summary>Höchste Werkbank-Stufe (mit Werkzeugregal); höher verlangt Valheim beim Aufwerten nie.</summary>
	internal const int MaxWorkbenchLevel = 5;

	internal static bool IsLeash(ItemDrop.ItemData? item, out Tier tier)
	{
		tier = null!;
		return item?.m_shared != null && ByItemName.TryGetValue(item.m_shared.m_name, out tier!);
	}

	/// <summary>Leine zur gespeicherten Nummer (Tier, Knoten); entfallene Leinen zählen als die Leine ihres Bioms.</summary>
	internal static Tier? TierAt(int level)
	{
		if (RetiredLevels.TryGetValue(level, out int kept))
		{
			level = kept;
		}
		foreach (Tier tier in Tiers)
		{
			if (tier.Level == level)
			{
				return tier;
			}
		}
		return null;
	}

	/// <summary>Spieler mit dieser Spieler-ID, wenn er gerade da ist (z. B. der Reiter laut Sattel).</summary>
	internal static Player? FindPlayer(long playerId)
	{
		if (playerId == 0L)
		{
			return null;
		}
		foreach (Player player in Player.GetAllPlayers())
		{
			if (player != null && player.GetPlayerID() == playerId)
			{
				return player;
			}
		}
		return null;
	}

	/// <summary>Item-Texte in Valheims Übersetzung eintragen (Token $gc_leash_item, $gc_leash_desc), aus den eigenen Sprachdateien.</summary>
	private static void AddLocalization()
	{
		CustomLocalization localization = LocalizationManager.Instance.GetLocalization();
		foreach (string key in new[] { "gc_leash_item", "gc_leash_desc", "gc_leash_knot", "gc_leash_knot_desc", "gc_whistle_item", "gc_whistle_desc", "gc_whistle_valkyrie" }.Concat(LeashMagic.LocalizationKeys()))
		{
			foreach (KeyValuePair<string, string> pair in GcLoc.AllLanguages(key))
			{
				localization.AddTranslation(pair.Key, key, pair.Value);
			}
		}
	}

	private static void CreateItems()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= CreateItems;
		try
		{
			Posts.CreateKnotPrefab(); // zuerst: ohne Knoten kein Anbinden, auch wenn bei den Leinen etwas schiefgeht
		}
		catch (Exception e)
		{
			Log.LogWarning($"Tierleinen: Knoten-Bauteil nicht erstellt, Anbinden ist aus: {e.Message}");
		}
		int created = 0;
		foreach (Tier tier in Tiers)
		{
			ItemDrop? material = PrefabManager.Instance.GetPrefab(tier.Material)?.GetComponent<ItemDrop>();
			if (material == null)
			{
				Log.LogInfo($"Leine Stufe {tier.Rank}: Material {tier.Material} gibt es im Spiel nicht, Stufe entfällt.");
				continue;
			}
			if (tier.Bar.Length > 0 && PrefabManager.Instance.GetPrefab(tier.Bar) == null)
			{
				Log.LogInfo($"Leine Stufe {tier.Rank}: Barren {tier.Bar} gibt es im Spiel nicht, Stufe entfällt.");
				continue;
			}
			string name = "$gc_leash_item (" + material.m_itemData.m_shared.m_name + ")";
			List<RequirementConfig> requirements = new() { new RequirementConfig(tier.Material, tier.Amount, MaterialPerQuality(tier), true) };
			if (tier.Bar.Length > 0)
			{
				requirements.Add(new RequirementConfig(tier.Bar, tier.BarAmount, 1, true));
			}
			ItemConfig config = new()
			{
				Name = name,
				Description = "$gc_leash_desc",
				CraftingStation = CraftingStations.Workbench,
				RepairStation = CraftingStations.Workbench,
				MinStationLevel = tier.Station,
				Weight = 0.5f,
				Icons = new[] { LeashIcon.Create(tier.Color) },
				Requirements = requirements.ToArray(),
			};
			config.StackSize = 1;
			CustomItem item = new(tier.PrefabName, tier.Material, config);
			ItemDrop.ItemData.SharedData shared = item.ItemDrop.m_itemData.m_shared;
			shared.m_name = name;
			// am Boden die Seilrolle aus Blender statt Fell oder Leder, Symbol daraus (vor MakeHandItem: entfernt alle Renderer)
			try
			{
				Sprite? icon = RopeLook.DressLeash(item.ItemDrop.gameObject, tier);
				if (icon != null)
				{
					shared.m_icons = new[] { icon };
				}
			}
			catch (Exception e)
			{
				Log.LogWarning($"Leine Stufe {tier.Rank}: Seilrolle nicht angelegt, es bleibt das alte Aussehen: {e.Message}");
			}
			Control.MakeHandItem(item.ItemDrop.gameObject, shared, tier); // in die Hand wie eine Waffe, ohne Schaden und Block
			shared.m_maxStackSize = 1;
			shared.m_useDurability = true;
			shared.m_canBeReparied = true;
			shared.m_maxQuality = MaxQuality;
			shared.m_durabilityPerLevel = DurabilityPerQuality(tier);
			shared.m_maxDurability = MaxDurability(tier);
			shared.m_teleportable = true;
			shared.m_value = 0;
			item.ItemDrop.m_itemData.m_durability = shared.m_maxDurability;
			if (!ItemManager.Instance.AddItem(item))
			{
				Log.LogWarning($"Leine Stufe {tier.Rank} ({tier.PrefabName}) konnte nicht registriert werden.");
				continue;
			}
			tier.Prefab = item.ItemDrop;
			tier.Item = item;
			ByItemName[name] = tier;
			created++;
		}
		ApplyRecipes();
		Log.LogInfo($"Tierleinen: {created} Stufen erstellt (Haltbarkeit {DurabilityStep.Value} pro Stufe).");
		LeashMagic.RegisterLoot(); // EpicLoot-Beute erst jetzt: nur Leinen, die es wirklich gibt
		try
		{
			Whistles.CreateItems(); // Tierpfeifen (Gorduan, 18.10.2026), nach den Leinen: gleicher Skill, gleiche Hand-Logik
		}
		catch (Exception e)
		{
			Log.LogWarning($"Tierpfeifen nicht erstellt: {e}");
		}
	}

	private static void ApplyRecipes()
	{
		foreach (Tier tier in Tiers)
		{
			if (tier.Item?.Recipe?.Recipe != null)
			{
				tier.Item.Recipe.Recipe.m_enabled = On; // aus: nicht mehr herstellbar, vorhandene Leinen bleiben erhalten
			}
		}
	}

	private static void ApplyDurability()
	{
		foreach (Tier tier in Tiers)
		{
			if (tier.Prefab == null)
			{
				continue;
			}
			ItemDrop.ItemData.SharedData shared = tier.Prefab.m_itemData.m_shared; // gilt für alle Leinen dieser Stufe
			shared.m_maxDurability = MaxDurability(tier);
			shared.m_durabilityPerLevel = DurabilityPerQuality(tier);
			tier.Prefab.m_itemData.m_durability = shared.m_maxDurability;
		}
		Player? player = Player.m_localPlayer;
		if (player == null)
		{
			return;
		}
		List<ItemDrop.ItemData> items = new(player.GetInventory().GetAllItems());
		if (LeashBar.Of(player) is Inventory bar)
		{
			items.AddRange(bar.GetAllItems()); // auch die Leinen der Leinenleiste
		}
		foreach (ItemDrop.ItemData item in items)
		{
			if (IsLeash(item, out Tier tier))
			{
				item.m_durability = Mathf.Min(item.m_durability, item.GetMaxDurability()); // nicht über dem neuen Höchstwert (mit Verzauberung)
			}
		}
	}
}

/// <summary>
/// Leinenlast je Tier (Gorduan, 15.10.2026, dritte Fassung: „sinnvolle Regeln“ nach Recherche der zähmbaren Tiere): So viel
/// Haltbarkeit verliert die Leine je CheckInterval Sekunden über der Höchstlänge (gedehnt oder mitgezogen); Kämpfen in
/// Reichweite kostet nichts. Sie richtet sich nach Größe, Kraft und Gewicht des Tiers (Spieldaten von Valheim 1.0,
/// Quellen im 19. Teil der Klassifizierung), in Schritten von 0,5:
///  * Huhn 0,5 (kleinster Körper, 10 LP), Wildschwein 1 (klein, 10 LP), Wolf 1,5 (mittel, 80 LP, starker Biss),
///    Asksvin 2,5 (groß, Reittier, 800 LP, schnell), Elch 3 (groß, Reittier, 1000 LP, schnellstes Tier, trampelt),
///    Lox 3,5 (riesig, schwerstes Tier, 1000 LP, Reittier, trampelt).
///  * Jungtiere die Hälfte ihres Tiers (auf 0,5 gerundet, mindestens 0,5), Beschwörungen wie ihr Tier.
///  * Alle anderen Kreaturen (falls eine Mod sie zähmbar macht) schätzt Estimate aus ihren Spieldaten: Körpergröße
///    (Kollider), Lebenspunkte, Gewicht (Rigidbody) und Tempo. Was so geschätzt wird, steht einmal im Log.
///
/// Reihenfolge der Regeln: Server-Regeln (Config AnimalRules) vor Regeln anderer Mods (LeashApi) vor der Tabelle. Danach
/// für unbekannte Tiere: ausgewachsene Form (Growup, halber Wert), Name ohne Zusatz (z. B. _sleeping), zuletzt Estimate.
/// </summary>
internal static class Ranks
{
	/// <summary>Kleinste Leinenlast (Huhn); auch die Untergrenze der EpicLoot-Eigenschaft „Leinenlast“.</summary>
	internal const float Min = 0.5f;
	internal const float Max = 10f;

	/// <summary>Stufen der Leinenlast; auch die Stufen der EpicLoot-Eigenschaft „Leinenlast“.</summary>
	internal const float Step = 0.5f;

	private static readonly Dictionary<string, float> Vanilla = new(StringComparer.OrdinalIgnoreCase)
	{
		// zähmbare Tiere (Valheim 1.0.17)
		["Hen"] = 0.5f,
		["Boar"] = 1f,
		["Wolf"] = 1.5f,
		["Asksvin"] = 2.5f,
		["Moose"] = 3f,
		["Lox"] = 3.5f,
		// Jungtiere: halber Wert ihres Tiers, auf 0,5 gerundet, mindestens 0,5
		["Chicken"] = 0.5f,
		["Boar_piggy"] = 0.5f,
		["Wolf_cub"] = 1f,
		["Asksvin_hatchling"] = 1.5f,
		["Moose_calf"] = 1.5f,
		["Lox_Calf"] = 2f,
		// Gorduan, 20.10.2026: weitere zähmbare Tiere (Standard aus) nach derselben Logik
		["Neck"] = 0.5f,
		["Hare"] = 0.5f,
		["Deer"] = 1.5f,
		["Seal"] = 2f,
		["Bjorn"] = 3f,
		["Unbjorn"] = 3.5f,
		["GC_Bjorn_Cub"] = 1.5f, // Bärenjunges aus GC_Tierarten
		// Beschwörungen (Spirit Caller, Dead Raiser) wie ihr Tier; der Geisterbär groß wie ein Asksvin, das Skelett menschengroß
		["Boar_spiritcaller"] = 1f,
		["Wolf_spiritcaller"] = 1.5f,
		["Moose_spiritcaller"] = 3f,
		["Bjorn_spiritcaller"] = 2.5f,
		["Skeleton_Friendly"] = 1f,
	};

	private static readonly List<KeyValuePair<string, float>> ConfigRules = new();
	internal static readonly List<KeyValuePair<string, float>> ApiRules = new();
	internal static readonly List<Func<GameObject, float>> ApiProviders = new();
	private static readonly Dictionary<string, float> Cache = new(StringComparer.OrdinalIgnoreCase);
	private static bool logged;

	/// <summary>Leinenlast als Text (eine Nachkommastelle, nur wenn nötig).</summary>
	internal static string Text(float load) => load.ToString("0.#");

	/// <summary>Auf 0,5 runden (halbe Schritte aufwärts), mindestens Min.</summary>
	private static float Round(float load) => Mathf.Max(Min, Mathf.Floor(load / Step + 0.5f) * Step);

	/// <summary>Server-Regeln aus der Config neu lesen (auch wenn der Server sie ändert). Kommazahlen mit Punkt: MyBear=2.5.</summary>
	internal static void Reload()
	{
		ConfigRules.Clear();
		foreach (string raw in Plugin.AnimalRules.Value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
		{
			string entry = raw.Trim();
			int split = entry.IndexOfAny(new[] { '=', ':' });
			if (split <= 0 || !float.TryParse(entry.Substring(split + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float load))
			{
				if (entry.Length > 0)
				{
					Plugin.Log.LogWarning($"Tierleinen: Regel „{entry}\" nicht verstanden (Prefab=Leinenlast, z. B. MyBear=2.5).");
				}
				continue;
			}
			ConfigRules.Add(new KeyValuePair<string, float>(entry.Substring(0, split).Trim(), Mathf.Clamp(load, Min, Max)));
		}
		Cache.Clear();
	}

	internal static void Changed() => Cache.Clear();

	public static float Of(Character animal) => Of(animal.gameObject);

	public static float Of(GameObject go)
	{
		string name = PrefabName(go);
		if (Cache.TryGetValue(name, out float load))
		{
			return load;
		}
		load = Mathf.Clamp(Resolve(go, name, 0, out string? guessed), Min, Max);
		Cache[name] = load;
		if (guessed != null)
		{
			Plugin.Log.LogInfo($"Tierleinen: {name} hat keine feste Leinenlast, geschätzt {Text(load)} ({guessed}). Festlegen mit AnimalRules, z. B. {name}={load.ToString("0.#", CultureInfo.InvariantCulture)}.");
		}
		return load;
	}

	internal static string PrefabName(GameObject go)
	{
		string name = go.name;
		int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
		return clone >= 0 ? name.Substring(0, clone).Trim() : name;
	}

	private static float Resolve(GameObject go, string name, int depth, out string? guessed)
	{
		guessed = null;
		if (Match(ConfigRules, name, out float load) || Match(ApiRules, name, out load))
		{
			return load;
		}
		foreach (Func<GameObject, float> provider in ApiProviders)
		{
			try
			{
				load = provider(go);
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning($"Tierleinen: Regel einer anderen Mod für {name} fehlgeschlagen: {e.Message}");
				load = 0f;
			}
			if (load > 0f)
			{
				return load;
			}
		}
		if (Vanilla.TryGetValue(name, out load))
		{
			return load;
		}
		// Jungtiere: halber Wert ihrer ausgewachsenen Form
		Growup? growup = go.GetComponent<Growup>();
		if (growup != null && growup.m_grownPrefab != null && depth < 3)
		{
			return Round(0.5f * Resolve(growup.m_grownPrefab, PrefabName(growup.m_grownPrefab), depth + 1, out guessed));
		}
		// Varianten wie Draugr_Elite_sleeping: Name schrittweise kürzen
		for (int cut = name.LastIndexOf('_'); cut > 0; cut = name.LastIndexOf('_', cut - 1))
		{
			string shorter = name.Substring(0, cut);
			if (Match(ConfigRules, shorter, out load) || Match(ApiRules, shorter, out load) || Vanilla.TryGetValue(shorter, out load))
			{
				return load;
			}
		}
		return Estimate(go, out guessed);
	}

	/// <summary>
	/// Schätzung aus den Spieldaten, nach denselben Merkmalen wie die Tabelle: 0,5 plus Größe (Kollider-Radius unter 0,45 m
	/// nichts, bis 0,8 m +0,5, bis 1,2 m +1, darüber +1,5), Kraft (Lebenspunkte unter 50 nichts, bis 500 +0,5, bis 2000 +1,
	/// darüber +1,5), Gewicht (Rigidbody ab 30 +0,5; die meisten Kreaturen wiegen 10, der Lox 60) und Tempo (Rennen ab 12
	/// +0,5). Höchstens 4.
	/// </summary>
	private static float Estimate(GameObject go, out string? guessed)
	{
		Character? character = go.GetComponent<Character>();
		if (character == null)
		{
			guessed = "kein Tier";
			return 1f;
		}
		CapsuleCollider? capsule = go.GetComponent<CapsuleCollider>();
		Vector3 scale = go.transform.localScale;
		float radius = capsule != null ? capsule.radius * Mathf.Max(scale.x, scale.z) : 0.5f;
		float health = character.m_health;
		Rigidbody? body = go.GetComponent<Rigidbody>();
		float mass = body != null ? body.mass : 10f;
		float load = Min
			+ (radius < 0.45f ? 0f : radius < 0.8f ? 0.5f : radius < 1.2f ? 1f : 1.5f)
			+ (health < 50f ? 0f : health < 500f ? 0.5f : health < 2000f ? 1f : 1.5f)
			+ (mass >= 30f ? 0.5f : 0f)
			+ (character.m_runSpeed >= 12f ? 0.5f : 0f);
		guessed = $"Radius {radius:0.##} m, {health:0} LP, Gewicht {mass:0}, Tempo {character.m_runSpeed:0.#}";
		return Mathf.Min(4f, load);
	}

	private static bool Match(List<KeyValuePair<string, float>> rules, string name, out float load)
	{
		foreach (KeyValuePair<string, float> rule in rules)
		{
			if (Matches(rule.Key, name))
			{
				load = rule.Value;
				return true;
			}
		}
		load = 0f;
		return false;
	}

	/// <summary>Prefab-Name gegen Regel: genau, oder mit * am Anfang, am Ende oder an beiden Enden.</summary>
	internal static bool Matches(string pattern, string name)
	{
		bool start = pattern.StartsWith("*", StringComparison.Ordinal);
		bool end = pattern.EndsWith("*", StringComparison.Ordinal) && pattern.Length > 1;
		string core = pattern.Trim('*');
		if (core.Length == 0)
		{
			return pattern.Length > 0; // nur „*": alle
		}
		if (start && end)
		{
			return name.IndexOf(core, StringComparison.OrdinalIgnoreCase) >= 0;
		}
		if (start)
		{
			return name.EndsWith(core, StringComparison.OrdinalIgnoreCase);
		}
		if (end)
		{
			return name.StartsWith(core, StringComparison.OrdinalIgnoreCase);
		}
		return string.Equals(core, name, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Einmal pro Spielstart: alle zähmbaren Tiere mit ihrer Leinenlast ins Log (auch die anderer Mods).</summary>
	internal static void LogTameable()
	{
		if (logged || ZNetScene.instance == null)
		{
			return;
		}
		logged = true;
		List<string> entries = new();
		foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
		{
			if (prefab != null && prefab.GetComponent<Tameable>() != null && prefab.GetComponent<Character>() != null)
			{
				entries.Add(prefab.name + " " + Text(Of(prefab)));
			}
		}
		Plugin.Log.LogInfo("Tierleinen: zähmbare Tiere und ihre Leinenlast: " + string.Join(", ", entries));
	}
}

/// <summary>
/// Einstiegspunkt für andere Mods: eigene Tiere für die Tierleinen einstufen (Leinenlast 1 bis 10 = Haltbarkeitsverlust
/// pro Prüfung, Kommazahlen erlaubt). Server-Regeln aus der Config (AnimalRules) haben immer Vorrang. Aufruf am besten im
/// Awake der Mod.
/// </summary>
public static class LeashApi
{
	/// <summary>Leinenlast für ein Prefab oder ein Muster mit * festlegen (z. B. "MyBear" oder "MyMod_*").</summary>
	public static void SetAnimalLoad(string prefabOrPattern, float load)
	{
		if (string.IsNullOrWhiteSpace(prefabOrPattern))
		{
			return;
		}
		string key = prefabOrPattern.Trim();
		Ranks.ApiRules.RemoveAll(rule => string.Equals(rule.Key, key, StringComparison.OrdinalIgnoreCase));
		Ranks.ApiRules.Add(new KeyValuePair<string, float>(key, Mathf.Clamp(load, Ranks.Min, Ranks.Max)));
		Ranks.Changed();
	}

	/// <summary>Wie SetAnimalLoad mit ganzer Zahl (ältere Schnittstelle).</summary>
	public static void SetAnimalRank(string prefabOrPattern, int rank) => SetAnimalLoad(prefabOrPattern, rank);

	/// <summary>Eigene Regel als Funktion: gibt für ein Tier (Prefab oder Instanz) die Leinenlast zurück, 0 = kennt das Tier nicht.</summary>
	public static void AddLoadProvider(Func<GameObject, float> provider)
	{
		if (provider != null)
		{
			Ranks.ApiProviders.Add(provider);
			Ranks.Changed();
		}
	}

	/// <summary>Wie AddLoadProvider mit ganzen Zahlen (ältere Schnittstelle).</summary>
	public static void AddRankProvider(Func<GameObject, int> provider)
	{
		if (provider != null)
		{
			AddLoadProvider(go => provider(go));
		}
	}

	/// <summary>Leinenlast, mit der GC_Server ein Tier einstuft.</summary>
	public static float GetAnimalLoad(GameObject animal) => animal != null ? Ranks.Of(animal) : 0f;

	/// <summary>Leinenlast gerundet (ältere Schnittstelle).</summary>
	public static int GetAnimalRank(GameObject animal) => Mathf.RoundToInt(GetAnimalLoad(animal));
}

/// <summary>Eine Leine des eigenen Spielers mit dem Tier daran.</summary>
internal sealed class Link
{
	public Player Holder = null!;
	public string ItemId = "";
	public ItemDrop.ItemData Item = null!;
	public Tameable Tame = null!;
	public Character Animal = null!;
	public MonsterAI? Ai;
	public ZNetView View = null!;
	public Tier Tier = null!;
	public float Load = 1f; // Leinenlast des Tiers (Ranks)
	public float NextCheck; // vorher kein Verschleiß (kurz nach dem Anlegen und nach Teleports)
	public float Stretched; // Sekunden über der Höchstlänge, noch nicht als Verschleiß abgerechnet
	public float NextWearMessage;
	public bool Traveling;
	public bool Ridden; // der Halter reitet sein Tier: Leine und Bonus bleiben, die Leine wirkt nicht aufs Laufen (Gorduan, 18.10.2026)
	// Tierpfeife (Gorduan, 18.10.2026): nach einem Pfiff, dem das Tier gehorcht, BuffSeconds lang Tempo und die Verzauberungen der
	// Pfeife (WhistleBond), dazu kein Leinenschaden. Bonds.Current verbindet sie mit den Werten der Leine
	public float WhistleUntil;
	public BondStats WhistleBond = BondStats.None;
	public BondStats? WhistleBase;
	public BondStats? WhistleCombined;
	public int TravelSlot;
	public GameObject? PrevFollow;
	public bool PrevPatrol;
	public BondStats Bond = BondStats.None;
	public float DragUntil; // Ziehen gilt mindestens bis hier (Weltzeit Time.time), damit das Tempo des Halters nicht pumpt
	public string? BondJson; // zuletzt gelesene Verzauberung (nur bei Änderung neu lesen)

	// Befehle mit der Leine in der Hand (Control): hinschicken (Linksklick) und angreifen lassen (Rechtsklick)
	public Vector3? GoTo; // Ziel, an dem das Tier wartet, bis ein neuer Befehl kommt oder der Halter zu weit weggeht
	public float GoToSince;
	public bool GoToReached;
	public Character? AttackTarget;
	public float AttackSince;

	// Einzigartige Leinen (LeashUniqueEffects, Gorduan 27.10.2026): Ratatöskr (hinter der Zuggrenze fest seit, Abstand dabei, nächster
	// Sprung), Sæhrimnir (gelaufene Zeit bis zum nächsten Fund), Bjarki (nächste Bärenwacht)
	public float StuckSince = -1f;
	public float StuckAt;
	public float NextFetch;
	public float ForageTime;
	public float NextBearGuard;

	private int Quality => Item != null ? Item.m_quality : 1;

	/// <summary>Leinenbonus dieser Leine (Stufe und Qualität): Anteil mehr Leben und Schaden für das Tier.</summary>
	public float Bonus => Plugin.LeashBonus(Tier, Quality);

	/// <summary>Befehle vergessen (neuer Befehl, Teleport, Lösen); die nachgegebene Leine gilt dann wieder als eingezogen.</summary>
	public void ClearCommands()
	{
		GoTo = null;
		GoToReached = false;
		AttackTarget = null;
		PaidOut = 0f;
	}

	/// <summary>
	/// Gorduan (16.10.2026): Bei einem Befehl (Links- oder Rechtsklick) gibt eine gekürzte Leine bis zur normalen Länge nach
	/// (Anteil 1); danach zieht sie sich mit dem Abstand wieder ein, nie wieder länger, bis zur eingestellten Länge (0 = nicht).
	/// </summary>
	public float PaidOut;

	/// <summary>Anteil der eingestellten an der normalen Länge (Mausrad).</summary>
	private float SetRatio => Plugin.LengthRatio(Tier, Item, Quality);

	/// <summary>Gilt gerade: eingestellte Länge oder, nach einem Befehl, die nachgegebene.</summary>
	public float Ratio => Mathf.Max(SetRatio, PaidOut);

	/// <summary>Mit dem Mausrad gekürzt?</summary>
	public bool Shortened => SetRatio < 0.999f;

	/// <summary>Höchstlänge der ungekürzten Leine (Stufe, Qualität, Verzauberung „Leinenreichweite").</summary>
	public float FullStrainFrom => Plugin.MaxLength(Tier, Quality) + Bond.Range;

	/// <summary>Mindestlänge (Stufe, Qualität, eingestellt oder nachgegeben): so nah kommt das Tier, wenn der Halter läuft.</summary>
	public float SlackUntil => Plugin.MinLength(Tier, Quality) * Ratio;

	/// <summary>Höchstlänge im selben Verhältnis: darüber ist sie gedehnt und verschleißt.</summary>
	public float StrainFrom => FullStrainFrom * Ratio;

	// Zögern beim Losgehen (Care.Hesitation): der Halter läuft, das Tier folgt erst ab FollowAt
	public bool HolderMoving;
	public float LastMoving;
	public float FollowAt;
	public Vector3 HesitateAt;

	/// <summary>Bis hier bewegt sich das Tier frei (90 % der Höchstlänge).</summary>
	public float FreeUntil => Plugin.FreeUntil(StrainFrom);

	/// <summary>Ab hier zieht die Leine das Tier mit (15 % über der Höchstlänge).</summary>
	public float DragFrom => Plugin.DragFrom(StrainFrom);

	/// <summary>Ab hier rutscht die Leine aus der Hand (Sicherung hinter der Zuggrenze).</summary>
	public float SlipFrom => Plugin.SlipFrom(StrainFrom);
}

/// <summary>Die Leinen des eigenen Spielers: Folgen, Haltbarkeit, Portale (läuft nur beim Halter).</summary>
internal static class Links
{
	public static readonly List<Link> Active = new();

	/// <summary>Zieht der eigene Spieler gerade ein Tier hinter sich her (Leine über der Zuggrenze, er läuft vom Tier weg)?</summary>
	public static bool Dragging;

	/// <summary>Gorduan: Wer ein Tier hinter sich herzieht, wird 50 % langsamer.</summary>
	public const float DragSpeedFactor = 0.5f;

	/// <summary>Aktive Leinen liegen in der Schnellleiste: oberste Reihe, Tasten 1–8 (auch bei verbreiterten Inventaren).</summary>
	public static bool InHotbar(ItemDrop.ItemData item) => item.m_gridPos.y == 0 && item.m_gridPos.x < 8;

	public static Link? ForTame(Tameable tame)
	{
		foreach (Link link in Active)
		{
			if (link.Tame == tame)
			{
				return link;
			}
		}
		return null;
	}

	public static Link? ForAnimal(Character animal)
	{
		foreach (Link link in Active)
		{
			if (link.Animal == animal)
			{
				return link;
			}
		}
		return null;
	}

	private static Link? ForItemId(string id)
	{
		foreach (Link link in Active)
		{
			if (link.ItemId == id)
			{
				return link;
			}
		}
		return null;
	}

	/// <summary>Sitzung eines Spielers: UserID seiner Spielfigur, bei jeder Anmeldung neu.</summary>
	public static long Session(Player? player) => player != null ? player.GetZDOID().UserID : 0L;

	public static Player? FindBySession(long session)
	{
		if (session == 0L)
		{
			return null;
		}
		foreach (Player player in Player.GetAllPlayers())
		{
			if (player != null && player.GetZDOID().UserID == session)
			{
				return player;
			}
		}
		return null;
	}

	/// <summary>Halter laut Tier-Daten, nur wenn er gerade da ist (sonst gilt die Leine als gelöst).</summary>
	public static Player? HolderOf(ZDO zdo) => FindBySession(zdo.GetLong(Plugin.HolderHash));

	/// <summary>Große Tiere messen ab ihrem Rand, nicht ab der Mitte.</summary>
	public static float Reach(Character animal) => Mathf.Max(0f, animal.GetRadius() - 0.5f);

	/// <summary>Höhenunterschied, der nicht zählt: Die Leine läuft von der Hand oder vom Knoten (gut 1 m hoch) zum Hals.</summary>
	internal const float HeightSlack = 1.5f;

	/// <summary>
	/// Wie weit die Leine gespannt ist (Halter oder Knoten bis Tier, ohne die Tiergröße). Höhenunterschiede bis 1,5 m zählen
	/// nicht, darüber der Rest: Sonst wäre eine kurze Leine am Pfostenknoten oder mit dem Halter eine Stufe höher immer
	/// straff, und das Tier klebte am Pfosten oder drückte gegen die Wand. Große Höhen (Klippe, Sprung) zählen weiter.
	/// </summary>
	public static float Span(Vector3 from, Character animal)
	{
		Vector3 to = from - animal.transform.position;
		float height = Mathf.Max(0f, Mathf.Abs(to.y) - HeightSlack);
		return Mathf.Sqrt(to.x * to.x + to.z * to.z + height * height) - Reach(animal);
	}

	/// <summary>Jede Leine bekommt beim ersten Anlegen eine ID; sie bleibt beim Verschieben im Inventar erhalten (Kopien übernehmen sie).</summary>
	private static string EnsureId(ItemDrop.ItemData item)
	{
		if (!item.m_customData.TryGetValue(Plugin.KeyItemId, out string id) || id.Length == 0)
		{
			id = Guid.NewGuid().ToString("N");
			item.m_customData[Plugin.KeyItemId] = id;
		}
		return id;
	}

	private static ItemDrop.ItemData? FindItem(Inventory inventory, string id)
	{
		foreach (ItemDrop.ItemData item in inventory.GetAllItems())
		{
			if (item.m_customData != null && item.m_customData.TryGetValue(Plugin.KeyItemId, out string value) && value == id)
			{
				return item;
			}
		}
		return null;
	}

	/// <summary>Die Leine (per ID) hält gerade ein Tier des eigenen Spielers?</summary>
	public static Link? ForItem(ItemDrop.ItemData item)
	{
		return item.m_customData != null && item.m_customData.TryGetValue(Plugin.KeyItemId, out string id) && id.Length > 0 ? ForItemId(id) : null;
	}

	/// <summary>Leine aus der Liste nehmen, ohne das Tier zu verändern (es wird gerade angebunden).</summary>
	public static void Forget(Link link) => Active.Remove(link);

	private static readonly int EpicSpiritHash = "el-spirit".GetStableHashCode();

	/// <summary>
	/// Geistertier aus EpicLoots Effekt „SpiritAnimal“ (0.15.2: das Original-Prefab, gezähmt, nicht dauerhaft, ZDO-Schlüssel
	/// „el-spirit“): vergeht nach Ablauf und darf nicht an die Leine (Review 27.10.2026: reiste sonst durchs Portal).
	/// </summary>
	private static bool EpicSpirit(Tameable tame) => tame.m_nview != null && tame.m_nview.IsValid() && tame.m_nview.GetZDO().GetLong(EpicSpiritHash) != 0L;

	/// <summary>
	/// E am Tier mit einer Leine in der Hand: anleinen; hängt es schon an einer deiner Leinen, lösen; ist es angebunden,
	/// die Leine vom Pfosten nehmen.
	/// </summary>
	public static void Use(Player player, Tameable tame, ItemDrop.ItemData item, Tier tier)
	{
		string name = tame.GetHoverName();
		if (tame.m_character != null && Illusions.IsIllusion(tame.m_character))
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_illusion_touch"));
			return; // Lokis Trugbilder lassen sich nicht anleinen
		}
		if (Ranks.PrefabName(tame.gameObject).StartsWith("EL_", StringComparison.Ordinal) || EpicSpirit(tame))
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_summon_touch"));
			return; // EpicLoots Beschwörungen (seit 0.15.0 Hels Diener, Geistertiere) vergehen von selbst: keine Leine
		}
		if (!tame.IsTamed())
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_not_tamed"));
			return;
		}
		Link? own = ForTame(tame);
		if (own != null)
		{
			Detach(own, "gcls_detached"); // Gorduan: E am angeleinten Tier löst die Leine (mit jeder Leine in der Hand)
			return;
		}
		ZNetView view = tame.m_nview;
		if (Posts.IsTied(view.GetZDO()))
		{
			Posts.Take(player, tame); // angebunden: mit der Leine vom Pfosten an die eigene Hand nehmen
			return;
		}
		Player? holder = HolderOf(view.GetZDO());
		if (holder != null && holder != player)
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_other_holder", name));
			return;
		}
		if (tame.HaveRider())
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_riding"));
			return;
		}
		if (tame.m_character != null && Bonds.IsFainted(tame.m_character))
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_is_fainted", name));
			return; // erst wieder anleinen, wenn es aufgewacht ist
		}
		if (item.m_durability <= 0f)
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_broken"));
			return;
		}
		if (LeashBar.Contains(player, item))
		{
			if (!LeashBar.Active(player, item))
			{
				player.Message(MessageHud.MessageType.Center, GcLoc.Get("gclb_locked", item.m_gridPos.x + 1, LeashBar.Levels[Mathf.Clamp(item.m_gridPos.x, 0, LeashBar.Slots - 1)]));
				return; // Leinenleiste: gesperrter Platz hält kein Tier
			}
		}
		else if (!InHotbar(item))
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_leash_hotbar"));
			return; // aktive Leinen liegen in der Schnellleiste (Tasten 1–8) oder in der Leinenleiste (Alt+1–5)
		}
		else if (LeashBar.HotbarLink(player, item) is Link other)
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gclb_hotbar_one_attach", other.Tame != null ? other.Tame.GetHoverName() : ""));
			return; // Gorduan: in der Schnellleiste wirkt immer nur eine Leine, weitere Tiere über die Leinenleiste
		}
		string id = EnsureId(item);
		Link? busy = ForItemId(id);
		if (busy != null)
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_in_use", busy.Tame != null ? busy.Tame.GetHoverName() : ""));
			return;
		}
		MonsterAI? ai = tame.GetComponent<MonsterAI>();
		Link? link = Attach(player, tame, item, tier, ai != null ? ai.GetFollowTarget() : null, ai != null && ai.GetPatrolPoint(out _));
		if (link != null)
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_attached", name, Ranks.Text(link.Load)));
		}
	}

	/// <summary>
	/// Tier an die Leine des Spielers nehmen (auch vom Pfosten). prevFollow/prevPatrol: Verhalten vor der Leine, das beim
	/// Lösen zurückkommt.
	/// </summary>
	public static Link? Attach(Player player, Tameable tame, ItemDrop.ItemData item, Tier tier, GameObject? prevFollow, bool prevPatrol)
	{
		Character? animal = tame.GetComponent<Character>();
		ZNetView view = tame.m_nview;
		if (animal == null || view == null || !view.IsValid())
		{
			return null;
		}
		string id = EnsureId(item);
		view.ClaimOwnership(); // Folgen und Ziehen laufen beim Halter, wie beim Reiten
		view.GetZDO().Set(Plugin.HolderHash, Session(player));
		view.GetZDO().Set(Plugin.TierHash, tier.Level);
		if (view.GetZDO().GetBool(Plugin.EscapedHash))
		{
			view.GetZDO().Set(Plugin.EscapedHash, false); // wieder eingefangen
		}
		MonsterAI? ai = tame.GetComponent<MonsterAI>();
		Link link = new()
		{
			Holder = player,
			ItemId = id,
			Item = item,
			Tame = tame,
			Animal = animal,
			Ai = ai,
			View = view,
			Tier = tier,
			Load = Ranks.Of(animal),
			NextCheck = Time.time + Plugin.CheckInterval.Value,
			PrevFollow = prevFollow,
			PrevPatrol = prevPatrol,
		};
		if (ai != null)
		{
			ai.ResetPatrolPoint(); // wie Valheims „Folgen"
			ai.SetFollowTarget(player.gameObject);
		}
		Active.Add(link);
		return link;
	}

	/// <summary>Leine lösen. release = false: Ein anderer Spieler hält das Tier inzwischen, nichts zurückschreiben.</summary>
	public static void Detach(Link link, string? messageKey, bool release = true)
	{
		Active.Remove(link);
		link.ClearCommands();
		string name = link.Tame != null ? link.Tame.GetHoverName() : "";
		if (release && link.View != null && link.View.IsValid())
		{
			ZDO zdo = link.View.GetZDO();
			long session = zdo.GetLong(Plugin.HolderHash);
			if (session == 0L || session == Session(link.Holder))
			{
				// nur, solange das Tier noch an dieser Leine hängt (Review 27.10.2026): Hat ein anderer Spieler es im selben Bild
				// übernommen, blieben sonst sein Besitz, seine Längen und sein Bonus nicht stehen
				if (!link.View.IsOwner())
				{
					link.View.ClaimOwnership();
				}
				zdo.Set(Plugin.HolderHash, 0L);
				zdo.Set(Plugin.TierHash, 0);
				ClearLengths(zdo);
				Bonds.ClearLight(link.View);
			}
			RestoreAi(link);
		}
		if (messageKey != null && link.Holder != null && link.Holder == Player.m_localPlayer)
		{
			link.Holder.Message(MessageHud.MessageType.Center, GcLoc.Get(messageKey, name));
		}
	}

	/// <summary>Halter-Schlüssel im Tier löschen, wenn sie noch diesen Halter nennen.</summary>
	public static void ClearKeys(ZDO zdo, Player? holder)
	{
		long session = zdo.GetLong(Plugin.HolderHash);
		if (session == 0L || session == Session(holder))
		{
			zdo.Set(Plugin.HolderHash, 0L);
			zdo.Set(Plugin.TierHash, 0);
			ClearLengths(zdo);
			zdo.Set(Bonds.LightHash, 0f); // Leuchten und Seillänge gehören zur Leine, nicht zum Tier
		}
	}

	/// <summary>Längen und Leinenbonus im Tier löschen (es hängt an keiner Leine mehr).</summary>
	public static void ClearLengths(ZDO zdo)
	{
		if (zdo.GetFloat(Plugin.MinLengthHash, 0f) != 0f)
		{
			zdo.Set(Plugin.MinLengthHash, 0f);
		}
		if (zdo.GetFloat(Plugin.MaxLengthHash, 0f) != 0f)
		{
			zdo.Set(Plugin.MaxLengthHash, 0f);
		}
		if (zdo.GetFloat(Plugin.BonusHash, 0f) != 0f)
		{
			zdo.Set(Plugin.BonusHash, 0f);
		}
		LeashUniqueEffects.ClearAnimal(zdo); // Mímir und Hel gehören zur Leine
	}

	/// <summary>Leinenbonus ins Tier schreiben, wenn er sich geändert hat (Leben und Schaden rechnet der Besitzer, Care).</summary>
	public static void WriteBonus(ZDO zdo, float bonus)
	{
		if (Mathf.Abs(zdo.GetFloat(Plugin.BonusHash, 0f) - bonus) > 0.001f)
		{
			zdo.Set(Plugin.BonusHash, bonus);
		}
	}

	/// <summary>Mindest- und Höchstlänge ins Tier schreiben, wenn sie sich geändert haben (Seil-Zeichnung bei allen, Prüfungen beim Besitzer).</summary>
	public static void WriteLengths(ZDO zdo, float min, float max)
	{
		if (Mathf.Abs(zdo.GetFloat(Plugin.MinLengthHash, 0f) - min) > 0.01f)
		{
			zdo.Set(Plugin.MinLengthHash, min);
		}
		if (Mathf.Abs(zdo.GetFloat(Plugin.MaxLengthHash, 0f) - max) > 0.01f)
		{
			zdo.Set(Plugin.MaxLengthHash, max);
		}
	}

	/// <summary>
	/// Die Leine ist gerissen (Gorduan): Das Tier ist entwischt, läuft frei herum, folgt niemandem und gehorcht erst wieder,
	/// wenn es mit einer anderen oder reparierten Leine eingefangen wird.
	/// </summary>
	public static void Escape(Link link)
	{
		Detach(link, "gcls_snapped");
		if (link.View == null || !link.View.IsValid() || !link.View.IsOwner())
		{
			return;
		}
		ZDO zdo = link.View.GetZDO();
		zdo.Set(Plugin.EscapedHash, true);
		if (zdo.GetString(ZDOVars.s_follow).Length > 0)
		{
			zdo.Set(ZDOVars.s_follow, ""); // sonst holte Valheims gespeichertes Folgen es zurück
		}
		if (link.Ai != null)
		{
			link.Ai.SetFollowTarget(null);
			link.Ai.ResetPatrolPoint(); // kein Platz, an dem es bleiben soll: es streift umher
		}
	}

	/// <summary>Wie vor der Leine: Wer folgte, folgt wieder; wer an einem Platz blieb, bleibt am neuen Platz; sonst frei.</summary>
	private static void RestoreAi(Link link)
	{
		MonsterAI? ai = link.Ai;
		if (ai == null)
		{
			return;
		}
		if (link.PrevFollow != null && link.PrevFollow.activeInHierarchy)
		{
			ai.SetFollowTarget(link.PrevFollow);
			ai.ResetPatrolPoint();
			return;
		}
		ai.SetFollowTarget(null);
		if (link.PrevPatrol)
		{
			ai.SetPatrolPoint();
		}
		else
		{
			ai.ResetPatrolPoint();
		}
	}

	private static bool reportedError;

	public static void Tick()
	{
		Dragging = false;
		for (int i = Active.Count - 1; i >= 0; i--)
		{
			if (i >= Active.Count)
			{
				continue; // Liste ist in diesem Durchgang kürzer geworden
			}
			Link link = Active[i];
			try
			{
				TickLink(link);
			}
			catch (Exception e)
			{
				if (!reportedError)
				{
					reportedError = true;
					Plugin.Log.LogWarning($"Tierleinen: Fehler bei einer Leine, sie wird gelöst: {e}");
				}
				try
				{
					Detach(link, null); // ordentlich lösen (Halter, Leuchten und Seillänge aus dem Tier)
				}
				catch (Exception)
				{
					Active.Remove(link); // nicht jedes Bild erneut werfen
				}
			}
		}
	}

	private static void TickLink(Link link)
	{
		bool viewValid = link.View != null && link.View.IsValid();
		if (link.Holder == null || link.Holder.IsDead() || link.Animal == null || link.Animal.IsDead() || !viewValid)
		{
			Detach(link, null, viewValid);
			return;
		}
		if (!Plugin.On)
		{
			Detach(link, "gcls_detached");
			return;
		}
		if (link.View!.GetZDO().GetLong(Plugin.HolderHash) != Session(link.Holder)) // viewValid oben
		{
			Detach(link, "gcls_taken", false); // jemand anderes hat das Tier angeleint: nicht um den Besitz streiten
			return;
		}
		long rider = link.View.GetZDO().GetLong(ZDOVars.s_user);
		bool ridden = rider != 0L && Plugin.FindPlayer(rider) != null; // direkt aus dem ZDO, HaveRider() ist vom letzten Physikschritt
		if (ridden && rider != link.Holder.GetPlayerID())
		{
			if (link.View.IsOwner())
			{
				ClearKeys(link.View.GetZDO(), link.Holder);
			}
			Detach(link, "gcls_ridden", false); // fremder Reiter: Besitz und Verhalten bleiben bei ihm
			return;
		}
		if (ridden != link.Ridden)
		{
			// Gorduan (18.10.2026): Reitet der Halter sein Tier, löst sich die Leine nicht (der Leinenbonus wirkt weiter), sie
			// zieht aber nicht, verschleißt nicht und nimmt keine Befehle. Beim Aufsitzen enden laufende Befehle
			link.Ridden = ridden;
			link.ClearCommands();
			link.Stretched = 0f;
		}
		ItemDrop.ItemData? item = FindItem(link.Holder.GetInventory(), link.ItemId) ?? LeashBar.Find(link.Holder, link.ItemId);
		if (item == null)
		{
			Detach(link, "gcls_lost_item"); // Leinen gehören ins Inventar oder in die Leinenleiste
			return;
		}
		link.Item = item;
		string? json = LeashMagic.JsonOf(item);
		if (!ReferenceEquals(json, link.BondJson))
		{
			link.BondJson = json; // EpicLoot ersetzt den Text bei jeder Änderung: nur dann neu lesen
			link.Bond = Bonds.Of(item);
		}
		if (item.m_durability <= 0f && !link.Bond.Unbreakable)
		{
			Escape(link);
			return;
		}
		if (LeashBar.Contains(link.Holder, item))
		{
			if (!LeashBar.Active(link.Holder, item))
			{
				Detach(link, "gclb_locked_detach"); // Leinenleiste: Platz gesperrt (Tierpflege gesunken), die Leine bleibt (Gorduan)
				return;
			}
		}
		else if (!InHotbar(item))
		{
			Detach(link, "gcls_not_hotbar"); // aus der Schnellleiste genommen: nicht mehr aktiv
			return;
		}
		else if (LeashBar.HotbarLink(link.Holder, item) is Link first && first.Item.m_gridPos.x < item.m_gridPos.x)
		{
			Detach(link, "gclb_hotbar_detach"); // Gorduan: in der Schnellleiste wirkt nur eine Leine, die linke behält ihr Tier
			return;
		}
		if (!link.View.IsOwner())
		{
			link.View.ClaimOwnership();
		}
		ZDO linkZdo = link.View.GetZDO();
		WriteLengths(linkZdo, link.SlackUntil, link.StrainFrom); // damit alle das Seil mit der richtigen Länge zeichnen
		WriteBonus(linkZdo, link.Bonus); // mehr Leben und Schaden, solange es an dieser Leine hängt
		if (link.Traveling)
		{
			link.ClearCommands();
			return; // unterwegs durchs Portal: die Position setzt Travel
		}
		if (link.Ridden)
		{
			return; // der Halter sitzt auf dem Tier: kein Folgen, Ziehen, Verschleiß und keine Befehle, nur der Bonus oben
		}
		Control.Check(link); // Befehle prüfen: Ziel erreicht oder zu weit weg, Gegner tot oder entkommen
		if (link.Ai != null && link.Ai.GetFollowTarget() != link.Holder.gameObject)
		{
			link.Ai.SetFollowTarget(link.Holder.gameObject);
		}
		float distance = Span(link.Holder.transform.position, link.Animal);
		if (link.Bond.Fetch && LeashUniqueEffects.Fetch(link, distance))
		{
			distance = Span(link.Holder.transform.position, link.Animal); // Ratatöskr: neben dem Halter, statt sich zu lösen (Gorduan, 27.10.2026)
		}
		if (distance > link.SlipFrom)
		{
			Detach(link, "gcls_too_far"); // Sicherung: Die Leine konnte das Tier nicht nachziehen, sie rutscht aus der Hand
			return;
		}
		if (link.PaidOut > 0f && link.AttackTarget == null && (link.Ai == null || link.Ai.m_targetCreature == null)
			&& (link.GoTo == null || link.GoToReached))
		{
			// Gorduan: Nach einem Befehl zieht sich die nachgegebene Leine mit dem Abstand wieder ein (nie wieder länger), bis zur
			// eingestellten Länge. Im Kampf (auch mit dem nächsten Gegner) und auf dem Weg zum Zielort nicht: sonst würde sie
			// dort straff und nähme Schaden. Wartet das Tier am Zielort, zählt auch der Abstand zum Ort, wie Control.Check
			// ihn misst (mit der Tiergröße): sonst höbe das Einziehen den Befehl bei großen Tieren gleich wieder auf
			float span = link.GoTo is Vector3 point ? Mathf.Max(distance, Utils.DistanceXZ(link.Holder.transform.position, point)) : distance;
			float fits = (span / Plugin.FreePart + 0.5f) / Mathf.Max(0.1f, link.FullStrainFrom); // das Tier ist gerade noch frei
			link.PaidOut = Mathf.Min(link.PaidOut, fits);
			if (link.PaidOut <= Plugin.LengthRatio(link.Tier, item, item.m_quality) + 0.001f)
			{
				link.PaidOut = 0f; // wieder auf der eingestellten Länge
			}
		}
		// Gorduan: Über der Zuggrenze (Höchstlänge + 15 %) zieht die Leine das Tier mit, der Halter ist dabei halb so schnell.
		// Das gilt mindestens 1,5 s und endet erst, wenn das Tier wieder innerhalb der Höchstlänge ist (sonst pumpt das
		// Tempo, weil das Tier beim halben Tempo sofort aufholt)
		if (link.Holder == Player.m_localPlayer)
		{
			// einmal über der Zuggrenze: verlängern, solange der Halter weiter wegläuft und die Leine über der Höchstlänge
			// ist (die Leine zieht das Tier sofort wieder unter die Zuggrenze, sonst wechselte sein Tempo ständig)
			bool dragging = Time.time < link.DragUntil;
			if (distance > link.DragFrom || (dragging && distance > link.StrainFrom))
			{
				Vector3 away = link.Holder.transform.position - link.Animal.transform.position;
				Vector3 velocity = link.Holder.GetVelocity();
				away.y = 0f;
				velocity.y = 0f;
				if (Vector3.Dot(velocity, away.normalized) > 0.5f)
				{
					link.DragUntil = Time.time + 1.5f;
				}
			}
			if (Time.time < link.DragUntil && distance > link.StrainFrom)
			{
				Dragging = true;
			}
		}
		// Gorduan: Kämpfen in Reichweite kostet nichts. Über der Höchstlänge (gedehnt, oder darüber mitgezogen) verschleißt die
		// Leine je CheckInterval Sekunden um die Leinenlast. Gezählt wird die Zeit über der Höchstlänge, nicht eine
		// Stichprobe je Sekunde: Ein Tier, das um die Grenze pendelt, fiel sonst fast immer knapp darunter
		if (Time.time < link.WhistleUntil)
		{
			link.Stretched = 0f; // Gorduan (18.10.2026): nach einem Pfiff so lange kein Leinenschaden
		}
		else if (distance > link.StrainFrom && Time.time >= link.NextCheck)
		{
			link.Stretched += Time.deltaTime;
			float interval = Mathf.Max(0.25f, Plugin.CheckInterval.Value);
			if (link.Stretched >= interval)
			{
				link.Stretched -= interval;
				Wear(link, link.Ai != null && link.Ai.m_targetCreature != null);
			}
		}
		else if (distance <= link.FreeUntil)
		{
			link.Stretched = 0f; // erst im freien Bereich vergessen: kurzes Ein- und Austauchen an der Grenze zählt weiter
		}
	}

	private static void Wear(Link link, bool fighting)
	{
		ItemDrop.ItemData item = link.Item;
		if (link.Bond.Unbreakable)
		{
			return; // verzaubert: unzerstörbar
		}
		// Leinenlast des Tiers, EpicLoot „Leinenlast“ zieht je Stufe 0,5 ab (mindestens 0,5); dazu die eingestellte Länge
		// (Gorduan: auf 1 m gekürzt +50 %, volle Länge −10 %)
		float loss = Mathf.Max(Ranks.Min, link.Load - Ranks.Step * link.Bond.LoadReduction) * Mathf.Clamp01(1f - link.Bond.Gentle / 100f)
			* Plugin.WearFactorAt(link.Tier, link.SlackUntil, item.m_quality); // nach einem Befehl die nachgegebene Länge (Gorduan: dann ×0,9)
		item.m_durability = Mathf.Max(0f, item.m_durability - loss);
		if (item.m_durability <= 0f)
		{
			Escape(link); // gerissen: das Tier ist entwischt
			return;
		}
		if (Time.time >= link.NextWearMessage)
		{
			link.NextWearMessage = Time.time + 4f;
			int now = Mathf.CeilToInt(item.m_durability);
			int max = Mathf.CeilToInt(item.GetMaxDurability());
			Gc.TopLeft(fighting ? GcLoc.Get("gcls_wear_fight", now, max, link.Tame.GetHoverName()) : GcLoc.Get("gcls_wear", now, max));
		}
	}

	// ------------------------------------------------------------------ Portale

	/// <summary>Der Halter geht durch ein Portal: Seine Tiere reisen mit.</summary>
	public static void BeginTravel(Player player)
	{
		if (!Plugin.PortalTravel.Value)
		{
			return;
		}
		int slot = 0;
		foreach (Link link in Active)
		{
			if (link.Holder == player)
			{
				link.Traveling = true;
				link.TravelSlot = slot++;
			}
		}
	}

	/// <summary>
	/// Läuft nach Player.UpdateTeleport (FixedUpdate). Sobald der Halter am Ziel steht, stehen die Tiere hinter ihm
	/// (Position und ZDO, damit sie mit ihm in den geladenen Bereich wechseln und nicht entladen werden). Solange das Ziel
	/// lädt, schweben sie wie der Halter; danach landen sie auf dem Boden.
	/// </summary>
	public static void UpdateTravel(Player player)
	{
		int count = 0;
		foreach (Link link in Active)
		{
			if (link.Traveling && link.Holder == player)
			{
				count++;
			}
		}
		if (count == 0)
		{
			return;
		}
		bool teleporting = player.m_teleporting;
		bool arrived = teleporting && player.m_teleportTimer > 2f; // ab hier steht der Halter am Ziel
		foreach (Link link in Active)
		{
			if (!link.Traveling || link.Holder != player)
			{
				continue;
			}
			if (link.Animal == null || link.View == null || !link.View.IsValid())
			{
				link.Traveling = false;
				continue;
			}
			if (teleporting && !arrived)
			{
				continue; // noch am Portal
			}
			Place(link, player, count, !teleporting);
			if (!teleporting)
			{
				link.Traveling = false;
				link.NextCheck = Time.time + Plugin.CheckInterval.Value;
			}
		}
	}

	/// <summary>Vor den Halter (in Blickrichtung beim Verlassen des Portals, hinter ihm steht das Portal), nebeneinander.</summary>
	private static void Place(Link link, Player holder, int count, bool land)
	{
		Transform at = holder.transform;
		Vector3 ahead = at.forward;
		ahead.y = 0f;
		ahead = ahead.sqrMagnitude > 0.001f ? ahead.normalized : Vector3.forward;
		Vector3 side = Vector3.Cross(Vector3.up, ahead);
		float offset = (link.TravelSlot - (count - 1) * 0.5f) * 1.6f;
		Vector3 position = at.position + ahead * (1.5f + Reach(link.Animal)) + side * offset;
		// Boden suchen, Strahl knapp unter üblicher Deckenhöhe beginnen (sonst landet das Tier im Haus auf dem Dach)
		if (land && ZoneSystem.instance != null && ZoneSystem.instance.FindFloor(position + Vector3.up * 0.8f, out float floor))
		{
			position.y = floor + 0.1f;
		}
		Character animal = link.Animal;
		animal.transform.position = position;
		animal.transform.rotation = Quaternion.LookRotation(ahead);
		Rigidbody? body = animal.m_body;
		if (body != null)
		{
			body.position = position;
			if (!body.isKinematic)
			{
				body.linearVelocity = Vector3.zero;
			}
		}
		link.View.GetZDO().SetPosition(position);
	}
}

/// <summary>Das Seil zwischen Hand und Tier (bei allen Spielern) und das Aufräumen alter Verbindungen beim Besitzer.</summary>
internal static class Rope
{
	private static readonly HashSet<Tameable> Known = new();
	private static readonly Dictionary<Tameable, LineRenderer> Lines = new();
	private static readonly Dictionary<Tameable, RopeView> Meshes = new();
	private static readonly Dictionary<Tameable, float> MissingSince = new();
	private static readonly Dictionary<int, Material> Materials = new();
	private static readonly List<Tameable> Dead = new();
	private static readonly Vector3[] Points = new Vector3[24];

	/// <summary>Seil aus Seilgliedern: eigenes Objekt ohne Elternteil (Ecken in Weltachsen, Lage = Seilanfang), aufgeräumt in Cleanup.</summary>
	private sealed class RopeView
	{
		internal GameObject? Go;
		internal MeshRenderer? Renderer;
		internal RopeMesh? Mesh;
		internal Vector3 Start;
		internal Vector3 End;
		internal float Sag = -1f;
		internal float BuiltAt;

		internal void Destroy()
		{
			Mesh?.Destroy();
			if (Go != null)
			{
				UnityEngine.Object.Destroy(Go); // hängt an keinem Tier: selbst wegräumen
			}
		}
	}

	/// <summary>Weiter weg als so viele Meter von der Kamera: kein Seil (wie Valheim kleine Dinge in der Ferne weglässt).</summary>
	private const float MeshRange = 60f;
	private static Shader? shader;
	private static bool noShader;
	private static bool reportedError;

	/// <summary>Tiere ohne Leine und Pfosten, schon aufgeräumt (bis sich das ändert).</summary>
	private static readonly HashSet<Tameable> Idle = new();

	public static void Register(Tameable tame)
	{
		if (!Gc.IsHeadless) // der Server zeichnet nichts, also auch nichts merken
		{
			Known.Add(tame);
		}
	}

	/// <summary>Alle geladenen zähmbaren Tiere (für die Pfosten).</summary>
	public static IEnumerable<Tameable> All => Known;

	public static void Tick()
	{
		Cleanup();
		foreach (Tameable tame in Known)
		{
			try
			{
				TickOne(tame);
			}
			catch (Exception e)
			{
				Hide(tame);
				if (!reportedError)
				{
					reportedError = true;
					Plugin.Log.LogWarning($"Tierleinen: Seil für {tame.name} konnte nicht gezeichnet werden: {e.Message}");
				}
			}
		}
	}

	/// <summary>Zerstörte Tiere aus allen Listen nehmen.</summary>
	private static void Cleanup()
	{
		Known.RemoveWhere(t => t == null);
		Idle.RemoveWhere(t => t == null);
		Dead.Clear();
		foreach (Tameable tame in Lines.Keys)
		{
			if (tame == null)
			{
				Dead.Add(tame!);
			}
		}
		foreach (Tameable tame in MissingSince.Keys)
		{
			if (tame == null)
			{
				Dead.Add(tame!);
			}
		}
		foreach (Tameable tame in Meshes.Keys)
		{
			if (tame == null)
			{
				Dead.Add(tame!);
			}
		}
		foreach (Tameable tame in Dead)
		{
			Lines.Remove(tame);
			MissingSince.Remove(tame);
			if (Meshes.TryGetValue(tame, out RopeView view))
			{
				view.Destroy();
				Meshes.Remove(tame);
			}
		}
		Posts.Cleanup();
	}

	private static void TickOne(Tameable tame)
	{
		ZNetView view = tame.m_nview;
		if (view == null || !view.IsValid())
		{
			Hide(tame);
			return;
		}
		ZDO zdo = view.GetZDO();
		if (!Posts.IsTied(zdo) && zdo.GetLong(Plugin.HolderHash) == 0L)
		{
			// weder angeleint noch angebunden (die meisten Tiere): nur beim Wechsel aufräumen (Review 27.10.2026: sonst jedes Bild
			// für jedes geladene Tier mehrere Listen-Abfragen)
			if (Idle.Add(tame))
			{
				Posts.NotTied(tame);
				Hide(tame);
				MissingSince.Remove(tame);
			}
			return;
		}
		Idle.Remove(tame);
		if (Posts.IsTied(zdo))
		{
			if (Plugin.On)
			{
				Posts.TickTied(tame, view, view.GetZDO());
			}
			else
			{
				Hide(tame);
				Posts.NotTied(tame); // kein Ziehen, Umherlaufen wieder normal
			}
			return;
		}
		Posts.NotTied(tame);
		long session = view.GetZDO().GetLong(Plugin.HolderHash);
		if (session == 0L)
		{
			Hide(tame);
			MissingSince.Remove(tame);
			return;
		}
		Player? holder = Links.FindBySession(session);
		if (view.IsOwner() && Stale(tame, view, holder, session))
		{
			Hide(tame);
			return;
		}
		Character? animal = tame.m_character;
		if (holder == null || animal == null)
		{
			Hide(tame);
			return;
		}
		Draw(tame, holder, animal, Plugin.TierAt(view.GetZDO().GetInt(Plugin.TierHash)));
	}

	/// <summary>Alte Verbindung (eigene ohne Leine, oder Halter seit 10 s nicht da, z. B. abgemeldet): beim Besitzer lösen.</summary>
	private static bool Stale(Tameable tame, ZNetView view, Player? holder, long session)
	{
		bool stale = false;
		long rider = view.GetZDO().GetLong(ZDOVars.s_user);
		if (session == Links.Session(Player.m_localPlayer))
		{
			stale = Links.ForTame(tame) == null;
		}
		else if (rider != 0L && Plugin.FindPlayer(rider) != null && (holder == null || rider != holder.GetPlayerID()))
		{
			stale = true; // jemand anderes reitet das Tier: die Leine des Halters gilt nicht mehr
		}
		else if (holder != null && Vector3.Distance(holder.transform.position, tame.transform.position) > 2f * Plugin.SlipFrom(view.GetZDO().GetFloat(Plugin.MaxLengthHash, Plugin.LongestMaxLength)))
		{
			stale = true; // Halter weit weg (z. B. Tier entladen, bevor die Leine sich lösen konnte)
		}
		else if (holder == null)
		{
			if (!MissingSince.TryGetValue(tame, out float since))
			{
				MissingSince[tame] = Time.time;
			}
			else
			{
				stale = Time.time - since > 10f;
			}
		}
		else
		{
			MissingSince.Remove(tame);
		}
		if (stale)
		{
			view.GetZDO().Set(Plugin.HolderHash, 0L);
			view.GetZDO().Set(Plugin.TierHash, 0);
			Links.ClearLengths(view.GetZDO());
			view.GetZDO().Set(Bonds.LightHash, 0f); // Leuchten und Seillänge gehören zur Leine
			MissingSince.Remove(tame);
		}
		return stale;
	}

	public static void Hide(Tameable tame)
	{
		if (Lines.TryGetValue(tame, out LineRenderer line) && line != null)
		{
			line.enabled = false;
		}
		if (Meshes.TryGetValue(tame, out RopeView view) && view.Renderer != null)
		{
			view.Renderer.enabled = false;
		}
	}

	private static void Draw(Tameable tame, Player holder, Character animal, Tier? tier)
	{
		// Hält der Halter eine Leine dieser Stufe in der Hand (rechts, wie eine Waffe), kommt das Seil aus der rechten Hand,
		// sonst aus der linken (bei allen Spielern gleich: das Item in der Hand steht in seiner Ausrüstung)
		VisEquipment? equipment = holder.m_visEquipment;
		Transform? hand = equipment == null ? null
			: tier != null && equipment.m_currentRightItemHash == tier.PrefabHash && equipment.m_rightHand != null ? equipment.m_rightHand
			: equipment.m_leftHand;
		DrawTo(tame, hand != null ? hand.position : holder.transform.position + Vector3.up * 1.1f, animal, tier);
	}

	/// <summary>Seil von start (Hand oder Pfostenknoten) zum Hals des Tiers.</summary>
	public static void DrawTo(Tameable tame, Vector3 start, Character animal, Tier? tier)
	{
		Vector3 head = animal.m_head != null ? animal.m_head.position : animal.GetTopPoint(); // nicht jedes Tier hat einen Kopf-Knochen
		Vector3 neck = animal.transform.position + Vector3.up * (head.y - animal.transform.position.y) * 0.8f;
		Vector3 end = Vector3.Lerp(neck, head, 0.5f);
		float distance = Vector3.Distance(start, end);
		float max = tame.m_nview != null && tame.m_nview.IsValid() ? tame.m_nview.GetZDO().GetFloat(Plugin.MaxLengthHash, 0f) : 0f;
		if (max <= 0f)
		{
			max = tier != null ? Plugin.MaxLength(tier, 1) : Plugin.Tiers[0].MaxLength; // noch nicht übertragen: Grundlänge der Stufe
		}
		// locker bis zum freien Bereich (90 % der Höchstlänge), dann fast gerade; durchhängen höchstens bis zum Boden
		float slack = Mathf.Max(0f, Plugin.FreeUntil(max) + Links.Reach(animal) - distance);
		float ground = Mathf.Max(0f, Mathf.Min(start.y, end.y) - animal.transform.position.y);
		float sag = 0.04f + Mathf.Min(slack * 0.45f, ground);
		for (int i = 0; i < Points.Length; i++)
		{
			float t = i / (float)(Points.Length - 1);
			Points[i] = Vector3.Lerp(start, end, t) + Vector3.down * (4f * t * (1f - t) * sag);
		}
		if (DrawMesh(tame, tier, start, end, sag))
		{
			if (Lines.TryGetValue(tame, out LineRenderer old) && old != null)
			{
				old.enabled = false;
			}
			return;
		}
		LineRenderer? line = GetLine(tame, tier);
		if (line == null)
		{
			return;
		}
		line.positionCount = Points.Length;
		line.SetPositions(Points);
		float light = Mathf.Clamp(RenderSettings.ambientLight.maxColorComponent * 1.6f, 0.2f, 1f); // nachts nicht leuchten
		line.startColor = line.endColor = new Color(light, light, light, 1f);
		line.enabled = true;
	}

	/// <summary>
	/// Das Seil aus Seilgliedern (RopeMesh) mit Valheims Karren-Seil in der Leinenfarbe, beleuchtet wie alles andere. Neu
	/// gerechnet nur, wenn sich Anfang, Ende oder Durchhang um mehr als 2 mm ändern. false: kein Modell, dann die alte Linie.
	/// </summary>
	private static bool DrawMesh(Tameable tame, Tier? tier, Vector3 start, Vector3 end, float sag)
	{
		if (RopeModel.Tile == null)
		{
			return false;
		}
		Material? material = RopeLook.Rope(tier);
		if (material == null)
		{
			return false;
		}
		if (!Meshes.TryGetValue(tame, out RopeView view) || view.Go == null || view.Renderer == null || view.Mesh == null)
		{
			view?.Destroy();
			GameObject go = new("GC_LeashRope");
			view = new RopeView { Go = go, Mesh = new RopeMesh("GC_LeashRope") };
			go.AddComponent<MeshFilter>().sharedMesh = view.Mesh.Mesh;
			view.Renderer = go.AddComponent<MeshRenderer>();
			view.Renderer.shadowCastingMode = ShadowCastingMode.On;
			Meshes[tame] = view;
		}
		if (view.Renderer.sharedMaterial != material)
		{
			view.Renderer.sharedMaterial = material;
		}
		// Neu rechnen nur, wenn man es sieht: Toleranz wächst mit dem Abstand zur Kamera (2 mm + 1 mm je Meter), weit weg aus,
		// unsichtbar höchstens alle 0,5 s (das Seilende hängt am Kopf und wackelt mit jeder Animation)
		Camera? cam = global::Utils.GetMainCamera();
		float distance = cam != null ? Vector3.Distance(cam.transform.position, start) : 0f;
		if (distance > MeshRange)
		{
			view.Renderer.enabled = false;
			return true;
		}
		float tolerance = 0.002f + 0.001f * distance;
		bool moved = (start - view.Start).sqrMagnitude > tolerance * tolerance || (end - view.End).sqrMagnitude > tolerance * tolerance || Mathf.Abs(sag - view.Sag) > tolerance;
		// sichtbar ab 10 m höchstens alle 0,15 s (Review 27.10.2026: ein Glied hat 24 Ecken, 10 m Seil etwa 4.000)
		float wait = !view.Renderer.isVisible ? 0.5f : distance > 10f ? 0.15f : 0f;
		if (moved && view.Renderer.enabled && Time.time - view.BuiltAt < wait)
		{
			moved = false;
		}
		if (moved || !view.Renderer.enabled)
		{
			view.Go.transform.SetPositionAndRotation(start, Quaternion.identity);
			if (!view.Mesh.Build(Points, Points.Length, start))
			{
				view.Renderer.enabled = false;
				return true;
			}
			view.Start = start;
			view.End = end;
			view.Sag = sag;
			view.BuiltAt = Time.time;
		}
		view.Renderer.enabled = true;
		return true;
	}

	private static LineRenderer? GetLine(Tameable tame, Tier? tier)
	{
		Material? material = MaterialFor(tier);
		if (material == null)
		{
			return null;
		}
		if (!Lines.TryGetValue(tame, out LineRenderer line) || line == null)
		{
			GameObject go = new("GC_LeashRope");
			go.transform.SetParent(tame.transform, false);
			line = go.AddComponent<LineRenderer>();
			line.useWorldSpace = true;
			line.widthMultiplier = 0.035f;
			line.numCapVertices = 2;
			line.shadowCastingMode = ShadowCastingMode.Off;
			line.receiveShadows = false;
			line.enabled = false;
			Lines[tame] = line;
		}
		if (line.sharedMaterial != material)
		{
			line.sharedMaterial = material;
		}
		return line;
	}

	/// <summary>Ein Material pro Leinenfarbe (die Farbe steckt im Material, damit sie auch ohne Vertex-Farben stimmt).</summary>
	internal static Material? MaterialFor(Tier? tier)
	{
		int key = tier?.Level ?? 0;
		if (Materials.TryGetValue(key, out Material existing) && existing != null)
		{
			return existing;
		}
		if (shader == null && !noShader)
		{
			shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
			if (shader == null)
			{
				noShader = true;
				Plugin.Log.LogWarning("Tierleinen: kein passender Shader gefunden, das Seil wird nicht gezeichnet.");
			}
		}
		if (shader == null)
		{
			return null;
		}
		Material material = new(shader) { color = tier?.Color ?? new Color(0.55f, 0.36f, 0.22f) };
		Materials[key] = material;
		return material;
	}
}

/// <summary>
/// Anbinden an Pfosten, Zäune und Mauern (Liste PostPieces), wie der Leinenknoten in Minecraft.
///
/// Anbinden: E am Bauteil bindet alle deine angeleinten Tiere in Reichweite an, eine Leine aus der Schnellleiste nur ihr
/// Tier. Die Leine verlässt dabei das Inventar und wird ein Knoten am Bauteil: ein kleines eigenes Bauteil
/// (GC_LeashKnot) mit der Haltbarkeit der Leine als Lebenspunkten. Am Knoten verliert die Leine keine Haltbarkeit durch
/// Abstand oder Kampf, nur durch direkten Schaden (Waffen, Gegner, Feuer), und lässt sich mit dem Hammer reparieren wie
/// jedes Bauteil. Wird der Knoten zerstört, ist das Tier frei und die Leine liegt kaputt am Boden (an der Werkbank
/// reparieren). Verschwindet das Bauteil, fällt die Leine mit ihrer Haltbarkeit herunter.
///
/// Aufnehmen: E am Knoten oder am angebundenen Tier nimmt die Leine zurück ins Inventar, das Tier bleibt an der Leine und
/// folgt dir. E am Bauteil nimmt die einzige Leine dort, bei mehreren erscheint ein Auswahlmenü.
///
/// Angebundene Tiere bleiben im Umkreis der Leinenlänge (Ziehen wie beim Halter). Tier und Knoten finden sich über die ID
/// der Leine, das Bauteil über seine Position; so übersteht alles Neustarts. Gerechnet wird beim Besitzer von Tier bzw.
/// Knoten.
/// </summary>
internal static class Posts
{
	internal const string DefaultPieces = "*pole*, *fence*, *pillar*, *wall*";
	internal const string KnotPrefabName = "GC_LeashKnot";
	internal static readonly int RadiusHash = "gc_leash_radius".GetStableHashCode();

	internal static GameObject? KnotPrefab;

	/// <summary>Angebundene Tiere, die wir besitzen: Tier → Knoten (für das Ziehen).</summary>
	internal static readonly Dictionary<Character, Vector3> Pulling = new();
	/// <summary>Zuletzt gesehene angebundene Tiere je Leinen-ID (für den Knoten: hängt noch ein Tier daran?).</summary>
	internal static readonly Dictionary<string, float> SeenTied = new();
	private static readonly Dictionary<Tameable, float> MissingSince = new();
	private static readonly Dictionary<Tameable, float> MissingCheck = new();
	private static readonly Dictionary<BaseAI, float> OriginalRange = new();
	private static readonly Dictionary<string, bool> PieceAllowed = new(StringComparer.OrdinalIgnoreCase);
	private static readonly List<Tameable> DeadTames = new();
	private static readonly List<Character> DeadAnimals = new();
	private static readonly List<BaseAI> DeadAis = new();
	private static List<string>? patterns;

	public static bool IsTied(ZDO zdo) => zdo.GetInt(Plugin.TiedHash) == 1;

	/// <summary>Neue Regeln für Bauteile (gilt für Bauteile, die ab jetzt geladen werden).</summary>
	public static void RulesChanged()
	{
		patterns = null;
		PieceAllowed.Clear();
	}

	// ------------------------------------------------------------------ Knoten als Bauteil

	/// <summary>Knoten-Bauteil aus dem Holzpfosten ableiten: unsichtbar bis auf das Seil, kleiner Kollisionskörper, nicht abbaubar, nichts zurück.</summary>
	public static void CreateKnotPrefab()
	{
		GameObject? knot = PrefabManager.Instance.CreateClonedPrefab(KnotPrefabName, "wood_pole");
		if (knot == null)
		{
			Plugin.Log.LogWarning("Tierleinen: wood_pole nicht gefunden, Anbinden an Pfosten ist aus.");
			return;
		}
		for (int i = knot.transform.childCount - 1; i >= 0; i--)
		{
			UnityEngine.Object.DestroyImmediate(knot.transform.GetChild(i).gameObject);
		}
		foreach (Collider collider in knot.GetComponents<Collider>())
		{
			UnityEngine.Object.DestroyImmediate(collider);
		}
		knot.layer = LayerMask.NameToLayer("piece");
		BoxCollider box = knot.AddComponent<BoxCollider>();
		box.size = new Vector3(0.32f, 0.14f, 0.32f);
		Piece piece = knot.GetComponent<Piece>();
		piece.m_name = "$gc_leash_knot";
		piece.m_description = "$gc_leash_knot_desc";
		piece.m_resources = Array.Empty<Piece.Requirement>(); // abreißen gibt nichts zurück, die Leine fällt über den Knoten
		piece.m_canBeRemoved = false; // lösen mit E, nicht mit dem Hammer
		piece.m_craftingStation = null; // Reparieren ohne Werkbank in der Nähe
		piece.m_comfort = 0;
		WearNTear wear = knot.GetComponent<WearNTear>();
		wear.m_new = null;
		wear.m_worn = null;
		wear.m_broken = null;
		wear.m_wet = null;
		wear.m_snow = null;
		wear.m_snowWorn = null;
		wear.m_snowBroken = null;
		wear.m_fragmentRoots = null;
		wear.m_autoCreateFragments = false;
		wear.m_noRoofWear = false; // kein Verschleiß durch Wetter
		wear.m_noSupportWear = false; // braucht keine Stütze
		wear.m_supports = false;
		wear.m_ashDamageImmune = true; // Asche-Welten: der Knoten soll nicht verglühen
		wear.m_health = 10f; // je Knoten auf die Haltbarkeit seiner Leinenstufe gesetzt
		knot.AddComponent<LeashKnot>();
		PrefabManager.Instance.AddPrefab(knot);
		KnotPrefab = knot;
	}

	// ------------------------------------------------------------------ Bauteile zum Anbinden

	/// <summary>Piece.Awake: Pfosten, Zäune und Mauern bekommen die Anbinde-Funktion (nach dem Abgleich mit dem Server, gilt also dessen Liste).</summary>
	public static void AttachTo(Piece piece)
	{
		if (piece.m_nview == null || piece.m_nview.GetZDO() == null || piece.GetComponent<LeashKnot>() != null)
		{
			return; // Bauvorschau oder selbst ein Knoten
		}
		string name = Ranks.PrefabName(piece.gameObject);
		if (!PieceAllowed.TryGetValue(name, out bool allowed))
		{
			patterns ??= Plugin.PostPieces.Value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
			allowed = patterns.Any(p => Ranks.Matches(p, name));
			if (allowed && (piece.GetComponentInChildren<Interactable>() != null || piece.GetComponentInChildren<Hoverable>() != null))
			{
				allowed = false; // eigene Benutzen-Funktion (z. B. Tor, Wandfackel) hat Vorrang
				Plugin.Log.LogInfo($"Tierleinen: {name} hat schon eine eigene Benutzen-Funktion, dort kein Anbinden.");
			}
			PieceAllowed[name] = allowed;
		}
		if (allowed && piece.GetComponent<LeashPost>() == null)
		{
			piece.gameObject.AddComponent<LeashPost>();
		}
	}

	private static readonly Collider[] Overlap = new Collider[128];
	private static int pieceMask = -1;

	/// <summary>Steht an der Position (noch) ein Bauteil? Unabhängig davon, ob es hier die Anbinde-Funktion hat.</summary>
	public static bool PieceAt(Vector3 anchor)
	{
		if (LeashPost.Find(anchor) != null)
		{
			return true;
		}
		if (pieceMask < 0)
		{
			pieceMask = LayerMask.GetMask("piece", "piece_nonsolid");
		}
		int count = Physics.OverlapSphereNonAlloc(anchor + Vector3.up * 0.5f, 1.5f, Overlap, pieceMask, QueryTriggerInteraction.Ignore);
		for (int i = 0; i < count; i++)
		{
			Piece? piece = Overlap[i] != null ? Overlap[i].GetComponentInParent<Piece>() : null;
			if (piece != null && piece.GetComponent<LeashKnot>() == null && (piece.transform.position - anchor).sqrMagnitude <= 0.09f)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>Knoten an diesem Bauteil (über die gespeicherte Position).</summary>
	public static List<LeashKnot> KnotsAt(LeashPost post)
	{
		List<LeashKnot> result = new();
		Vector3 anchor = post.transform.position;
		foreach (LeashKnot knot in LeashKnot.ById.Values)
		{
			if (knot != null && knot.Ready && (knot.Anchor - anchor).sqrMagnitude <= 0.09f)
			{
				result.Add(knot);
			}
		}
		return result;
	}

	/// <summary>Das angebundene Tier zu einer Leinen-ID (nur geladene).</summary>
	public static Tameable? AnimalFor(string id)
	{
		foreach (Tameable tame in Rope.All)
		{
			if (tame != null && tame.m_nview != null && tame.m_nview.IsValid() && IsTied(tame.m_nview.GetZDO()) && tame.m_nview.GetZDO().GetString(Plugin.LeashIdHash) == id)
			{
				return tame;
			}
		}
		return null;
	}

	// ------------------------------------------------------------------ Bedienung

	public static string HoverText(LeashPost post)
	{
		Player? player = Player.m_localPlayer;
		if (!Plugin.On || player == null)
		{
			return "";
		}
		// Leine in der Hand: nur ihr Tier (eine freie Leine bindet nichts an); ohne Leine in der Hand alle deine Tiere
		Link? held = HeldLink(player);
		int mine = held != null ? 1 : Control.Held(player) != null ? 0 : Links.Active.Count(l => l.Holder == player && !l.Traveling);
		List<LeashKnot> knots = KnotsAt(post);
		if (mine == 0 && knots.Count == 0)
		{
			return ""; // ein Bauteil wie jedes andere
		}
		string text = post.GetHoverName();
		if (knots.Count > 0)
		{
			text += "\n" + GcLoc.Get("gcls_post_list", string.Join(", ", knots.Select(k => k.AnimalName())));
		}
		if (held != null)
		{
			text += "\n" + Localization.instance.Localize(GcLoc.Get("gcls_post_tie_one", held.Tame != null ? held.Tame.GetHoverName() : ""));
		}
		else if (mine > 0)
		{
			text += "\n" + Localization.instance.Localize(GcLoc.Get("gcls_post_tie", mine));
		}
		else if (knots.Count == 1)
		{
			text += "\n" + Localization.instance.Localize(GcLoc.Get("gcls_hover_untie"));
		}
		else
		{
			text += "\n" + Localization.instance.Localize(GcLoc.Get("gcls_post_choose", knots.Count));
		}
		return text;
	}

	/// <summary>Die Leine in der Hand, wenn sie gerade ein Tier hält (sonst null).</summary>
	private static Link? HeldLink(Player player)
	{
		ItemDrop.ItemData? item = Control.Held(player);
		Link? link = item != null ? Links.ForItem(item) : null;
		return link != null && link.Holder == player && !link.Traveling ? link : null;
	}

	/// <summary>
	/// E am Bauteil (Gorduan: „mit E an einen Pfahl" entleinen): Mit einer Leine in der Hand nur deren Tier anbinden, ohne alle
	/// deine angeleinten Tiere. Lässt sich keines anbinden (zu weit, Ward), oder hast du keines an der Leine: die eine
	/// Leine dort aufnehmen oder bei mehreren die Auswahl zeigen.
	/// </summary>
	public static bool Interact(Player player, LeashPost post)
	{
		if (!Plugin.On)
		{
			return false;
		}
		Link? held = HeldLink(player);
		if (held != null)
		{
			string name = held.Tame != null ? held.Tame.GetHoverName() : "";
			if (Tie(held, post))
			{
				player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_tied", name));
			}
			return true; // zu weit: Tie hat es gemeldet
		}
		List<Link> mine = Control.Held(player) != null ? new List<Link>() : Links.Active.Where(l => l.Holder == player && !l.Traveling).ToList();
		if (mine.Count > 0)
		{
			List<string> tied = new();
			foreach (Link link in mine)
			{
				string name = link.Tame != null ? link.Tame.GetHoverName() : "";
				if (Tie(link, post))
				{
					tied.Add(name);
				}
			}
			if (tied.Count > 0)
			{
				player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_tied", string.Join(", ", tied)));
				return true;
			}
		}
		List<LeashKnot> knots = KnotsAt(post);
		if (knots.Count == 0)
		{
			return false; // still: E an Mauern soll nicht ständig etwas melden
		}
		if (knots.Count == 1)
		{
			return TakeKnot(player, knots[0]);
		}
		LeashMenu.Open(post);
		return true;
	}

	/// <summary>Ein Tier vom Halter an das Bauteil: Leine aus dem Inventar wird ein Knoten am Bauteil (mit allen Daten der Leine).</summary>
	private static bool Tie(Link link, LeashPost post)
	{
		Vector3 anchor = post.transform.position;
		if (KnotPrefab == null || link.Traveling || link.Animal == null || link.Tame == null || link.View == null || !link.View.IsValid() || !PrivateArea.CheckAccess(anchor))
		{
			return false;
		}
		// Nah genug: höchstens die Höchstlänge der Leine selbst (am Knoten wirkt keine Leinenreichweite), sonst risse der
		// Knoten das Tier mit 12 m/s heran. Und nicht weit unter oder über dem Knoten (höchstens 4 m Höhenunterschied):
		// dort hinge die Leine nur, das Tier drückte gegen die Wand darunter
		// Gorduan: Eine mit dem Mausrad gekürzte Leine bleibt auch am Pfosten so kurz (Höchstlänge im selben Verhältnis)
		float min = Plugin.SetLength(link.Tier, link.Item, link.Item.m_quality);
		float max = Plugin.MaxLength(link.Tier, link.Item.m_quality) * Plugin.LengthRatio(link.Tier, link.Item, link.Item.m_quality);
		float height = Mathf.Abs(post.Knot.y - link.Animal.transform.position.y) - Links.HeightSlack;
		if (Links.Span(post.Knot, link.Animal) > max || height > 2.5f)
		{
			link.Holder.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_tie_far", link.Tame.GetHoverName(), max.ToString("0.#")));
			return false;
		}
		Inventory? inventory = LeashBar.InventoryWith(link.Holder, link.Item); // Schnellleiste oder Leinenleiste
		if (inventory == null)
		{
			return false;
		}
		// Knoten übereinander: der unterste freie Platz am Bauteil
		List<LeashKnot> existing = KnotsAt(post);
		int slot = 0;
		while (slot < 12 && existing.Any(k => Mathf.Abs(k.transform.position.y - (post.Knot.y + 0.13f * slot)) < 0.06f))
		{
			slot++;
		}
		Vector3 position = post.Knot + Vector3.up * (0.13f * slot);
		// Review 27.10.2026 (nicht atomar): Leine vor dem Knoten speichern; geht danach etwas schief, verschwindet der Knoten
		// wieder und die Leine bleibt in der Hand. Sonst warf der Knoten ohne Tier später eine zweite Leine mit derselben ID ab.
		byte[] itemData = SaveItem(link.Item);
		GameObject go = UnityEngine.Object.Instantiate(KnotPrefab, position, Quaternion.identity);
		ZNetView? knotView = go.GetComponent<ZNetView>();
		if (knotView == null || !knotView.IsValid())
		{
			UnityEngine.Object.Destroy(go);
			return false;
		}
		ZDO? tiedZdo = null;
		try
		{
			ZDO knotZdo = knotView.GetZDO();
			knotZdo.Set(Plugin.LeashIdHash, link.ItemId);
			knotZdo.Set(Plugin.TierHash, link.Tier.Level);
			knotZdo.Set(Plugin.PostHash, anchor);
			knotZdo.Set(RadiusHash, post.Radius);
			knotZdo.Set(ItemDataHash, itemData); // ganze Leine (Hersteller, Schlösser, Verzauberung …)
			knotZdo.Set(MaxHealthHash, link.Item.GetMaxDurability()); // mit Verzauberung „Haltbarkeit"
			knotZdo.Set(UnbreakableHash, link.Bond.Unbreakable ? 1 : 0);
			knotZdo.Set(DamageFactorHash, Plugin.KnotDamageFactor(link.Tier, link.Item, link.Item.m_quality)); // gekürzt: weniger Schaden am Knoten
			knotZdo.Set(ZDOVars.s_health, Mathf.Clamp(link.Item.m_durability, 0.01f, link.Item.GetMaxDurability()));
			if (go.GetComponent<Piece>() is Piece knotPiece)
			{
				knotPiece.m_creator = link.Holder.GetPlayerID(); // gilt als vom Spieler gebaut (ohne Plattform-ID, die der Server nicht braucht)
				knotZdo.Set(ZDOVars.s_creator, knotPiece.m_creator);
			}
			go.GetComponent<LeashKnot>()?.Setup();

			if (!link.View.IsOwner())
			{
				link.View.ClaimOwnership();
			}
			ZDO zdo = link.View.GetZDO();
			tiedZdo = zdo;
			zdo.Set(Plugin.TiedHash, 1);
			zdo.Set(Plugin.LeashIdHash, link.ItemId);
			zdo.Set(Plugin.PostHash, anchor);
			zdo.Set(Plugin.TierHash, link.Tier.Level);
			zdo.Set(Plugin.HolderHash, 0L);
			zdo.Set(ZDOVars.s_follow, ""); // Valheims gespeichertes „Folgen" würde das Tier sonst jedes Bild wieder losschicken
			// Längen der Leine (Stufe und Qualität); angebunden wirkt keine Verzauberung, also ohne „Leinenreichweite"
			Links.WriteLengths(zdo, min, max);
			Links.WriteBonus(zdo, Plugin.LeashBonus(link.Tier, link.Item.m_quality)); // Gorduan: auch am Pfosten mehr Leben und Schaden
			Bonds.ClearLight(link.View);
			if (link.Holder.IsItemEquiped(link.Item))
			{
				link.Holder.UnequipItem(link.Item, false); // RemoveItem legt nichts ab: sonst bliebe sie (unsichtbar) in der Hand
			}
			inventory.RemoveItem(link.Item); // die Leine hängt jetzt am Bauteil
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"Tierleinen: Anbinden abgebrochen, die Leine bleibt in der Hand: {e}");
			ZNetScene.instance.Destroy(go);
			if (tiedZdo != null)
			{
				tiedZdo.Set(Plugin.TiedHash, 0);
				tiedZdo.Set(Plugin.LeashIdHash, "");
			}
			return false;
		}
		Links.Forget(link);
		Hold(link.Tame, anchor);
		SeenTied[link.ItemId] = Time.time;
		return true;
	}

	/// <summary>E am angebundenen Tier: dessen Leine aufnehmen. Fehlt der Knoten bei geladenem Bereich, wird das Tier frei.</summary>
	public static bool Take(Player player, Tameable tame)
	{
		ZNetView view = tame.m_nview;
		if (view == null || !view.IsValid() || !IsTied(view.GetZDO()))
		{
			return false;
		}
		string id = view.GetZDO().GetString(Plugin.LeashIdHash);
		if (!LeashKnot.ById.TryGetValue(id, out LeashKnot knot) || knot == null || !knot.Ready)
		{
			Vector3 anchor = view.GetZDO().GetVec3(Plugin.PostHash, tame.transform.position);
			if (Loaded(anchor, 4f))
			{
				FreeAnimal(id); // Knoten ist weg (z. B. vom Server zerstört): Tier nicht ewig angebunden lassen
				player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_knot_gone_free", tame.GetHoverName()));
				return true;
			}
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_knot_missing"));
			return false;
		}
		return TakeKnot(player, knot);
	}

	internal const string RpcGive = "GC_LeashGive";
	internal const string RpcServerTake = "GC_LeashServerTake";
	internal static readonly int ItemDataHash = "gc_leash_data".GetStableHashCode();
	internal static readonly int MaxHealthHash = "gc_leash_maxdura".GetStableHashCode();
	internal static readonly int UnbreakableHash = "gc_leash_unbreakable".GetStableHashCode();
	/// <summary>Im Knoten: Schadensfaktor (Gorduan: eine gekürzte Leine nimmt am Pfosten bis zu 50 % weniger Schaden), fehlt = 1.</summary>
	internal static readonly int DamageFactorHash = "gc_leash_knotfactor".GetStableHashCode();

	/// <summary>Laufende Anfragen je Leinen-ID (Zeitpunkt), damit doppeltes E nichts doppelt anfragt und freie Plätze reichen.</summary>
	private static readonly Dictionary<string, float> Requested = new();

	/// <summary>Auf dem Server: schon vergebene Knoten (ZDOID, Zeitpunkt).</summary>
	private static readonly Dictionary<ZDOID, float> Taken = new();

	/// <summary>Bei jedem Spielstart (Game.Start): Anfrage beim Server und Antwort registrieren.</summary>
	public static void RegisterRpcs()
	{
		Requested.Clear();
		Taken.Clear();
		RideTakes.Clear();
		if (ZRoutedRpc.instance == null)
		{
			return;
		}
		ZRoutedRpc.instance.Register<ZDOID>(RpcServerTake, RPC_ServerTake);
		ZRoutedRpc.instance.Register<ZPackage>(RpcGive, RPC_Give);
		ZRoutedRpc.instance.Register<ZPackage>(RpcServerDrop, RPC_ServerDrop);
		ZRoutedRpc.instance.Register<ZPackage>(RpcDropped, RPC_Dropped);
	}

	internal const string RpcServerDrop = "GC_LeashServerDrop";
	internal const string RpcDropped = "GC_LeashDropped";

	/// <summary>
	/// Ein Knoten fällt herunter (Bauteil weg, Tier weg, durch Schaden zerstört). Auch hier entscheidet der Server, damit
	/// nicht gleichzeitig jemand die Leine aufnimmt und sie zusätzlich am Boden liegt. Die Anfrage trägt alle Daten der
	/// Leine, falls der Knoten beim Server schon verschwunden ist; destroy: der Server entfernt den Knoten selbst.
	/// </summary>
	internal static void RequestDrop(LeashKnot knot, float durability, string messageKey, bool destroy)
	{
		if (knot.View == null || !knot.View.IsValid() || ZRoutedRpc.instance == null)
		{
			return;
		}
		ZDO zdo = knot.View.GetZDO();
		ZPackage request = new();
		request.Write(zdo.m_uid);
		request.Write(destroy);
		request.Write(knot.Id);
		request.Write(zdo.GetInt(Plugin.TierHash));
		request.Write(durability);
		request.Write(knot.transform.position);
		request.Write(zdo.GetByteArray(ItemDataHash) ?? Array.Empty<byte>());
		request.Write(messageKey);
		request.Write(AnimalFor(knot.Id)?.GetHoverName() ?? "");
		ZRoutedRpc.instance.InvokeRoutedRPC(RpcServerDrop, request); // an den Server (auch beim Host)
	}

	/// <summary>Auf dem Server: Abwurf nur, wenn den Knoten nicht schon jemand aufgenommen hat (gleiche Liste wie beim Aufnehmen).</summary>
	private static void RPC_ServerDrop(long sender, ZPackage request)
	{
		if (ZNet.instance == null || !ZNet.instance.IsServer())
		{
			return;
		}
		ZDOID knotId = request.ReadZDOID();
		bool destroy = request.ReadBool();
		bool granted = !Taken.ContainsKey(knotId);
		if (granted)
		{
			Taken[knotId] = Time.time;
			ZDO? zdo = destroy ? ZDOMan.instance.GetZDO(knotId) : null;
			if (zdo != null && zdo.GetPrefab() == KnotPrefabName.GetStableHashCode())
			{
				zdo.SetOwner(ZDOMan.GetSessionID()); // nur der Besitzer darf ein ZDO löschen
				ZDOMan.instance.DestroyZDO(zdo);
			}
		}
		ZPackage reply = new();
		reply.Write(granted);
		reply.Write(request);
		ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcDropped, reply);
	}

	/// <summary>Antwort an den Besitzer des Knotens: Leine mit allen Daten fallen lassen, Tier frei, Meldung in der Nähe.</summary>
	private static void RPC_Dropped(long sender, ZPackage reply)
	{
		bool granted = reply.ReadBool();
		ZPackage request = reply.ReadPackage();
		if (!granted)
		{
			return; // jemand hat die Leine im selben Moment aufgenommen: sie gehört ihm
		}
		request.ReadZDOID();
		request.ReadBool();
		string id = request.ReadString();
		int tierLevel = request.ReadInt();
		float durability = request.ReadSingle();
		Vector3 position = request.ReadVector3();
		byte[] data = request.ReadByteArray();
		string messageKey = request.ReadString();
		string name = request.ReadString();
		Tier? tier = Plugin.TierAt(tierLevel);
		ItemDrop.ItemData? item = LoadItem(data) ?? (tier?.Prefab != null ? MakeLeash(tier, durability, id) : null);
		if (item != null)
		{
			item.m_durability = Mathf.Clamp(durability, 0f, item.GetMaxDurability());
			item.m_customData[Plugin.KeyItemId] = id;
			ItemDrop.DropItem(item, 1, position + Vector3.up * 0.3f, Quaternion.identity);
		}
		FreeAnimal(id);
		Player? local = Player.m_localPlayer;
		if (local != null && Vector3.Distance(local.transform.position, position) < 30f)
		{
			Gc.TopLeft(GcLoc.Get(messageKey, name));
		}
	}

	/// <summary>
	/// Leine vom Knoten zurück in die Schnellleiste, das Tier bleibt an der Leine und folgt dem Spieler. Der Server
	/// entscheidet: Er entfernt den Knoten und schickt die Leine genau einem Spieler, auch wenn zwei gleichzeitig E
	/// drücken oder der Knoten gerade keinen Besitzer hat (sonst gäbe es die Leine doppelt).
	/// </summary>
	public static bool TakeKnot(Player player, LeashKnot knot)
	{
		if (!knot.Ready || knot.Released || knot.View == null || !knot.View.IsValid() || !PrivateArea.CheckAccess(knot.transform.position))
		{
			return false;
		}
		if (Requested.TryGetValue(knot.Id, out float since) && Time.time - since < 5f)
		{
			return false; // Anfrage läuft schon
		}
		int pending = Requested.Values.Count(t => Time.time - t < 5f);
		if (LeashBar.FreePlaces(player) <= pending) // Leinenleiste und eine Leine in der Schnellleiste (Gorduan, 24.10.2026)
		{
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_no_hotbar"));
			return false;
		}
		Requested[knot.Id] = Time.time;
		ZRoutedRpc.instance.InvokeRoutedRPC(RpcServerTake, knot.View.GetZDO().m_uid); // an den Server (auch beim Host)
		return true;
	}

	/// <summary>Freie Plätze in der Schnellleiste (Tasten 1–8, oberste Inventarreihe).</summary>
	internal static int FreeHotbarSlots(Inventory inventory)
	{
		int free = 0;
		for (int x = 0; x < Math.Min(8, inventory.GetWidth()); x++)
		{
			if (inventory.GetItemAt(x, 0) == null)
			{
				free++;
			}
		}
		return free;
	}

	/// <summary>Auf dem Server: Knoten genau einmal vergeben, entfernen und die Leine samt Daten zurückschicken.</summary>
	private static void RPC_ServerTake(long sender, ZDOID knotId)
	{
		if (ZNet.instance == null || !ZNet.instance.IsServer())
		{
			return;
		}
		foreach (ZDOID old in Taken.Where(e => Time.time - e.Value > 60f).Select(e => e.Key).ToList())
		{
			Taken.Remove(old);
		}
		ZDO? zdo = ZDOMan.instance.GetZDO(knotId);
		if (zdo != null && zdo.GetPrefab() != KnotPrefabName.GetStableHashCode())
		{
			zdo = null; // nur Knoten: angebundene Tiere tragen dieselbe Leinen-ID und dürfen nie gelöscht werden
		}
		string id = zdo != null ? zdo.GetString(Plugin.LeashIdHash) : "";
		ZPackage reply = new();
		if (zdo == null || id.Length == 0 || Taken.ContainsKey(knotId))
		{
			reply.Write(id);
			reply.Write(0);
			reply.Write(-1f); // abgelehnt
			reply.Write(Vector3.zero);
			reply.Write(Array.Empty<byte>());
			ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcGive, reply);
			return;
		}
		Taken[knotId] = Time.time;
		int tier = zdo.GetInt(Plugin.TierHash);
		float durability = zdo.GetFloat(ZDOVars.s_health, Plugin.TierAt(tier) is Tier t ? Plugin.MaxDurability(t) : 1f);
		Vector3 position = zdo.GetPosition();
		byte[] data = zdo.GetByteArray(ItemDataHash) ?? Array.Empty<byte>();
		zdo.SetOwner(ZDOMan.GetSessionID()); // nur der Besitzer darf ein ZDO löschen
		ZDOMan.instance.DestroyZDO(zdo);
		reply.Write(id);
		reply.Write(tier);
		reply.Write(durability);
		reply.Write(position);
		reply.Write(data);
		ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcGive, reply);
	}

	/// <summary>Antwort des Servers: Leine in die Schnellleiste, Tier an die Leine (Haltbarkeit &lt; 0: abgelehnt).</summary>
	private static void RPC_Give(long sender, ZPackage reply)
	{
		string id = reply.ReadString();
		int tierLevel = reply.ReadInt();
		float durability = reply.ReadSingle();
		Vector3 knotPosition = reply.ReadVector3();
		byte[] data = reply.ReadByteArray();
		Requested.Remove(id);
		if (durability >= 0f)
		{
			RideTakes.Remove(id); // Knoten ist weg: beim nächsten Anbinden mit derselben Leine wieder nehmen (Review 27.10.2026)
		}
		Player? player = Player.m_localPlayer;
		Tier? tier = Plugin.TierAt(tierLevel);
		if (durability < 0f)
		{
			player?.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_knot_busy"));
			return;
		}
		// der Server hat den Knoten schon entfernt: die Leine darf hier nicht verloren gehen
		ItemDrop.ItemData? loaded = LoadItem(data) ?? (tier?.Prefab != null ? MakeLeash(tier, durability, id) : null);
		if (loaded == null)
		{
			Plugin.Log.LogWarning($"Tierleinen: Leine {id} (Stufe {tierLevel}) vom Server ließ sich nicht wiederherstellen.");
			return;
		}
		ItemDrop.ItemData item = loaded;
		item.m_durability = Mathf.Clamp(durability, 0f, item.GetMaxDurability());
		item.m_customData[Plugin.KeyItemId] = id;
		if (player == null)
		{
			ItemDrop.DropItem(item, 1, knotPosition + Vector3.up * 0.5f, Quaternion.identity); // gerade tot o. Ä.: Leine bleibt am Pfosten liegen
			return;
		}
		Inventory inventory = player.GetInventory();
		bool inHotbar = LeashBar.Place(player, item); // erst die Leinenleiste, sonst die Schnellleiste (dort hält nur eine Leine ein Tier)
		if (!inHotbar)
		{
			// Schnellleiste inzwischen voll: Leine ins Inventar oder vor die Füße, das Tier ist frei
			if (!inventory.AddItem(item))
			{
				ItemDrop.DropItem(item, 1, player.transform.position + player.transform.forward * 0.6f + Vector3.up, Quaternion.identity);
			}
			FreeAnimal(id);
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_no_hotbar_free"));
			return;
		}
		Tameable? tame = AnimalFor(id);
		if (tame == null || tier == null)
		{
			if (tame != null)
			{
				FreeAnimal(id); // Stufe hier unbekannt: Leine behalten, Tier frei
			}
			player.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_took_leash")); // Tier nicht geladen: sein Besitzer gibt es frei
			return;
		}
		ZNetView view = tame.m_nview;
		if (!view.IsOwner())
		{
			view.ClaimOwnership();
		}
		view.GetZDO().Set(Plugin.TiedHash, 0);
		Unhold(tame);
		if (tame.m_character != null)
		{
			Pulling.Remove(tame.m_character);
		}
		Link? link = Links.Attach(player, tame, item, tier, null, true); // nach dem Lösen bleibt es, wo du es loslässt
		player.Message(MessageHud.MessageType.Center, link != null ? GcLoc.Get("gcls_untied", tame.GetHoverName()) : GcLoc.Get("gcls_took_leash"));
	}

	/// <summary>Eine Leine vollständig speichern (wie das Inventar: Haltbarkeit, Hersteller, Zusatzdaten wie Schlösser oder Verzauberungen).</summary>
	internal static byte[] SaveItem(ItemDrop.ItemData item)
	{
		Inventory inventory = new("gc_leash", null, 1, 1);
		ItemDrop.ItemData copy = item.Clone();
		copy.m_gridPos = Vector2i.zero;
		copy.m_equipped = false;
		inventory.m_inventory.Add(copy);
		ZPackage package = new();
		inventory.Save(package);
		return package.GetArray();
	}

	/// <summary>Gespeicherte Leine wiederherstellen (null, wenn nichts gespeichert oder das Prefab fehlt).</summary>
	internal static ItemDrop.ItemData? LoadItem(byte[]? data)
	{
		if (data == null || data.Length == 0)
		{
			return null;
		}
		try
		{
			Inventory inventory = new("gc_leash", null, 1, 1);
			inventory.Load(new ZPackage(data));
			List<ItemDrop.ItemData> items = inventory.GetAllItems();
			return items.Count > 0 ? items[0] : null;
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"Tierleinen: gespeicherte Leine nicht lesbar ({e.Message}), sie wird neu erzeugt.");
			return null;
		}
	}

	/// <summary>Ist der Bereich um diesen Punkt bei uns vollständig geladen (Mitte und vier Ecken im aktiven Bereich und bereit)?</summary>
	internal static bool Loaded(Vector3 center, float radius)
	{
		ZNetScene? scene = ZNetScene.instance;
		if (scene == null)
		{
			return false;
		}
		foreach (Vector3 offset in new[] { Vector3.zero, new Vector3(radius, 0f, radius), new Vector3(-radius, 0f, radius), new Vector3(radius, 0f, -radius), new Vector3(-radius, 0f, -radius) })
		{
			Vector3 point = center + offset;
			if (scene.OutsideActiveArea(point) || !scene.IsAreaReady(point))
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// Ein Dedicated Server rechnet nichts für Leinen. Besitzt er ein angebundenes Tier oder einen Knoten (rund um den
	/// Weltmittelpunkt behält er alles), übernimmt ein Spieler in der Nähe, damit Festhalten, Ziehen und Prüfungen laufen.
	/// </summary>
	internal static void ClaimFromServer(ZNetView view)
	{
		ZNet? net = ZNet.instance;
		Player? local = Player.m_localPlayer;
		if (view.IsOwner() || net == null || net.IsServer() || local == null)
		{
			return;
		}
		ZNetPeer? server = net.GetServerPeer();
		if (server != null && view.GetZDO().GetOwner() == server.m_uid && Vector3.Distance(local.transform.position, view.transform.position) < 30f)
		{
			view.ClaimOwnership();
		}
	}

	internal static ItemDrop.ItemData MakeLeash(Tier tier, float durability, string id)
	{
		ItemDrop.ItemData item = tier.Prefab!.m_itemData.Clone();
		item.m_dropPrefab = tier.Prefab.gameObject;
		item.m_stack = 1;
		item.m_durability = Mathf.Clamp(durability, 0f, Plugin.MaxDurability(tier));
		if (id.Length > 0)
		{
			item.m_customData[Plugin.KeyItemId] = id;
		}
		return item;
	}

	/// <summary>Tier frei (Knoten zerstört, abgebaut oder Bauteil weg). Nur beim Besitzer des Tiers wirksam.</summary>
	public static void FreeAnimal(string id)
	{
		Tameable? tame = AnimalFor(id);
		if (tame == null)
		{
			return; // nicht geladen: der Besitzer merkt beim Laden, dass der Knoten fehlt
		}
		ZNetView view = tame.m_nview;
		if (!view.IsOwner())
		{
			view.ClaimOwnership();
		}
		view.GetZDO().Set(Plugin.TiedHash, 0);
		Links.ClearLengths(view.GetZDO());
		Unhold(tame);
		if (tame.m_character != null)
		{
			Pulling.Remove(tame.m_character);
		}
		tame.GetComponent<BaseAI>()?.SetPatrolPoint(); // bleibt in der Nähe
	}

	// ------------------------------------------------------------------ Angebundene Tiere

	/// <summary>Am Bauteil halten: nicht folgen, Patrouillenpunkt am Bauteil, im Umkreis der Mindestlänge umherlaufen (frei bis 90 % der Höchstlänge).</summary>
	private static void Hold(Tameable tame, Vector3 anchor)
	{
		BaseAI? ai = tame.GetComponent<BaseAI>();
		if (ai == null)
		{
			return;
		}
		if (ai is MonsterAI monster && monster.GetFollowTarget() != null)
		{
			monster.SetFollowTarget(null);
		}
		ZDO? zdo = tame.m_nview != null && tame.m_nview.IsValid() ? tame.m_nview.GetZDO() : null;
		if (zdo != null && zdo.GetString(ZDOVars.s_follow).Length > 0)
		{
			zdo.Set(ZDOVars.s_follow, ""); // auch wenn jemand am angebundenen Tier „Folgen" gesetzt hat
		}
		if (!ai.GetPatrolPoint(out Vector3 point) || (point - anchor).sqrMagnitude > 0.25f)
		{
			ai.SetPatrolPoint(anchor);
		}
		if (!OriginalRange.ContainsKey(ai))
		{
			OriginalRange[ai] = ai.m_randomMoveRange;
		}
		ai.m_randomMoveRange = Mathf.Min(OriginalRange[ai], zdo != null ? TiedLengths(zdo).Min : 2f);
	}

	/// <summary>Mindest- und Höchstlänge der angebundenen Leine (beim Anbinden ins Tier geschrieben; sonst die Grundlängen der Stufe).</summary>
	internal static (float Min, float Max) TiedLengths(ZDO zdo)
	{
		float min = zdo.GetFloat(Plugin.MinLengthHash, 0f);
		float max = zdo.GetFloat(Plugin.MaxLengthHash, 0f);
		if (min <= 0f || max <= 0f)
		{
			Tier? tier = Plugin.TierAt(zdo.GetInt(Plugin.TierHash));
			min = tier != null ? Plugin.MinLength(tier, 1) : Plugin.Tiers[0].MinLength;
			max = tier != null ? Plugin.MaxLength(tier, 1) : Plugin.Tiers[0].MaxLength;
		}
		return (min, max);
	}

	/// <summary>Umherlaufen wieder wie vorher (nur auf diesem Rechner geändert).</summary>
	private static void Unhold(Tameable? tame)
	{
		BaseAI? ai = tame != null ? tame.GetComponent<BaseAI>() : null;
		if (ai != null && OriginalRange.TryGetValue(ai, out float range))
		{
			ai.m_randomMoveRange = range;
			OriginalRange.Remove(ai);
		}
	}

	/// <summary>Das Tier ist (nicht mehr) angebunden: Reste aufräumen.</summary>
	public static void NotTied(Tameable tame)
	{
		if (OriginalRange.Count > 0)
		{
			Unhold(tame);
		}
		if (Pulling.Count > 0 && tame.m_character != null)
		{
			Pulling.Remove(tame.m_character);
		}
		MissingSince.Remove(tame);
		MissingCheck.Remove(tame);
	}

	/// <summary>Jedes Bild für jedes angebundene Tier: Seil (alle), beim Besitzer festhalten und ziehen. Kein Verschleiß am Bauteil.</summary>
	public static void TickTied(Tameable tame, ZNetView view, ZDO zdo)
	{
		Character? animal = tame.m_character;
		string id = zdo.GetString(Plugin.LeashIdHash);
		Vector3 anchor = zdo.GetVec3(Plugin.PostHash, tame.transform.position);
		SeenTied[id] = Time.time;
		LeashKnot? knot = LeashKnot.ById.TryGetValue(id, out LeashKnot found) && found != null && found.Ready ? found : null;
		if (animal == null)
		{
			Rope.Hide(tame);
			return;
		}
		Rope.DrawTo(tame, knot != null ? knot.transform.position : anchor + Vector3.up, animal, Plugin.TierAt(zdo.GetInt(Plugin.TierHash)));
		ClaimFromServer(view);
		if (!view.IsOwner())
		{
			Pulling.Remove(animal);
			return;
		}
		if (knot == null)
		{
			// Knoten nicht geladen oder weg: erst entscheiden, wenn der Bereich sicher geladen ist (höchstens einmal pro Sekunde prüfen)
			Pulling.Remove(animal);
			if (MissingCheck.TryGetValue(tame, out float nextMissingCheck) && Time.time < nextMissingCheck)
			{
				return;
			}
			MissingCheck[tame] = Time.time + 1f;
			if (Loaded(anchor, 4f))
			{
				if (!MissingSince.TryGetValue(tame, out float since))
				{
					MissingSince[tame] = Time.time;
				}
				else if (Time.time - since > 20f) // Review 27.10.2026: 20 statt 5 s, ein träger Server schickt das Knoten-ZDO beim Einloggen später
				{
					MissingSince.Remove(tame);
					FreeAnimal(id);
				}
			}
			return;
		}
		MissingSince.Remove(tame);
		if (RiderTakes(zdo, knot))
		{
			Pulling.Remove(animal); // beim Reiten zieht der Pfosten nicht, solange die Leine in die Hand des Reiters wechselt
			return;
		}
		Hold(tame, anchor);
		Pulling[animal] = knot.transform.position;
	}

	/// <summary>Knoten, deren Leine der Reiter bei diesem Aufsitzen schon zu nehmen versucht hat (je Aufsitzen einmal).</summary>
	private static readonly HashSet<string> RideTakes = new();

	/// <summary>
	/// Gorduan (18.10.2026): Beim Reiten löst sich die Leine nicht und stört das Laufen nicht. Sitzt der eigene Spieler auf einem
	/// angebundenen Tier auf, nimmt er die Leine deshalb vom Pfosten in die Hand (wie E am Knoten, der Server entscheidet);
	/// danach gilt die Leine in der Hand (Bonus ja, Ziehen nein). True, solange die Anfrage läuft: dann nicht ziehen. Ist
	/// in der Schnellleiste kein Platz (oder der Knoten im Schutzbereich eines anderen), bleibt das Tier angebunden.
	/// </summary>
	private static bool RiderTakes(ZDO zdo, LeashKnot knot)
	{
		Player? local = Player.m_localPlayer;
		long rider = zdo.GetLong(ZDOVars.s_user);
		if (local == null || rider == 0L || rider != local.GetPlayerID())
		{
			RideTakes.Remove(knot.Id);
			return false;
		}
		if (RideTakes.Add(knot.Id))
		{
			TakeKnot(local, knot);
		}
		return Requested.TryGetValue(knot.Id, out float asked) && Time.time - asked < 5f;
	}

	/// <summary>Zerstörte Tiere aus den Listen nehmen.</summary>
	public static void Cleanup()
	{
		if (MissingSince.Count > 0)
		{
			DeadTames.Clear();
			foreach (Tameable tame in MissingSince.Keys)
			{
				if (tame == null)
				{
					DeadTames.Add(tame!);
				}
			}
			foreach (Tameable tame in DeadTames)
			{
				MissingSince.Remove(tame);
				MissingCheck.Remove(tame);
			}
		}
		if (Pulling.Count > 0)
		{
			DeadAnimals.Clear();
			foreach (Character animal in Pulling.Keys)
			{
				if (animal == null)
				{
					DeadAnimals.Add(animal!);
				}
			}
			foreach (Character animal in DeadAnimals)
			{
				Pulling.Remove(animal);
			}
		}
		if (OriginalRange.Count > 0)
		{
			DeadAis.Clear();
			foreach (BaseAI ai in OriginalRange.Keys)
			{
				if (ai == null)
				{
					DeadAis.Add(ai!);
				}
			}
			foreach (BaseAI ai in DeadAis)
			{
				OriginalRange.Remove(ai);
			}
		}
		if (SeenTied.Count > 0 && Time.time >= nextSeenCleanup)
		{
			nextSeenCleanup = Time.time + 30f;
			DeadIds.Clear();
			foreach (KeyValuePair<string, float> entry in SeenTied)
			{
				if (Time.time - entry.Value > 60f)
				{
					DeadIds.Add(entry.Key); // lange nicht gesehen: vergessen (Knoten fragen nur die letzten 3 s ab)
				}
			}
			foreach (string id in DeadIds)
			{
				SeenTied.Remove(id);
			}
		}
	}

	private static readonly List<string> DeadIds = new();
	private static float nextSeenCleanup;
}

/// <summary>Anbinde-Funktion an einem Pfosten, Zaun oder einer Mauer (wird beim Laden des Bauteils angehängt).</summary>
public class LeashPost : MonoBehaviour, Hoverable, Interactable
{
	private static readonly Dictionary<Vector3Int, List<LeashPost>> ByCell = new();
	private Piece? piece;
	private Vector3Int cell;
	private Bounds? bounds;

	private static Vector3Int CellOf(Vector3 position) => new(Mathf.RoundToInt(position.x * 20f), Mathf.RoundToInt(position.y * 20f), Mathf.RoundToInt(position.z * 20f));

	private void Awake()
	{
		piece = GetComponent<Piece>();
		cell = CellOf(transform.position);
		if (!ByCell.TryGetValue(cell, out List<LeashPost> posts))
		{
			ByCell[cell] = posts = new List<LeashPost>(1);
		}
		posts.Add(this);
	}

	private void OnDestroy()
	{
		if (ByCell.TryGetValue(cell, out List<LeashPost> posts))
		{
			posts.Remove(this);
			if (posts.Count == 0)
			{
				ByCell.Remove(cell);
			}
		}
	}

	/// <summary>Bauteil an der gespeicherten Position (Bauteile bewegen sich nicht).</summary>
	public static LeashPost? Find(Vector3 anchor)
	{
		Vector3Int center = CellOf(anchor);
		for (int x = -1; x <= 1; x++)
		{
			for (int y = -1; y <= 1; y++)
			{
				for (int z = -1; z <= 1; z++)
				{
					if (ByCell.TryGetValue(center + new Vector3Int(x, y, z), out List<LeashPost> posts))
					{
						foreach (LeashPost post in posts)
						{
							if (post != null)
							{
								return post;
							}
						}
					}
				}
			}
		}
		return null;
	}

	private Bounds Bounds
	{
		get
		{
			if (bounds == null)
			{
				Bounds? b = null;
				foreach (Collider collider in GetComponentsInChildren<Collider>())
				{
					if (!collider.enabled || collider.isTrigger)
					{
						continue; // ausgeschaltete Kollider melden leere Maße am Ursprung
					}
					if (b is Bounds current)
					{
						current.Encapsulate(collider.bounds);
						b = current;
					}
					else
					{
						b = collider.bounds;
					}
				}
				bounds = b ?? new Bounds(transform.position + Vector3.up, Vector3.one * 0.2f);
			}
			return bounds.Value;
		}
	}

	/// <summary>Knoten: knapp über dem Boden am Bauteil, höchstens 1,1 m über seinem unteren Ende.</summary>
	internal Vector3 Knot => new(Bounds.center.x, Mathf.Min(Bounds.max.y, Bounds.min.y + 1.1f), Bounds.center.z);

	/// <summary>Halbe Dicke des Bauteils (für die Seilschlinge), bei Mauern höchstens 0,35 m.</summary>
	internal float Radius => Mathf.Clamp(Mathf.Min(Bounds.extents.x, Bounds.extents.z), 0.06f, 0.35f);

	public string GetHoverText() => Posts.HoverText(this);

	public string GetHoverName() => piece != null ? Localization.instance.Localize(piece.m_name) : "";

	public float GetHoverOffset() => 0f;

	public bool Interact(Humanoid user, bool hold, bool alt) => !hold && !alt && user == Player.m_localPlayer && Posts.Interact((Player)user, this);

	/// <summary>Zifferntaste am Bauteil: nichts Eigenes, Valheim nimmt das Item in die Hand (angebunden wird mit E).</summary>
	public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
}

/// <summary>
/// Knoten einer angebundenen Leine (eigenes Bauteil GC_LeashKnot). Lebenspunkte = Haltbarkeit der Leine. Zeichnet die
/// Seilschlinge um das Bauteil, prüft beim Besitzer, ob Bauteil und Tier noch da sind, und gibt die Leine beim Zerstören
/// kaputt frei.
/// </summary>
public class LeashKnot : MonoBehaviour, Hoverable, Interactable
{
	internal static readonly Dictionary<string, LeashKnot> ById = new();
	internal ZNetView? View;
	private WearNTear? wear;
	private LineRenderer? coil;
	private MeshRenderer? coilMesh;
	private string id = "";
	private bool released;
	private float nextCheck;
	private float postMissingSince = -1f;
	private float animalMissingSince = -1f;

	internal bool Ready => id.Length > 0 && View != null && View.IsValid();
	internal bool Released => released;
	internal string Id => id;
	internal Vector3 Anchor => View != null && View.IsValid() ? View.GetZDO().GetVec3(Plugin.PostHash, transform.position) : transform.position;
	internal Tier? LeashTier => View != null && View.IsValid() ? Plugin.TierAt(View.GetZDO().GetInt(Plugin.TierHash)) : null;
	internal float MaxHealth
	{
		get
		{
			float stored = View != null && View.IsValid() ? View.GetZDO().GetFloat(Posts.MaxHealthHash, 0f) : 0f;
			return stored > 0f ? stored : LeashTier is Tier tier ? Plugin.MaxDurability(tier) : 10f;
		}
	}

	internal bool Unbreakable => View != null && View.IsValid() && View.GetZDO().GetInt(Posts.UnbreakableHash) == 1;
	internal float Health => View != null && View.IsValid() ? Mathf.Clamp(View.GetZDO().GetFloat(ZDOVars.s_health, MaxHealth), 0f, MaxHealth) : 0f;

	private void Awake()
	{
		View = GetComponent<ZNetView>();
		wear = GetComponent<WearNTear>();
		if (wear != null)
		{
			wear.m_onDestroyed = (Action)Delegate.Combine(wear.m_onDestroyed, new Action(OnBroken));
		}
		nextCheck = Time.time + UnityEngine.Random.Range(0.5f, 1.5f); // Knoten einer Basis prüfen nicht alle im selben Bild
		Setup();
	}



	/// <summary>Daten aus dem ZDO übernehmen (beim Anbinden erst nach dem Erzeugen gesetzt, deshalb auch später aufgerufen).</summary>
	internal void Setup()
	{
		if (View == null || View.GetZDO() == null)
		{
			return;
		}
		ZDO zdo = View.GetZDO();
		string newId = zdo.GetString(Plugin.LeashIdHash);
		if (newId.Length == 0)
		{
			return;
		}
		id = newId;
		ById[id] = this;
		if (wear != null)
		{
			wear.m_health = MaxHealth; // Lebenspunkte = Haltbarkeit der Leinenstufe, Hammer repariert bis dahin
			wear.m_healthPercentage = Mathf.Clamp01(Health / MaxHealth);
		}
		if (GetComponent<BoxCollider>() is BoxCollider box)
		{
			float size = zdo.GetFloat(Posts.RadiusHash, 0.12f) * 2f + 0.04f; // knapp um das Bauteil, damit es selbst erreichbar bleibt
			box.size = new Vector3(size, 0.1f, size);
		}
	}

	private void OnDestroy()
	{
		if (id.Length > 0 && ById.TryGetValue(id, out LeashKnot knot) && knot == this)
		{
			ById.Remove(id);
		}
	}

	private void Update()
	{
		if (id.Length == 0)
		{
			Setup();
			if (id.Length == 0)
			{
				return;
			}
		}
		if (Gc.IsHeadless)
		{
			return; // der Server hängt keine Anbinde-Funktion an Bauteile und sieht keine Tiere: dort nichts prüfen
		}
		DrawCoil();
		if (!Plugin.On || released || View == null || !View.IsValid() || Time.time < nextCheck)
		{
			return; // abgeschaltet: Knoten bleiben, wie sie sind
		}
		nextCheck = Time.time + 1f;
		Posts.ClaimFromServer(View);
		if (!View.IsOwner())
		{
			return;
		}
		Vector3 anchor = Anchor;
		// erst entscheiden, wenn der ganze Bereich der Leine bei uns geladen ist (sonst fehlt das Tier nur scheinbar)
		bool areaReady = Posts.Loaded(anchor, Plugin.KnotCheckRadius);
		// Bauteil abgerissen oder zerstört: Leine fällt mit ihrer Haltbarkeit herunter, das Tier ist frei
		if (!Posts.PieceAt(anchor) && areaReady)
		{
			if (postMissingSince < 0f)
			{
				postMissingSince = Time.time;
			}
			else if (Time.time - postMissingSince > 5f)
			{
				DropAndRemove(Health, "gcls_post_gone");
				return;
			}
		}
		else
		{
			postMissingSince = -1f;
		}
		// kein Tier mehr an der Leine (z. B. gestorben): Leine fällt herunter
		bool animalSeen = Posts.SeenTied.TryGetValue(id, out float seen) && Time.time - seen < 3f;
		if (!animalSeen && areaReady)
		{
			if (animalMissingSince < 0f)
			{
				animalMissingSince = Time.time;
			}
			else if (Time.time - animalMissingSince > 10f)
			{
				DropAndRemove(Health, "gcls_knot_lost_animal");
			}
		}
		else
		{
			animalMissingSince = -1f;
		}
	}

	/// <summary>Bauteil oder Tier weg: Der Server entfernt den Knoten und gibt den Abwurf frei (Leine mit ihrer Haltbarkeit, Tier frei).</summary>
	private void DropAndRemove(float durability, string messageKey)
	{
		released = true;
		Posts.RequestDrop(this, durability, messageKey, true);
	}

	/// <summary>Durch Schaden zerstört (WearNTear, beim Besitzer, entfernt den Knoten selbst): Leine liegt kaputt am Boden, das Tier ist frei.</summary>
	private void OnBroken()
	{
		if (released || id.Length == 0)
		{
			return;
		}
		released = true;
		Posts.RequestDrop(this, 0f, "gcls_knot_broken", false);
	}

	internal string AnimalName() => Posts.AnimalFor(id)?.GetHoverName() ?? "?";

	/// <summary>Seilschlinge: zwei Windungen um das Bauteil in der Farbe der Leine (aus Seilgliedern, sonst als Linie).</summary>
	private void DrawCoil()
	{
		if (coilMesh != null)
		{
			return; // beleuchtet: nachts nichts abzudunkeln
		}
		if (coil == null && MeshCoil())
		{
			return;
		}
		if (coil == null)
		{
			Material? material = Rope.MaterialFor(LeashTier);
			if (material == null)
			{
				return;
			}
			GameObject go = new("GC_LeashCoil");
			go.transform.SetParent(transform, false);
			coil = go.AddComponent<LineRenderer>();
			coil.useWorldSpace = true;
			coil.sharedMaterial = material;
			coil.widthMultiplier = 0.035f;
			coil.numCapVertices = 2;
			coil.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			coil.receiveShadows = false;
			float radius = View != null && View.IsValid() ? View.GetZDO().GetFloat(Posts.RadiusHash, 0.12f) + 0.02f : 0.14f;
			const int points = 33;
			Vector3[] positions = new Vector3[points];
			Vector3 center = transform.position;
			for (int i = 0; i < points; i++)
			{
				float t = i / (float)(points - 1);
				float angle = t * Mathf.PI * 4f; // zwei Windungen
				positions[i] = center + new Vector3(Mathf.Cos(angle) * radius, (t - 0.5f) * 0.07f, Mathf.Sin(angle) * radius);
			}
			coil.positionCount = points;
			coil.SetPositions(positions);
		}
		float light = Mathf.Clamp(RenderSettings.ambientLight.maxColorComponent * 1.6f, 0.2f, 1f); // nachts nicht leuchten
		coil.startColor = coil.endColor = new Color(light, light, light, 1f);
	}

	/// <summary>Die Schlinge einmal aus Seilgliedern bauen (ein Bauteil bewegt sich nicht). false: kein Modell oder Material.</summary>
	private bool MeshCoil()
	{
		Material? material = RopeLook.Rope(LeashTier);
		if (RopeModel.Tile == null || material == null)
		{
			return false;
		}
		float radius = View != null && View.IsValid() ? View.GetZDO().GetFloat(Posts.RadiusHash, 0.12f) + 0.02f : 0.14f;
		Mesh? ring = Ring(radius);
		if (ring == null)
		{
			return false;
		}
		GameObject go = new("GC_LeashCoil");
		go.transform.SetParent(transform, false);
		go.transform.SetPositionAndRotation(transform.position, Quaternion.identity);
		go.AddComponent<MeshFilter>().sharedMesh = ring;
		coilMesh = go.AddComponent<MeshRenderer>();
		coilMesh.sharedMaterial = material;
		return true;
	}

	/// <summary>Ringe je Radius (mm): Ecken relativ zur Mitte in Weltachsen, also für alle Knoten gleicher Größe dasselbe Mesh.</summary>
	private static readonly Dictionary<int, Mesh> Rings = new();

	private static Mesh? Ring(float radius)
	{
		int key = Mathf.RoundToInt(radius * 1000f);
		if (Rings.TryGetValue(key, out Mesh existing) && existing != null)
		{
			return existing;
		}
		const int points = 49;
		Vector3[] path = new Vector3[points];
		for (int i = 0; i < points; i++)
		{
			float t = i / (float)(points - 1);
			float angle = t * Mathf.PI * 4f; // zwei Windungen
			path[i] = new Vector3(Mathf.Cos(angle) * radius, (t - 0.5f) * 0.07f, Mathf.Sin(angle) * radius);
		}
		RopeMesh mesh = new("GC_LeashCoil_" + key);
		if (!mesh.Build(path, points, Vector3.zero))
		{
			mesh.Destroy();
			return null;
		}
		Rings[key] = mesh.Mesh;
		return mesh.Mesh;
	}

	public string GetHoverText()
	{
		if (!Ready || !Plugin.On)
		{
			return "";
		}
		Tier? tier = LeashTier;
		string name = tier?.Prefab != null ? Localization.instance.Localize(tier.Prefab.m_itemData.m_shared.m_name) : Localization.instance.Localize("$gc_leash_knot");
		string text = name
			+ "\n" + GcLoc.Get("gcls_knot_holds", AnimalName())
			+ "\n" + GcLoc.Get("gcls_knot_durability", Mathf.CeilToInt(Health), Mathf.CeilToInt(MaxHealth));
		return text + "\n" + Localization.instance.Localize(GcLoc.Get("gcls_hover_untie"));
	}

	public string GetHoverName() => Localization.instance.Localize("$gc_leash_knot");

	public float GetHoverOffset() => 0f;

	public bool Interact(Humanoid user, bool hold, bool alt) => !hold && !alt && user == Player.m_localPlayer && Plugin.On && Posts.TakeKnot((Player)user, this);

	public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
}

/// <summary>Auswahl, wenn mehrere Tiere am selben Bauteil hängen: welche Leine aufnehmen.</summary>
internal static class LeashMenu
{
	private static GameObject? window;
	private static Text? header;
	private static RectTransform? content;
	private static LeashPost? post;
	private static int openedFrame = -1;
	private static float nextRefresh;
	private static string signature = "";
	private static bool blocking;
	private static int unblockFrame = -1;

	public static bool IsOpen => window != null && window.activeSelf;

	public static void Open(LeashPost target)
	{
		Build();
		if (window == null)
		{
			return;
		}
		post = target;
		signature = "";
		openedFrame = Time.frameCount; // das E, das die Auswahl geöffnet hat, schließt sie nicht gleich wieder
		window.SetActive(true);
		unblockFrame = -1;
		if (!blocking)
		{
			blocking = true;
			GUIManager.BlockInput(true);
		}
		Refresh();
	}

	/// <summary>Schließen; die Eingabe wird erst im nächsten Bild frei, sonst wirkt dieselbe Taste (E, Esc) gleich noch im Spiel.</summary>
	public static void Close()
	{
		if (window != null && window.activeSelf)
		{
			window.SetActive(false);
		}
		if (blocking && unblockFrame < 0)
		{
			unblockFrame = Time.frameCount + 1;
		}
		post = null;
	}

	public static void Update()
	{
		if (!IsOpen)
		{
			if (blocking && unblockFrame < 0)
			{
				unblockFrame = Time.frameCount + 1; // Fenster verschwunden (z. B. abgemeldet)
			}
			if (blocking && Time.frameCount >= unblockFrame)
			{
				blocking = false;
				unblockFrame = -1;
				GUIManager.BlockInput(false);
			}
			return;
		}
		Player? player = Player.m_localPlayer;
		bool escape = Time.frameCount != openedFrame && (Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("Use") || ZInput.GetButtonDown("JoyUse") || ZInput.GetButtonDown("JoyButtonB"));
		if (player == null || post == null || !Plugin.On || Vector3.Distance(player.transform.position, post.transform.position) > 10f || escape)
		{
			Close();
			return;
		}
		if (Time.time >= nextRefresh)
		{
			nextRefresh = Time.time + 0.5f;
			Refresh();
		}
	}

	private static void Build()
	{
		if (window != null || GUIManager.Instance == null || GUIManager.CustomGUIFront == null)
		{
			return;
		}
		window = GcUi.CreateWindow("GC_LeashMenu", 640f, 520f, out Text title, out RectTransform inner);
		header = title;
		content = GcUi.CreateScroll(inner, 568f, 330f);
		GcUi.CreateButton(window.transform, GcLoc.Get("gcls_menu_take_all"), new Vector2(0.5f, 0f), new Vector2(-120f, 48f), 220f, 40f, TakeAll);
		GcUi.CreateButton(window.transform, GcLoc.Get("gcls_menu_close"), new Vector2(0.5f, 0f), new Vector2(120f, 48f), 220f, 40f, Close);
	}

	private static void Refresh()
	{
		if (!IsOpen || post == null || content == null)
		{
			return;
		}
		List<LeashKnot> knots = Posts.KnotsAt(post);
		if (knots.Count == 0)
		{
			Close();
			return;
		}
		string current = string.Join("|", knots.Select(k => k.Id + ":" + Mathf.CeilToInt(k.Health)));
		if (current == signature)
		{
			return;
		}
		signature = current;
		if (header != null)
		{
			header.text = GcLoc.Get("gcls_menu_title", post.GetHoverName());
		}
		GcUi.ClearChildren(content);
		foreach (LeashKnot knot in knots)
		{
			Tameable? tame = Posts.AnimalFor(knot.Id);
			Tier? tier = knot.LeashTier;
			string leash = tier?.Prefab != null ? Localization.instance.Localize(tier.Prefab.m_itemData.m_shared.m_name) : "";
			string rank = tame != null && tame.m_character != null ? Ranks.Text(Ranks.Of(tame.m_character)) : "?";
			RectTransform row = GcUi.CreateRow(content, 44f);
			GcUi.CreateLabel(row, GcLoc.Get("gcls_menu_row", knot.AnimalName(), rank, leash, Mathf.CeilToInt(knot.Health), Mathf.CeilToInt(knot.MaxHealth)),
				new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(6f, 0f), new Vector2(-150f, 0f), 17, TextAnchor.MiddleLeft);
			LeashKnot target = knot;
			GcUi.CreateButton(row, GcLoc.Get("gcls_menu_take"), new Vector2(1f, 0.5f), new Vector2(-70f, 0f), 130f, 36f, () => Take(target));
		}
	}

	private static void Take(LeashKnot knot)
	{
		Player? player = Player.m_localPlayer;
		if (player != null && knot != null)
		{
			Posts.TakeKnot(player, knot);
		}
		signature = "";
		Refresh();
	}

	private static void TakeAll()
	{
		Player? player = Player.m_localPlayer;
		if (player == null || post == null)
		{
			return;
		}
		foreach (LeashKnot knot in Posts.KnotsAt(post))
		{
			if (!Posts.TakeKnot(player, knot) && LeashBar.FreePlaces(player) == 0)
			{
				break; // Leinenleiste und Schnellleiste voll
			}
		}
		signature = "";
		Refresh();
	}
}

// ------------------------------------------------------------------ Spiel-Anbindung

/// <summary>
/// Knoten am Pfosten (Gorduan): Eine unzerstörbare Leine (EpicLoot) nimmt keinen Schaden. Sonst nur ein Viertel des Schadens
/// (18.10.2026), eine gekürzte davon noch bis zu 50 % weniger (15.10.2026, bei 1 m also ein Achtel).
/// </summary>
[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.RPC_Damage))]
internal static class KnotDamagePatch
{
	/// <summary>Gorduan, 18.10.2026: angebundene Leinen nehmen nur noch ¼ des normalen Schadens.</summary>
	internal const float Share = 0.25f;

	private static bool Prefix(WearNTear __instance, HitData hit)
	{
		if (__instance.GetComponent<LeashKnot>() is not LeashKnot knot)
		{
			return true;
		}
		if (knot.Unbreakable)
		{
			return false;
		}
		ZNetView? view = __instance.m_nview;
		float length = view != null && view.IsValid() ? Mathf.Clamp(view.GetZDO().GetFloat(Posts.DamageFactorHash, 1f), 0.5f, 1f) : 1f;
		if (hit != null)
		{
			hit.m_damage.Modify(Share * length);
		}
		return true;
	}
}

/// <summary>Bei jedem Spielstart die Knoten-RPCs registrieren und Gemerktes der letzten Sitzung vergessen.</summary>
[HarmonyPatch(typeof(Game), nameof(Game.Start))]
internal static class GameStartPatch
{
	private static void Postfix()
	{
		Posts.RegisterRpcs();
		Care.RegisterRpcs(); // Tierpflege-Erfahrung für andere Spieler
		Whistles.RegisterRpcs(); // Pfiffe anderer Spieler hören
		LeashUniqueEffects.RegisterRpcs(); // Bärenwacht für Gegner, die wir besitzen
		LeashUniqueEffects.Reset();
		Bonds.Reset();
		Illusions.Reset();
		Behaviour.Reset();
		Control.Reset();
	}
}

/// <summary>
/// Aufwerten an der Werkbank (Gorduan): Valheim verlangt je Qualität eine Werkbank-Stufe mehr (Recipe.GetRequiredStationLevel).
/// Für Leinen höchstens Stufe 5 (mit Werkzeugregal), sonst ließen sich die höheren Leinen nie ganz aufwerten.
/// </summary>
[HarmonyPatch(typeof(Recipe), nameof(Recipe.GetRequiredStationLevel))]
internal static class LeashUpgradeStationPatch
{
	private static void Postfix(Recipe __instance, ref int __result)
	{
		if (__result > Plugin.MaxWorkbenchLevel && __instance.m_item != null && Plugin.IsLeash(__instance.m_item.m_itemData, out _))
		{
			__result = Plugin.MaxWorkbenchLevel;
		}
	}
}

/// <summary>
/// Tooltip einer Leine, kurz wie Valheims Werte-Zeilen (Gorduan, 16.10.2026: „drastisch kürzen“): Länge von der Mindest-
/// bis zur Höchstlänge (Stufe und Qualität, beim Aufwerten die nächste Qualität, ohne Leinenreichweite aus EpicLoot),
/// die mit Umschalt + Mausrad eingestellte Länge (nur wenn gekürzt) und der Leinenbonus.
/// </summary>
[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), new Type[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool) })]
internal static class LeashTooltipPatch
{
	private static void Postfix(ItemDrop.ItemData item, int qualityLevel, ref string __result)
	{
		if (item == null || !Plugin.IsLeash(item, out Tier tier) || LeashTooltipEditor.Handles())
		{
			return; // mit EpicLoot 0.15.2 setzt der Tooltip-Editor die Zeilen hinter die Werte
		}
		foreach (string line in Lines(item, tier, Mathf.Max(1, qualityLevel)))
		{
			__result += "\n" + line;
		}
	}

	internal static List<string> Lines(ItemDrop.ItemData item, Tier tier, int quality)
	{
		List<string> lines = new() { GcLoc.Get("gcls_tooltip_length", Plugin.MinLength(tier, quality).ToString("0.#"), Plugin.MaxLength(tier, quality).ToString("0.#")) };
		float set = Plugin.SetLength(tier, item, quality);
		if (set < Plugin.MinLength(tier, quality) - 0.01f)
		{
			lines.Add(GcLoc.Get("gcls_tooltip_len_set", set.ToString("0.#")));
		}
		lines.Add(GcLoc.Get("gcls_tooltip_bonus", Mathf.RoundToInt(Plugin.LeashBonus(tier, quality) * 100f)));
		return lines;
	}
}

/// <summary>
/// EpicLoot 0.15.2 baut die Tooltips selbst und bietet anderen Mods einen Editor (API.AddTooltipEditor; Gorduan, 27.10.2026:
/// „alle aktuellen Tools für das aktuelle EpicLoot umsetzen“): Länge und Bonus kommen als Zusatzzeilen direkt hinter die Werte
/// (Anker „AfterStats“), bei verzauberten Leinen also nicht mehr unter die Effektliste. Ohne EpicLoot, mit älterem EpicLoot
/// oder wenn eine andere Mod EpicLoots Tooltips ersetzt, hängt LeashTooltipPatch die Zeilen wie bisher an.
/// </summary>
internal static class LeashTooltipEditor
{
	private const string Id = "gorduan.gc.tiere.leash";
	private const string ExtraList = "\"Extra\":[";
	private static readonly Regex QualityField = new("\"Quality\":(\\d+)");
	private static bool active;
	private static MethodInfo? overridden;
	private static FieldInfo? disabled;

	internal static void Register()
	{
		try
		{
			Type? api = AccessTools.TypeByName("EpicLoot.API");
			MethodInfo? add = api != null ? AccessTools.Method(api, "AddTooltipEditor", new[] { typeof(string), typeof(Func<ItemDrop.ItemData, string, string>) }) : null;
			if (add == null || active)
			{
				return; // kein EpicLoot oder älter als 0.15.2: wie bisher anhängen
			}
			overridden = AccessTools.Method(api, "IsTooltipOverridden");
			disabled = AccessTools.Field(AccessTools.TypeByName("EpicLoot.ItemTooltip"), "Disabled");
			active = add.Invoke(null, new object[] { Id, (Func<ItemDrop.ItemData, string, string>)Edit }) is true;
		}
		catch (Exception e)
		{
			active = false;
			Plugin.Log.LogWarning($"Tierleinen: EpicLoot-Tooltip-Editor nicht angemeldet, Länge und Bonus stehen wie bisher unten: {e.Message}");
		}
	}

	/// <summary>Setzt gerade EpicLoot die Zeilen (Editor angemeldet, EpicLoots Tooltip weder abgeschaltet noch ersetzt)?</summary>
	internal static bool Handles()
	{
		if (!active)
		{
			return false;
		}
		try
		{
			return disabled?.GetValue(null) is not true && overridden?.Invoke(null, null) is not true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>Zusatzzeilen in EpicLoots Tooltip-Daten (JSON, Liste „Extra“) einfügen; die Qualität kommt aus den Daten (beim Aufwerten die nächste).</summary>
	private static string Edit(ItemDrop.ItemData item, string json)
	{
		if (item == null || string.IsNullOrEmpty(json) || !Plugin.IsLeash(item, out Tier tier))
		{
			return json;
		}
		int at = json.IndexOf(ExtraList, StringComparison.Ordinal);
		if (at < 0)
		{
			return json;
		}
		Match match = QualityField.Match(json);
		int quality = match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : item.m_quality;
		StringBuilder sb = new();
		foreach (string line in LeashTooltipPatch.Lines(item, tier, Mathf.Max(1, quality)))
		{
			sb.Append(sb.Length > 0 ? "," : "").Append("{\"Anchor\":\"AfterStats\",\"Text\":\"").Append(Escape(line)).Append("\"}");
		}
		int insert = at + ExtraList.Length;
		return json.Insert(insert, sb.ToString() + (json.Length > insert && json[insert] != ']' ? "," : ""));
	}

	private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}

/// <summary>
/// Beim Aufwerten legt Valheim ein neues Item an (InventoryGui.DoCrafting). Leinen-ID, Verzauberung und andere
/// Zusatzdaten der alten Leine kommen auf die neue (nur fehlende Einträge, falls eine andere Mod sie schon übertragen
/// hat). So bleibt auch ein angeleintes Tier an der aufgewerteten Leine.
/// </summary>
[HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
internal static class LeashUpgradeKeepDataPatch
{
	private struct State
	{
		public bool Active;
		public Vector2i Position;
		public ItemDrop.ItemData? Old;
		public Dictionary<string, string>? Data;
	}

	private static void Prefix(InventoryGui __instance, Player player, out State __state)
	{
		__state = default;
		ItemDrop.ItemData? old = __instance.m_craftUpgradeItem;
		if (old == null || player == null || old.m_customData == null || old.m_customData.Count == 0 || !Plugin.IsLeash(old, out _)
			|| !player.GetInventory().ContainsItem(old))
		{
			return; // nicht (mehr) im Inventar: Valheim bricht das Aufwerten ab
		}
		__state.Active = true;
		__state.Position = old.m_gridPos;
		__state.Old = old;
		__state.Data = new Dictionary<string, string>(old.m_customData);
	}

	private static void Postfix(Player player, State __state)
	{
		if (!__state.Active || __state.Data == null || __state.Old == null || player == null)
		{
			return;
		}
		Inventory inventory = player.GetInventory();
		if (inventory.ContainsItem(__state.Old))
		{
			return; // nicht aufgewertet (z. B. Zutaten fehlten): die alte Leine ist noch da
		}
		ItemDrop.ItemData? upgraded = inventory.GetItemAt(__state.Position.x, __state.Position.y);
		if (upgraded == null || ReferenceEquals(upgraded, __state.Old) || upgraded.m_dropPrefab != __state.Old.m_dropPrefab)
		{
			return; // nur auf die neue Leine derselben Art, die Valheim an den alten Platz gelegt hat
		}
		foreach (KeyValuePair<string, string> entry in __state.Data)
		{
			if (!upgraded.m_customData.ContainsKey(entry.Key))
			{
				upgraded.m_customData[entry.Key] = entry.Value;
			}
		}
	}
}

/// <summary>Pfosten und Zäune bekommen beim Laden die Anbinde-Funktion.</summary>
[HarmonyPatch(typeof(Piece), nameof(Piece.Awake))]
internal static class PieceAwakePatch
{
	private static void Postfix(Piece __instance)
	{
		if (!Gc.IsHeadless)
		{
			Posts.AttachTo(__instance);
		}
	}
}

[HarmonyPatch(typeof(Tameable), nameof(Tameable.Awake))]
internal static class TameableAwakePatch
{
	private static void Postfix(Tameable __instance)
	{
		Rope.Register(__instance);
		Illusions.OnAwake(__instance); // Lokis Trugbilder bei allen Spielern erkennen und entschärfen
	}
}

/// <summary>
/// Zifferntaste, während man ein Tier anschaut: Valheim fragt zuerst das Tier (Sattel, Futter). Leinen nimmt Valheim
/// danach in die Hand wie jede Waffe (Gorduan: Leine auswählen, sie bleibt ausgewählt; angeleint wird mit E). Lokis
/// Trugbilder nehmen gar nichts an.
/// </summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.UseItem))]
internal static class UseItemPatch
{
	[HarmonyPriority(Priority.First)]
	private static bool Prefix(Tameable __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
	{
		if (__instance.m_character == null || !Illusions.IsIllusion(__instance.m_character))
		{
			return true;
		}
		if (user != null && user == Player.m_localPlayer && item != null && !item.IsEquipable())
		{
			user.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_illusion_touch")); // Sattel oder Futter am Trugbild
		}
		__result = false;
		return false; // eine Waffe oder Leine nimmt Valheim danach trotzdem in die Hand
	}
}

/// <summary>
/// E am Tier (Gorduan, 13.10.2026): Mit einer Leine in der Hand leint E an, löst die Leine oder nimmt das Tier vom Pfosten.
/// Ohne Leine in der Hand streichelt E wie in Valheim. Hängt das Tier an einer Leine oder an einem Pfosten, nur
/// streicheln: Valheims Folgen/Bleiben (bei Tieren, die Befehle annehmen, etwa Wölfen) brächte das Folgen an der Leine durcheinander.
/// </summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.Interact))]
internal static class InteractPatch
{
	private static bool Prefix(Tameable __instance, Humanoid user, bool hold, bool alt, ref bool __result)
	{
		Character? animal = __instance.m_character;
		if (animal != null && Illusions.IsIllusion(animal))
		{
			if (!hold && user != null && user == Player.m_localPlayer)
			{
				user.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_illusion_touch"));
			}
			__result = false;
			return false; // Trugbild: kein Streicheln, Befehlen oder Umbenennen
		}
		if (animal != null && !hold && Bonds.AnyFainted && animal.IsTamed() && Bonds.IsFainted(animal))
		{
			if (user != null && user == Player.m_localPlayer)
			{
				user.Message(MessageHud.MessageType.Center, GcLoc.Get("gcls_is_fainted", __instance.GetHoverName()));
			}
			__result = false;
			return false;
		}
		if (!hold && user != null && user == Player.m_localPlayer && Care.InfoKeyHeld() && Care.ShowInfo((Player)user, __instance))
		{
			__result = false; // Alt + E: nur die Boni anzeigen, nicht streicheln (auch keine Erfahrung, keine Animation)
			return false;
		}
		if (!Plugin.On || hold || alt || user == null || user != Player.m_localPlayer || __instance.m_nview == null || !__instance.m_nview.IsValid())
		{
			return true;
		}
		Player player = (Player)user;
		ItemDrop.ItemData? held = Control.Held(player);
		if (held != null && Plugin.IsLeash(held, out Tier tier))
		{
			Links.Use(player, __instance, held, tier);
			__result = true;
			return false;
		}
		ZDO zdo = __instance.m_nview.GetZDO();
		if (__instance.IsTamed() && (Links.ForTame(__instance) != null || Posts.IsTied(zdo) || Links.HolderOf(zdo) != null))
		{
			__result = Control.Pet(__instance, player); // nur streicheln, ohne Folgen/Bleiben
			return false;
		}
		return true;
	}
}

/// <summary>
/// Hinweise am Tier: Stufe gezüchteter Tiere hinter dem Namen, Leinenlast, an wessen Leine oder an welchem Pfosten es
/// hängt und was E gerade tut: mit einer Leine in der Hand anleinen, lösen oder vom Pfosten nehmen, sonst streicheln.
/// </summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.GetHoverText))]
internal static class HoverPatch
{
	private const string PetLine = "\n[<color=yellow><b>$KEY_Use</b></color>] $hud_pet";

	/// <summary>
	/// Taste der ersten freien, heilen Leine, leer ohne (Gorduan, 18.10.2026: im Hinweis nur die Taste): zuerst die Leinenleiste
	/// (Alt+1–5, offene Plätze), sonst die Schnellleiste (1–8), solange dort noch keine Leine ein Tier hält (Gorduan, 24.10.2026).
	/// Kaputte Leinen nimmt Links.Use nicht an.
	/// </summary>
	private static string LeashKey(Player player)
	{
		int slot = 0;
		foreach (ItemDrop.ItemData item in LeashBar.OpenItems(player))
		{
			if ((slot == 0 || item.m_gridPos.x + 1 < slot) && item.m_durability > 0f && Plugin.IsLeash(item, out _) && Links.ForItem(item) == null)
			{
				slot = item.m_gridPos.x + 1;
			}
		}
		if (slot > 0)
		{
			return "Alt+" + slot;
		}
		if (LeashBar.HotbarLink(player, null) != null)
		{
			return "";
		}
		int key = 0;
		foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
		{
			if (Links.InHotbar(item) && (key == 0 || item.m_gridPos.x + 1 < key) && item.m_durability > 0f && Plugin.IsLeash(item, out _) && Links.ForItem(item) == null)
			{
				key = item.m_gridPos.x + 1;
			}
		}
		return key > 0 ? key.ToString() : "";
	}

	private static void Postfix(Tameable __instance, ref string __result)
	{
		if (string.IsNullOrEmpty(__result) || __instance.m_nview == null || !__instance.m_nview.IsValid())
		{
			return;
		}
		Character? animal = __instance.m_character;
		if (animal != null && Illusions.IsIllusion(animal))
		{
			__result = __instance.GetHoverName() + "\n" + GcLoc.Get("gcls_illusion_hover"); // nichts zu tun an einem Trugbild
			return;
		}
		__result = Care.WithLevel(__instance, __result);
		if (!Plugin.On)
		{
			return;
		}
		Player? local = Player.m_localPlayer;
		ZDO zdo = __instance.m_nview.GetZDO();
		Player? holder = Links.HolderOf(zdo);
		bool tied = Posts.IsTied(zdo);
		bool own = Links.ForTame(__instance) != null;
		if (zdo.GetBool(Plugin.EscapedHash))
		{
			__result += "\n" + GcLoc.Get("gcls_hover_escaped"); // Leine gerissen: nur mit einer Leine wieder einzufangen
		}
		if (tied)
		{
			__result += "\n" + GcLoc.Get("gcls_hover_tied");
		}
		else if (holder != null)
		{
			__result += "\n" + GcLoc.Get("gcls_hover_leashed", holder.GetPlayerName());
		}
		if (local == null || !__instance.IsTamed())
		{
			return;
		}
		ItemDrop.ItemData? held = Control.Held(local);
		bool hint = held != null || own || tied || holder != null;
		if (held != null)
		{
			// Leine in der Hand: E tut das mit der Leine, statt zu streicheln
			Link? busy = Links.ForItem(held);
			string line = own ? GcLoc.Get("gcls_hover_release")
				: tied ? GcLoc.Get("gcls_hover_untie")
				: holder != null ? ""
				: busy != null && busy.Holder == local ? GcLoc.Get("gcls_hover_busy", busy.Tame != null ? busy.Tame.GetHoverName() : "")
				: GcLoc.Get("gcls_hover_attach");
			line = line.Length > 0 ? "\n" + Localization.instance.Localize(line) : "";
			string pet = Localization.instance.Localize(PetLine);
			__result = __result.Contains(pet) ? __result.Replace(pet, line) : __result + line;
			if (own && busy != null && busy.Tame == __instance && busy.Holder == local)
			{
				__result += "\n" + Control.Status(local, busy); // Befehle: ab welcher Stufe, bereit oder Pause
			}
		}
		else if (!own && !tied && holder == null)
		{
			string key = LeashKey(local);
			if (key.Length > 0)
			{
				__result += "\n" + GcLoc.Get("gcls_hover_take_leash", key); // Leine mit der Taste in der Hand: dann steht dort [E] Anleinen
				hint = true;
			}
		}
		if (hint && animal != null)
		{
			__result += "\n" + GcLoc.Get("gcls_hover_rank", Ranks.Text(Ranks.Of(animal)));
		}
	}
}

/// <summary>
/// Straffe Leine zieht das Tier (beim Halter, der das Tier besitzt). Sitzt im Ziel-Tempo von Valheims eigener
/// Bewegung (Character.AddPushbackForce, beim Gehen, Schwimmen und Fliegen), damit Valheim das Ziehen nicht im nächsten
/// Physikschritt wieder abzieht. Seitliche Bewegung des Tiers bleibt. Zonen (Gorduan, 13.10.2026):
///  * bis 90 % der Höchstlänge: frei, die Leine wirkt nicht (Folgen und freies Bewegen: Behaviour);
///  * bis 15 % über die Höchstlänge: straff bzw. gedehnt, die Leine führt sanft (2 m/s pro Meter, höchstens 3 m/s), ohne
///    das Tempo des Halters zu übertragen: Ist das Tier zu langsam, fällt es zurück. Läuft es weg (Kampf), bremst sie
///    nur um diesen Wert. Über der Höchstlänge verschleißt sie (Links.TickLink);
///  * darüber: Die Leine zieht das Tier mit, sein Tempo in Zugrichtung (relativ zum Schiff, auf dem er steht) plus 8 m/s
///    pro Meter zu viel. Der Halter ist dabei halb so schnell (BondSpeedPatch), die Leine verschleißt weiter.
/// Über eine Kante, die tiefer ist als 4 m, zieht die Leine nicht (Behaviour.Ledge).
/// </summary>
[HarmonyPatch(typeof(Character), nameof(Character.AddPushbackForce))]
internal static class PullPatch
{
	private const float GuidePerMeter = 2f;
	private const float GuideMax = 3f;
	private const float DragPerMeter = 8f;
	private const float MaxSpeed = 12f;

	private static void Postfix(Character __instance, ref Vector3 velocity)
	{
		if ((Posts.Pulling.Count > 0 || Links.Active.Count > 0) && __instance.HaveRider())
		{
			return; // Gorduan (18.10.2026): Beim Reiten beeinflusst die Leine das Laufen nicht
		}
		if (Posts.Pulling.Count > 0 && Posts.Pulling.TryGetValue(__instance, out Vector3 knot))
		{
			// angebunden: zum Knoten, der Knoten steht still; Höchstlänge der angebundenen Leine (dort ohne Verschleiß)
			float max = __instance.m_nview != null && __instance.m_nview.IsValid() ? Posts.TiedLengths(__instance.m_nview.GetZDO()).Max : Plugin.Tiers[0].MaxLength;
			Pull(__instance, ref velocity, knot, Vector3.zero, Plugin.FreeUntil(max), Plugin.DragFrom(max), 1f);
			return;
		}
		if (Links.Active.Count == 0)
		{
			return;
		}
		Link? link = Links.ForAnimal(__instance);
		if (link == null || link.Traveling || link.Holder == null || link.View == null || !link.View.IsValid() || !link.View.IsOwner())
		{
			return;
		}
		Vector3 holderVelocity = link.Holder.GetVelocity();
		Rigidbody? ground = link.Holder.IsOnGround() ? link.Holder.m_lastGroundBody : null;
		if (ground != null && __instance.m_lastGroundBody == ground)
		{
			holderVelocity -= ground.GetPointVelocity(link.Holder.transform.position); // auf einem Schiff zählt das Tempo an Deck
		}
		Pull(__instance, ref velocity, link.Holder.transform.position, holderVelocity, link.FreeUntil, link.DragFrom, 1f + link.Bond.Pull / 100f);
	}

	/// <summary>Ziel-Tempo in Richtung target anheben, sobald die Leine straff ist (seitliche Bewegung bleibt).</summary>
	private static void Pull(Character animal, ref Vector3 velocity, Vector3 target, Vector3 targetVelocity, float free, float dragFrom, float factor)
	{
		float distance = Links.Span(target, animal); // wie beim Verschleiß (Höhenunterschiede bis 1,5 m zählen nicht)
		Vector3 to = target - animal.transform.position;
		to.y = 0f;
		if (distance <= free || to.magnitude - Links.Reach(animal) <= free)
		{
			return; // frei, oder nur die Höhe spannt (Halter oder Knoten über dem Tier): hochziehen kann die Leine nicht
		}
		Vector3 direction = to.normalized;
		if (Behaviour.Ledge(animal, direction))
		{
			return; // nicht über eine tiefe Kante ziehen: die Leine spannt eben (und verschleißt)
		}
		float guide = Mathf.Min(distance, dragFrom) - free;
		float drag = Mathf.Max(0f, distance - dragFrom);
		// sanfte Führung höchstens 3 m/s, auch mit Zugkraft (die macht sie nur früher straff): Bei langen Leinen (bis 20 m)
		// soll sie das Tier vor der Zuggrenze nicht mitschleifen. Schwimmend und fliegend keine Führung: Dort behält
		// Valheim das Tempo von Bild zu Bild (m_currentVel), die Führung summierte sich und wäre wieder eine Wand
		float wanted = animal.IsSwimming() || animal.IsFlying() ? 0f : Mathf.Min(guide * GuidePerMeter * factor, GuideMax);
		if (drag > 0f)
		{
			targetVelocity.y = 0f;
			wanted += Mathf.Max(0f, Vector3.Dot(targetVelocity, direction)) + drag * DragPerMeter * factor; // hinter sich herziehen
		}
		wanted = Mathf.Min(wanted, MaxSpeed);
		float along = Vector3.Dot(new Vector3(velocity.x, 0f, velocity.z), direction);
		if (along >= wanted)
		{
			return;
		}
		float correction = wanted - along;
		if (drag <= 0f)
		{
			// Bis zur Zuggrenze bremst die Leine nur, um höchstens so viel, wie sie führt: Das Tier kann gegen die straffe
			// Leine anlaufen. Kämpft es, zieht sie es gar nicht zurück (bremst nur), damit es am Gegner bleiben kann.
			bool fighting = animal.GetBaseAI() is MonsterAI ai && ai.m_targetCreature != null;
			velocity += direction * (fighting ? Mathf.Clamp(-along, 0f, wanted) : Mathf.Min(correction, wanted));
			return;
		}
		// Über der Zuggrenze zieht sie es mit, weich auf dem ersten halben Meter: Kommt ein Tier mit vollem Tempo am Ende
		// der Leine an, pendelte es sonst jeden Physikschritt zwischen raus und rein. So bleibt es 0,1 bis 0,3 m darüber stehen.
		velocity += direction * Mathf.Min(correction, Mathf.Lerp(wanted, correction, Mathf.Clamp01(drag / 0.5f)));
	}
}

/// <summary>
/// Jeder Teleport des Halters (Portal, Dungeon-Eingang und -Ausgang, auch andere Mods über TeleportTo): Hat er wirklich
/// begonnen, reisen die angeleinten Tiere mit (Gorduan: auch in Dungeons).
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
internal static class TeleportStartPatch
{
	private static void Postfix(Player __instance, bool __result)
	{
		if (__result && __instance == Player.m_localPlayer && Links.Active.Count > 0)
		{
			Links.BeginTravel(__instance);
		}
	}
}

[HarmonyPatch(typeof(Player), nameof(Player.UpdateTeleport))]
internal static class TeleportPatch
{
	private static void Postfix(Player __instance)
	{
		if (Links.Active.Count > 0 && __instance == Player.m_localPlayer)
		{
			Links.UpdateTravel(__instance);
		}
	}
}

/// <summary>
/// Symbol einer Leine: aufgerollte Leine mit Schlaufe und Karabiner, in der Farbe des Materials. Die Form wird einmal
/// berechnet (als Anteile von Leinenfarbe, Karabiner-Grau und Deckkraft), jede Stufe färbt sie nur noch ein.
/// </summary>
internal static class LeashIcon
{
	private const int Size = 128;
	private static readonly Color Metal = new(0.72f, 0.72f, 0.76f);
	private static float[]? colorPart;
	private static float[]? metalPart;
	private static float[]? alpha;

	public static Sprite Create(Color color)
	{
		BuildShape();
		Texture2D texture = new(Size, Size, TextureFormat.RGBA32, false)
		{
			filterMode = FilterMode.Bilinear,
			wrapMode = TextureWrapMode.Clamp,
			hideFlags = HideFlags.HideAndDontSave,
		};
		Color[] pixels = new Color[Size * Size];
		for (int i = 0; i < pixels.Length; i++)
		{
			if (alpha![i] <= 0f)
			{
				continue;
			}
			Color c = color * colorPart![i] + Metal * metalPart![i];
			c.a = alpha[i];
			pixels[i] = c;
		}
		texture.SetPixels(pixels);
		texture.Apply();
		Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, Size, Size), new Vector2(0.5f, 0.5f), 100f);
		sprite.hideFlags = HideFlags.HideAndDontSave;
		return sprite;
	}

	private static void BuildShape()
	{
		if (alpha != null)
		{
			return;
		}
		colorPart = new float[Size * Size];
		metalPart = new float[Size * Size];
		alpha = new float[Size * Size];
		// drei Windungen, eine Schlaufe zum Halten, ein Ende mit Karabiner (in Bildkoordinaten 0..1, y nach oben)
		DrawCurve(Ellipse(0.47f, 0.43f, 0.31f, 0.21f, 0f, 1f), 0.040f, 1f, 0f);
		DrawCurve(Ellipse(0.50f, 0.47f, 0.28f, 0.19f, 0f, 1f), 0.040f, 0.92f, 0f);
		DrawCurve(Ellipse(0.53f, 0.51f, 0.25f, 0.17f, 0f, 1f), 0.040f, 1f, 0f);
		DrawCurve(Ellipse(0.74f, 0.77f, 0.12f, 0.10f, 0.15f, 0.95f), 0.034f, 1f, 0f);
		DrawCurve(Line(0.24f, 0.36f, 0.15f, 0.19f), 0.034f, 1f, 0f);
		DrawCurve(Ellipse(0.13f, 0.14f, 0.060f, 0.060f, 0f, 1f), 0.017f, 0f, 1f);
	}

	private static List<Vector2> Ellipse(float cx, float cy, float rx, float ry, float from, float to)
	{
		List<Vector2> points = new();
		for (int i = 0; i <= 72; i++)
		{
			float a = Mathf.Lerp(from, to, i / 72f) * Mathf.PI * 2f;
			points.Add(new Vector2(cx + Mathf.Cos(a) * rx, cy + Mathf.Sin(a) * ry));
		}
		return points;
	}

	private static List<Vector2> Line(float x0, float y0, float x1, float y1)
	{
		List<Vector2> points = new();
		for (int i = 0; i <= 12; i++)
		{
			points.Add(Vector2.Lerp(new Vector2(x0, y0), new Vector2(x1, y1), i / 12f));
		}
		return points;
	}

	/// <summary>
	/// Dicke Linie mit runder Schattierung (wie ein Seil), gedrehten Streifen und dunklem Rand; später Gezeichnetes liegt
	/// oben. Nur Pixel im Rechteck um die Linie werden geprüft.
	/// </summary>
	private static void DrawCurve(List<Vector2> curve, float thickness, float colorShare, float metalShare)
	{
		float radius = thickness * Size;
		Vector2[] points = curve.Select(p => p * Size).ToArray();
		int minX = Mathf.Max(0, Mathf.FloorToInt(points.Min(p => p.x) - radius - 2f));
		int maxX = Mathf.Min(Size - 1, Mathf.CeilToInt(points.Max(p => p.x) + radius + 2f));
		int minY = Mathf.Max(0, Mathf.FloorToInt(points.Min(p => p.y) - radius - 2f));
		int maxY = Mathf.Min(Size - 1, Mathf.CeilToInt(points.Max(p => p.y) + radius + 2f));
		for (int y = minY; y <= maxY; y++)
		{
			for (int x = minX; x <= maxX; x++)
			{
				Vector2 p = new(x + 0.5f, y + 0.5f);
				float best = float.MaxValue;
				float along = 0f;
				float walked = 0f;
				for (int i = 0; i < points.Length - 1; i++)
				{
					Vector2 a = points[i];
					Vector2 ab = points[i + 1] - a;
					float len = ab.magnitude;
					float t = len > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / (len * len)) : 0f;
					float d = Vector2.Distance(p, a + ab * t);
					if (d < best)
					{
						best = d;
						along = walked + t * len;
					}
					walked += len;
				}
				if (best > radius + 1f)
				{
					continue;
				}
				float edge = Mathf.Clamp01(radius + 1f - best); // weicher Rand
				float u = Mathf.Clamp01(best / radius);
				float light = Mathf.Sqrt(Mathf.Max(0f, 1f - u * u));
				float twist = 0.88f + 0.12f * Mathf.Sin(along * 0.9f + best * 0.6f); // gedrehte Fasern
				float shade = (0.45f + 0.55f * light) * twist;
				if (u > 0.82f)
				{
					shade *= 0.6f; // dunkler Rand
				}
				int index = y * Size + x;
				colorPart![index] = Mathf.Lerp(colorPart[index], colorShare * shade, edge);
				metalPart![index] = Mathf.Lerp(metalPart[index], metalShare * shade, edge);
				alpha![index] = Mathf.Lerp(alpha[index], 1f, edge);
			}
		}
	}
}

/// <summary>
/// Eine Leine je Biom (Gorduan, 19.10.2026): Die Leinen aus Lederfetzen, Bjørn-Haut und Robbenhaut gibt es nicht mehr. Ihre
/// Namen zeigen in Valheims Item-Verzeichnis auf die Leine ihres Bioms: Beim Laden eines Inventars (Spieler, Truhen)
/// entsteht daraus die neue Leine mit Qualität, Haltbarkeit und Verzauberung, und so wird sie auch gespeichert.
/// </summary>
[HarmonyPatch(typeof(ObjectDB), "UpdateRegisters")]
internal static class RetiredLeashPatch
{
	private static void Postfix(ObjectDB __instance)
	{
		foreach (KeyValuePair<string, string> pair in Plugin.RetiredPrefabs)
		{
			int old = pair.Key.GetStableHashCode();
			if (!__instance.m_itemByHash.ContainsKey(old) && __instance.m_itemByHash.TryGetValue(pair.Value.GetStableHashCode(), out GameObject kept))
			{
				__instance.m_itemByHash[old] = kept;
			}
		}
	}
}

/// <summary>
/// Dasselbe für Objekte in der Welt (Review 27.10.2026): Eine alte Leine, die am Boden liegt, hat noch den alten Prefab-Hash.
/// Ohne Eintrag löschten Host und Server sie beim Laden („Destroyed invalid prefab ZDO“), Spieler meldeten ständig
/// „Missing prefab hash“. Nachgeschlagen erst, wenn Valheim den Hash nicht kennt (Reihenfolge zu Jötunn egal).
/// </summary>
[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.GetPrefab), typeof(int))]
internal static class RetiredLeashScenePatch
{
	private static Dictionary<int, int>? retired;

	private static void Postfix(ZNetScene __instance, int hash, ref GameObject __result)
	{
		if (__result != null)
		{
			return;
		}
		retired ??= Plugin.RetiredPrefabs.ToDictionary(pair => pair.Key.GetStableHashCode(), pair => pair.Value.GetStableHashCode());
		if (retired.TryGetValue(hash, out int kept) && __instance.m_namedPrefabs.TryGetValue(kept, out GameObject prefab))
		{
			__result = prefab;
		}
	}
}
