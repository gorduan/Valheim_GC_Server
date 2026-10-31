using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GC.Server.Leash;
using GC.Shared;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Courier;

/// <summary>
/// Walküren-Transport (Gorduan, 19.10.2026): teurer Lufttransport für Fracht, die nicht durchs Portal darf.
///
/// Ablauf:
/// 1. Abholen (Event 1): Im Karren liegen 20 rohe Loxfleisch und 1 Ymir-Fleisch (Gorduan, 25.10.2026, statt der zerbrechenden
///    Pfeife), man pfeift mit der Walkürenpfeife (Schwarzmetall + Donnerstein) höchstens 1 m daneben. Der Karren darf nicht gezogen werden und nicht offen sein. Es wird 5 s dunkel und gewittert,
///    ein Blitz wie beim Vernichter schlägt in den Karren (Schaden wie beim Vernichter), er brennt blau und erlischt, das
///    Wetter kehrt zurück. Mit einem Schrei kommt die Walküre aus dem Intro, packt das Zugseil und fliegt mit dem Karren
///    davon. Läuft gerade eine Abholung, wartet die Walküre in einer Reihe (nie zwei zugleich, Gorduan); die Ruf-Effekte laufen
///    sofort. Danach beginnt dort ein Raid, der gerade möglich ist, auch ohne Gebäude in der Nähe (mit Easteregg: Hühner).
/// 2. Abliefern (Event 2): Der nächste Pfiff mit einer Walkürenpfeife, egal wo: ein kurzer Blitz an der Stelle (Vernichter-
///    Schaden; seit 25.10.2026 zerbricht die Pfeife nicht mehr, Gorduan), der Schrei, dann fällt der Karren genau dort aus großer
///    Höhe, zerschellt, und die Fracht liegt am Boden (ohne Loxfleisch und Ymir-Fleisch). Erst danach kann man wieder einen
///    Karren schicken.
///
/// Je Spieler ist ein Karren unterwegs (nur wer ihn geschickt hat, kann ihn rufen). Der Server führt Buch: Fracht als
/// beständige ZDOs (Store), Reihe der Abholungen, Raids. Ausgeräumt wird der Karren vom Besitzer seines ZDOs (nur er darf
/// das Inventar ändern), abgeworfen wird die Fracht vom abliefernden Spieler; die Effekte sieht jeder im Umkreis (Show).
/// </summary>
internal static class Transport
{
	internal const string LoxMeat = "LoxMeat";
	/// <summary>Ymir-Fleisch (Valheims Prefab YmirRemains), zweite Pflicht-Fracht seit 25.10.2026 (Gorduan).</summary>
	internal const string YmirFlesh = "YmirRemains";
	internal const string CartPrefab = "Cart";
	internal static readonly int CartHash = CartPrefab.GetStableHashCode();

	/// <summary>Gorduan: höchstens 1 m neben dem Karren (gemessen von der Körpermitte, plus halbe Körperbreite).</summary>
	internal const float CartReach = 1f;

	/// <summary>Gorduan: Effekte für alle Spieler in 150 m.</summary>
	internal const float ShowRange = 150f;

	// Server ← Spieler
	private const string RpcSend = "GC_Courier_Send";
	private const string RpcDeliver = "GC_Courier_Deliver";
	private const string RpcDelivered = "GC_Courier_Delivered";
	private const string RpcQuery = "GC_Courier_Query";
	private const string RpcStored = "GC_Courier_Stored";
	private const string RpcClaimFailed = "GC_Courier_ClaimFailed";
	private const string RpcDeliverFailed = "GC_Courier_DeliverFailed";
	// Server → Besitzer des Karrens (an dessen ZNetView)
	internal const string RpcClaim = "GC_Courier_Claim";
	// Server → Spieler
	private const string RpcState = "GC_Courier_State";
	private const string RpcMessage = "GC_Courier_Msg";
	private const string RpcCargo = "GC_Courier_Cargo";
	// Server → alle (die Effekte, jeder Spieler entscheidet selbst nach Abstand)
	private const string RpcCall = "GC_Courier_Call";
	private const string RpcPickup = "GC_Courier_Pickup";
	private const string RpcDrop = "GC_Courier_Drop";
	private const string RpcRaid = "GC_Courier_Raid";
	private const string RpcFinale = "GC_Courier_Finale";
	// Spieler, der gepfiffen hat → Server: die Wächter des Eastereggs sind erschienen (ZDOIDs)
	private const string RpcGuards = "GC_Courier_Guards";

	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<int> MeatAmount = null!;
	internal static ConfigEntry<int> YmirAmount = null!;
	internal static ConfigEntry<bool> RaidAfter = null!;
	internal static ConfigEntry<bool> EasterEgg = null!;

	internal static bool On => Enabled != null && Enabled.Value;

	internal static void Init(ConfigFile config)
	{
		const string section = "12 - Courier";
		Enabled = Gc.BindSynced(config, section, "Enabled", true, "Walküren-Transport: Walkürenpfeife (Schwarzmetall + Donnerstein, Schmiede) neben einem Karren mit Loxfleisch und Ymir-Fleisch: eine Walküre holt ihn ab (auch mit Fracht, die nicht durchs Portal darf). Der nächste Pfiff mit einer Walkürenpfeife ruft den Karren dorthin, wo man steht.");
		MeatAmount = Gc.BindSynced(config, section, "LoxMeat", 20, "So viel rohes Loxfleisch muss im Karren liegen. Die Walküre nimmt es sich, der Rest der Fracht kommt an.", new AcceptableValueRange<int>(0, 100));
		YmirAmount = Gc.BindSynced(config, section, "YmirFlesh", 1, "So viel Ymir-Fleisch muss zusätzlich im Karren liegen. Die Walküre nimmt es mit dem Loxfleisch (Gorduan, 25.10.2026: statt dass die Pfeife beim Abliefern zerbricht).", new AcceptableValueRange<int>(0, 20));
		RaidAfter = Gc.BindSynced(config, section, "RaidAfterPickup", true, "Nach dem Abholen beginnt am Karren ein Raid, der in der Welt gerade möglich ist (wie Valheims eigene Raids, aber auch ohne Gebäude in der Nähe). Mit Easteregg: Kampfhühner.");
		EasterEgg = Gc.BindSynced(config, section, "EasterEgg", false, "Easteregg: Das Abholen wird völlig übertrieben (Lärm, Blitze, Beben, Feuer am Himmel …, kein zusätzlicher Schaden), statt der Walküre kommt eine Möwe, und der Raid danach besteht aus Kampfhühnern. Das Abliefern bleibt gleich.");
		Enabled.SettingChanged += (_, _) => Whistles.ApplyRecipes();
		PrefabManager.OnVanillaPrefabsAvailable += Store.CreatePrefab;
		RaidHens.Init();
		AddLocalization();
	}

