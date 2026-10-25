using System.Collections.Generic;
using BepInEx.Bootstrap;
using GC.Shared;
using HarmonyLib;
using UnityEngine;

namespace GC.Client.Fields;

/// <summary>Pflanzbares aus dem Kultivator: Wuchsradius, Körper und Reihenabstand.</summary>
internal sealed class Spec
{
	public Piece Piece = null!;
	public Plant? Plant;
	/// <summary>So viel freien Platz braucht die Pflanze um sich (Plant.m_growRadius, ohne Wuchsregel: Abstand minus Körper).</summary>
	public float Radius;
	/// <summary>Waagerechter Radius ihres Körpers (Kollider, auch ausgewachsen).</summary>
	public float Body;
	/// <summary>Abstand zweier gleicher Pflanzen in der Reihe (dichteste Packung, die Valheim wachsen lässt, plus Spiel).</summary>
	public float Step;
}

/// <summary>Eine Pflanze oder ein Strauch in der Nähe.</summary>
internal struct Neighbour
{
	public Vector3 Position;
	public float Radius;
	public float Body;
}

/// <summary>
/// Pflanzen im Feld (Gorduan, 16.10.2026). Mit einem Samen im Kultivator springt die Vorschau im Feld auf den nächsten
/// freien Platz: Reihen parallel zur ersten Kante, Reihe für Reihe ab dieser Kante, gleiche Pflanzen versetzt wie
/// Bienenwaben (dichteste Kreispackung). Nur Plätze bis zur Reichweite des Servers (Standard 10 m) um den Spieler, man
/// kann dabei laufen. E halten setzt alle Pause-Sekunden (Standard 0,5) eine Pflanze.
///
/// Gesetzt wird über Valheims eigenen Klick (Kosten, Ausdauer, Haltbarkeit, Skill, Schutzzonen, auch fremde Mods). Die
/// Vorschau wird dafür in Player.PieceRayTest auf den Platz gelegt; Valheim prüft dort selbst. Vorher prüft diese Klasse,
/// ob die Pflanze dort wachsen wird (Plant.UpdateHealth: Biom, bestellter Boden, Hitze und Kälte, Dach, Platz).
/// Abstand zweier Pflanzen a und b: mindestens max(Radius a + Körper b, Radius b + Körper a) + 5 cm, wie
/// Plant.HaveGrowSpace (eine Kugel mit dem Wuchsradius darf keinen fremden Kollider treffen). Das Gitter hat 2 cm mehr
/// Spiel, damit Rundungen in Weltkoordinaten keine Plätze kippen.
/// </summary>
internal static class Planting
{
	private const float Margin = 0.05f;
	private const float Slack = 0.02f;
	private const string PlantEasilyGuid = "advize.PlantEasily";

	private enum Kind : byte
	{
		None,
		Plant,
		Pickable
	}

	private struct PrefabInfo
	{
		public Kind Kind;
		public float Radius;
		public float Body;
	}

	private static readonly Dictionary<string, Spec> Specs = new();
	private static readonly Dictionary<string, float> Bodies = new();
	private static readonly Dictionary<int, PrefabInfo> Prefabs = new();
	private static Dictionary<string, float>? grownRadius;
	private static float cachesUntil;
	private static readonly List<Neighbour> Neighbours = new();
	private static readonly Dictionary<long, float> Failed = new();
	private static readonly List<long> Expired = new();
	private static readonly List<(Vector3 Position, Spec Spec, float Until)> Recent = new();
	private static readonly Collider[] Hits = new Collider[64];

	private static int roofMask;
	private static int spaceMask;

	// Umlenken der Vorschau (je Aufruf von UpdatePlacementGhost)
	private static bool inGhost;
	internal static bool Redirected;
	private static bool blocking;
	private static Vector3 target;
	private static Player.PlacementStatus captured;
	private static Vector3 capturedPosition;
	private static bool lastValid;

	// letzter gesuchter Platz
	private static Vector3? slot;
	private static float searchUntil;
	private static FieldView? slotField;
	private static Spec? slotSpec;

	// E halten
	private static bool holding;
	private static bool noSeeds;
	private static float nextAuto;
	private static float autoClickUntil;
	private static float nextNoSlot;
	private static bool warnedPlantEasily;

	private static float Reach => GcFields.Get("reach", 10f);

	/// <summary>PlantEasily setzt ganze Raster je Klick: dann pflanzen Felder nicht automatisch.</summary>
	internal static bool PlantEasily => Chainloader.PluginInfos.ContainsKey(PlantEasilyGuid);

