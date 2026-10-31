using System.IO;
using System.Linq;
using GC.Shared;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Outfit;

/// <summary>
/// Kleiderschrank (Gorduan, 30.10.2026): Modell nach Gorduans Referenzbildern (E:\Blender\Kleiderschrank, build_kleiderschrank.py,
/// 3,00 m hoch, 2,00 m breit = ein Wand- und Bodenfeld, 0,55 m tief; Gorduan 31.10.2026: Höhe ×1,5, Breite an Valheims Bauteile
/// angepasst, Textur wie Valheims Bauholz: breite Bretter, Farbwolken, Schmutz an Fugen, 128 px, make_kleiderschrank_textur.py): Korpus und zwei Türen, die sich beim Benutzen öffnen und beim Schließen des Menüs
/// wieder schließen. Der Schrank hat keinen Inhalt (Gorduan). Rezept (Gorduan): 10 Edelholz und 10 Bronzenägel,
/// gebaut mit dem Hammer an der Werkbank (Möbel).
/// Bauteil aus piece_chest_wood (Holz: Bau-, Treffer- und Zerstörungseffekte, Öffnen- und Schließen-Geräusch der Kiste);
/// dessen Modelle und die Kiste selbst fliegen raus.
/// </summary>
internal static class Wardrobes
{
	internal const string PrefabName = "GC_Wardrobe";

	/// <summary>Scharnierachsen der Türen (Unity, aus build_kleiderschrank.py).</summary>
	private static readonly Vector3 LeftHinge = new(-0.91f, 0f, 0.255f);
	private static readonly Vector3 RightHinge = new(0.91f, 0f, 0.255f);

	internal static Piece? Piece;

	internal static void Init() => PrefabManager.OnVanillaPrefabsAvailable += Create;

	private static void Create()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= Create;
		GameObject? chest = PrefabManager.Instance.GetPrefab("piece_chest_wood");
		Material? wood = chest == null ? null : WoodMaterial(chest);
		Gcm? body = Gcm.Read("kleiderschrank_korpus.gcm");
		Gcm? left = Gcm.Read("kleiderschrank_tuer_links.gcm");
		Gcm? right = Gcm.Read("kleiderschrank_tuer_rechts.gcm");
		Texture2D? texture = LoadTexture("kleiderschrank.png");
		if (chest == null || wood == null || body == null || left == null || right == null || texture == null)
		{
			Outfits.Log.LogError("Holzkiste (piece_chest_wood), ihr Material oder das Schrankmodell fehlt: Kleiderschrank wird nicht erstellt.");
			return;
		}
		Material material = new(wood) { name = "GC_Wardrobe", mainTexture = texture };
		if (material.HasProperty("_BumpMap"))
		{
			material.SetTexture("_BumpMap", null); // die Normalen der Kiste passen nicht zur eigenen Textur
		}
		GameObject? prefab = PrefabManager.Instance.CreateClonedPrefab(PrefabName, "piece_chest_wood");
		if (prefab == null)
		{
			Outfits.Log.LogError("Kleiderschrank: piece_chest_wood ließ sich nicht kopieren.");
			return;
		}
		Container? container = prefab.GetComponent<Container>();
		EffectList open = CopyEffects(container != null ? container.m_openEffects : null);
		EffectList close = CopyEffects(container != null ? container.m_closeEffects : null);
		if (container != null)
		{
			Object.DestroyImmediate(container); // Gorduan: der Schrank hat keinen Inhalt
		}
		int layer = prefab.layer;
		foreach (Transform child in prefab.transform.Cast<Transform>().ToList())
		{
			Object.DestroyImmediate(child.gameObject);
		}
		foreach (Collider collider in prefab.GetComponents<Collider>())
		{
			Object.DestroyImmediate(collider);
		}
		BoxCollider box = prefab.AddComponent<BoxCollider>();
		box.center = new Vector3(0f, 1.5f, 0f);
		box.size = new Vector3(2.0f, 3.0f, 0.58f);

