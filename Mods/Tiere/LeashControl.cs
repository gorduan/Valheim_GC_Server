using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Leine in der Hand (Gorduan, 13.10.2026): Man nimmt eine Leine mit ihrer Taste 1–8 in die Hand wie eine Waffe, sie bleibt
/// ausgewählt. Ein Einhandgegenstand ohne Schaden, Block und Angriff; sichtbar als Seilknäuel in der rechten Hand, aus
/// dem auch das Seil zum Tier kommt.
///  * E am gezähmten Tier: anleinen; am eigenen angeleinten Tier: lösen; am angebundenen Tier: vom Pfosten nehmen.
///    E am Pfosten bindet das Tier dieser Leine an (InteractPatch, Posts.Interact).
///  * Linksklick: Das Tier dieser Leine geht an den angezielten Ort und wartet dort, bis ein neuer Befehl kommt oder du
///    dich so weit entfernst, dass die Leine es wieder führt. Weiter als die Leine frei lässt (90 % der Höchstlänge)
///    geht es nicht. Unterwegs lässt es sich nicht ablenken.
///  * Rechtsklick: Das Tier greift das anvisierte Ziel an (nur Gegner des Tiers, höchstens ChaseDistance von ihm weg),
///    bis es tot oder entkommen ist.
///  * Gorduan (Nachtrag): Befehle nimmt ein Tier erst ab Stufe 10 an (gezüchtete Tiere, Tierpflege). Danach je Tier 60 s
///    Pause, kürzer um den Durchschnitt aus Tier-Stufe und Tierpflege des Spielers in Prozent (ab 100 %: keine Pause).
/// Um ein anderes Tier zu steuern, nimmt man dessen Leine in die Hand. Ohne Leine in der Hand streichelt E wie immer.
/// </summary>
internal static class Control
{
	/// <summary>So lange versucht das Tier, den Zielort zu erreichen.</summary>
	private const float GoToTimeout = 20f;
	/// <summary>Angriffsbefehl endet, wenn das Tier so lange nicht zuschlagen konnte (Valheim selbst gibt nach 60 s auf).</summary>
	private const float AttackTimeout = 30f;
	private const float HintPause = 120f;

	private static int aimMask;
	private static float nextHint;
	private static float nextNoAnimal;

	internal static void Reset()
	{
		nextHint = 0f;
		nextNoAnimal = 0f;
	}

	/// <summary>Die Leine in der Hand des Spielers (rechte Hand), sonst null.</summary>
	internal static ItemDrop.ItemData? Held(Player? player)
	{
		if (!Plugin.On || player == null)
		{
			return null;
		}
		ItemDrop.ItemData? item = player.GetRightItem();
		return item != null && Plugin.IsLeash(item, out _) ? item : null;
	}

	// ------------------------------------------------------------------ Item

	/// <summary>
	/// Leine als Einhandgegenstand: kein Schaden, kein Block, keine Parade, kein Rückstoß, keine Ausdauer (der Tooltip zeigt
	/// davon nichts). Fähigkeit Tierpflege. In der Hand ein Seilknäuel in der Farbe der Leine.
	/// </summary>
	internal static void MakeHandItem(GameObject prefab, ItemDrop.ItemData.SharedData shared, Tier tier)
	{
		MakeHandShared(shared);
		AddCoil(prefab, tier);
	}

