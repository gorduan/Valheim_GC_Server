using System.Collections.Generic;
using GC.Shared;
using UnityEngine;
using UnityEngine.Rendering;

namespace GC.Client.Fields;

/// <summary>
/// Ein Feld: Eckpunkte (Weltkoordinaten, Reihenfolge wie gezeichnet), Ersteller und Gilde. Die Reihen laufen parallel zur
/// ersten Kante (Punkt 0 nach Punkt 1), nach innen fortlaufend (Gorduan, 16.10.2026).
/// </summary>
internal sealed class Field
{
	public readonly List<Vector3> Points = new();
	public long Creator;
	public string CreatorName = "";
	public int Guild = -1;

	/// <summary>Richtung der ersten Kante (waagerecht, Länge 1) und die Senkrechte ins Feld hinein.</summary>
	public Vector3 Along;
	public Vector3 Inward;

	public float MinX, MaxX, MinZ, MaxZ;

	public Vector3 Origin => Points[0];

	internal static readonly int PointsHash = GcFields.KeyPoints.GetStableHashCode();
	internal static readonly int CreatorHash = GcFields.KeyCreator.GetStableHashCode();
	internal static readonly int CreatorNameHash = GcFields.KeyCreatorName.GetStableHashCode();
	internal static readonly int GuildHash = GcFields.KeyGuild.GetStableHashCode();

	public static Field? Read(ZDO zdo)
	{
		byte[]? data = zdo.GetByteArray(PointsHash);
		if (data == null || data.Length < 4)
		{
			return null;
		}
		Field field = new()
		{
			Creator = zdo.GetLong(CreatorHash, 0L),
			CreatorName = zdo.GetString(CreatorNameHash, ""),
			Guild = zdo.GetInt(GuildHash, -1),
		};
		try
		{
			ZPackage package = new(data);
			int count = package.ReadInt();
			for (int i = 0; i < count && i < 4096; i++)
			{
				field.Points.Add(package.ReadVector3());
			}
		}
		catch
		{
			return null;
		}
		return field.Points.Count >= 3 ? field.Prepare() : null;
	}

	public static void Write(ZDO zdo, List<Vector3> points, long creator, string creatorName, int guild)
	{
		ZPackage package = new();
		package.Write(points.Count);
		foreach (Vector3 point in points)
		{
			package.Write(point);
		}
		zdo.Set(PointsHash, package.GetArray());
		zdo.Set(CreatorHash, creator);
		zdo.Set(CreatorNameHash, creatorName);
		zdo.Set(GuildHash, guild);
	}

	/// <summary>Kantenrichtungen und Begrenzung berechnen; null bei einer entarteten ersten Kante.</summary>
	public Field? Prepare()
	{
		Vector3 edge = Points[1] - Points[0];
		edge.y = 0f;
		if (edge.sqrMagnitude < 0.0001f)
		{
			return null;
		}
		Along = edge.normalized;
		Inward = new Vector3(-Along.z, 0f, Along.x); // links der ersten Kante
		if (Geometry.SignedArea(Points) < 0f)
		{
			Inward = -Inward; // im Uhrzeigersinn gezeichnet: innen liegt rechts (auch bei verwinkelten Feldern richtig)
		}
		MinX = MinZ = float.MaxValue;
		MaxX = MaxZ = float.MinValue;
		foreach (Vector3 point in Points)
		{
			MinX = Mathf.Min(MinX, point.x);
			MaxX = Mathf.Max(MaxX, point.x);
			MinZ = Mathf.Min(MinZ, point.z);
			MaxZ = Mathf.Max(MaxZ, point.z);
		}
		return this;
	}

	public bool Contains(Vector3 point) => Geometry.Contains(Points, point.x, point.z);

	/// <summary>Waagerechter Abstand eines Punktes zum Rand.</summary>
	public float EdgeDistance(Vector3 point) => Geometry.EdgeDistance(Points, point);

	public float Area => Mathf.Abs(Geometry.SignedArea(Points));

	public static Vector3 Centroid(List<Vector3> points)
	{
		Vector3 sum = Vector3.zero;
		foreach (Vector3 point in points)
		{
			sum += point;
		}
		return sum / Mathf.Max(1, points.Count);
	}
}

