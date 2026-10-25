using System;
using System.Collections.Generic;
using System.IO;
using GC.Shared;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.Audio;

namespace GC.Server.Leash;

/// <summary>
/// Eine Tierpfeife (Gorduan, 19.10.2026: nur noch vier, Stufe 1 Edelholz, 2 Eisen, 3 Walküre, 4 Flammenmetall; die alten
/// Barren-Pfeifen bis 1.6.0 gibt es nicht mehr, vorhandene verschwinden beim Laden, Gorduan: „dürfen zerstört werden“).
/// </summary>
internal sealed class WhistleTier
{
	public int Level;
	/// <summary>Stärke auf der alten Skala 1 (Kupfer) bis 8 (Blutgold): die vier Pfeifen gleichmäßig von der schwächsten bis zur stärksten.</summary>
	public float Strength;
	/// <summary>Werkbank statt Schmiede (Edelholz).</summary>
	public bool Workbench;
	public string Bar = "";
	public string PrefabName = "";
	public Color Color;
	public ItemDrop? Prefab;
	public CustomItem? Item;

	/// <summary>Walkürenpfeife (Gorduan, 19.10.2026): Stärke der Schwarzmetall-Pfeife, ruft dazu die Walküre (Valkyrie.Transport).</summary>
	public bool Valkyrie;

	/// <summary>Zweite Zutat neben dem Barren (Walkürenpfeife: Donnerstein), leer: nur der Barren.</summary>
	public string Extra = "";

	/// <summary>Tonhöhe des Pfiffs (ClipFor), 0: wie die Stufe.</summary>
	public int SoundLevel;

	/// <summary>Walkürenpfeife beim Pfiff zusätzlich (Gorduan): Angriffe +x % als Blitzschaden, danach normaler Schaden −x % (ein Teil wird Blitz).</summary>
	public float ExtraLightning;
	public float ExtraNormal;

	public int Tone => SoundLevel > 0 ? SoundLevel : Level;

	/// <summary>Befehlspause kürzer um diesen Anteil: Edelholz 6,25 %, Eisen 20,8 %, Walküre 35,4 %, Flammenmetall 50 % (Gorduan: beste Pfeife 50 %).</summary>
	public float Reduction => Strength * 0.0625f;

	/// <summary>Tempo beim Pfiff in Prozent: Edelholz 3,1, Eisen 10,4, Walküre 17,7, Flammenmetall 25 (Gorduan: beste Pfeife 25 %).</summary>
	public float Speed => Strength * 3.125f;
}

/// <summary>
/// Tierpfeifen (Gorduan, 18.10.2026; seit 19.10.2026 nur noch Edelholz, Eisen, Walküre und Flammenmetall), ein Einhandgegenstand wie die Leine, aktiv
/// aus der Schnellleiste mit ihrer Taste. Mit der Pfeife in der Hand gelten die Befehle der Leine für alle Tiere an den
/// eigenen Leinen: Linksklick schickt alle an den angezielten Ort (leicht verteilt, jedes so weit seine Leine reicht),
/// Rechtsklick lässt alle das Ziel angreifen. Je Tier gelten die Leinen-Regeln (ab Stufe 10, eigene Pause); die Pfeife
/// verkürzt diese Pause (WhistleTier.Reduction). Jedes Tier, das gehorcht, bekommt BuffSeconds lang mehr Tempo
/// (WhistleTier.Speed) und die EpicLoot-Verzauberungen der Pfeife (nur Tier-Werte, LeashMagic „GC_IsLeashOrWhistle“),
/// und seine Leine nimmt so lange keinen Schaden. Nach einem Pfiff ist die Pfeife so lange gesperrt, wie der Buff hält
/// (Gorduan, 19.10.2026: 5 s, „um sie etwas zu nerfen“).
///
/// Modell: eigenes Blender-Modell (E:\Blender\Tierpfeife, build_tierpfeife.py), als Dreiecksnetz in der DLL
/// (Assets\tierpfeife.gcm, Format GCM1), in der Farbe des Barrens; Symbol per Jötunn-RenderManager aus dem Modell.
/// Ton: ein kurzer Pfiff (synthetisch erzeugt, Tonhöhe je Stufe), alle Spieler in der Nähe hören ihn.
/// </summary>
internal static class Whistles
{
	internal const float BuffSeconds = 5f;
	private const string SoundRpc = "GC_Whistle";
	private const float HearingRange = 90f;

