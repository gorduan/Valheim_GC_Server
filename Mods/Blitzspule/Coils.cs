using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Mods.Blitzspule;

/// <summary>
/// Drei Blitzspulen als Aufwertung für Valheims Vernichter (Prefab „incinerator“), Gorduan 27.10.2026. Lore: Die Spule leitet den
/// Strom des Vernichter-Blitzes ab, deshalb wird weniger zerstört und mehr wiederverwertet.
///
///  * ⅓ so groß wie der Vernichter (Modell E:\Blender\Blitzspule, 3,83 m, Low-Poly): Körper mit dem Material des Vernichters,
///    Wicklung, Akzentmetall und Kristall mit eigener 64-px-Textur (Assets\blitzspule_akzent.png).
///  * Spule I Berge (Drachenträne von Moder), II Nebellande (Königin), III Tiefer Norden (Kall Mandefimbul); Kosten je
///    6 Kupfer, 4 Metall des Bioms (Silber, Eitr, Gold) und das Boss-Item, gebaut mit dem Hammer an der Schmiede.
///  * Bauen nur in Reichweite (5 m) eines Vernichters, Spule II nur, wenn dort schon Spule I steht, III nur mit I und II.
///  * Es wirkt nur die lückenlose Kette ab Stufe I (I, I+II, I+II+III, siehe <see cref="Chain"/>): Doppelte Spulen und Spulen
///    über einer Lücke (z. B. III, nachdem I abgerissen wurde) zählen nicht (Review 27.10.2026).
///  * Wirkung (in <see cref="Recycling"/>): je Stufe der Kette ⅓ weniger Blitzschaden, mit allen drei keiner; die Spulen selbst
///    nehmen keinen Blitz- und Feuerschaden. Die höchste Stufe der Kette bestimmt die Rückgabe der Baumaterialien (25 / 55 / 85 %).
/// </summary>
internal static class Coils
{
	internal static readonly string[] PrefabNames = { "GC_Blitzspule1", "GC_Blitzspule2", "GC_Blitzspule3" };
	private static readonly (string Item, int Amount)[][] Costs =
	{
		new[] { ("Copper", 6), ("Silver", 4), ("DragonTear", 1) },
		new[] { ("Copper", 6), ("Eitr", 4), ("QueenDrop", 1) },
		new[] { ("Copper", 6), ("Gold", 4), ("FrozenKingDrop", 1) },
	};
	/// <summary>Farbe des Kristall-Lichts: Drachenträne, Eitr, Eis.</summary>
	private static readonly Color[] Glow = { new(0.45f, 0.7f, 1f), new(0.65f, 0.35f, 1f), new(0.8f, 0.92f, 1f) };
	/// <summary>Höhe des Kristalls über dem Boden (für Licht und überspringende Blitze).</summary>
	internal const float CrownHeight = 3.55f;

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<float> Range = null!;
	internal static readonly ConfigEntry<float>[] Rates = new ConfigEntry<float>[3];
	internal static ConfigEntry<float> EpicMin = null!;
	internal static ConfigEntry<float> EpicMax = null!;
	internal static readonly Piece?[] Pieces = new Piece?[3];
	/// <summary>Warum der Bauvorschlag gerade rot ist (Text für die Meldung beim Klick), sonst null.</summary>
	internal static string? GhostProblem;

	internal static void Init(ConfigFile config, ManualLogSource log)
	{
		Log = log;
		const string section = "1 - Blitzspule";
		Enabled = Gc.BindSynced(config, section, "Enabled", true, "Blitzspulen baubar und wirksam (vom Server vorgegeben).");
		Range = Gc.BindSynced(config, section, "Range", 5f, "Abstand in Metern, in dem eine Spule zu einem Vernichter gehört (wie Werkbank-Aufwertungen).", new AcceptableValueRange<float>(2f, 20f));
		float[] defaults = { 25f, 55f, 85f };
		for (int i = 0; i < 3; i++)
		{
			Rates[i] = Gc.BindSynced(config, section, "RateCoil" + (i + 1), defaults[i], $"Prozent der Baumaterialien, die Vernichtetes zurückgibt, wenn die Spulen-Kette am Vernichter (I, I+II, I+II+III) bis Blitzspule {Roman(i + 1)} reicht (immer abgerundet).", new AcceptableValueRange<float>(0f, 100f));
			Rates[i].SettingChanged += (_, _) => ApplyTexts();
		}
		EpicMin = Gc.BindSynced(config, section, "EpicLootMin", 50f, "EpicLoot-Items geben zusätzlich zufällig zwischen EpicLootMin und EpicLootMax Prozent ihrer Opfer-Produkte zurück (abgerundet). Spule II hebt die Untergrenze auf die Mitte, Handwerk anteilig.", new AcceptableValueRange<float>(0f, 100f));
		EpicMax = Gc.BindSynced(config, section, "EpicLootMax", 80f, "Obergrenze für EpicLoot-Items. Mit allen drei Spulen (I, II, III) am Vernichter oder Handwerk 100 beim Spieler am Hebel gilt immer die Obergrenze.", new AcceptableValueRange<float>(0f, 100f));
		Enabled.SettingChanged += (_, _) => ApplyEnabled();
		PrefabManager.OnVanillaPrefabsAvailable += Create;
	}