		GameObject model = new("new") { layer = layer };
		model.transform.SetParent(prefab.transform, false);
		model.AddComponent<MeshFilter>().sharedMesh = body.ToMesh("GC_Wardrobe_Body");
		model.AddComponent<MeshRenderer>().sharedMaterial = material;
		Transform leftDoor = Door(model.transform, "door_left", left.ToMesh("GC_Wardrobe_DoorLeft"), material, LeftHinge, layer);
		Transform rightDoor = Door(model.transform, "door_right", right.ToMesh("GC_Wardrobe_DoorRight"), material, RightHinge, layer);

		WearNTear wear = prefab.GetComponent<WearNTear>();
		if (wear != null)
		{
			wear.m_new = model;
			wear.m_worn = model;
			wear.m_broken = model;
			wear.m_wet = null; // zeigte auf Teile der Kiste
			wear.m_fragmentRoots = null; // Splitter aus dem eigenen Modell statt aus den gelöschten Teilen der Kiste
			wear.m_health = 400f;
			wear.m_materialType = WearNTear.MaterialType.Wood;
		}
		Wardrobe wardrobe = prefab.AddComponent<Wardrobe>();
		wardrobe.LeftDoor = leftDoor;
		wardrobe.RightDoor = rightDoor;
		wardrobe.OpenEffects = open;
		wardrobe.CloseEffects = close;

		Piece piece = prefab.GetComponent<Piece>();
		piece.m_name = GcLoc.Get("gcof_wardrobe");
		piece.m_description = GcLoc.Get("gcof_wardrobe_desc");
		if (!Gc.IsHeadless)
		{
			piece.m_icon = Icon(prefab);
		}
		Piece = piece;
		PieceConfig config = new()
		{
			PieceTable = "_HammerPieceTable",
			Category = "Furniture",
			CraftingStation = "piece_workbench",
			Requirements = new[] { new RequirementConfig("FineWood", 10, 0, true), new RequirementConfig("BronzeNails", 10, 0, true) },
		};
		PieceManager.Instance.AddPiece(new CustomPiece(prefab, true, config));
		ApplyEnabled();
		Outfits.Log.LogInfo("Kleiderschrank angelegt (Werkbank, Hammer → Möbel, 10 Edelholz, 10 Bronzenägel).");
	}

	private static Transform Door(Transform parent, string name, Mesh mesh, Material material, Vector3 hinge, int layer)
	{
		GameObject door = new(name) { layer = layer };
		door.transform.SetParent(parent, false);
		door.transform.localPosition = hinge;
		door.AddComponent<MeshFilter>().sharedMesh = mesh;
		door.AddComponent<MeshRenderer>().sharedMaterial = material;
		return door.transform;
	}

	/// <summary>
	/// Holzmaterial der Kiste (Shader Custom/Piece). Das erste Kind der Kiste ist ein abgeschalteter Schneeboden
	/// (floor_2x2_snow, Material snow_flat), dessen Shader die eigene Textur nicht zeigt.
	/// </summary>
	private static Material? WoodMaterial(GameObject chest)
	{
		MeshRenderer[] renderers = chest.GetComponentsInChildren<MeshRenderer>(true);
		return renderers.Where(r => r.name == "woodchest").Select(r => r.sharedMaterial).FirstOrDefault(m => m != null)
			?? renderers.Select(r => r.sharedMaterial).FirstOrDefault(m => m != null && m.name.StartsWith("woodchest"));
	}

	/// <summary>Eigene Effektliste mit denselben Einträgen; die der Kiste bleibt unberührt.</summary>
	private static EffectList CopyEffects(EffectList? source) =>
		new() { m_effectPrefabs = source?.m_effectPrefabs?.ToArray() ?? new EffectList.EffectData[0] };

	private static Texture2D? LoadTexture(string file)
	{
		System.Reflection.Assembly assembly = typeof(Wardrobes).Assembly;
		string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + file));
		if (resource == null)
		{
			return null;
		}
		using Stream stream = assembly.GetManifestResourceStream(resource)!;
		using MemoryStream memory = new();
		stream.CopyTo(memory);
		Texture2D texture = new(2, 2, TextureFormat.RGBA32, false) { name = "GC_Wardrobe" };
		if (!texture.LoadImage(memory.ToArray()))
		{
			return null;
		}
		texture.filterMode = FilterMode.Point; // Pixel wie Valheims Holz
		texture.wrapMode = TextureWrapMode.Clamp;
		return texture;
	}

	private static Sprite? Icon(GameObject prefab)
	{
		try
		{
			return RenderManager.Instance.Render(new RenderManager.RenderRequest(prefab) { Width = 128, Height = 128, Rotation = Quaternion.Euler(6f, 205f, 0f) });
		}
		catch (System.Exception e)
		{
			Outfits.Log.LogWarning($"Symbol des Kleiderschranks nicht erzeugt: {e.Message}");
			return null;
		}
	}

	/// <summary>Bauen nur, solange die Server-Regel Outfits erlaubt.</summary>
	internal static void ApplyEnabled()
	{
		if (Piece != null)
		{
			Piece.m_enabled = Outfits.Enabled.Value;
		}
	}
}

