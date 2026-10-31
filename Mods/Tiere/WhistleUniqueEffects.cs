using System;
using System.Collections.Generic;
using System.Globalization;
using GC.Shared;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Wirkung der neuen Sondereffekte einzigartiger Tierpfeifen (Gorduan, 27.10.2026; Werte in LeashUniques.cs). Wie bei den
/// mythischen Pfeifen (Whistles.Mythic) geschieht alles einmal beim Pfiff und nur, wenn ein Tier gehorcht hat. Ausnahmen:
/// Fenrirs Heulen hängt am Buff des Pfiffs (Whistles.Buff), Hermóðs Weckruf wirkt auch ohne Tier an der Leine (ohnmächtige
/// Tiere hängen an keiner Leine mehr).
///  * Huldras Lockruf: Gegner bis Lure m um den Pfeifenden nehmen 5 s lang das nächste gehorchende Tier als Ziel. Der Besitzer
///    des Gegners setzt das Ziel (RPC), WhistleLurePatch hält es.
///  * Eirs Weise: der Pfeifende, Spieler bis 10 m und die gehorchenden Tiere heilen über 5 s Song % ihres Höchstlebens
///    (Statuseffekt GC_WhistleSong, heilt beim Besitzer der Figur; ohne Kategorie, Met und Tränke bleiben frei).
///  * Ráns Netz: Gegner bis 12 m sind 3 s lang Net % langsamer (Statuseffekt GC_WhistleNet, mit Valheims Teer-Effekt).
///  * Fenrirs Heulen: +Pack % Schaden und Tempo je weiterem Tier an deinen Leinen (höchstens 5), solange der Pfiff wirkt.
///  * Hermóðs Weckruf: ohnmächtige zahme Tiere bis 30 m stehen sofort auf (Bonds.Wake beim Besitzer, volles Leben gibt
///    das Ohnmacht-System), 60 s Abklingzeit.
///  * Urleere: die gehorchenden Tiere sind Void s unverwundbar (Zeitstempel im Tier-ZDO, geprüft beim Besitzer), Gegner bis
///    15 m taumeln (mit Gjallarhorns Hall zusammen, Whistles.Mythic), alle Befehlspausen enden; 60 s Abklingzeit.
/// Die Abklingzeiten stehen im Spieler (Player.m_customData, Weltzeit) und gelten so über das Ausloggen hinaus.
/// </summary>
internal static class WhistleUniqueEffects
{
	private const float LureSeconds = 5f;
	private const float SongRadius = 10f;
	private const float SongSeconds = 5f;
	private const float NetRadius = 12f;
	private const float NetSeconds = 3f;
	private const int PackMax = 5;
	private const float WakeRadius = 30f;
	private const float WakeCooldown = 60f;
	private const float VoidCooldown = 60f;
	private const float VoidMax = 30f;

	/// <summary>Urleere: Gegner in diesem Umkreis um den Pfeifenden taumeln (Whistles.Mythic, wie Gjallarhorns Hall).</summary>
	internal const float VoidStaggerRadius = 15f;

	private const string LureRpc = "GC_WhistleLure";
	private const string WakeRpc = "GC_WhistleWake";
	private const string VoidRpc = "GC_WhistleVoid";

	/// <summary>Im Spieler (m_customData): Weltzeit in Sekunden, ab der Weckruf bzw. Urleere wieder wirken.</summary>
	private const string WakeKey = "gc_whistle_wake";
	private const string VoidKey = "gc_whistle_void";

	internal const string SongName = "GC_WhistleSong";
	internal const string NetName = "GC_WhistleNet";
	internal static readonly int SongHash = SongName.GetStableHashCode();
	internal static readonly int NetHash = NetName.GetStableHashCode();

	/// <summary>Im Tier: unverwundbar bis (Weltzeit in Ticks), gesetzt vom Besitzer (Urleere).</summary>
	internal static readonly int VoidHash = "gc_whistle_void_until".GetStableHashCode();
	private static readonly long VoidMaxTicks = TimeSpan.FromSeconds(VoidMax + 1f).Ticks;

	private struct LureHold
	{
		public Character Animal;
		public float Until;
	}

