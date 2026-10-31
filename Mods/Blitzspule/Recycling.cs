using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Bootstrap;
using GC.Shared;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GC.Mods.Blitzspule;

/// <summary>
/// Was die Blitzspulen am Vernichter bewirken (Gorduan, 27.10.2026). Valheims Vernichter (Incinerator) läuft beim Besitzer seines
/// Netzobjekts: Hebel → nach 5–7 s der Blitz (m_lightingAOEs), dann Umwandlungen, dann Inventory.RemoveAll und die Ergebnisse.
/// Es wirkt nur die lückenlose Kette der Spulen (I, I+II, I+II+III, <see cref="Coils.Chain"/>); ihre Länge ist die beste Stufe.
///  * Blitz: je Stufe der Kette ⅓ weniger Schaden und Wucht, mit allen drei trifft er nichts. Das prüft jeder Rechner selbst in
///    Aoe.Awake an der Position (Wurzel ist das Blitz-Prefab des Vernichters und steht genau auf ihm), nicht nur der Besitzer im
///    Lauf (Review 27.10.2026). Alle Spieler sehen Blitze in die Kronen der wirkenden Spulen.
///  * Während des Schritts der Coroutine Incinerate (MoveNext) gilt beim Besitzer ein „Lauf“ mit der Kette. Vor RemoveAll:
///    Rückgabe für alles, was nach Valheims Umwandlungen noch im Vernichter liegt. Rezeptkosten (siehe <see cref="AddRecipe"/>,
///    Stapel anteilig) × Satz der besten Stufe (25 / 55 / 85 %), nicht bei gesetztem Weltschlüssel NoCraftCost. EpicLoot-Items
///    dazu ihre Opfer-Produkte (EpicLoot.API.GetSacrificeProductsJson) × Zufall zwischen Untergrenze und EpicLootMax; die
///    Untergrenze liegt bei Lerp(EpicLootMin, EpicLootMax, q) mit q = max((Kettenlänge − 1) / 2, Handwerk / 100), mit allen drei
///    Spulen oder Handwerk 100 gilt also der Höchstwert. Jede Sorte wird über die ganze Ladung zusammengezählt und abgerundet
///    (Gorduan: immer abgerundet).
///  * Nach dem Schritt: die Rückgabe in den Vernichter, was nicht passt, fällt daneben; der Spieler am Hebel bekommt eine Zeile.
/// Den Skill kennt nur der Spieler am Hebel: Er schickt ihn per RPC an den Besitzer, bevor Valheim den Vernichter anfordert.
/// EpicLoot 0.15 hat keinen eigenen Verzauberungs-Skill mehr, daher zählt Valheims Handwerk (Crafting).
/// </summary>
internal static class Recycling
{
	internal const string EpicLootGuid = "randyknapp.mods.epicloot";
	private const string RpcSkill = "GC_Blitzspule_Skill";
	private const string RpcInfo = "GC_Blitzspule_Info";
	private const string RpcBolts = "GC_Blitzspule_Bolts";
	/// <summary>Höchstens so viele Blitze aus einem Paket (es kommen höchstens drei, Review 27.10.2026: fremde Pakete begrenzen).</summary>
	private const int MaxBolts = 8;
	private static readonly Regex Product = new("\"Item\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"Amount\"\\s*:\\s*(-?\\d+)");

	internal static readonly List<Incinerator> Incinerators = new();
	/// <summary>Handwerk (Stufe 0–100) des Spielers am Hebel, je Absender.</summary>
	private static readonly Dictionary<long, float> SkillByPeer = new();
	private static Run? current;
	private static FieldInfo? iteratorThis;
	private static FieldInfo? iteratorUid;
	private static bool epicSearched;
	private static MethodInfo? epicProducts;
	private static MethodInfo? epicIsMagic;
	private static MethodInfo? epicIsEpicLoot;

	private sealed class Run
	{
		internal Incinerator Incinerator = null!;
		internal long Uid;
		/// <summary>Die wirkende Kette (I, I+II oder I+II+III), nie leer.</summary>
		internal List<Coil> Chain = null!;
		internal bool Struck;
		internal Dictionary<GameObject, float>? Materials;
		internal Dictionary<GameObject, float>? Epic;
		internal int Percent;
	}