/// <summary>Flächen-Rechnungen in der waagerechten Ebene (x, z).</summary>
internal static class Geometry
{
	public static bool Contains(List<Vector3> polygon, float x, float z)
	{
		bool inside = false;
		for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
		{
			Vector3 a = polygon[i];
			Vector3 b = polygon[j];
			if ((a.z > z) != (b.z > z) && x < (b.x - a.x) * (z - a.z) / (b.z - a.z) + a.x)
			{
				inside = !inside;
			}
		}
		return inside;
	}

	public static float SignedArea(List<Vector3> polygon)
	{
		float sum = 0f;
		for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
		{
			sum += polygon[j].x * polygon[i].z - polygon[i].x * polygon[j].z;
		}
		return sum * 0.5f;
	}

	public static float EdgeDistance(List<Vector3> polygon, Vector3 point)
	{
		float best = float.MaxValue;
		Vector2 p = new(point.x, point.z);
		for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
		{
			Vector2 a = new(polygon[j].x, polygon[j].z);
			Vector2 b = new(polygon[i].x, polygon[i].z);
			Vector2 ab = b - a;
			float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
			best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
		}
		return best;
	}

	/// <summary>Schneiden sich die Strecken ab und cd echt (Berührung an den Enden zählt nicht)?</summary>
	public static bool Crosses(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
	{
		float d1 = Cross(c, d, a);
		float d2 = Cross(c, d, b);
		float d3 = Cross(a, b, c);
		float d4 = Cross(a, b, d);
		return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
	}

	private static float Cross(Vector3 a, Vector3 b, Vector3 p) => (b.x - a.x) * (p.z - a.z) - (b.z - a.z) * (p.x - a.x);

	/// <summary>Überschneiden sich zwei Flächen (Kanten kreuzen sich oder eine liegt in der anderen)?</summary>
	public static bool Overlaps(List<Vector3> a, List<Vector3> b)
	{
		for (int i = 0, j = a.Count - 1; i < a.Count; j = i++)
		{
			for (int k = 0, l = b.Count - 1; k < b.Count; l = k++)
			{
				if (Crosses(a[j], a[i], b[l], b[k]))
				{
					return true;
				}
			}
		}
		return Contains(b, a[0].x, a[0].z) || Contains(a, b[0].x, b[0].z);
	}

	/// <summary>Dreiecke einer einfachen Fläche (Ohrenschneiden), als Indizes in die Punktliste.</summary>
	public static List<int> Triangulate(List<Vector3> polygon)
	{
		List<int> result = new();
		List<int> rest = new();
		bool counterClockwise = SignedArea(polygon) > 0f;
		for (int i = 0; i < polygon.Count; i++)
		{
			rest.Add(counterClockwise ? i : polygon.Count - 1 - i);
		}
		int guard = 0;
		while (rest.Count > 3 && guard++ < 10000)
		{
			bool cut = false;
			for (int i = 0; i < rest.Count; i++)
			{
				int prev = rest[(i + rest.Count - 1) % rest.Count];
				int cur = rest[i];
				int next = rest[(i + 1) % rest.Count];
				Vector3 a = polygon[prev], b = polygon[cur], c = polygon[next];
				if (Cross(a, b, c) <= 0f)
				{
					continue; // spitze Ecke nach innen: kein Ohr
				}
				bool empty = true;
				foreach (int other in rest)
				{
					if (other != prev && other != cur && other != next && InTriangle(polygon[other], a, b, c))
					{
						empty = false;
						break;
					}
				}
				if (!empty)
				{
					continue;
				}
				result.Add(prev);
				result.Add(cur);
				result.Add(next);
				rest.RemoveAt(i);
				cut = true;
				break;
			}
			if (!cut)
			{
				break; // fast gerade Kanten: Rest als Fächer
			}
		}
		for (int i = 1; i + 1 < rest.Count; i++)
		{
			result.Add(rest[0]);
			result.Add(rest[i]);
			result.Add(rest[i + 1]);
		}
		return result;
	}

	private static bool InTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
	{
		return Cross(a, b, p) >= 0f && Cross(b, c, p) >= 0f && Cross(c, a, p) >= 0f;
	}

	/// <summary>Bodenhöhe ohne Bauteile (Gelände), sonst die angegebene Höhe.</summary>
	public static float Ground(Vector3 point)
	{
		if (Heightmap.GetHeight(point, out float height))
		{
			return height;
		}
		return ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(point, out height) ? height : point.y;
	}
}

/// <summary>
/// Hängt GC_Client an jedes Feld-Objekt von GC_Server: liest die Eckpunkte aus dem ZDO (neu, wenn sie sich ändern) und
/// zeigt Umriss und gelb-durchsichtige Fläche, solange der Spieler den Kultivator in der Hand hat.
/// </summary>
public class FieldView : MonoBehaviour
{
	internal static readonly List<FieldView> All = new();