	/// <summary>Einhandgegenstand ohne Schaden, Block, Parade, Rückstoß und Ausdauer, Fähigkeit Tierpflege (Leine, Tierpfeife).</summary>
	internal static void MakeHandShared(ItemDrop.ItemData.SharedData shared)
	{
		shared.m_itemType = ItemDrop.ItemData.ItemType.OneHandedWeapon;
		shared.m_animationState = ItemDrop.ItemData.AnimationState.OneHanded;
		shared.m_skillType = Care.Skill != Skills.SkillType.None ? Care.Skill : Skills.SkillType.Unarmed;
		shared.m_damages = new HitData.DamageTypes();
		shared.m_damagesPerLevel = new HitData.DamageTypes();
		shared.m_blockPower = 0f;
		shared.m_blockPowerPerLevel = 0f;
		shared.m_deflectionForce = 0f;
		shared.m_deflectionForcePerLevel = 0f;
		shared.m_timedBlockBonus = 1f;
		shared.m_perfectBlockAdrenaline = 0f;
		shared.m_attackForce = 0f;
		shared.m_backstabBonus = 1f;
		shared.m_equipDuration = 0.3f;
		// In der Hand prüft Valheim laufend die Haltbarkeit (Humanoid.DrainEquipedItemDurability): Eine gerissene Leine
		// legt es ab, gelöscht wird sie nicht (sie lässt sich an der Werkbank reparieren). Kein Verschleiß durchs Halten
		shared.m_destroyBroken = false;
		shared.m_durabilityDrain = 0f;
		shared.m_useDurabilityDrain = 0f;
		if (shared.m_attack != null)
		{
			shared.m_attack.m_attackStamina = 0f;
		}
		if (shared.m_secondaryAttack != null)
		{
			shared.m_secondaryAttack.m_attackStamina = 0f;
		}
	}

	/// <summary>Größe der Seilrolle in der Hand (am Boden ganz).</summary>
	private const float HandScale = 0.55f;

	/// <summary>
	/// „attach" (so heißt bei Valheim das Modell in der Hand): die Seilrolle aus Blender (RopeModel), oben an der Windung
	/// gehalten, kleiner als am Boden. Im Prefab aus, Valheim schaltet die Kopie in der Hand ein. Ohne Modell wie bisher ein
	/// Seilknäuel aus zwei gekreuzten Windungen (Linien), das aus jedem Winkel gleich aussieht.
	/// </summary>
	private static void AddCoil(GameObject prefab, Tier tier)
	{
		if (prefab.transform.Find("attach") != null)
		{
			return;
		}
		GameObject attach = new("attach");
		attach.SetActive(false);
		attach.transform.SetParent(prefab.transform, false);
		Mesh? coilMesh = Gc.IsHeadless ? null : RopeModel.Coil;
		Material? rope = coilMesh != null ? RopeLook.Rope(tier) : null;
		Material? metal = coilMesh != null ? RopeLook.Metal() : null;
		if (coilMesh != null && rope != null && metal != null)
		{
			GameObject model = new("coil");
			model.transform.SetParent(attach.transform, false);
			model.transform.localScale = Vector3.one * HandScale;
			model.transform.localPosition = -RopeModel.CoilGrip * HandScale;
			model.AddComponent<MeshFilter>().sharedMesh = coilMesh;
			model.AddComponent<MeshRenderer>().sharedMaterials = new[] { rope, metal };
			return;
		}
		Material? material = Gc.IsHeadless ? null : Rope.MaterialFor(tier);
		if (material == null)
		{
			return; // Server ohne Grafik (oder kein Shader): nur die leere Halterung, gezeichnet wird bei den Spielern
		}
		GameObject go = new("coil");
		go.transform.SetParent(attach.transform, false);
		LineRenderer line = go.AddComponent<LineRenderer>();
		line.useWorldSpace = false;
		line.sharedMaterial = material;
		line.widthMultiplier = 0.022f;
		line.numCapVertices = 2;
		line.numCornerVertices = 2;
		line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
		line.receiveShadows = false;
		const int perTurn = 40;
		const float radius = 0.055f;
		Vector3[] points = new Vector3[perTurn * 2];
		for (int i = 0; i < perTurn; i++)
		{
			// zwei Spiralen über eine Kugel, die zweite um 90° gekippt: ein Knäuel
			float t = i / (float)(perTurn - 1);
			float polar = Mathf.Lerp(0.25f, Mathf.PI - 0.25f, t);
			float azimuth = t * Mathf.PI * 2f * 3.5f;
			Vector3 p = new(Mathf.Sin(polar) * Mathf.Cos(azimuth), Mathf.Cos(polar), Mathf.Sin(polar) * Mathf.Sin(azimuth));
			points[i] = p * radius;
			points[perTurn + i] = new Vector3(p.y, -p.x, p.z) * (radius * 0.92f);
		}
		line.positionCount = points.Length;
		line.SetPositions(points);
		attach.AddComponent<LeashHandCoil>();
	}

