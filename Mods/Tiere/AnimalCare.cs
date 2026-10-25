using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using GC.Shared;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Skill „Tierpflege" (Gorduan, 13.10.2026).
///
/// Erfahrung: Füttern (nur wer das Futter fallen gelassen oder gepflückt hat, kurze Abklingzeit je Tier), Zähmen (keine
/// Abklingzeit), Streicheln (30 Minuten je Tier), wenn ein Tier an deiner Leine etwas tötet, und Züchten.
///
/// Wirkung: Gezähmte Tiere in der Nähe eines Spielers bekommen je Stufe +1 % Leben, 0,05 Leben + 0,01 % des Höchstlebens
/// Heilung pro Sekunde, +0,5 % Schaden und +0,25 % Tempo (Stufe 100: +100 %, 5 + 1 %/s, +50 %, +25 %). Gorduan, 19.10.2026:
/// Der Skill wirkt halb so stark (vorher +200 %, 5 + 2 %/s, +100 %, +50 %), auch über die Leinen (Befehlspause, Zögern);
/// die feste Heilung und das Zähmen bleiben. Es zählt der Spieler mit der höchsten Stufe in der Nähe (nicht addiert); ein
/// angeleintes Tier bekommt die Stufe seines Halters. Zähmen geht je Stufe 2 % schneller.
///
/// Gorduan, 20.10.2026: Alle Stufen-Boni (eigene Stufe und Tierpflege gleich) sind mit 5/12 gerechnet (BonusShare): ein Lox mit
/// 2 Sternen, Stufe 300, Pfleger 100 und Elchhaut-Leine Q4 hat höchstens 10.000 Leben (9.750) und 683 Biss-Schaden. Leben,
/// Schaden, Tempo und die prozentuale Heilung je wirksamer Stufe also 0,83 %, 0,42 %, 0,21 % und 0,0042 %; die feste Heilung
/// (0,05 Leben je Stufe) und der Leinenbonus bleiben. Höchststufe je Tierart (Species): Lox 300, Wolf und Bär 200, Unbjørn
/// 300, sonst 150; eigene Stufe (halb) und Pfleger zusammen höchstens Species.EffectCap (150, Grenze A).
///
/// Gezüchtete Tiere (Gorduan, 18.10.2026): Stufe = Zufall zwischen ¾ des Eltern-Durchschnitts und dem höheren Elternteil, plus
/// Zufall 0 bis ¼ der Tierpflege-Stufe des Züchters (höchstens 150), sichtbar als „(Lvl X)" hinter dem Namen. Sie haben dauerhaft den halben Bonus ihrer Stufe (mit den
/// vollen Sätzen +2 % Leben, +1 % Schaden, +0,5 % Tempo je Stufe; die prozentuale Heilung ist auch hier halbiert, Gorduan
/// 19.10.2026); der Bonus aus der Nähe eines Spielers kommt dazu, zusammen höchstens Stufe 150. Züchter ist, wessen Futter das Muttertier zuletzt
/// gefressen hat (hingeworfen oder gepflückt), mit seiner Stufe von dem Moment; er muss bei der Geburt nicht da sein.
///
/// Mehrspieler: Jeder Spieler veröffentlicht seine Stufe in seinem Spieler-ZDO. Tiere rechnet ihr Besitzer (wie Valheim
/// selbst). Erfahrung für andere Spieler geht als Nachricht an deren Sitzung.
/// </summary>
internal static class Care
{
	/// <summary>Nie ändern: daran hängt der Fortschritt im Spielstand jedes Spielers.</summary>
	internal const string SkillId = "gorduan.gc_server.tierpflege";
	internal static Skills.SkillType Skill = Skills.SkillType.None;

	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<float> Radius = null!;
	internal static ConfigEntry<float> FeedXp = null!;
	internal static ConfigEntry<float> FeedCooldown = null!;
	internal static ConfigEntry<float> PetXp = null!;
	internal static ConfigEntry<float> PetCooldown = null!;
	internal static ConfigEntry<float> TameXp = null!;
	internal static ConfigEntry<float> BreedXp = null!;
	internal static ConfigEntry<float> KillXp = null!;
	internal static ConfigEntry<float> FollowDelay = null!;
	internal static ConfigEntry<float> FollowDelayLevel = null!;
	internal static ConfigEntry<bool> AnimalXp = null!;
	internal static ConfigEntry<float> AnimalXpFactor = null!;
	internal static ConfigEntry<float> AnimalKillXp = null!;

	/// <summary>Im Spieler-ZDO: Tierpflege-Stufe (0–100), für alle sichtbar.</summary>
	internal static readonly int SkillHash = "gc_care_skill".GetStableHashCode();
	/// <summary>Im Tier: Stufe eines gezüchteten Tiers (0–150).</summary>
	internal static readonly int LevelHash = "gc_care_level".GetStableHashCode();
	/// <summary>Im Tier: Sitzung des Spielers, dessen Futter es zuletzt gefressen hat (Zähmer, Züchter).</summary>
	internal static readonly int FeederHash = "gc_care_feeder".GetStableHashCode();
	/// <summary>Im fallengelassenen oder gepflückten Item: Sitzung des Spielers, der es fallen ließ oder pflückte.</summary>
	internal static readonly int DropperHash = "gc_care_dropper".GetStableHashCode();
	/// <summary>Im fallengelassenen oder gepflückten Item: Tierpflege-Stufe dieses Spielers in dem Moment.</summary>
	internal static readonly int DropperSkillHash = "gc_care_dropper_skill".GetStableHashCode();
	/// <summary>Im Tier: Tierpflege-Stufe des Fütterers aus seinem Futter (−1 oder fehlt: unbekannt, Futter von vor 1.5.0).</summary>
	internal static readonly int FeederSkillHash = "gc_care_feeder_skill".GetStableHashCode();
	/// <summary>Im Tier: Höchstleben ohne Boni (Grundlage) und der zuletzt angewendete Faktor, damit jeder Besitzer gleich rechnet.</summary>
	private static readonly int HpBaseHash = "gc_care_hpbase".GetStableHashCode();
	private static readonly int HpFactorHash = "gc_care_hpfactor".GetStableHashCode();
	/// <summary>Im Ei (Item-Daten): Stufe des Jungen, das daraus schlüpft.</summary>
	internal const string LevelKey = "gc_care_level";
	/// <summary>Im Spieler (m_customData): Abklingzeiten fürs Streicheln, „Tier=Weltzeit;…".</summary>
	private const string PetKey = "gc_care_pet";

	internal const float MaxAnimalLevel = 150f;
	private const string XpRpc = "GC_CareXP";

	internal struct Stats
	{
		public float Damage; // Anteil, 0,5 = +50 %
		public float Speed;
	}

	/// <summary>Eigene (beim Besitzer berechnete) Tiere mit Bonus, für Tempo und Schaden.</summary>
	internal static readonly Dictionary<Character, Stats> Animals = new();
	private static readonly Dictionary<string, float> FeedUntil = new();
	private static readonly List<string> Expired = new();
	private static float nextPublish;
	private static float nextAura;
	private static float lastAura;

	internal static void Init(ConfigFile Config)
	{
		const string section = "8 - Animal Care";
		Enabled = Gc.BindSynced(Config, section, "Enabled", true, "Skill Tierpflege: Erfahrung durch Füttern, Zähmen, Streicheln, Züchten und Kämpfe deiner angeleinten Tiere; zahme Tiere in der Nähe werden stärker.");
		Radius = Gc.BindSynced(Config, section, "Radius", 30f, "Umkreis in Metern: So nah muss ein Spieler sein, damit sein Tierpflege-Bonus auf zahme Tiere wirkt (und aufs Zähmen).", new AcceptableValueRange<float>(5f, 100f));
		// Gorduan, 19.10.2026: Erfahrung für den Skill halbiert (nicht fürs Töten durch Tiere an der Leine). Neue Schlüssel, damit die
		// neuen Werte auch dort gelten, wo die alten (FeedXp, PetXp, TameXp, BreedXp) schon in der Config stehen
		FeedXp = Gc.BindSynced(Config, section, "FeedExperience", 1.5f, "Erfahrung, wenn ein Tier Futter frisst, das du fallen gelassen oder gepflückt hast (Gorduan, 19.10.2026: halbiert, vorher 3).", new AcceptableValueRange<float>(0f, 100f));
		FeedCooldown = Gc.BindSynced(Config, section, "FeedCooldown", 60f, "Sekunden, bis Füttern desselben Tiers wieder Erfahrung gibt.", new AcceptableValueRange<float>(0f, 3600f));
		PetXp = Gc.BindSynced(Config, section, "PetExperience", 2.5f, "Erfahrung fürs Streicheln eines zahmen Tiers (Gorduan, 19.10.2026: halbiert, vorher 5).", new AcceptableValueRange<float>(0f, 100f));
		PetCooldown = Gc.BindSynced(Config, section, "PetCooldown", 1800f, "Sekunden, bis Streicheln desselben Tiers wieder Erfahrung gibt (Gorduan: 30 Minuten).", new AcceptableValueRange<float>(0f, 86400f));
		TameXp = Gc.BindSynced(Config, section, "TameExperience", 15f, "Erfahrung, wenn ein Tier zahm wird (an den, dessen Futter es zuletzt fraß, sonst den nächsten Spieler; Gorduan, 19.10.2026: halbiert, vorher 30).", new AcceptableValueRange<float>(0f, 1000f));
		BreedXp = Gc.BindSynced(Config, section, "BreedExperience", 7.5f, "Erfahrung für den Züchter, wenn ein Junges geboren wird oder ein Ei gelegt wird (Gorduan, 19.10.2026: halbiert, vorher 15).", new AcceptableValueRange<float>(0f, 1000f));
		// Gorduan, 19.10.2026: bleibt wie vorher (die Halbierung war ein Missverständnis), deshalb wieder der alte Schlüssel
		KillXp = Gc.BindSynced(Config, section, "KillXp", 2f, "Erfahrung, wenn ein Tier an deiner Leine etwas tötet (mal Stufe des Opfers: 1 Stern doppelt, 2 Sterne dreifach).", new AcceptableValueRange<float>(0f, 100f));
		FollowDelay = Gc.BindSynced(Config, section, "FollowDelay", 2f, "Läuft der Halter los, zögert ein Tier an der Leine zufällig bis zu so vielen Sekunden (freier Wille), bevor es folgt. Je höher die Mischstufe (Tier-Stufe und halbe Tierpflege des Halters 1:2), desto kürzer.", new AcceptableValueRange<float>(0f, 10f));
		FollowDelayLevel = Gc.BindSynced(Config, section, "FollowDelayLevel", 30f, "Ab dieser Mischstufe (Tier-Stufe + 2 × halbe Tierpflege des Halters, geteilt durch 3; Gorduan, 19.10.2026: Tierpflege zählt halb) folgt ein Tier an der Leine sofort; darunter zögert es anteilig.", new AcceptableValueRange<float>(1f, 150f));
		AnimalXp = Gc.BindSynced(Config, section, "AnimalExperience", true, "Zahme Tiere sammeln im Kampf Erfahrung und steigen bis Stufe 150 auf. Je Stufe so viel wie Valheims Waffen-Skills (1 bei Stufe 1, 178 bei 50, 501 bei 100, 920 bei 150).");
		AnimalXpFactor = Gc.BindSynced(Config, section, "AnimalExperienceFactor", 1f, "Tempo der Tier-Erfahrung: 1 = wie Valheims Waffen-Skills, 2 = doppelt so schnell.", new AcceptableValueRange<float>(0.1f, 10f));
		AnimalKillXp = Gc.BindSynced(Config, section, "AnimalKillExperience", 5f, "Erfahrung fürs Töten, mal Stufe des Opfers (ohne Stern 5, 1 Stern 10, 2 Sterne 15). Dazu 1 je Angriff, der trifft, wie bei Waffen.", new AcceptableValueRange<float>(0f, 100f));
		Skill = SkillManager.Instance.AddSkill(new SkillConfig
		{
			Identifier = SkillId,
			Name = "$gcs_skill_care",
			Description = "$gcs_skill_care_desc",
			Icon = PawIcon(),
			IncreaseStep = 1f,
		});
		CommandManager.Instance.AddConsoleCommand(new CareLevelCommand());
		CustomLocalization localization = LocalizationManager.Instance.GetLocalization();
		foreach (string key in new[] { "gcs_skill_care", "gcs_skill_care_desc" })
		{
			foreach (KeyValuePair<string, string> pair in GcLoc.AllLanguages(key))
			{
				localization.AddTranslation(pair.Key, key, pair.Value);
			}
		}
	}