	private const float Lift = 0.12f; // gepflügter Boden ist uneben: sonst ragt er durch die Fläche
	private const int MaxVertices = 6000;

	/// <summary>Höchstens ein Feld je Bild liest Bodenhöhen (sonst ruckelt es, wenn der Kultivator in die Hand kommt).</summary>
	private static int heightFrame = -1;

	private ZNetView? nview;
	private Field? cached; // nicht „field“: das ist in Eigenschaften seit C# 14 ein Schlüsselwort (Sicherungsfeld)
	private uint revision = uint.MaxValue;
	private GameObject? visual;
	private LineRenderer? outline;
	private Mesh? mesh;
	private bool shown;
	private bool topologyDirty = true;
	private bool heightsDone;
	private float nextHeights;
	private Vector3[] flatOutline = new Vector3[0];
	private Vector3[] vertices = new Vector3[0];
	private int[] triangles = new int[0];

	internal ZNetView? View => nview;

	private void Awake()
	{
		nview = GetComponent<ZNetView>();
		All.Add(this);
	}

	private void OnDestroy()
	{
		All.Remove(this);
		if (visual != null)
		{
			Destroy(visual);
		}
		if (mesh != null)
		{
			Destroy(mesh);
		}
	}

	/// <summary>Die Felddaten (neu gelesen, wenn das ZDO sich geändert hat); null, solange keine gültigen Punkte da sind.</summary>
	internal Field? Data
	{
		get
		{
			if (nview == null || !nview.IsValid())
			{
				return null;
			}
			ZDO zdo = nview.GetZDO();
			if (zdo.DataRevision != revision)
			{
				revision = zdo.DataRevision;
				cached = Field.Read(zdo);
				topologyDirty = true;
				heightsDone = false;
			}
			return cached;
		}
	}

	internal void Show(bool show)
	{
		if (show != shown)
		{
			shown = show;
			if (visual != null)
			{
				visual.SetActive(show && cached != null);
			}
		}
		if (!show)
		{
			return;
		}
		Field? data = Data;
		if (data == null)
		{
			if (visual != null)
			{
				visual.SetActive(false);
			}
			return;
		}
		if (topologyDirty)
		{
			BuildTopology(data);
		}
		RefreshHeights(data);
	}