	internal static string Roman(int tier) => tier switch { 1 => "I", 2 => "II", _ => "III" };

	private static void Create()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= Create;
		GameObject? incinerator = PrefabManager.Instance.GetPrefab("incinerator");
		Material? body = incinerator == null ? null : incinerator.GetComponentsInChildren<MeshRenderer>(true)
			.Select(r => r.sharedMaterial).FirstOrDefault(m => m != null && m.name.StartsWith("Obliterator_mat"));
		Gcm? bodyModel = Gcm.Read("blitzspule_koerper.gcm");
		Texture2D? texture = LoadTexture("blitzspule_akzent.png");
		if (incinerator == null || body == null || bodyModel == null || texture == null)
		{
			Log.LogError("Vernichter (incinerator), sein Material oder das Spulenmodell fehlt: Blitzspulen werden nicht erstellt.");
			return;
		}
		Material accent = new(body) { name = "GC_Blitzspule_Akzent", mainTexture = texture };
		if (accent.HasProperty("_BumpMap"))
		{
			accent.SetTexture("_BumpMap", null); // die Normalen des Vernichters passen nicht zur eigenen Textur
		}
		for (int i = 0; i < 3; i++)
		{
			Gcm? accentModel = Gcm.Read($"blitzspule_akzent_{i + 1}.gcm");
			GameObject? prefab = accentModel == null ? null : Build(i + 1, Gcm.Combine(PrefabNames[i], bodyModel, accentModel), body, accent, incinerator);
			if (prefab == null)
			{
				Log.LogError($"Blitzspule {Roman(i + 1)} nicht erstellt.");
				continue;
			}
			Piece piece = prefab.GetComponent<Piece>();
			Pieces[i] = piece;
			piece.m_icon = Icon(prefab);
			PieceConfig config = new()
			{
				PieceTable = "_HammerPieceTable",
				Category = "Crafting",
				CraftingStation = "forge",
				Requirements = Costs[i].Select(c => new RequirementConfig(c.Item, c.Amount, 0, true)).ToArray()
			};
			PieceManager.Instance.AddPiece(new CustomPiece(prefab, true, config));
		}
		ApplyTexts();
		ApplyEnabled();
		Log.LogInfo($"Blitzspulen: {Pieces.Count(p => p != null)} Spulen angelegt (Schmiede, Hammer → Handwerk).");
	}

	/// <summary>
	/// Bauteil aus metalbar_1x2 (Materialtyp Eisen, Bau-Ebene). Dessen Modelle, Andockpunkte und Abnutzungsstufen fliegen raus;
	/// das Spulenmodell ist zugleich neu, abgenutzt und kaputt. metalbar_1x2 ist in Valheim ein Schwarzmarmor-Teil
	/// ($piece_blackmarble1x1) mit Marmor-Effekten; Bau-, Treffer- und Zerstörungseffekte kommen deshalb vom Vernichter selbst
	/// (Eisen: vfx_Place_chest + sfx_build_hammer_metal, vfx_HitSparks, vfx_HitSparks + sfx_metal_blocked; geprüft im Bundle
	/// c4210710 von Valheim 1.0.20, Review 27.10.2026).
	/// </summary>
	private static GameObject? Build(int tier, Mesh mesh, Material body, Material accent, GameObject incinerator)
	{
		GameObject? prefab = PrefabManager.Instance.CreateClonedPrefab(PrefabNames[tier - 1], "metalbar_1x2");
		if (prefab == null)
		{
			Log.LogError("metalbar_1x2 nicht gefunden.");
			return null;
		}
		int layer = prefab.layer;
		foreach (Transform child in prefab.transform.Cast<Transform>().ToList())
		{
			Object.DestroyImmediate(child.gameObject);
		}
		BoxCollider box = prefab.GetComponent<BoxCollider>() ?? prefab.AddComponent<BoxCollider>();
		box.center = new Vector3(0f, 0.21f, 0f);
		box.size = new Vector3(0.8f, 0.42f, 0.8f);
		CapsuleCollider column = prefab.AddComponent<CapsuleCollider>();
		column.direction = 1;
		column.center = new Vector3(0f, 2.05f, 0f);
		column.height = 3.3f;
		column.radius = 0.32f;

		GameObject model = new("new") { layer = layer };
		model.transform.SetParent(prefab.transform, false);
		model.AddComponent<MeshFilter>().sharedMesh = mesh;
		model.AddComponent<MeshRenderer>().sharedMaterials = new[] { body, accent };
		GameObject glow = new("glow") { layer = layer };
		glow.transform.SetParent(model.transform, false);
		glow.transform.localPosition = new Vector3(0f, CrownHeight, 0f);
		Light light = glow.AddComponent<Light>();
		light.type = LightType.Point;
		light.range = 2.5f;
		light.intensity = 0.8f;
		light.color = Glow[tier - 1];
		light.shadows = LightShadows.None;

		WearNTear wear = prefab.GetComponent<WearNTear>();
		if (wear != null)
		{
			wear.m_new = model;
			wear.m_worn = model;
			wear.m_broken = model;
			wear.m_health = 1000f; // wie der Vernichter
			wear.m_materialType = WearNTear.MaterialType.Iron;
			wear.m_damages.m_lightning = HitData.DamageModifier.Immune; // Gorduan: die Spulen nehmen den Blitz, aber keinen Schaden
			wear.m_damages.m_fire = HitData.DamageModifier.Immune;
			WearNTear? iron = incinerator.GetComponent<WearNTear>();
			if (iron != null)
			{
				wear.m_hitEffect = CopyEffects(iron.m_hitEffect);
				wear.m_destroyedEffect = CopyEffects(iron.m_destroyedEffect);
			}
		}
		Piece? piece = prefab.GetComponent<Piece>();
		Piece? ironPiece = incinerator.GetComponent<Piece>();
		if (piece != null && ironPiece != null)
		{
			piece.m_placeEffect = CopyEffects(ironPiece.m_placeEffect);
		}
		prefab.AddComponent<Coil>().Tier = tier;
		return prefab;
	}

	/// <summary>Eigene Effektliste mit denselben Einträgen; die des Vernichters bleibt unberührt.</summary>
	private static EffectList CopyEffects(EffectList? source) =>
		new() { m_effectPrefabs = source?.m_effectPrefabs?.ToArray() ?? new EffectList.EffectData[0] };

	private static Texture2D? LoadTexture(string file)
	{
		System.Reflection.Assembly assembly = typeof(Coils).Assembly;
		string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + file));
		if (resource == null)
		{
			return null;
		}
		using Stream stream = assembly.GetManifestResourceStream(resource)!;
		using MemoryStream memory = new();
		stream.CopyTo(memory);
		Texture2D texture = new(2, 2, TextureFormat.RGBA32, false) { name = "GC_Blitzspule_Akzent" };
		if (!texture.LoadImage(memory.ToArray()))
		{
			return null;
		}
		texture.filterMode = FilterMode.Point; // Pixel wie die Vernichter-Textur
		texture.wrapMode = TextureWrapMode.Clamp;
		return texture;
	}

	private static Sprite? Icon(GameObject prefab)
	{
		try
		{
			return RenderManager.Instance.Render(new RenderManager.RenderRequest(prefab) { Width = 128, Height = 128, Rotation = Quaternion.Euler(8f, 200f, 0f) });
		}
		catch (System.Exception e)
		{
			Log.LogWarning($"Symbol der Blitzspule nicht erzeugt: {e.Message}");
			return null;
		}
	}

	private static void ApplyTexts()
	{
		for (int i = 0; i < 3; i++)
		{
			Piece? piece = Pieces[i];
			if (piece == null)
			{
				continue;
			}
			piece.m_name = GcLoc.Get("gcbs_name", Roman(i + 1));
			piece.m_description = GcLoc.Get("gcbs_desc", Mathf.FloorToInt(Rates[i].Value).ToString());
		}
	}

	private static void ApplyEnabled()
	{
		foreach (Piece? piece in Pieces)
		{
			if (piece != null)
			{
				piece.m_enabled = Enabled.Value;
			}
		}
	}

	/// <summary>Nächster echter Vernichter (kein Bauvorschlag) in Reichweite von position.</summary>
	internal static Incinerator? NearestIncinerator(Vector3 position)
	{
		Incinerator? best = null;
		float bestDistance = Range.Value;
		Recycling.Incinerators.RemoveAll(i => i == null);
		foreach (Incinerator incinerator in Recycling.Incinerators)
		{
			if (incinerator.m_nview == null || !incinerator.m_nview.IsValid())
			{
				continue;
			}
			float distance = Vector3.Distance(incinerator.transform.position, position);
			if (distance <= bestDistance)
			{
				best = incinerator;
				bestDistance = distance;
			}
		}
		return best;
	}

	/// <summary>
	/// Wirksame Spulen eines Vernichters: die lückenlose Kette ab Stufe I (I, I+II, I+II+III), je Stufe die nächste gebaute Spule
	/// in Reichweite. Die Länge der Kette ist die beste Stufe. Doppelte Stufen zählen nicht, Spulen über einer Lücke wirken nicht
	/// (Review 27.10.2026: Die Kette galt nur beim Bauen; nach dem Abriss von I und II wirkte III allein mit 85 %, und
	/// 3× Spule I hielt den ganzen Blitz ab).
	/// </summary>
	internal static List<Coil> Chain(Vector3 incinerator)
	{
		Coil.All.RemoveAll(c => c == null);
		List<Coil> chain = new();
		for (int tier = 1; tier <= 3; tier++)
		{
			Coil? nearest = null;
			float nearestDistance = Range.Value;
			foreach (Coil coil in Coil.All)
			{
				if (coil.Tier != tier || !coil.Valid)
				{
					continue;
				}
				float distance = Vector3.Distance(coil.transform.position, incinerator);
				if (distance <= nearestDistance)
				{
					nearest = coil;
					nearestDistance = distance;
				}
			}
			if (nearest == null)
			{
				break;
			}
			chain.Add(nearest);
		}
		return chain;
	}

	/// <summary>Warum Spule tier hier nicht gebaut werden darf, sonst null. Spule II braucht die Kette bis I, III die bis II.</summary>
	internal static string? PlacementProblem(int tier, Vector3 position)
	{
		Incinerator? incinerator = NearestIncinerator(position);
		if (incinerator == null)
		{
			return GcLoc.Get("gcbs_need_incinerator", Range.Value.ToString("0.#"));
		}
		List<Coil> built = Chain(incinerator.transform.position);
		if (built.Count < tier - 1)
		{
			return GcLoc.Get("gcbs_need_previous", GcLoc.Get("gcbs_name", Roman(built.Count + 1))); // die erste fehlende Stufe
		}
		if (built.Count >= tier)
		{
			// Gorduan: drei verschiedene Spulen; eine zweite gleicher Stufe wirkte nicht (Review 27.10.2026), also gar nicht erst bauen
			return GcLoc.Get("gcbs_already", GcLoc.Get("gcbs_name", Roman(tier)));
		}
		return null;
	}
}