	/// <summary>Bei jedem Spielstart: Erfahrung für andere Spieler empfangen.</summary>
	internal static void RegisterRpcs()
	{
		Animals.Clear();
		FeedUntil.Clear();
		Birth = null;
		ZRoutedRpc.instance?.Register<float>(XpRpc, RPC_Xp);
		AnimalExperience.RegisterRpc(); // Erfahrung der Tiere (eigene Tiere, von anderen Rechnern)
	}

	private static void RPC_Xp(long sender, float amount)
	{
		Player? local = Player.m_localPlayer;
		if (local != null && Enabled.Value && Skill != Skills.SkillType.None)
		{
			local.RaiseSkill(Skill, Mathf.Clamp(amount, 0f, MaxGrant())); // nicht mehr, als ein Ereignis nach den Einstellungen geben kann
		}
	}

	/// <summary>Größte Erfahrung, die ein Ereignis geben kann (Beute: mal Stufe des Opfers, mit Mods bis 5 Sterne).</summary>
	private static float MaxGrant() => Mathf.Max(FeedXp.Value, PetXp.Value, TameXp.Value, BreedXp.Value, KillXp.Value * 6f);

	/// <summary>Erfahrung für den Spieler dieser Sitzung (lokal oder als Nachricht an seinen Rechner).</summary>
	internal static void Grant(long session, float amount)
	{
		if (!Enabled.Value || amount <= 0f || session == 0L || Skill == Skills.SkillType.None)
		{
			return;
		}
		Player? local = Player.m_localPlayer;
		if (local != null && Links.Session(local) == session)
		{
			local.RaiseSkill(Skill, amount);
			return;
		}
		ZRoutedRpc.instance?.InvokeRoutedRPC(session, XpRpc, amount);
	}

	/// <summary>Veröffentlichte Tierpflege-Stufe eines Spielers (0–100).</summary>
	internal static float LevelOf(Player? player)
	{
		ZNetView? view = player != null ? player.m_nview : null;
		return view != null && view.IsValid() ? view.GetZDO().GetFloat(SkillHash, 0f) : 0f;
	}

	/// <summary>Höchste Stufe der Spieler im Umkreis (nicht addiert).</summary>
	internal static float NearbyLevel(Vector3 position)
	{
		Nearby(position, out float best);
		return best;
	}

	/// <summary>Der Spieler mit der höchsten Tierpflege im Umkreis (null, wenn keiner eine Stufe hat).</summary>
	private static Player? Nearby(Vector3 position, out float best)
	{
		best = 0f;
		Player? who = null;
		float range = Radius.Value * Radius.Value;
		foreach (Player player in Player.GetAllPlayers())
		{
			if (player != null && !player.IsDead() && (player.transform.position - position).sqrMagnitude <= range)
			{
				float level = LevelOf(player);
				if (level > best)
				{
					best = level;
					who = player;
				}
			}
		}
		return who;
	}

	/// <summary>
	/// Der Pfleger, der gerade auf das Tier wirkt: angeleint (oder Lokis Trugbild) der Halter, sonst der Spieler mit der
	/// höchsten Tierpflege in der Nähe. So rechnet auch der Besitzer des Tiers.
	/// </summary>
	internal static Player? Caretaker(Character animal, ZDO zdo, out float level)
	{
		long holder = zdo.GetLong(Plugin.HolderHash, 0L);
		if (holder == 0L)
		{
			holder = zdo.GetLong(Illusions.IllusionHash, 0L);
		}
		Player? leader = holder != 0L ? Links.FindBySession(holder) : null;
		if (leader != null)
		{
			level = LevelOf(leader);
			return leader;
		}
		return Nearby(animal.transform.position, out level);
	}

	/// <summary>
	/// Gorduan, 19.10.2026: Der Skill wirkt halb so stark auf Leben, Schaden und Tempo der Tiere und über die Leinen (Befehlspause,
	/// Zögern). Nicht aufs Zähmen und nicht auf die feste Heilung.
	/// </summary>
	internal const float SkillShare = 0.5f;

	/// <summary>
	/// Prozentuale Heilung je Stufe als Anteil des Höchstlebens: 0,01 % (Stufe 100: 1 % je Sekunde). Gorduan, 19.10.2026: halbiert,
	/// für Tier-Stufe und Tierpflege; die feste Heilung (0,05 Leben je Stufe) nicht.
	/// </summary>
	internal const float HealPercentPerLevel = 0.0001f;

	/// <summary>
	/// Gorduan, 20.10.2026: alle Stufen-Boni (eigene Stufe und Tierpflege gleich) mal 5/12, damit ein Lox mit 2 Sternen auf
	/// Höchststufe mit allem höchstens 10.000 Leben und 683 Biss-Schaden hat. Nicht für die feste Heilung und das Zähmen.
	/// </summary>
	internal const float BonusShare = 5f / 12f;

	/// <summary>
	/// Stufen eines zahmen Tiers: die eigene (halbe Zucht-Stufe) und die des Pflegers (Halter, Lokis Trugbild oder bester
	/// Spieler in der Nähe), zusammen höchstens Species.EffectCap (150).
	/// </summary>
	internal static void Levels(Character animal, ZDO zdo, out float own, out float care)
	{
		float cap = SpeciesLink.EffectCap;
		own = Mathf.Clamp(zdo.GetFloat(LevelHash, 0f) * 0.5f, 0f, cap);
		Caretaker(animal, zdo, out float aura);
		care = Mathf.Clamp(aura, 0f, cap - own);
	}

	/// <summary>
	/// Heilung je Sekunde für diese Stufe: je Stufe 0,05 Leben (fest) und 0,01 % × 5/12 des Höchstlebens (Stufe 100:
	/// 5 Leben + 0,42 %).
	/// </summary>
	internal static float HealPerSecond(float level, float max) => level * (0.05f + BonusShare * HealPercentPerLevel * max);

	/// <summary>Alt gedrückt: Alt + E zeigt die Boni eines zahmen Tiers, statt es zu streicheln (Gorduan, 16.10.2026).</summary>
	internal static bool InfoKeyHeld() => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