	/// <summary>Beim Besitzer des Gegners: gelockte Gegner und das Tier, das sie bis Until angreifen.</summary>
	private static readonly Dictionary<MonsterAI, LureHold> Lured = new();
	private static readonly List<MonsterAI> LureDone = new();
	private static readonly List<Character> Near = new();
	private static readonly List<Character> Sleepers = new();
	private static readonly List<Player> Players = new();
	private static bool effectsCreated;
	private static EffectList? netLook;
	private static bool netLookSearched;

	internal static bool AnyLured => Lured.Count > 0;

	/// <summary>Hat die Pfeife einen der Effekte, die beim Pfiff einmal wirken (Lockruf, Weise, Netz, Urleere)?</summary>
	private static bool AnyOnWhistle(BondStats bond) => bond.Lure > 0f || bond.Song > 0f || bond.Net > 0f || bond.Void > 0f;

	// ------------------------------------------------------------------ Anmeldung

	/// <summary>
	/// Statuseffekte für Weise und Netz bei Jötunn anmelden (einmal, aus Whistles.CreateItems). Alle Spieler und der Server haben
	/// GC_Tiere: Ein Effekt, den SEMan an den Besitzer einer Figur schickt, findet dort dieselbe Vorlage in der ObjectDB.
	/// </summary>
	internal static void CreateStatusEffects()
	{
		if (effectsCreated)
		{
			return;
		}
		effectsCreated = true;
		try
		{
			GcWhistleSongEffect song = ScriptableObject.CreateInstance<GcWhistleSongEffect>();
			song.name = SongName;
			song.m_name = "$gc_leg_eir";
			song.m_ttl = SongSeconds;
			GcWhistleNetEffect net = ScriptableObject.CreateInstance<GcWhistleNetEffect>();
			net.name = NetName;
			net.m_name = "$gc_leg_ran";
			net.m_ttl = NetSeconds;
			bool songAdded = ItemManager.Instance.AddStatusEffect(new CustomStatusEffect(song, false));
			bool netAdded = ItemManager.Instance.AddStatusEffect(new CustomStatusEffect(net, false));
			if (!songAdded || !netAdded)
			{
				Plugin.Log.LogWarning($"Tierpfeifen: Statuseffekte nicht vollständig angemeldet (Eirs Weise {songAdded}, Ráns Netz {netAdded}).");
			}
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"Tierpfeifen: Statuseffekte für Eirs Weise und Ráns Netz nicht angemeldet: {e.Message}");
		}
	}

	/// <summary>Bei jedem Spielstart (Whistles.RegisterRpcs): eigene RPCs, Gemerktes der letzten Sitzung vergessen.</summary>
	internal static void RegisterRpcs()
	{
		Lured.Clear();
		netLook = null;
		netLookSearched = false;
		ZRoutedRpc.instance?.Register<ZDOID, ZDOID, float>(LureRpc, RPC_Lure);
		ZRoutedRpc.instance?.Register<ZDOID>(WakeRpc, RPC_Wake);
		ZRoutedRpc.instance?.Register<ZDOID, float>(VoidRpc, RPC_Void);
	}

	// ------------------------------------------------------------------ beim Pfiff

	/// <summary>
	/// Einmal je Pfiff, wenn mindestens ein Tier gehorcht hat (aus Whistles.Mythic): Lockruf, Weise, Netz und Urleere. Liefert
	/// den Umkreis, in dem Gegner zusätzlich taumeln (Urleere hat gewirkt: 15 m), sonst 0.
	/// </summary>
	internal static float Trigger(Player player, BondStats bond, List<Link> obeyed)
	{
		if (!AnyOnWhistle(bond) || obeyed.Count == 0)
		{
			return 0f;
		}
		if (bond.Lure > 0f)
		{
			Lure(player, bond.Lure, obeyed);
		}
		if (bond.Song > 0f)
		{
			Sing(player, bond.Song, obeyed);
		}
		if (bond.Net > 0f)
		{
			CastNet(player, bond.Net);
		}
		return bond.Void > 0f && OpenVoid(player, bond.Void, obeyed) ? VoidStaggerRadius : 0f;
	}

	/// <summary>
	/// Fenrirs Heulen (aus Whistles.Buff): +Pack % Schaden und Tempo je weiterem Tier an den Leinen des Halters, höchstens 5.
	/// Kommt in die Pfiff-Werte des Tiers und gilt deshalb genau so lange wie der Pfiff (Bonds.Current).
	/// </summary>
	internal static void AddPack(BondStats whistleBond, float pack, Link link)
	{
		if (pack <= 0f)
		{
			return;
		}
		int others = 0;
		foreach (Link other in Links.Active)
		{
			if (other != link && other.Holder == link.Holder && other.Animal != null && !other.Traveling && !other.Animal.IsDead())
			{
				others++;
			}
		}
		float bonus = pack * Mathf.Min(others, PackMax);
		whistleBond.Damage += bonus;
		whistleBond.Speed += bonus;
	}

	/// <summary>
	/// Huldras Lockruf: Gegner (keine Bosse, keine schlafenden) bis radius m um den Pfeifenden greifen 5 s lang das nächste
	/// gehorchende Tier an, das sie als Feind sehen. Gesetzt wird das Ziel beim Besitzer des Gegners (RPC, auch an sich selbst).
	/// </summary>
	private static void Lure(Player player, float radius, List<Link> obeyed)
	{
		ZRoutedRpc? rpc = ZRoutedRpc.instance;
		if (rpc == null)
		{
			return;
		}
		Near.Clear();
		Character.GetCharactersInRange(player.transform.position, radius, Near);
		foreach (Character enemy in Near)
		{
			if (enemy == null || enemy.IsBoss() || enemy.m_baseAI is not MonsterAI ai || ai.IsSleeping())
			{
				continue;
			}
			Character? animal = NearestAnimal(enemy, obeyed);
			long owner = animal != null ? enemy.m_nview.GetZDO().GetOwner() : 0L;
			if (animal != null && owner != 0L)
			{
				rpc.InvokeRoutedRPC(owner, LureRpc, enemy.GetZDOID(), animal.GetZDOID(), LureSeconds);
			}
		}
	}

	/// <summary>Das gehorchende Tier, das dem Gegner am nächsten ist und für das er ein Feind ist (Whistles.Foe).</summary>
	private static Character? NearestAnimal(Character enemy, List<Link> obeyed)
	{
		Character? best = null;
		float bestDistance = float.MaxValue;
		foreach (Link link in obeyed)
		{
			Character animal = link.Animal;
			if (animal == null || animal.IsDead() || Bonds.IsFainted(animal) || !Whistles.Foe(animal, enemy))
			{
				continue;
			}
			float distance = Vector3.Distance(animal.transform.position, enemy.transform.position);
			if (distance < bestDistance)
			{
				best = animal;
				bestDistance = distance;
			}
		}
		return best;
	}

	/// <summary>Eirs Weise: der Pfeifende und Spieler bis 10 m (jeder heilt bei sich selbst), dazu die gehorchenden Tiere.</summary>
	private static void Sing(Player player, float percent, List<Link> obeyed)
	{
		Players.Clear();
		Player.GetPlayersInRange(player.transform.position, SongRadius, Players);
		if (!Players.Contains(player))
		{
			Players.Add(player);
		}
		foreach (Player other in Players)
		{
			if (other != null && !other.IsDead())
			{
				other.GetSEMan()?.AddStatusEffect(SongHash, resetTime: true, itemLevel: 0, skillLevel: percent);
			}
		}
		foreach (Link link in obeyed)
		{
			Character animal = link.Animal;
			if (animal != null && !animal.IsDead() && !Bonds.IsFainted(animal))
			{
				animal.GetSEMan()?.AddStatusEffect(SongHash, resetTime: true, itemLevel: 0, skillLevel: percent);
			}
		}
	}

	/// <summary>Ráns Netz: Gegner (keine Bosse) bis 12 m um den Pfeifenden, der Statuseffekt geht über SEMan an ihren Besitzer.</summary>
	private static void CastNet(Player player, float percent)
	{
		Near.Clear();
		Character.GetCharactersInRange(player.transform.position, NetRadius, Near);
		foreach (Character enemy in Near)
		{
			if (Whistles.Foe(player, enemy) && !enemy.IsBoss())
			{
				enemy.GetSEMan()?.AddStatusEffect(NetHash, resetTime: true, itemLevel: 0, skillLevel: percent);
			}
		}
	}

	/// <summary>
	/// Urleere: Abklingzeit prüfen (Meldung, wenn sie noch läuft), dann die gehorchenden Tiere unverwundbar machen und die
	/// Befehlspausen aller Tiere an deinen Leinen beenden (Control.EndPause). True: hat gewirkt, Gegner taumeln (Whistles.Mythic).
	/// </summary>
	private static bool OpenVoid(Player player, float seconds, List<Link> obeyed)
	{
		float wait = CooldownLeft(player, VoidKey, VoidCooldown);
		if (wait > 0f)
		{
			Gc.TopLeft(GcLoc.Get("gcwh_void_wait", Mathf.CeilToInt(wait)));
			return false;
		}
		seconds = Mathf.Clamp(seconds, 0f, VoidMax);
		foreach (Link link in obeyed)
		{
			Character animal = link.Animal;
			if (animal != null && !animal.IsDead() && !Bonds.IsFainted(animal))
			{
				Shield(animal, seconds);
			}
		}
		foreach (Link link in Links.Active)
		{
			if (link.Holder == player)
			{
				Control.EndPause(link);
			}
		}
		StartCooldown(player, VoidKey, VoidCooldown);
		Gc.TopLeft(GcLoc.Get("gcwh_void", Mathf.RoundToInt(seconds)));
		return true;
	}

	/// <summary>Zeitstempel „unverwundbar bis“ ins Tier, beim Besitzer (angeleinte Tiere besitzt ihr Halter, sonst per RPC).</summary>
	private static void Shield(Character animal, float seconds)
	{
		ZNetView? view = animal.m_nview;
		if (view == null || !view.IsValid() || ZNet.instance == null)
		{
			return;
		}
		if (view.IsOwner())
		{
			view.GetZDO().Set(VoidHash, ZNet.instance.GetTime().AddSeconds(seconds).Ticks);
			return;
		}
		long owner = view.GetZDO().GetOwner();
		if (owner != 0L)
		{
			ZRoutedRpc.instance?.InvokeRoutedRPC(owner, VoidRpc, animal.GetZDOID(), seconds);
		}
	}

	/// <summary>
	/// Hermóðs Weckruf (aus Whistles.Send/Sic, vor der Suche nach Tieren an der Leine): ohnmächtige zahme Tiere bis 30 m um den
	/// Pfeifenden wachen auf. Ohne ohnmächtiges Tier in der Nähe geschieht nichts, auch keine Abklingzeit. True: hat geweckt.
	/// </summary>
	internal static bool Wake(Player player, ItemDrop.ItemData item)
	{
		if (!Bonds.Of(item).Wake)
		{
			return false;
		}
		Near.Clear();
		Sleepers.Clear();
		Character.GetCharactersInRange(player.transform.position, WakeRadius, Near);
		foreach (Character animal in Near)
		{
			if (animal != null && !animal.IsPlayer() && !animal.IsDead() && animal.IsTamed() && Bonds.IsFainted(animal))
			{
				Sleepers.Add(animal);
			}
		}
		if (Sleepers.Count == 0)
		{
			return false;
		}
		float wait = CooldownLeft(player, WakeKey, WakeCooldown);
		if (wait > 0f)
		{
			Gc.TopLeft(GcLoc.Get("gcwh_wake_wait", Mathf.CeilToInt(wait)));
			return false;
		}
		foreach (Character animal in Sleepers)
		{
			WakeAnimal(animal);
		}
		StartCooldown(player, WakeKey, WakeCooldown);
		Gc.TopLeft(Sleepers.Count == 1 ? GcLoc.Get("gcwh_wake_one", Sleepers[0].GetHoverName()) : GcLoc.Get("gcwh_wake_many", Sleepers.Count));
		return true;
	}

	/// <summary>
	/// Wecken beim Besitzer (SDK: Markierungen an fremden Tieren per Besitzer-RPC). Dort setzt Bonds.Wake die Ohnmacht ab, das
	/// Ohnmacht-System (Bonds.HoldFainted) heilt voll und lässt das Tier aufstehen. Ohne Besitzer übernimmt Bonds.Wake selbst.
	/// </summary>
	private static void WakeAnimal(Character animal)
	{
		ZNetView? view = animal.m_nview;
		if (view == null || !view.IsValid())
		{
			return;
		}
		long owner = view.GetZDO().GetOwner();
		if (view.IsOwner() || owner == 0L || ZRoutedRpc.instance == null)
		{
			Bonds.Wake(animal);
			return;
		}
		ZRoutedRpc.instance.InvokeRoutedRPC(owner, WakeRpc, animal.GetZDOID());
	}

	// ------------------------------------------------------------------ Abklingzeiten im Spieler

	/// <summary>Sekunden, bis der Effekt wieder wirkt (0 = jetzt). Weiter in der Zukunft als eine ganze Abklingzeit: aus einer anderen Welt, vergessen.</summary>
	private static float CooldownLeft(Player player, string key, float length)
	{
		if (ZNet.instance == null || player.m_customData == null || !player.m_customData.TryGetValue(key, out string text)
			|| !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double until))
		{
			return 0f;
		}
		double left = until - ZNet.instance.GetTimeSeconds();
		return left > 0.0 && left <= length + 1.0 ? (float)left : 0f;
	}

	private static void StartCooldown(Player player, string key, float length)
	{
		if (ZNet.instance != null && player.m_customData != null)
		{
			player.m_customData[key] = (ZNet.instance.GetTimeSeconds() + length).ToString("0.###", CultureInfo.InvariantCulture);
		}
	}

	// ------------------------------------------------------------------ beim Besitzer

	private static Character? Find(ZDOID id)
	{
		GameObject? go = ZNetScene.instance != null && !id.IsNone() ? ZNetScene.instance.FindInstance(id) : null;
		Character? character = go != null ? go.GetComponent<Character>() : null;
		return character != null && character.m_nview != null && character.m_nview.IsValid() ? character : null;
	}

	/// <summary>Lockruf beim Besitzer des Gegners: Ziel setzen und merken; WhistleLurePatch hält es bis Until.</summary>
	private static void RPC_Lure(long sender, ZDOID enemyId, ZDOID animalId, float seconds)
	{
		Character? enemy = Find(enemyId);
		Character? animal = Find(animalId);
		if (enemy == null || animal == null || !enemy.m_nview.IsOwner() || enemy.m_baseAI is not MonsterAI ai)
		{
			return;
		}
		if (enemy.IsDead() || enemy.IsBoss() || enemy.IsTamed() || enemy.m_aiCannotTargetOthers || animal.IsDead() || ai.IsSleeping()
			|| !BaseAI.IsEnemy(enemy, animal))
		{
			return;
		}
		ForgetDone();
		Lured[ai] = new LureHold { Animal = animal, Until = Time.time + Mathf.Clamp(seconds, 0f, 10f) };
		ai.m_targetCreature = animal;
		ai.m_targetStatic = null;
		ai.m_lastKnownTargetPos = animal.transform.position;
		ai.m_beenAtLastPos = false;
		ai.SetTargetInfo(animal.GetZDOID());
		if (!ai.IsAlerted())
		{
			ai.SetAlerted(true);
		}
	}

	/// <summary>Das Tier, das dieser Gegner gerade angreifen muss, sonst null (abgelaufen, Tier tot, ohnmächtig oder kein Feind mehr).</summary>
	internal static Character? LureTarget(MonsterAI ai)
	{
		if (!Lured.TryGetValue(ai, out LureHold hold))
		{
			return null;
		}
		Character animal = hold.Animal;
		if (Time.time >= hold.Until || animal == null || animal.IsDead() || !animal.gameObject.activeInHierarchy
			|| (Bonds.AnyFainted && Bonds.IsFainted(animal)) || ai.m_character == null || !BaseAI.IsEnemy(ai.m_character, animal))
		{
			Lured.Remove(ai);
			return null;
		}
		return animal;
	}

	/// <summary>Abgelaufene und zerstörte Einträge entfernen (bei jedem neuen Lockruf).</summary>
	private static void ForgetDone()
	{
		LureDone.Clear();
		foreach (KeyValuePair<MonsterAI, LureHold> entry in Lured)
		{
			if (entry.Key == null || entry.Value.Animal == null || Time.time >= entry.Value.Until)
			{
				LureDone.Add(entry.Key!);
			}
		}
		foreach (MonsterAI ai in LureDone)
		{
			Lured.Remove(ai);
		}
	}

	/// <summary>Weckruf beim Besitzer: Ohnmacht absetzen (Bonds.HoldFainted weckt das Tier mit vollem Leben).</summary>
	private static void RPC_Wake(long sender, ZDOID id)
	{
		Character? animal = Find(id);
		if (animal != null && animal.m_nview.IsOwner() && animal.IsTamed())
		{
			Bonds.Wake(animal);
		}
	}

	/// <summary>Urleere beim Besitzer des Tiers: Zeitstempel setzen.</summary>
	private static void RPC_Void(long sender, ZDOID id, float seconds)
	{
		Character? animal = Find(id);
		if (animal != null && ZNet.instance != null && animal.m_nview.IsOwner() && animal.IsTamed())
		{
			animal.m_nview.GetZDO().Set(VoidHash, ZNet.instance.GetTime().AddSeconds(Mathf.Clamp(seconds, 0f, VoidMax)).Ticks);
		}
	}

	/// <summary>
	/// Urleere: Ist das zahme Tier gerade unverwundbar? Nur beim Besitzer (dort laufen Treffer und Schaden), aus dem Tier-ZDO:
	/// gilt also auch, wenn der Besitz wechselt. Ein Stempel weiter in der Zukunft als die längste Urleere gilt nicht.
	/// </summary>
	internal static bool Untouchable(Character character)
	{
		ZNetView? view = character != null ? character.m_nview : null;
		if (view == null || !view.IsValid() || !view.IsOwner() || ZNet.instance == null || character!.IsPlayer() || !character.IsTamed())
		{
			return false;
		}
		long until = view.GetZDO().GetLong(VoidHash, 0L);
		if (until == 0L)
		{
			return false;
		}
		long now = ZNet.instance.GetTime().Ticks;
		return now < until && until - now <= VoidMaxTicks;
	}

	// ------------------------------------------------------------------ Aussehen

	/// <summary>
	/// Ráns Netz sichtbar machen: Valheims Teer-Effekt (sonst Frost) als Start-Effekt, aus der ObjectDB beim ersten Gegner, der
	/// sich verfängt. Ohne passenden Effekt bleibt das Netz unsichtbar und wirkt trotzdem.
	/// </summary>
	internal static void NetLook(StatusEffect effect)
	{
		if (effect.m_startEffects != null && effect.m_startEffects.HasEffects())
		{
			return;
		}
		if (!netLookSearched && ObjectDB.instance != null)
		{
			netLookSearched = true;
			foreach (int hash in new[] { SEMan.s_statusEffectTared, SEMan.s_statusEffectFrost })
			{
				StatusEffect? look = ObjectDB.instance.GetStatusEffect(hash);
				if (look != null && look.m_startEffects != null && look.m_startEffects.HasEffects())
				{
					netLook = look.m_startEffects;
					break;
				}
			}
		}
		if (netLook != null)
		{
			effect.m_startEffects = netLook;
		}
	}
}