	/// <summary>Beschreibung der Walkürenpfeife, $1 Loxfleisch und $2 Ymir-Fleisch (Gorduan, 25.10.2026: aufs Minimum gekürzt).</summary>
	internal const string DescriptionKey = "gc_whistle_valkyrie_desc";

	/// <summary>Valheim-Texte ($-Schlüssel) für Pfeife, Kampfhuhn und Hühner-Raid aus den Übersetzungen der Mod.</summary>
	private static void AddLocalization()
	{
		CustomLocalization localization = LocalizationManager.Instance.GetLocalization();
		foreach (string key in new[] { "gc_whistle_valkyrie", DescriptionKey, "gc_raidhen", "gc_henraid_start", "gc_henraid_end" })
		{
			foreach (KeyValuePair<string, string> pair in GcLoc.AllLanguages(key))
			{
				// Mengen aus der Config von jetzt; im Tooltip hält CourierDescriptionPatch sie aktuell (Server-Werte)
				string text = key == DescriptionKey ? pair.Value.Replace("$1", MeatAmount.Value.ToString()).Replace("$2", YmirAmount.Value.ToString()) : pair.Value;
				localization.AddTranslation(pair.Key, key, text);
			}
		}
	}

	/// <summary>Beschreibung der Walkürenpfeife mit den Mengen, die gerade gelten.</summary>
	internal static string Description() => GcLoc.Get(DescriptionKey, MeatAmount.Value, YmirAmount.Value);

	private static readonly HashSet<string> Reported = new();

	/// <summary>Jedes Bild (Server und Spieler), jeder Teil für sich abgesichert: ein Fehler steht einmal im Log.</summary>
	internal static void Tick()
	{
		Guard("Server", ServerTick);
		if (!Gc.IsHeadless)
		{
			Guard("Spieler", ClientTick);
			Guard("Effekte", Show.Tick);
		}
	}

	private static void Guard(string part, Action action)
	{
		try
		{
			action();
		}
		catch (Exception e)
		{
			if (Reported.Add(part))
			{
				ServerPlugin.Log.LogWarning($"Walküren-Transport ({part}): {e}");
			}
		}
	}

	// =================================================================== Spieler (Client)

	private static CargoState state;
	private static int place;
	private static bool known;
	private static float waitUntil;
	private static float nextQuery;
	private static int queries;
	private static string? meatName;
	private static string? ymirName;

	internal static CargoState MyState => state;

	/// <summary>Valheims Name des Loxfleischs („$item_loxmeat“), zum Zählen im Karren.</summary>
	internal static string MeatName()
	{
		if (meatName == null && ObjectDB.instance != null)
		{
			meatName = ObjectDB.instance.GetItemPrefab(LoxMeat)?.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_name;
		}
		return meatName ?? "$item_loxmeat";
	}

	/// <summary>Valheims Name des Ymir-Fleischs („$item_ymirremains“), zum Zählen im Karren.</summary>
	internal static string YmirName()
	{
		if (ymirName == null && ObjectDB.instance != null)
		{
			ymirName = ObjectDB.instance.GetItemPrefab(YmirFlesh)?.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_name;
		}
		return ymirName ?? "$item_ymirremains";
	}

	/// <summary>Liegt genug Loxfleisch und Ymir-Fleisch im Karren?</summary>
	internal static bool HasCargoFee(Inventory inventory, int meat, int ymir) => inventory.CountItems(MeatName()) >= meat && inventory.CountItems(YmirName()) >= ymir;

	/// <summary>
	/// Pfiff mit der Walkürenpfeife (links oder rechts, Gorduan). true: die Walküre war gemeint (Tiere bekommen keinen Befehl).
	/// In der Luft: Abliefern hier. Wartend: Hinweis, die Tiere gehorchen wie sonst. Sonst: beladener Karren in 1 m?
	/// </summary>
	internal static bool OnWhistle(Player player, ItemDrop.ItemData item)
	{
		if (!On || ZRoutedRpc.instance == null || ZNet.instance == null)
		{
			return false;
		}
		if (Time.time < waitUntil)
		{
			return true; // Antwort des Servers steht noch aus
		}
		if (!known)
		{
			Query();
		}
		if (state == CargoState.Carried)
		{
			if (player.InInterior())
			{
				Gc.Center(GcLoc.Get("gccr_interior"));
				return false;
			}
			waitUntil = Time.time + 6f;
			ZRoutedRpc.instance.InvokeRoutedRPC(RpcDeliver, player.GetPlayerID(), player.transform.position);
			return true;
		}
		if (state == CargoState.Locked)
		{
			Gc.Center(GcLoc.Get("gccr_locked")); // Gorduan: Event 2 erst, wenn der Raid vorbei ist
			return false;
		}
		if (state == CargoState.Waiting)
		{
			Gc.Center(place > 0 ? GcLoc.Get("gccr_waiting_place", place) : place < 0 ? GcLoc.Get("gccr_waiting_guards") : GcLoc.Get("gccr_waiting"));
			return false;
		}
		Vagon? cart = NearCart(player);
		if (cart == null)
		{
			return false;
		}
		int meat = MeatAmount.Value;
		int ymir = YmirAmount.Value;
		Container? container = cart.m_container;
		if (container == null || !HasCargoFee(container.GetInventory(), meat, ymir))
		{
			Gc.Center(GcLoc.Get("gccr_need_cargo", meat, ymir));
			return false;
		}
		if (cart.InUse())
		{
			Gc.Center(GcLoc.Get("gccr_cart_busy"));
			return true;
		}
		if (SeatTaken(cart))
		{
			Gc.Center(GcLoc.Get("gccr_cart_seat"));
			return true;
		}
		// fremde Schutzzone: wie Valheims Kisten (Prüfung 19.10.: sonst ließe sich ein Karren aus fremder Basis verschicken)
		if (container.m_checkGuardStone && !PrivateArea.CheckAccess(cart.transform.position))
		{
			return true;
		}
		waitUntil = Time.time + 8f;
		ZRoutedRpc.instance.InvokeRoutedRPC(RpcSend, player.GetPlayerID(), cart.m_nview.GetZDO().m_uid);
		return true;
	}

