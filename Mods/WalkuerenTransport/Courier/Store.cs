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

	/// <summary>Alle Fracht-ZDOs der Welt, einmal je Sitzung gesucht. Was beim Neustart noch auf die Walküre wartete, ist jetzt in der Luft.</summary>
	private static List<ZDO> Records()
	{
		if (!scanned && ZDOMan.instance != null)
		{
			scanned = true;
			All.Clear();
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
				}
			}
			if (All.Count > 0)
			{
				ServerPlugin.Log.LogInfo($"Walküren-Transport: {All.Count} Fracht(en) in der Luft gefunden.");
			}
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

	/// <summary>Stand eines Spielers: in der Luft, sobald eine Fracht abgeholt ist, sonst wartend, sonst keine.</summary>
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
			if (s == CargoState.Locked || state == CargoState.None)
			{
				state = s == CargoState.Locked ? CargoState.Locked : CargoState.Waiting;
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

	/// <summary>Fracht abgeliefert: das ZDO verschwindet (dafür muss der Server sein Besitzer sein).</summary>
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
		return $"{NameOf(zdo)} ({PlayerOf(zdo)}), {count} Stapel";
	}
}
