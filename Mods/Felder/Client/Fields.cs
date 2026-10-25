using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.Rendering;

namespace GC.Client.Fields;

/// <summary>Farbe der Felder (Gorduan, 16.10.2026: in den Optionen wählbar).</summary>
public enum FieldColor
{
	Yellow,
	Orange,
	Red,
	Green,
	Turquoise,
	Blue,
	Purple,
	White
}

/// <summary>
/// Felder für den Kultivator (Gorduan, 16.10.2026), Client-Teil. Das Welt-Objekt und die Regeln kommen aus GC_Server.
///  * Kultivator-Menü „Feld markieren“: Linksklick setzt Eckpunkte, ein Klick auf den Startpunkt schließt das Feld.
///    Rechtsklick nimmt beim Zeichnen den letzten Punkt zurück, sonst hebt er das Feld unter dem Fadenkreuz auf (mit
///    Rückfrage, die Pflanzen bleiben). Esc bricht das Zeichnen ab.
///  * Felder sieht man gelb-durchsichtig (Valheim-Gelb, nachts gedämpft), solange der Kultivator in der Hand ist.
///  * Pflanzen im Feld: FieldPlanting (nächster freier Platz in Reihen parallel zur ersten Kante, E halten).
/// Kanten dürfen sich nicht kreuzen, Felder sich nicht überschneiden; Eckpunkte und Kantenlänge begrenzt der Server.
/// </summary>
internal static class Plugin
{
	internal const string MarkerName = "GC_FieldMarker";
	private const string CultivatorTable = "_CultivatorPieceTable";
	private const float CloseRadius = 1f;

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<FieldColor> Tint = null!;
	internal static ConfigEntry<int> FillOpacity = null!;
	internal static ConfigEntry<int> LineOpacity = null!;

	/// <summary>Eckpunkte des Feldes, das gerade gezeichnet wird.</summary>
	internal static readonly List<Vector3> Draft = new();

	private static LineRenderer? draftLine;
	private static bool shownFields;
	private static float nextHint;
	private static Piece? lastSelected;

	/// <summary>Der Menüeintrag; ist er aus (Felder aus oder GC_Server fehlt), verschwindet er aus dem Kultivator.</summary>
	internal static Piece? MarkerPiece;

	/// <summary>So weit darf eine Ecke vom Mittelpunkt liegen: Das Feld-Objekt lädt nur im Umkreis des Spielers.</summary>
	private const float MaxRadius = 100f;

	internal static bool PrefabAvailable => ZNetScene.instance != null && ZNetScene.instance.GetPrefab(GcFields.Prefab) != null;