	/// <summary>
	/// Sitzt jemand auf dem Karren? (Review 27.10.2026: Valheims Vagon.InUse prüft nur Ziehen und offene Kiste, nicht den Sitz.)
	/// Wie Valheims Chair.IsInUse: ein Spieler genau am Sitzpunkt. Auch fremde Sitzende stehen dort genau, ihre Lage kommt
	/// relativ zum Karren über das Netz (Player.GetRelativePosition).
	/// </summary>
	internal static bool SeatTaken(Vagon vagon)
	{
		Chair? chair = vagon.m_chair != null ? vagon.m_chair : vagon.GetComponentInChildren<Chair>();
		return chair != null && chair.m_attachPoint != null && chair.IsInUse();
	}

	/// <summary>Nächster Karren (Valheims „Cart“) höchstens 1 m neben dem Spieler: Abstand der Körpermitte zum Kasten.</summary>
	private static Vagon? NearCart(Player player)
	{
		Vector3 me = player.transform.position + Vector3.up * 0.9f;
		float reach = CartReach + 0.5f; // halbe Körperbreite
		Vagon? best = null;
		float bestDistance = float.MaxValue;
		foreach (Vagon vagon in Vagon.m_instances)
		{
			if (vagon == null || vagon.m_nview == null || !vagon.m_nview.IsValid() || vagon.m_nview.GetZDO().GetPrefab() != CartHash)
			{
				continue;
			}
			if (Vector3.Distance(me, vagon.transform.position) > 6f)
			{
				continue;
			}
			float distance = float.MaxValue;
			foreach (BoxCollider box in vagon.GetComponentsInChildren<BoxCollider>())
			{
				if (box.isTrigger || !box.enabled)
				{
					continue;
				}
				distance = Mathf.Min(distance, Vector3.Distance(me, box.ClosestPoint(me)));
			}
			if (distance == float.MaxValue)
			{
				distance = Vector3.Distance(me, vagon.transform.position) - 1.5f;
			}
			if (distance <= reach && distance < bestDistance)
			{
				best = vagon;
				bestDistance = distance;
			}
		}
		return best;
	}

	/// <summary>Stand beim Server erfragen (beim Spawnen, bis zu drei Mal, falls das Spieler-ZDO dort noch fehlt).</summary>
	internal static void Query()
	{
		Player? player = Player.m_localPlayer;
		if (!On || player == null || ZRoutedRpc.instance == null || Time.time < nextQuery)
		{
			return;
		}
		nextQuery = Time.time + 4f;
		ZRoutedRpc.instance.InvokeRoutedRPC(RpcQuery, player.GetPlayerID());
	}

	/// <summary>Jedes Bild beim Spieler: bis der Server den Stand genannt hat, noch einmal fragen.</summary>
	internal static void ClientTick()
	{
		if (!known && queries < 4 && Player.m_localPlayer != null && Time.time >= nextQuery)
		{
			queries++;
			Query();
		}
	}

	/// <summary>Zeile im Tooltip der Walkürenpfeife: wo der eigene Karren gerade ist (leer, wenn keiner unterwegs ist).</summary>
	internal static string TooltipStatus()
	{
		if (!On)
		{
			return GcLoc.Get("gccr_tip_off");
		}
		return state switch
		{
			CargoState.Carried => GcLoc.Get("gccr_tip_carried"),
			CargoState.Locked => GcLoc.Get("gccr_tip_locked"),
			CargoState.Waiting => GcLoc.Get("gccr_tip_waiting"),
			_ => "", // wie man einen Karren schickt, sagt die Beschreibung
		};
	}

	private static void RPC_State(long sender, int newState, int newPlace)
	{
		if (sender != ServerUid())
		{
			return;
		}
		state = (CargoState)newState;
		place = newPlace;
		known = true;
		waitUntil = 0f;
	}

	private static void RPC_Message(long sender, string key, int value)
	{
		if (sender != ServerUid() || Player.m_localPlayer == null)
		{
			return;
		}
		waitUntil = 0f;
		Gc.Center(GcLoc.Get(key, value, YmirAmount.Value)); // $2 = Ymir-Fleisch (gleicher Wert wie beim Server, die Einstellung ist abgeglichen)
	}

	/// <summary>Die eigene Fracht kommt an (nur beim abliefernden Spieler): sie wird beim Aufprall abgeworfen (Show).</summary>
	private static void RPC_Cargo(long sender, long playerId, ZDOID record, ZPackage cargo)
	{
		if (sender != ServerUid() || Player.m_localPlayer == null)
		{
			return;
		}
		Show.SetCargo(playerId, record, cargo);
	}

	/// <summary>
	/// Fracht liegt am Boden: dem Server melden, im selben Bild, in dem die Gegenstände entstehen (Review 27.10.2026, Show.Drop).
	/// Mit dem Datensatz: so zählt auch eine späte Meldung (Prüfung 19.10.: sonst ließe sich dieselbe Fracht ein zweites Mal rufen).
	/// </summary>
	internal static void ConfirmDelivered(long playerId, ZDOID record)
	{
		ZRoutedRpc.instance?.InvokeRoutedRPC(RpcDelivered, playerId, record);
	}

	/// <summary>Abwerfen gescheitert: Die Fracht bleibt in der Luft, man kann sie noch einmal rufen.</summary>
	internal static void DeliveryFailed(long playerId)
	{
		ZRoutedRpc.instance?.InvokeRoutedRPC(RpcDeliverFailed, playerId);
	}

	// =================================================================== Besitzer des Karrens

