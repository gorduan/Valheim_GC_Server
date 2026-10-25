using System;
using System.Collections.Generic;
using System.IO;
using GC.Shared;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.Rendering;

namespace GC.Server.Leash;

// Gcm (Blender-Modelle) liegt seit dem Kern in GC.Shared.Gcm (GC_Kern).

/// <summary>
/// Seilglied und Seilrolle aus Blender (E:\Blender\Leine\build_leine.py), Low-Poly im Valheim-Stil (Gorduan, 18.10.2026: die Leine
/// als Seil statt leuchtender Striche; die erste, detaillierte Fassung war „viel zu detailliert für Valheim“). Das Glied ist
/// ein sechseckiges Rohr, 6 cm lang und dabei um 60° gedreht, und setzt sich nahtlos fort; RopeMesh reiht es entlang der
/// Leine auf. Die Rolle (Seil und Metall als zwei Untermeshes) liegt am Boden, hängt in der Hand und gibt das Symbol.
/// </summary>
internal static class RopeModel
{
	/// <summary>Griff der Rolle (oben an der Windung, Blender core(90°)): dort hält die Hand sie. Unity-Achsen.</summary>
	internal static readonly Vector3 CoilGrip = new(0f, 0.043f, 0f);

	private static bool loaded;
	private static Gcm? tile;
	private static Mesh? coil;

	/// <summary>Länge eines Glieds (größtes z).</summary>
	internal static float TileLength { get; private set; }

	/// <summary>Je Ecke des Glieds ihr Ring; alle Ecken eines Rings liegen in einer Ebene quer zur Achse (gleiches z).</summary>
	internal static int[] RingOf { get; private set; } = Array.Empty<int>();

	/// <summary>z der Ringe, aufsteigend.</summary>
	internal static float[] RingZ { get; private set; } = Array.Empty<float>();

	internal static Gcm? Tile
	{
		get
		{
			Load();
			return tile;
		}
	}

	internal static Mesh? Coil
	{
		get
		{
			Load();
			return coil;
		}
	}