	/// <summary>Umriss (Kanten in Stücken bis 1 m) und Dreiecksnetz der Fläche, nur wenn sich das Feld ändert.</summary>
	private void BuildTopology(Field data)
	{
		topologyDirty = false;
		heightsDone = false;
		List<Vector3> line = new();
		for (int i = 0; i < data.Points.Count; i++)
		{
			Vector3 a = data.Points[i];
			Vector3 b = data.Points[(i + 1) % data.Points.Count];
			int steps = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(a, b)), 1, 200);
			for (int s = 0; s < steps; s++)
			{
				line.Add(Vector3.Lerp(a, b, s / (float)steps));
			}
		}
		flatOutline = line.ToArray();
		List<int> ears = Geometry.Triangulate(data.Points);
		// Zellgröße so, dass auch große Felder höchstens MaxVertices Ecken haben (je Dreieck (n + 1)(n + 2)/2)
		float cell = 1f;
		for (int attempt = 0; attempt < 12; attempt++)
		{
			int total = 0;
			for (int t = 0; t + 2 < ears.Count; t += 3)
			{
				int n = Steps(data.Points[ears[t]], data.Points[ears[t + 1]], data.Points[ears[t + 2]], cell);
				total += (n + 1) * (n + 2) / 2;
			}
			if (total <= MaxVertices)
			{
				break;
			}
			cell *= Mathf.Sqrt(total / (float)MaxVertices) * 1.05f;
		}
		List<Vector3> points = new();
		List<int> tris = new();
		for (int t = 0; t + 2 < ears.Count; t += 3)
		{
			Subdivide(data.Points[ears[t]], data.Points[ears[t + 1]], data.Points[ears[t + 2]], cell, points, tris);
		}
		vertices = points.ToArray();
		triangles = tris.ToArray();
		if (visual == null)
		{
			visual = new GameObject("GC_FieldVisual");
			outline = visual.AddComponent<LineRenderer>();
			outline.useWorldSpace = true;
			outline.loop = true;
			outline.widthMultiplier = 0.05f;
			outline.shadowCastingMode = ShadowCastingMode.Off;
			outline.receiveShadows = false;
			outline.material = Materials.Line;
			GameObject fill = new("fill");
			fill.transform.SetParent(visual.transform, false);
			mesh = new Mesh { name = "GC_FieldFill", indexFormat = IndexFormat.UInt32 };
			fill.AddComponent<MeshFilter>().sharedMesh = mesh;
			MeshRenderer renderer = fill.AddComponent<MeshRenderer>();
			renderer.sharedMaterial = Materials.Fill;
			renderer.shadowCastingMode = ShadowCastingMode.Off;
			renderer.receiveShadows = false;
		}
		visual.SetActive(false); // sichtbar, sobald die Bodenhöhen da sind
	}

	/// <summary>
	/// Bodenhöhen für Umriss und Fläche: einmal beim Zeigen, danach alle 3 s für Felder in der Nähe (Umgraben, Ebnen).
	/// Höchstens ein Feld je Bild.
	/// </summary>
	private void RefreshHeights(Field data)
	{
		if (heightsDone && Time.time < nextHeights)
		{
			return;
		}
		if (heightFrame == Time.frameCount)
		{
			return;
		}
		if (heightsDone && !Near(data))
		{
			return; // entfernte Felder behalten ihre Höhen
		}
		heightFrame = Time.frameCount;
		heightsDone = true;
		nextHeights = Time.time + 3f;
		Vector3[] line = new Vector3[flatOutline.Length];
		for (int i = 0; i < line.Length; i++)
		{
			Vector3 p = flatOutline[i];
			p.y = Geometry.Ground(p) + Lift + 0.02f;
			line[i] = p;
		}
		outline!.positionCount = line.Length;
		outline.SetPositions(line);
		for (int i = 0; i < vertices.Length; i++)
		{
			vertices[i].y = Geometry.Ground(vertices[i]) + Lift;
		}
		mesh!.Clear();
		mesh.vertices = vertices;
		mesh.triangles = triangles;
		mesh.RecalculateBounds();
		visual!.SetActive(shown);
	}

	private static bool Near(Field data)
	{
		Player? player = Player.m_localPlayer;
		if (player == null)
		{
			return false;
		}
		Vector3 p = player.transform.position;
		const float range = 64f;
		return p.x >= data.MinX - range && p.x <= data.MaxX + range && p.z >= data.MinZ - range && p.z <= data.MaxZ + range;
	}

	private static int Steps(Vector3 a, Vector3 b, Vector3 c, float cell)
	{
		float longest = Mathf.Max(Vector3.Distance(a, b), Mathf.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
		return Mathf.Clamp(Mathf.CeilToInt(longest / cell), 1, 64);
	}

	private static void Subdivide(Vector3 a, Vector3 b, Vector3 c, float cell, List<Vector3> vertices, List<int> triangles)
	{
		int n = Steps(a, b, c, cell);
		int start = vertices.Count;
		// Gitter im Dreieck: Zeile i hat n - i + 1 Punkte
		for (int i = 0; i <= n; i++)
		{
			for (int j = 0; j <= n - i; j++)
			{
				vertices.Add(a + (b - a) * (i / (float)n) + (c - a) * (j / (float)n));
			}
		}
		int Index(int i, int j) => start + i * (n + 1) - i * (i - 1) / 2 + j;
		for (int i = 0; i < n; i++)
		{
			for (int j = 0; j < n - i; j++)
			{
				triangles.Add(Index(i, j));
				triangles.Add(Index(i + 1, j));
				triangles.Add(Index(i, j + 1));
				if (j < n - i - 1)
				{
					triangles.Add(Index(i + 1, j));
					triangles.Add(Index(i + 1, j + 1));
					triangles.Add(Index(i, j + 1));
				}
			}
		}
	}
}