	internal static bool EpicLootLoaded => Chainloader.PluginInfos.ContainsKey(EpicLootGuid);

	/// <summary>
	/// Neuer Vernichter: in die Liste (für Bauregeln und Reichweite), eigene RPCs an seinem Netzobjekt. Ergebnis-Zeile und Blitze
	/// nur vom Besitzer des Vernichters annehmen, den Handwerk-Wert eines Spielers auf 0–100 begrenzen (Review 27.10.2026).
	/// </summary>
	internal static void Register(Incinerator incinerator)
	{
		ZNetView view = incinerator.m_nview;
		if (view == null || view.GetZDO() == null)
		{
			return; // Bauvorschlag
		}
		Incinerators.Add(incinerator);
		view.Register<float>(RpcSkill, (sender, skill) => SkillByPeer[sender] = float.IsNaN(skill) ? 0f : Mathf.Clamp(skill, 0f, 100f));
		view.Register<int, string>(RpcInfo, (sender, percent, list) =>
			Plugin.Module.Guard("Blitzspulen-Meldung", () =>
			{
				if (FromOwner(view, sender))
				{
					ShowInfo(percent, list);
				}
			}));
		view.Register<ZPackage>(RpcBolts, (sender, package) =>
			Plugin.Module.Guard("Blitzspulen-Blitze", () =>
			{
				if (FromOwner(view, sender))
				{
					PlayBolts(view.transform.position, package);
				}
			}));
	}

	/// <summary>Kommt das RPC vom Besitzer des Vernichters (nur der lässt ihn laufen)? Absender und Besitzer sind beide die Sitzungs-ID.</summary>
	private static bool FromOwner(ZNetView view, long sender) => view != null && view.IsValid() && view.GetZDO().GetOwner() == sender;

	/// <summary>Spieler am Hebel: Handwerk (Stufe 0–100) an den Besitzer des Vernichters, bevor Valheim ihn anfordert.</summary>
	internal static void SendSkill(Incinerator incinerator)
	{
		ZNetView view = incinerator.m_nview;
		Player? player = Player.m_localPlayer;
		if (!Plugin.Module.On || view == null || !view.IsValid() || player == null)
		{
			return;
		}
		view.InvokeRPC(RpcSkill, player.GetSkillLevel(Skills.SkillType.Crafting));
	}

	/// <summary>Anfang eines Schritts der Coroutine Incinerate: Lauf mit der Spulen-Kette (ohne Spule I kein Lauf).</summary>
	internal static void Begin(object iterator)
	{
		current = null;
		if (!Plugin.Module.On)
		{
			return;
		}
		Type type = iterator.GetType();
		iteratorThis ??= AccessTools.Field(type, "<>4__this");
		iteratorUid ??= AccessTools.Field(type, "uid");
		if (iteratorThis?.GetValue(iterator) is not Incinerator incinerator || incinerator == null)
		{
			return;
		}
		List<Coil> chain = Coils.Chain(incinerator.transform.position);
		if (chain.Count == 0)
		{
			return;
		}
		current = new Run { Incinerator = incinerator, Uid = iteratorUid?.GetValue(iterator) is long uid ? uid : 0L, Chain = chain };
	}