	private static void Load()
	{
		if (loaded)
		{
			return;
		}
		loaded = true;
		tile = Gcm.Read("seilglied.gcm");
		if (tile != null && !Rings(tile))
		{
			tile = null;
		}
		Gcm? rope = Gcm.Read("seilrolle.gcm");
		Gcm? metal = Gcm.Read("seilrolle_metall.gcm");
		if (rope != null && metal != null)
		{
			coil = Gcm.Combine("GC_Seilrolle", rope, metal);
		}
		Plugin.Log.LogInfo($"Tierleinen: Seilglied {(tile != null ? $"geladen ({tile.Indices.Length / 3} Dreiecke, {TileLength * 100f:0.#} cm)" : "fehlt")}, Seilrolle {(coil != null ? "geladen" : "fehlt")}.");
	}

	private static bool Rings(Gcm g)
	{
		List<float> rings = new();
		foreach (Vector3 p in g.Positions)
		{
			if (!rings.Exists(z => Mathf.Abs(z - p.z) < 1e-4f))
			{
				rings.Add(p.z);
			}
		}
		rings.Sort();
		int[] of = new int[g.Positions.Length];
		for (int v = 0; v < of.Length; v++)
		{
			of[v] = rings.FindIndex(z => Mathf.Abs(z - g.Positions[v].z) < 1e-4f);
		}
		RingZ = rings.ToArray();
		RingOf = of;
		TileLength = rings.Count > 0 ? rings[rings.Count - 1] : 0f;
		if (rings.Count < 2 || TileLength < 0.005f)
		{
			Plugin.Log.LogWarning("Tierleinen: Seilglied ohne Ringe, das Seil bleibt eine Linie.");
			return false;
		}
		return true;
	}
}

/// <summary>
/// Materialien im Valheim-Stil: das Seil trägt Valheims Karren-Seil (Material des Prefabs „Cart“, Textur rope_d 32×32 ohne
/// Glättung), je Leinenstufe eingefärbt; Hülsen und Karabiner das Material des Eisenbarrens. Fehlt eins: Unitys Standard-Shader.
/// </summary>
internal static class RopeLook
{
	/// <summary>Valheims Seil-Textur ist graubeige (etwa 60 % hell): so stark aufhellen, dass die Leinenfarbe trägt.</summary>
	private const float Brighten = 1.6f;

	private static readonly Dictionary<int, Material> Ropes = new();
	private static Material? ropeBase;
	private static Material? metal;
	private static bool searched;

	private static void Search()
	{
		if (searched)
		{
			return;
		}
		searched = true;
		ropeBase = FindMaterial("Cart", "rope");
		metal = FindMaterial("Iron", null);
		Plugin.Log.LogInfo($"Tierleinen: Seil-Material {Describe(ropeBase)}, Metall {Describe(metal)}.");
	}

	private static string Describe(Material? material) => material != null ? $"{material.name} ({material.shader.name})" : "nicht gefunden (Standard-Shader)";

	private static Material? FindMaterial(string prefab, string? contains)
	{
		GameObject? go = PrefabManager.Instance.GetPrefab(prefab);
		if (go == null)
		{
			return null;
		}
		foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(true))
		{
			foreach (Material material in renderer.sharedMaterials)
			{
				if (material != null && (contains == null || material.name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0))
				{
					return material;
				}
			}
		}
		return null;
	}

	/// <summary>Seil-Material je Leinenstufe (Kopie von Valheims Karren-Seil, eingefärbt). null auf dem Server.</summary>
	internal static Material? Rope(Tier? tier)
	{
		if (Gc.IsHeadless)
		{
			return null;
		}
		Search();
		int key = tier?.Level ?? 0;
		if (Ropes.TryGetValue(key, out Material existing) && existing != null)
		{
			return existing;
		}
		Material material;
		if (ropeBase != null)
		{
			material = new Material(ropeBase);
		}
		else
		{
			Shader? shader = Shader.Find("Standard");
			if (shader == null)
			{
				return null;
			}
			material = new Material(shader) { mainTexture = FallbackTexture() };
		}
		material.name = "GC_Leash_Rope_" + key;
		if (material.HasProperty("_BumpMap"))
		{
			material.SetTexture("_BumpMap", null); // das bewegte Seil hat keine Tangenten: ohne Normal-Map gleiches Licht wie die Rolle
		}
		Color color = tier?.Color ?? new Color(0.55f, 0.36f, 0.22f);
		if (material.HasProperty("_Color"))
		{
			material.SetColor("_Color", new Color(Mathf.Min(1.6f, color.r * Brighten), Mathf.Min(1.6f, color.g * Brighten), Mathf.Min(1.6f, color.b * Brighten), 1f));
		}
		Ropes[key] = material;
		return material;
	}

	/// <summary>Metall für Hülsen und Karabiner: das Material des Eisenbarrens. null auf dem Server.</summary>
	internal static Material? Metal()
	{
		if (Gc.IsHeadless)
		{
			return null;
		}
		Search();
		if (metal == null)
		{
			Shader? shader = Shader.Find("Standard");
			if (shader == null)
			{
				return null;
			}
			metal = new Material(shader) { name = "GC_Leash_Metal", color = new Color(0.62f, 0.62f, 0.66f) };
		}
		return metal;
	}

	/// <summary>Ersatz ohne Valheims Seil: 32×32 Pixel, Fasern längs, ohne Glättung.</summary>
	private static Texture2D FallbackTexture()
	{
		const int size = 32;
		Texture2D texture = new(size, size, TextureFormat.RGBA32, false) { name = "GC_Leash_Rope_Fallback", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
		System.Random random = new(1301);
		Color[] pixels = new Color[size * size];
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				float fiber = 0.55f + 0.08f * Mathf.Sin((x + y * 0.5f) * 1.3f) + (float)random.NextDouble() * 0.08f;
				pixels[y * size + x] = new Color(fiber, fiber * 0.96f, fiber * 0.9f, 1f);
			}
		}
		texture.SetPixels(pixels);
		texture.Apply();
		return texture;
	}

	/// <summary>
	/// Leine am Boden: die Seilrolle statt des Fells oder Leders, Kollider in Rollengröße (auch auf dem Server, damit er
	/// überall gleich ist). Liefert das Symbol aus der Rolle (Jötunn RenderManager), null ohne Grafik oder Modell.
	/// Vor MakeHandItem aufrufen: Es entfernt alle Renderer des Prefabs.
	/// </summary>
	internal static Sprite? DressLeash(GameObject prefab, Tier tier)
	{
		Mesh? coilMesh = RopeModel.Coil;
		if (coilMesh == null)
		{
			return null;
		}
		Material? rope = Rope(tier);
		Material? iron = Metal();
		if (!Gc.IsHeadless && (rope == null || iron == null))
		{
			return null; // ohne Material bliebe die Leine unsichtbar: dann lieber das Fell
		}
		List<GameObject> visuals = new();
		foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
		{
			if (renderer.gameObject == prefab)
			{
				UnityEngine.Object.DestroyImmediate(renderer);
			}
			else if (!visuals.Contains(renderer.gameObject))
			{
				visuals.Add(renderer.gameObject);
			}
		}
		foreach (GameObject visual in visuals)
		{
			if (visual != null)
			{
				UnityEngine.Object.DestroyImmediate(visual);
			}
		}
		foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(true))
		{
			UnityEngine.Object.DestroyImmediate(filter);
		}
		foreach (LODGroup lod in prefab.GetComponentsInChildren<LODGroup>(true))
		{
			UnityEngine.Object.DestroyImmediate(lod);
		}
		foreach (Collider collider in prefab.GetComponentsInChildren<Collider>(true))
		{
			UnityEngine.Object.DestroyImmediate(collider);
		}
		BoxCollider box = prefab.AddComponent<BoxCollider>();
		box.center = coilMesh.bounds.center;
		box.size = Vector3.Max(coilMesh.bounds.size, Vector3.one * 0.04f);
		if (Gc.IsHeadless || rope == null || iron == null)
		{
			return null;
		}
		// am Item selbst, nicht an einem Kind: Jötunns RenderManager prüft vor dem Symbol nur aktive Objekte
		prefab.AddComponent<MeshFilter>().sharedMesh = coilMesh;
		prefab.AddComponent<MeshRenderer>().sharedMaterials = new[] { rope, iron };
		try
		{
			// Jötunns Kamera schaut entlang −Z: um 180° gedreht zeigt die Rolle ihre Vorderseite (wie im Blender-Bild)
			Sprite? icon = RenderManager.Instance.Render(new RenderManager.RenderRequest(prefab) { Width = 128, Height = 128, Rotation = Quaternion.Euler(8f, 190f, 0f) });
			if (icon == null)
			{
				Plugin.Log.LogWarning($"Tierleinen: Symbol für {prefab.name} nicht gerendert, es bleibt das gezeichnete.");
			}
			return icon;
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"Tierleinen: Symbol für {prefab.name} nicht gerendert: {e.Message}");
			return null;
		}
	}
}