	// ------------------------------------------------------------------ Streicheln

	/// <summary>
	/// Streicheln ohne Folgen/Bleiben (Tier an einer Leine oder am Pfosten): Herzen und Text wie in Valheim, dieselbe
	/// Sekunde Pause. Erfahrung in Tierpflege gibt CarePetPatch.
	/// </summary>
	internal static bool Pet(Tameable tame, Player player)
	{
		if (Time.time - tame.m_lastPetTime <= 1f)
		{
			return false;
		}
		tame.m_lastPetTime = Time.time;
		tame.m_petEffect.Create(tame.transform.position, tame.transform.rotation);
		Game.instance?.IncrementPlayerStat(PlayerStatType.TamedPetting);
		string text = tame.m_tameTextGetter != null ? tame.m_tameTextGetter() ?? "" : "";
		if (text.Length == 0)
		{
			text = tame.m_nameBeforeText ? tame.GetHoverName() + " " + tame.m_tameText : tame.m_tameText;
		}
		player.Message(MessageHud.MessageType.Center, text);
		return true;
	}

	// ------------------------------------------------------------------ Befehle

	/// <summary>Im Tier: Weltzeit (Ticks), ab der es wieder einen Befehl annimmt.</summary>
	internal static readonly int ReadyHash = "gc_leash_cmd_ready".GetStableHashCode();

	/// <summary>Die Leine in der Hand hält ein Tier dieses Spielers (sonst Hinweis und null).</summary>
	private static Link? CommandLink(Player player, ItemDrop.ItemData held)
	{
		Link? link = Links.ForItem(held);
		if (link == null || link.Holder != player || link.Animal == null || link.Tame == null)
		{
			Gc.TopLeft(GcLoc.Get("gcls_cmd_idle"));
			return null;
		}
		return link.Traveling ? null : link;
	}

	/// <summary>
	/// Gorduan: Befehle nimmt ein Tier erst ab Stufe 10 an (Zucht-Stufe aus der Tierpflege; nur wenn sie an ist). Danach gilt
	/// je Tier eine Pause (Cooldown).
	/// </summary>
	internal static bool Ready(Link link, string name, out string refusal)
	{
		refusal = "";
		if (Care.Enabled.Value && Care.AnimalLevelExact(link.Tame) < Plugin.CommandMinLevel.Value)
		{
			refusal = GcLoc.Get("gcls_cmd_level", name, Plugin.CommandMinLevel.Value);
			return false;
		}
		float wait = Remaining(link);
		if (wait > 0f)
		{
			refusal = GcLoc.Get("gcls_cmd_wait", name, Mathf.CeilToInt(wait));
			return false;
		}
		return true;
	}

	/// <summary>
	/// Pause nach einem Befehl (Gorduan): 60 s, kürzer um den Durchschnitt aus Tier-Stufe und Tierpflege-Stufe des Spielers in
	/// Prozent; die Tierpflege zählt seit 19.10.2026 halb (Gorduan). Stufe 20 und 20 ergeben 15 % kürzer; über 100 % zählt wie
	/// 100 %, also keine Pause.
	/// </summary>
	internal static float Cooldown(Player player, Link link)
	{
		float skill = Care.Enabled.Value && Care.Skill != Skills.SkillType.None ? player.GetSkillLevel(Care.Skill) : 0f;
		float percent = (Care.AnimalLevelExact(link.Tame) + Care.SkillShare * skill) * 0.5f;
		return Plugin.CommandCooldown.Value * Mathf.Clamp01(1f - percent / 100f);
	}