	/// <summary>
	/// Eine Aoe entsteht (auf jedem Rechner, der sie lädt): Ist sie Teil von Valheims Vernichter-Blitz, nimmt die Kette je Stufe
	/// ⅓ von Schaden und Wucht, alle drei Stufen schalten sie ganz ab. Review 27.10.2026: nicht mehr am statischen Lauf festmachen,
	/// den gibt es nur beim Besitzer des Vernichters. Valheim erzeugt den Blitz mit Netzobjekt auch auf den anderen Rechnern in der
	/// Nähe; jede Kopie wird gleich behandelt, damit keine ungeminderte treffen kann (etwa nach einem Besitzwechsel oder über Trigger).
	/// </summary>
	internal static void Strike(Aoe aoe)
	{
		if (!Plugin.Module.On)
		{
			return;
		}
		Incinerator? incinerator = StruckIncinerator(aoe.transform.root);
		if (incinerator == null)
		{
			return; // nur Valheims Vernichter-Blitz, keine anderen Aoe (Angriffe, Stäbe, die Aufwertungs-Station …)
		}
		int chain = Coils.Chain(incinerator.transform.position).Count;
		float factor = 1f - chain / 3f;
		Run? run = current;
		if (run != null && run.Incinerator == incinerator && !run.Struck)
		{
			run.Struck = true;
			Coils.Log.LogInfo($"Blitzspulen: Blitz am Vernichter mit Spulen-Kette bis {(chain == 0 ? "-" : Coils.Roman(chain))}, {Mathf.RoundToInt(factor * 100f)} % Schaden.");
		}
		if (chain == 0)
		{
			return;
		}
		if (factor <= 0.01f)
		{
			Disarm(aoe);
			return;
		}
		aoe.m_damage.Modify(factor);
		aoe.m_damagePerLevel.Modify(factor);
		aoe.m_attackForce *= factor;
		aoe.m_knockBackForce *= factor;
		aoe.m_launchForceMinMax *= factor;
	}

	/// <summary>
	/// Zu welchem Vernichter gehört der Blitz mit dieser Wurzel? Valheim setzt ihn genau auf die Position des Vernichters
	/// (Incinerator.cs:192), die anderen Rechner an dieselbe Position aus seinem ZDO (ZNetScene.CreateObject). Erst die Position
	/// (billig, Aoe.Awake läuft bei jedem Angriff), dann der Prefab-Name.
	/// </summary>
	private static Incinerator? StruckIncinerator(Transform root)
	{
		Vector3 position = root.position;
		Incinerators.RemoveAll(i => i == null); // entladene Vernichter, sonst wächst die Liste beim Herumlaufen
		foreach (Incinerator incinerator in Incinerators)
		{
			if (incinerator == null || (incinerator.transform.position - position).sqrMagnitude > 0.25f)
			{
				continue;
			}
			GameObject? strike = incinerator.m_lightingAOEs;
			if (strike != null && Utils.GetPrefabName(root.gameObject) == Utils.GetPrefabName(strike))
			{
				return incinerator;
			}
		}
		return null;
	}

	/// <summary>
	/// Alle drei Stufen: Der Blitz trifft gar nichts mehr. enabled = false allein reicht nicht, Trigger erreichen auch
	/// ausgeschaltete Komponenten (Aoe.cs:564–586, 669); deshalb auch Treffer-Arten, die schon in Awake gebildete Treffer-Maske,
	/// Schaden, Wucht, Hochschleudern und Status-Effekte (samt ihrer in Awake berechneten Hashes) leeren (Review 27.10.2026).
	/// Bild und Ton gehören zu anderen Komponenten des Blitzes.
	/// </summary>
	private static void Disarm(Aoe aoe)
	{
		aoe.m_hitCharacters = false;
		aoe.m_hitProps = false;
		aoe.m_hitTerrain = false;
		aoe.m_rayMask = 0;
		aoe.m_damage = new HitData.DamageTypes();
		aoe.m_damagePerLevel = new HitData.DamageTypes();
		aoe.m_damageSelf = 0f;
		aoe.m_attackForce = 0f;
		aoe.m_knockBackForce = 0f;
		aoe.m_launchCharacters = false;
		aoe.m_launchForceMinMax = Vector2.zero;
		aoe.m_statusEffect = aoe.m_statusEffectIfBoss = aoe.m_statusEffectIfPlayer = "";
		aoe.m_statusEffectHash = aoe.m_statusEffectIfBossHash = aoe.m_statusEffectIfPlayerHash = 0;
		aoe.m_chainStartChance = 0f;
		aoe.m_spawnOnHitTerrain = null;
		aoe.enabled = false;
	}

