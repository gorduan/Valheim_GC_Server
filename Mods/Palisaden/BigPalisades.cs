using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Palisaden;

/// <summary>
/// Große Palisade und großes Tor aus Kernholz (Gorduan, 02.11.2026): doppelt so hoch und doppelt so breit wie Valheims
/// Palisadenwall (stake_wall) und Holztor (wood_gate), dreimal so teuer, viermal so haltbar; Valheims Modelle, nur dunkler
/// (Gorduan: „wie Original aber dunkler“). Beide kosten Kernholz, auch das Tor (Valheims Holztor kostet Holz).
///
/// Vorlagen in den Spieldaten 1.0.20: Palisadenwall 4 Kernholz, 1000 Haltbarkeit, 2 m breit, gut 3,4 m hoch; Holztor 12 Holz,
/// 400 Haltbarkeit, ein Flügel knapp 1,7 m breit und 3 m hoch. Neu: Palisade 12 Kernholz, 4000 Haltbarkeit, 4 m breit, knapp
/// 7 m hoch; Tor 36 Kernholz, 1600 Haltbarkeit, ein Flügel knapp 3,4 × 6 m. Wie bei Valheim ist das Tor schwächer als die
/// Wand. Gebaut mit dem Hammer an der Werkbank, in der Kategorie der Vorlage. Server-Regel Enabled (Standard an): aus = nicht
/// mehr baubar, Gebautes bleibt.
///
/// Halbe Palisade und Kernholztor (Gorduan, 05.11.2026: „Palisaden, welche nur halb so hoch und halb so breit wie die neuen sind und
/// nur die Hälfte kosten, sie müssen sich aber optisch anpassen … damit wir auch die normalen Tore an diese anpassen können und
/// darüber unsere halben Palisaden setzen“; „gleiche Farben“): die halbe Palisade in Valheims Größe, 6 Kernholz, 2000
/// Haltbarkeit; das Kernholztor so breit wie Valheims Holztor und so hoch wie die halbe Palisade (Tor und halbe Palisade
/// darüber sind so hoch wie die große Wand), 18 Kernholz, 800 Haltbarkeit. Alle gleich dunkel.
/// Andockpunkte (Gorduan: „generell brauchen unsere großen Palisaden mehr Andockpunkte“): Valheims Vorlagen haben nur Ecken. Jedes
/// Teil bekommt an jeder Kante Anfang, Mitte und Ende (AddSnapPoints), so schließen zwei halbe Teile genau an ein großes an.
/// </summary>
internal static class BigPalisades
{
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ManualLogSource Log = null!;

	/// <summary>Gorduan: doppelt so hoch und doppelt so breit (die Tiefe wächst mit, die Pfähle werden doppelt so dick).</summary>
	private const float Large = 2f;

	/// <summary>Gorduan: „dunkler“, Farbe des Holzes mal diesem Faktor.</summary>
	private const float Darken = 0.6f;

	private sealed class Spec
	{
		public string Prefab = "";
		public string From = "";
		public string Name = "";
		public int Cost;
		public float Health;
		public float Scale = Large;

		/// <summary>Nur die Höhe so strecken, dass das Teil so hoch ist wie Valheims Palisadenwall (das Kernholztor).</summary>
		public bool WallHeight;
	}

	private static readonly Spec[] Specs =
	{
		new() { Prefab = "GC_StakeWall_Large", From = "stake_wall", Name = "gcpa_wall", Cost = 12, Health = 4000f },
		new() { Prefab = "GC_Gate_Large", From = "wood_gate", Name = "gcpa_gate", Cost = 36, Health = 1600f },
		new() { Prefab = "GC_StakeWall_Half", From = "stake_wall", Name = "gcpa_wall_half", Cost = 6, Health = 2000f, Scale = 1f },
		new() { Prefab = "GC_Gate_Dark", From = "wood_gate", Name = "gcpa_gate_dark", Cost = 18, Health = 800f, Scale = 1f, WallHeight = true },
	};

