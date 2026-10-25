using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Server;
using GC.Shared;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Ladder;

/// <summary>
/// GC_Ladder: baubare, senkrechte Leiter (Hammer → Sonstiges, Werkbank nötig).
///
/// Vanilla hat nur die schräge Stufenleiter (wood_stepladder), die per AutoJumpLedge hochhüpfen lässt, und eine
/// Klasse „Ladder", die den Spieler beim Benutzen an einen Zielpunkt teleportiert. Eine Kletteranimation gibt
/// es im Spiel nicht. Diese Leiter arbeitet deshalb wie die Vanilla-Klasse: E bringt dich ans obere Ende,
/// wenn du unten stehst, und ans untere Ende, wenn du oben stehst. Umschalt+E erzwingt „nach unten".
/// Leitern lassen sich übereinander setzen (Andockpunkte oben und unten) und werden dann als eine lange Leiter
/// behandelt. Oben stellt die Leiter dich hinter die Leiter (auf die Plattform), unten davor. Vorn ist immer dieselbe
/// Seite, egal von wo man klickt (Gorduan, 16.10.2026): unten die Seite mit Platz für einen Spieler, sonst die gegenüber der
/// Plattform (LadderClimb.FrontOf).
/// Das Modell wird zur Laufzeit aus zwei Holzpfosten (Holme) und Sprossen (Modell New aus wood_pole) zusammengesetzt.
/// Der Drehpunkt liegt unten, damit die Leiter auf dem Boden steht.
///
/// Gorduan (13.10.2026): Ein Leiterstück ist 1 m hoch (aus wood_pole), damit man es feiner setzen kann, mit dünnen Holmen
/// (9 cm) und Sprossen (5 cm, alle 33 cm). Die Dicke der Pfosten-Modelle wird zur Laufzeit gemessen und auf diese Maße
/// skaliert. Die alte 2-m-Leiter (GC_Ladder aus wood_pole2) bleibt als Prefab, damit gebaute Leitern stehen bleiben,
/// sieht genauso fein aus, ist aber nicht mehr im Baumenü.
/// </summary>
internal static class Plugin
{
	public const string Guid = "gorduan.gc_ladder";
	public const string ModName = "GC_Ladder";
	public const string Version = "1.0.0";
	/// <summary>Alte 2-m-Leiter: bleibt ladbar (gebaute Leitern), nicht mehr baubar.</summary>
	internal const string PrefabName = "GC_Ladder";
	/// <summary>Leiterstück 1 m (Gorduan: halb so hoch, feiner setzbar).</summary>
	internal const string ShortPrefabName = "GC_Ladder_1m";
	private const float HalfWidth = 0.3f;     // Holm-Mitte zu Leiter-Mitte
	private const float RailThickness = 0.09f;
	private const float RungThickness = 0.05f;
	private const float RungSpacing = 0.33f;
	internal static Piece? OldLadderPiece;

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static bool Blocked => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);
	internal static bool On => Enabled.Value && !Blocked;
	internal static Piece? LadderPiece;
	internal static ConfigEntry<int> WoodCost = null!;
	internal static ConfigEntry<float> ExitDistance = null!;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ServerPlugin.Log;
		Enabled = Gc.BindSynced(Config, "1 - Ladder", "Enabled", true, "Leiter baubar und benutzbar (wird vom Server vorgegeben, auch im Menü GC Option).");
		Enabled.SettingChanged += (_, _) => ApplyEnabled();
		WoodCost = Gc.BindSynced(Config, "1 - Ladder", "WoodCost", 4, "Holzkosten pro 2 m Leiter. Ein Leiterstück ist 1 m hoch und kostet die Hälfte (aufgerundet).", new AcceptableValueRange<int>(1, 50));
		WoodCost.SettingChanged += (_, _) => ApplyCost();
		ExitDistance = Gc.Bind(Config, "1 - Ladder", "ExitDistance", 0.8f, "Abstand von der Leiter beim Absteigen in Metern.", new AcceptableValueRange<float>(0.3f, 2f));
		PrefabManager.OnVanillaPrefabsAvailable += CreateLadder;
	}

	private static void CreateLadder()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= CreateLadder;
		if (Blocked)
		{
			return; // die alte Einzelmod GC_Ladder registriert dasselbe Prefab
		}
		GameObject? rungSource = PrefabManager.Instance.GetPrefab("wood_pole");
		GameObject? ladder = rungSource != null ? Build(ShortPrefabName, "wood_pole", rungSource, 1f) : null;
		if (ladder == null || rungSource == null)
		{
			Log.LogError("wood_pole nicht gefunden, Leiter wird nicht erstellt.");
			return;
		}
		Sprite icon = LadderIcon();
		Piece piece = ladder.GetComponent<Piece>();
		LadderPiece = piece;
		piece.m_enabled = On;
		piece.m_name = GcLoc.Get("gcld_name");
		piece.m_description = GcLoc.Get("gcld_desc");
		piece.m_icon = icon;
		PieceConfig config = new()
		{
			PieceTable = "_HammerPieceTable",
			Category = "Misc",
			CraftingStation = "piece_workbench",
			Requirements = new[] { new RequirementConfig("Wood", PieceCost, 0, true) }
		};
		PieceManager.Instance.AddPiece(new CustomPiece(ladder, true, config));

		// alte 2-m-Leiter: gleich aussehend, nur noch als Prefab (gebaute Leitern laden und lassen sich abreißen)
		GameObject? old = Build(PrefabName, "wood_pole2", rungSource, 2f);
		if (old != null)
		{
			Piece oldPiece = old.GetComponent<Piece>();
			OldLadderPiece = oldPiece;
			oldPiece.m_name = piece.m_name;
			oldPiece.m_description = piece.m_description;
			oldPiece.m_icon = icon;
			ItemDrop? wood = PrefabManager.Instance.GetPrefab("Wood")?.GetComponent<ItemDrop>();
			if (wood != null)
			{
				oldPiece.m_resources = new[] { new Piece.Requirement { m_resItem = wood, m_amount = WoodCost.Value, m_recover = true } };
			}
			PrefabManager.Instance.AddPrefab(new CustomPrefab(old, true));
		}
	}

	/// <summary>Holz je Leiterstück (1 m): die Hälfte der Kosten für 2 m, aufgerundet.</summary>
	internal static int PieceCost => Mathf.Max(1, Mathf.CeilToInt(WoodCost.Value / 2f));

	/// <summary>
	/// Leiter aus einem Holzpfosten bauen: Holme links und rechts (Modell, Abnutzungs-Stufen, Kollision), dünn skaliert,
	/// Sprossen aus dem Modell „New" von wood_pole, waagerecht, alle 33 cm. Drehpunkt unten.
	/// </summary>
	private static GameObject? Build(string name, string poleName, GameObject rungSource, float fallbackHeight)
	{
		GameObject? ladder = PrefabManager.Instance.CreateClonedPrefab(name, poleName);
		if (ladder == null)
		{
			Log.LogError(poleName + " nicht gefunden, Leiter " + name + " wird nicht erstellt.");
			return null;
		}
		LogHierarchy(ladder.transform, poleName);
		Transform root = ladder.transform;
		Bounds pole = SnapBounds(root) ?? new Bounds(new Vector3(0f, fallbackHeight * 0.5f, 0f), new Vector3(0.2f, fallbackHeight, 0.2f));
		float height = pole.size.y;

		// LODGroup entfernen: sie kennt nur die Original-Renderer, Kopien und Sprossen würden sonst anders ausgeblendet
		LODGroup? lod = ladder.GetComponent<LODGroup>();
		if (lod != null)
		{
			UnityEngine.Object.DestroyImmediate(lod);
		}

		// Drehpunkt nach unten legen, damit die Leiter auf dem Boden steht statt halb darin
		List<Transform> children = new();
		foreach (Transform child in root)
		{
			children.Add(child);
		}
		float shift = -pole.min.y;
		foreach (Transform child in children)
		{
			child.localPosition += Vector3.up * shift;
		}

		// Zwei Holme: Die Teile des Pfostens (Modell mit Abnutzungs-Stufen, Schneekappe, Kollision) wandern in einen
		// Halter, der quer zur Leiter dünn skaliert wird. Es bleiben dieselben Objekte, also wechselt WearNTear weiter
		// zwischen neu, abgenutzt und kaputt. Kopie ohne Schnee nach rechts.
		Transform? poleModel = root.Find("New");
		Bounds? railBounds = (poleModel != null ? BoundsIn(poleModel, root) : null) ?? BoundsIn(root, root); // Server: Kollider des Pfostens
		float railThick = railBounds.HasValue ? Mathf.Max(railBounds.Value.size.x, railBounds.Value.size.z) : 0f;
		float railFactor = railThick > 0.02f ? Mathf.Clamp(RailThickness / railThick, 0.05f, 1.5f) : 1f;
		GameObject left = new("rail_L");
		left.transform.SetParent(root, false);
		foreach (Transform child in children)
		{
			if (!child.CompareTag("snappoint"))
			{
				child.SetParent(left.transform, true);
			}
		}
		left.transform.localScale = new Vector3(railFactor, 1f, railFactor);
		left.transform.localPosition = new Vector3(-HalfWidth, 0f, 0f);
		GameObject right = UnityEngine.Object.Instantiate(left, root, false);
		right.name = "rail_R";
		right.transform.localPosition = new Vector3(HalfWidth, 0f, 0f);
		foreach (Transform part in right.GetComponentsInChildren<Transform>(true))
		{
			if (part != right.transform && part.name.Contains("snow"))
			{
				UnityEngine.Object.DestroyImmediate(part.gameObject);
				break;
			}
		}

		// Ein Kollider direkt am Pfosten (falls es einen gibt) säße jetzt mitten zwischen den Holmen: weg damit, die Fläche unten ersetzt ihn
		foreach (Collider collider in ladder.GetComponents<Collider>())
		{
			UnityEngine.Object.DestroyImmediate(collider);
		}

		// Sprossen aus dem 1-m-Pfosten (Modell „New"): Längsachse aus der Begrenzungsbox des Modells bestimmen, quer legen,
		// auf Leiterbreite und 5 cm Dicke bringen und mittig setzen (unabhängig davon, wie das Modell innen gedreht ist)
		Transform? rungModel = rungSource.transform.Find("New");
		int rungs = 0;
		if (rungModel != null)
		{
			rungs = Mathf.Max(2, Mathf.RoundToInt(height / RungSpacing));
			float spacing = height / rungs;
			Bounds? rungBounds = BoundsIn(rungModel, rungSource.transform);
			Quaternion turn = Quaternion.identity;
			Vector3 shapeScale = Vector3.one;
			Vector3 shapePosition = Vector3.zero;
			if (rungBounds.HasValue)
			{
				Bounds b = rungBounds.Value;
				Vector3 size = new(Mathf.Max(0.001f, b.size.x), Mathf.Max(0.001f, b.size.y), Mathf.Max(0.001f, b.size.z));
				int axis = size.y >= size.x && size.y >= size.z ? 1 : size.z >= size.x ? 2 : 0;
				turn = axis == 1 ? Quaternion.Euler(0f, 0f, 90f) : axis == 2 ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
				float length = 2f * HalfWidth + RailThickness;
				shapeScale = new Vector3(
					(axis == 0 ? length : RungThickness) / size.x,
					(axis == 1 ? length : RungThickness) / size.y,
					(axis == 2 ? length : RungThickness) / size.z);
				shapePosition = -(turn * Vector3.Scale(b.center, shapeScale));
			}
			for (int i = 0; i < rungs; i++)
			{
				GameObject rung = new("rung_" + i);
				rung.transform.SetParent(root, false);
				rung.transform.localPosition = new Vector3(0f, spacing * (i + 0.5f), 0f);
				GameObject shape = new("shape");
				shape.transform.SetParent(rung.transform, false);
				shape.transform.localRotation = turn;
				shape.transform.localScale = shapeScale;
				shape.transform.localPosition = shapePosition;
				GameObject model = UnityEngine.Object.Instantiate(rungModel.gameObject, shape.transform, false);
				foreach (Collider collider in model.GetComponentsInChildren<Collider>(true))
				{
					UnityEngine.Object.DestroyImmediate(collider);
				}
			}
		}
		else
		{
			Log.LogWarning("Modell 'New' in wood_pole nicht gefunden, Leiter ohne Sprossen.");
		}

		// Eine dünne Fläche über die ganze Leiter: dünne Holme allein wären mit dem Mauszeiger schwer zu treffen
		BoxCollider face = ladder.AddComponent<BoxCollider>();
		face.center = new Vector3(0f, height * 0.5f, 0f);
		face.size = new Vector3(2f * HalfWidth + RailThickness, height, 0.12f);

		LadderClimb climb = ladder.AddComponent<LadderClimb>();
		climb.m_bottom = 0f;
		climb.m_top = height;
		Log.LogInfo($"Leiter {name} erstellt: Höhe {height:F2} m, {rungs} Sprossen, Holme {railThick:F2} m → ×{railFactor:F2}.");
		return ladder;
	}

	/// <summary>
	/// Begrenzungsbox aller Meshes (beim Spieler) oder Box-Kollider (Server ohne Meshes) unter model, im Raum von space;
	/// null, wenn es keine gibt. Berücksichtigt Drehungen und Größen der Teile.
	/// </summary>
	private static Bounds? BoundsIn(Transform model, Transform space)
	{
		Bounds? result = null;
		Matrix4x4 toSpace = space.worldToLocalMatrix;
		foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
		{
			if (filter.sharedMesh != null)
			{
				Encapsulate(ref result, filter.sharedMesh.bounds, toSpace * filter.transform.localToWorldMatrix);
			}
		}
		if (result == null)
		{
			foreach (BoxCollider box in model.GetComponentsInChildren<BoxCollider>(true))
			{
				Encapsulate(ref result, new Bounds(box.center, box.size), toSpace * box.transform.localToWorldMatrix);
			}
		}
		return result;
	}

	private static void Encapsulate(ref Bounds? result, Bounds local, Matrix4x4 matrix)
	{
		Vector3 min = local.min;
		Vector3 max = local.max;
		for (int i = 0; i < 8; i++)
		{
			Vector3 corner = new((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
			Vector3 point = matrix.MultiplyPoint3x4(corner);
			if (result == null)
			{
				result = new Bounds(point, Vector3.zero);
			}
			else
			{
				Bounds bounds = result.Value;
				bounds.Encapsulate(point);
				result = bounds;
			}
		}
	}

	/// <summary>Schalter im Mod-Menü: abgeschaltet verschwindet die Leiter aus dem Hammer, gebaute Leitern bleiben stehen.</summary>
	internal static void ApplyEnabled()
	{
		if (LadderPiece == null || LadderPiece.m_enabled == On)
		{
			return;
		}
		LadderPiece.m_enabled = On;
		if (Player.m_localPlayer != null)
		{
			Player.m_localPlayer.UpdateAvailablePiecesList();
		}
	}

	/// <summary>Holzkosten nach der Server-Synchronisierung übernehmen (beim Anlegen der Leiter gilt noch die lokale Config).</summary>
	internal static void ApplyCost()
	{
		if (LadderPiece == null || LadderPiece.m_resources == null)
		{
			return;
		}
		if (LadderPiece.m_resources.Length == 1)
		{
			LadderPiece.m_resources[0].m_amount = PieceCost; // nur Holz; der Name kann noch Jötunns Platzhalter sein
		}
		if (OldLadderPiece != null && OldLadderPiece.m_resources != null && OldLadderPiece.m_resources.Length == 1)
		{
			OldLadderPiece.m_resources[0].m_amount = WoodCost.Value; // alte 2-m-Leiter: Rückgabe beim Abreißen
		}
	}

	/// <summary>Höhe aus den Andockpunkten des Pfostens (zuverlässig auch auf dem Server, wo Meshes fehlen können).</summary>
	private static Bounds? SnapBounds(Transform root)
	{
		List<Vector3> points = new();
		foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
		{
			if (child.CompareTag("snappoint"))
			{
				points.Add(root.InverseTransformPoint(child.position));
			}
		}
		if (points.Count < 2)
		{
			return null;
		}
		float min = points.Min(p => p.y);
		float max = points.Max(p => p.y);
		if (max - min < 0.5f)
		{
			return null;
		}
		return new Bounds(new Vector3(0f, (min + max) * 0.5f, 0f), new Vector3(0.2f, max - min, 0.2f));
	}

	private static void LogHierarchy(Transform root, string label)
	{
		foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
		{
			string components = string.Join(",", child.GetComponents<Component>().Select(c => c.GetType().Name));
			Log.LogDebug($"Leiter-Aufbau [{label}] {child.name} tag={child.tag} pos={root.InverseTransformPoint(child.position)} {components}");
		}
	}

	private static Sprite LadderIcon()
	{
		const int size = 64;
		Texture2D tex = new(size, size, TextureFormat.RGBA32, false);
		Color clear = new(0f, 0f, 0f, 0f);
		Color wood = new(0.62f, 0.42f, 0.22f, 1f);
		Color dark = new(0.3f, 0.18f, 0.08f, 1f);
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				bool rail = (x >= 14 && x <= 20) || (x >= 43 && x <= 49);
				bool rung = x > 20 && x < 43 && (y % 12) >= 4 && (y % 12) <= 7;
				Color c = rail || rung ? wood : clear;
				if ((rail && (x == 14 || x == 20 || x == 43 || x == 49)) || (rung && ((y % 12) == 4 || (y % 12) == 7)))
				{
					c = dark;
				}
				tex.SetPixel(x, y, c);
			}
		}
		tex.Apply();
		return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
	}
}