	internal static readonly WhistleTier[] Tiers =
	{
		// Gorduan, 19.10.2026: vier Pfeifen, gleichmäßig von der bisher schwächsten (Kupfer, Stärke 1) bis zur stärksten (Blutgold, 8)
		new() { Level = 1, Strength = 1f, Bar = "FineWood", PrefabName = "GC_Whistle_FineWood", Color = new Color(0.62f, 0.45f, 0.28f), Workbench = true },
		new() { Level = 2, Strength = 1f + 7f / 3f, Bar = "Iron", PrefabName = "GC_Whistle_Iron", Color = new Color(0.48f, 0.48f, 0.50f) },
		// Walkürenpfeife (Gorduan): Schwarzmetall und Donnerstein, ruft die Walküre; für Tiere dazu Blitzschaden und Schutz, eigener Ton
		new() { Level = 3, Strength = 1f + 14f / 3f, Bar = "BlackMetal", Extra = "Thunderstone", PrefabName = "GC_Whistle_Valkyrie", Color = new Color(0.20f, 0.24f, 0.34f),
			Valkyrie = true, SoundLevel = 9, ExtraLightning = 5f, ExtraNormal = -5f },
		new() { Level = 4, Strength = 8f, Bar = "FlametalNew", PrefabName = "GC_Whistle_Flametal", Color = new Color(0.95f, 0.42f, 0.12f) },
	};

	private static readonly Dictionary<string, WhistleTier> ByItemName = new();
	private static readonly Dictionary<int, AudioClip> Clips = new();
	private static readonly Dictionary<long, float> LastHeard = new();
	private static readonly List<Link> Mine = new();
	private static readonly List<Link> Willing = new();
	private static readonly List<Link> Obeyed = new();
	private static readonly List<Character> Near = new();
	private static readonly HashSet<Character> Burned = new();

	/// <summary>Surts Glutwelle: Umkreis um jedes gehorchende Tier.</summary>
	private const float BlazeRadius = 6f;
	private static Mesh? model;
	private static bool modelLoaded;
	private static AudioMixerGroup? sfx;
	private static bool sfxSearched;
	private static float nextSound;
	private static float readyAt;

	internal static bool On => Plugin.On && Plugin.WhistlesEnabled.Value;

	// ------------------------------------------------------------------ Items

	internal static bool IsWhistle(ItemDrop.ItemData? item, out WhistleTier tier)
	{
		tier = null!;
		return item?.m_shared != null && ByItemName.TryGetValue(item.m_shared.m_name, out tier);
	}

	/// <summary>Die Tierpfeife in der Hand des Spielers (rechte Hand), sonst null.</summary>
	internal static ItemDrop.ItemData? Held(Player? player)
	{
		if (!On || player == null)
		{
			return null;
		}
		ItemDrop.ItemData? item = player.GetRightItem();
		return item != null && IsWhistle(item, out _) ? item : null;
	}

	internal static void CreateItems()
	{
		int created = 0;
		foreach (WhistleTier tier in Tiers)
		{
			ItemDrop? bar = PrefabManager.Instance.GetPrefab(tier.Bar)?.GetComponent<ItemDrop>();
			if (bar == null)
			{
				Plugin.Log.LogInfo($"Tierpfeife Stufe {tier.Level}: Barren {tier.Bar} gibt es im Spiel nicht, Pfeife entfällt.");
				continue;
			}
			ItemDrop? extra = tier.Extra.Length > 0 ? PrefabManager.Instance.GetPrefab(tier.Extra)?.GetComponent<ItemDrop>() : null;
			if (tier.Extra.Length > 0 && extra == null)
			{
				Plugin.Log.LogInfo($"Tierpfeife {tier.PrefabName}: Zutat {tier.Extra} gibt es im Spiel nicht, Pfeife entfällt.");
				continue;
			}
			string name = tier.Valkyrie ? "$gc_whistle_valkyrie" : "$gc_whistle_item (" + bar.m_itemData.m_shared.m_name + ")";
			// die Beschreibung der Walkürenpfeife (Karren-Transport) trägt GC_WalkuerenTransport ein; ohne die Mod die allgemeine
			string description = tier.Valkyrie && GcHooks.CourierTooltip != null ? "$gc_whistle_valkyrie_desc" : "$gc_whistle_desc";
			List<RequirementConfig> requirements = new() { new RequirementConfig(tier.Bar, 1, 0, true) };
			if (extra != null)
			{
				requirements.Add(new RequirementConfig(tier.Extra, 1, 0, true));
			}
			ItemConfig config = new()
			{
				Name = name,
				Description = description,
				CraftingStation = tier.Workbench ? CraftingStations.Workbench : CraftingStations.Forge,
				MinStationLevel = 1,
				Weight = 0.3f,
				Requirements = requirements.ToArray(),
			};
			config.StackSize = 1;
			CustomItem item = new(tier.PrefabName, tier.Bar, config);
			ItemDrop.ItemData.SharedData shared = item.ItemDrop.m_itemData.m_shared;
			shared.m_name = name;
			shared.m_description = description;
			Control.MakeHandShared(shared); // in die Hand wie die Leine, ohne Schaden und Block
			shared.m_maxStackSize = 1;
			shared.m_maxQuality = 1;
			shared.m_useDurability = false;
			shared.m_canBeReparied = false;
			shared.m_teleportable = true;
			shared.m_value = 0;
			Dress(item.ItemDrop.gameObject, tier, bar, extra);
			Sprite? icon = Icon(item.ItemDrop.gameObject);
			if (icon != null)
			{
				shared.m_icons = new[] { icon };
			}
			if (!ItemManager.Instance.AddItem(item))
			{
				Plugin.Log.LogWarning($"Tierpfeife Stufe {tier.Level} ({tier.PrefabName}) konnte nicht registriert werden.");
				continue;
			}
			tier.Prefab = item.ItemDrop;
			tier.Item = item;
			ByItemName[name] = tier;
			created++;
		}
		ApplyRecipes();
		Plugin.Log.LogInfo($"Tierpfeifen: {created} Pfeifen erstellt (Modell {(Model() != null ? "geladen" : "fehlt")}).");
	}