	/// <summary>Neue Spielsitzung: alles neu lesen (PlantEverything ändert Wuchsradien beim Start).</summary>
	internal static void ResetCaches()
	{
		Specs.Clear();
		Bodies.Clear();
		Prefabs.Clear();
		grownRadius = null;
		Recent.Clear();
		Failed.Clear();
		slot = null;
		slotField = null;
		slotSpec = null;
	}

	internal static bool IsPlantable(Piece? piece)
	{
		return piece != null && !Plugin.IsMarker(piece) && piece.GetComponent<TerrainOp>() == null
			&& (piece.GetComponent<Plant>() != null || piece.GetComponent<Pickable>() != null);
	}

	/// <summary>Abstände hängen an Server-Einstellungen und Wuchsradien: alle 30 s neu berechnen.</summary>
	private static void RefreshCaches()
	{
		if (Time.time < cachesUntil)
		{
			return;
		}
		cachesUntil = Time.time + 30f;
		Specs.Clear();
		Prefabs.Clear();
		grownRadius = null;
	}

	private static Spec SpecOf(Piece piece)
	{
		string name = piece.gameObject.name;
		if (Specs.TryGetValue(name, out Spec spec) && spec.Piece == piece)
		{
			return spec;
		}
		Plant? plant = piece.GetComponent<Plant>();
		float body = BodyOf(name, piece.gameObject);
		float radius = plant != null ? plant.m_growRadius : Mathf.Max(0.05f, Spacing(name) - body);
		spec = new Spec { Piece = piece, Plant = plant, Radius = radius, Body = body, Step = radius + body + Margin + Slack };
		Specs[name] = spec;
		return spec;
	}

	/// <summary>Abstand für Pflanzbares ohne Wuchsregel (PlantEverything): Sträucher, Pilze, Sonstiges (Server-Einstellung).</summary>
	private static float Spacing(string name)
	{
		string lower = name.ToLowerInvariant();
		if (lower.Contains("bush"))
		{
			return GcFields.Get("spacingBush", 1.5f);
		}
		if (lower.Contains("mushroom"))
		{
			return GcFields.Get("spacingMushroom", 0.5f);
		}
		return GcFields.Get("spacingOther", 1f);
	}

	/// <summary>Waagerechter Körperradius eines Prefabs aus seinen Kollidern, bei Pflanzen auch der ausgewachsenen Formen.</summary>
	private static float BodyOf(string name, GameObject? instance)
	{
		if (Bodies.TryGetValue(name, out float body))
		{
			return body;
		}
		GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
		GameObject? source = prefab != null ? prefab : instance;
		body = source != null ? ColliderRadius(source) : 0.2f;
		Plant? plant = source != null ? source.GetComponent<Plant>() : null;
		if (plant != null && plant.m_grownPrefabs != null)
		{
			foreach (GameObject grown in plant.m_grownPrefabs)
			{
				if (grown != null)
				{
					body = Mathf.Max(body, ColliderRadius(grown));
				}
			}
		}
		body = Mathf.Clamp(body, 0.05f, 1.5f);
		Bodies[name] = body;
		return body;
	}

	private static float ColliderRadius(GameObject go)
	{
		float result = 0f;
		Transform root = go.transform;
		foreach (Collider collider in go.GetComponentsInChildren<Collider>(true))
		{
			if (collider.isTrigger)
			{
				continue;
			}
			Vector3 scale = collider.transform.lossyScale;
			float flat = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
			Vector3 offset = root.InverseTransformPoint(collider.transform.position);
			float shift = new Vector2(offset.x, offset.z).magnitude;
			float radius = collider switch
			{
				CapsuleCollider capsule => capsule.radius * flat + new Vector2(capsule.center.x, capsule.center.z).magnitude * flat,
				SphereCollider sphere => sphere.radius * flat + new Vector2(sphere.center.x, sphere.center.z).magnitude * flat,
				BoxCollider box => 0.5f * Mathf.Max(box.size.x * Mathf.Abs(scale.x), box.size.z * Mathf.Abs(scale.z)) + new Vector2(box.center.x, box.center.z).magnitude * flat,
				MeshCollider mesh when mesh.sharedMesh != null => Mathf.Max(mesh.sharedMesh.bounds.extents.x * Mathf.Abs(scale.x), mesh.sharedMesh.bounds.extents.z * Mathf.Abs(scale.z)),
				_ => 0f,
			};
			result = Mathf.Max(result, radius + shift);
		}
		return result;
	}