/// <summary>
/// Ein Seil aus aneinandergereihten Seilgliedern entlang eines Pfads (Seil zum Tier, Schlinge am Pfosten). Die Glieder werden
/// auf ganze Stücke gestaucht oder gestreckt, die Rahmen entlang des Pfads parallel verschoben (kein Verdrillen). Ecken
/// relativ zu einem Ursprung, weil Weltkoordinaten groß sein können. Glied-Achsen: x → Normale, y → Binormale (T × N),
/// z → Tangente; das ist eine echte Drehung, so bleibt die Seildrehung dieselbe wie in der Rolle aus Blender.
/// </summary>
internal sealed class RopeMesh
{
	private const int MaxTiles = 1000; // 60 m: auch bei Störungen (Teleport) bleibt die Rechnung klein

	/// <summary>Länger kann keine Leine sein (Höchstlänge 18,5 m + Aufwerten + Dehnung): darüber ist es ein Teleport-Bild, kein Seil.</summary>
	private const float MaxLength = 150f;

	internal readonly Mesh Mesh;
	private Vector3[] vertices = Array.Empty<Vector3>();
	private Vector3[] normals = Array.Empty<Vector3>();
	private Vector2[] uvs = Array.Empty<Vector2>();
	private int[] indices = Array.Empty<int>();
	private float[] lengths = Array.Empty<float>();
	private Vector3[] tangents = Array.Empty<Vector3>();
	private Vector3[] frameNormals = Array.Empty<Vector3>();
	private Vector3[] ringC = Array.Empty<Vector3>();
	private Vector3[] ringN = Array.Empty<Vector3>();
	private Vector3[] ringB = Array.Empty<Vector3>();
	private Vector3[] ringT = Array.Empty<Vector3>();
	private int tiles;

	internal RopeMesh(string name)
	{
		Mesh = new Mesh { name = name };
		Mesh.MarkDynamic();
	}

	/// <summary>Das Mesh freigeben (stirbt nicht mit seinem GameObject, sonst erst bei Resources.UnloadUnusedAssets).</summary>
	internal void Destroy()
	{
		if (Mesh != null)
		{
			UnityEngine.Object.Destroy(Mesh);
		}
	}