	internal static void ApplyRecipes()
	{
		foreach (WhistleTier tier in Tiers)
		{
			if (tier.Item?.Recipe?.Recipe != null)
			{
				// aus: nicht mehr herstellbar, vorhandene Pfeifen bleiben erhalten; die Walkürenpfeife hängt zusätzlich am Transport
				tier.Item.Recipe.Recipe.m_enabled = On && (!tier.Valkyrie || GcHooks.IsCourierOn());
			}
		}
	}

	/// <summary>
	/// Der Barren als Vorlage bekommt das Pfeifen-Modell: am Boden („model“) und in der Hand („attach“, von Valheim beim
	/// Ausrüsten an die Hand gehängt). Die Darstellung des Barrens fällt weg, ein Quader-Kollider in Modellgröße kommt dazu.
	/// Auf dem Server ohne Grafik nur Kollider und leere Halterung.
	/// </summary>
	private static void Dress(GameObject prefab, WhistleTier tier, ItemDrop bar, ItemDrop? extra)
	{
		Material? baseMaterial = null;
		// erst sammeln, dann löschen: ein Renderer unter einem anderen wäre sonst schon mit seinem Elternteil zerstört (Prüfung 18.10.)
		List<GameObject> visuals = new();
		foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
		{
			if (baseMaterial == null && renderer is MeshRenderer or SkinnedMeshRenderer)
			{
				baseMaterial = renderer.sharedMaterial; // der Barren selbst, nicht Partikel oder Linien
			}
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
		Mesh? mesh = Model();
		Bounds bounds = mesh != null ? mesh.bounds : new Bounds(new Vector3(0f, 0f, 0.06f), new Vector3(0.018f, 0.018f, 0.126f));
		BoxCollider box = prefab.AddComponent<BoxCollider>();
		box.center = bounds.center;
		box.size = Vector3.Max(bounds.size + Vector3.one * 0.01f, Vector3.one * 0.04f); // mindestens 4 cm: Valheims Zeige-Strahl trifft sonst schwer
		GameObject attach = new("attach");
		attach.SetActive(false);
		attach.transform.SetParent(prefab.transform, false);
		if (Gc.IsHeadless || mesh == null)
		{
			return;
		}
		Material material = MaterialFor(tier, baseMaterial);
		// Bodenmodell direkt am Item, nicht an einem Kind: Jötunns RenderManager prüft vor dem Symbol nur aktive Objekte, und
		// unter Jötunns inaktivem Prefab-Behälter zählt sicher nur das Item selbst (Prüfung 18.10.)
		prefab.AddComponent<MeshFilter>().sharedMesh = mesh;
		prefab.AddComponent<MeshRenderer>().sharedMaterial = material;
		// in der Hand: wie ein kurzer Griff, Mundstück nach oben (Valheims Hand-Halterung zeigt mit +Y nach vorn oben)
		Transform hand = AddModel(attach.transform, mesh, material, new Vector3(0f, -0.045f, 0f), Quaternion.Euler(-90f, 0f, 0f));
		if (tier.Valkyrie && extra != null)
		{
			// Walkürenpfeife: Valheims Donnerstein, klein am Tragering (Ring bei z ≈ 0,1225, Mundstück bei z = 0)
			AddGem(prefab.transform, extra);
			AddGem(hand, extra);
		}
	}

	/// <summary>
	/// Der Donnerstein als Anhänger am Ring: Netz und Material des Valheim-Items (kein eigenes Modell), auf 2,6 cm verkleinert,
	/// leicht gedreht unter dem Ring. Ohne passendes Netz bleibt die Pfeife ohne Stein.
	/// </summary>
	private static void AddGem(Transform parent, ItemDrop stone)
	{
		MeshFilter? source = null;
		foreach (MeshFilter filter in stone.GetComponentsInChildren<MeshFilter>(true))
		{
			if (filter.sharedMesh != null && filter.GetComponent<MeshRenderer>() != null)
			{
				source = filter;
				break;
			}
		}
		if (source == null)
		{
			return;
		}
		Mesh gemMesh = source.sharedMesh;
		Bounds bounds = gemMesh.bounds;
		float size = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
		float scale = size > 0.0001f ? 0.026f / size : 1f;
		GameObject gem = new("gem");
		gem.transform.SetParent(parent, false);
		gem.transform.localRotation = Quaternion.Euler(20f, 35f, 10f);
		gem.transform.localScale = Vector3.one * scale;
		gem.transform.localPosition = new Vector3(0f, -0.011f, 0.131f) - gem.transform.localRotation * (bounds.center * scale);
		gem.AddComponent<MeshFilter>().sharedMesh = gemMesh;
		gem.AddComponent<MeshRenderer>().sharedMaterials = source.GetComponent<MeshRenderer>().sharedMaterials;
	}

	private static Transform AddModel(Transform parent, Mesh mesh, Material material, Vector3 position, Quaternion rotation)
	{
		GameObject go = new("model");
		go.transform.SetParent(parent, false);
		go.transform.localPosition = position;
		go.transform.localRotation = rotation;
		go.AddComponent<MeshFilter>().sharedMesh = mesh;
		MeshRenderer renderer = go.AddComponent<MeshRenderer>();
		renderer.sharedMaterial = material;
		return go.transform;
	}

	/// <summary>
	/// Das Material des Barrens unverändert: Valheims Shader und die Pixel-Textur des Metalls (Kupfer mit Patina, Eisen, Silber …),
	/// so passt die Pfeife zum Spiel (Gorduan 18.10.2026). Die UVs des Modells legen die Barren-Textur einmal um die Pfeife.
	/// Nur ohne Barren-Material ein Ersatz mit eigener Metallfläche.
	/// </summary>
	private static Material MaterialFor(WhistleTier tier, Material? baseMaterial)
	{
		if (baseMaterial != null)
		{
			return baseMaterial;
		}
		// Ersatz: Unitys Standard-Shader mit eigener Metallfläche in der Farbe der Pfeife
		Material material = new(Shader.Find("Standard") ?? Shader.Find("Diffuse")) { name = tier.PrefabName, mainTexture = MetalTexture(tier) };
		if (material.HasProperty("_Metallic"))
		{
			material.SetFloat("_Metallic", tier.Bar == "BlackMetal" ? 0.55f : tier.Workbench ? 0f : 0.8f);
		}
		if (material.HasProperty("_Glossiness"))
		{
			material.SetFloat("_Glossiness", 0.6f);
		}
		return material;
	}

	/// <summary>Kleine Metallfläche: Grundfarbe mit feinen Schleifspuren entlang der Pfeife (v = Länge).</summary>
	private static Texture2D MetalTexture(WhistleTier tier)
	{
		const int size = 64;
		Texture2D texture = new(size, size, TextureFormat.RGBA32, false) { name = tier.PrefabName + "_tex", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point };
		System.Random random = new(tier.Level * 7919);
		float[] streak = new float[size];
		for (int x = 0; x < size; x++)
		{
			streak[x] = (float)random.NextDouble() * 0.08f - 0.04f;
		}
		Color[] pixels = new Color[size * size];
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				float shade = 1f + streak[x] + ((float)random.NextDouble() * 0.04f - 0.02f);
				Color c = tier.Color * shade;
				c.a = 1f;
				pixels[y * size + x] = c;
			}
		}
		texture.SetPixels(pixels);
		texture.Apply(true);
		return texture;
	}

	/// <summary>Symbol aus dem Modell (schräg von oben, Mundstück unten links). Ohne Grafik oder bei einem Fehler: das des Barrens.</summary>
	private static Sprite? Icon(GameObject prefab)
	{
		if (Gc.IsHeadless || Model() == null)
		{
			return null;
		}
		try
		{
			Quaternion rotation = Quaternion.AngleAxis(35f, Vector3.forward) * Quaternion.AngleAxis(-30f, Vector3.right) * Quaternion.AngleAxis(90f, Vector3.up);
			Sprite? icon = RenderManager.Instance.Render(new RenderManager.RenderRequest(prefab) { Width = 128, Height = 128, Rotation = rotation });
			if (icon == null)
			{
				Plugin.Log.LogWarning($"Tierpfeife: Symbol für {prefab.name} nicht gerendert, es bleibt das des Barrens.");
			}
			return icon;
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning($"Tierpfeife: Symbol nicht gerendert, es bleibt das des Barrens: {e.Message}");
			return null;
		}
	}

	/// <summary>
	/// Low-Poly-Modell aus der DLL (E:\Blender\Tierpfeife\build_tierpfeife_lowpoly.py, 256 Dreiecke; Gorduan 18.10.2026: die erste
	/// Fassung war zu detailliert für Valheim). Gelesen mit dem gemeinsamen GCM-Lader.
	/// </summary>
	internal static Mesh? Model()
	{
		if (modelLoaded)
		{
			return model;
		}
		modelLoaded = true;
		model = Gcm.Read("tierpfeife.gcm")?.ToMesh("GC_Tierpfeife");
		return model;
	}

	// ------------------------------------------------------------------ Befehle

	/// <summary>Alle Tiere an den eigenen Leinen, die gerade Befehle nehmen können (nicht unterwegs, nicht geritten).</summary>
	private static List<Link> MyAnimals(Player player)
	{
		Mine.Clear();
		foreach (Link link in Links.Active)
		{
			if (link.Holder == player && link.Animal != null && link.Tame != null && !link.Traveling && !link.Ridden)
			{
				Mine.Add(link);
			}
		}
		return Mine;
	}

	/// <summary>Linksklick mit der Pfeife: alle Tiere an deinen Leinen gehen zum angezielten Ort, um ihn verteilt.</summary>
	internal static void Send(Player player, ItemDrop.ItemData item)
	{
		if (!IsWhistle(item, out WhistleTier tier) || !Cooled())
		{
			return;
		}
		Sound(player, tier);
		if (tier.Valkyrie && GcHooks.CourierWhistle != null && GcHooks.CourierWhistle(player, item))
		{
			return; // Walkürenpfeife am beladenen Karren oder mit Karren in der Luft: die Walküre statt der Tiere
		}
		List<Link> links = MyAnimals(player);
		if (links.Count == 0)
		{
			Gc.TopLeft(GcLoc.Get("gcwh_none"));
			return;
		}
		if (!Control.Aim(out Vector3 point))
		{
			Gc.TopLeft(GcLoc.Get("gcwh_no_point"));
			return;
		}
		// erst die Tiere, die überhaupt gehorchen können: nur sie werden um den Ort verteilt
		Willing.Clear();
		string? refusal = null;
		foreach (Link link in links)
		{
			if (Control.Ready(link, link.Tame.GetHoverName(), out string why))
			{
				Willing.Add(link);
			}
			else
			{
				refusal ??= why;
			}
		}
		Obeyed.Clear();
		float radius = SpreadRadius(Willing.Count);
		Vector3 holder = player.transform.position;
		for (int i = 0; i < Willing.Count; i++)
		{
			// Mitte erst in die Reichweite dieser Leine (abzüglich Kreis), dann verteilen: sonst drängen sich am Leinenende alle auf einem Fleck
			Vector3 centre = Control.Within(holder, point, Mathf.Max(1f, Control.FreeReach(Willing[i]) - radius));
			if (Control.TrySend(player, Willing[i], Spread(centre, i, Willing.Count, radius), 1f - tier.Reduction, out string message))
			{
				Obeyed.Add(Willing[i]);
				Buff(Willing[i], item, tier);
			}
			else
			{
				refusal ??= message;
			}
		}
		Mythic(player, item, Obeyed);
		Gc.TopLeft(Obeyed.Count > 0 ? GcLoc.Get("gcwh_go", Obeyed.Count, links.Count) : GcLoc.Get("gcwh_refused", refusal ?? ""));
	}

	/// <summary>Rechtsklick mit der Pfeife: alle Tiere an deinen Leinen greifen das anvisierte Ziel an.</summary>
	internal static void Sic(Player player, ItemDrop.ItemData item)
	{
		if (!IsWhistle(item, out WhistleTier tier) || !Cooled())
		{
			return;
		}
		Sound(player, tier);
		if (tier.Valkyrie && GcHooks.CourierWhistle != null && GcHooks.CourierWhistle(player, item))
		{
			return;
		}
		List<Link> links = MyAnimals(player);
		if (links.Count == 0)
		{
			Gc.TopLeft(GcLoc.Get("gcwh_none"));
			return;
		}
		Character? target = player.GetHoverCreature();
		Obeyed.Clear();
		string? refusal = null;
		foreach (Link link in links)
		{
			if (Control.TrySic(player, link, target, 1f - tier.Reduction, out string message))
			{
				Obeyed.Add(link);
				Buff(link, item, tier);
			}
			else
			{
				refusal ??= message;
			}
		}
		Mythic(player, item, Obeyed);
		Gc.TopLeft(Obeyed.Count > 0 && target != null ? GcLoc.Get("gcwh_attack", Obeyed.Count, links.Count, target.GetHoverName()) : GcLoc.Get("gcwh_refused", refusal ?? ""));
	}

	/// <summary>
	/// Mythische Pfeifen (Gorduan, 24.10.2026: „mythisch darf mehr, aber eben nur spezielle einzigartige Effekte“): was nicht wie
	/// die übrigen Verzauberungen BuffSeconds lang am Tier hängt, geschieht einmal beim Pfiff, nur wenn ein Tier gehorcht hat.
	/// Gjallarhorns Hall lässt Gegner um dich taumeln (keine Bosse, wie die Abschreckung), Heimdalls Weckruf heilt die Tiere,
	/// Surts Glutwelle versengt Gegner um jedes Tier (jeden nur einmal). Treffer und Heilung gehen per RPC an den Besitzer.
	/// </summary>
	private static void Mythic(Player player, ItemDrop.ItemData item, List<Link> obeyed)
	{
		if (obeyed.Count == 0)
		{
			return;
		}
		BondStats bond = Bonds.Of(item);
		if (bond.Hall <= 0f && bond.Rally <= 0f && bond.Blaze <= 0f)
		{
			return;
		}
		if (bond.Hall > 0f)
		{
			Vector3 centre = player.transform.position;
			Near.Clear();
			Character.GetCharactersInRange(centre, bond.Hall, Near);
			foreach (Character other in Near)
			{
				if (Foe(player, other) && other.m_staggerWhenBlocked && !other.IsBoss())
				{
					other.Stagger((other.transform.position - centre).normalized);
				}
			}
		}
		Burned.Clear();
		foreach (Link link in obeyed)
		{
			Character animal = link.Animal;
			if (animal == null || animal.m_nview == null || !animal.m_nview.IsValid() || animal.IsDead() || Bonds.IsFainted(animal))
			{
				continue;
			}
			if (bond.Rally > 0f)
			{
				animal.Heal(animal.GetMaxHealth() * bond.Rally / 100f);
			}
			if (bond.Blaze > 0f)
			{
				Near.Clear();
				Character.GetCharactersInRange(animal.transform.position, BlazeRadius, Near);
				foreach (Character other in Near)
				{
					if (!Foe(animal, other) || !Burned.Add(other))
					{
						continue;
					}
					HitData hit = new();
					hit.m_damage.m_fire = bond.Blaze;
					hit.m_point = other.GetCenterPoint();
					hit.m_dir = (other.transform.position - animal.transform.position).normalized;
					hit.SetAttacker(animal);
					other.Damage(hit);
				}
			}
		}
	}

	/// <summary>
	/// Gegner für Hall und Glutwelle: lebt, kein Spieler, nicht zahm, und Valheim hält ihn für feindlich. Gültige ZNetView: Ein
	/// gerade entferntes Objekt steht bis zum Ende des Bilds noch in der Liste, ohne ZDO (Prüfung 24.10.2026).
	/// </summary>
	private static bool Foe(Character self, Character other)
	{
		return other != null && other != self && other.m_nview != null && other.m_nview.IsValid() && !other.IsDead() && !other.IsPlayer() && !other.IsTamed()
			&& BaseAI.IsEnemy(self, other);
	}

	/// <summary>
	/// Abklingzeit (Gorduan, 19.10.2026): Nach einem Pfiff ist jede Pfeife so lange gesperrt, wie der Buff hält (5 s). Gilt für
	/// alle Pfiffe, auch für die Walküre.
	/// </summary>
	private static bool Cooled()
	{
		float wait = readyAt - Time.time;
		if (wait > 0f)
		{
			Gc.TopLeft(GcLoc.Get("gcwh_cooldown", Mathf.CeilToInt(wait)));
			return false;
		}
		readyAt = Time.time + BuffSeconds;
		return true;
	}

	/// <summary>Kreis, auf dem mehrere Tiere um den Zielort stehen (ein Tier: keiner).</summary>
	private static float SpreadRadius(int count) => count <= 1 ? 0f : Mathf.Clamp(0.7f * count, 1.6f, 4.5f);

	/// <summary>Zielort je Tier: bei mehreren Tieren gleichmäßig im Kreis um den Ort (nicht ineinander), Höhe vom Boden.</summary>
	private static Vector3 Spread(Vector3 point, int index, int count, float radius)
	{
		if (count <= 1)
		{
			return point;
		}
		float angle = index * Mathf.PI * 2f / count;
		Vector3 spot = point + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
		spot.y = Control.Ground(spot, point.y);
		return spot;
	}

	/// <summary>Das Tier hat gehorcht: BuffSeconds lang Tempo und die Verzauberungen der Pfeife, kein Leinenschaden.</summary>
	private static void Buff(Link link, ItemDrop.ItemData item, WhistleTier tier)
	{
		BondStats bond = Bonds.Of(item).ForWhistle(tier.Speed); // neues Objekt: die Zusätze ändern nichts an der Pfeife selbst
		bond.Lightning += tier.ExtraLightning;
		bond.NormalAfter += tier.ExtraNormal;
		link.WhistleBond = bond;
		link.WhistleUntil = Time.time + BuffSeconds;
		link.WhistleBase = null; // Bonds.Current bildet die Werte neu
		link.WhistleCombined = null;
		if (bond.Fireproof && link.View != null && link.View.IsValid() && link.View.IsOwner() && link.Animal != null)
		{
			link.Animal.GetSEMan().RemoveStatusEffect(SEMan.s_statusEffectBurning, true); // Surts Glut löscht auch einen laufenden Brand
		}
	}

	// ------------------------------------------------------------------ Pfiff (Ton)

	/// <summary>Bei jedem Spielstart: Pfiffe anderer Spieler hören.</summary>
	internal static void RegisterRpcs()
	{
		ZRoutedRpc.instance?.Register<Vector3, int>(SoundRpc, RPC_Sound);
		sfxSearched = false;
		sfx = null;
		LastHeard.Clear();
	}

	/// <summary>Pfiff an alle (auch an sich selbst: ZRoutedRpc.Everybody läuft auch lokal).</summary>
	private static void Sound(Player player, WhistleTier tier)
	{
		if (Time.time < nextSound)
		{
			return;
		}
		nextSound = Time.time + 0.25f;
		ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, SoundRpc, player.transform.position + Vector3.up * 1.6f, tier.Tone);
	}

	private static void RPC_Sound(long sender, Vector3 position, int level)
	{
		Player? local = Player.m_localPlayer;
		if (Gc.IsHeadless || local == null || Vector3.Distance(local.transform.position, position) > HearingRange)
		{
			return;
		}
		if (LastHeard.TryGetValue(sender, out float last) && Time.time - last < 0.2f)
		{
			return; // auch beim Empfänger gebremst, nicht nur beim Absender
		}
		LastHeard[sender] = Time.time;
		AudioClip clip = ClipFor(Mathf.Clamp(level, 1, Tiers.Length));
		GameObject go = new("GC_Whistle_Sound");
		go.transform.position = position;
		AudioSource source = go.AddComponent<AudioSource>();
		source.clip = clip;
		source.spatialBlend = 1f;
		source.minDistance = 4f;
		source.maxDistance = HearingRange;
		source.rolloffMode = AudioRolloffMode.Linear;
		source.volume = 0.6f;
		source.outputAudioMixerGroup = SfxGroup();
		source.Play();
		UnityEngine.Object.Destroy(go, clip.length + 0.25f);
	}

	/// <summary>Valheims Mischpult-Gruppe für Geräusche (damit die Lautstärke-Einstellung gilt).</summary>
	internal static AudioMixerGroup? SfxGroup()
	{
		if (sfxSearched)
		{
			return sfx;
		}
		sfxSearched = true;
		// Valheims Geräusche (sfx_*, meist ohne ZNetView in m_nonNetViewPrefabs) hängen an der Gruppe, die SfxVol regelt
		if (ZNetScene.instance != null)
		{
			foreach (List<GameObject> prefabs in new[] { ZNetScene.instance.m_nonNetViewPrefabs, ZNetScene.instance.m_prefabs })
			{
				foreach (GameObject prefab in prefabs)
				{
					AudioSource? audio = prefab != null && prefab.name.StartsWith("sfx_", StringComparison.Ordinal) ? prefab.GetComponentInChildren<AudioSource>(true) : null;
					if (audio != null && audio.outputAudioMixerGroup != null)
					{
						return sfx = audio.outputAudioMixerGroup;
					}
				}
			}
		}
		if (AudioMan.instance != null && AudioMan.instance.m_masterMixer != null)
		{
			AudioMixerGroup[] groups = AudioMan.instance.m_masterMixer.FindMatchingGroups("SFX");
			if (groups.Length > 0)
			{
				return sfx = groups[0];
			}
		}
		Plugin.Log.LogWarning("Tierpfeife: keine Geräusch-Gruppe gefunden, der Pfiff folgt nicht der Lautstärke-Einstellung.");
		return null;
	}

	/// <summary>
	/// Kurzer Signalpfiff, erzeugt statt aufgenommen (Valheim hat keinen): kurzer Anstieg, Triller wie bei einer Pfeife mit
	/// Kugel, ein Hauch Atemgeräusch, Ausklang. Bessere Pfeifen klingen etwas heller.
	/// </summary>
	private static AudioClip ClipFor(int level)
	{
		if (Clips.TryGetValue(level, out AudioClip existing) && existing != null)
		{
			return existing;
		}
		const int rate = 44100;
		bool call = level >= 9; // Walkürenpfeife: Ruf aus zwei Tönen, der zweite fällt ab
		float duration = call ? 0.9f : 0.55f;
		int count = (int)(rate * duration);
		float[] data = new float[count];
		float baseFrequency = call ? 2900f : 2350f + 110f * level;
		System.Random noise = new(level * 104729);
		double phase = 0.0;
		for (int i = 0; i < count; i++)
		{
			float t = i / (float)rate;
			float envelope = Mathf.Clamp01(t / 0.035f) * Mathf.Pow(Mathf.Clamp01((duration - t) / 0.12f), 2f);
			float glide = t < 0.08f ? Mathf.Lerp(0.88f, 1f, t / 0.08f) : 1f;
			if (call && t > 0.42f)
			{
				glide *= Mathf.Lerp(0.82f, 0.62f, Mathf.Clamp01((t - 0.42f) / 0.45f));
				envelope *= Mathf.Clamp01((t - 0.42f) / 0.03f) * 0.9f + (t < 0.45f ? 0.1f : 0f);
			}
			float trill = 1f + 0.018f * Mathf.Sin(2f * Mathf.PI * 22f * t);
			phase += 2.0 * Math.PI * baseFrequency * glide * trill / rate;
			float tone = (float)(Math.Sin(phase) + 0.18 * Math.Sin(2.0 * phase) + 0.06 * Math.Sin(3.0 * phase));
			float breath = ((float)noise.NextDouble() * 2f - 1f) * 0.05f;
			data[i] = (tone * 0.55f + breath) * envelope * 0.6f;
		}
		AudioClip clip = AudioClip.Create("GC_Whistle_" + level, count, 1, rate, false);
		clip.SetData(data, 0);
		Clips[level] = clip;
		return clip;
	}
}