	/// <summary>
	/// Beim Besitzer des Karren-ZDOs (nur er darf das Inventar ändern): prüfen, Loxfleisch und Ymir-Fleisch nehmen, Inventar
	/// einpacken, Karren leeren und entfernen, die Fracht an den Server. Nur auf Anfrage des Servers. Die Menge an
	/// Ymir-Fleisch kommt aus der abgeglichenen Einstellung (gleich wie beim Server).
	/// </summary>
	internal static void Claim(Vagon vagon, long sender, long playerId, long whistler, int meat)
	{
		long server = ServerUid();
		if (sender != server || ZRoutedRpc.instance == null)
		{
			return;
		}
		ZNetView nview = vagon.m_nview;
		Container? container = vagon.m_container;
		ZDOID cart = nview != null && nview.IsValid() ? nview.GetZDO().m_uid : ZDOID.None;
		if (nview == null || !nview.IsValid() || !nview.IsOwner() || container == null)
		{
			ZRoutedRpc.instance.InvokeRoutedRPC(server, RpcClaimFailed, playerId, whistler, cart, "gccr_no_cart");
			return;
		}
		container.Load(); // eben erst Besitzer geworden: den neuesten Stand lesen (Valheim liest nur einmal je Sekunde)
		if (vagon.InUse())
		{
			ZRoutedRpc.instance.InvokeRoutedRPC(server, RpcClaimFailed, playerId, whistler, cart, "gccr_cart_busy");
			return;
		}
		if (SeatTaken(vagon))
		{
			// Review 27.10.2026: sonst verschwände der Karren unter einem sitzenden Spieler
			ZRoutedRpc.instance.InvokeRoutedRPC(server, RpcClaimFailed, playerId, whistler, cart, "gccr_cart_seat");
			return;
		}
		if (!container.CheckAccess(playerId))
		{
			ZRoutedRpc.instance.InvokeRoutedRPC(server, RpcClaimFailed, playerId, whistler, cart, "gccr_no_access"); // wie Valheims private Kisten
			return;
		}
		Inventory inventory = container.GetInventory();
		int ymir = YmirAmount.Value;
		if (!HasCargoFee(inventory, meat, ymir))
		{
			ZRoutedRpc.instance.InvokeRoutedRPC(server, RpcClaimFailed, playerId, whistler, cart, "gccr_need_cargo");
			return;
		}
		if (meat > 0)
		{
			inventory.RemoveItem(MeatName(), meat);
		}
		if (ymir > 0)
		{
			inventory.RemoveItem(YmirName(), ymir); // verschwindet beim Transport wie das Loxfleisch (Gorduan)
		}
		float fill = inventory.SlotsUsedPercentage();
		ZPackage items = new();
		inventory.Save(items);
		ZPackage cargo = new();
		cargo.Write(inventory.GetWidth());
		cargo.Write(inventory.GetHeight());
		cargo.Write(fill);
		cargo.Write(items.GetArray());
		// erst leeren, dann entfernen: nichts darf doppelt existieren, auch wenn danach etwas schiefgeht
		inventory.RemoveAll();
		container.Save();
		Vector3 position = vagon.transform.position;
		Quaternion rotation = vagon.transform.rotation;
		ZNetScene.instance.Destroy(vagon.gameObject);
		ZRoutedRpc.instance.InvokeRoutedRPC(server, RpcStored, playerId, whistler, cart, position, rotation, cargo);
	}

	// =================================================================== Server

	/// <summary>Angefragter Karren: wer gepfiffen hat, wem der Karren gehört (nur der darf die Fracht melden), welcher Karren.</summary>
	private sealed class Reservation
	{
		public long PlayerId;
		public long Peer;
		public long Owner;
		public ZDOID Cart;
		public double Until;
		/// <summary>Abgelaufen: bis dahin zählt noch eine späte Meldung ihres Besitzers (Late).</summary>
		public double LateUntil;
	}

	private sealed class Pickup
	{
		public long PlayerId;
		public ZDOID Cargo;
		public Vector3 Position;
		public Quaternion Rotation;
		public float Fill;
		public bool Easter;
		public double CallTime;
		public double NotBefore;
		public double Start;
		public double End;
	}

	/// <summary>Easteregg: Ruf und Wächter-Kampf; die Möwe kommt erst, wenn alle Wächter gefallen sind (Gorduan).</summary>
	private sealed class Fight
	{
		public long PlayerId;
		public long Whistler;
		public ZDOID Cargo;
		public Vector3 Position;
		public Quaternion Rotation;
		public float Fill;
		public double CallTime;
		public readonly List<ZDOID> Guards = new();
		public readonly HashSet<ZDOID> Seen = new();
		public bool Reported;
		public double ReportedAt;
		public double NextCheck;
	}

	/// <summary>Fracht beim abliefernden Spieler: gilt, bis er sie meldet, das Abwerfen scheitert oder er sich trennt (keine Zeitgrenze).</summary>
	private sealed class Lend
	{
		public long Peer;
		public ZDOID Cargo;
	}

	/// <summary>Server-Prüfung beim Abschicken: Spieler und Karren höchstens so weit auseinander (Mitte zu Mitte, ZDO-Positionen).</summary>
	private const float SendReach = 4f;

	/// <summary>
	/// So lange nach dem Ablauf einer Anfrage nimmt der Server die Fracht ihres Besitzers noch an (Review 27.10.2026): Der
	/// Besitzer leert und entfernt den Karren, bevor der Server die Fracht gespeichert hat, danach gibt es sie nur noch in
	/// seiner Meldung.
	/// </summary>
	private const double LateSeconds = 600.0;

	/// <summary>Höchstens so lange nach dem Erscheinen der Wächter, dann kommt das Finale von selbst (Review 27.10.2026).</summary>
	private const double FightSeconds = 20.0 * 60.0;

	/// <summary>So oft löscht der Server abgelieferte Fracht, die älter als 7 Tage ist.</summary>
	private const double PurgeSeconds = 600.0;

	private static readonly Dictionary<long, Reservation> Reserved = new();
	private static readonly List<Reservation> Late = new();
	private static double nextPurge;
	private static readonly Dictionary<long, Lend> Delivering = new();
	private static readonly List<Pickup> Queue = new();
	private static readonly List<Fight> Fights = new();
	private static Pickup? current;

	internal static long ServerUid() => ZRoutedRpc.instance != null ? ZRoutedRpc.instance.GetServerPeerID() : 0L;

	private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

	/// <summary>
	/// Unitys Spielzeit, nicht Valheims Netzzeit: Beim Schlafen springt die Netzzeit Hunderte Sekunden vor (Prüfung 19.10.:
	/// sonst liefen Fristen mitten im Abwerfen ab, und dieselbe Fracht ließe sich zweimal rufen). Nicht die echte Zeit: Im
	/// Pausenmenü der eigenen Welt steht die Spielzeit, und die Szenen der Spieler warten mit (Prüfung 2).
	/// </summary>
	internal static double Now => Time.timeAsDouble;

	/// <summary>Wo der Spieler steht (sein Spieler-ZDO beim Server, beim Gastgeber die eigene Figur).</summary>
	private static bool PositionOf(long peerUid, out Vector3 position)
	{
		position = Vector3.zero;
		if (peerUid == ZDOMan.GetSessionID())
		{
			if (Player.m_localPlayer == null)
			{
				return false;
			}
			position = Player.m_localPlayer.transform.position;
			return true;
		}
		ZNetPeer? peer = ZNet.instance.GetPeer(peerUid);
		ZDO? character = peer != null ? ZDOMan.instance.GetZDO(peer.m_characterID) : null;
		if (character == null)
		{
			return false;
		}
		position = character.GetPosition();
		return true;
	}

