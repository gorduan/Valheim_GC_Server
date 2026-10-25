using System.Collections.Generic;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Verhalten an der Leine (Gorduans Regeln vom 13.10.2026):
///  * Abstand: Nur wenn der Halter läuft, kommt das Tier auf die Mindestlänge der Leine (2 bis 10 m je Stufe und
///    Qualität) heran und hält sie. Es rennt, sobald es zurückfällt, ist dabei aber an sein Tempo und seine Wegfindung
///    gebunden. Steht der Halter, bewegt es sich frei bis 90 % der Höchstlänge (läuft im Umkreis der Mindestlänge umher).
///  * Ziehen: Schafft es das nicht oder zerrt es im Kampf, dehnt sich die Leine über die Höchstlänge und verschleißt.
///    Über 15 % mehr zieht sie das Tier mit, der Halter ist dabei halb so schnell, und sie verschleißt weiter.
///    Über eine Kante, die tiefer ist als 4 m, zieht die Leine kein Tier (in Valheim nehmen Tiere zwar keinen
///    Fallschaden, aber ein Tier springt nicht in die Tiefe); dann spannt die Leine eben.
///  * Kampf: In Leinenreichweite kostet Kämpfen nichts. Ein Tier kämpft, bis der Gegner tot oder weiter als
///    ChaseDistance (Standard 20 m) von ihm weg ist; dann folgt es wieder dem Spieler.
///  * Gehorsam (EpicLoot, 0–100 %): Solange der Halter sich bewegt, läuft das Tier in seine Richtung mit und lässt sich
///    seltener ablenken (Gegner, Futter). Bei 100 % greift es unterwegs niemanden an. Darunter steigt mit jedem
///    fehlenden Prozent die Chance, und nahe Gegner werden eher angegriffen als ferne:
///    Chance = (1 − Gehorsam) × ChaseDistance ÷ Abstand (mindestens 1 m), gewürfelt alle 2 s je Gegner.
///  * Entwischt: Reißt die Leine, läuft das Tier frei herum und gehorcht keinem Befehl, bis es wieder angeleint wird.
///  * Zögern (Gorduan, 15.10.2026): Läuft der Halter los, überlegt ein Tier mit niedriger Mischstufe (Tier-Stufe und Tierpflege
///    des Halters 1:2) zufällig bis 2 s, bevor es folgt, und streift so lange weiter herum (Care.Hesitation). Ab
///    Mischstufe 30 folgt es sofort. Die Leine zieht in dieser Zeit wie immer.
///  * Länge: Mit Umschalt + Mausrad lässt sich die Leine bis 1 m kürzen; alle Bereiche schrumpfen im selben Verhältnis.
/// </summary>
internal static class Behaviour
{
	/// <summary>So tief darf es an der Leine hinuntergehen (ab 4 m beginnt in Valheim der Fallschaden der Spieler).</summary>
	internal const float SafeDrop = 4f;
	private const float DecisionTime = 2f; // wie Valheims Zielsuche in Spielernähe (alle 2 s)

	private struct Decision
	{
		public Character? Target;
		public bool Allowed;
		public float Until;
	}

	private struct LedgeCheck
	{
		public bool Ledge;
		public Vector3 Direction;
		public float Until;
	}

	private static readonly Dictionary<Character, Decision> Decisions = new();
	private static readonly Dictionary<Character, Decision> FoodDecisions = new();
	private static readonly Dictionary<Character, LedgeCheck> Ledges = new();
	private static readonly List<Character> Dead = new();
	private static readonly HashSet<BaseAI> Following = new(); // folgen gerade einem laufenden Halter
	private static int groundMask;

	/// <summary>Der Halter läuft: Das Tier folgt (merken, damit es beim Anhalten nicht zu einem alten Ziel zurückläuft).</summary>
	internal static void Follows(BaseAI ai) => Following.Add(ai);

	/// <summary>
	/// Der Halter steht: frei umherlaufen um ihn, im Umkreis von radius (Valheims eigene Zufallsbewegung, mit Pausen).
	/// Hat es eben noch gefolgt, bleibt es erst stehen und sucht sich nach einer Pause ein Ziel rund um den Halter. Läuft
	/// es noch zu einem alten Ziel weit weg (etwa aus einem Kampf), sucht es sofort ein neues rund um den Halter.
	/// </summary>
	internal static void Wander(BaseAI ai, Vector3 center, float dt, float radius)
	{
		if (Following.Remove(ai))
		{
			ai.ResetRandomMovement();
		}
		else if (!ai.m_reachedRandomMoveTarget && Utils.DistanceXZ(ai.m_randomMoveTarget, center) > radius * 1.5f)
		{
			ai.m_randomMoveUpdateTimer = 0f;
		}
		float range = ai.m_randomMoveRange;
		ai.m_randomMoveRange = Mathf.Max(1f, radius);
		try
		{
			ai.RandomMovement(dt, center, snapToGround: true);
		}
		finally
		{
			ai.m_randomMoveRange = range;
		}
	}