	/// <summary>Sekunden, bis das Tier wieder einen Befehl annimmt (0 = jetzt). Steht im Tier, gilt also auch nach neuem Anleinen.</summary>
	internal static float Remaining(Link link)
	{
		if (ZNet.instance == null || link.View == null || !link.View.IsValid())
		{
			return 0f;
		}
		long ready = link.View.GetZDO().GetLong(ReadyHash, 0L);
		if (ready <= 0L)
		{
			return 0f;
		}
		double left = (new System.DateTime(ready) - ZNet.instance.GetTime()).TotalSeconds;
		return left > 0.0 && left <= Plugin.CommandCooldown.Value + 1f ? (float)left : 0f; // mehr als eine ganze Pause: Weltzeit gesprungen
	}

	/// <summary>Befehl angenommen: Pause beginnt (mit der Tierpfeife kürzer, pauseFactor &lt; 1).</summary>
	private static void Obeyed(Player player, Link link, float pauseFactor)
	{
		if (ZNet.instance == null || link.View == null || !link.View.IsValid())
		{
			return;
		}
		float cooldown = Cooldown(player, link) * Mathf.Clamp01(pauseFactor);
		link.View.GetZDO().Set(ReadyHash, cooldown > 0f ? ZNet.instance.GetTime().AddSeconds(cooldown).Ticks : 0L);
	}

	/// <summary>
	/// Echo aus Ginnungagap (Gorduan, 27.10.2026): Die Befehlspause dieses Tiers endet sofort. Wie beim Folgen schreibt der Halter
	/// ins Tier; angeleinte Tiere besitzt er ohnehin (Links), sonst übernimmt er sie wie dort.
	/// </summary>
	internal static void EndPause(Link link)
	{
		if (link.View == null || !link.View.IsValid() || link.View.GetZDO().GetLong(ReadyHash, 0L) == 0L)
		{
			return;
		}
		if (!link.View.IsOwner())
		{
			link.View.ClaimOwnership();
		}
		link.View.GetZDO().Set(ReadyHash, 0L);
	}

	/// <summary>Hinweis am Tier, wenn seine Leine in der Hand ist: Befehle ab welcher Stufe, bereit oder in wie vielen Sekunden.</summary>
	internal static string Status(Player player, Link link)
	{
		if (Care.Enabled.Value && Care.AnimalLevelExact(link.Tame) < Plugin.CommandMinLevel.Value)
		{
			return GcLoc.Get("gcls_hover_cmd_level", Plugin.CommandMinLevel.Value);
		}
		float wait = Remaining(link);
		return wait > 0f ? GcLoc.Get("gcls_hover_cmd_wait", Mathf.CeilToInt(wait)) : GcLoc.Get("gcls_hover_cmd_ready", Mathf.CeilToInt(Cooldown(player, link)));
	}

	/// <summary>Linksklick: Das Tier geht an den angezielten Ort (höchstens so weit, wie die Leine frei lässt).</summary>
	internal static void Send(Player player, ItemDrop.ItemData held)
	{
		Link? link = CommandLink(player, held);
		if (link == null)
		{
			return;
		}
		string name = link.Tame.GetHoverName();
		if (!Ready(link, name, out string refusal))
		{
			Gc.TopLeft(refusal);
			return;
		}
		if (!Aim(out Vector3 point))
		{
			Gc.TopLeft(GcLoc.Get("gcls_cmd_no_point", name));
			return;
		}
		TrySend(player, link, point, 1f, out string message);
		Gc.TopLeft(message);
	}

	/// <summary>
	/// „Hingehen“ für ein Tier (Leine oder Tierpfeife): an den Punkt, höchstens so weit, wie die Leine frei lässt. Liefert
	/// die Meldung dazu; false, wenn das Tier nicht gehorcht (Stufe, Pause, kein Weg).
	/// </summary>
	internal static bool TrySend(Player player, Link link, Vector3 point, float pauseFactor, out string message)
	{
		string name = link.Tame.GetHoverName();
		if (!Ready(link, name, out message))
		{
			return false;
		}
		Vector3 holder = player.transform.position;
		float free = FreeReach(link);
		Vector3 flat = new(point.x - holder.x, 0f, point.z - holder.z);
		bool shortened = flat.magnitude > free;
		if (shortened)
		{
			point = holder + flat.normalized * free;
			point.y = Ground(point, holder.y);
		}
		if (link.Ai != null && !link.Ai.HavePath(point))
		{
			message = GcLoc.Get("gcls_cmd_no_path", name); // Dach, Kante, Wasser: kein Weg dorthin (keine Pause)
			return false;
		}
		link.ClearCommands();
		link.PaidOut = link.Shortened ? 1f : 0f; // gekürzte Leine gibt nach, zieht sich danach wieder ein (Links.TickLink)
		link.GoTo = point;
		link.GoToSince = Time.time;
		Obeyed(player, link, pauseFactor);
		message = shortened ? GcLoc.Get("gcls_cmd_go_short", name, free.ToString("0.#")) : GcLoc.Get("gcls_cmd_go", name);
		return true;
	}

