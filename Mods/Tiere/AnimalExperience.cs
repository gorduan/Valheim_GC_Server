using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Erfahrung der Tiere (Gorduan, 18.10.2026): Zahme Tiere sammeln im Kampf Erfahrung und steigen bis Stufe 150 auf. Eine Stufe
/// kostet so viel wie bei Valheims Waffen-Skills, (Stufe + 1)^1,5 × 0,5 + 0,5 (Skills.Skill.GetNextLevelRequirement): 1 bei
/// Stufe 1, 17 bei 10, 178 bei 50, 501 bei 100, 920 bei 150. Niedrige Tiere steigen also schnell, hohe langsam.
///
/// Erfahrung gibt es wie bei Waffen für jeden Angriff, der einen Gegner trifft: Valheim ruft dafür auch bei zahmen Tieren
/// Character.RaiseSkill auf (Attack, Projectile, Aoe). Gorduan: 1 je Angriff, deshalb zählt 1 statt Valheims Menge (Nahkampf
/// auf Kreaturen gäbe 1,5). Nur wenn derselbe Angriff im selben Bild einen echten Gegner traf (Character.Damage beim
/// Angreifer, NoteHit): keine Trainingspuppe, keine Bogenscheibe, nichts mit m_aiSkipTarget (Review 18.10.2026). Dazu fürs Töten
/// AnimalKillExperience × Stufe des Opfers (ohne Stern 5, 1 Stern 10, 2 Sterne 15). Alles mal Faktor aus dem Menü und
/// Valheims Welt-Einstellung für den Skill-Gewinn. Beim Aufstieg verfällt der Überschuss wie bei Valheim.
///
/// Es steigt dieselbe Stufe wie aus der Zucht („(Lvl X)“, ZDO gc_care_level). Lokis Trugbilder geben ihre Erfahrung an ihr
/// echtes Tier, Beschwörungen bekommen keine. Gerechnet wird beim Besitzer des Tiers; wer es nicht besitzt, schickt die
/// Erfahrung dorthin. Den Aufstieg sehen alle Spieler bis 20 m als „Lvl X!“ (Countdown).
/// </summary>
internal static class AnimalExperience
{
	/// <summary>Im Tier: gesammelte Erfahrung zur nächsten Stufe.</summary>
	internal static readonly int XpHash = "gc_care_xp".GetStableHashCode();
	/// <summary>Im Tier: Weltzeit (Ticks) des letzten Aufstiegs durch Erfahrung, für die Anzeige bei allen Spielern.</summary>
	internal static readonly int LevelUpHash = "gc_care_levelup".GetStableHashCode();
	private const string Rpc = "GC_AnimalXP";

	internal static bool On => Care.Enabled.Value && Care.AnimalXp.Value;

	/// <summary>Bei jedem Spielstart (Care.RegisterRpcs): Erfahrung für eigene Tiere von anderen Rechnern empfangen.</summary>
	internal static void RegisterRpc() => ZRoutedRpc.instance?.Register<ZDOID, float>(Rpc, RPC_Xp);

	/// <summary>Valheims Waffen-Kurve: so viel Erfahrung kostet der Schritt von dieser Stufe zur nächsten.</summary>
	internal static float Requirement(float level) => Mathf.Pow(Mathf.Floor(level + 1f), 1.5f) * 0.5f + 0.5f;

	/// <summary>Größte Erfahrung, die ein Ereignis bringen kann (Kill: mit Mods bis 10 Sterne); begrenzt fremde Nachrichten.</summary>
	private static float MaxEvent() => Mathf.Max(2f, Care.AnimalKillXp.Value * 11f);

	/// <summary>Je zahmem Angreifer das Bild, in dem er zuletzt einen echten Gegner traf.</summary>
	private static readonly Dictionary<Character, int> ValidHits = new();

	/// <summary>Beim Angreifer (Character.Damage): Treffer auf einen echten Gegner merken; Puppen und Spieler zählen nicht.</summary>
	internal static void NoteHit(Character attacker, Character victim)
	{
		if (victim.IsPlayer() || victim.IsTamed() || victim.GetFaction() == Character.Faction.TrainingDummy || victim.m_aiSkipTarget)
		{
			return;
		}
		if (ValidHits.Count > 256)
		{
			ValidHits.Clear();
		}
		ValidHits[attacker] = Time.frameCount;
	}

	/// <summary>Traf dieser Angreifer gerade (dieses oder letztes Bild) einen echten Gegner? Zählt einmal.</summary>
	internal static bool TakeHit(Character attacker)
	{
		if (!ValidHits.TryGetValue(attacker, out int frame))
		{
			return false;
		}
		ValidHits.Remove(attacker);
		return Time.frameCount - frame <= 1;
	}