	/// <summary>Bewegt sich der Halter gerade (waagerecht mehr als 1 m/s)?</summary>
	internal static bool Moving(Player holder)
	{
		Vector3 velocity = holder.GetVelocity();
		velocity.y = 0f;
		return velocity.sqrMagnitude > 1f;
	}

	/// <summary>Halter des Tiers, wenn es an einer eigenen aktiven Leine hängt (nicht unterwegs) oder ein eigenes Trugbild ist.</summary>
	internal static Player? HolderOf(Character animal, out Link? link)
	{
		link = Links.Active.Count > 0 ? Links.ForAnimal(animal) : null;
		if (link != null)
		{
			return link.Traveling ? null : link.Holder;
		}
		return Illusions.IsIllusion(animal) ? Player.m_localPlayer : null;
	}

	/// <summary>Gehorsam: Greift das Tier diesen Gegner an, während der Halter läuft? Die Entscheidung gilt 2 s je Gegner.</summary>
	internal static bool MayAttack(Character animal, Character target, float obedience)
	{
		if (obedience >= 100f)
		{
			return false;
		}
		if (Decisions.TryGetValue(animal, out Decision decision) && decision.Target == target && Time.time < decision.Until)
		{
			return decision.Allowed;
		}
		float distance = Mathf.Max(1f, Vector3.Distance(animal.transform.position, target.transform.position));
		float chance = Mathf.Clamp01((1f - obedience / 100f) * Plugin.ChaseDistance.Value / distance);
		bool allowed = UnityEngine.Random.value < chance;
		Decisions[animal] = new Decision { Target = target, Allowed = allowed, Until = Time.time + DecisionTime };
		return allowed;
	}

	/// <summary>Gehorsam: Lässt das Tier Futter liegen, während der Halter läuft? Chance = Gehorsam, gewürfelt alle 2 s.</summary>
	internal static bool IgnoresFood(Character animal, float obedience)
	{
		if (FoodDecisions.TryGetValue(animal, out Decision decision) && Time.time < decision.Until)
		{
			return decision.Allowed;
		}
		bool ignore = UnityEngine.Random.value * 100f < obedience;
		FoodDecisions[animal] = new Decision { Allowed = ignore, Until = Time.time + DecisionTime };
		return ignore;
	}

	/// <summary>
	/// Läge die Zugrichtung über einer Kante, die tiefer ist als SafeDrop (und kein Wasser darunter)? Der Strahl kommt von
	/// SafeDrop über den Füßen (bergauf trifft er so den Hang von oben) und schaut je nach Tempo weiter voraus. Gecacht 0,1 s.
	/// </summary>
	internal static bool Ledge(Character animal, Vector3 direction)
	{
		if (!animal.IsOnGround() || animal.IsSwimming())
		{
			return false; // schwimmt oder ist schon in der Luft: nichts mehr zu entscheiden
		}
		if (Ledges.TryGetValue(animal, out LedgeCheck cached) && Time.time < cached.Until && Vector3.Dot(cached.Direction, direction) > 0.95f)
		{
			return cached.Ledge;
		}
		if (groundMask == 0)
		{
			groundMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
		}
		Vector3 feet = animal.transform.position;
		Vector3 velocity = animal.GetVelocity();
		velocity.y = 0f;
		float ahead = animal.GetRadius() + 0.8f + Mathf.Min(velocity.magnitude * 0.2f, 2f);
		Vector3 probe = feet + direction * ahead + Vector3.up * SafeDrop;
		bool ground = Physics.Raycast(probe, Vector3.down, out RaycastHit hit, SafeDrop * 2f, groundMask) && hit.point.y >= feet.y - SafeDrop;
		bool ledge = !ground && Floating.GetLiquidLevel(probe) < feet.y - SafeDrop;
		Ledges[animal] = new LedgeCheck { Ledge = ledge, Direction = direction, Until = Time.time + 0.1f };
		return ledge;
	}