	/// <summary>
	/// Alt + E (Gorduan, 16.10.2026): kurz die Boni eines zahmen Tiers aus seiner eigenen Stufe, dahinter in Klammern die aus
	/// dem Pfleger, der gerade wirkt (Halter oder bester Spieler in der Nähe, nicht unbedingt man selbst). Zusammen
	/// höchstens Stufe 150 wie beim Rechnen; der Leinenbonus zählt nicht dazu. False: nichts anzuzeigen (Valheim macht weiter).
	/// </summary>
	internal static bool ShowInfo(Player player, Tameable tame)
	{
		Character? animal = tame.m_character;
		ZNetView? view = tame.m_nview;
		if (!Enabled.Value || Skill == Skills.SkillType.None || animal == null || view == null || !view.IsValid() || !tame.IsTamed() || Summoned(animal))
		{
			return false;
		}
		ZDO zdo = view.GetZDO();
		float cap = SpeciesLink.EffectCap;
		float own = Mathf.Clamp(zdo.GetFloat(LevelHash, 0f) * 0.5f, 0f, cap);
		Player? carer = Caretaker(animal, zdo, out float aura);
		float care = Mathf.Clamp(aura, 0f, cap - own);
		float ownBonus = BonusShare * own; // Gorduan, 20.10.2026: alle Stufen-Boni mal 5/12
		float skill = BonusShare * SkillShare * care; // Leben, Schaden, Tempo aus dem Skill halb; Heilung nach voller Stufe (Gorduan, 19.10.2026)
		string name = tame.GetHoverName(); // mit „(Lvl X)“ (CareNamePatch)
		string head = carer != null && aura > 0f
			? GcLoc.Get("gcs_info_head", name, carer.GetPlayerName(), Mathf.FloorToInt(aura))
			: GcLoc.Get("gcs_info_head_none", name);
		float max = animal.GetMaxHealth();
		string lines = GcLoc.Get("gcs_info_lines", Mathf.RoundToInt(2f * ownBonus), Mathf.RoundToInt(2f * skill), Mathf.RoundToInt(ownBonus),
			Mathf.RoundToInt(skill), Mathf.RoundToInt(0.5f * ownBonus), Mathf.RoundToInt(0.5f * skill), Heal(own, max), Heal(care, max));
		if (AnimalExperience.On)
		{
			float level = zdo.GetFloat(LevelHash, 0f); // Fortschritt zur nächsten Stufe (Gorduan, 18.10.2026)
			lines += "\n" + (level >= SpeciesLink.MaxLevel(animal) ? GcLoc.Get("gcs_info_xp_max")
				: GcLoc.Get("gcs_info_xp", Mathf.FloorToInt(zdo.GetFloat(AnimalExperience.XpHash, 0f)), Mathf.CeilToInt(AnimalExperience.Requirement(level))));
		}
		player.Message(MessageHud.MessageType.Center, head + "\n" + lines);
		return true;
	}

	/// <summary>Heilung je Sekunde für diese Stufe, gerundet (wie Apply).</summary>
	private static string Heal(float level, float max) => HealPerSecond(level, max).ToString("0.#");

	// ------------------------------------------------------------------ Jedes Bild

	internal static void Tick()
	{
		Player? local = Player.m_localPlayer;
		if (local == null || ZNet.instance == null || Skill == Skills.SkillType.None)
		{
			return;
		}
		float now = Time.time;
		if (now >= nextPublish)
		{
			nextPublish = now + 2f;
			Publish(local);
			Prune();
		}
		if (now < nextAura)
		{
			return;
		}
		float dt = lastAura > 0f ? Mathf.Clamp(now - lastAura, 0f, 3f) : 0f;
		lastAura = now;
		nextAura = now + 1f;
		Animals.Clear();
		foreach (Character animal in Character.GetAllCharacters())
		{
			if (animal == null || animal.IsPlayer() || !animal.IsTamed() || animal.IsDead())
			{
				continue;
			}
			ZNetView? view = animal.m_nview;
			if (view == null || !view.IsValid() || !view.IsOwner())
			{
				continue; // rechnet sein Besitzer
			}
			if (Summoned(animal))
			{
				continue; // beschworene Wesen (Dead Raiser, Spirit Caller) sind keine Tiere
			}
			ZDO zdo = view.GetZDO();
			float leash = LeashBonus(zdo);
			float own = 0f;
			float care = 0f;
			if (Enabled.Value)
			{
				Levels(animal, zdo, out own, out care);
			}
			// Leben, Schaden, Tempo: Tier-Stufe voll, Tierpflege halb (Gorduan, 19.10.2026), beides mal 5/12 (Gorduan, 20.10.2026)
			float level = BonusShare * (own + SkillShare * care);
			Apply(animal, zdo, leash, level, own + care, dt);
			if (leash > 0f || level > 0f)
			{
				Animals[animal] = new Stats { Damage = leash + level / 100f, Speed = 0.5f * level / 100f };
			}
		}
	}

	private static readonly Dictionary<string, bool> SummonedByPrefab = new();

	/// <summary>
	/// Beschworen statt gezähmt: Die Beschwörungen von Valheim 1.0 (Skeleton_Friendly aus dem Dead Raiser, die Geister des
	/// Spirit Caller) verschwinden mit Abstand oder nach dem Ausloggen ihres Besitzers (Tameable.m_unsummonDistance bzw.
	/// m_unsummonOnOwnerLogoutSeconds); zahme Tiere und Jungtiere nie.
	/// </summary>
	internal static bool Summoned(Character animal)
	{
		string name = Utils.GetPrefabName(animal.gameObject);
		if (!SummonedByPrefab.TryGetValue(name, out bool summoned))
		{
			Tameable? tame = animal.GetComponent<Tameable>();
			// Valheims eigenes Zeichen für Beschwörungen: Erfahrung geht an den Skill ihres Herrn (Character.RaiseSkill)
			summoned = tame != null && (tame.m_unsummonDistance > 0f || tame.m_unsummonOnOwnerLogoutSeconds > 0f || tame.m_levelUpOwnerSkill != Skills.SkillType.None);
			SummonedByPrefab[name] = summoned;
		}
		return summoned;
	}

	private static void Publish(Player local)
	{
		ZNetView? view = local.m_nview;
		if (view == null || !view.IsValid())
		{
			return;
		}
		float level = Enabled.Value ? local.GetSkillLevel(Skill) : 0f;
		ZDO zdo = view.GetZDO();
		if (Mathf.Abs(zdo.GetFloat(SkillHash, 0f) - level) > 0.01f)
		{
			zdo.Set(SkillHash, level);
		}
	}

	/// <summary>Leinenbonus des Tiers, nur solange es wirklich an einer Leine hängt oder angebunden ist.</summary>
	private static float LeashBonus(ZDO zdo)
	{
		if (!Plugin.On)
		{
			return 0f;
		}
		float bonus = zdo.GetFloat(Plugin.BonusHash, 0f);
		if (bonus > 0f && zdo.GetLong(Plugin.HolderHash, 0L) == 0L && !Posts.IsTied(zdo))
		{
			zdo.Set(Plugin.BonusHash, 0f); // Rest einer Leine, die nicht mehr da ist (wir sind der Besitzer)
			return 0f;
		}
		return bonus;
	}

	/// <summary>
	/// Leben (Leinenbonus + je wirksamer Stufe +2 %; die Tierpflege zählt halb, Stufe 100: +100 %) als echtes Höchstleben,
	/// der Anteil des aktuellen Lebens bleibt (An- und Ableinen heilt nicht). Grundlage ist das Höchstleben ohne Boni: Hat
	/// Valheim oder eine andere Mod es neu gesetzt (neue Stufe, Laden bei vollem Leben), gilt der neue Wert als Grundlage.
	/// Dazu Heilung je Sekunde nach healLevel (Tier-Stufe + volle Tierpflege).
	/// </summary>
	private static void Apply(Character animal, ZDO zdo, float leash, float level, float healLevel, float dt)
	{
		if ((Bonds.AnyFainted && Bonds.IsFainted(animal)) || animal.GetHealth() <= 0f)
		{
			return; // in Ohnmacht gehört das Leben der Wiederkehr; bei 0 nicht wiederbeleben (Valheim prüft den Tod erst im nächsten Schritt)
		}
		float factor = 1f + leash + 2f * level / 100f;
		float max = animal.GetMaxHealth();
		float storedBase = zdo.GetFloat(HpBaseHash, 0f);
		float storedFactor = zdo.GetFloat(HpFactorHash, 1f);
		bool ours = storedBase > 0f && Mathf.Abs(max - storedBase * storedFactor) <= Mathf.Max(1f, max * 0.01f);
		float baseMax = ours ? storedBase : max;
		if (baseMax <= 0f)
		{
			return;
		}
		float target = baseMax * factor;
		if (Mathf.Abs(max - target) > Mathf.Max(0.5f, target * 0.005f))
		{
			float share = max > 0f ? Mathf.Clamp01(animal.GetHealth() / max) : 1f;
			animal.SetMaxHealth(target);
			animal.SetHealth(Mathf.Max(1f, share * target));
			max = target;
		}
		if (factor <= 1.0001f)
		{
			if (storedBase > 0f)
			{
				zdo.Set(HpBaseHash, 0f); // ohne Bonus: nichts mehr zu merken
				zdo.Set(HpFactorHash, 1f);
			}
		}
		else
		{
			if (Mathf.Abs(storedBase - baseMax) > 0.01f)
			{
				zdo.Set(HpBaseHash, baseMax);
			}
			if (Mathf.Abs(storedFactor - factor) > 0.0001f)
			{
				zdo.Set(HpFactorHash, factor);
			}
		}
		if (healLevel > 0f && dt > 0f && animal.GetHealth() < max)
		{
			animal.Heal(HealPerSecond(healLevel, max) * dt, false); // Stufe 100: 5 Leben + 1 % des Höchstlebens je Sekunde
		}
	}

	private static void Prune()
	{
		if (FeedUntil.Count == 0)
		{
			return;
		}
		Expired.Clear();
		float now = Time.time;
		foreach (KeyValuePair<string, float> entry in FeedUntil)
		{
			if (entry.Value <= now)
			{
				Expired.Add(entry.Key);
			}
		}
		foreach (string key in Expired)
		{
			FeedUntil.Remove(key);
		}
	}

	// ------------------------------------------------------------------ Erfahrung

	/// <summary>
	/// Ein Tier frisst Futter, das ein Spieler fallen ließ oder pflückte: Er wird sein Fütterer (mit seiner Stufe aus dem
	/// Futter, −1 = unbekannt) und bekommt Erfahrung.
	/// </summary>
	internal static void Fed(Tameable tame, long dropper, float skill)
	{
		ZNetView? view = tame.m_nview;
		if (dropper == 0L || view == null || !view.IsValid())
		{
			return;
		}
		ZDO zdo = view.GetZDO();
		if (view.IsOwner())
		{
			bool other = zdo.GetLong(FeederHash, 0L) != dropper;
			if (other)
			{
				zdo.Set(FeederHash, dropper);
			}
			// Futter von vor 1.5.0 kennt keine Stufe (−1): Die Stufe desselben Fütterers bleibt stehen, die eines früheren
			// Fütterers gilt aber nicht für den neuen.
			if (skill >= 0f ? Mathf.Abs(zdo.GetFloat(FeederSkillHash, -1f) - skill) > 0.001f : other)
			{
				zdo.Set(FeederSkillHash, skill);
			}
		}
		if (!Enabled.Value || FeedXp.Value <= 0f)
		{
			return;
		}
		string key = zdo.m_uid.UserID + ":" + zdo.m_uid.ID + ":" + dropper;
		if (FeedUntil.TryGetValue(key, out float until) && Time.time < until)
		{
			return;
		}
		FeedUntil[key] = Time.time + FeedCooldown.Value;
		Grant(dropper, FeedXp.Value);
	}