	/// <summary>Rechtsklick: Das Tier greift das anvisierte Ziel an.</summary>
	internal static void Sic(Player player, ItemDrop.ItemData held)
	{
		Link? link = CommandLink(player, held);
		if (link == null)
		{
			return;
		}
		TrySic(player, link, player.GetHoverCreature(), 1f, out string message);
		Gc.TopLeft(message);
	}

	/// <summary>„Angreifen“ für ein Tier (Leine oder Tierpfeife). Liefert die Meldung; false, wenn es nicht gehorcht.</summary>
	internal static bool TrySic(Player player, Link link, Character? target, float pauseFactor, out string message)
	{
		string name = link.Tame.GetHoverName();
		if (!Young.CanAttack(link))
		{
			message = GcLoc.Get("gcls_cmd_cant_attack", name); // Jungtiere und Tiere ohne Angriff (keine Pause)
			return false;
		}
		if (!Ready(link, name, out message))
		{
			return false;
		}
		if (target == null || target.IsDead() || target == link.Animal)
		{
			message = GcLoc.Get("gcls_cmd_no_target", name);
			return false;
		}
		if (target.IsPlayer() || target.IsTamed() || !SpeciesLink.EnemyForOrder(link.Animal, target)
			|| target.GetFaction() == Character.Faction.TrainingDummy || target.m_aiSkipTarget)
		{
			message = GcLoc.Get("gcls_cmd_not_enemy", name, target.GetHoverName());
			return false; // keine Spieler, keine zahmen Tiere, nichts, was das Tier nicht als Gegner sieht, keine Trainingspuppe
		}
		float distance = Vector3.Distance(link.Animal.transform.position, target.transform.position);
		if (distance > Plugin.ChaseDistance.Value)
		{
			message = GcLoc.Get("gcls_cmd_too_far", name, Plugin.ChaseDistance.Value.ToString("0"));
			return false;
		}
		if (Vector3.Distance(link.Holder.transform.position, target.transform.position) > FightReach(link))
		{
			message = GcLoc.Get("gcls_cmd_out_of_reach", name, FightReach(link).ToString("0"));
			return false;
		}
		link.ClearCommands();
		link.PaidOut = link.Shortened ? 1f : 0f; // gekürzte Leine gibt nach, zieht sich nach dem Kampf wieder ein
		link.AttackTarget = target;
		link.AttackSince = Time.time;
		Obeyed(player, link, pauseFactor);
		message = GcLoc.Get("gcls_cmd_attack", name, target.GetHoverName());
		return true;
	}

	/// <summary>Jedes Bild beim Halter: Befehle beenden, wenn sie erledigt oder nicht mehr möglich sind.</summary>
	internal static void Check(Link link)
	{
		if (link.GoTo is Vector3 point)
		{
			Vector3 holder = link.Holder.transform.position;
			if (Utils.DistanceXZ(holder, point) > link.FreeUntil + 0.5f || (!link.GoToReached && Time.time - link.GoToSince > GoToTimeout))
			{
				link.GoTo = null; // du gehst weiter weg (die Leine führt es wieder) oder es kommt nicht hin
				link.GoToReached = false;
			}
		}
		Character? target = link.AttackTarget;
		if (link.AttackTarget is not null && (target == null || target.IsDead() || !target.gameObject.activeInHierarchy
			|| Vector3.Distance(link.Animal.transform.position, target.transform.position) > Plugin.ChaseDistance.Value
			|| Vector3.Distance(link.Holder.transform.position, target.transform.position) > FightReach(link)
			|| (Time.time - link.AttackSince > AttackTimeout && link.Animal.GetTimeSinceLastAttack() > AttackTimeout)))
		{
			link.AttackTarget = null; // besiegt, entkommen oder unerreichbar: wieder folgen
		}
	}

