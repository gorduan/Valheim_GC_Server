using System;
using System.Collections.Generic;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Courier;

/// <summary>Stand der Fracht eines Spielers: wartet auf die Walküre, ist in der Luft.</summary>
internal enum CargoState
{
	None = 0,
	Waiting = 1,
	Carried = 2,
	/// <summary>In der Luft, aber der Raid nach dem Abholen läuft noch: erst danach lässt sich der Karren rufen (Gorduan, 19.10.2026).</summary>
	Locked = 3,
	/// <summary>
	/// Abgeliefert (Review 27.10.2026, statt den Datensatz zu löschen): Der Spieler hat das Abwerfen gemeldet. Der Datensatz
	/// bleibt mit der Fracht 7 Tage in der Welt, damit ein Admin sie wiederherstellen kann, falls die Gegenstände nie beim
	/// Server ankamen. Geliefert wird er nie wieder, und er zählt für keinen Spieler mehr (Store.MarkDelivered).
	/// </summary>
	Delivered = 4,
}

/// <summary>
/// Fracht in der Luft, nur auf dem Server. Jede Fracht ist ein beständiges ZDO der Welt (Prefab „GC_CourierCargo“, ohne
/// Modell) weit außerhalb der Karte, wo nie ein Spieler hinkommt: Valheim speichert es mit der Welt und mit jeder Sicherung.
/// So passen Welt und Fracht immer zusammen (eine zurückgespielte Sicherung hat den Karren und keine Fracht oder umgekehrt,
/// nie beides), und ein Neustart verliert nichts. Inhalt: Valheims eigenes Inventar-Format (Container.Save), also mit
/// Qualität, Haltbarkeit, Hersteller und den Zusatzdaten von Mods (EpicLoot-Verzauberungen).
/// </summary>
internal static class Store
{
	internal const string Prefab = "GC_CourierCargo";
	internal static readonly int PrefabHash = Prefab.GetStableHashCode();
	private static readonly int PlayerHash = "gc_courier_player".GetStableHashCode();
	private static readonly int NameHash = "gc_courier_name".GetStableHashCode();
	private static readonly int ItemsHash = "gc_courier_items".GetStableHashCode();
	private static readonly int WidthHash = "gc_courier_w".GetStableHashCode();
	private static readonly int HeightHash = "gc_courier_h".GetStableHashCode();
	private static readonly int FillHash = "gc_courier_fill".GetStableHashCode();
	private static readonly int StateHash = "gc_courier_state".GetStableHashCode();
	private static readonly int OriginHash = "gc_courier_origin".GetStableHashCode();
	private static readonly int TimeHash = "gc_courier_time".GetStableHashCode();
	/// <summary>Abgeliefert: wem die Fracht gehörte (der Spieler-Schlüssel steht dann auf 0) und wann (UTC-Ticks).</summary>
	private static readonly int DeliveredPlayerHash = "gc_courier_delivered_player".GetStableHashCode();
	private static readonly int DeliveredTimeHash = "gc_courier_delivered_time".GetStableHashCode();

	/// <summary>So lange bleibt abgelieferte Fracht für den Admin in der Welt (Review 27.10.2026).</summary>
	internal static readonly TimeSpan KeepDelivered = TimeSpan.FromDays(7);

	/// <summary>
	/// Hinter dem Weltrand (Spieler werden ab 10 500 m zurückgeschoben), aber noch in Valheims Sektor-Raster (±16 384 m): kein
	/// Spieler lädt diese Zone, und die Welt speichert die ZDOs wie jedes andere Objekt (Abschnitts-Speicher seit 1.0.20).
	/// </summary>
	private static readonly Vector3 Parking = new(-12500f, 0f, -12500f);

	private static readonly List<ZDO> All = new();
	private static bool scanned;