	/// <summary>Wuchsradius der Pflanze, aus der ein Erntegut wächst (Pickable_Carrot ← Möhrensamen), sonst −1.</summary>
	private static float GrownRadius(string name)
	{
		if (grownRadius == null)
		{
			grownRadius = new Dictionary<string, float>();
			if (ZNetScene.instance != null)
			{
				foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
				{
					Plant? plant = prefab != null ? prefab.GetComponent<Plant>() : null;
					if (plant?.m_grownPrefabs == null)
					{
						continue;
					}
					foreach (GameObject grown in plant.m_grownPrefabs)
					{
						if (grown != null && (!grownRadius.TryGetValue(grown.name, out float old) || plant.m_growRadius > old))
						{
							grownRadius[grown.name] = plant.m_growRadius;
						}
					}
				}
			}
		}
		return grownRadius.TryGetValue(name, out float radius) ? radius : -1f;
	}

	/// <summary>Pflanze, Erntegut/Strauch oder nichts, je Prefab einmal bestimmt.</summary>
	private static PrefabInfo InfoOf(int hash)
	{
		if (Prefabs.TryGetValue(hash, out PrefabInfo info))
		{
			return info;
		}
		info = new PrefabInfo { Kind = Kind.None };
		GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
		if (prefab != null)
		{
			Plant? plant = prefab.GetComponent<Plant>();
			if (plant != null)
			{
				info = new PrefabInfo { Kind = Kind.Plant, Radius = plant.m_growRadius, Body = BodyOf(prefab.name, prefab) };
			}
			else if (prefab.GetComponent<Pickable>() != null)
			{
				float body = BodyOf(prefab.name, prefab);
				float grown = GrownRadius(prefab.name);
				info = new PrefabInfo { Kind = Kind.Pickable, Radius = grown >= 0f ? grown : Mathf.Max(0.05f, Spacing(prefab.name) - body), Body = body };
			}
		}
		Prefabs[hash] = info;
		return info;
	}

	// ------------------------------------------------------------------ Platz suchen

	/// <summary>Pflanzen und Sträucher um den Spieler (alle geladenen Objekte, nach ZDO-Position und Prefab vorsortiert).</summary>
	private static void CollectNeighbours(Vector3 around, float range)
	{
		Neighbours.Clear();
		if (ZNetScene.instance == null)
		{
			return;
		}
		float range2 = range * range;
		foreach (KeyValuePair<ZDO, ZNetView> pair in ZNetScene.instance.m_instances)
		{
			ZDO zdo = pair.Key;
			Vector3 position = zdo.GetPosition();
			float dx = position.x - around.x;
			float dz = position.z - around.z;
			if (dx * dx + dz * dz > range2)
			{
				continue;
			}
			PrefabInfo info = InfoOf(zdo.GetPrefab());
			if (info.Kind != Kind.None)
			{
				Neighbours.Add(new Neighbour { Position = position, Radius = info.Radius, Body = info.Body });
			}
		}
		for (int i = Recent.Count - 1; i >= 0; i--)
		{
			if (Time.time > Recent[i].Until)
			{
				Recent.RemoveAt(i);
			}
			else
			{
				Neighbours.Add(new Neighbour { Position = Recent[i].Position, Radius = Recent[i].Spec.Radius, Body = Recent[i].Spec.Body });
			}
		}
	}