	/// <summary>Streicheln: Erfahrung, je Tier höchstens alle PetCooldown Sekunden (im Spieler gespeichert, Weltzeit).</summary>
	internal static void Pet(Player player, Tameable tame)
	{
		ZNetView? view = tame.m_nview;
		if (!Enabled.Value || PetXp.Value <= 0f || view == null || !view.IsValid() || ZNet.instance == null || Skill == Skills.SkillType.None)
		{
			return;
		}
		double now = ZNet.instance.GetTimeSeconds();
		string id = AnimalKey(view.GetZDO());
		Dictionary<string, double> list = ReadPets(player, now);
		if (list.TryGetValue(id, out double until) && until > now)
		{
			return;
		}
		list[id] = now + PetCooldown.Value;
		WritePets(player, list);
		player.RaiseSkill(Skill, PetXp.Value);
	}

	/// <summary>
	/// Dauerhafter Schlüssel eines Tiers: Valheim vergibt die ZDO-IDs bei jedem Laden der Welt neu. Art und Zeitpunkt des
	/// Erscheinens (BaseAI schreibt ihn einmal ins Tier) bleiben gleich; nur ohne diesen Zeitpunkt die ZDO-ID.
	/// </summary>
	private static string AnimalKey(ZDO zdo)
	{
		long spawned = zdo.GetLong(ZDOVars.s_spawnTime, 0L);
		return spawned != 0L ? zdo.GetPrefab().ToString(CultureInfo.InvariantCulture) + ":" + spawned.ToString(CultureInfo.InvariantCulture) : zdo.m_uid.UserID + ":" + zdo.m_uid.ID;
	}

	private static Dictionary<string, double> ReadPets(Player player, double now)
	{
		Dictionary<string, double> list = new();
		if (player.m_customData == null || !player.m_customData.TryGetValue(PetKey, out string text) || string.IsNullOrEmpty(text))
		{
			return list;
		}
		double latest = now + PetCooldown.Value + 1.0; // weiter in der Zukunft: aus einer anderen Welt, vergessen
		foreach (string part in text.Split(';'))
		{
			int split = part.LastIndexOf('=');
			if (split > 0 && double.TryParse(part.Substring(split + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double until) && until > now && until <= latest)
			{
				list[part.Substring(0, split)] = until;
			}
		}
		return list;
	}

	private static void WritePets(Player player, Dictionary<string, double> list)
	{
		if (player.m_customData == null)
		{
			return;
		}
		StringBuilder sb = new();
		foreach (KeyValuePair<string, double> entry in list)
		{
			sb.Append(sb.Length > 0 ? ";" : "").Append(entry.Key).Append('=').Append(entry.Value.ToString("0", CultureInfo.InvariantCulture));
		}
		player.m_customData[PetKey] = sb.ToString();
	}

	/// <summary>Ein Item am Boden merkt sich, wer es fallen ließ oder pflückte, und dessen Tierpflege-Stufe (fürs Füttern).</summary>
	internal static void MarkDropper(ItemDrop item, long session, float skill)
	{
		if (session == 0L || item == null || item.m_nview == null || !item.m_nview.IsValid() || !item.m_nview.IsOwner())
		{
			return;
		}
		ZDO zdo = item.m_nview.GetZDO();
		zdo.Set(DropperHash, session);
		zdo.Set(DropperSkillHash, Mathf.Clamp(skill, 0f, 100f)); // Trank- und Ausrüstungs-Boni nicht über 100
	}

	/// <summary>
	/// Tierpflege-Stufe des Spielers einer Sitzung: der eigene direkt, andere aus ihrem Spieler-ZDO (auch wenn ihre Figur
	/// hier nicht geladen ist, über Valheims Spielerliste). 0, wenn er nicht zu finden ist.
	/// </summary>
	internal static float SkillOfSession(long session)
	{
		if (session == 0L || !Enabled.Value || Skill == Skills.SkillType.None)
		{
			return 0f;
		}
		Player? local = Player.m_localPlayer;
		if (local != null && Links.Session(local) == session)
		{
			return local.GetSkillLevel(Skill);
		}
		Player? player = Links.FindBySession(session);
		if (player != null)
		{
			return LevelOf(player);
		}
		if (ZNet.instance == null || ZDOMan.instance == null)
		{
			return 0f;
		}
		foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
		{
			if (info.m_characterID.UserID == session)
			{
				ZDO? zdo = ZDOMan.instance.GetZDO(info.m_characterID);
				return zdo != null ? zdo.GetFloat(SkillHash, 0f) : 0f;
			}
		}
		return 0f;
	}

	/// <summary>Zähmer: wessen Futter das Tier zuletzt fraß, sonst der nächste Spieler.</summary>
	internal static long CaretakerOf(Character animal, ZDO zdo, float range)
	{
		long feeder = zdo.GetLong(FeederHash, 0L);
		if (feeder != 0L && Links.FindBySession(feeder) != null)
		{
			return feeder;
		}
		return Links.Session(Player.GetClosestPlayer(animal.transform.position, range));
	}

	// ------------------------------------------------------------------ Züchten, Aufwachsen, Schlüpfen

	/// <summary>Was gerade geboren wird oder aufwächst (gilt nur während Procreate bzw. GrowUpdate).</summary>
	internal sealed class Spawning
	{
		public Procreation? Parent;   // Geburt: Stufe und Züchter werden beim neuen Tier ermittelt
		public Component? Source;     // Aufwachsen, Schlüpfen: das alte Objekt (nicht sich selbst markieren)
		public float Level;           // Aufwachsen, Schlüpfen: Stufe, die weitergegeben wird
		public bool Done;
		public Character? Born;       // das neue Tier (für den Umzug der Leine nach dem Aufwachsen)
		public ItemDrop? Egg;         // gelegtes Ei (Sterne, Gorduan 20.10.2026)
		public long Breeder;          // Züchter (Meldung bei einem Stern mehr)
	}

	internal static Spawning? Birth;

	/// <summary>Neues Tier (Junges, Ausgewachsenes, Geschlüpftes): Stufe eintragen, bei der Geburt dem Züchter Erfahrung geben.</summary>
	internal static void OnBorn(Character born)
	{
		Spawning? birth = Birth;
		if (birth == null || birth.Done || born == null || (birth.Source != null && born.gameObject == birth.Source.gameObject))
		{
			return;
		}
		if (birth.Parent != null && born.gameObject == birth.Parent.gameObject)
		{
			return;
		}
		ZNetView? view = born.m_nview;
		if (view == null || !view.IsValid() || !view.IsOwner())
		{
			return;
		}
		birth.Done = true;
		birth.Born = born;
		if (birth.Source is Growup && birth.Source.GetComponent<Character>() is Character young)
		{
			Young.CopyState(young, born); // Name, Anbindung am Pfosten usw. ziehen mit um (Gorduan: Jungtiere an der Leine)
		}
		long breeder = 0L;
		float level = birth.Parent != null ? BirthLevel(birth.Parent, out breeder) : birth.Level;
		birth.Breeder = breeder;
		if (level > 0f)
		{
			view.GetZDO().Set(LevelHash, Mathf.Min(level, SpeciesLink.MaxLevel(born)));
		}
		if (birth.Parent != null)
		{
			Grant(breeder, BreedXp.Value);
		}
	}

	/// <summary>Ei gelegt: Stufe ins Ei schreiben (schlüpft später), Züchter bekommt Erfahrung.</summary>
	internal static void OnEgg(ItemDrop egg)
	{
		Spawning? birth = Birth;
		if (birth?.Parent == null || birth.Done || egg == null || egg.m_nview == null || !egg.m_nview.IsValid() || !egg.m_nview.IsOwner())
		{
			return;
		}
		birth.Done = true;
		birth.Egg = egg;
		float level = BirthLevel(birth.Parent, out long breeder);
		birth.Breeder = breeder;
		if (level > 0f)
		{
			egg.m_itemData.m_customData[LevelKey] = Mathf.Min(level, SpeciesLink.MaxLevel(birth.Parent.m_character)).ToString("0.##", CultureInfo.InvariantCulture);
			egg.Save();
		}
		Grant(breeder, BreedXp.Value);
	}

	/// <summary>
	/// Gorduan (18.10.2026): zufällig zwischen ¾ des Eltern-Durchschnitts und der Stufe des höheren Elternteils, dazu zufällig 0
	/// bis ¼ der Tierpflege-Stufe des Züchters, höchstens 150. Gewürfelt wird einmal beim Besitzer der Mutter; die Stufe
	/// steht danach im Tier bzw. im Ei, alle sehen also dieselbe. Bis GC_Server 1.4.0 galt Gorduans erste Fassung vom 13.10.
	/// (Durchschnitt ÷ 4 + Stufe des Züchters).
	/// </summary>
	private static float BirthLevel(Procreation parent, out long breeder)
	{
		breeder = 0L;
		Character? mother = parent.m_character;
		if (mother == null || parent.m_nview == null || !parent.m_nview.IsValid() || !Enabled.Value)
		{
			return 0f;
		}
		ZDO zdo = parent.m_nview.GetZDO();
		float own = zdo.GetFloat(LevelHash, 0f);
		// GC_Futterfolge (Gorduan, 20.10.2026): der beste satte Partner beim Trächtigwerden; ohne die Mod das nächste Tier wie bisher
		float? chosen = GcHooks.GetMateLevel(parent);
		float partner = own;
		if (chosen != null)
		{
			partner = chosen.Value;
		}
		else
		{
			string kind = Utils.GetPrefabName(parent.m_seperatePartner != null ? parent.m_seperatePartner : mother.gameObject);
			Character? mate = MateOf(mother, kind, Mathf.Max(parent.m_partnerCheckRange, 3f));
			if (mate != null && mate.m_nview != null && mate.m_nview.IsValid())
			{
				partner = mate.m_nview.GetZDO().GetFloat(LevelHash, 0f);
			}
		}
		float inherited = UnityEngine.Random.Range(0.75f * (own + partner) * 0.5f, Mathf.Max(own, partner));
		float care = UnityEngine.Random.Range(0f, BreederSkill(zdo, out breeder) / 4f);
		return Mathf.Clamp(inherited + care, 0f, SpeciesLink.MaxLevel(mother)); // Höchststufe der Art (Gorduan, 20.10.2026)
	}

	/// <summary>
	/// Züchter (Gorduan, 18.10.2026: „das Futter muss zählen“): wessen Futter das Muttertier zuletzt gefressen hat, mit seiner
	/// Stufe aus diesem Futter. Er muss bei der Geburt weder in der Nähe noch online sein. Futter von vor 1.5.0 kennt keine
	/// Stufe: dann seine aktuelle, falls er zu finden ist. Hat das Tier nie Futter eines Spielers gefressen, zählt niemand.
	/// </summary>
	private static float BreederSkill(ZDO zdo, out long breeder)
	{
		breeder = zdo.GetLong(FeederHash, 0L);
		if (breeder == 0L)
		{
			return 0f;
		}
		float skill = zdo.GetFloat(FeederSkillHash, -1f);
		return Mathf.Clamp(skill >= 0f ? skill : SkillOfSession(breeder), 0f, 100f);
	}

	/// <summary>Nächstes zahmes Tier der Partner-Art (wie Valheims Partnersuche beim Züchten, im selben Umkreis).</summary>
	private static Character? MateOf(Character mother, string kind, float range)
	{
		Character? best = null;
		float bestDistance = range * range;
		foreach (Character other in Character.GetAllCharacters())
		{
			if (other == null || other == mother || !other.IsTamed() || other.IsDead() || Illusions.IsIllusion(other) || Utils.GetPrefabName(other.gameObject) != kind)
			{
				continue; // Lokis Trugbilder heißen wie ihr Tier, sind aber keine Partner
			}
			float distance = (other.transform.position - mother.transform.position).sqrMagnitude;
			if (distance <= bestDistance)
			{
				bestDistance = distance;
				best = other;
			}
		}
		return best;
	}

	/// <summary>
	/// Gorduan: Läuft der Halter los, „überlegt“ ein Tier an der Leine zufällig 0 bis 2 s, ob es folgt (freier Wille), danach
	/// folgt es. Mischstufe = (Tier-Stufe + 2 × Tierpflege des Halters) ÷ 3 (Gorduan: 1:2), die Tierpflege zählt seit
	/// 19.10.2026 halb (Gorduan). Die längste Bedenkzeit sinkt gleichmäßig bis zur Mischstufe 30, ab dort folgt es sofort wie
	/// bisher. Nur mit Tierpflege.
	/// </summary>
	internal static float Hesitation(Link link)
	{
		if (!Enabled.Value || FollowDelay.Value <= 0f)
		{
			return 0f;
		}
		float skill = Skill != Skills.SkillType.None && link.Holder != null ? link.Holder.GetSkillLevel(Skill) : 0f;
		float mixed = (AnimalLevelExact(link.Tame) + 2f * SkillShare * skill) / 3f;
		float longest = FollowDelay.Value * Mathf.Clamp01(1f - mixed / Mathf.Max(1f, FollowDelayLevel.Value));
		return longest > 0f ? UnityEngine.Random.Range(0f, longest) : 0f;
	}

	/// <summary>Stufe eines gezüchteten Tiers (abgerundet), 0 bei allen anderen.</summary>
	internal static int AnimalLevel(Tameable tame) => Mathf.FloorToInt(AnimalLevelExact(tame));

	/// <summary>Stufe eines gezüchteten Tiers mit Nachkommastellen (für Rechnungen), 0 bei allen anderen.</summary>
	internal static float AnimalLevelExact(Tameable? tame)
	{
		ZNetView? view = tame != null ? tame.m_nview : null;
		return view != null && view.IsValid() ? view.GetZDO().GetFloat(LevelHash, 0f) : 0f;
	}

	/// <summary>Hinweistext am Tier: „(Lvl X)" hinter dem Namen in der ersten Zeile, wie über dem Kopf.</summary>
	internal static string WithLevel(Tameable tame, string text)
	{
		int level = AnimalLevel(tame);
		if (level < 1)
		{
			return text;
		}
		string name = tame.GetName();
		return name.Length > 0 && text.StartsWith(name, StringComparison.Ordinal) ? name + " (Lvl " + level + ")" + text.Substring(name.Length) : text;
	}

	internal static float EggLevel(ItemDrop.ItemData? item)
	{
		return item?.m_customData != null && item.m_customData.TryGetValue(LevelKey, out string text)
			&& float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float level) ? level : 0f;
	}