	private static readonly List<Piece> Pieces = new();

	internal static void Init(ConfigFile config, ManualLogSource log)
	{
		Log = log;
		Enabled = Gc.BindSynced(config, "1 - Palisades", "Enabled", true, "Gorduan, 02.11./05.11.2026: große Palisade (12 Kernholz, 4000 Haltbarkeit), großes Tor (36 Kernholz, 1600), halbe Palisade (6 Kernholz, 2000) und Kernholztor (18 Kernholz, 800) lassen sich bauen. Aus: nicht mehr baubar, Gebautes bleibt.");
		Enabled.SettingChanged += (_, _) => ApplyEnabled();
		PrefabManager.OnVanillaPrefabsAvailable += Create;
	}

	private static void Create()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= Create;
		Dictionary<Material, Material> dark = new();
		foreach (Spec spec in Specs)
		{
			try
			{
				Make(spec, dark);
			}
			catch (Exception e)
			{
				Log.LogError($"{spec.Prefab} nicht angelegt: {e}");
			}
		}
		ApplyEnabled();
		Log.LogInfo($"Palisaden: {Pieces.Count} Bauteile angelegt (Werkbank, Kernholz).");
	}

	private static void Make(Spec spec, Dictionary<Material, Material> dark)
	{
		GameObject? prefab = PrefabManager.Instance.CreateClonedPrefab(spec.Prefab, spec.From);
		if (prefab == null)
		{
			Log.LogError($"{spec.From} fehlt: {spec.Prefab} wird nicht angelegt.");
			return;
		}
		int before = SnapPoints(prefab).Count;
		int added = AddSnapPoints(prefab);
		Vector3 scale = Vector3.one * spec.Scale;
		if (spec.WallHeight && PrefabManager.Instance.GetPrefab("stake_wall") is GameObject wall && Height(prefab) > 0.1f)
		{
			scale.y *= Height(wall) / Height(prefab);
		}
		prefab.transform.localScale = Vector3.Scale(prefab.transform.localScale, scale);
		Log.LogInfo($"{spec.Prefab}: {before} Andockpunkte von Valheim, {added} dazu; Maßstab {scale.x:0.##} × {scale.y:0.##}.");
		int plain = 0;
		foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
		{
			Material[] materials = renderer.sharedMaterials;
			for (int i = 0; i < materials.Length; i++)
			{
				Material? material = materials[i];
				if (material == null)
				{
					continue;
				}
				if (!dark.TryGetValue(material, out Material darker))
				{
					darker = new Material(material) { name = material.name + "_gc_dark" };
					if (darker.HasProperty("_Color"))
					{
						Color color = darker.color;
						darker.color = new Color(color.r * Darken, color.g * Darken, color.b * Darken, color.a);
					}
					else
					{
						plain++;
					}
					dark[material] = darker;
				}
				materials[i] = darker;
			}
			renderer.sharedMaterials = materials;
		}
		if (plain > 0)
		{
			Log.LogWarning($"{spec.Prefab}: {plain} Materialien ohne Farbe (_Color) bleiben hell.");
		}
		WearNTear? wear = prefab.GetComponent<WearNTear>();
		if (wear != null)
		{
			wear.m_health = spec.Health;
		}
		Piece piece = prefab.GetComponent<Piece>();
		piece.m_name = GcLoc.Get(spec.Name);
		piece.m_description = GcLoc.Get(spec.Name + "_desc");
		if (!Gc.IsHeadless)
		{
			piece.m_icon = Icon(prefab) ?? piece.m_icon;
		}
		PieceConfig config = new()
		{
			PieceTable = "_HammerPieceTable",
			CraftingStation = "piece_workbench",
			Requirements = new[] { new RequirementConfig("RoundLog", spec.Cost, 0, true) },
		};
		PieceManager.Instance.AddPiece(new CustomPiece(prefab, true, config));
		Pieces.Add(piece);
	}

	private static List<Transform> SnapPoints(GameObject prefab)
	{
		List<Transform> points = new();
		foreach (Transform child in prefab.transform)
		{
			if (child.CompareTag("snappoint"))
			{
				points.Add(child);
			}
		}
		return points;
	}

	/// <summary>
	/// Andockpunkte an jeder Kante: Anfang, Mitte und Ende in Breite und Höhe (ohne die Mitte der Fläche), in der Ebene von Valheims
	/// Punkten. Liegen Valheims Punkte alle unten, gibt das Modell die Höhe vor. Ergebnis: Zahl der neuen Punkte.
	/// </summary>
	private static int AddSnapPoints(GameObject prefab)
	{
		List<Transform> points = SnapPoints(prefab);
		if (points.Count == 0)
		{
			return 0;
		}
		float minX = float.MaxValue;
		float maxX = float.MinValue;
		float minY = float.MaxValue;
		float maxY = float.MinValue;
		float z = 0f;
		foreach (Transform point in points)
		{
			Vector3 p = point.localPosition;
			minX = Mathf.Min(minX, p.x);
			maxX = Mathf.Max(maxX, p.x);
			minY = Mathf.Min(minY, p.y);
			maxY = Mathf.Max(maxY, p.y);
			z += p.z;
		}
		z /= points.Count;
		if (maxY - minY < 0.1f)
		{
			maxY = minY + Height(prefab);
		}
		int added = 0;
		float[] xs = { minX, (minX + maxX) / 2f, maxX };
		float[] ys = { minY, (minY + maxY) / 2f, maxY };
		for (int i = 0; i < 3; i++)
		{
			for (int j = 0; j < 3; j++)
			{
				if (i == 1 && j == 1)
				{
					continue; // Mitte der Fläche
				}
				Vector3 at = new(xs[i], ys[j], z);
				if (points.Exists(point => Vector3.Distance(point.localPosition, at) < 0.05f))
				{
					continue;
				}
				GameObject snap = new("_snappoint") { tag = "snappoint" };
				snap.transform.SetParent(prefab.transform, false);
				snap.transform.localPosition = at;
				snap.SetActive(points[0].gameObject.activeSelf); // wie Valheims Punkte
				added++;
			}
		}
		return added;
	}

	/// <summary>Höhe des Modells (Meshes) im Maß der Wurzel, ohne das Teil in die Welt zu setzen.</summary>
	private static float Height(GameObject prefab)
	{
		float min = float.MaxValue;
		float max = float.MinValue;
		foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(true))
		{
			if (filter.sharedMesh == null)
			{
				continue;
			}
			Bounds bounds = filter.sharedMesh.bounds;
			for (int corner = 0; corner < 8; corner++)
			{
				Vector3 local = new(
					(corner & 1) == 0 ? bounds.min.x : bounds.max.x,
					(corner & 2) == 0 ? bounds.min.y : bounds.max.y,
					(corner & 4) == 0 ? bounds.min.z : bounds.max.z);
				float y = prefab.transform.InverseTransformPoint(filter.transform.TransformPoint(local)).y;
				min = Mathf.Min(min, y);
				max = Mathf.Max(max, y);
			}
		}
		return max > min ? max - min : 0f;
	}

	private static Sprite? Icon(GameObject prefab)
	{
		try
		{
			return RenderManager.Instance.Render(new RenderManager.RenderRequest(prefab) { Width = 128, Height = 128, Rotation = Quaternion.Euler(6f, 205f, 0f) });
		}
		catch (Exception e)
		{
			Log.LogWarning($"Symbol für {prefab.name} nicht erzeugt: {e.Message}");
			return null;
		}
	}

	/// <summary>Bauen nur, solange die Server-Regel an ist; Gebautes bleibt.</summary>
	private static void ApplyEnabled()
	{
		foreach (Piece piece in Pieces)
		{
			if (piece != null)
			{
				piece.m_enabled = Enabled.Value;
			}
		}
	}
}