	/// <summary>Angezielter Punkt (Boden, Bauteile) unter dem Fadenkreuz, höchstens 60 m weit.</summary>
	internal static bool Aim(out Vector3 point)
	{
		point = Vector3.zero;
		GameCamera? camera = GameCamera.instance;
		if (camera == null)
		{
			return false;
		}
		if (aimMask == 0)
		{
			aimMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
		}
		Transform view = camera.transform;
		if (!Physics.Raycast(view.position, view.forward, out RaycastHit hit, 60f, aimMask, QueryTriggerInteraction.Ignore))
		{
			return false;
		}
		point = hit.point;
		return true;
	}

	/// <summary>Bodenhöhe an einer Stelle (Strahl von oben), sonst die angegebene Höhe.</summary>
	/// <summary>
	/// Wie weit ein Befehl ein Tier schicken darf: der freie Bereich der Leine (90 % der Höchstlänge), mit einem halben Meter
	/// Rand. Gorduan: Bei Befehlen gibt eine gekürzte Leine bis zur normalen Länge nach, also zählt hier die ungekürzte.
	/// </summary>
	internal static float FreeReach(Link link) => Mathf.Max(1f, Plugin.FreeUntil(link.FullStrainFrom) - 0.5f);

	/// <summary>
	/// So weit vom Halter darf ein Gegner sein, gegen den ein angeleintes Tier kämpft: Zuggrenze der Leine plus 2 m (Bisslänge).
	/// Review 27.10.2026: Bis ChaseDistance (20 m vom Tier) zerrte das Tier an der Leine und verschliss sie im Kampf, obwohl
	/// „Kämpfen in Reichweite kostet nichts“ gilt.
	/// </summary>
	internal static float FightReach(Link link) => link.DragFrom + 2f;

	/// <summary>Punkt höchstens so weit (waagerecht) vom Halter, Höhe dann vom Boden.</summary>
	internal static Vector3 Within(Vector3 holder, Vector3 point, float reach)
	{
		Vector3 flat = new(point.x - holder.x, 0f, point.z - holder.z);
		if (flat.magnitude <= reach)
		{
			return point;
		}
		Vector3 inside = holder + flat.normalized * reach;
		inside.y = Ground(inside, holder.y);
		return inside;
	}

	internal static float Ground(Vector3 at, float fallback)
	{
		if (aimMask == 0)
		{
			aimMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
		}
		return Physics.Raycast(new Vector3(at.x, fallback + 20f, at.z), Vector3.down, out RaycastHit hit, 60f, aimMask, QueryTriggerInteraction.Ignore) ? hit.point.y : fallback;
	}

	// ------------------------------------------------------------------ Länge (Umschalt + Mausrad)

	/// <summary>In diesem Bild gehört das Mausrad der Leine: Die Kamera zoomt nicht (LeashZoomPatch).</summary>
	internal static bool WheelTaken;