	// ------------------------------------------------------------------ Symbol

	/// <summary>Pfotenabdruck für die Skill-Liste.</summary>
	private static Sprite PawIcon()
	{
		const int size = 64;
		Texture2D texture = new(size, size, TextureFormat.RGBA32, false);
		Color clear = new(0f, 0f, 0f, 0f);
		Color fill = new(0.93f, 0.86f, 0.72f, 1f);
		Color edge = new(0.35f, 0.24f, 0.14f, 1f);
		(Vector2 Center, Vector2 Radius)[] pads =
		{
			(new Vector2(32f, 22f), new Vector2(14f, 12f)),
			(new Vector2(14f, 38f), new Vector2(6f, 7.5f)),
			(new Vector2(25f, 48f), new Vector2(6f, 7.5f)),
			(new Vector2(39f, 48f), new Vector2(6f, 7.5f)),
			(new Vector2(50f, 38f), new Vector2(6f, 7.5f)),
		};
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				Color color = clear;
				foreach ((Vector2 center, Vector2 radius) in pads)
				{
					float dx = (x + 0.5f - center.x) / radius.x;
					float dy = (y + 0.5f - center.y) / radius.y;
					float d = dx * dx + dy * dy;
					if (d <= 1f)
					{
						color = d > 0.72f ? edge : fill;
						break;
					}
				}
				texture.SetPixel(x, y, color);
			}
		}
		texture.Apply();
		return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
	}
}

/// <summary>
/// Zum Testen (nur mit devcommands): Zucht-Stufe des zahmen Tiers unter dem Fadenkreuz setzen, z. B. „gc_tierstufe 40",
/// damit man die Befehle (ab Stufe 10) prüfen kann, ohne erst zu züchten.
/// </summary>
internal sealed class CareLevelCommand : Jotunn.Entities.ConsoleCommand
{
	public override string Name => "gc_tierstufe";

	public override string Help => "GC_Server Tierpflege: Zucht-Stufe (0–150) des zahmen Tiers unter dem Fadenkreuz setzen (Test)";

	public override bool IsCheat => true;

	public override void Run(string[] args)
	{
		Player? player = Player.m_localPlayer;
		Character? animal = player != null ? player.GetHoverCreature() : null;
		Tameable? tame = animal != null ? animal.GetComponent<Tameable>() : null;
		if (animal == null || tame == null || !animal.IsTamed() || tame.m_nview == null || !tame.m_nview.IsValid())
		{
			global::Console.instance.Print("gc_tierstufe: Schau ein zahmes Tier an.");
			return;
		}
		if (args.Length < 1 || !float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float level))
		{
			global::Console.instance.Print($"Aufruf: gc_tierstufe <0 bis {SpeciesLink.MaxLevel(animal):0}>");
			return;
		}
		level = Mathf.Clamp(level, 0f, SpeciesLink.MaxLevel(animal));
		if (!tame.m_nview.IsOwner())
		{
			tame.m_nview.ClaimOwnership();
		}
		tame.m_nview.GetZDO().Set(Care.LevelHash, level);
		global::Console.instance.Print($"gc_tierstufe: {tame.GetHoverName()} hat jetzt Zucht-Stufe {level:0}.");
	}
}

// ---------------------------------------------------------------------- Futter: wer hat es fallen lassen?

/// <summary>Merkt sich, dass gerade der eigene Spieler etwas fallen lässt (Humanoid.DropItem → ItemDrop.DropItem).</summary>
[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DropItem))]
internal static class CareDropPatch
{
	internal static bool Dropping;

	private static void Prefix(Humanoid __instance) => Dropping = __instance == Player.m_localPlayer;

	private static void Finalizer() => Dropping = false;
}

/// <summary>Das fallengelassene Item merkt sich den Spieler und seine Tierpflege-Stufe (Fütterer, Züchter).</summary>
[HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.DropItem))]
internal static class CareDropMarkPatch
{
	private static void Postfix(ItemDrop __result)
	{
		Player? local = Player.m_localPlayer;
		if (CareDropPatch.Dropping && local != null)
		{
			long session = Links.Session(local);
			Care.MarkDropper(__result, session, Care.SkillOfSession(session));
		}
	}
}

/// <summary>
/// Gepflücktes zählt wie Hingeworfenes (Gorduan, 18.10.2026): Valheim lässt Beeren, Gerste usw. beim Besitzer des Strauchs
/// entstehen (Pickable.RPC_Pick → Drop → Instantiate), nicht aus dem Inventar des Pflückers; ohne diese Markierung hatte
/// ein Tier, das nur Gepflücktes fraß, keinen Fütterer. Absender des RPC ist der Pflücker (auch Sense und PlantEverything
/// gehen über RPC_Pick).
/// </summary>
[HarmonyPatch(typeof(Pickable), "RPC_Pick")]
internal static class CarePickPatch
{
	internal static long Picker;
	internal static float Skill;

