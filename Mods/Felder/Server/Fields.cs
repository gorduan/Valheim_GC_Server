using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Fields;

/// <summary>Wer ein Feld aufheben darf (Gorduan, 16.10.2026: Standard alle, der Admin kann es einschränken).</summary>
public enum Removers
{
	Everyone,
	CreatorOrAdmin,
	CreatorGuildLeadersOrAdmin,
	AdminOnly
}

/// <summary>
/// Felder für den Kultivator, Server-Teil (Gorduan, 16.10.2026). Gezeichnet und bepflanzt wird in GC_Client. Hier entsteht das
/// unsichtbare Welt-Objekt „GC_Field“ (ohne Modell und Kollision, beständig): Valheim speichert es mit der Welt und
/// verteilt es an die Spieler in der Nähe wie jedes Bauteil, die Eckpunkte stehen in seinem ZDO. Dazu die Einstellungen,
/// die der Server vorgibt (Jötunn verteilt sie, nur Admins ändern sie im Menü GC Option).
/// </summary>
internal static class Plugin
{
	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<Removers> RemoveBy = null!;
	internal static ConfigEntry<int> MaxPoints = null!;
	internal static ConfigEntry<float> MaxEdge = null!;
	internal static ConfigEntry<float> Reach = null!;
	internal static ConfigEntry<float> Interval = null!;
	internal static ConfigEntry<float> SpacingBush = null!;
	internal static ConfigEntry<float> SpacingMushroom = null!;
	internal static ConfigEntry<float> SpacingOther = null!;

	internal static void Init(ConfigFile Config)
	{
		Log = ServerPlugin.Log;
		const string section = "11 - Fields";
		Enabled = Gc.BindSynced(Config, section, "Enabled", true, "Felder mit dem Kultivator markieren und automatisch in Reihen bepflanzen (Zeichnen und Pflanzen in GC_Client, wird vom Server vorgegeben).");
		RemoveBy = Gc.BindSynced(Config, section, "RemoveBy", Removers.Everyone, "Wer ein Feld aufheben darf: Everyone (alle), CreatorOrAdmin (Ersteller und Admins), CreatorGuildLeadersOrAdmin (Ersteller, Leiter und Co-Leiter seiner Gilde, Admins; ohne Gilden-Mod wie CreatorOrAdmin), AdminOnly (nur Admins). Die Pflanzen bleiben immer stehen.");
		MaxPoints = Gc.BindSynced(Config, section, "MaxPoints", 32, "Höchstzahl Eckpunkte je Feld, 0 = unbegrenzt.", new AcceptableValueRange<int>(0, 1000));
		MaxEdge = Gc.BindSynced(Config, section, "MaxEdgeLength", 50f, "Höchstlänge einer Feldkante in Metern, 0 = unbegrenzt.", new AcceptableValueRange<float>(0f, 1000f));
		Reach = Gc.BindSynced(Config, section, "Reach", 10f, "So weit vom Spieler werden Plätze im Feld bepflanzt (Meter). Man kann sich dabei bewegen.", new AcceptableValueRange<float>(2f, 100f));
		Interval = Gc.BindSynced(Config, section, "PlantInterval", 0.5f, "Sekunden zwischen zwei Pflanzen beim Halten von E (Valheim selbst braucht mindestens 0,4 s).", new AcceptableValueRange<float>(0.1f, 10f));
		SpacingBush = Gc.BindSynced(Config, section, "SpacingBush", 1.5f, "Abstand für Sträucher ohne eigene Wachstumsregel (z. B. Beerensträucher aus PlantEverything), in Metern.", new AcceptableValueRange<float>(0.1f, 50f));
		SpacingMushroom = Gc.BindSynced(Config, section, "SpacingMushroom", 0.5f, "Abstand für Pilze ohne eigene Wachstumsregel, in Metern.", new AcceptableValueRange<float>(0.1f, 50f));
		SpacingOther = Gc.BindSynced(Config, section, "SpacingOther", 1f, "Abstand für alles andere ohne eigene Wachstumsregel (Blumen, Disteln usw.), in Metern.", new AcceptableValueRange<float>(0.1f, 50f));
		GcFields.Publish(new Dictionary<string, Func<object>>
		{
			["enabled"] = () => Enabled.Value,
			["removeBy"] = () => (int)RemoveBy.Value,
			["maxPoints"] = () => MaxPoints.Value,
			["maxEdge"] = () => MaxEdge.Value,
			["reach"] = () => Reach.Value,
			["interval"] = () => Interval.Value,
			["spacingBush"] = () => SpacingBush.Value,
			["spacingMushroom"] = () => SpacingMushroom.Value,
			["spacingOther"] = () => SpacingOther.Value,
		});
		PrefabManager.OnVanillaPrefabsAvailable += CreatePrefab;
	}

	/// <summary>Leeres, beständiges Welt-Objekt: Jötunns Vorlage ist ein Würfel, Modell und Kollision kommen weg.</summary>
	private static void CreatePrefab()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= CreatePrefab;
		GameObject? field = PrefabManager.Instance.CreateEmptyPrefab(GcFields.Prefab, true);
		if (field == null)
		{
			Log.LogError("Felder: Welt-Objekt " + GcFields.Prefab + " konnte nicht angelegt werden.");
			return;
		}
		foreach (Component part in field.GetComponents<Component>())
		{
			if (part is Collider || part is Renderer || part is MeshFilter)
			{
				UnityEngine.Object.DestroyImmediate(part);
			}
		}
		ZNetView view = field.GetComponent<ZNetView>();
		view.m_persistent = true;
		view.m_distant = true; // auch in größerem Umkreis geladen: große Felder sieht man ganz
		PrefabManager.Instance.AddPrefab(new CustomPrefab(field, false));
		Log.LogInfo("Felder: Welt-Objekt " + GcFields.Prefab + " angelegt.");
	}
}