/// <summary>Benutzen bringt den Spieler an das obere bzw. untere Ende einer Leiter oder eines Leiterstapels.</summary>
public class LadderClimb : MonoBehaviour, Interactable, Hoverable
{
	public float m_bottom;
	public float m_top = 2f;

	private static readonly List<LadderClimb> All = new();

	private void OnEnable() => All.Add(this);

	private void OnDisable() => All.Remove(this);

	private Vector3 Bottom => transform.TransformPoint(new Vector3(0f, m_bottom, 0f));

	private Vector3 Top => transform.TransformPoint(new Vector3(0f, m_top, 0f));

	private LadderClimb? Neighbour(bool up)
	{
		Vector3 anchor = up ? Top : Bottom;
		foreach (LadderClimb other in All)
		{
			if (other == this || other == null)
			{
				continue;
			}
			Vector3 point = up ? other.Bottom : other.Top;
			if (Vector3.Distance(anchor, point) < 0.45f)
			{
				return other;
			}
		}
		return null;
	}

	private LadderClimb End(bool up)
	{
		LadderClimb current = this;
		HashSet<LadderClimb> seen = new() { this };
		while (true)
		{
			LadderClimb? next = current.Neighbour(up);
			if (next == null || !seen.Add(next))
			{
				return current;
			}
			current = next;
		}
	}