/// <summary>
/// Der Schrank in der Welt: E öffnet das Outfit-Fenster (WardrobeWindow), die Türen gehen auf und beim Schließen des Fensters
/// wieder zu. Die Türen bewegen sich bei allen Spielern in der Nähe (RPC an alle); wer später dazukommt, sieht sie zu.
/// </summary>
internal sealed class Wardrobe : MonoBehaviour, Hoverable, Interactable
{
	private const string DoorRpc = "GC_WardrobeDoors";
	private const float OpenAngle = 100f;
	private const float Seconds = 0.35f;

	public Transform? LeftDoor;
	public Transform? RightDoor;
	public EffectList OpenEffects = new();
	public EffectList CloseEffects = new();

	private ZNetView? nview;
	private float current;
	private float target;

	private void Awake()
	{
		nview = GetComponent<ZNetView>();
		if (nview == null || nview.GetZDO() == null)
		{
			return; // Bauvorschlag
		}
		nview.Register<bool>(DoorRpc, (sender, open) => SetTarget(open));
	}

	private void Update()
	{
		if (Mathf.Approximately(current, target))
		{
			return;
		}
		current = Mathf.MoveTowards(current, target, Time.deltaTime / Seconds);
		float eased = Mathf.SmoothStep(0f, 1f, current);
		if (LeftDoor != null)
		{
			LeftDoor.localRotation = Quaternion.Euler(0f, -OpenAngle * eased, 0f);
		}
		if (RightDoor != null)
		{
			RightDoor.localRotation = Quaternion.Euler(0f, OpenAngle * eased, 0f);
		}
	}

	private void SetTarget(bool open)
	{
		float wanted = open ? 1f : 0f;
		if (Mathf.Approximately(wanted, target))
		{
			return;
		}
		target = wanted;
		(open ? OpenEffects : CloseEffects).Create(transform.position + Vector3.up, transform.rotation);
	}

	internal bool Valid => nview != null && nview.IsValid();

	/// <summary>Türen auf oder zu, für alle Spieler in der Nähe.</summary>
	internal void SetDoors(bool open)
	{
		if (Valid)
		{
			nview!.InvokeRPC(ZNetView.Everybody, DoorRpc, open);
		}
	}

	public string GetHoverText()
	{
		if (!Valid)
		{
			return "";
		}
		string name = GcLoc.Get("gcof_wardrobe");
		if (!PrivateArea.CheckAccess(transform.position, 0f, false))
		{
			return Localization.instance.Localize(name + "\n$piece_noaccess");
		}
		return Localization.instance.Localize(name + "\n[<color=yellow><b>$KEY_Use</b></color>] ") + GcLoc.Get("gcof_use");
	}

	public string GetHoverName() => GcLoc.Get("gcof_wardrobe");

	public float GetHoverOffset() => 0f;

	public bool Interact(Humanoid user, bool hold, bool alt)
	{
		if (hold || user != Player.m_localPlayer || !Valid)
		{
			return false;
		}
		if (!PrivateArea.CheckAccess(transform.position))
		{
			return true;
		}
		WardrobeWindow.Begin(this);
		return true;
	}

	public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
}