/// <summary>Merkt sich die Stufe der Spule und führt die Liste der gebauten Spulen.</summary>
internal sealed class Coil : MonoBehaviour
{
	internal static readonly List<Coil> All = new();
	[SerializeField]
	internal int Tier = 1;
	private ZNetView? nview;

	internal bool Valid => nview != null && nview.IsValid();

	private void Awake()
	{
		nview = GetComponent<ZNetView>();
		All.Add(this);
	}

	private void OnDestroy() => All.Remove(this);
}

/// <summary>
/// Bauvorschlag einer Spule ohne Vernichter in Reichweite (oder ohne die Spulen davor) wird rot. Läuft als letzter Postfix und
/// prüft auch, wenn der Status schon nicht Valid ist (Review 27.10.2026): ValheimPlus (Building.noInvalidPlacementRestriction,
/// noMysticalForcesPreventPlacementRestriction) setzt in seinem Postfix Invalid und NoBuildZone auf Valid zurück; lief diese
/// Prüfung vorher oder nur bei Valid, ließ sich die Spule ohne Vernichter bauen. ExtensionMissingStation setzt V+ nicht zurück.
/// </summary>
[HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
internal static class CoilPlacementPatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Player __instance)
	{
		Coils.GhostProblem = null;
		GameObject ghost = __instance.m_placementGhost;
		if (ghost == null || !ghost.activeSelf)
		{
			return; // kein Bauvorschlag sichtbar (z. B. Blick in den Himmel): Valheims eigener Grund bleibt
		}
		Coil? coil = ghost.GetComponent<Coil>();
		if (coil == null)
		{
			return;
		}
		string? problem = Coils.PlacementProblem(coil.Tier, ghost.transform.position);
		if (problem == null)
		{
			return;
		}
		Coils.GhostProblem = problem;
		__instance.m_placementStatus = Player.PlacementStatus.ExtensionMissingStation;
		__instance.SetPlacementGhostValid(false);
	}
}

/// <summary>Beim Klick auf einen roten Spulen-Vorschlag den eigenen Grund zeigen statt „Werkbank fehlt“.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.Message))]
internal static class CoilPlacementMessagePatch
{
	private static void Prefix(ref string msg)
	{
		if (Coils.GhostProblem != null && msg == "$msg_extensionmissingstation")
		{
			msg = Coils.GhostProblem;
		}
	}
}