	private static void Prefix(long sender)
	{
		Picker = sender;
		Skill = Care.SkillOfSession(sender);
	}

	private static void Finalizer() => Picker = 0L;
}

/// <summary>Dasselbe für einzelne Gegenstände zum Aufheben in der Welt (PickableItem.RPC_Pick → Drop → Instantiate).</summary>
[HarmonyPatch(typeof(PickableItem), "RPC_Pick")]
internal static class CarePickItemPatch
{
	private static void Prefix(long sender)
	{
		CarePickPatch.Picker = sender;
		CarePickPatch.Skill = Care.SkillOfSession(sender);
	}

	private static void Finalizer() => CarePickPatch.Picker = 0L;
}

/// <summary>Jedes beim Pflücken entstandene Item (Pickable.Drop und PickableItem.Drop rufen OnCreateNew) bekommt den Pflücker.</summary>
[HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.OnCreateNew), typeof(ItemDrop), typeof(bool))]
internal static class CarePickMarkPatch
{
	private static void Postfix(ItemDrop item)
	{
		if (CarePickPatch.Picker != 0L)
		{
			Care.MarkDropper(item, CarePickPatch.Picker, CarePickPatch.Skill);
		}
	}
}

/// <summary>Fressen entfernt das Futter, bevor Tameable davon erfährt: den Fallenlasser vorher auslesen.</summary>
[HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.RemoveOne))]
internal static class CareConsumeSourcePatch
{
	internal static ItemDrop? Item;
	internal static long Dropper;
	internal static float DropperSkill;

	private static void Prefix(ItemDrop __instance)
	{
		Item = __instance;
		ZDO? zdo = __instance.m_nview != null && __instance.m_nview.IsValid() ? __instance.m_nview.GetZDO() : null;
		Dropper = zdo != null ? zdo.GetLong(Care.DropperHash, 0L) : 0L;
		DropperSkill = zdo != null ? zdo.GetFloat(Care.DropperSkillHash, -1f) : -1f;
	}
}

/// <summary>Ein Tier hat gefressen (beim Besitzer des Tiers): Erfahrung für den, der das Futter fallen ließ oder pflückte.</summary>
[HarmonyPatch(typeof(Tameable), "OnConsumedItem")]
internal static class CareFeedPatch
{
	private static void Postfix(Tameable __instance, ItemDrop item)
	{
		if (item != null && CareConsumeSourcePatch.Item == item)
		{
			Care.Fed(__instance, CareConsumeSourcePatch.Dropper, CareConsumeSourcePatch.DropperSkill);
		}
		CareConsumeSourcePatch.Item = null;
	}
}

/// <summary>Ein Tier wird zahm (beim Besitzer): Erfahrung für seinen Fütterer, sonst den nächsten Spieler.</summary>
[HarmonyPatch(typeof(Tameable), "Tame")]
internal static class CareTamePatch
{
	private static void Prefix(Tameable __instance, out bool __state) => __state = __instance.IsTamed();

	private static void Postfix(Tameable __instance, bool __state)
	{
		if (__state || !__instance.IsTamed() || __instance.m_character == null || __instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner())
		{
			return;
		}
		Care.Grant(Care.CaretakerOf(__instance.m_character, __instance.m_nview.GetZDO(), 30f), Care.TameXp.Value);
	}
}

/// <summary>Ein Tier an einer Leine (oder Lokis Trugbild) tötet etwas: Erfahrung für den Halter (beim Besitzer des Opfers).</summary>
[HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
internal static class CareKillPatch
{
	private static void Prefix(Character __instance)
	{
		if (!Care.Enabled.Value || __instance.IsPlayer() || __instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner())
		{
			return;
		}
		Character? attacker = __instance.m_lastHit?.GetAttacker();
		if (attacker == null || attacker.IsPlayer() || !attacker.IsTamed() || attacker.m_nview == null || !attacker.m_nview.IsValid())
		{
			return;
		}
		if (!__instance.IsTamed())
		{
			// Erfahrung fürs Tier (Gorduan, 18.10.2026): je Stufe des Opfers, nur Monster und wilde Tiere
			AnimalExperience.Give(attacker, Care.AnimalKillXp.Value * Mathf.Max(1, __instance.GetLevel()));
		}
		ZDO zdo = attacker.m_nview.GetZDO();
		long holder = zdo.GetLong(Plugin.HolderHash, 0L);
		if (holder == 0L)
		{
			holder = zdo.GetLong(Illusions.IllusionHash, 0L);
		}
		if (holder != 0L)
		{
			Care.Grant(holder, Care.KillXp.Value * Mathf.Max(1, __instance.GetLevel()));
		}
	}
}

/// <summary>Streicheln (E am zahmen Tier ohne Leine in der Hand): Erfahrung mit Abklingzeit je Tier.</summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.Interact))]
internal static class CarePetPatch
{
	private static void Postfix(Tameable __instance, Humanoid user, bool hold, bool alt, bool __result)
	{
		Player? local = Player.m_localPlayer;
		if (!__result || hold || alt || local == null || user != local || !__instance.IsTamed() || Control.Held(local) != null)
		{
			return;
		}
		if (__instance.m_character != null && (Illusions.IsIllusion(__instance.m_character) || Care.Summoned(__instance.m_character)))
		{
			return; // Trugbilder und Beschwörungen sind keine Tiere zum Pflegen
		}
		Care.Pet(local, __instance);
	}
}

/// <summary>Hinweis am zahmen Tier, wie Valheims „[Umschalt + E] Umbenennen“: „[Alt + E] Boni“ (Gorduan, 16.10.2026).</summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.GetHoverText))]
internal static class CareInfoHoverPatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Tameable __instance, ref string __result)
	{
		Character? animal = __instance.m_character;
		if (string.IsNullOrEmpty(__result) || !Care.Enabled.Value || Care.Skill == Skills.SkillType.None || animal == null || !__instance.IsTamed()
			|| Illusions.IsIllusion(animal) || Care.Summoned(animal))
		{
			return;
		}
		__result += "\n" + Localization.instance.Localize(GcLoc.Get("gcs_hover_info"));
	}
}

/// <summary>
/// Tierpflege in der Skill-Liste (Gorduan, 16.10.2026): ein Satz und die aktuellen Boni auf deiner Stufe, sonst nichts.
/// Valheims Tooltip übersetzt seinen Text erst beim Anzeigen, deshalb setzt das Öffnen der Liste das Wort neu.
/// </summary>
[HarmonyPatch(typeof(SkillsDialog), nameof(SkillsDialog.Setup))]
internal static class CareSkillTextPatch
{
	private static void Prefix(Player player)
	{
		if (player == null || Care.Skill == Skills.SkillType.None || Localization.instance == null)
		{
			return;
		}
		string text = GcLoc.Get("gcs_skill_care_desc");
		if (Care.Enabled.Value)
		{
			float level = player.GetSkillLevel(Care.Skill);
			// Leben, Schaden, Tempo halb und mal 5/12; feste Heilung nach voller Stufe, prozentuale mal 5/12; Zähmen voll (Gorduan)
			float skill = Care.BonusShare * Care.SkillShare * level;
			text += "\n" + GcLoc.Get("gcs_skill_care_now", Mathf.RoundToInt(2f * skill), Mathf.RoundToInt(skill), Mathf.RoundToInt(0.5f * skill),
				(0.05f * level).ToString("0.#"), (Care.BonusShare * Care.HealPercentPerLevel * 100f * level).ToString("0.##"), (1f + 2f * level / 100f).ToString("0.#"));
		}
		Localization.instance.AddWord("gcs_skill_care_desc", text);
		Localization.instance.m_cache.EvictAll(); // Localize merkt sich übersetzte Texte: sonst bliebe der alte Wert stehen
	}
}

// ---------------------------------------------------------------------- Geburt, Aufwachsen, Schlüpfen

[HarmonyPatch(typeof(Procreation), "Procreate")]
internal static class CareProcreatePatch
{
	private static void Prefix(Procreation __instance)
	{
		if (__instance.m_nview != null && __instance.m_nview.IsValid() && __instance.m_nview.IsOwner())
		{
			Care.Birth = new Care.Spawning { Parent = __instance }; // nur beim Besitzer gebiert es
		}
	}

	private static void Finalizer() => Care.Birth = null;
}

[HarmonyPatch(typeof(Growup), "GrowUpdate")]
internal static class CareGrowupPatch
{
	private static void Prefix(Growup __instance)
	{
		ZNetView? view = __instance.m_nview;
		// immer beim Besitzer: Stufe, Name, Pfosten und Leine ziehen vom Jungtier zum großen Tier um
		Care.Birth = view != null && view.IsValid() && view.IsOwner()
			? new Care.Spawning { Source = __instance, Level = view.GetZDO().GetFloat(Care.LevelHash, 0f) }
			: null;
	}

	private static void Postfix(Growup __instance)
	{
		Character? born = Care.Birth?.Born;
		if (born != null)
		{
			Young.Relink(__instance.GetComponent<Character>(), born); // erst jetzt: das große Tier ist fertig aufgebaut
		}
	}

	private static void Finalizer() => Care.Birth = null;
}

[HarmonyPatch(typeof(EggGrow), "GrowUpdate")]
internal static class CareEggPatch
{
	private static void Prefix(EggGrow __instance)
	{
		float level = Care.EggLevel(__instance.m_item != null ? __instance.m_item.m_itemData : null);
		Care.Birth = level > 0f ? new Care.Spawning { Source = __instance, Level = level } : null;
	}

	private static void Finalizer() => Care.Birth = null;
}

[HarmonyPatch(typeof(Character), "Awake")]
internal static class CareBornPatch
{
	private static void Postfix(Character __instance)
	{
		if (Care.Birth != null && !__instance.IsPlayer())
		{
			Care.OnBorn(__instance);
		}
	}
}

[HarmonyPatch(typeof(ItemDrop), "Awake")]
internal static class CareEggLaidPatch
{
	private static void Postfix(ItemDrop __instance)
	{
		if (Care.Birth?.Parent != null)
		{
			Care.OnEgg(__instance);
		}
	}
}