	/// <summary>Kurz bevor Valheim den Vernichter leert: berechnen, was die Spulen zurückgeben.</summary>
	internal static void BeforeDestroy(Inventory inventory)
	{
		Run? run = current;
		if (run == null || run.Materials != null || run.Incinerator.m_container == null || inventory != run.Incinerator.m_container.GetInventory())
		{
			return;
		}
		int best = Mathf.Clamp(run.Chain.Count, 1, 3); // höchste Stufe der lückenlosen Kette (Review 27.10.2026)
		float rate = Mathf.Clamp(Coils.Rates[best - 1].Value, 0f, 100f) / 100f;
		run.Percent = Mathf.FloorToInt(rate * 100f);
		run.Materials = new Dictionary<GameObject, float>();
		run.Epic = new Dictionary<GameObject, float>();
		// Weltschlüssel NoCraftCost: Valheim nimmt beim Herstellen nichts (InventoryGui.cs:1799, 1905), also keine Rezept-Rückgabe
		bool recipes = ZoneSystem.instance == null || !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost);
		if (!recipes)
		{
			Coils.Log.LogInfo("Blitzspulen: Weltschlüssel NoCraftCost gesetzt, keine Rückgabe von Rezept-Materialien.");
		}
		bool epic = FindEpicLoot();
		float skill = SkillByPeer.TryGetValue(run.Uid, out float s) ? s : 0f;
		SkillByPeer.Remove(run.Uid);
		// Untergrenze für EpicLoot (Review 27.10.2026, Gorduans Vorgabe 50–80 %, 80 % fest mit allen drei Spulen oder Handwerk 100):
		// Spule I allein 0, I+II die Mitte, I+II+III der Höchstwert; Handwerk 0–100 anteilig, das Größere gilt
		float quality = Mathf.Clamp01(Mathf.Max((best - 1) / 2f, skill / 100f));
		float max = Mathf.Clamp(Coils.EpicMax.Value, 0f, 100f) / 100f;
		float min = Mathf.Min(max, Mathf.Clamp(Coils.EpicMin.Value, 0f, 100f) / 100f);
		foreach (ItemDrop.ItemData item in inventory.GetAllItems())
		{
			if (recipes)
			{
				AddRecipe(item, rate, run.Materials);
			}
			if (epic && IsEpicLootItem(item))
			{
				AddEpic(item, UnityEngine.Random.Range(Mathf.Lerp(min, max, quality), max), run.Epic);
			}
		}
	}

	/// <summary>Ende des Schritts: Rückgabe in den Vernichter, Meldung an den Spieler am Hebel, Blitze in die Kronen der Kette.</summary>
	internal static void End()
	{
		Run? run = current;
		current = null;
		if (run == null || run.Incinerator == null)
		{
			return;
		}
		ZNetView view = run.Incinerator.m_nview;
		if (run.Materials != null && run.Epic != null)
		{
			Dictionary<GameObject, int> refund = new();
			Collect(run.Materials, refund);
			Collect(run.Epic, refund);
			Deliver(run.Incinerator, refund);
			Coils.Log.LogInfo($"Blitzspulen: Vernichter mit Spulen-Kette bis {Coils.Roman(Mathf.Clamp(run.Chain.Count, 1, 3))} ({run.Percent} %), zurück: "
				+ (refund.Count == 0 ? "nichts" : string.Join(", ", refund.Select(r => $"{r.Key.name} ×{r.Value}"))));
			if (view != null && view.IsValid() && refund.Count > 0)
			{
				string list = string.Join(", ", refund.Select(r => $"{r.Key.GetComponent<ItemDrop>().m_itemData.m_shared.m_name} ×{r.Value}"));
				view.InvokeRPC(run.Uid, RpcInfo, run.Percent, list);
			}
		}
		List<Coil> crowns = run.Chain.Where(c => c != null).ToList();
		if (run.Struck && crowns.Count > 0 && view != null && view.IsValid())
		{
			ZPackage package = new();
			package.Write(crowns.Count);
			foreach (Coil coil in crowns)
			{
				package.Write(coil.transform.position + Vector3.up * Coils.CrownHeight);
			}
			view.InvokeRPC(ZNetView.Everybody, RpcBolts, package);
		}
	}

	/// <summary>Bei einem Fehler im Schritt keinen halben Lauf stehen lassen.</summary>
	internal static void Abort() => current = null;

	/// <summary>
	/// Rezeptkosten des Items × Satz, je Material summiert, so wie Valheim sie an der Werkbank nimmt (Player.ConsumeResources,
	/// Player.cs:2890: ohne Aufwertungs-Station nur die normalen Zutaten, je Stufe Piece.Requirement.GetAmount): Grundrezept und
	/// jede Aufwertungsstufe bis m_maxQuality, Stapel anteilig. Bewusst nicht erstattet (Review 27.10.2026):
	///  * Rezepte mit m_requireOnlyOneIngredient (Recipe_Fish1: eine von zwölf Fischarten): Welche Zutat genommen wurde, weiß
	///    niemand, alle zurückzugeben vervielfacht (Recipe.cs:25, 47–58; InventoryGui.cs:1908–1910).
	///  * Stufen über m_maxQuality: Die gibt es nur an der Aufwertungs-Station (m_upgrader, InventoryGui.cs:1505, 1790–1791), und
	///    dort nimmt Valheim nur die Aufwertungs-Zutat (UpgraderNWeapon/-Armor, eine je Versuch), mit Glück, Fehlschlag und
	///    Zerbrechen (InventoryGui.cs:1841–1897). Wie viele es waren, ist unklar, und Valheim selbst gibt sie auch beim Zerbrechen
	///    nicht zurück (m_recover = false in allen 271 Rezepten, InventoryGui.cs:1880).
	/// </summary>
	private static void AddRecipe(ItemDrop.ItemData item, float rate, Dictionary<GameObject, float> into)
	{
		Recipe? recipe = ObjectDB.instance != null ? ObjectDB.instance.GetRecipe(item) : null;
		if (recipe == null || recipe.m_resources == null || recipe.m_requireOnlyOneIngredient || rate <= 0f)
		{
			return;
		}
		float units = item.m_stack / (float)Mathf.Max(1, recipe.m_amount);
		int maxQuality = recipe.m_item != null ? recipe.m_item.m_itemData.m_shared.m_maxQuality : item.m_shared.m_maxQuality;
		int levels = Mathf.Clamp(item.m_quality, 1, Mathf.Max(1, maxQuality));
		foreach (Piece.Requirement requirement in recipe.m_resources)
		{
			if (requirement?.m_resItem == null || requirement.m_upgraderResource)
			{
				continue;
			}
			int spent = 0;
			for (int level = 1; level <= levels; level++)
			{
				spent += requirement.GetAmount(level);
			}
			if (spent > 0)
			{
				Add(into, requirement.m_resItem.gameObject, spent * units * rate);
			}
		}
	}

	private static void AddEpic(ItemDrop.ItemData item, float factor, Dictionary<GameObject, float> into)
	{
		string json;
		try
		{
			json = epicProducts?.Invoke(null, new object[] { item }) as string ?? "";
		}
		catch (Exception e)
		{
			Coils.Log.LogWarning($"Blitzspulen: EpicLoot-Opfer-Produkte nicht gelesen: {e.InnerException?.Message ?? e.Message}");
			return;
		}
		foreach (Match match in Product.Matches(json))
		{
			GameObject? prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(match.Groups[1].Value) : null;
			if (prefab != null && prefab.GetComponent<ItemDrop>() != null && int.TryParse(match.Groups[2].Value, out int amount) && amount > 0)
			{
				Add(into, prefab, amount * Mathf.Max(1, item.m_stack) * factor);
			}
		}
	}

	private static void Add(Dictionary<GameObject, float> into, GameObject prefab, float amount)
	{
		into.TryGetValue(prefab, out float sum);
		into[prefab] = sum + amount;
	}

	/// <summary>Je Sorte abrunden (Gorduan: immer abgerundet) und zur Rückgabe zählen.</summary>
	private static void Collect(Dictionary<GameObject, float> from, Dictionary<GameObject, int> into)
	{
		foreach (KeyValuePair<GameObject, float> entry in from)
		{
			int amount = Mathf.FloorToInt(entry.Value + 0.0001f);
			if (amount > 0)
			{
				into.TryGetValue(entry.Key, out int sum);
				into[entry.Key] = sum + amount;
			}
		}
	}

	/// <summary>In den Vernichter legen; was nicht passt, fällt vor ihm auf den Boden.</summary>
	private static void Deliver(Incinerator incinerator, Dictionary<GameObject, int> refund)
	{
		Inventory inventory = incinerator.m_container.GetInventory();
		Vector3 drop = incinerator.transform.position + incinerator.transform.forward * 1.6f + Vector3.up * 0.8f;
		foreach (KeyValuePair<GameObject, int> entry in refund)
		{
			ItemDrop item = entry.Key.GetComponent<ItemDrop>();
			int left = entry.Value;
			int stack = Mathf.Max(1, item.m_itemData.m_shared.m_maxStackSize);
			while (left > 0)
			{
				int amount = Mathf.Min(left, stack);
				if (!inventory.CanAddItem(entry.Key, amount) || !inventory.AddItem(entry.Key, amount))
				{
					ItemDrop.ItemData data = item.m_itemData.Clone();
					data.m_dropPrefab = entry.Key;
					data.m_stack = amount;
					ItemDrop.DropItem(data, amount, drop, Quaternion.identity);
				}
				left -= amount;
			}
		}
	}

	private static bool FindEpicLoot()
	{
		if (epicSearched)
		{
			return epicProducts != null;
		}
		epicSearched = true;
		if (!Chainloader.PluginInfos.TryGetValue(EpicLootGuid, out BepInEx.PluginInfo info) || info.Instance == null)
		{
			return false;
		}
		Type? api = info.Instance.GetType().Assembly.GetType("EpicLoot.API");
		epicProducts = api?.GetMethod("GetSacrificeProductsJson", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ItemDrop.ItemData) }, null);
		epicIsMagic = api?.GetMethod("IsMagicItem", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ItemDrop.ItemData) }, null);
		epicIsEpicLoot = api?.GetMethod("IsEpicLootItem", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ItemDrop.ItemData) }, null);
		Coils.Log.LogInfo(epicProducts != null ? "Blitzspulen: EpicLoot erkannt, Opfer-Produkte kommen zurück." : "Blitzspulen: EpicLoot ohne Opfer-Schnittstelle, nur Baumaterialien.");
		return epicProducts != null;
	}

	/// <summary>
	/// EpicLoot-Item: verzaubert (API.IsMagicItem) oder ein EpicLoot-eigenes. Für Letzteres EpicLoots eigene Prüfung
	/// API.IsEpicLootItem (ab 0.15.2, API.cs:2189: Name „$mod_epicloot…“, Splitter-Steine, Runensteine, Magie-Material, nicht
	/// identifiziertes Material), sonst wie bisher der Name (Review 27.10.2026). IsEpicLootItem allein reicht nicht, verzauberte
	/// Waffen und Rüstungen zählt es nicht mit. Trophäen und Boss-Drops sind bewusst keine EpicLoot-Items: EpicLoot nimmt sie
	/// zwar als Opfer an, Gorduans Vorgabe gilt aber nur für EpicLoot-Items, ihre Opfer-Produkte kommen also nicht zurück.
	/// </summary>
	private static bool IsEpicLootItem(ItemDrop.ItemData item)
	{
		try
		{
			if (epicIsMagic?.Invoke(null, new object[] { item }) is true)
			{
				return true;
			}
			if (epicIsEpicLoot != null)
			{
				return epicIsEpicLoot.Invoke(null, new object[] { item }) is true;
			}
		}
		catch
		{
			// Fehler in EpicLoot: dann nur der Name
		}
		return item.m_shared.m_name.StartsWith("$mod_epicloot", StringComparison.Ordinal);
	}

	private static void ShowInfo(int percent, string list)
	{
		if (MessageHud.instance == null || Player.m_localPlayer == null)
		{
			return;
		}
		string items = Localization.instance != null ? Localization.instance.Localize(list) : list;
		MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, GcLoc.Get("gcbs_result", percent.ToString(), items));
	}

	/// <summary>
	/// Bei jedem Spieler: Valheims Blitz-Effekt (nur Bild und Ton) in jede Krone. fx_Lightning hat ZNetView und ZSyncTransform;
	/// so erzeugt wie Valheims Bauvorschau (Player.cs:3486–3501): Mit m_forceDisableInit entfernt sich das ZNetView selbst und
	/// ZSyncTransform schaltet sich mangels ZDO ab (Review 27.10.2026: das früher entfernte ZNetView ließ ZSyncTransform.Awake
	/// werfen, danach warf CustomFixedUpdate jeden Physikschritt). Höchstens <see cref="MaxBolts"/> Blitze, nur nahe dem Vernichter.
	/// </summary>
	private static void PlayBolts(Vector3 incinerator, ZPackage package)
	{
		if (Gc.IsHeadless || ZNetScene.instance == null)
		{
			return;
		}
		GameObject? prefab = ZNetScene.instance.GetPrefab("fx_Lightning");
		if (prefab == null)
		{
			return;
		}
		int count = Mathf.Clamp(package.ReadInt(), 0, MaxBolts);
		float reach = Coils.Range.Value + Coils.CrownHeight + 1f;
		for (int i = 0; i < count; i++)
		{
			Vector3 crown = package.ReadVector3();
			if (Vector3.Distance(crown, incinerator) > reach)
			{
				continue;
			}
			GameObject bolt;
			bool terrainOps = TerrainOp.m_forceDisableTerrainOps;
			ZNetView.m_forceDisableInit = true;
			TerrainOp.m_forceDisableTerrainOps = true;
			try
			{
				bolt = Object.Instantiate(prefab, crown, Quaternion.identity);
			}
			finally
			{
				ZNetView.m_forceDisableInit = false;
				TerrainOp.m_forceDisableTerrainOps = terrainOps;
			}
			// heute ohne solche Teile; falls ein Spiel-Update sie dazulegt: nichts davon darf treffen oder Gelände ändern
			foreach (Component part in bolt.GetComponentsInChildren<Component>(true).Where(c => c is Aoe or TerrainOp or TerrainModifier).ToList())
			{
				Object.DestroyImmediate(part);
			}
			Object.Destroy(bolt, 6f);
		}
	}
}