/// <summary>
/// Eirs Weise (Gorduan, 27.10.2026): heilt beim Besitzer der Figur in fünf Schritten (einmal je Sekunde) den Anteil ihres
/// Höchstlebens, den der Pfiff mitschickt (SEMan.AddStatusEffect, skillLevel = Prozent). Keine Kategorie: Met und Tränke
/// bleiben unberührt. Ein neuer Pfiff beginnt die 5 s von vorn, er zählt nicht doppelt.
/// </summary>
public sealed class GcWhistleSongEffect : StatusEffect
{
	private const int Steps = 5;
	private float percent;
	private int healed;

	public override void SetLevel(int itemLevel, float skillLevel)
	{
		percent = Mathf.Clamp(skillLevel, 0f, 100f);
	}

	public override void ResetTime()
	{
		base.ResetTime();
		healed = 0;
	}

	public override void UpdateStatusEffect(float dt)
	{
		base.UpdateStatusEffect(dt);
		float step = m_ttl > 0f ? m_ttl / Steps : 1f;
		int due = Mathf.Min(Steps, Mathf.FloorToInt(m_time / step));
		while (healed < due)
		{
			healed++;
			if (percent > 0f && m_character != null && !m_character.IsDead())
			{
				m_character.Heal(m_character.GetMaxHealth() * percent / 100f / Steps);
			}
		}
	}
}