	/// <summary>Glieder entlang points[0 … count) (Weltkoordinaten), Ecken relativ zu origin. false: kein Modell oder kein Pfad.</summary>
	internal bool Build(Vector3[] points, int count, Vector3 origin)
	{
		Gcm? tile = RopeModel.Tile;
		if (tile == null || count < 2)
		{
			return false;
		}
		if (lengths.Length < count)
		{
			lengths = new float[count];
			tangents = new Vector3[count];
			frameNormals = new Vector3[count];
		}
		lengths[0] = 0f;
		for (int i = 1; i < count; i++)
		{
			lengths[i] = lengths[i - 1] + (points[i] - points[i - 1]).magnitude;
		}
		float total = lengths[count - 1];
		if (total < 0.001f || total > MaxLength)
		{
			return false;
		}
		for (int i = 0; i < count; i++)
		{
			Vector3 d = points[Mathf.Min(i + 1, count - 1)] - points[Mathf.Max(i - 1, 0)];
			tangents[i] = d.sqrMagnitude > 1e-12f ? d.normalized : i > 0 ? tangents[i - 1] : Vector3.forward;
		}
		Vector3 first = Vector3.Cross(tangents[0], Vector3.up);
		if (first.sqrMagnitude < 1e-6f)
		{
			first = Vector3.Cross(tangents[0], Vector3.forward);
		}
		frameNormals[0] = first.normalized;
		for (int i = 1; i < count; i++)
		{
			Vector3 n = frameNormals[i - 1] - Vector3.Dot(frameNormals[i - 1], tangents[i]) * tangents[i];
			frameNormals[i] = n.sqrMagnitude > 1e-12f ? n.normalized : frameNormals[i - 1];
		}
		float tileLength = RopeModel.TileLength;
		float exact = total / tileLength;
		int tileCount = Mathf.Clamp(Mathf.RoundToInt(exact), 1, MaxTiles);
		if (tiles > 0 && Mathf.Abs(exact - tiles) <= 0.75f)
		{
			tileCount = tiles; // Hysterese: das Drehmuster springt nicht bei jeder kleinen Längenänderung
		}
		float stretch = total / (tileCount * tileLength);
		Topology(tileCount, tile);
		float[] ringZ = RopeModel.RingZ;
		int rings = ringZ.Length;
		if (ringC.Length < tileCount * rings)
		{
			int size = Mathf.NextPowerOfTwo(tileCount * rings);
			ringC = new Vector3[size];
			ringN = new Vector3[size];
			ringB = new Vector3[size];
			ringT = new Vector3[size];
		}
		int segment = 0;
		for (int t = 0; t < tileCount; t++)
		{
			for (int r = 0; r < rings; r++)
			{
				float d = Mathf.Min(total, (t * tileLength + ringZ[r]) * stretch);
				while (segment < count - 2 && lengths[segment + 1] < d)
				{
					segment++;
				}
				float span = lengths[segment + 1] - lengths[segment];
				float f = span > 1e-6f ? Mathf.Clamp01((d - lengths[segment]) / span) : 0f;
				Vector3 tt = Vector3.Lerp(tangents[segment], tangents[segment + 1], f).normalized;
				Vector3 nn = Vector3.Lerp(frameNormals[segment], frameNormals[segment + 1], f);
				nn = (nn - Vector3.Dot(nn, tt) * tt).normalized;
				int k = t * rings + r;
				ringC[k] = Vector3.LerpUnclamped(points[segment], points[segment + 1], f) - origin;
				ringT[k] = tt;
				ringN[k] = nn;
				ringB[k] = Vector3.Cross(tt, nn);
			}
		}
		Vector3[] tilePositions = tile.Positions;
		Vector3[] tileNormals = tile.Normals;
		int[] ringOf = RopeModel.RingOf;
		int per = tilePositions.Length;
		for (int t = 0; t < tileCount; t++)
		{
			int ringBase = t * rings;
			int vertexBase = t * per;
			for (int v = 0; v < per; v++)
			{
				int k = ringBase + ringOf[v];
				Vector3 p = tilePositions[v];
				Vector3 q = tileNormals[v];
				vertices[vertexBase + v] = ringC[k] + p.x * ringN[k] + p.y * ringB[k];
				normals[vertexBase + v] = q.x * ringN[k] + q.y * ringB[k] + q.z * ringT[k];
			}
		}
		Mesh.SetVertices(vertices, 0, tileCount * per);
		Mesh.SetNormals(normals, 0, tileCount * per);
		Mesh.RecalculateBounds();
		return true;
	}

	/// <summary>Anzahl der Glieder geändert: UVs und Dreiecke neu (die Ecken setzt Build gleich danach).</summary>
	private void Topology(int count, Gcm tile)
	{
		if (count == tiles)
		{
			return;
		}
		tiles = count;
		int per = tile.Positions.Length;
		int perIndex = tile.Indices.Length;
		if (vertices.Length < count * per)
		{
			int size = Mathf.NextPowerOfTwo(count);
			vertices = new Vector3[size * per];
			normals = new Vector3[size * per];
			uvs = new Vector2[size * per];
			indices = new int[size * perIndex];
		}
		for (int t = 0; t < count; t++)
		{
			Array.Copy(tile.Uvs, 0, uvs, t * per, per);
			for (int i = 0; i < perIndex; i++)
			{
				indices[t * perIndex + i] = tile.Indices[i] + t * per;
			}
		}
		Mesh.Clear();
		Mesh.indexFormat = count * per > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
		Mesh.SetVertices(vertices, 0, count * per);
		Mesh.SetNormals(normals, 0, count * per);
		Mesh.SetUVs(0, uvs, 0, count * per);
		Mesh.SetTriangles(indices, 0, count * perIndex, 0, false);
	}
}