/// <summary>Gezüchtete Tiere: „(Lvl X)" hinter dem Namen.</summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.GetHoverName))]
internal static class CareNamePatch
{
	private static void Postfix(Tameable __instance, ref string __result)
	{
		int level = Care.AnimalLevel(__instance);
		if (level >= 1)
		{
			__result += " (Lvl " + level + ")";
		}
	}
}

// ---------------------------------------------------------------------- Wirkung beim Besitzer

/// <summary>Tempo der Tiere mit Tierpflege-Bonus.</summary>
[HarmonyPatch(typeof(SEMan), nameof(SEMan.ApplyStatusEffectSpeedMods))]
internal static class CareSpeedPatch
{
	private static void Postfix(SEMan __instance, ref float speed)
	{
		if (Care.Animals.Count > 0 && __instance.m_character != null && Care.Animals.TryGetValue(__instance.m_character, out Care.Stats stats) && stats.Speed > 0f)
		{
			speed *= 1f + stats.Speed;
		}
	}
}

/// <summary>Schaden der Tiere: Leinen-Bonus und Tierpflege.</summary>
[HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyAttack))]
internal static class CareAttackPatch
{
	private static void Postfix(SEMan __instance, ref HitData hitData)
	{
		if (Care.Animals.Count > 0 && __instance.m_character != null && Care.Animals.TryGetValue(__instance.m_character, out Care.Stats stats) && stats.Damage > 0f)
		{
			hitData.m_damage.Modify(1f + stats.Damage);
		}
	}
}

/// <summary>Zähmen geht je Tierpflege-Stufe des besten Spielers in der Nähe 2 % schneller (Stufe 100: dreimal so schnell).</summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.DecreaseRemainingTime))]
internal static class CareTamingPatch
{
	private static void Prefix(Tameable __instance, ref float time)
	{
		if (!Care.Enabled.Value)
		{
			return;
		}
		float level = Care.NearbyLevel(__instance.transform.position);
		if (level > 0f)
		{
			time *= 1f + 2f * level / 100f;
		}
	}
}

// ---------------------------------------------------------------------- Eier mit Zucht-Stufe

/// <summary>
/// Eier verschiedener Zucht-Stufe stapeln sich nicht (Inventar, Truhen, Boden). Valheim stapelt nur nach Name, Qualität
/// und Weltstufe; sonst bekäme ein ganzer Stapel die Stufe des Eis, auf das gestapelt wurde (oder verlöre sie).
/// </summary>
internal static class EggStacks
{
	private static readonly Dictionary<string, bool> EggByPrefab = new();

	/// <summary>Das Item, das Inventory.AddItem gerade einsortiert (FindFreeStackItem kennt es nicht).</summary>
	internal static ItemDrop.ItemData? Adding;

	/// <summary>Ein Ei, aus dem ein Tier schlüpft (Prefab mit EggGrow).</summary>
	internal static bool IsEgg(ItemDrop.ItemData? item)
	{
		GameObject? prefab = item?.m_dropPrefab;
		if (prefab == null)
		{
			return false;
		}
		if (!EggByPrefab.TryGetValue(prefab.name, out bool egg))
		{
			egg = prefab.GetComponent<EggGrow>() != null;
			EggByPrefab[prefab.name] = egg;
		}
		return egg;
	}

	/// <summary>Zwei Eier mit verschiedener Zucht-Stufe (auch: eins mit, eins ohne).</summary>
	internal static bool Mismatch(ItemDrop.ItemData? a, ItemDrop.ItemData? b) =>
		a != null && b != null && IsEgg(a) && Mathf.Abs(Care.EggLevel(a) - Care.EggLevel(b)) > 0.01f;
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData) })]
internal static class EggAddPatch
{
	private static void Prefix(ItemDrop.ItemData item) => EggStacks.Adding = EggStacks.IsEgg(item) ? item : null;

	private static void Finalizer() => EggStacks.Adding = null;
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData), typeof(Vector2i) })]
internal static class EggAddAtPatch
{
	private static void Prefix(ItemDrop.ItemData item) => EggStacks.Adding = EggStacks.IsEgg(item) ? item : null;

	private static void Finalizer() => EggStacks.Adding = null;
}

/// <summary>Freier Stapel für ein Ei: nur einer mit derselben Zucht-Stufe.</summary>
[HarmonyPatch(typeof(Inventory), "FindFreeStackItem")]
internal static class EggFreeStackPatch
{
	private static void Postfix(Inventory __instance, string name, int quality, float worldLevel, ref ItemDrop.ItemData? __result)
	{
		ItemDrop.ItemData? adding = EggStacks.Adding;
		if (adding == null || __result == null || !EggStacks.Mismatch(adding, __result))
		{
			return;
		}
		__result = null;
		foreach (ItemDrop.ItemData item in __instance.m_inventory)
		{
			if (item.m_shared.m_name == name && item.m_quality == quality && item.m_stack < item.m_shared.m_maxStackSize
				&& item.m_worldLevel == worldLevel && !EggStacks.Mismatch(adding, item))
			{
				__result = item;
				return;
			}
		}
	}
}

/// <summary>Ziehen auf einen Stapel (Inventar, Truhe, „Alles nehmen"): Eier verschiedener Stufe gelten als verschieden.</summary>
[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.IsSameType))]
internal static class EggSameTypePatch
{
	private static void Postfix(ItemDrop.ItemData __instance, ItemDrop.ItemData other, ref bool __result)
	{
		if (__result && EggStacks.Mismatch(__instance, other))
		{
			__result = false;
		}
	}
}

/// <summary>Am Boden fasst Valheim Gegenstände bei mehr als 200 in der Nähe zusammen; Eier nicht (sie schlüpfen nur einzeln).</summary>
[HarmonyPatch(typeof(ItemDrop), "AutoStackItems")]
internal static class EggAutoStackPatch
{
	private static bool Prefix(ItemDrop __instance) => !(Care.Enabled.Value && EggStacks.IsEgg(__instance.m_itemData));
}

/// <summary>Tooltip eines Eis: Zucht-Stufe des Tiers, das daraus schlüpft.</summary>
[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool) })]
internal static class EggTooltipPatch
{
	private static void Postfix(ItemDrop.ItemData item, ref string __result)
	{
		float level = item != null && EggStacks.IsEgg(item) ? Care.EggLevel(item) : 0f;
		if (level >= 1f)
		{
			__result += "\n" + GcLoc.Get("gcs_egg_level", Mathf.FloorToInt(level));
		}
	}
}

// ---------------------------------------------------------------------- Countdowns über Tieren und Eiern

/// <summary>
/// Countdowns bis 20 m, nur Anzeige, jeder Spieler für sich (Gorduan): „Zahm in hh:mm:ss“ beim Zähmen (16.10.2026),
/// „Geburt in …“ (bei Eierlegern „Ei in …“) über trächtigen Tieren, „Ausgewachsen in …“ über Jungtieren und „Schlüpft
/// in …“ über warmen Eiern (18.10.2026).
///
/// Zähmen: Valheim zählt die Zähmzeit (ZDO tameTimeLeft) alle 3 s herunter, solange das Tier satt und nicht verängstigt
/// ist, schneller mit Tierpflege (CareTamingPatch) und mit Valheims Zähm-Bonus (TamingBoost, je Spieler in der Nähe
/// doppelt). Die Anzeige rechnet mit demselben Tempo und zählt zwischen den Schritten sekundengenau weiter. Ist das Tier
/// hungrig oder verängstigt, steht die Zeit still und der Grund dahinter.
///
/// Geburt, Aufwachsen und Schlüpfen rechnen wie Valheim mit der Weltzeit: trächtig seit ZDO pregnant
/// (Procreation.m_pregnancyDuration), Jungtier seit ZDO spawntime (Growup.m_growTime), Ei warm seit ZDO GrowStart
/// (EggGrow.m_growTime; „kalt“ zeigt Valheim selbst am Ei). Vor der Trächtigkeit gibt es keine feste Zeit, Valheim
/// würfelt Liebespunkte aus. Bei 00:00:00 kommt das Tier mit Valheims nächster Prüfung, wenige Sekunden später.
/// </summary>
internal static class Countdown
{
	private const float ShowRange = 20f;
	private const float Step = 3f; // so oft zählt Valheim die Zähmzeit herunter (Tameable.TamingUpdate)

	private struct Sample
	{
		public float Remaining;
		public float Since;
	}

	private static readonly Dictionary<Tameable, Sample> Samples = new();
	private static readonly HashSet<GameObject> Shown = new();
	private static readonly HashSet<GameObject> Seen = new();
	private static readonly List<GameObject> Stale = new();
	private static readonly List<Tameable> Forget = new();
	private static readonly List<Player> InRange = new();
	private static float next;