	internal static void Cleanup()
	{
		if (Following.Count > 0)
		{
			Following.RemoveWhere(ai => ai == null);
		}
		if (Decisions.Count == 0 && FoodDecisions.Count == 0 && Ledges.Count == 0)
		{
			return;
		}
		Dead.Clear();
		foreach (Character animal in Decisions.Keys)
		{
			if (animal == null)
			{
				Dead.Add(animal!);
			}
		}
		foreach (Character animal in FoodDecisions.Keys)
		{
			if (animal == null)
			{
				Dead.Add(animal!);
			}
		}
		foreach (Character animal in Ledges.Keys)
		{
			if (animal == null)
			{
				Dead.Add(animal!);
			}
		}
		foreach (Character animal in Dead)
		{
			Decisions.Remove(animal);
			FoodDecisions.Remove(animal);
			Ledges.Remove(animal);
		}
	}

	internal static void Reset()
	{
		Decisions.Clear();
		FoodDecisions.Clear();
		Ledges.Clear();
		Following.Clear();
	}
}

/// <summary>
/// Kampf an der Leine: Valheim lässt gezähmte Tiere nur Gegner jagen, die nah am Spieler sind (Alarm-Umkreis um den,
/// dem sie folgen). An der Leine gilt Gorduans Regel: kämpfen, bis der Gegner tot oder mehr als ChaseDistance vom Tier weg
/// ist, dann wieder folgen. Dafür schaltet der Prefix nur diese Umkreis-Regel ab (Alarm-Umkreis kurz unendlich; sie ist
/// in UpdateTarget die einzige Stelle, die ihn liest); Valheims eigenes Aufgeben (30 s nicht wahrgenommen, 60 s kein
/// Angriff) bleibt. Dazu Gehorsam (unterwegs seltener angreifen) und: Ohnmächtige Tiere greift niemand an.
/// </summary>
[HarmonyPatch(typeof(MonsterAI), "UpdateTarget")]
internal static class LeashTargetPatch
{
	private struct State
	{
		public bool Leashed;
		public float AlertRange;
	}

	private static void Prefix(MonsterAI __instance, out State __state)
	{
		__state = default;
		if ((Links.Active.Count == 0 && !Illusions.Any) || __instance.m_character == null || Behaviour.HolderOf(__instance.m_character, out _) == null)
		{
			return;
		}
		__state.Leashed = true;
		__state.AlertRange = __instance.m_alertRange;
		__instance.m_alertRange = float.MaxValue;
	}

	private static void Postfix(MonsterAI __instance, State __state, ref bool canHearTarget, ref bool canSeeTarget)
	{
		Character? target = __instance.m_targetCreature;
		if (target != null && Bonds.AnyFainted && target.IsTamed() && Bonds.IsFainted(target))
		{
			Drop(__instance, ref canHearTarget, ref canSeeTarget); // ohnmächtig und unsichtbar: niemandes Ziel
			target = null;
		}
		if (!__state.Leashed)
		{
			return;
		}
		Character self = __instance.m_character;
		Player? holder = Behaviour.HolderOf(self, out Link? link);
		if (holder == null)
		{
			return;
		}
		// Befehl „angreifen" (Rechtsklick mit der Leine in der Hand): dieses Ziel, bis es tot, entkommen oder unerreichbar
		// ist (Control.Check), auch gegen den Gehorsam
		Character? ordered = link?.AttackTarget;
		if (ordered != null && !ordered.IsDead())
		{
			if (__instance.m_targetCreature != ordered)
			{
				__instance.m_targetCreature = ordered;
				__instance.SetTargetInfo(ordered.GetZDOID());
			}
			__instance.m_targetStatic = null;
			canSeeTarget = __instance.CanSeeTarget(ordered);
			canHearTarget = true; // nicht aus den Augen verlieren: es soll hinlaufen, nicht zur letzten Stelle
			// Valheims Aufgeben (30 s nicht wahrgenommen, 60 s ohne Angriff) setzte sonst jedes Bild Alarm und Ziel zurück;
			// den Befehl beendet Control.Check (Ziel tot, entkommen oder 30 s ohne eigenen Treffer)
			__instance.m_timeSinceSensedTargetCreature = 0f;
			__instance.m_timeSinceAttacking = Mathf.Min(__instance.m_timeSinceAttacking, 30f);
			if (!__instance.IsAlerted())
			{
				__instance.SetAlerted(true);
			}
			return;
		}
		if (target == null)
		{
			return;
		}
		if (link?.GoTo != null && !link.GoToReached)
		{
			Drop(__instance, ref canHearTarget, ref canSeeTarget); // Befehl „hingehen": unterwegs nicht ablenken lassen
			return;
		}
		if (Vector3.Distance(self.transform.position, target.transform.position) > Plugin.ChaseDistance.Value)
		{
			Drop(__instance, ref canHearTarget, ref canSeeTarget); // zu weit weg: Kampf abbrechen, dem Spieler folgen
			return;
		}
		if (Bonds.TryGet(self, out BondStats bond) && bond.Obedience > 0f && Behaviour.Moving(holder) && !Behaviour.MayAttack(self, target, bond.Obedience))
		{
			Drop(__instance, ref canHearTarget, ref canSeeTarget); // gehorsam: läuft mit, statt anzugreifen
		}
	}