	/// <summary>Spieler-ID (Charakter) und Name eines verbundenen Spielers; 0, solange sein Spieler-ZDO dem Server fehlt.</summary>
	private static long PlayerIdOf(long peerUid, out string name)
	{
		name = "";
		if (ZNet.instance == null || ZDOMan.instance == null)
		{
			return 0L;
		}
		if (peerUid == ZDOMan.GetSessionID())
		{
			Player? local = Player.m_localPlayer; // Gastgeber einer eigenen Welt
			if (local == null)
			{
				return 0L;
			}
			name = local.GetPlayerName();
			return local.GetPlayerID();
		}
		ZNetPeer? peer = ZNet.instance.GetPeer(peerUid);
		if (peer == null)
		{
			return 0L;
		}
		name = peer.m_playerName;
		ZDO? character = ZDOMan.instance.GetZDO(peer.m_characterID);
		return character != null ? character.GetLong(ZDOVars.s_playerID) : 0L;
	}

	/// <summary>Verbundener Spieler mit dieser Spieler-ID (0: keiner).</summary>
	private static long PeerOfPlayer(long playerId)
	{
		if (ZNet.instance == null)
		{
			return 0L;
		}
		if (Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerID() == playerId)
		{
			return ZDOMan.GetSessionID();
		}
		foreach (ZNetPeer peer in ZNet.instance.GetPeers())
		{
			if (PlayerIdOf(peer.m_uid, out _) == playerId)
			{
				return peer.m_uid;
			}
		}
		return 0L;
	}

	private static void SendState(long peer, long playerId)
	{
		if (peer == 0L || ZRoutedRpc.instance == null)
		{
			return;
		}
		CargoState s = Store.StateOf(playerId);
		int index = 0;
		if (s == CargoState.Waiting && Fights.Exists(f => f.PlayerId == playerId))
		{
			index = -1; // Easteregg: erst müssen die Wächter fallen
		}
		else if (s == CargoState.Waiting)
		{
			for (int i = 0; i < Queue.Count; i++)
			{
				if (Queue[i].PlayerId == playerId)
				{
					index = i + 1 + (current != null ? 1 : 0); // Platz in der Reihe (1 = als Nächstes)
					break;
				}
			}
		}
		ZRoutedRpc.instance.InvokeRoutedRPC(peer, RpcState, (int)s, index);
	}