/// <summary>
/// Ráns Netz (Gorduan, 27.10.2026): beim Besitzer des Gegners Tempo × (1 − Prozent); den Prozentwert schickt der Pfiff mit
/// (skillLevel). Sichtbar mit Valheims Teer-Effekt (WhistleUniqueEffects.NetLook).
/// </summary>
public sealed class GcWhistleNetEffect : StatusEffect
{
	private float slow;

	public override void Setup(Character character)
	{
		WhistleUniqueEffects.NetLook(this);
		base.Setup(character);
	}

	public override void SetLevel(int itemLevel, float skillLevel)
	{
		slow = Mathf.Clamp01(skillLevel / 100f);
	}

	public override void ModifySpeed(float baseSpeed, ref float speed, Character character, Vector3 dir)
	{
		speed *= 1f - slow;
	}
}

/// <summary>
/// Huldras Lockruf beim Besitzer des Gegners: 5 s lang greift er das Tier an, auf das der Pfiff ihn gelockt hat. Wie beim
/// Angriffsbefehl der Leine (LeashTargetPatch) nach Valheims Zielwahl gesetzt; danach wählt Valheim wieder selbst.
/// </summary>
[HarmonyPatch(typeof(MonsterAI), "UpdateTarget")]
internal static class WhistleLurePatch
{
	private static void Postfix(MonsterAI __instance, ref bool canHearTarget, ref bool canSeeTarget)
	{
		if (!WhistleUniqueEffects.AnyLured)
		{
			return;
		}
		Character? animal = WhistleUniqueEffects.LureTarget(__instance);
		if (animal == null)
		{
			return;
		}
		if (__instance.m_targetCreature != animal)
		{
			__instance.m_targetCreature = animal;
			__instance.SetTargetInfo(animal.GetZDOID());
		}
		__instance.m_targetStatic = null;
		canSeeTarget = __instance.CanSeeTarget(animal);
		canHearTarget = true; // nicht aus den Augen verlieren: hinlaufen, nicht zur letzten bekannten Stelle
		__instance.m_timeSinceSensedTargetCreature = 0f;
		if (!__instance.IsAlerted())
		{
			__instance.SetAlerted(true);
		}
	}
}