	/// <summary>Alarm-Umkreis immer zurücksetzen, auch wenn UpdateTarget wirft (er wird danach in UpdateAI gebraucht).</summary>
	private static void Finalizer(MonsterAI __instance, State __state)
	{
		if (__state.Leashed)
		{
			__instance.m_alertRange = __state.AlertRange;
		}
	}

	private static void Drop(MonsterAI ai, ref bool canHearTarget, ref bool canSeeTarget)
	{
		ai.m_targetCreature = null;
		canHearTarget = false;
		canSeeTarget = false;
		ai.SetTargetInfo(ZDOID.None);
	}
}

/// <summary>
/// Folgen an der Leine (statt Valheims „bis 3 m gehen, erst ab 10 m rennen"): Läuft der Halter, hält das Tier die
/// Mindestlänge und rennt, sobald es weiter weg ist, über Valheims Wegfindung (MoveTo). Gehorsame Tiere laufen in deine
/// Richtung mit. Steht der Halter, bewegt sich das Tier frei (bis 90 % der Höchstlänge).
/// </summary>
[HarmonyPatch(typeof(BaseAI), nameof(BaseAI.Follow))]
internal static class LeashFollowPatch
{
	private static bool Prefix(BaseAI __instance, GameObject go, float dt)
	{
		if ((Links.Active.Count == 0 && !Illusions.Any) || go == null)
		{
			return true;
		}
		Character self = __instance.m_character;
		if (self == null)
		{
			return true;
		}
		Player? holder = Behaviour.HolderOf(self, out Link? link);
		if (holder == null || go != holder.gameObject)
		{
			return true;
		}
		if (link?.GoTo is Vector3 point)
		{
			// Befehl „hingehen" (Linksklick mit der Leine in der Hand): hinlaufen und dort warten; wird es weggedrängt, zurück
			float left = Utils.DistanceXZ(__instance.transform.position, point);
			float near = 2f + Links.Reach(self);
			if (!link.GoToReached || left > near)
			{
				// MoveTo meldet „fertig" auch, wenn es keinen Weg gibt: angekommen nur nah am Ziel. Endet der Weg früher (der
				// Rest ist nicht erreichbar), wartet es dort, wo es hinkam
				if (__instance.MoveTo(dt, point, 0.5f, left > 4f))
				{
					link.GoToReached = true;
					if (left > near)
					{
						link.GoTo = __instance.transform.position;
					}
				}
			}
			else
			{
				__instance.StopMoving();
			}
			return false;
		}
		// Mindestlänge der Leine (2 bis 10 m je Stufe): so nah bleibt das Tier, solange der Halter läuft; rennen erst, wenn
		// es 1 m weiter zurückliegt
		float length = link != null ? link.SlackUntil : Illusions.FollowLength(self); // Trugbilder: Länge ihrer Leine
		float runFrom = length + 1f;
		Vector3 position = __instance.transform.position;
		Vector3 holderPosition = go.transform.position;
		float distance = Links.Span(holderPosition, self); // wie die Leine misst (Höhenunterschiede bis 1,5 m zählen nicht)
		Vector3 velocity = holder.GetVelocity();
		velocity.y = 0f;
		bool moving = velocity.sqrMagnitude > 1f;
		if (link != null)
		{
			if (moving)
			{
				if (!link.HolderMoving)
				{
					// Gorduan: Läuft der Halter los, überlegt das Tier erst (freier Wille, 0–2 s je nach Mischstufe), dann folgt es
					link.HolderMoving = true;
					link.FollowAt = Time.time + Care.Hesitation(link);
					link.HesitateAt = holderPosition;
				}
				link.LastMoving = Time.time;
				if (Time.time < link.FollowAt)
				{
					Behaviour.Wander(__instance, link.HesitateAt, dt, length + Links.Reach(self)); // macht weiter wie im Stand
					return false;
				}
			}
			else if (link.HolderMoving && Time.time - link.LastMoving > 0.7f)
			{
				link.HolderMoving = false; // erst nach kurzem Stehen: Wenden oder Springen löst kein neues Zögern aus
			}
		}
		if (!moving)
		{
			if (link != null)
			{
				// Gorduan: Steht der Halter, bewegt sich das Tier frei bis 90 % der Höchstlänge (läuft im Umkreis der
				// Mindestlänge umher). Liegt es weiter weg, kommt es zurück, über der Höchstlänge rennend
				if (distance > link.FreeUntil)
				{
					__instance.MoveTo(dt, holderPosition, 0f, distance > link.StrainFrom);
				}
				else
				{
					Behaviour.Wander(__instance, holderPosition, dt, length + Links.Reach(self)); // große Tiere weiter weg, nicht in den Halter
				}
				return false;
			}
			// Lokis Trugbilder (an keiner Leine): nah beim Halter warten, ohne ihn anzurempeln (beide Körper plus 0,2 m)
			float bodies = holder.GetRadius() + self.GetRadius() - Links.Reach(self) + 0.2f;
			if (distance < Mathf.Min(length, Mathf.Max(length - 0.3f, bodies)))
			{
				__instance.StopMoving();
				return false;
			}
			__instance.MoveTo(dt, holderPosition, 0f, distance > runFrom);
			return false;
		}
		Behaviour.Follows(__instance);
		// der Halter läuft: nicht auf ihn zu (MoveTo weicht Spielern nicht aus), sondern hinter ihn oder, gehorsam, neben ihn
		Vector3 forward = velocity.normalized;
		Vector3 target;
		if (Bonds.TryGet(self, out BondStats bond) && bond.Obedience > 0f)
		{
			Vector3 side = Vector3.Cross(Vector3.up, forward);
			float sign = Vector3.Dot(position - holderPosition, side) >= 0f ? 1f : -1f;
			target = holderPosition + velocity * (0.5f * bond.Obedience / 100f) + side * (sign * 1.5f); // läuft in deine Richtung neben dir mit
		}
		else
		{
			target = holderPosition - forward * (Mathf.Max(1.2f, length) + Links.Reach(self)); // im Mindestabstand hinter dir (große Tiere weiter)
		}
		__instance.MoveTo(dt, target, 0f, distance > runFrom); // zurückgefallen: rennen, um den Mindestabstand zu halten
		return false;
	}
}