	private static void Message(long peer, string key, int value = 0)
	{
		if (peer != 0L)
		{
			ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcMessage, key, value);
		}
	}

	/// <summary>Die vom Spieler genannte ID gilt, wenn sein Spieler-ZDO sie bestätigt (oder es dem Server noch fehlt und nur gefragt wird).</summary>
	private static long Verified(long sender, long claimed, bool strict, out string name)
	{
		long known = PlayerIdOf(sender, out name);
		if (known == claimed && known != 0L)
		{
			return known;
		}
		return !strict && known == 0L ? claimed : 0L;
	}

	private static void RPC_Query(long sender, long playerId)
	{
		if (!IsServer)
		{
			return;
		}
		long id = Verified(sender, playerId, false, out _);
		if (id != 0L)
		{
			SendState(sender, id);
		}
	}

	private static void RPC_Send(long sender, long playerId, ZDOID cart)
	{
		if (!IsServer)
		{
			return;
		}
		if (!On)
		{
			Message(sender, "gccr_off");
			return;
		}
		long id = Verified(sender, playerId, true, out _);
		if (id == 0L)
		{
			Message(sender, "gccr_unknown");
			return;
		}
		if (Store.StateOf(id) != CargoState.None)
		{
			SendState(sender, id);
			Message(sender, "gccr_already");
			return;
		}
		double now = Now;
		if (Reserved.TryGetValue(id, out Reservation mine) && mine.Until > now)
		{
			return;
		}
		foreach (Reservation other in Reserved.Values)
		{
			if (other.Cart == cart && other.Until > now)
			{
				Message(sender, "gccr_cart_taken"); // zwei Pfiffe am selben Karren: der zweite bekommt eine Antwort
				return;
			}
		}
		ZDO? zdo = ZDOMan.instance.GetZDO(cart);
		if (zdo == null || zdo.GetPrefab() != CartHash || zdo.GetOwner() == 0L)
		{
			Message(sender, "gccr_no_cart");
			return;
		}
		// auch der Server prüft den Abstand (Prüfung 19.10.: sonst ließe sich jeder Karren von überall verschicken)
		if (!PositionOf(sender, out Vector3 where) || Vector3.Distance(where, zdo.GetPosition()) > SendReach)
		{
			Message(sender, "gccr_too_far");
			return;
		}
		long owner = zdo.GetOwner();
		Reserved[id] = new Reservation { PlayerId = id, Peer = sender, Owner = owner, Cart = cart, Until = now + 60.0 };
		ZRoutedRpc.instance.InvokeRoutedRPC(owner, cart, RpcClaim, id, sender, MeatAmount.Value);
	}

	/// <summary>Die passende Anfrage: nur der Besitzer des angefragten Karrens darf antworten.</summary>
	private static Reservation? Expected(long sender, long playerId, long whistler, ZDOID cart)
	{
		return Reserved.TryGetValue(playerId, out Reservation r) && r.Owner == sender && r.Peer == whistler && r.Cart == cart ? r : null;
	}

	private static void RPC_ClaimFailed(long sender, long playerId, long whistler, ZDOID cart, string reason)
	{
		if (!IsServer || Expected(sender, playerId, whistler, cart) == null)
		{
			return;
		}
		Reserved.Remove(playerId);
		Message(whistler, reason, MeatAmount.Value);
	}

	private static void RPC_Stored(long sender, long playerId, long whistler, ZDOID cart, Vector3 position, Quaternion rotation, ZPackage cargo)
	{
		if (!IsServer)
		{
			return;
		}
		if (Expected(sender, playerId, whistler, cart) != null)
		{
			Reserved.Remove(playerId);
		}
		else
		{
			// Review 27.10.2026: Der Besitzer hat den Karren schon geleert und entfernt, die Fracht gibt es nur noch hier. Eine
			// späte Meldung des reservierten Besitzers zählt noch, wenn der Spieler keine Fracht hat; sonst steht sie ganz im Log.
			Reservation? late = Late.Find(r => r.PlayerId == playerId && r.Owner == sender && r.Peer == whistler && r.Cart == cart);
			if (late == null || Store.StateOf(playerId) != CargoState.None)
			{
				ServerPlugin.Log.LogWarning($"Walküren-Transport: Fracht für {playerId} von {sender} abgelehnt ({(late == null ? "ohne passende Anfrage" : "der Spieler hat schon Fracht")}), Karren bei {position:F0}. Zum Retten (Base64 für new ZPackage(…): Breite, Höhe, Füllung, Inventar): {cargo.GetBase64()}");
				return;
			}
			Late.Remove(late);
			ServerPlugin.Log.LogInfo($"Walküren-Transport: späte Fracht für {playerId} von {sender} angenommen.");
		}
		int width = cargo.ReadInt();
		int height = cargo.ReadInt();
		float fill = cargo.ReadSingle();
		byte[] items = cargo.ReadByteArray();
		PlayerIdOf(whistler, out string name);
		ZDO record = Store.Put(playerId, name, width, height, fill, items, position);
		bool easter = EasterEgg.Value;
		int waiting = 0;
		if (easter)
		{
			// Easteregg: erst das Schauspiel und die Wächter, die Möwe danach (Finale)
			Fights.Add(new Fight { PlayerId = playerId, Whistler = whistler, Cargo = record.m_uid, Position = position, Rotation = rotation, Fill = fill, CallTime = Now });
		}
		else
		{
			Queue.Add(new Pickup
			{
				PlayerId = playerId,
				Cargo = record.m_uid,
				Position = position,
				Rotation = rotation,
				Fill = fill,
				Easter = false,
				CallTime = Now,
				NotBefore = Now + Show.NormalCallSeconds,
			});
			waiting = Queue.Count - 1 + (current != null ? 1 : 0); // so viele Karren holt die Walküre vorher
		}
		ServerPlugin.Log.LogInfo($"Walküren-Transport: Karren von {Store.Describe(record)} bei {position:F0} abgeschickt{(easter ? " (Easteregg)" : "")}, {waiting} vor ihm in der Reihe.");
		ZPackage show = new();
		show.Write(playerId);
		show.Write(whistler);
		show.Write(position);
		show.Write(rotation);
		show.Write(fill);
		show.Write(easter);
		show.Write(waiting);
		ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcCall, show);
		SendState(whistler, playerId);
		if (waiting > 0)
		{
			Message(whistler, "gccr_queued", waiting);
		}
	}

	private static void RPC_Deliver(long sender, long playerId, Vector3 target)
	{
		if (!IsServer)
		{
			return;
		}
		long id = Verified(sender, playerId, true, out _);
		if (id == 0L)
		{
			Message(sender, "gccr_unknown");
			return;
		}
		if (Delivering.ContainsKey(id))
		{
			return; // diese Fracht fällt schon
		}
		ZDO? record = Store.FirstCarried(id);
		if (record == null)
		{
			SendState(sender, id);
			CargoState state = Store.StateOf(id);
			Message(sender, state == CargoState.Waiting ? "gccr_waiting" : state == CargoState.Locked ? "gccr_locked" : "gccr_none");
			return;
		}
		Delivering[id] = new Lend { Peer = sender, Cargo = record.m_uid };
		ServerPlugin.Log.LogInfo($"Walküren-Transport: Karren von {Store.Describe(record)} fällt bei {target:F0}.");
		ZPackage show = new();
		show.Write(id);
		show.Write(sender);
		show.Write(target);
		show.Write(Store.Fill(record));
		ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcDrop, show);
		ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcCargo, id, record.m_uid, Store.Package(record));
	}

	/// <summary>
	/// Fracht liegt am Boden: Der genannte Datensatz gilt als abgeliefert, wenn er diesem Spieler gehört und in der Luft ist,
	/// auch wenn die Meldung spät kommt (Prüfung 19.10.: eine Fracht darf nie ein zweites Mal fallen).
	/// Review 27.10.2026 (Verdopplung beim Ausloggen direkt nach dem Aufprall): Der Spieler meldet jetzt im selben Bild, in dem
	/// er die Gegenstände erzeugt. Die Meldung geht als Routed-RPC sofort in die Leitung, seine neuen ZDOs folgen erst mit dem
	/// nächsten Abgleich (ZDOMan, alle 0,05 s, beim Ausloggen FlushClientObjects) auf derselben Verbindung, kommen also danach
	/// an. Der Datensatz wird nicht gelöscht, sondern 7 Tage als „abgeliefert“ aufbewahrt (Store.MarkDelivered): Stürzt das
	/// Spiel genau zwischen Meldung und Abgleich ab, fehlen die Gegenstände, die Fracht ist aber für den Admin noch da.
	/// Die Meldung des abwerfenden Spielers zählt auch, wenn sein Spieler-ZDO gerade fehlt (vom Aufprall getötet).
	/// </summary>
	private static void RPC_Delivered(long sender, long playerId, ZDOID recordId)
	{
		if (!IsServer)
		{
			return;
		}
		bool lent = Delivering.TryGetValue(playerId, out Lend lend) && lend.Peer == sender && lend.Cargo == recordId;
		long id = lent ? playerId : Verified(sender, playerId, true, out _);
		if (id == 0L)
		{
			ServerPlugin.Log.LogWarning($"Walküren-Transport: Meldung „abgeliefert“ für {playerId} von {sender} ohne passende Lieferung abgelehnt.");
			return;
		}
		if (Delivering.TryGetValue(id, out lend) && (lend.Peer == sender || lend.Cargo == recordId))
		{
			Delivering.Remove(id);
		}
		ZDO? record = Store.Find(recordId);
		if (record != null && Store.PlayerOf(record) == id && Store.StateOfRecord(record) == CargoState.Carried)
		{
			Store.MarkDelivered(record);
			ServerPlugin.Log.LogInfo($"Walküren-Transport: Fracht von {Store.Describe(record)} abgeliefert (der Datensatz bleibt {Store.KeepDelivered.TotalDays:0} Tage für den Admin).");
		}
		SendState(sender, id);
	}

	/// <summary>Abwerfen beim Spieler gescheitert: Die Fracht bleibt in der Luft und lässt sich noch einmal rufen.</summary>
	private static void RPC_DeliverFailed(long sender, long playerId)
	{
		if (!IsServer || !Delivering.TryGetValue(playerId, out Lend lend) || lend.Peer != sender)
		{
			return;
		}
		Delivering.Remove(playerId);
		ServerPlugin.Log.LogWarning($"Walküren-Transport: Abwerfen bei Spieler {playerId} gescheitert, die Fracht bleibt in der Luft.");
		SendState(sender, playerId);
		Message(sender, "gccr_drop_failed");
	}

	/// <summary>Jedes Bild auf dem Server: Zeitgrenzen, Reihe der Abholungen, Raids danach.</summary>
	internal static void ServerTick()
	{
		if (!IsServer || ZDOMan.instance == null || ZRoutedRpc.instance == null)
		{
			return;
		}
		double now = Now;
		if (Reserved.Count > 0)
		{
			List<long>? expired = null;
			foreach (KeyValuePair<long, Reservation> pair in Reserved)
			{
				if (pair.Value.Until < now)
				{
					(expired ??= new List<long>()).Add(pair.Key);
				}
			}
			expired?.ForEach(key =>
			{
				// abgelaufen, aber eine späte Meldung des Besitzers zählt noch eine Weile (Review 27.10.2026, RPC_Stored)
				Reservation old = Reserved[key];
				old.LateUntil = now + LateSeconds;
				Late.Add(old);
				Reserved.Remove(key);
			});
		}
		if (Late.Count > 0)
		{
			Late.RemoveAll(r => r.LateUntil < now);
		}
		if (now >= nextPurge)
		{
			nextPurge = now + PurgeSeconds;
			Store.PurgeDelivered();
		}
		if (Delivering.Count > 0)
		{
			List<long>? gone = null;
			foreach (KeyValuePair<long, Lend> pair in Delivering)
			{
				// getrennt: die Fracht bleibt in der Luft, man kann sie noch einmal rufen (keine Zeitgrenze, Prüfung 19.10.). Ohne
				// Meldung gab es kein Abwerfen: sie geht im selben Bild wie die Gegenstände und kommt vor ihnen an (Review 27.10.2026)
				bool connected = pair.Value.Peer == ZDOMan.GetSessionID() || ZNet.instance.GetPeer(pair.Value.Peer) != null;
				if (!connected)
				{
					(gone ??= new List<long>()).Add(pair.Key);
				}
			}
			gone?.ForEach(key => Delivering.Remove(key));
		}
		if (Fights.Count > 0)
		{
			TickFights(now);
		}
		if (current != null && now >= current.End)
		{
			Complete(current);
			current = null;
		}
		if (current == null && Queue.Count > 0)
		{
			StartPickup(Queue[0], now);
		}
		Raids.ServerTick(now);
	}

	private static void StartPickup(Pickup pickup, double now)
	{
		Queue.RemoveAt(0);
		double start = Math.Max(now, pickup.NotBefore);
		pickup.Start = start;
		pickup.End = start + Show.PickupSeconds(pickup.Easter);
		current = pickup;
		ZPackage show = new();
		show.Write(pickup.PlayerId);
		show.Write(pickup.Position);
		show.Write(pickup.Rotation);
		show.Write(pickup.Fill);
		show.Write(pickup.Easter);
		show.Write((float)(start - now));
		ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcPickup, show);
		foreach (Pickup waiting in Queue)
		{
			SendState(PeerOfPlayer(waiting.PlayerId), waiting.PlayerId); // alle rücken einen Platz vor
		}
	}

	private static void Complete(Pickup pickup)
	{
		ZDO? record = Store.Find(pickup.Cargo);
		bool raid = RaidAfter.Value;
		if (record != null && Store.StateOfRecord(record) == CargoState.Waiting)
		{
			// Gorduan: Event 2 erst, wenn der Raid vorbei ist (mit und ohne Easteregg). Nur aus „wartend“ (Review 27.10.2026): ein
			// abgelieferter Datensatz darf auf keinem Weg wieder in die Luft kommen
			Store.SetState(record, raid ? CargoState.Locked : CargoState.Carried);
		}
		long peer = PeerOfPlayer(pickup.PlayerId);
		SendState(peer, pickup.PlayerId);
		Message(peer, raid ? "gccr_carried_raid" : "gccr_carried");
		if (raid)
		{
			Raids.After(pickup.Position, pickup.Easter, pickup.Cargo);
		}
	}

	/// <summary>Der Raid nach dem Abholen ist vorbei (oder fiel aus): Jetzt lässt sich der Karren rufen.</summary>
	internal static void Unlock(ZDOID cargo, bool raidOver)
	{
		ZDO? record = Store.Find(cargo);
		if (record == null || Store.StateOfRecord(record) != CargoState.Locked)
		{
			return;
		}
		Store.SetState(record, CargoState.Carried);
		long playerId = Store.PlayerOf(record);
		long peer = PeerOfPlayer(playerId);
		SendState(peer, playerId);
		Message(peer, raidOver ? "gccr_unlocked" : "gccr_carried");
		ServerPlugin.Log.LogInfo($"Walküren-Transport: Raid vorbei, Karren von {Store.Describe(record)} lässt sich rufen.");
	}

	/// <summary>Easteregg: Wächter gemeldet? Alle gefallen? Dann Finale und die Möwe in die Reihe.</summary>
	private static void TickFights(double now)
	{
		foreach (Fight fight in Fights.ToArray())
		{
			if (!fight.Reported)
			{
				if (now > fight.CallTime + Easter.CallSeconds + 45.0)
				{
					ServerPlugin.Log.LogInfo("Walküren-Transport: keine Wächter gemeldet (Spieler fort?), die Möwe kommt so.");
					Finale(fight);
				}
				continue;
			}
			if (now > fight.ReportedAt + FightSeconds)
			{
				// Review 27.10.2026: ein verirrter oder unerreichbarer Wächter hielte die Fracht sonst bis zum Neustart „wartend“
				ServerPlugin.Log.LogInfo($"Walküren-Transport: Wächter-Kampf für Spieler {fight.PlayerId} nach {FightSeconds / 60.0:0} Minuten beendet, die Möwe kommt so.");
				Finale(fight);
				continue;
			}
			if (now < fight.NextCheck)
			{
				continue;
			}
			fight.NextCheck = now + 1.5;
			// Ein Wächter gilt erst als gefallen, wenn der Server ihn schon gesehen hat: Die Meldung kann vor den ZDOs ankommen
			// (Prüfung 2). Kommt einer eine Minute lang nicht an, zählt er nicht mehr.
			bool alive = false;
			foreach (ZDOID id in fight.Guards)
			{
				if (ZDOMan.instance.GetZDO(id) != null)
				{
					fight.Seen.Add(id);
					alive = true;
				}
				else if (!fight.Seen.Contains(id) && now < fight.ReportedAt + 60.0)
				{
					alive = true;
				}
			}
			if (!alive)
			{
				ServerPlugin.Log.LogInfo("Walküren-Transport: alle Wächter gefallen.");
				Finale(fight);
			}
		}
	}

	private static void Finale(Fight fight)
	{
		Fights.Remove(fight);
		ZPackage show = new();
		show.Write(fight.PlayerId);
		show.Write(fight.Position);
		ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcFinale, show);
		Queue.Add(new Pickup
		{
			PlayerId = fight.PlayerId,
			Cargo = fight.Cargo,
			Position = fight.Position,
			Rotation = fight.Rotation,
			Fill = fight.Fill,
			Easter = true,
			CallTime = fight.CallTime,
			NotBefore = Now + Show.FinaleSeconds,
		});
		SendState(PeerOfPlayer(fight.PlayerId), fight.PlayerId);
	}

	/// <summary>Die Wächter sind erschienen: nur der Spieler, der gepfiffen hat, meldet sie.</summary>
	private static void RPC_Guards(long sender, long playerId, ZPackage ids)
	{
		if (!IsServer)
		{
			return;
		}
		Fight? fight = Fights.Find(f => f.PlayerId == playerId && f.Whistler == sender && !f.Reported);
		if (fight == null)
		{
			return;
		}
		int count = ids.ReadInt();
		for (int i = 0; i < count; i++)
		{
			fight.Guards.Add(ids.ReadZDOID());
		}
		fight.Reported = true;
		fight.ReportedAt = Now;
		fight.NextCheck = Now + 3.0;
		ServerPlugin.Log.LogInfo($"Walküren-Transport: {count} Wächter für Spieler {playerId} gemeldet.");
		if (count == 0)
		{
			Finale(fight);
		}
	}

	/// <summary>Beim Spieler, der gepfiffen hat: die erschienenen Wächter dem Server melden.</summary>
	internal static void ReportGuards(long playerId, List<ZDOID> ids)
	{
		ZPackage pkg = new();
		pkg.Write(ids.Count);
		foreach (ZDOID id in ids)
		{
			pkg.Write(id);
		}
		ZRoutedRpc.instance?.InvokeRoutedRPC(RpcGuards, playerId, pkg);
	}

	/// <summary>Neue Welt (Server oder eigene Welt): alles Gemerkte vergessen.</summary>
	internal static void ResetServer()
	{
		Reserved.Clear();
		Late.Clear();
		nextPurge = 0.0;
		Delivering.Clear();
		Queue.Clear();
		Fights.Clear();
		current = null;
		Store.Reset();
		Raids.Reset();
	}

	/// <summary>Bei jedem Spielstart (Server und Spieler) die RPCs anmelden.</summary>
	internal static void RegisterRpcs()
	{
		ZRoutedRpc? rpc = ZRoutedRpc.instance;
		if (rpc == null)
		{
			return;
		}
		rpc.Register<long>(RpcQuery, RPC_Query);
		rpc.Register<long, ZDOID>(RpcSend, RPC_Send);
		rpc.Register<long, long, ZDOID, string>(RpcClaimFailed, RPC_ClaimFailed);
		rpc.Register<long, long, ZDOID, Vector3, Quaternion, ZPackage>(RpcStored, RPC_Stored);
		rpc.Register<long, Vector3>(RpcDeliver, RPC_Deliver);
		rpc.Register<long, ZDOID>(RpcDelivered, RPC_Delivered);
		rpc.Register<long>(RpcDeliverFailed, RPC_DeliverFailed);
		rpc.Register<int, int>(RpcState, RPC_State);
		rpc.Register<string, int>(RpcMessage, RPC_Message);
		rpc.Register<long, ZDOID, ZPackage>(RpcCargo, RPC_Cargo);
		rpc.Register<ZPackage>(RpcCall, (sender, pkg) => Show.OnCall(sender, pkg));
		rpc.Register<ZPackage>(RpcPickup, (sender, pkg) => Show.OnPickup(sender, pkg));
		rpc.Register<ZPackage>(RpcDrop, (sender, pkg) => Show.OnDrop(sender, pkg));
		rpc.Register<ZPackage>(RpcFinale, (sender, pkg) => Show.OnFinale(sender, pkg));
		rpc.Register<long, ZPackage>(RpcGuards, RPC_Guards);
		rpc.Register<Vector3, string>(RpcRaid, (sender, position, name) => Show.OnRaid(sender, position, name));
		state = CargoState.None;
		place = 0;
		known = false;
		queries = 0;
		nextQuery = 0f;
		waitUntil = 0f;
		ResetServer();
	}

	/// <summary>Raid begonnen: allen sagen (für Horn und Musik am Ort).</summary>
	internal static void AnnounceRaid(Vector3 position, string name)
	{
		ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcRaid, position, name);
	}
}