/// <summary>
/// Urleere: Treffer auf ein unverwundbares Tier wirken nicht (beim Besitzer, vor allen anderen Präfixen). HarmonyX ruft die
/// übrigen Präfixe trotzdem auf; sie sehen einen leeren Treffer (kein Schaden, kein Statuseffekt, kein Stoß, kein Taumeln),
/// Dornen werfen deshalb nichts zurück.
/// </summary>
[HarmonyPatch(typeof(Character), nameof(Character.RPC_Damage))]
internal static class WhistleVoidHitPatch
{
	[HarmonyPriority(Priority.First)]
	private static bool Prefix(Character __instance, HitData hit)
	{
		if (hit == null || !WhistleUniqueEffects.Untouchable(__instance))
		{
			return true;
		}
		hit.m_damage = new HitData.DamageTypes();
		hit.m_statusEffectHash = 0;
		hit.m_pushForce = 0f;
		hit.m_staggerMultiplier = 0f;
		if (DamageText.instance != null)
		{
			DamageText.instance.ShowText(DamageText.TextType.Immune, hit.m_point, 0f); // wie Valheim bei Immunität: „0“
		}
		return false;
	}
}

/// <summary>Urleere: auch Brand, Gift, Lava und Ertrinken schaden einem unverwundbaren Tier nicht (laufen nicht über RPC_Damage).</summary>
[HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
internal static class WhistleVoidDamagePatch
{
	[HarmonyPriority(Priority.First)]
	private static bool Prefix(Character __instance) => !WhistleUniqueEffects.Untouchable(__instance);
}