	/// <summary>Felder an: hier eingeschaltet, vom Server erlaubt und das Welt-Objekt aus GC_Server vorhanden.</summary>
	internal static bool On => Enabled.Value && GcFields.Get("enabled", true) && PrefabAvailable && ClientPlugin.Allowed && ClientPlugin.OnServer;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ClientPlugin.Log;
		Enabled = Gc.Bind(Config, "12 - Fields", "Enabled", true, "Felder mit dem Kultivator anzeigen, markieren und in Reihen bepflanzen (die Regeln gibt GC_Server vor).");
		Tint = Gc.Bind(Config, "12 - Fields", "Color", FieldColor.Yellow, "Farbe von Fläche und Rand (Yellow = Valheim-Gelb).");
		FillOpacity = Gc.Bind(Config, "12 - Fields", "FillOpacity", 10, "Deckkraft der Fläche in Prozent (nachts automatisch gedämpft).", new AcceptableValueRange<int>(0, 100));
		LineOpacity = Gc.Bind(Config, "12 - Fields", "LineOpacity", 55, "Deckkraft des Rands in Prozent.", new AcceptableValueRange<int>(0, 100));
		Tint.SettingChanged += (_, _) => Materials.Changed();
		FillOpacity.SettingChanged += (_, _) => Materials.Changed();
		LineOpacity.SettingChanged += (_, _) => Materials.Changed();
		PrefabManager.OnVanillaPrefabsAvailable += CreateMarker;
		PrefabManager.OnPrefabsRegistered += AttachView;
	}

	/// <summary>Jedes Feld-Objekt von GC_Server bekommt die Anzeige von GC_Client (nach Jötunns Eintrag in die Szene).</summary>
	private static void AttachView()
	{
		Planting.ResetCaches();
		GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(GcFields.Prefab) : null;
		if (prefab == null)
		{
			Log.LogWarning("Felder: Welt-Objekt " + GcFields.Prefab + " fehlt (GC_Server zu alt oder nicht installiert), Felder bleiben aus.");
			return;
		}
		if (prefab.GetComponent<FieldView>() == null)
		{
			prefab.AddComponent<FieldView>();
		}
	}

	/// <summary>Kultivator in der Hand?</summary>
	internal static bool HoldsCultivator(Player? player)
	{
		ItemDrop.ItemData? item = player != null ? player.GetRightItem() : null;
		PieceTable? table = item?.m_shared?.m_buildPieces;
		return table != null && table.name == CultivatorTable;
	}

	internal static bool IsMarker(Piece? piece) => piece != null && piece.GetComponent<FieldMarker>() != null;

	/// <summary>
	/// Menüeintrag „Feld markieren“: Kopie von cultivate_v2 ohne Geländeänderung (sonst bestellte der Klick den Boden,
	/// und EarthWright hielte den Eintrag für einen seiner Pinsel), ohne Kosten. Geklickt wird über TryPlacePiece.
	/// </summary>
	private static void CreateMarker()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= CreateMarker;
		GameObject? marker = PrefabManager.Instance.CreateClonedPrefab(MarkerName, "cultivate_v2");
		if (marker == null)
		{
			Log.LogError("Felder: cultivate_v2 nicht gefunden, kein Menüeintrag „Feld markieren“.");
			return;
		}
		foreach (TerrainOp op in marker.GetComponentsInChildren<TerrainOp>(true))
		{
			Object.DestroyImmediate(op);
		}
		foreach (TerrainModifier modifier in marker.GetComponentsInChildren<TerrainModifier>(true))
		{
			Object.DestroyImmediate(modifier);
		}
		marker.AddComponent<FieldMarker>();
		Piece piece = marker.GetComponent<Piece>();
		MarkerPiece = piece;
		piece.m_name = GcLoc.Get("gccf_marker_name");
		piece.m_description = GcLoc.Get("gccf_marker_desc");
		piece.m_icon = Icon();
		piece.m_resources = new Piece.Requirement[0];
		piece.m_craftingStation = null;
		piece.m_cultivatedGroundOnly = false;
		piece.m_vegetationGroundOnly = false;
		piece.m_onlyInBiome = Heightmap.Biome.None;
		PieceManager.Instance.AddPiece(new CustomPiece(marker, true, new PieceConfig { PieceTable = CultivatorTable }));
		Log.LogInfo("Felder: Menüeintrag „Feld markieren“ im Kultivator angelegt.");
	}

	/// <summary>Grünes Feld mit Eckpunkten als Symbol.</summary>
	private static Sprite Icon()
	{
		const int size = 64;
		Texture2D texture = new(size, size, TextureFormat.RGBA32, false);
		Vector2[] corners = { new(10, 14), new(54, 10), new(50, 54), new(14, 46) };
		List<Vector3> polygon = new();
		foreach (Vector2 corner in corners)
		{
			polygon.Add(new Vector3(corner.x, 0f, corner.y));
		}
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				Color color = new(0f, 0f, 0f, 0f);
				if (Geometry.Contains(polygon, x + 0.5f, y + 0.5f))
				{
					color = new Color(0.3f, 0.8f, 0.3f, 0.6f);
					if ((y / 6) % 2 == 0 && x % 6 < 3)
					{
						color = new Color(0.45f, 0.3f, 0.15f, 1f); // Saatreihen
					}
				}
				if (Geometry.EdgeDistance(polygon, new Vector3(x + 0.5f, 0f, y + 0.5f)) < 1.6f)
				{
					color = new Color(0.4f, 1f, 0.4f, 1f);
				}
				foreach (Vector2 corner in corners)
				{
					if (Vector2.Distance(corner, new Vector2(x, y)) < 3.5f)
					{
						color = new Color(1f, 0.9f, 0.3f, 1f);
					}
				}
				texture.SetPixel(x, y, color);
			}
		}
		texture.Apply();
		return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
	}

	// ------------------------------------------------------------------ Jedes Bild

	internal static void Update()
	{
		Player? player = Player.m_localPlayer;
		if (MarkerPiece != null && player != null && MarkerPiece.m_enabled != On)
		{
			MarkerPiece.m_enabled = On; // Server schaltet Felder aus, oder GC_Server fehlt
			player.UpdateAvailablePiecesList();
		}
		bool cultivator = player != null && On && HoldsCultivator(player);
		if (cultivator)
		{
			Materials.Tick(); // nachts dunkler, damit die Felder nicht leuchten
		}
		ShowFields(cultivator);
		if (!cultivator)
		{
			if (Draft.Count > 0)
			{
				Draft.Clear(); // Kultivator weggelegt: Zeichnung verwerfen
			}
			UpdateDraftLine(null);
			Planting.Update(null, null);
			lastSelected = null;
			return;
		}
		Piece? selected = player!.GetSelectedPiece();
		if (selected != lastSelected)
		{
			lastSelected = selected;
			Hint(selected);
		}
		UpdateDraftLine(IsMarker(selected) ? player : null);
		Planting.Update(player, selected);
	}

	private static void ShowFields(bool show)
	{
		if (!show && !shownFields)
		{
			return;
		}
		shownFields = show;
		foreach (FieldView view in FieldView.All)
		{
			if (view != null)
			{
				view.Show(show);
			}
		}
	}

	private static void Hint(Piece? selected)
	{
		if (Time.time < nextHint || selected == null)
		{
			return;
		}
		if (IsMarker(selected))
		{
			nextHint = Time.time + 60f;
			Gc.TopLeft(GcLoc.Get("gccf_hint_marker"));
		}
		else if (Planting.IsPlantable(selected) && !Planting.PlantEasily && Nearest(Player.m_localPlayer!.transform.position, 30f) != null)
		{
			nextHint = Time.time + 120f;
			Gc.TopLeft(GcLoc.Get("gccf_hint_plant"));
		}
	}

	/// <summary>Linie der Zeichnung: gesetzte Punkte und das Stück bis zum Fadenkreuz.</summary>
	private static void UpdateDraftLine(Player? player)
	{
		if (player == null || Draft.Count == 0)
		{
			if (draftLine != null)
			{
				draftLine.enabled = false;
			}
			return;
		}
		if (draftLine == null)
		{
			GameObject holder = new("GC_FieldDraft");
			Object.DontDestroyOnLoad(holder);
			draftLine = holder.AddComponent<LineRenderer>();
			draftLine.useWorldSpace = true;
			draftLine.widthMultiplier = 0.07f;
			draftLine.shadowCastingMode = ShadowCastingMode.Off;
			draftLine.receiveShadows = false;
			draftLine.material = Materials.Draft;
		}
		List<Vector3> points = new();
		foreach (Vector3 point in Draft)
		{
			points.Add(point + Vector3.up * 0.15f);
		}
		GameObject? ghost = player.m_placementGhost;
		if (ghost != null && ghost.activeSelf)
		{
			points.Add(ghost.transform.position + Vector3.up * 0.15f);
		}
		draftLine.enabled = true;
		draftLine.positionCount = points.Count;
		draftLine.SetPositions(points.ToArray());
	}

	// ------------------------------------------------------------------ Zeichnen

	/// <summary>Linksklick mit „Feld markieren“: Punkt setzen oder das Feld am Startpunkt schließen.</summary>
	internal static void Click(Player player)
	{
		GameObject? ghost = player.m_placementGhost;
		if (ghost == null || !ghost.activeSelf)
		{
			return;
		}
		Vector3 point = ghost.transform.position;
		point.y = Geometry.Ground(point);
		if (Draft.Count > 0 && Flat(point, Draft[0]) <= CloseRadius)
		{
			if (Draft.Count >= 3)
			{
				Close(player);
			}
			else
			{
				Gc.TopLeft(GcLoc.Get("gccf_need_three")); // sonst entstünde ein Punkt direkt am Start, der das Schließen verhindert
			}
			return;
		}
		if (Draft.Count == 0 && At(point) != null)
		{
			Gc.TopLeft(GcLoc.Get("gccf_inside_other")); // hier liegt schon ein Feld
			return;
		}
		int maxPoints = GcFields.Get("maxPoints", 32);
		if (maxPoints > 0 && Draft.Count >= maxPoints)
		{
			Gc.TopLeft(GcLoc.Get("gccf_too_many", maxPoints));
			return;
		}
		if (Draft.Count > 0 && !EdgeAllowed(Draft[Draft.Count - 1], point, Draft.Count - 1))
		{
			return;
		}
		Draft.Add(point);
	}

	private static float Flat(Vector3 a, Vector3 b)
	{
		a.y = b.y = 0f;
		return Vector3.Distance(a, b);
	}

	/// <summary>Neue Kante prüfen: Länge, keine Kreuzung mit den bisherigen Kanten (außer der Nachbarkante), kein anderes Feld.</summary>
	private static bool EdgeAllowed(Vector3 from, Vector3 to, int fromIndex, bool closing = false)
	{
		float maxEdge = GcFields.Get("maxEdge", 50f);
		if (maxEdge > 0f && Flat(from, to) > maxEdge)
		{
			Gc.TopLeft(GcLoc.Get("gccf_too_long", maxEdge.ToString("0.#")));
			return false;
		}
		if (Flat(from, to) < 0.3f)
		{
			return false; // Doppelklick auf dieselbe Stelle
		}
		for (int i = 0; i + 1 < Draft.Count; i++)
		{
			bool neighbour = i == fromIndex - 1 || i + 1 == fromIndex || (closing && i == 0);
			if (!neighbour && Geometry.Crosses(Draft[i], Draft[i + 1], from, to))
			{
				Gc.TopLeft(GcLoc.Get("gccf_crossing"));
				return false;
			}
		}
		foreach (FieldView view in FieldView.All)
		{
			Field? other = view != null ? view.Data : null;
			if (other == null)
			{
				continue;
			}
			if (other.Contains(to))
			{
				Gc.TopLeft(GcLoc.Get("gccf_overlap"));
				return false;
			}
			for (int i = 0, j = other.Points.Count - 1; i < other.Points.Count; j = i++)
			{
				if (Geometry.Crosses(other.Points[j], other.Points[i], from, to))
				{
					Gc.TopLeft(GcLoc.Get("gccf_overlap"));
					return false;
				}
			}
		}
		return true;
	}

	private static void Close(Player player)
	{
		if (!EdgeAllowed(Draft[Draft.Count - 1], Draft[0], Draft.Count - 1, true))
		{
			return;
		}
		List<Vector3> points = new(Draft);
		if (Mathf.Abs(Geometry.SignedArea(points)) < 1f)
		{
			Gc.TopLeft(GcLoc.Get("gccf_too_small"));
			return;
		}
		Vector3 middle = Field.Centroid(points);
		foreach (Vector3 corner in points)
		{
			if (Flat(corner, middle) > MaxRadius)
			{
				Gc.TopLeft(GcLoc.Get("gccf_too_big", MaxRadius.ToString("0")));
				return;
			}
		}
		foreach (FieldView view in FieldView.All)
		{
			Field? other = view != null ? view.Data : null;
			if (other != null && Geometry.Overlaps(points, other.Points))
			{
				Gc.TopLeft(GcLoc.Get("gccf_overlap"));
				return;
			}
		}
		GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(GcFields.Prefab) : null;
		if (prefab == null)
		{
			Gc.TopLeft(GcLoc.Get("gccf_no_server"));
			return;
		}
		Vector3 centre = Field.Centroid(points);
		centre.y = Geometry.Ground(centre);
		GameObject created = Object.Instantiate(prefab, centre, Quaternion.identity);
		ZNetView? view2 = created.GetComponent<ZNetView>();
		if (view2 == null || !view2.IsValid())
		{
			Object.Destroy(created);
			return;
		}
		Field.Write(view2.GetZDO(), points, player.GetPlayerID(), player.GetPlayerName(), GcGuildInfo.OwnGuildId());
		Draft.Clear();
		Gc.TopLeft(GcLoc.Get("gccf_closed", Mathf.RoundToInt(Mathf.Abs(Geometry.SignedArea(points)))));
	}

	/// <summary>Rechtsklick mit „Feld markieren“: beim Zeichnen den letzten Punkt zurücknehmen, sonst das Feld unter dem Fadenkreuz aufheben.</summary>
	internal static bool RightClick(Player player)
	{
		if (Draft.Count > 0)
		{
			Draft.RemoveAt(Draft.Count - 1);
			return true;
		}
		FieldView? view = AimedField(player);
		if (view == null)
		{
			return false; // kein Feld: Rechtsklick öffnet wie immer das Baumenü
		}
		Field? data = view.Data;
		if (data == null)
		{
			return false;
		}
		string? denied = Denied(player, data);
		if (denied != null)
		{
			Gc.TopLeft(denied);
			return true;
		}
		UnifiedPopup.Push(new YesNoPopup(GcLoc.Get("gccf_remove_title"), GcLoc.Get("gccf_remove_text"), () =>
		{
			UnifiedPopup.Pop();
			Remove(view);
		}, UnifiedPopup.Pop, false));
		return true;
	}

	/// <summary>Regel des Servers: Wer darf dieses Feld aufheben? null = erlaubt, sonst die Meldung.</summary>
	private static string? Denied(Player player, Field data)
	{
		bool admin = GcMenu.CanChangeServer;
		bool creator = data.Creator != 0L && data.Creator == player.GetPlayerID();
		bool guildLeader = data.Guild >= 0 && GcGuildInfo.OwnGuildId() == data.Guild && GcGuildInfo.IsLeader();
		switch (GcFields.Get("removeBy", GcFields.RemoveEveryone))
		{
			case GcFields.RemoveCreatorOrAdmin:
				return creator || admin ? null : GcLoc.Get("gccf_denied_creator");
			case GcFields.RemoveCreatorGuildOrAdmin:
				return creator || admin || guildLeader ? null : GcLoc.Get("gccf_denied_guild");
			case GcFields.RemoveAdminOnly:
				return admin ? null : GcLoc.Get("gccf_denied_admin");
			default:
				return null;
		}
	}

	private static void Remove(FieldView view)
	{
		if (view == null)
		{
			return;
		}
		ZNetView? nview = view.View;
		if (nview != null && nview.IsValid())
		{
			nview.ClaimOwnership(); // nur der Besitzer löscht das ZDO
			ZNetScene.instance.Destroy(view.gameObject);
			Gc.TopLeft(GcLoc.Get("gccf_removed"));
		}
	}

	/// <summary>Esc beim Zeichnen: Zeichnung verwerfen (statt das Menü zu öffnen).</summary>
	internal static bool CancelDraft()
	{
		Player? player = Player.m_localPlayer;
		if (Draft.Count == 0 || player == null || !IsMarker(player.GetSelectedPiece()) || InventoryGui.IsVisible() || Minimap.IsOpen()
			|| global::Console.IsVisible() || TextInput.IsVisible() || StoreGui.IsVisible() || Hud.IsPieceSelectionVisible() || UnifiedPopup.IsVisible()
			|| Hud.InRadial() || (Chat.instance != null && Chat.instance.m_wasFocused))
		{
			return false; // Esc schließt gerade etwas anderes (wie in Menu.Update)
		}
		Draft.Clear();
		Gc.TopLeft(GcLoc.Get("gccf_cancel"));
		return true;
	}

	/// <summary>
	/// Feld unter dem Fadenkreuz, wie Valheims Bau-Strahl (PieceRayTest): erster Treffer, höchstens Bau-Reichweite vom Auge.
	/// Trifft er eine Pflanze im Feld, zählt ihre Stelle (die Vorschau von „Feld markieren“ ist dort unsichtbar).
	/// </summary>
	private static FieldView? AimedField(Player player)
	{
		if (GameCamera.instance == null)
		{
			return null;
		}
		Transform camera = GameCamera.instance.transform;
		if (!Physics.Raycast(camera.position, camera.forward, out RaycastHit hit, 50f, player.m_placeRayMask))
		{
			return null;
		}
		Piece? marker = player.m_placementGhost != null ? player.m_placementGhost.GetComponent<Piece>() : null;
		float reach = player.m_maxPlaceDistance + (marker != null ? marker.m_extraPlacementDistance : 0);
		return Vector3.Distance(player.m_eye.position, hit.point) <= reach ? At(hit.point) : null;
	}

	/// <summary>Feld an dieser Stelle (nur geladene).</summary>
	internal static FieldView? At(Vector3 point)
	{
		foreach (FieldView view in FieldView.All)
		{
			Field? data = view != null ? view.Data : null;
			if (data != null && point.x >= data.MinX && point.x <= data.MaxX && point.z >= data.MinZ && point.z <= data.MaxZ && data.Contains(point))
			{
				return view;
			}
		}
		return null;
	}

	/// <summary>Nächstes Feld, dessen Rand höchstens so weit weg ist (oder in dem man steht).</summary>
	internal static FieldView? Nearest(Vector3 point, float range)
	{
		FieldView? best = null;
		float bestDistance = range;
		foreach (FieldView view in FieldView.All)
		{
			Field? data = view != null ? view.Data : null;
			if (data == null || point.x < data.MinX - range || point.x > data.MaxX + range || point.z < data.MinZ - range || point.z > data.MaxZ + range)
			{
				continue;
			}
			float distance = data.Contains(point) ? 0f : data.EdgeDistance(point);
			if (distance <= bestDistance)
			{
				best = view;
				bestDistance = distance;
			}
		}
		return best;
	}
}