/// <summary>
/// Durchsichtige Farben für Felder (Sprites/Default: ohne Licht, beidseitig, schreibt keine Tiefe). Gorduan (16.10.2026):
/// typisches Valheim-Gelb, kaum Leuchten, durchsichtiger; Farbe und Deckkraft in den Optionen. Weil der Shader kein Licht
/// kennt, folgt die Helligkeit dem Umgebungslicht wie beim Leinenseil, sonst strahlt die Fläche nachts.
/// </summary>
internal static class Materials
{
	private static Color Base => Plugin.Tint.Value switch
	{
		FieldColor.Orange => new Color(1f, 0.55f, 0.15f),
		FieldColor.Red => new Color(0.95f, 0.25f, 0.2f),
		FieldColor.Green => new Color(0.35f, 0.85f, 0.3f),
		FieldColor.Turquoise => new Color(0.25f, 0.85f, 0.8f),
		FieldColor.Blue => new Color(0.3f, 0.55f, 1f),
		FieldColor.Purple => new Color(0.7f, 0.4f, 1f),
		FieldColor.White => new Color(0.95f, 0.95f, 0.95f),
		_ => new Color(1f, 0.82f, 0.25f), // Valheim-Gelb
	};

	private static Color FillColor => WithAlpha(Base, Plugin.FillOpacity.Value / 100f);

	private static Color LineColor => WithAlpha(Color.Lerp(Base, Color.white, 0.08f), Plugin.LineOpacity.Value / 100f);

	/// <summary>Zeichenlinie: dieselbe Farbe, etwas heller und kräftiger, damit man sie beim Markieren sieht.</summary>
	private static Color DraftColor => WithAlpha(Color.Lerp(Base, Color.white, 0.25f), 0.8f);

	private static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, Mathf.Clamp01(alpha));

	private static Material? fill;
	private static Material? line;
	private static Material? draft;
	private static float light = -1f;

	public static Material Fill => fill ??= Make(FillColor, 3100);

	public static Material Line => line ??= Make(LineColor, 3110);

	/// <summary>Feld, das gerade gezeichnet wird.</summary>
	public static Material Draft => draft ??= Make(DraftColor, 3110);

	private static Material Make(Color color, int queue)
	{
		Shader shader = Shader.Find("Sprites/Default");
		Material material = new(shader) { color = Dim(color, light < 0f ? 1f : light), renderQueue = queue };
		return material;
	}

	/// <summary>Farbe oder Deckkraft in den Optionen geändert: beim nächsten Bild neu setzen.</summary>
	public static void Changed() => light = -1f;

	/// <summary>Helligkeit nach Umgebungslicht (nachts gedämpft), nur bei spürbarer Änderung neu setzen.</summary>
	public static void Tick()
	{
		float now = Mathf.Clamp(RenderSettings.ambientLight.maxColorComponent * 1.6f, 0.2f, 1f);
		if (light >= 0f && Mathf.Abs(now - light) < 0.02f)
		{
			return;
		}
		light = now;
		if (fill != null)
		{
			fill.color = Dim(FillColor, light);
		}
		if (line != null)
		{
			line.color = Dim(LineColor, light);
		}
		if (draft != null)
		{
			draft.color = Dim(DraftColor, light);
		}
	}

	private static Color Dim(Color color, float factor) => new(color.r * factor, color.g * factor, color.b * factor, color.a);
}