	/// <summary>Erfahrung für ein angreifendes Tier oder Trugbild, auf jedem Rechner aufrufbar.</summary>
	internal static void Give(Character? attacker, float amount, bool forwarded = false)
	{
		if (!On || attacker == null || !(amount > 0f) || attacker.IsPlayer() || attacker.m_nview == null || !attacker.m_nview.IsValid()) // !(> 0) fängt auch NaN
		{
			return;
		}
		if (!attacker.m_nview.IsOwner())
		{
			if (!forwarded)
			{
				Send(attacker.m_nview.GetZDO(), amount); // nur sein Besitzer kennt Trugbild und echtes Tier
			}
			return;
		}
		Character? animal = Illusions.IsIllusion(attacker) ? Illusions.RealOf(attacker) : attacker;
		if (animal == null || animal.IsPlayer() || !animal.IsTamed() || animal.IsDead() || Care.Summoned(animal) || animal.m_nview == null || !animal.m_nview.IsValid())
		{
			return;
		}
		if (!animal.m_nview.IsOwner())
		{
			if (!forwarded || animal != attacker)
			{
				Send(animal.m_nview.GetZDO(), amount); // Trugbild beim Halter, Tier gehört gerade jemand anderem
			}
			return;
		}
		ZDO zdo = animal.m_nview.GetZDO();
		Add(zdo, amount * LeashUniqueEffects.Wisdom(zdo)); // Mímirs Weisheit der Leine: +x % (Gorduan, 27.10.2026)
	}

	private static void Send(ZDO zdo, float amount)
	{
		long owner = zdo.GetOwner();
		if (owner != 0L && ZRoutedRpc.instance != null)
		{
			ZRoutedRpc.instance.InvokeRoutedRPC(owner, Rpc, zdo.m_uid, amount);
		}
	}

	private static void RPC_Xp(long sender, ZDOID id, float amount)
	{
		GameObject? go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(id) : null;
		Character? character = go != null ? go.GetComponent<Character>() : null;
		if (character != null)
		{
			Give(character, Mathf.Clamp(amount, 0f, MaxEvent()), true); // höchstens einmal weitergeben, kein Hin und Her
		}
	}

	/// <summary>
	/// Beim Besitzer: Erfahrung zählen, bei genug eine Stufe höher (höchstens die Höchststufe der Art: Lox 300, Wolf und Bär
	/// 200, Unbjørn 300, sonst 150), der Rest verfällt wie bei Valheim.
	/// </summary>
	private static void Add(ZDO zdo, float amount)
	{
		float level = zdo.GetFloat(Care.LevelHash, 0f);
		float max = SpeciesLink.MaxLevel(zdo.GetPrefab());
		if (level >= max || ZNet.instance == null)
		{
			return;
		}
		float xp = zdo.GetFloat(XpHash, 0f) + amount * Care.AnimalXpFactor.Value * Game.m_skillGainRate;
		if (xp < Requirement(level))
		{
			zdo.Set(XpHash, xp);
			return;
		}
		zdo.Set(Care.LevelHash, Mathf.Min(level + 1f, max));
		zdo.Set(XpHash, 0f);
		zdo.Set(LevelUpHash, ZNet.instance.GetTime().Ticks);
	}
}

/// <summary>
/// Treffer: Nach jedem Angriff, der einen Gegner trifft, ruft Valheim RaiseSkill des Angreifers auf, auch bei zahmen Tieren
/// (Character.RaiseSkill; dort nutzt Valheim es nur für Beschwörungen). Spieler haben ihre eigene Fassung (Player.RaiseSkill).
/// Läuft beim Besitzer des Angreifers. Zählt nur, wenn derselbe Angriff einen echten Gegner traf (AnimalXpDamagePatch).
/// </summary>
[HarmonyPatch(typeof(Character), nameof(Character.RaiseSkill))]
internal static class AnimalXpHitPatch
{
	private static void Postfix(Character __instance)
	{
		if (!__instance.IsPlayer() && __instance.IsTamed() && AnimalExperience.TakeHit(__instance))
		{
			AnimalExperience.Give(__instance, 1f); // Gorduan: 1 je Angriff, der trifft
		}
	}
}

/// <summary>
/// Treffer merken: Character.Damage läuft beim Angreifer, bevor Valheim den Schaden an den Besitzer des Opfers schickt
/// (Attack, Projectile, Aoe rufen es vor RaiseSkill auf).
/// </summary>
[HarmonyPatch(typeof(Character), nameof(Character.Damage))]
internal static class AnimalXpDamagePatch
{
	private static void Prefix(Character __instance, HitData hit)
	{
		if (!AnimalExperience.On || hit == null || hit.m_attacker.IsNone())
		{
			return;
		}
		Character? attacker = hit.GetAttacker();
		if (attacker != null && attacker != __instance && !attacker.IsPlayer() && attacker.IsTamed())
		{
			AnimalExperience.NoteHit(attacker, __instance);
		}
	}
}