	/// <summary>
	/// Gorduan: Mit der Leine in der Hand, an der ein Tier hängt, stellt linke Umschalttaste + Mausrad die Länge ein: kürzer bis
	/// 1 m, länger höchstens bis zur normalen Mindestlänge der Leine, je Raste 0,5 m. Die Höchstlänge und alle Bereiche
	/// wachsen und schrumpfen im selben Verhältnis (Link.StrainFrom). Die Leine merkt sich die Länge (auch am Pfosten).
	/// </summary>
	internal static void TickWheel()
	{
		WheelTaken = false;
		Player? player = Player.m_localPlayer;
		if (player == null || !Input.GetKey(KeyCode.LeftShift) || Gc.TextInputActive() || InventoryGui.IsVisible() || Menu.IsVisible() || Minimap.IsOpen() || Hud.InRadial())
		{
			return;
		}
		ItemDrop.ItemData? held = Held(player);
		if (held == null)
		{
			return;
		}
		WheelTaken = true; // Umschalt mit einer Leine in der Hand: das Rad verstellt nur die Leine, nicht den Zoom
		float scroll = ZInput.GetMouseScrollWheel();
		if (Mathf.Abs(scroll) < 0.0001f)
		{
			return;
		}
		Link? link = Links.ForItem(held);
		if (link == null || link.Holder != player || link.Item == null)
		{
			// Gorduan (16.10.2026, Loki-Leine): sonst geschah stumm nichts. Die Länge stellt man nur mit Tier an der Leine ein
			if (Time.time >= nextNoAnimal)
			{
				nextNoAnimal = Time.time + 2f;
				Gc.TopLeft(GcLoc.Get("gcls_len_no_animal"));
			}
			return;
		}
		int quality = link.Item.m_quality;
		float normal = Plugin.MinLength(link.Tier, quality);
		float now = Plugin.SetLength(link.Tier, link.Item, quality);
		float next = Mathf.Clamp(now + (scroll > 0f ? Plugin.LengthStep : -Plugin.LengthStep), Plugin.ShortestLength, normal);
		if (next >= normal - 0.001f)
		{
			link.Item.m_customData.Remove(Plugin.KeyLength); // wieder die normale Länge
		}
		else
		{
			link.Item.m_customData[Plugin.KeyLength] = next.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
		}
		// die eingestellte Länge: Hat die Leine nach einem Befehl nachgegeben, zieht sie sich bis dahin ein, statt
		// zurückzuschnappen (das kostete sonst Haltbarkeit)
		float ratio = Plugin.LengthRatio(link.Tier, link.Item, quality);
		Gc.TopLeft(GcLoc.Get("gcls_len_set", (normal * ratio).ToString("0.#"), (link.FullStrainFrom * ratio).ToString("0.#"),
			Plugin.WearFactor(link.Tier, link.Item, quality).ToString("0.##")));
	}

	/// <summary>Hinweis beim In-die-Hand-Nehmen (höchstens alle 2 Minuten).</summary>
	internal static void Hint(Player player, ItemDrop.ItemData item)
	{
		if (Time.time < nextHint)
		{
			return;
		}
		nextHint = Time.time + HintPause;
		Link? link = Links.ForItem(item);
		string name = link != null && link.Holder == player && link.Tame != null ? link.Tame.GetHoverName() : "";
		Gc.TopLeft(name.Length > 0 ? GcLoc.Get("gcls_hand_hint", name) : GcLoc.Get("gcls_hand_hint_idle"));
	}
}

/// <summary>Seilknäuel in der Hand: nachts abgedunkelt wie das Seil zum Tier (die Farbe leuchtet sonst im Dunkeln).</summary>
public class LeashHandCoil : MonoBehaviour
{
	private LineRenderer? line;
	private float next;

	private void Update()
	{
		if (Time.time < next)
		{
			return;
		}
		next = Time.time + 0.5f;
		if (line == null)
		{
			line = GetComponentInChildren<LineRenderer>();
			if (line == null)
			{
				enabled = false;
				return;
			}
		}
		float light = Mathf.Clamp(RenderSettings.ambientLight.maxColorComponent * 1.6f, 0.2f, 1f);
		line.startColor = line.endColor = new Color(light, light, light, 1f);
	}
}