/// <summary>Neuer Vernichter: merken und eigene RPCs anmelden.</summary>
[HarmonyPatch(typeof(Incinerator), "Awake")]
internal static class IncineratorAwakePatch
{
	private static void Postfix(Incinerator __instance) => Plugin.Module.Guard("Vernichter", () => Recycling.Register(__instance));
}

/// <summary>Hebel gezogen (beim Spieler): Handwerk an den Besitzer schicken.</summary>
[HarmonyPatch(typeof(Incinerator), "OnIncinerate")]
internal static class IncineratorLeverPatch
{
	private static void Prefix(Incinerator __instance) => Plugin.Module.Guard("Vernichter", () => Recycling.SendSkill(__instance));
}

/// <summary>Jeder Schritt der Coroutine Incinerate: Lauf beginnen und am Ende ausliefern.</summary>
[HarmonyPatch]
internal static class IncinerateStepPatch
{
	private static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(Incinerator), "Incinerate"));

	private static void Prefix(object __instance) => Plugin.Module.Guard("Vernichter", () => Recycling.Begin(__instance));

	private static void Postfix() => Plugin.Module.Guard("Vernichter", Recycling.End);

	private static Exception? Finalizer(Exception? __exception)
	{
		if (__exception != null)
		{
			Recycling.Abort();
		}
		return __exception;
	}
}

/// <summary>Valheim leert den Vernichter: vorher die Rückgabe berechnen.</summary>
[HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveAll))]
internal static class IncineratorEmptyPatch
{
	private static void Prefix(Inventory __instance) => Plugin.Module.Guard("Vernichter", () => Recycling.BeforeDestroy(__instance));
}

/// <summary>Eine Aoe entsteht (auf jedem Rechner): Ist es der Blitz eines Vernichters, nimmt die Spulen-Kette ihren Anteil.</summary>
[HarmonyPatch(typeof(Aoe), "Awake")]
internal static class IncineratorStrikePatch
{
	private static void Postfix(Aoe __instance) => Plugin.Module.Guard("Vernichter", () => Recycling.Strike(__instance));
}