	/// <summary>
	/// Wohin führt E? Die unterste Stufe der Leiter nach oben, die oberste nach unten (solange man nicht schon am
	/// anderen Ende steht); dazwischen nach der Höhe des Spielers zur Leitermitte. Umschalt+E immer nach unten.
	/// </summary>
	private bool GoesUp(Humanoid user, bool alt, LadderClimb top, LadderClimb bottom)
	{
		if (alt)
		{
			return false;
		}
		float y = user.transform.position.y;
		if (this == top && this != bottom && y > Bottom.y + 0.5f)
		{
			return false; // oberste Stufe: nach unten
		}
		if (this == bottom && this != top && y < Top.y - 0.5f)
		{
			return true; // unterste Stufe: nach oben
		}
		return y < (top.Top.y + bottom.Bottom.y) * 0.5f;
	}

	public bool Interact(Humanoid user, bool hold, bool alt)
	{
		if (hold || user != Player.m_localPlayer || !Plugin.On)
		{
			return false;
		}
		LadderClimb top = End(true);
		LadderClimb bottom = End(false);
		Vector3 topPoint = top.Top;
		Vector3 bottomPoint = bottom.Bottom;
		bool goUp = GoesUp(user, alt, top, bottom);
		float distance = Plugin.ExitDistance.Value;
		Vector3 front = bottom.FrontOf(topPoint, bottomPoint, distance, user); // bei gemischt gedrehten Stücken zählt das unterste
		Vector3 target;
		if (goUp)
		{
			// oben auf die Plattform hinter der Leiter, sonst nach vorn
			target = Spot(topPoint - front * distance, 1.6f, user) ?? Spot(topPoint + front * distance, 1.6f, user)
				?? Ground(topPoint - front * distance, 1.6f) ?? topPoint + Vector3.up * 0.2f;
		}
		else
		{
			// unten immer vor die Leiter, auf die freie Seite (Gorduan, 16.10.2026: vom Dach aus landete man sonst in der Wand)
			target = Spot(bottomPoint + front * distance, 2.5f, user) ?? Spot(bottomPoint - front * distance, 2.5f, user)
				?? Ground(bottomPoint + front * distance, 2.5f) ?? bottomPoint + front * distance;
		}

		user.transform.position = target;
		Rigidbody? body = user.GetComponent<Rigidbody>();
		if (body != null)
		{
			body.position = target;
			body.linearVelocity = Vector3.zero;
		}
		Physics.SyncTransforms();
		return false;
	}

