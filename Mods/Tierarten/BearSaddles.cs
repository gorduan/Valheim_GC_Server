using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GC.Shared;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Sättel (Gorduan, 20.10.2026).
///
/// Bärensattel (5 Bjørn-Haut, 10 Bronze) und Unbjørn-Sattel (5 Unbjørn-Rippen, 5 Bjørn-Haut, 20 Schwarzmetall), beide an der
/// Schmiede, nur wenn Bären zähmbar sind. Reiten wie beim Lox. Das Modell ist ein eigener Low-Poly-Sattel aus Blender
/// (E:\Blender\Baerensattel\build_baerensattel.py, 592 Dreiecke), angepasst an den vermessenen Bärenrücken (2,3 m hoch hinter
/// den Schultern, Krümmung etwa 0,55 m); der Unbjørn trägt dasselbe Modell 1,3-mal so groß. Der Bär hat keine eigenen
/// Sattel-Knochen wie der Lox, der Sattel sitzt deshalb starr am Rücken-Knochen „Spine_02“.
///
/// Seit dem Kern in GC_Tierarten (Modelle, Rezepte, Sitz am Tier); die Verzauberung der Sättel und das Mitnehmen der
/// Item-Daten beim Auf- und Absatteln macht GC_Tiere (Saddles), für alle Sättel.
/// </summary>
internal static class BearSaddles
{
	private sealed class Fit
	{
		public string Creature = "";
		public string Item = "";
		public string Name = "";
		public string Bone = "Spine_02";
		public Vector3 Origin;   // Sattel-Ursprung (Mitte der Auflage) im Raum der Kreatur, Boden = 0
		public Vector3 Attach;   // Sitzpunkt des Reiters
		public float Scale = 1f;
		public float Radius = 0.35f;
		public string Animation = "attach_asksvin";
		public Color Leather, Strap, Metal, Cloth;
		public (string Item, int Amount)[] Recipe = Array.Empty<(string, int)>();
		public CustomItem? Custom;
	}

	private static readonly Fit[] Fits =
	{
		new()
		{
			Creature = "Bjorn", Item = Saddles.BearSaddle, Name = "gc_saddle_bjorn", Origin = new Vector3(0f, 2.26f, -0.55f), Attach = new Vector3(0f, 2.46f, -0.62f),
			Scale = 1f, Radius = 0.35f, Animation = "attach_asksvin",
			Leather = new Color(0.45f, 0.28f, 0.15f), Strap = new Color(0.22f, 0.13f, 0.07f), Metal = new Color(0.72f, 0.50f, 0.24f), Cloth = new Color(0.50f, 0.13f, 0.10f),
			Recipe = new[] { ("BjornHide", 5), ("Bronze", 10) },
		},
		new()
		{
			Creature = "Unbjorn", Item = Saddles.UnbjornSaddle, Name = "gc_saddle_unbjorn", Origin = new Vector3(0f, 2.90f, -0.72f), Attach = new Vector3(0f, 3.16f, -0.80f),
			Scale = 1.3f, Radius = 0.45f, Animation = "attach_lox",
			Leather = new Color(0.20f, 0.17f, 0.15f), Strap = new Color(0.08f, 0.07f, 0.06f), Metal = new Color(0.36f, 0.38f, 0.42f), Cloth = new Color(0.62f, 0.60f, 0.55f),
			Recipe = new[] { ("UndeadBjornRibcage", 5), ("BjornHide", 5), ("BlackMetal", 20) },
		},
	};

	private static Mesh? model;
	private static bool modelLoaded;

	internal static void Init(ConfigFile config)
	{
		PrefabManager.OnVanillaPrefabsAvailable += Setup; // nach Species: Bär und Unbjørn haben dann ihr Tameable, das Junge ist schon geklont
	}

	internal static Mesh? Model()
	{
		if (!modelLoaded)
		{
			modelLoaded = true;
			model = Gcm.Read("baerensattel.gcm")?.ToMesh("GC_Baerensattel");
		}
		return model;
	}

	// ------------------------------------------------------------------ Vorlagen