/// <summary>Jeder Karren nimmt die Anfrage des Servers an (Ausräumen nur beim Besitzer).</summary>
[HarmonyPatch(typeof(Vagon), nameof(Vagon.Awake))]
internal static class CourierCartPatch
{
	private static void Postfix(Vagon __instance)
	{
		ZNetView nview = __instance.m_nview;
		if (nview == null || nview.GetZDO() == null)
		{
			return;
		}
		Vagon vagon = __instance;
		nview.Register<long, long, int>(Transport.RpcClaim, (sender, playerId, whistler, meat) => Transport.Claim(vagon, sender, playerId, whistler, meat));
	}
}

/// <summary>Bei jedem Spielstart: RPCs anmelden, Gemerktes der letzten Sitzung vergessen.</summary>
[HarmonyPatch(typeof(Game), nameof(Game.Start))]
internal static class CourierGameStartPatch
{
	private static void Postfix()
	{
		Transport.RegisterRpcs();
		Show.Reset();
	}
}

/// <summary>Walkürenpfeife in die Hand genommen: kurz sagen, wo der eigene Karren ist und was der Pfiff gerade tut.</summary>
[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
internal static class CourierEquipHintPatch
{
	private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, bool __result)
	{
		if (__result && __instance == Player.m_localPlayer && Transport.On && item != null && Whistles.IsWhistle(item, out WhistleTier tier) && tier.Valkyrie && Hud.instance != null)
		{
			string status = Transport.TooltipStatus();
			if (status.Length > 0)
			{
				Gc.TopLeft(status);
			}
		}
	}
}

/// <summary>
/// Beschreibung der Walkürenpfeife im Tooltip mit den Mengen des Servers (Gorduan, 25.10.2026: nur „20× Loxfleisch + 1× Ymir-Fleisch
/// in einen Karren, daneben pfeifen“). Einzigartige legendäre Pfeifen zeigen ihre eigene Beschreibung, dort steht der Satz am Ende.
/// </summary>
[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), new Type[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool) })]
internal static class CourierDescriptionPatch
{
	private const string Token = "$" + Transport.DescriptionKey;

	private static void Postfix(ItemDrop.ItemData item, ref string __result)
	{
		if (item == null || __result == null || !Whistles.IsWhistle(item, out WhistleTier tier) || !tier.Valkyrie)
		{
			return;
		}
		string text = Transport.Description();
		__result = __result.Contains(Token) ? __result.Replace(Token, text) : __result + "\n" + text;
	}
}

/// <summary>Eigener Spieler gespawnt: Stand der eigenen Fracht beim Server erfragen.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
internal static class CourierSpawnPatch
{
	private static void Postfix(Player __instance)
	{
		if (__instance == Player.m_localPlayer)
		{
			Transport.Query();
		}
	}
}