	/// <summary>Leeres, beständiges Welt-Objekt für die Fracht (wie GC_Field: Jötunns Würfel ohne Modell und Kollision).</summary>
	internal static void CreatePrefab()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= CreatePrefab;
		GameObject? cargo = PrefabManager.Instance.CreateEmptyPrefab(Prefab, true);
		if (cargo == null)
		{
			ServerPlugin.Log.LogError("Walküren-Transport: Welt-Objekt " + Prefab + " konnte nicht angelegt werden.");
			return;
		}
		foreach (Component part in cargo.GetComponents<Component>())
		{
			if (part is Collider || part is Renderer || part is MeshFilter)
			{
				UnityEngine.Object.DestroyImmediate(part);
			}
		}
		cargo.GetComponent<ZNetView>().m_persistent = true;
		PrefabManager.Instance.AddPrefab(new CustomPrefab(cargo, false));
	}

	/// <summary>Neue Welt geladen: Liste vergessen (sie wird beim ersten Zugriff aus den ZDOs neu gebildet).</summary>
	internal static void Reset()
	{
		All.Clear();
		scanned = false;
	}

	/// <summary>
	/// Alle Fracht-ZDOs der Welt, einmal je Sitzung gesucht. Was beim Neustart noch auf die Walküre wartete, ist jetzt in der
	/// Luft. Abgelieferte bleiben abgeliefert (nur Wartend und Gesperrt werden rufbar; Speicherstände von vor dem 27.10.2026
	/// kennen den Zustand nicht und laden unverändert), nach 7 Tagen verschwinden sie.
	/// </summary>
	private static List<ZDO> Records()
	{
		if (!scanned && ZDOMan.instance != null)
		{
			scanned = true;
			All.Clear();
			int delivered = 0;
			foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
			{
				if (zdo != null && zdo.GetPrefab() == PrefabHash)
				{
					All.Add(zdo);
					int state = zdo.GetInt(StateHash);
					if (state == (int)CargoState.Waiting || state == (int)CargoState.Locked)
					{
						// Abholung, Wächter-Kampf oder Raid gingen beim Neustart verloren, die Fracht nicht: sie ist jetzt rufbar
						zdo.Set(StateHash, (int)CargoState.Carried);
					}
					else if (state == (int)CargoState.Delivered)
					{
						delivered++;
					}
				}
			}
			if (All.Count > 0)
			{
				ServerPlugin.Log.LogInfo($"Walküren-Transport: {All.Count - delivered} Fracht(en) in der Luft gefunden, {delivered} abgelieferte aufbewahrt.");
			}
			PurgeDelivered();
		}
		All.RemoveAll(z => z == null || !z.IsValid());
		return All;
	}

	internal static ZDO Put(long playerId, string playerName, int width, int height, float fill, byte[] items, Vector3 origin)
	{
		List<ZDO> records = Records(); // erst die Welt durchsuchen, sonst stünde das neue ZDO danach doppelt in der Liste
		ZDO zdo = ZDOMan.instance.CreateNewZDO(Parking + new Vector3((playerId & 0xFF) * 2f, 0f, records.Count * 2f), PrefabHash);
		zdo.SetPrefab(PrefabHash);
		zdo.Persistent = true;
		zdo.Set(PlayerHash, playerId);
		zdo.Set(NameHash, playerName);
		zdo.Set(WidthHash, width);
		zdo.Set(HeightHash, height);
		zdo.Set(FillHash, fill);
		zdo.Set(ItemsHash, items);
		zdo.Set(StateHash, (int)CargoState.Waiting);
		zdo.Set(OriginHash, origin);
		zdo.Set(TimeHash, DateTime.UtcNow.Ticks);
		records.Add(zdo);
		return zdo;
	}

	/// <summary>
	/// Stand eines Spielers: in der Luft, sobald eine Fracht abgeholt ist, sonst gesperrt, sonst wartend, sonst keine.
	/// Abgelieferte Fracht zählt nicht (Review 27.10.2026: sonst stünde der Spieler danach für immer auf „wartend“).
	/// </summary>
	internal static CargoState StateOf(long playerId)
	{
		CargoState state = CargoState.None;
		foreach (ZDO zdo in Records())
		{
			if (zdo.GetLong(PlayerHash) != playerId)
			{
				continue;
			}
			CargoState s = (CargoState)zdo.GetInt(StateHash);
			if (s == CargoState.Carried)
			{
				return CargoState.Carried;
			}
			if (s == CargoState.Locked)
			{
				state = CargoState.Locked;
			}
			else if (s == CargoState.Waiting && state == CargoState.None)
			{
				state = CargoState.Waiting;
			}
		}
		return state;
	}

	internal static CargoState StateOfRecord(ZDO zdo) => (CargoState)zdo.GetInt(StateHash);

	/// <summary>Älteste Fracht des Spielers, die schon in der Luft ist.</summary>
	internal static ZDO? FirstCarried(long playerId)
	{
		ZDO? best = null;
		foreach (ZDO zdo in Records())
		{
			if (zdo.GetLong(PlayerHash) == playerId && zdo.GetInt(StateHash) == (int)CargoState.Carried
				&& (best == null || zdo.GetLong(TimeHash) < best.GetLong(TimeHash)))
			{
				best = zdo;
			}
		}
		return best;
	}

	internal static ZDO? Find(ZDOID id)
	{
		foreach (ZDO zdo in Records())
		{
			if (zdo.m_uid == id)
			{
				return zdo;
			}
		}
		return null;
	}

	internal static void SetState(ZDO zdo, CargoState state) => zdo.Set(StateHash, (int)state);

	internal static float Fill(ZDO zdo) => zdo.GetFloat(FillHash);

	internal static long PlayerOf(ZDO zdo) => zdo.GetLong(PlayerHash);

	internal static string NameOf(ZDO zdo) => zdo.GetString(NameHash);

	/// <summary>Die Fracht als Paket für den abliefernden Spieler: Breite, Höhe, Füllung, Inventar-Daten.</summary>
	internal static ZPackage Package(ZDO zdo)
	{
		ZPackage pkg = new();
		pkg.Write(zdo.GetInt(WidthHash, 6));
		pkg.Write(zdo.GetInt(HeightHash, 3));
		pkg.Write(zdo.GetFloat(FillHash));
		pkg.Write(zdo.GetByteArray(ItemsHash) ?? Array.Empty<byte>());
		return pkg;
	}

	/// <summary>
	/// Fracht abgeliefert (Review 27.10.2026, statt sie zu löschen): Zustand „abgeliefert“ mit Zeitpunkt, die Fracht bleibt im
	/// Datensatz. Der Spieler wandert unter einen eigenen Schlüssel: So wird sie nie wieder geliefert, auch nicht von einer
	/// älteren Fassung der Mod nach einem Zurückspielen (die kennt den Zustand nicht und hielte den Spieler sonst für immer
	/// auf „wartend“).
	/// </summary>
	internal static void MarkDelivered(ZDO zdo)
	{
		zdo.Set(DeliveredPlayerHash, zdo.GetLong(PlayerHash));
		zdo.Set(DeliveredTimeHash, DateTime.UtcNow.Ticks);
		zdo.Set(StateHash, (int)CargoState.Delivered);
		zdo.Set(PlayerHash, 0L);
	}

	/// <summary>Wem eine abgelieferte Fracht gehörte (0 bei allen anderen).</summary>
	internal static long DeliveredTo(ZDO zdo) => zdo.GetLong(DeliveredPlayerHash);

	/// <summary>
	/// Abgelieferte Fracht nach 7 Tagen endgültig aus der Welt (beim ersten Durchsuchen der Welt und danach regelmäßig vom
	/// Server). Vor dem ersten Durchsuchen nichts: es soll nicht früher stattfinden als bisher.
	/// </summary>
	internal static void PurgeDelivered()
	{
		if (!scanned)
		{
			return;
		}
		long now = DateTime.UtcNow.Ticks;
		foreach (ZDO zdo in All.ToArray())
		{
			if (zdo == null || !zdo.IsValid() || zdo.GetInt(StateHash) != (int)CargoState.Delivered)
			{
				continue;
			}
			long at = zdo.GetLong(DeliveredTimeHash);
			if (at <= 0L)
			{
				zdo.Set(DeliveredTimeHash, now); // ohne Zeitpunkt: die 7 Tage beginnen jetzt, nicht gleich löschen
				continue;
			}
			if (now - at < KeepDelivered.Ticks)
			{
				continue;
			}
			ServerPlugin.Log.LogInfo($"Walküren-Transport: abgelieferte Fracht von {Describe(zdo)} nach 7 Tagen gelöscht.");
			Remove(zdo);
		}
	}

	/// <summary>Datensatz endgültig löschen: das ZDO verschwindet (dafür muss der Server sein Besitzer sein).</summary>
	internal static void Remove(ZDO zdo)
	{
		Records().Remove(zdo);
		if (!zdo.IsOwner())
		{
			zdo.SetOwner(ZDOMan.GetSessionID());
		}
		ZDOMan.instance.DestroyZDO(zdo);
	}

	/// <summary>Kurzbeschreibung für das Server-Log.</summary>
	internal static string Describe(ZDO zdo)
	{
		int count = 0;
		try
		{
			byte[]? data = zdo.GetByteArray(ItemsHash);
			if (data != null)
			{
				Inventory inventory = new("courier", null, Math.Max(1, zdo.GetInt(WidthHash, 6)), Math.Max(1, zdo.GetInt(HeightHash, 3)));
				inventory.Load(new ZPackage(data));
				count = inventory.NrOfItems();
			}
		}
		catch (Exception)
		{
			// nur für das Log
		}
		long player = StateOfRecord(zdo) == CargoState.Delivered ? DeliveredTo(zdo) : PlayerOf(zdo);
		return $"{NameOf(zdo)} ({player}), {count} Stapel";
	}
}