	/// <summary>
	/// Vorderseite der Leiter (Gorduan, 16.10.2026: „nur ein Vorne“, unabhängig davon, von wo man sie anklickt): unten die
	/// Seite, auf der ein Spieler Platz hat. Sind unten beide frei (oder beide zu), liegt vorn gegenüber der Plattform oben.
	/// Sonst gilt die Vorderseite des Bauteils.
	/// </summary>
	private Vector3 FrontOf(Vector3 topPoint, Vector3 bottomPoint, float distance, Humanoid user)
	{
		Vector3 forward = transform.forward;
		forward.y = 0f;
		forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
		bool freeFront = Spot(bottomPoint + forward * distance, 2.5f, user) != null;
		bool freeBack = Spot(bottomPoint - forward * distance, 2.5f, user) != null;
		if (freeFront != freeBack)
		{
			return freeFront ? forward : -forward;
		}
		bool platformFront = Spot(topPoint + forward * distance, 1.6f, user) != null;
		bool platformBack = Spot(topPoint - forward * distance, 1.6f, user) != null;
		if (platformFront != platformBack)
		{
			return platformBack ? forward : -forward; // die Plattform liegt hinter der Leiter
		}
		return forward;
	}

	private static readonly Collider[] Hits = new Collider[16];

	/// <summary>Boden unter dieser Stelle, auf dem ein Spieler Platz hat (keine Wand, kein Fels, kein Bauteil im Weg).</summary>
	private static Vector3? Spot(Vector3 point, float depth, Humanoid user)
	{
		Vector3? ground = Ground(point, depth);
		if (ground == null)
		{
			return null;
		}
		Vector3 feet = ground.Value;
		int count = Physics.OverlapCapsuleNonAlloc(feet + Vector3.up * 0.6f, feet + Vector3.up * 1.5f, 0.3f, Hits, Mask, QueryTriggerInteraction.Ignore);
		for (int i = 0; i < count; i++)
		{
			Collider hit = Hits[i];
			if (hit == null || hit.GetComponentInParent<LadderClimb>() != null || hit.GetComponentInParent<Character>() != null)
			{
				continue; // die Leiter selbst, Spieler und Tiere sind kein Hindernis
			}
			return null;
		}
		return feet;
	}

	private static int mask;

	private static int Mask => mask != 0 ? mask : mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");

	private static Vector3? Ground(Vector3 point, float depth)
	{
		if (Physics.Raycast(point + Vector3.up * 1.2f, Vector3.down, out RaycastHit hit, 1.2f + depth, Mask, QueryTriggerInteraction.Ignore))
		{
			if (hit.collider.GetComponentInParent<LadderClimb>() != null)
			{
				return null;
			}
			return hit.point + Vector3.up * 0.05f;
		}
		return null;
	}

	public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

	public string GetHoverText()
	{
		Player? player = Player.m_localPlayer;
		bool up = player != null && GoesUp(player, false, End(true), End(false));
		string text = GcLoc.Get("gcld_name") + "\n[<color=yellow><b>$KEY_Use</b></color>] " + GcLoc.Get(up ? "gcld_up" : "gcld_down");
		if (up)
		{
			text += "\n[<color=yellow><b>$KEY_AltPlace + $KEY_Use</b></color>] " + GcLoc.Get("gcld_down"); // nur wenn E nach oben führt
		}
		return Localization.instance.Localize(text);
	}

	public string GetHoverName() => GcLoc.Get("gcld_name");

	public float GetHoverOffset() => 0f;
}