	private static bool PairOk(Vector3 point, Spec spec)
	{
		foreach (Neighbour other in Neighbours)
		{
			float need = Mathf.Max(spec.Radius + other.Body, other.Radius + spec.Body) + Margin;
			float dx = other.Position.x - point.x;
			float dz = other.Position.z - point.z;
			if (dx * dx + dz * dz < need * need)
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>Wächst die Pflanze hier (wie Plant.UpdateHealth)? Ranken brauchen Halt und gehören nicht ins Feld.</summary>
	private static bool Grows(Vector3 point, Spec spec)
	{
		if (ZoneSystem.instance != null && point.y < ZoneSystem.instance.m_waterLevel)
		{
			return false; // unter Wasser
		}
		Heightmap? heightmap = Heightmap.FindHeightmap(point);
		if (heightmap == null)
		{
			return false; // Gelände nicht geladen
		}
		Heightmap.Biome biome = heightmap.GetBiome(point);
		if (spec.Piece.m_onlyInBiome != Heightmap.Biome.None && (biome & spec.Piece.m_onlyInBiome) == 0)
		{
			return false; // Valheim lehnt das Pflanzen hier ab
		}
		Plant? plant = spec.Plant;
		if (plant != null)
		{
			if ((biome & plant.m_biome) == 0 || (plant.m_needCultivatedGround && !heightmap.IsCultivated(point)) || plant.m_attachDistance > 0f)
			{
				return false;
			}
			bool shielded = ShieldGenerator.IsInsideShield(point);
			if ((!plant.m_tolerateHeat && biome == Heightmap.Biome.AshLands && !shielded)
				|| (!plant.m_tolerateCold && (biome == Heightmap.Biome.DeepNorth || biome == Heightmap.Biome.Mountain) && !shielded))
			{
				return false;
			}
			if (roofMask == 0)
			{
				roofMask = LayerMask.GetMask("Default", "static_solid", "piece");
			}
			if (Physics.Raycast(point + Vector3.up * 0.1f, Vector3.up, 100f, roofMask))
			{
				return false; // kein Himmel über der Pflanze
			}
		}
		if (spec.Piece.m_cultivatedGroundOnly && !heightmap.IsCultivated(point))
		{
			return false;
		}
		if (spaceMask == 0)
		{
			spaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
		}
		int count = Physics.OverlapSphereNonAlloc(point, spec.Radius + Slack, Hits, spaceMask);
		for (int i = 0; i < count; i++)
		{
			Collider hit = Hits[i];
			if (hit != null && hit.GetComponentInParent<Plant>() == null && hit.GetComponentInParent<Pickable>() == null)
			{
				return false; // Stein, Bauteil, Baum … (andere Pflanzen prüft PairOk mit Abstand)
			}
		}
		return true;
	}

	private static long Key(Vector3 point) => ((long)Mathf.RoundToInt(point.x * 10f) << 32) ^ (uint)Mathf.RoundToInt(point.z * 10f);

	private static bool IsFailed(Vector3 point) => Failed.TryGetValue(Key(point), out float until) && Time.time < until;

	/// <summary>
	/// Nächster freier Platz im Feld für diese Pflanze: Reihen parallel zur ersten Kante (versetzt wie Bienenwaben), in
	/// Reihenfolge ab der ersten Kante, nur in Reichweite des Spielers. null, wenn keiner frei ist.
	/// </summary>
	private static Vector3? Search(Player player, Field field, Spec spec)
	{
		Vector3 me = player.transform.position;
		float reach = Reach;
		CollectNeighbours(me, reach + 4f);
		float step = spec.Step;
		float pitch = step * 0.8660254f;
		float inset = Mathf.Min(spec.Body, 0.25f) + Margin;
		float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
		foreach (Vector3 corner in field.Points)
		{
			Vector3 d = corner - field.Origin;
			float u = d.x * field.Along.x + d.z * field.Along.z;
			float v = d.x * field.Inward.x + d.z * field.Inward.z;
			minU = Mathf.Min(minU, u);
			maxU = Mathf.Max(maxU, u);
			minV = Mathf.Min(minV, v);
			maxV = Mathf.Max(maxV, v);
		}
		// nur Reihen und Spalten in Reichweite des Spielers
		Vector3 rel = me - field.Origin;
		float meU = rel.x * field.Along.x + rel.z * field.Along.z;
		float meV = rel.x * field.Inward.x + rel.z * field.Inward.z;
		minU = Mathf.Max(minU, meU - reach);
		maxU = Mathf.Min(maxU, meU + reach);
		minV = Mathf.Max(minV, meV - reach);
		maxV = Mathf.Min(maxV, meV + reach);
		if (minU > maxU || minV > maxV)
		{
			return null;
		}
		int firstRow = Mathf.FloorToInt((minV - inset) / pitch);
		int lastRow = Mathf.CeilToInt((maxV - inset) / pitch);
		for (int row = firstRow; row <= lastRow; row++)
		{
			float v = inset + row * pitch;
			float shift = (row & 1) == 1 ? step * 0.5f : 0f;
			int first = Mathf.FloorToInt((minU - shift) / step);
			int last = Mathf.CeilToInt((maxU - shift) / step);
			for (int col = first; col <= last; col++)
			{
				float u = col * step + shift;
				Vector3 point = field.Origin + field.Along * u + field.Inward * v;
				float dx = point.x - me.x;
				float dz = point.z - me.z;
				if (dx * dx + dz * dz > reach * reach || !field.Contains(point) || field.EdgeDistance(point) < inset)
				{
					continue;
				}
				point.y = Geometry.Ground(point);
				if (IsFailed(point) || !PairOk(point, spec) || !Grows(point, spec))
				{
					continue;
				}
				return point;
			}
		}
		return null;
	}

	// ------------------------------------------------------------------ Vorschau umlenken

	internal static void BeginGhost()
	{
		inGhost = true;
		Redirected = false;
		blocking = false;
	}

	/// <summary>
	/// Aus Player.PieceRayTest: Zielt der Spieler mit einem Samen ins Feld (oder hält er E an einem Feld), liegt die
	/// Vorschau auf dem nächsten freien Platz. Kein freier Platz im Feld: Pflanzen dort sperren.
	/// </summary>
	internal static bool Redirect(Player player, bool hit, Vector3 aim, out Vector3 point)
	{
		point = Vector3.zero;
		if (!inGhost || player != Player.m_localPlayer || !Plugin.On || !Plugin.HoldsCultivator(player))
		{
			return false;
		}
		Piece? piece = player.GetSelectedPiece();
		if (!IsPlantable(piece))
		{
			return false;
		}
		if (PlantEasily)
		{
			if (!warnedPlantEasily)
			{
				warnedPlantEasily = true;
				Plugin.Log.LogWarning("Felder: PlantEasily ist installiert (setzt ganze Raster je Klick), deshalb pflanzen Felder nicht automatisch.");
			}
			return false;
		}
		RefreshCaches();
		FieldView? aimed = hit ? Plugin.At(aim) : null;
		FieldView? view = aimed;
		if (view == null && holding)
		{
			view = Plugin.Nearest(player.transform.position, Reach);
		}
		Field? field = view != null ? view.Data : null;
		if (field == null)
		{
			slot = null;
			return false;
		}
		Spec spec = SpecOf(piece!);
		if (view != slotField || spec != slotSpec || Time.time >= searchUntil)
		{
			slotField = view;
			slotSpec = spec;
			searchUntil = Time.time + 0.25f;
			slot = Search(player, field, spec);
		}
		if (slot == null)
		{
			blocking = aimed != null; // Fadenkreuz im Feld, aber kein Platz frei
			return false;
		}
		point = slot.Value;
		target = point;
		Redirected = true;
		return true;
	}

	/// <summary>Nach Valheims Prüfung, vor anderen Mods: Ergebnis am Feldplatz merken.</summary>
	internal static void Capture(Player player)
	{
		if (Redirected && player.m_placementGhost != null)
		{
			captured = player.m_placementStatus;
			capturedPosition = player.m_placementGhost.transform.position; // mit Valheims eigener Anpassung an den Boden
		}
	}

	/// <summary>
	/// Zuletzt: Hat eine andere Mod (EarthWrights Saat-Raster, Valheim Plus Ausrichtung) die Vorschau verschoben, kommt
	/// sie zurück auf Valheims Position am Feldplatz, mit Valheims Ergebnis dort; sonst gilt, was die Mods entschieden haben
	/// (etwa Valheim Plus' gelockerte Regeln). Ein abgelehnter Platz wird übersprungen (Schutzzone, Bauverbot: 30 s). Im
	/// Feld ohne freien Platz wird nicht gepflanzt, und ein E-Klick pflanzt nie außerhalb eines Feldes.
	/// </summary>
	internal static void EndGhost(Player player, bool placing)
	{
		inGhost = false;
		GameObject? ghost = player.m_placementGhost;
		lastValid = false;
		if (ghost == null)
		{
			return;
		}
		if (Redirected)
		{
			if ((ghost.transform.position - capturedPosition).sqrMagnitude > 0.000001f)
			{
				ghost.transform.position = capturedPosition; // eine andere Mod hat sie verschoben
				player.m_placementStatus = captured;
				player.SetPlacementGhostValid(captured == Player.PlacementStatus.Valid);
			}
			Player.PlacementStatus status = player.m_placementStatus;
			lastValid = status == Player.PlacementStatus.Valid;
			if (!lastValid)
			{
				// nächste Suche nimmt den nächsten Platz; Schutzzone und Bauverbot ändern sich selten, dort länger überspringen
				bool area = status == Player.PlacementStatus.PrivateZone || status == Player.PlacementStatus.NoBuildZone;
				Failed[Key(target)] = Time.time + (area ? 30f : 3f);
			}
		}
		else if (blocking || (placing && Time.time < autoClickUntil))
		{
			player.m_placementStatus = Player.PlacementStatus.MoreSpace;
			player.SetPlacementGhostValid(false);
		}
	}

	/// <summary>Valheim hat gepflanzt: Platz als belegt merken, bis die Pflanze in den Objekten auftaucht.</summary>
	internal static void Placed(Piece piece, Vector3 position)
	{
		if (!IsPlantable(piece))
		{
			return;
		}
		Recent.Add((position, SpecOf(piece), Time.time + 3f));
		searchUntil = 0f;
		autoClickUntil = 0f;
	}

	// ------------------------------------------------------------------ E halten

	internal static void Update(Player? player, Piece? selected)
	{
		Expire();
		if (player == null || !IsPlantable(selected) || PlantEasily)
		{
			holding = false;
			noSeeds = false;
			return;
		}
		bool use = ZInput.GetButton("Use") && !Gc.TextInputActive() && !InventoryGui.IsVisible() && !Hud.IsPieceSelectionVisible()
			&& !Menu.IsVisible() && !UnifiedPopup.IsVisible() && !player.IsDead();
		if (!use || Plugin.Nearest(player.transform.position, Reach) == null)
		{
			holding = false;
			noSeeds = false;
			return; // E ohne Feld in der Nähe: nichts zu tun
		}
		holding = true;
		if (noSeeds || Time.time < nextAuto)
		{
			return;
		}
		if (!lastValid)
		{
			nextAuto = Time.time + 0.25f;
			if (Time.time >= nextNoSlot)
			{
				nextNoSlot = Time.time + 3f;
				Gc.TopLeft(GcLoc.Get("gccf_no_slot"));
			}
			return;
		}
		ItemDrop.ItemData? tool = player.GetRightItem();
		if (tool == null || !player.HaveStamina(tool.m_shared.m_attack.m_attackStamina))
		{
			if (Hud.instance != null)
			{
				Hud.instance.StaminaBarEmptyFlash();
			}
			nextAuto = Time.time + 0.5f;
			return;
		}
		if (!player.m_noPlacementCost && !player.HaveRequirements(selected!, Player.RequirementMode.CanBuild))
		{
			noSeeds = true; // erst nach erneutem Drücken weiter
			Gc.TopLeft(GcLoc.Get("gccf_no_seeds"));
			return;
		}
		player.m_placePressedTime = Time.time; // ein echter Klick in Valheims Bau-Ablauf
		autoClickUntil = Time.time + 0.3f;
		nextAuto = Time.time + Mathf.Max(0.1f, GcFields.Get("interval", 0.5f));
	}

	private static void Expire()
	{
		Expired.Clear();
		foreach (KeyValuePair<long, float> entry in Failed)
		{
			if (Time.time > entry.Value)
			{
				Expired.Add(entry.Key);
			}
		}
		foreach (long key in Expired)
		{
			Failed.Remove(key);
		}
	}
}

[HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
internal static class FieldGhostPatch
{
	private static void Prefix(Player __instance)
	{
		if (__instance == Player.m_localPlayer)
		{
			Planting.BeginGhost();
		}
	}

	[HarmonyPriority(Priority.First)]
	private static void Postfix(Player __instance)
	{
		if (__instance == Player.m_localPlayer)
		{
			Planting.Capture(__instance);
		}
	}
}

/// <summary>Nach allen anderen Mods: Vorschau zurück auf den Feldplatz (eigene Patch-Klasse für die Reihenfolge).</summary>
[HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
internal static class FieldGhostLastPatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Player __instance, bool flashGuardStone)
	{
		if (__instance == Player.m_localPlayer)
		{
			Planting.EndGhost(__instance, flashGuardStone); // true nur aus TryPlacePiece: gleich wird gepflanzt
		}
	}
}

/// <summary>Strahl der Vorschau: im Feld auf den nächsten freien Platz legen.</summary>
[HarmonyPatch(typeof(Player), "PieceRayTest")]
internal static class FieldRayPatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Player __instance, ref bool __result, ref Vector3 point, ref Vector3 normal, ref Piece piece, ref Heightmap heightmap, ref Collider waterSurface)
	{
		if (!Planting.Redirect(__instance, __result, point, out Vector3 slot))
		{
			return;
		}
		point = slot;
		normal = Vector3.up;
		piece = null!;
		heightmap = Heightmap.FindHeightmap(slot);
		waterSurface = null!;
		__result = true;
	}
}

[HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
internal static class FieldPlacedPatch
{
	private static void Postfix(Player __instance, Piece piece, Vector3 pos)
	{
		if (__instance == Player.m_localPlayer)
		{
			Planting.Placed(piece, pos);
		}
	}
}