/// <summary>Kennzeichen des Menüeintrags „Feld markieren“.</summary>
public class FieldMarker : MonoBehaviour
{
}

/// <summary>Klick mit „Feld markieren“: nichts bauen, sondern einen Eckpunkt setzen (keine Kosten, keine Ausdauer).</summary>
[HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
internal static class FieldClickPatch
{
	[HarmonyPriority(Priority.First)]
	private static bool Prefix(Player __instance, Piece piece, ref bool __result)
	{
		if (__instance != Player.m_localPlayer || !Plugin.IsMarker(piece))
		{
			return true;
		}
		__result = false;
		if (Plugin.On)
		{
			Plugin.Click(__instance);
		}
		return false;
	}
}

/// <summary>Rechtsklick mit „Feld markieren“: Punkt zurück oder Feld aufheben, sonst wie immer das Baumenü.</summary>
[HarmonyPatch(typeof(Player), "UpdateBuildGuiInput")]
internal static class FieldRightClickPatch
{
	private static bool Prefix(Player __instance)
	{
		if (__instance != Player.m_localPlayer || !Plugin.On || !ZInput.GetButtonDown("BuildMenu") || Hud.IsPieceSelectionVisible()
			|| PlayerController.HasInputDelay || Hud.InRadial()
			|| !Plugin.IsMarker(__instance.GetSelectedPiece()))
		{
			return true;
		}
		return !Plugin.RightClick(__instance);
	}
}

/// <summary>Esc beim Zeichnen bricht die Zeichnung ab, statt das Menü zu öffnen.</summary>
[HarmonyPatch(typeof(Menu), "Update")]
internal static class FieldEscapePatch
{
	private static bool Prefix()
	{
		return !(ZInput.GetKeyDown(KeyCode.Escape) && !Menu.IsVisible() && Plugin.CancelDraft()); // CancelDraft prüft wie Valheim, ob Esc etwas anderes schließt
	}
}