/// <summary>
/// Maustasten mit der Leine in der Hand: Linksklick schickt das Tier hin, Rechtsklick lässt es angreifen. Die Klicks sind
/// Befehle, kein Angriff und kein Block des Spielers (auch Mitteltaste: kein Zweitangriff).
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
internal static class LeashControlPatch
{
	private static void Prefix(Player __instance, ref bool attack, ref bool attackHold, ref bool secondaryAttack, ref bool secondaryAttackHold, ref bool block, ref bool blockHold)
	{
		if (__instance != Player.m_localPlayer || __instance.GetDoodadController() is Sadle)
		{
			return; // beim Reiten gehören die Tasten Valheim: rechte Maustaste wendet das Tier (Sadle.ApplyControlls, Gorduan 18.10.2026)
		}
		ItemDrop.ItemData? held = Control.Held(__instance);
		ItemDrop.ItemData? whistle = held == null ? Whistles.Held(__instance) : null; // Tierpfeife: dieselben Tasten für alle Tiere
		if (held == null && whistle == null)
		{
			return;
		}
		if (!__instance.IsAttached() && !__instance.InEmote() && __instance.GetDoodadController() == null)
		{
			if (attack)
			{
				// gedrückt in diesem Schritt (Valheim meldet nur die Flanke)
				if (held != null)
				{
					Control.Send(__instance, held);
				}
				else
				{
					Whistles.Send(__instance, whistle!);
				}
			}
			if (block)
			{
				if (held != null)
				{
					Control.Sic(__instance, held);
				}
				else
				{
					Whistles.Sic(__instance, whistle!);
				}
			}
			attack = false;
			attackHold = false;
			secondaryAttack = false;
			secondaryAttackHold = false;
		}
		// Sitzt, tanzt oder steuert der Spieler, bleibt der Klick bei Valheim: aufstehen bzw. Steuer loslassen (Valheim
		// löscht den Angriff dabei selbst). Blocken mit der Leine gibt es nie
		block = false;
		blockHold = false;
		__instance.m_blocking = false; // auch bei „Blocken umschalten" nicht im Block bleiben
	}
}

/// <summary>
/// Leine oder Tierpfeife am Gegenstandsständer: das Modell zeigen. Valheim kopiert dort das Modell „attach" ohne es
/// einzuschalten, und das unserer Items ist im Prefab aus (sonst läge es am Boden daneben).
/// </summary>
[HarmonyPatch(typeof(ItemStand), "SetVisualItem")]
internal static class LeashItemStandPatch
{
	private static void Postfix(ItemStand __instance)
	{
		GameObject? visual = __instance.m_visualItem;
		ItemDrop.ItemData? shown = __instance.m_visualItemDrop != null ? __instance.m_visualItemDrop.m_itemData : null;
		if (visual != null && !visual.activeSelf && shown != null && (Plugin.IsLeash(shown, out _) || Whistles.IsWhistle(shown, out _)))
		{
			visual.SetActive(true);
		}
	}
}

/// <summary>
/// Kamera-Zoom: Hält man Umschalt mit einer Leine in der Hand, gehört das Mausrad der Leine. Valheim zoomt in UpdateCamera
/// mit m_distance -= Rad × m_zoomSens und setzt die Kamera im selben Aufruf (Valheim Plus im Ich-Modus genauso im
/// Postfix). Den Abstand danach zurückzusetzen ließ die Kamera bei jeder Raste ruckeln (Gorduan, 16.10.2026), deshalb ist
/// die Zoom-Empfindlichkeit während des Aufrufs 0. Der Finalizer stellt sie auch nach einem Fehler wieder her.
/// </summary>
[HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
internal static class LeashZoomPatch
{
	[HarmonyPriority(Priority.First)]
	private static void Prefix(GameCamera __instance, out float __state)
	{
		__state = float.NaN; // NaN: nicht angefasst (jeder echte Wert, auch ein negativer, wird zurückgestellt)
		if (Control.WheelTaken)
		{
			__state = __instance.m_zoomSens;
			__instance.m_zoomSens = 0f;
		}
	}

	private static void Finalizer(GameCamera __instance, float __state)
	{
		if (!float.IsNaN(__state) && __instance.m_zoomSens == 0f)
		{
			__instance.m_zoomSens = __state;
		}
	}
}

/// <summary>Leine in die Hand genommen: kurzer Hinweis, was die Tasten tun.</summary>
[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
internal static class LeashEquipHintPatch
{
	private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, bool __result)
	{
		if (__result && Plugin.On && __instance == Player.m_localPlayer && item != null && Plugin.IsLeash(item, out _) && Hud.instance != null)
		{
			Control.Hint((Player)__instance, item);
		}
	}
}