	internal static void Tick()
	{
		if (Time.time < next)
		{
			return;
		}
		next = Time.time + 0.5f;
		Player? local = Player.m_localPlayer;
		Chat? chat = Chat.instance;
		if (local == null || chat == null || ZNet.instance == null)
		{
			if (chat != null)
			{
				foreach (GameObject shown in Shown)
				{
					if (shown != null)
					{
						chat.ClearNpcText(shown); // z. B. nach dem Tod: die Anzeigen laufen nicht von selbst ab
					}
				}
			}
			Shown.Clear();
			Samples.Clear();
			return;
		}
		Vector3 me = local.transform.position;
		Seen.Clear();
		foreach (Character animal in Character.GetAllCharacters())
		{
			if (animal == null || animal.IsPlayer() || animal.IsDead() || (animal.transform.position - me).sqrMagnitude > ShowRange * ShowRange)
			{
				continue;
			}
			try
			{
				string? text = animal.IsTamed() ? LevelUp(local, animal) ?? Breeding(animal) : Taming(animal);
				if (text != null)
				{
					Show(chat, animal.gameObject, animal.GetTopPoint().y - animal.transform.position.y + 0.4f, text);
				}
			}
			catch (Exception e)
			{
				Report(e); // ein einzelnes Tier (z. B. eine Kreatur aus einer anderen Mod) hält die übrigen Anzeigen nicht auf
			}
		}
		foreach (ItemDrop item in ItemDrop.s_instances)
		{
			if (item == null || (item.transform.position - me).sqrMagnitude > ShowRange * ShowRange)
			{
				continue;
			}
			try
			{
				string? text = Hatching(item);
				if (text != null)
				{
					Show(chat, item.gameObject, 0.6f, text);
				}
			}
			catch (Exception e)
			{
				Report(e);
			}
		}
		Stale.Clear();
		foreach (GameObject shown in Shown)
		{
			if (!Seen.Contains(shown))
			{
				Stale.Add(shown);
			}
		}
		foreach (GameObject gone in Stale)
		{
			Shown.Remove(gone);
			if (gone != null)
			{
				chat.ClearNpcText(gone); // fertig (zahm, geboren, groß, geschlüpft), gestorben oder weiter als 20 m weg
			}
		}
		Forget.Clear();
		foreach (KeyValuePair<Tameable, Sample> entry in Samples)
		{
			Tameable tame = entry.Key;
			if (tame == null || !Seen.Contains(tame.gameObject))
			{
				Forget.Add(entry.Key); // zerstört (Unity-null) oder nicht mehr in der Zähmung
			}
		}
		foreach (Tameable tame in Forget)
		{
			Samples.Remove(tame);
		}
	}

	private static bool reported;

	/// <summary>Fehler bei einem einzelnen Tier oder Ei nur einmal ins Log, die Anzeige läuft für die anderen weiter.</summary>
	private static void Report(Exception e)
	{
		if (!reported)
		{
			reported = true;
			Plugin.Log.LogWarning("Countdown: " + e);
		}
	}

	private static void Show(Chat chat, GameObject go, float height, string text)
	{
		Seen.Add(go);
		Shown.Add(go);
		Chat.NpcText? shown = chat.FindNpcText(go);
		if (shown == null)
		{
			chat.SetNpcText(go, Vector3.up * height, ShowRange + 10f, 0f, "", text, false); // 20 m prüft Tick, die Kamera steht dahinter
		}
		else if (shown.m_text != text)
		{
			shown.m_text = text;
			shown.UpdateText();
		}
	}

	/// <summary>Wildes Tier, das gerade gezähmt wird: „Zahm in …“, sonst null.</summary>
	private static string? Taming(Character animal)
	{
		Tameable? tame = animal.GetComponent<Tameable>();
		GcTaming taming = SpeciesLink.Taming(animal);
		if (taming == GcTaming.Blocked)
		{
			return null; // Unbjørn und abgeschaltete Arten (GC_Tierarten) werden nicht gezähmt
		}
		// Valheim zähmt nur mit Monster-KI; Hirsch, Hase und Robbe zähmt GC_Tierarten mit der einfachen Tier-KI
		BaseAI? ai = tame != null && tame.m_monsterAI != null ? tame.m_monsterAI : taming == GcTaming.SimpleAI ? animal.GetBaseAI() : null;
		if (tame == null || tame.m_nview == null || !tame.m_nview.IsValid() || ai == null)
		{
			return null;
		}
		float remaining = tame.GetRemainingTime();
		bool hungry = tame.IsHungry();
		if (hungry && remaining >= tame.m_tamingTime - 0.01f)
		{
			return null; // noch nie gefüttert: kein Zähmen im Gang
		}
		float now = Time.time;
		if (!Samples.TryGetValue(tame, out Sample sample) || Mathf.Abs(sample.Remaining - remaining) > 0.001f)
		{
			sample = new Sample { Remaining = remaining, Since = now }; // neuer Schritt beim Besitzer des Tiers
			Samples[tame] = sample;
		}
		bool frightened = ai.IsAlerted();
		float seconds = remaining / Speed(tame, animal.transform.position);
		if (!hungry && !frightened)
		{
			seconds -= Mathf.Min(now - sample.Since, Step); // zwischen den Schritten weiterzählen, höchstens einen Schritt
		}
		string text = GcLoc.Get("gcs_tame_in", Clock(seconds));
		if (hungry || frightened)
		{
			text += " (" + Localization.instance.Localize(frightened ? "$hud_tamefrightened" : "$hud_tamehungry") + ")";
		}
		return text;
	}

	private static readonly Dictionary<ZDOID, long> Celebrated = new();

	/// <summary>
	/// Aufstieg durch Erfahrung (Gorduan, 18.10.2026): 4 s lang „Lvl X!“ über dem Tier, einmal Valheims Aufstiegs-Effekt
	/// (wie bei Skills des Spielers). Der Besitzer schreibt den Zeitpunkt ins Tier, so sieht es jeder in der Nähe.
	/// </summary>
	private static string? LevelUp(Player local, Character animal)
	{
		ZNetView? view = animal.m_nview;
		if (view == null || !view.IsValid())
		{
			return null;
		}
		ZDO zdo = view.GetZDO();
		long at = zdo.GetLong(AnimalExperience.LevelUpHash, 0L);
		if (at == 0L)
		{
			return null;
		}
		double age = (ZNet.instance.GetTime() - new DateTime(at)).TotalSeconds;
		if (age < -2.0 || age > 4.0)
		{
			return null; // Uhren der Rechner weichen etwas ab
		}
		if (!Celebrated.TryGetValue(zdo.m_uid, out long seen) || seen != at)
		{
			if (Celebrated.Count > 500)
			{
				Celebrated.Clear();
			}
			Celebrated[zdo.m_uid] = at;
			PlayLevelUp(local, animal);
		}
		return GcLoc.Get("gcs_levelup", Mathf.FloorToInt(zdo.GetFloat(Care.LevelHash, 0f)));
	}

	private static EffectList? levelUpLocal;

	/// <summary>
	/// Valheims Aufstiegs-Effekt am Tier, mit ihm mitlaufend. Nur die Teile ohne ZNetView: Jeder Spieler in der Nähe erzeugt
	/// ihn selbst, vernetzte Teile entstünden sonst einmal je Spieler (Review 18.10.2026).
	/// </summary>
	private static void PlayLevelUp(Player local, Character animal)
	{
		if (levelUpLocal == null)
		{
			List<EffectList.EffectData> parts = new();
			foreach (EffectList.EffectData data in local.m_skillLevelupEffects.m_effectPrefabs)
			{
				if (data != null && data.m_prefab != null && data.m_prefab.GetComponent<ZNetView>() == null)
				{
					parts.Add(data);
				}
			}
			levelUpLocal = new EffectList { m_effectPrefabs = parts.ToArray() };
		}
		levelUpLocal.Create(animal.GetTopPoint(), Quaternion.identity, animal.transform);
	}

	/// <summary>Zahmes Tier: Jungtier „Ausgewachsen in …“, trächtig „Geburt in …“ bzw. „Ei in …“, sonst null.</summary>
	private static string? Breeding(Character animal)
	{
		ZNetView? view = animal.m_nview;
		if (view == null || !view.IsValid() || Illusions.IsIllusion(animal))
		{
			return null;
		}
		ZDO zdo = view.GetZDO();
		DateTime now = ZNet.instance.GetTime();
		Growup? young = animal.GetComponent<Growup>();
		if (young != null)
		{
			long born = zdo.GetLong(ZDOVars.s_spawnTime, 0L); // wie BaseAI.GetTimeSinceSpawned, das Growup prüft
			return born != 0L ? GcLoc.Get("gcs_grown_in", Clock(young.m_growTime - (float)(now - new DateTime(born)).TotalSeconds)) : null;
		}
		Procreation? parent = animal.GetComponent<Procreation>();
		long pregnant = parent != null ? zdo.GetLong(ZDOVars.s_pregnant, 0L) : 0L;
		if (parent == null || pregnant == 0L)
		{
			return null;
		}
		float left = parent.m_pregnancyDuration - (float)(now - new DateTime(pregnant)).TotalSeconds;
		bool egg = parent.m_offspring != null && parent.m_offspring.GetComponent<ItemDrop>() != null; // Hühner legen Eier
		return GcLoc.Get(egg ? "gcs_egg_in" : "gcs_birth_in", Clock(left));
	}

	/// <summary>Warmes Ei am Boden: „Schlüpft in …“, sonst null (kalt oder gestapelt wächst es nicht).</summary>
	private static string? Hatching(ItemDrop item)
	{
		EggGrow? egg = item.GetComponent<EggGrow>();
		if (egg == null || item.m_nview == null || !item.m_nview.IsValid() || item.m_itemData.m_stack > 1)
		{
			return null;
		}
		float start = item.m_nview.GetZDO().GetFloat(ZDOVars.s_growStart, 0f);
		return start > 0f ? GcLoc.Get("gcs_hatch_in", Clock(start + egg.m_growTime - (float)ZNet.instance.GetTimeSeconds())) : null;
	}

	/// <summary>Zähmsekunden je Sekunde, wie beim Besitzer: Tierpflege in der Nähe, EpicLoot-Zähmhilfe und Valheims Zähm-Bonus.</summary>
	private static float Speed(Tameable tame, Vector3 position)
	{
		float speed = Care.Enabled.Value ? 1f + 2f * Care.NearbyLevel(position) / 100f : 1f;
		speed *= BondTamingPatch.Factor(position);
		InRange.Clear();
		Player.GetPlayersInRange(position, tame.m_tamingSpeedMultiplierRange, InRange);
		foreach (Player player in InRange)
		{
			if (player.GetSEMan().HaveStatusAttribute(StatusEffect.StatusAttribute.TamingBoost))
			{
				speed *= tame.m_tamingBoostMultiplier;
			}
		}
		return Mathf.Max(0.01f, speed);
	}

	private static string Clock(float seconds)
	{
		int total = Mathf.Max(0, Mathf.CeilToInt(seconds));
		return (total / 3600).ToString("00") + ":" + (total / 60 % 60).ToString("00") + ":" + (total % 60).ToString("00");
	}
}