	private static void Setup()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= Setup;
		try
		{
			Sadle? template = PrefabManager.Instance.GetPrefab("Lox")?.GetComponentInChildren<Sadle>(true);
			ItemDrop? loxSaddle = PrefabManager.Instance.GetPrefab("SaddleLox")?.GetComponent<ItemDrop>();
			Material? baseMaterial = null;
			if (loxSaddle != null)
			{
				foreach (Renderer renderer in loxSaddle.GetComponentsInChildren<Renderer>(true))
				{
					if (renderer is MeshRenderer or SkinnedMeshRenderer && renderer.sharedMaterial != null)
					{
						baseMaterial = renderer.sharedMaterial;
						break;
					}
				}
			}
			int made = 0;
			foreach (Fit fit in Fits)
			{
				Material? material = Gc.IsHeadless || Model() == null ? null : MaterialFor(fit, baseMaterial);
				if (CreateItem(fit, material) && AddToCreature(fit, template, material))
				{
					made++;
				}
			}
			ApplyRecipes();
			Species.Bear.Tameable!.SettingChanged += (_, _) => ApplyRecipes();
			ServerPlugin.Log.LogInfo($"Sättel: {made} Sättel für Bär und Unbjørn angelegt (Modell {(Model() != null ? "geladen" : "fehlt")}), Lox- und Asksvin-Sattel als EpicLoot-Items.");
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Sättel: nicht angelegt: {e}");
		}
	}

	internal static void ApplyRecipes()
	{
		foreach (Fit fit in Fits)
		{
			if (fit.Custom?.Recipe?.Recipe != null)
			{
				fit.Custom.Recipe.Recipe.m_enabled = Species.Enabled(Species.Bear); // Gorduan: nur, wenn Bären zähmbar sind
			}
		}
	}

	private static bool CreateItem(Fit fit, Material? material)
	{
		List<RequirementConfig> requirements = new();
		foreach ((string item, int amount) in fit.Recipe)
		{
			if (PrefabManager.Instance.GetPrefab(item) == null)
			{
				ServerPlugin.Log.LogInfo($"Sättel: Zutat {item} gibt es im Spiel nicht, {fit.Item} entfällt.");
				return false;
			}
			requirements.Add(new RequirementConfig(item, amount, 0, true));
		}
		string name = "$" + fit.Name;
		ItemConfig config = new()
		{
			Name = name,
			Description = name + "_desc",
			CraftingStation = CraftingStations.Forge,
			MinStationLevel = 1,
			Requirements = requirements.ToArray(),
		};
		CustomItem custom = new(fit.Item, "SaddleLox", config);
		ItemDrop.ItemData.SharedData shared = custom.ItemDrop.m_itemData.m_shared;
		shared.m_name = name;
		shared.m_description = name + "_desc";
		shared.m_maxStackSize = 1;
		Dress(custom.ItemDrop.gameObject, material);
		Sprite? icon = Icon(custom.ItemDrop.gameObject);
		if (icon != null)
		{
			shared.m_icons = new[] { icon };
		}
		if (!ItemManager.Instance.AddItem(custom))
		{
			ServerPlugin.Log.LogWarning($"Sättel: {fit.Item} konnte nicht registriert werden.");
			return false;
		}
		fit.Custom = custom;
		Saddles.AddName(name); // GC_Tiere: verzauberbar, Daten im Tier
		return true;
	}

	/// <summary>
	/// Das Sattel-Item am Boden: eigenes Modell statt des Lox-Sattels, Kollider in Modellgröße. Das Modell sitzt direkt am
	/// Item, nicht an einem Kind: Jötunns RenderManager rendert das Symbol nur aus aktiven Objekten (wie bei der Tierpfeife).
	/// </summary>
	private static void Dress(GameObject prefab, Material? material)
	{
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
		Mesh? mesh = Model();
		Bounds bounds = mesh != null ? mesh.bounds : new Bounds(new Vector3(0f, 0f, 0f), new Vector3(1.2f, 0.5f, 1f));
		BoxCollider box = prefab.AddComponent<BoxCollider>();
		box.center = bounds.center;
		box.size = bounds.size;
		if (mesh != null && material != null)
		{
			prefab.AddComponent<MeshFilter>().sharedMesh = mesh;
			prefab.AddComponent<MeshRenderer>().sharedMaterial = material;
		}
	}

	private static Sprite? Icon(GameObject prefab)
	{
		if (Gc.IsHeadless || Model() == null)
		{
			return null;
		}
		try
		{
			Quaternion rotation = Quaternion.AngleAxis(-25f, Vector3.right) * Quaternion.AngleAxis(-40f, Vector3.up);
			return RenderManager.Instance.Render(new RenderManager.RenderRequest(prefab) { Width = 128, Height = 128, Rotation = rotation });
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Sättel: Symbol nicht gerendert: {e.Message}");
			return null;
		}
	}

	/// <summary>Valheims Material des Lox-Sattels mit einer eigenen kleinen Pixel-Textur (Leder, Gurte, Metall, Decke).</summary>
	private static Material MaterialFor(Fit fit, Material? baseMaterial)
	{
		Material material = baseMaterial != null ? new Material(baseMaterial) : new Material(Shader.Find("Standard") ?? Shader.Find("Diffuse"));
		material.name = fit.Item + "_mat";
		material.mainTexture = Atlas(fit);
		if (material.HasProperty("_BumpMap"))
		{
			material.SetTexture("_BumpMap", null);
		}
		return material;
	}

	/// <summary>64 x 64 Pixel wie im Blender-Skript: u 0..0,5 Leder, 0,5..0,75 Gurte, 0,75..1 unten Metall, oben Decke.</summary>
	private static Texture2D Atlas(Fit fit)
	{
		const int size = 64;
		Texture2D texture = new(size, size, TextureFormat.RGBA32, false) { name = fit.Item + "_tex", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
		System.Random random = new(fit.Item.GetStableHashCode());
		Color[] pixels = new Color[size * size];
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				float u = (x + 0.5f) / size;
				float v = (y + 0.5f) / size;
				float noise = (float)random.NextDouble() * 0.08f - 0.04f;
				Color c;
				if (u < 0.5f)
				{
					c = fit.Leather * (1f + noise);
					if (x % 32 == 1 || y % 16 == 1)
					{
						c *= 0.75f; // Nähte
					}
				}
				else if (u < 0.75f)
				{
					c = fit.Strap * (1f + noise);
					if (x == 33 || x == 46)
					{
						c = Color.Lerp(c, fit.Leather, 0.5f); // Ziernaht am Gurt
					}
				}
				else if (v < 0.5f)
				{
					c = fit.Metal * (1f + noise * 1.5f);
					if ((x + y) % 7 == 0)
					{
						c *= 1.25f; // Glanzlicht
					}
				}
				else
				{
					c = fit.Cloth * (1f + noise);
					if ((y / 4) % 3 == 0)
					{
						c = Color.Lerp(c, fit.Metal, 0.35f); // Webmuster der Decke
					}
				}
				c.a = 1f;
				pixels[y * size + x] = c;
			}
		}
		texture.SetPixels(pixels);
		texture.Apply(true);
		return texture;
	}

	/// <summary>
	/// Sattel am Tier: Kind des Rücken-Knochens (bewegt sich mit), Lage aus dem vermessenen Rücken, Kollider zum Aufsitzen,
	/// Sitzpunkt, Valheims Sadle mit den Werten des Lox-Sattels. Ausgeschaltet, bis ein Sattel aufgelegt wird.
	/// </summary>
	private static bool AddToCreature(Fit fit, Sadle? template, Material? material)
	{
		GameObject? creature = PrefabManager.Instance.GetPrefab(fit.Creature);
		Tameable? tame = creature != null ? creature.GetComponent<Tameable>() : null;
		Transform? bone = creature != null ? Utils.FindChild(creature.transform, fit.Bone) : null;
		if (creature == null || tame == null || bone == null || fit.Custom == null)
		{
			ServerPlugin.Log.LogWarning($"Sättel: {fit.Creature} ohne Zähm-Komponente oder Knochen {fit.Bone}, kein Sattel.");
			return false;
		}
		GameObject saddle = new("GC_Saddle") { layer = creature.layer };
		saddle.SetActive(false);
		saddle.transform.SetParent(bone, false);
		saddle.transform.position = creature.transform.TransformPoint(fit.Origin);
		saddle.transform.rotation = creature.transform.rotation;
		Vector3 boneScale = bone.lossyScale;
		saddle.transform.localScale = new Vector3(fit.Scale / Mathf.Max(0.0001f, boneScale.x), fit.Scale / Mathf.Max(0.0001f, boneScale.y), fit.Scale / Mathf.Max(0.0001f, boneScale.z));
		if (Model() is Mesh mesh && material != null)
		{
			saddle.AddComponent<MeshFilter>().sharedMesh = mesh;
			saddle.AddComponent<MeshRenderer>().sharedMaterial = material;
		}
		SphereCollider collider = saddle.AddComponent<SphereCollider>();
		collider.center = new Vector3(0f, 0.12f, 0f);
		collider.radius = fit.Radius;
		GameObject attach = new("GC_SaddleAttach") { layer = creature.layer };
		attach.transform.SetParent(saddle.transform, false);
		attach.transform.position = creature.transform.TransformPoint(fit.Attach);
		attach.transform.rotation = creature.transform.rotation;
		Sadle sadle = saddle.AddComponent<Sadle>();
		if (template != null)
		{
			sadle.m_maxUseRange = template.m_maxUseRange;
			sadle.m_detachOffset = template.m_detachOffset;
			sadle.m_maxStamina = template.m_maxStamina;
			sadle.m_runStaminaDrain = template.m_runStaminaDrain;
			sadle.m_swimStaminaDrain = template.m_swimStaminaDrain;
			sadle.m_staminaRegen = template.m_staminaRegen;
			sadle.m_staminaRegenHungry = template.m_staminaRegenHungry;
			sadle.m_drownEffects = template.m_drownEffects;
			sadle.m_mountIcon = template.m_mountIcon;
			sadle.m_hoverOffset = template.m_hoverOffset;
		}
		sadle.m_hoverText = "$" + fit.Name;
		sadle.m_attachPoint = attach.transform;
		sadle.m_attachAnimation = fit.Animation;
		tame.m_saddle = sadle;
		tame.m_saddleItem = fit.Custom.ItemDrop;
		tame.m_dropSaddleOnDeath = true;
		tame.m_dropSaddleOffset = new Vector3(0f, 1.5f * fit.Scale, 0f);
		return true;
	}
}