/// <summary>Tooltip einer Tierpfeife (Gorduan: kurz wie Valheims Werte-Zeilen): Befehlspause, Pfiff, Hinweis auf Verzauberungen.</summary>
[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), new Type[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool) })]
internal static class WhistleTooltipPatch
{
	private static void Postfix(ItemDrop.ItemData item, ref string __result)
	{
		if (item == null || !Whistles.IsWhistle(item, out WhistleTier tier))
		{
			return;
		}
		__result += "\n" + GcLoc.Get("gcwh_tooltip_pause", Rounded(tier.Reduction * 100f));
		__result += "\n" + GcLoc.Get("gcwh_tooltip_buff", Rounded(tier.Speed), Rounded(Whistles.BuffSeconds));
		__result += "\n" + GcLoc.Get("gcwh_tooltip_cooldown", Rounded(Whistles.BuffSeconds));
		if (tier.Valkyrie)
		{
			__result += "\n" + GcLoc.Get("gcwh_tooltip_valkyrie", Rounded(tier.ExtraLightning), Rounded(-tier.ExtraNormal));
			string status = GcHooks.CourierTooltip?.Invoke() ?? "";
			if (status.Length > 0)
			{
				__result += "\n" + status;
			}
		}
		if (Bonds.Of(item).AnimalAny)
		{
			__result += "\n" + GcLoc.Get("gcwh_tooltip_magic", Rounded(Whistles.BuffSeconds));
		}
	}

	/// <summary>Kaufmännisch gerundet (Mathf.RoundToInt rundet 12,5 auf 12).</summary>
	private static int Rounded(float value) => (int)Math.Floor(value + 0.5f);
}