/// <summary>Futter: Unterwegs zu einem Befehlsziel (Linksklick) sucht das Tier keins; mit Gehorsam seltener, solange der Halter läuft.</summary>
[HarmonyPatch(typeof(MonsterAI), "UpdateConsumeItem")]
internal static class LeashFoodPatch
{
	private static bool Prefix(MonsterAI __instance, ref bool __result)
	{
		Character self = __instance.m_character;
		if (self != null && Links.Active.Count > 0 && Links.ForAnimal(self) is Link link && link.GoTo != null && !link.GoToReached)
		{
			__result = false;
			return false; // Befehl „hingehen": unterwegs nicht zum Futter abbiegen
		}
		if (Bonds.Animals.Count == 0 || self == null || !Bonds.TryGet(self, out BondStats bond) || bond.Obedience <= 0f)
		{
			return true;
		}
		Player? holder = Player.m_localPlayer;
		if (holder == null || !Behaviour.Moving(holder) || !Behaviour.IgnoresFood(self, bond.Obedience))
		{
			return true;
		}
		__result = false;
		return false;
	}
}

/// <summary>
/// Befehle (Folgen/Bleiben über Streicheln): Ein entwischtes Tier hört erst wieder, wenn es angeleint wurde (Gorduan: mit
/// einer weiteren oder reparierten Leine einfangen). Lokis Trugbilder nehmen gar keine Befehle an.
/// </summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.Command))]
internal static class CommandPatch
{
	private static bool Prefix(Tameable __instance, Humanoid user)
	{
		if (__instance.m_nview == null || !__instance.m_nview.IsValid())
		{
			return true;
		}
		ZDO zdo = __instance.m_nview.GetZDO();
		bool illusion = Illusions.IsIllusion(zdo);
		bool escaped = !illusion && Plugin.On && zdo.GetBool(Plugin.EscapedHash);
		if (!illusion && !escaped)
		{
			return true;
		}
		if (user != null && user == Player.m_localPlayer)
		{
			user.Message(MessageHud.MessageType.Center, GcLoc.Get(illusion ? "gcls_illusion_touch" : "gcls_escaped", __instance.GetHoverName()));
		}
		return false;
	}
}
