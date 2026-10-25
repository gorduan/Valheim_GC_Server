using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GC.Shared;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Leash;

/// <summary>
/// Tierarten (Gorduan, 20.10.2026).
///
/// Höchststufe je Art, im Menü je Tier einstellbar: Lox (mit Kalb) 300, Wolf (mit Welpe) 200, Bär 200, Unbjørn 300,
/// alle anderen 150. Eigene Stufe (zur Hälfte) und Pfleger zählen für die Boni zusammen höchstens EffectCap (150, Grenze A).
///
/// Sterne bei der Geburt (Gorduan): Valheim gibt dem Jungen die Sterne der Mutter. Wäre es ohne Stern, hat es 1 % Chance auf
/// einen; mit einem Stern 0,5 % auf zwei. Höchstens ein Stern mehr je Geburt, auch bei Eiern (beim Legen); der Züchter
/// bekommt eine Meldung.
///
/// Weitere zähmbare Tiere (Gorduan, Standard aus, je Tier im Menü): Nixe (roher Fisch), Hirsch (Himbeeren, Blaubeeren, Pilze),
/// Hase (Pilze), Robbe (roher Fisch), Bär (Honig, Beeren). Futter hinwerfen, das Tier frisst es wie Wildschwein und Wolf,
/// 30 Minuten Zähmzeit. Hirsch, Hase und Robbe haben Valheims einfache Tier-KI: Sie fressen hier vom Boden (Graze), das
/// Zähmen läuft über TamingUpdate. Gezähmt fliehen sie nicht mehr; an der Leine folgen sie (LeashAnimalAIPatch).
///
/// Bär (Gorduan): Bären bekommen Junge, ein Drittel so groß, mit zwei Dritteln der Farbsättigung, einem Drittel Leben und
/// Schaden; sie wachsen zum Bären heran. Frisst ein Bär (zahm oder wild) verrottetes Fleisch (das „Unbjørn-Fleisch“),
/// verwandelt er sich 30 Minuten (Weltzeit) später in einen Unbjørn, mit Stufe, Sternen, Namen, Leine und Zahm-Status.
/// Unbjørn lassen sich nicht zähmen und bekommen keine Jungen. Zahme Bären greifen wilde Bären nicht von selbst an (auf
/// Befehl schon); Unbjørn aus zahmen Bären greifen alles Wilde von selbst an, keine Spieler und keine zahmen Tiere.
///
/// Beute (Gorduan): Jedes Tier mit Stufe hat beim Schlachten eine 1:1-Chance auf mehr Beute, fließend nach der Stufe: das
/// Höchstmaß ist 1 + Stufe ÷ 50 (Stufe 25: 1,5-fach, 50: doppelt, 100: bis dreifach, 300: bis siebenfach), getroffen wird
/// zufällig zwischen dem Doppelten (bzw. darunter dem Höchstmaß) und dem Höchstmaß. Valheims Sterne zählen wie immer dazu.
/// </summary>
internal static class Species
{
	internal const string CubPrefab = "GC_Bjorn_Cub";
	internal const string CubName = "$gc_bjorn_cub";
	internal const string BearName = "$enemy_bjorn";
	internal const string UnbjornName = "$enemy_unbjorn";
	internal const string RottenMeat = "RottenMeat";

	/// <summary>Bärenjunges: Größe, Leben und Schaden ein Drittel, Farbsättigung zwei Drittel (Gorduan).</summary>
	internal const float CubScale = 1f / 3f;
	internal const float CubSaturation = -1f / 3f;
	internal const float CubGrowTime = 6000f; // wie Valheims Lox-Kalb (großes Tier)

	/// <summary>Verwandlung in einen Unbjørn: so viele Sekunden Weltzeit nach dem Fressen (Gorduan: 30 Minuten).</summary>
	internal const double TransformSeconds = 1800.0;

	/// <summary>Gorduan: je so viele Stufen das Höchstmaß der Beute einmal mehr (fließend: 25 → 1,5-fach, 50 → doppelt, 100 → bis dreifach).</summary>
	internal const float LootStep = 50f;

	internal sealed class Kind
	{
		public string Key = "";
		public string[] Prefabs = Array.Empty<string>();
		public int DefaultMax = 150;
		public bool Optional;
		public string[] Foods = Array.Empty<string>();
		public ConfigEntry<int> MaxLevel = null!;
		public ConfigEntry<bool>? Tameable;
		public readonly HashSet<string> FoodNames = new(); // m_shared.m_name des Futters (für die einfache Tier-KI)
		public List<ItemDrop> VanillaFood = new();
		public List<ItemDrop> AllFood = new();
	}

	internal static readonly Kind Wolf = new() { Key = "Wolf", Prefabs = new[] { "Wolf", "Wolf_cub" }, DefaultMax = 200 };
	internal static readonly Kind Lox = new() { Key = "Lox", Prefabs = new[] { "Lox", "Lox_Calf" }, DefaultMax = 300 };
	internal static readonly Kind Boar = new() { Key = "Boar", Prefabs = new[] { "Boar", "Boar_piggy" } };
	internal static readonly Kind Hen = new() { Key = "Hen", Prefabs = new[] { "Hen", "Chicken" } };
	internal static readonly Kind Asksvin = new() { Key = "Asksvin", Prefabs = new[] { "Asksvin", "Asksvin_hatchling" } };
	internal static readonly Kind Moose = new() { Key = "Moose", Prefabs = new[] { "Moose", "Moose_calf" } };
	internal static readonly Kind Bear = new() { Key = "Bjorn", Prefabs = new[] { "Bjorn", CubPrefab }, DefaultMax = 200, Optional = true, Foods = new[] { "Honey", "Raspberry", "Blueberries", "Cloudberry", RottenMeat } };
	internal static readonly Kind Unbjorn = new() { Key = "Unbjorn", Prefabs = new[] { "Unbjorn" }, DefaultMax = 300 };
	internal static readonly Kind Neck = new() { Key = "Neck", Prefabs = new[] { "Neck" }, Optional = true, Foods = new[] { "FishRaw" } };
	internal static readonly Kind Deer = new() { Key = "Deer", Prefabs = new[] { "Deer" }, Optional = true, Foods = new[] { "Raspberry", "Blueberries", "Mushroom" } };
	internal static readonly Kind Hare = new() { Key = "Hare", Prefabs = new[] { "Hare" }, Optional = true, Foods = new[] { "Mushroom", "MushroomYellow", "MushroomJotunPuffs" } };
	internal static readonly Kind Seal = new() { Key = "Seal", Prefabs = new[] { "Seal" }, Optional = true, Foods = new[] { "FishRaw" } };
	internal static readonly Kind Other = new() { Key = "Other" };
	internal static readonly Kind[] All = { Wolf, Lox, Boar, Hen, Asksvin, Moose, Bear, Unbjorn, Neck, Deer, Hare, Seal, Other };

	internal static ConfigEntry<float> EffectCap = null!;
	internal static ConfigEntry<float> StarChance1 = null!;
	internal static ConfigEntry<float> StarChance2 = null!;

	private static readonly Dictionary<int, Kind> ByHash = new();
	private static readonly int BearHash = "Bjorn".GetStableHashCode();
	private static readonly int UnbjornHash = "Unbjorn".GetStableHashCode();
	internal static readonly int TransformHash = "gc_unbjorn_at".GetStableHashCode();
	private const string TellRpc = "GC_SpeciesTell";
	private static string rottenMeatName = "$item_rottenmeat";
	private static EffectList? eatEffects;
	private static float nextTick;

	/// <summary>Während der Befehl „angreifen“ geprüft wird: Valheims Feindschaft ohne den Bärenfrieden (Befehl gegen wilde Bären erlaubt).</summary>
	private static bool bypassPeace;

	internal static void Init(ConfigFile config)
	{
		const string section = "14 - Animal Species";
		EffectCap = Gc.BindSynced(config, section, "EffectCap", 150f, "Höchste wirksame Stufe für die Boni: halbe eigene Stufe + Tierpflege des Pflegers zusammen (Gorduan: Grenze A, wie bisher 150). Darüber bringt der Pfleger nichts mehr.", new AcceptableValueRange<float>(10f, 1000f));
		StarChance1 = Gc.BindSynced(config, section, "StarChance1", 1f, "Prozent-Chance, dass ein Junges (oder Ei), das ohne Stern wäre, einen Stern bekommt (Gorduan: 1 %).", new AcceptableValueRange<float>(0f, 100f));
		StarChance2 = Gc.BindSynced(config, section, "StarChance2", 0.5f, "Prozent-Chance, dass ein Junges (oder Ei) mit einem Stern zwei bekommt (Gorduan: 0,5 %).", new AcceptableValueRange<float>(0f, 100f));
		foreach (Kind kind in All)
		{
			string who = kind == Other ? "alle anderen zahmen Tiere (auch aus anderen Mods)" : string.Join(", ", kind.Prefabs);
			kind.MaxLevel = Gc.BindSynced(config, section, "MaxLevel" + kind.Key, kind.DefaultMax, $"Höchststufe (Zucht und Erfahrung): {who}.", new AcceptableValueRange<int>(1, 1000));
			if (kind.Optional)
			{
				string extra = kind == Bear ? " Dazu: Bärenjunge, Verwandlung in einen Unbjørn mit verrottetem Fleisch, Sättel für Bär und Unbjørn." : "";
				kind.Tameable = Gc.BindSynced(config, section, "Tameable" + kind.Key, false, $"Gorduan: {kind.Prefabs[0]} lässt sich zähmen (Futter: {string.Join(", ", kind.Foods)}) und anleinen.{extra}");
			}
			foreach (string prefab in kind.Prefabs)
			{
				ByHash[prefab.GetStableHashCode()] = kind;
			}
		}
		PrefabManager.OnVanillaPrefabsAvailable += SetupPrefabs;
	}

	// ------------------------------------------------------------------ Abfragen

	internal static Kind Of(int prefabHash) => ByHash.TryGetValue(prefabHash, out Kind kind) ? kind : Other;

	internal static Kind Of(Character? character)
	{
		ZNetView? view = character != null ? character.m_nview : null;
		ZDO? zdo = view != null && view.IsValid() ? view.GetZDO() : null;
		return zdo != null ? Of(zdo.GetPrefab()) : Other;
	}

	internal static float MaxLevel(int prefabHash) => Mathf.Max(1, Of(prefabHash).MaxLevel.Value);

	internal static float MaxLevel(Character? character) => Mathf.Max(1, Of(character).MaxLevel.Value);

	/// <summary>Darf diese Art gezähmt werden (Vanilla-Arten immer; Unbjørn folgt dem Bären)?</summary>
	internal static bool Enabled(Kind kind)
	{
		if (kind == Unbjorn)
		{
			return Bear.Tameable != null && Bear.Tameable.Value;
		}
		return kind.Tameable == null || kind.Tameable.Value;
	}

	private static bool IsBear(Character c) => c.m_name == BearName || c.m_name == CubName;

	private static bool IsWild(Character c)
	{
		if (c.IsPlayer() || c.IsTamed())
		{
			return false;
		}
		Character.Faction faction = c.GetFaction();
		return faction != Character.Faction.Players && faction != Character.Faction.PlayerSpawned && faction != Character.Faction.TrainingDummy;
	}

	// ------------------------------------------------------------------ Vorlagen

	/// <summary>Einmal, wenn Valheims Vorlagen da sind: Zähm-Komponenten, Futter, Zucht der Bären, Bärenjunges.</summary>
	private static void SetupPrefabs()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= SetupPrefabs;
		try
		{
			GameObject? wolf = PrefabManager.Instance.GetPrefab("Wolf");
			Tameable? template = wolf != null ? wolf.GetComponent<Tameable>() : null;
			Humanoid? lox = PrefabManager.Instance.GetPrefab("Lox")?.GetComponent<Humanoid>();
			eatEffects = lox != null ? lox.m_consumeItemEffects : null;
			ItemDrop? rotten = PrefabManager.Instance.GetPrefab(RottenMeat)?.GetComponent<ItemDrop>();
			if (rotten != null)
			{
				rottenMeatName = rotten.m_itemData.m_shared.m_name;
			}
			foreach (Kind kind in new[] { Bear, Unbjorn, Neck, Deer, Hare, Seal })
			{
				foreach (string name in kind.Foods)
				{
					ItemDrop? food = PrefabManager.Instance.GetPrefab(name)?.GetComponent<ItemDrop>();
					if (food == null)
					{
						ServerPlugin.Log.LogInfo($"Tierarten: Futter {name} gibt es im Spiel nicht.");
						continue;
					}
					kind.FoodNames.Add(food.m_itemData.m_shared.m_name);
					kind.AllFood.Add(food);
				}
				foreach (string name in kind.Prefabs)
				{
					if (name == CubPrefab)
					{
						continue;
					}
					GameObject? prefab = PrefabManager.Instance.GetPrefab(name);
					if (prefab == null)
					{
						ServerPlugin.Log.LogInfo($"Tierarten: {name} gibt es im Spiel nicht.");
						continue;
					}
					AddTameable(prefab, template);
					MonsterAI? ai = prefab.GetComponent<MonsterAI>();
					if (ai != null && kind.AllFood.Count > 0)
					{
						kind.VanillaFood = new List<ItemDrop>(ai.m_consumeItems ?? new List<ItemDrop>());
						List<ItemDrop> all = new(kind.VanillaFood);
						foreach (ItemDrop food in kind.AllFood)
						{
							if (!all.Contains(food))
							{
								all.Add(food);
							}
						}
						kind.AllFood = all;
					}
				}
			}
			GameObject? bear = PrefabManager.Instance.GetPrefab("Bjorn");
			if (bear != null)
			{
				CreateCub(bear);
				AddProcreation(bear);
			}
			ServerPlugin.Log.LogInfo("Tierarten: Nixe, Hirsch, Hase, Robbe und Bär vorbereitet (zähmbar nur, wenn im Menü an), Bärenjunges angelegt.");
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Tierarten: Vorbereitung fehlgeschlagen: {e}");
		}
	}

	private static void AddTameable(GameObject prefab, Tameable? template)
	{
		Tameable tame = prefab.GetComponent<Tameable>() ?? prefab.AddComponent<Tameable>();
		tame.m_fedDuration = 600f;
		tame.m_tamingTime = 1800f;
		tame.m_commandable = false; // die Leinen übernehmen das Folgen
		if (template != null)
		{
			tame.m_tamedEffect = template.m_tamedEffect;
			tame.m_sootheEffect = template.m_sootheEffect;
			tame.m_petEffect = template.m_petEffect;
			tame.m_tamingSpeedMultiplierRange = template.m_tamingSpeedMultiplierRange;
			tame.m_tamingBoostMultiplier = template.m_tamingBoostMultiplier;
		}
	}

	/// <summary>Bärenjunges (Gorduan): Klon des Bären, ein Drittel so groß, zwei Drittel Farbsättigung, ein Drittel Leben, wächst heran.</summary>
	private static void CreateCub(GameObject bear)
	{
		GameObject? cub = PrefabManager.Instance.CreateClonedPrefab(CubPrefab, "Bjorn");
		if (cub == null)
		{
			ServerPlugin.Log.LogWarning("Tierarten: Bärenjunges nicht angelegt, Bären bekommen keine Jungen.");
			return;
		}
		foreach (Type type in new[] { typeof(Procreation), typeof(CharacterDrop), typeof(Growup) })
		{
			foreach (Component part in cub.GetComponents(type))
			{
				UnityEngine.Object.DestroyImmediate(part);
			}
		}
		cub.transform.localScale = bear.transform.localScale * CubScale;
		Character? character = cub.GetComponent<Character>();
		Character? adult = bear.GetComponent<Character>();
		if (character != null)
		{
			character.m_name = CubName;
			character.m_health = (adult != null ? adult.m_health : character.m_health) * CubScale;
		}
		Growup grow = cub.AddComponent<Growup>();
		grow.m_growTime = CubGrowTime;
		grow.m_grownPrefab = bear;
		grow.m_inheritTame = true;
		foreach (Renderer renderer in cub.GetComponentsInChildren<Renderer>(true))
		{
			Material[] materials = renderer.sharedMaterials;
			bool changed = false;
			for (int i = 0; i < materials.Length; i++)
			{
				if (materials[i] != null && materials[i].HasProperty("_Saturation"))
				{
					Material copy = new(materials[i]) { name = materials[i].name + "_gc_cub" };
					copy.SetFloat("_Saturation", copy.GetFloat("_Saturation") + CubSaturation);
					materials[i] = copy;
					changed = true;
				}
			}
			if (changed)
			{
				renderer.sharedMaterials = materials;
			}
		}
		PrefabManager.Instance.AddPrefab(new CustomPrefab(cub, true));
	}

	/// <summary>Bären bekommen Junge (Gorduan): Zucht wie beim Lox (großes Tier), Junges ist das Bärenjunge.</summary>
	private static void AddProcreation(GameObject bear)
	{
		GameObject? cub = PrefabManager.Instance.GetPrefab(CubPrefab);
		Procreation? template = PrefabManager.Instance.GetPrefab("Lox")?.GetComponent<Procreation>();
		if (cub == null || bear.GetComponent<Procreation>() != null)
		{
			return;
		}
		Procreation procreation = bear.AddComponent<Procreation>();
		procreation.m_offspring = cub;
		procreation.m_updateInterval = 30f;
		procreation.m_pregnancyChance = 0.33f;
		procreation.m_pregnancyDuration = 120f;
		procreation.m_requiredLovePoints = 4;
		procreation.m_maxCreatures = 4;
		procreation.m_totalCheckRange = 20f;
		procreation.m_partnerCheckRange = 8f;
		procreation.m_spawnOffset = 2f;
		if (template != null)
		{
			procreation.m_birthEffects = template.m_birthEffects;
			procreation.m_loveEffects = template.m_loveEffects;
		}
	}

	// ------------------------------------------------------------------ Sterne bei der Geburt

	/// <summary>Nach Valheims Geburt (Procreate): vielleicht ein Stern mehr, höchstens einer (Gorduan).</summary>
	internal static void Mutate(Care.Spawning? birth)
	{
		if (birth == null || !birth.Done)
		{
			return;
		}
		if (birth.Born != null && birth.Born.m_nview != null && birth.Born.m_nview.IsValid() && birth.Born.m_nview.IsOwner())
		{
			Character born = birth.Born;
			int level = born.GetLevel();
			int next = Roll(level);
			if (next > level)
			{
				born.SetLevel(next);
				born.SetHealth(born.GetMaxHealth());
				Tell(birth.Breeder, born.transform.position, next == 2 ? "gcs_star_born1" : "gcs_star_born2", born.GetHoverName());
			}
			return;
		}
		ItemDrop? egg = birth.Egg;
		if (egg != null && egg.m_nview != null && egg.m_nview.IsValid() && egg.m_nview.IsOwner())
		{
			int quality = egg.m_itemData.m_quality;
			int next = Roll(quality);
			if (next > quality)
			{
				egg.SetQuality(next);
				egg.Save();
				Tell(birth.Breeder, egg.transform.position, next == 2 ? "gcs_star_egg1" : "gcs_star_egg2", Localization.instance.Localize(egg.m_itemData.m_shared.m_name));
			}
		}
	}

	private static int Roll(int level)
	{
		float roll = UnityEngine.Random.value * 100f;
		if (level == 1 && roll < StarChance1.Value)
		{
			return 2;
		}
		if (level == 2 && roll < StarChance2.Value)
		{
			return 3;
		}
		return level;
	}

	internal static void RegisterRpcs()
	{
		ZRoutedRpc.instance?.Register<string, string>(TellRpc, RPC_Tell);
	}

	/// <summary>Meldung an den Züchter (oder, ohne Züchter, an den nächsten Spieler bis 30 m).</summary>
	private static void Tell(long session, Vector3 position, string key, string name)
	{
		Player? local = Player.m_localPlayer;
		if (session == 0L)
		{
			Player? near = Player.GetClosestPlayer(position, 30f);
			if (near == null)
			{
				return;
			}
			session = Links.Session(near);
		}
		if (local != null && Links.Session(local) == session)
		{
			local.Message(MessageHud.MessageType.Center, GcLoc.Get(key, name));
			return;
		}
		ZRoutedRpc.instance?.InvokeRoutedRPC(session, TellRpc, key, name);
	}

	private static void RPC_Tell(long sender, string key, string name)
	{
		if (key.StartsWith("gcs_", StringComparison.Ordinal))
		{
			Player.m_localPlayer?.Message(MessageHud.MessageType.Center, GcLoc.Get(key, name));
		}
	}

	// ------------------------------------------------------------------ Fressen der einfachen Tier-KI

	private sealed class Graze
	{
		public ItemDrop? Target;
		public float NextSearch;
	}

	private static readonly Dictionary<AnimalAI, Graze> Grazing = new();
	private static int itemMask;

	/// <summary>Hirsch, Hase, Robbe beim Zähmen: hungrig und ohne Gefahr suchen sie Futter im Umkreis von 10 m und fressen es.</summary>
	internal static void GrazeUpdate(AnimalAI ai, float dt)
	{
		Character? character = ai.m_character;
		if (character == null || character.IsTamed())
		{
			return;
		}
		Kind kind = Of(character);
		if (!kind.Optional || !Enabled(kind) || kind.FoodNames.Count == 0 || ai.IsAlerted())
		{
			return;
		}
		Tameable? tame = character.GetComponent<Tameable>();
		if (tame == null || !tame.IsHungry())
		{
			return;
		}
		if (!Grazing.TryGetValue(ai, out Graze graze))
		{
			Grazing[ai] = graze = new Graze();
		}
		float now = Time.time;
		if (graze.Target == null && now >= graze.NextSearch)
		{
			graze.NextSearch = now + 2f;
			graze.Target = FindFood(ai, kind);
		}
		ItemDrop? target = graze.Target;
		if (target == null)
		{
			return;
		}
		if (target.m_nview == null || !target.m_nview.IsValid())
		{
			graze.Target = null;
			return;
		}
		if (ai.MoveTo(dt, target.transform.position, 1.2f, false))
		{
			ai.LookAt(target.transform.position);
			if (target.RemoveOne())
			{
				tame.OnConsumedItem(target);
				eatEffects?.Create(character.transform.position, Quaternion.identity);
			}
			graze.Target = null;
		}
	}

	private static ItemDrop? FindFood(AnimalAI ai, Kind kind)
	{
		if (itemMask == 0)
		{
			itemMask = LayerMask.GetMask("item");
		}
		ItemDrop? best = null;
		float bestDistance = float.MaxValue;
		foreach (Collider collider in Physics.OverlapSphere(ai.transform.position, 10f, itemMask))
		{
			ItemDrop? item = collider.attachedRigidbody != null ? collider.attachedRigidbody.GetComponent<ItemDrop>() : null;
			if (item == null || item.m_nview == null || !item.m_nview.IsValid() || !kind.FoodNames.Contains(item.m_itemData.m_shared.m_name))
			{
				continue;
			}
			float distance = Vector3.Distance(item.transform.position, ai.transform.position);
			if (distance < bestDistance)
			{
				best = item;
				bestDistance = distance;
			}
		}
		return best != null && ai.HavePath(best.transform.position) ? best : null;
	}

	/// <summary>Zähmen für Tiere ohne Monster-KI (Valheims TamingUpdate kann nur Monster-KI): satt und ohne Gefahr alle 3 s weiter.</summary>
	internal static void AnimalTamingUpdate(Tameable tame, Character character)
	{
		ZNetView? view = tame.m_nview;
		BaseAI? ai = character.GetBaseAI();
		if (view == null || !view.IsValid() || !view.IsOwner() || tame.IsHungry() || ai == null || ai.IsAlerted())
		{
			return;
		}
		tame.DecreaseRemainingTime(3f);
		if (tame.GetRemainingTime() > 0f)
		{
			tame.m_sootheEffect.Create(character.transform.position, character.transform.rotation);
			return;
		}
		character.SetTamed(true);
		tame.m_tamedEffect.Create(character.transform.position, character.transform.rotation);
		Game.instance.IncrementPlayerStat(PlayerStatType.CreatureTamed);
		Player? closest = Player.GetClosestPlayer(character.transform.position, 30f);
		closest?.Message(MessageHud.MessageType.Center, character.m_name + " $hud_tamedone");
		Care.Grant(Care.CaretakerOf(character, view.GetZDO(), 30f), Care.TameXp.Value);
	}

	// ------------------------------------------------------------------ Unbjørn

	/// <summary>Ein Bär hat gefressen: verrottetes Fleisch startet die Verwandlung (30 Minuten Weltzeit).</summary>
	internal static void OnBearAte(Tameable tame, ItemDrop? item)
	{
		Character? bear = tame.m_character;
		ZNetView? view = tame.m_nview;
		if (item == null || bear == null || view == null || !view.IsValid() || !view.IsOwner() || !Enabled(Bear) || ZNet.instance == null)
		{
			return;
		}
		if (view.GetZDO().GetPrefab() != BearHash || item.m_itemData.m_shared.m_name != rottenMeatName)
		{
			return;
		}
		ZDO zdo = view.GetZDO();
		if (zdo.GetLong(TransformHash, 0L) == 0L)
		{
			zdo.Set(TransformHash, ZNet.instance.GetTime().AddSeconds(TransformSeconds).Ticks);
		}
	}

	/// <summary>Alle 5 s beim Spieler: fällige Verwandlungen (nur eigene Bären), alte Fress-Ziele aufräumen.</summary>
	internal static void Tick()
	{
		float now = Time.time;
		if (now < nextTick || ZNet.instance == null || ZNetScene.instance == null)
		{
			return;
		}
		nextTick = now + 5f;
		if (Grazing.Count > 0)
		{
			List<AnimalAI> gone = new();
			foreach (KeyValuePair<AnimalAI, Graze> entry in Grazing)
			{
				if (entry.Key == null)
				{
					gone.Add(entry.Key!);
				}
			}
			foreach (AnimalAI ai in gone)
			{
				Grazing.Remove(ai);
			}
		}
		if (!Enabled(Bear))
		{
			return;
		}
		long ticks = ZNet.instance.GetTime().Ticks;
		foreach (Character character in new List<Character>(Character.GetAllCharacters()))
		{
			ZNetView? view = character != null ? character.m_nview : null;
			if (view == null || !view.IsValid() || !view.IsOwner() || character!.IsDead())
			{
				continue;
			}
			ZDO zdo = view.GetZDO();
			if (zdo.GetPrefab() != BearHash)
			{
				continue;
			}
			long due = zdo.GetLong(TransformHash, 0L);
			if (due != 0L && ticks >= due)
			{
				Transform(character);
			}
		}
	}

	/// <summary>Bär → Unbjørn (Gorduan): Stufe, Sterne, Name, Leine, Zahm-Status übernehmen; der Bärensattel fällt herunter.</summary>
	private static void Transform(Character bear)
	{
		GameObject? prefab = ZNetScene.instance.GetPrefab("Unbjorn");
		ZDO from = bear.m_nview.GetZDO();
		from.Set(TransformHash, 0L);
		if (prefab == null)
		{
			return;
		}
		Vector3 position = bear.transform.position;
		GameObject go = UnityEngine.Object.Instantiate(prefab, position, bear.transform.rotation);
		Character? unbjorn = go.GetComponent<Character>();
		if (unbjorn == null || unbjorn.m_nview == null || !unbjorn.m_nview.IsValid())
		{
			return;
		}
		bool tamed = bear.IsTamed();
		unbjorn.SetLevel(bear.GetLevel());
		if (tamed)
		{
			unbjorn.SetTamed(true);
		}
		ZDO to = unbjorn.m_nview.GetZDO();
		to.Set(Care.LevelHash, from.GetFloat(Care.LevelHash, 0f));
		to.Set(AnimalExperience.XpHash, from.GetFloat(AnimalExperience.XpHash, 0f));
		Young.CopyState(bear, unbjorn); // Name, Fütterer, Pfosten
		Young.Relink(bear, unbjorn, "gcls_transformed"); // eigene Leine
		if (from.GetBool(ZDOVars.s_haveSaddleHash))
		{
			GameObject? saddle = ZNetScene.instance.GetPrefab(Saddles.BearSaddle);
			if (saddle != null)
			{
				// der Bärensattel fällt mit seiner Verzauberung herunter (Daten aus dem Bären-ZDO)
				string packed = from.GetString(Saddles.DataHash);
				Saddles.Dropping = packed.Length > 0 ? packed : null;
				try
				{
					UnityEngine.Object.Instantiate(saddle, position + Vector3.up * 1.5f, Quaternion.identity);
				}
				finally
				{
					Saddles.Dropping = null;
				}
			}
		}
		foreach (string effect in new[] { "fx_unbjorn_hit", "vfx_spawn_large" })
		{
			GameObject? fx = ZNetScene.instance.GetPrefab(effect);
			if (fx != null)
			{
				UnityEngine.Object.Instantiate(fx, position, Quaternion.identity);
			}
		}
		string name = bear.GetHoverName();
		foreach (Player player in Player.GetAllPlayers())
		{
			if (player != null && Vector3.Distance(player.transform.position, position) < 40f)
			{
				Tell(Links.Session(player), position, "gcs_unbjorn", name);
			}
		}
		ZNetScene.instance.Destroy(bear.gameObject);
		ServerPlugin.Log.LogInfo($"Tierarten: {name} hat sich in einen Unbjørn verwandelt (zahm: {tamed}).");
	}

	// ------------------------------------------------------------------ Feindschaft

	/// <summary>Für den Befehl „angreifen“: Valheims Feindschaft, ohne den Bärenfrieden.</summary>
	internal static bool EnemyForOrder(Character animal, Character target)
	{
		bypassPeace = true;
		try
		{
			return BaseAI.IsEnemy(animal, target);
		}
		finally
		{
			bypassPeace = false;
		}
	}

	internal static void EnemyRules(Character a, Character b, ref bool result)
	{
		if (a == null || b == null || a == b)
		{
			return;
		}
		// Unbjørn aus zahmen Bären: greift alles Wilde von selbst an, keine Spieler und keine zahmen Tiere (Gorduan)
		if (a.m_name == UnbjornName && a.IsTamed())
		{
			if (IsWild(b))
			{
				result = true;
			}
			return;
		}
		if (b.m_name == UnbjornName && b.IsTamed())
		{
			if (IsWild(a))
			{
				result = true;
			}
			return;
		}
		// zahme Bären greifen wilde Bären nicht von selbst an, auf Befehl schon (Gorduan)
		if (!result || bypassPeace || !IsBear(a) || !IsBear(b) || a.IsTamed() == b.IsTamed())
		{
			return;
		}
		Character tamed = a.IsTamed() ? a : b;
		Character wild = a.IsTamed() ? b : a;
		Link? link = Links.ForAnimal(tamed);
		if (link == null || link.AttackTarget != wild)
		{
			result = false;
		}
	}

	// ------------------------------------------------------------------ Beute

	/// <summary>
	/// Gorduan: mit 1:1-Chance mehr Beute beim Schlachten, fließend nach der Stufe. Höchstmaß 1 + Stufe ÷ 50; getroffen wird
	/// zufällig zwischen dem Doppelten (darunter dem Höchstmaß selbst) und dem Höchstmaß. Ein Wurf je Tier für jede Beute;
	/// gerundet wird zufällig nach dem Rest (1 Haut × 1,2 gibt in 20 % der Fälle 2), damit auch kleine Mengen fließend mehr
	/// werden. Valheims Sterne zählen wie immer dazu.
	/// </summary>
	internal static void LevelLoot(CharacterDrop drop, List<KeyValuePair<GameObject, int>> loot)
	{
		Character? character = drop.m_character;
		ZNetView? view = character != null ? character.m_nview : null;
		if (loot == null || loot.Count == 0 || view == null || !view.IsValid())
		{
			return;
		}
		float level = view.GetZDO().GetFloat(Care.LevelHash, 0f);
		if (level <= 0f || UnityEngine.Random.value >= 0.5f)
		{
			return; // ohne Stufe oder die 1:1-Chance nicht getroffen: normale Beute
		}
		float max = 1f + level / LootStep;
		float factor = UnityEngine.Random.Range(Mathf.Min(2f, max), max);
		for (int i = 0; i < loot.Count; i++)
		{
			float amount = loot[i].Value * factor;
			int whole = Mathf.FloorToInt(amount);
			if (UnityEngine.Random.value < amount - whole)
			{
				whole++;
			}
			loot[i] = new KeyValuePair<GameObject, int>(loot[i].Key, Mathf.Max(loot[i].Value, whole));
		}
	}
}

// ---------------------------------------------------------------------- Patches

/// <summary>Monster-KI frisst nur, was ihre Art fressen darf (abgeschaltet: nur Valheims eigenes Futter).</summary>
[HarmonyPatch(typeof(MonsterAI), "UpdateConsumeItem")]
internal static class SpeciesConsumePatch
{
	private static void Prefix(MonsterAI __instance)
	{
		Character? character = __instance.m_character;
		Species.Kind kind = Species.Of(character);
		if (!kind.Optional || kind.AllFood.Count == 0)
		{
			return;
		}
		bool open = Species.Enabled(kind) || (character != null && character.IsTamed());
		List<ItemDrop> wanted = open ? kind.AllFood : kind.VanillaFood;
		if (__instance.m_consumeItems != wanted)
		{
			__instance.m_consumeItems = wanted;
		}
	}
}

/// <summary>Zähmen: abgeschaltete Arten und der Unbjørn nicht; Tiere ohne Monster-KI über Species.AnimalTamingUpdate.</summary>
[HarmonyPatch(typeof(Tameable), "TamingUpdate")]
internal static class SpeciesTamingPatch
{
	private static bool Prefix(Tameable __instance)
	{
		Character? character = __instance.m_character;
		if (character == null || character.IsTamed())
		{
			return true;
		}
		Species.Kind kind = Species.Of(character);
		if (kind == Species.Unbjorn)
		{
			return false; // Gorduan: Unbjørn lassen sich nicht zähmen (nur aus einem zahmen Bären)
		}
		if (!kind.Optional)
		{
			return true;
		}
		if (!Species.Enabled(kind))
		{
			return false;
		}
		if (__instance.m_monsterAI != null)
		{
			return true; // Nixe, Bär: Valheims Zähmen
		}
		Species.AnimalTamingUpdate(__instance, character);
		return false;
	}
}

/// <summary>Abgeschaltete Arten zeigen wie in Valheim keinen Zähm-Hinweis.</summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.GetHoverText))]
internal static class SpeciesHoverPatch
{
	private static void Postfix(Tameable __instance, ref string __result)
	{
		Character? character = __instance.m_character;
		if (character == null || character.IsTamed())
		{
			return;
		}
		Species.Kind kind = Species.Of(character);
		if (kind == Species.Unbjorn || (kind.Optional && !Species.Enabled(kind)))
		{
			__result = "";
		}
	}
}

/// <summary>Hirsch, Hase, Robbe: beim Zähmen Futter vom Boden fressen.</summary>
[HarmonyPatch(typeof(AnimalAI), nameof(AnimalAI.UpdateAI))]
internal static class SpeciesGrazePatch
{
	private static void Postfix(AnimalAI __instance, float dt, bool __result)
	{
		if (__result)
		{
			Species.GrazeUpdate(__instance, dt);
		}
	}
}

/// <summary>Bären-Zucht nur, wenn Bären zähmbar sind; nach jeder Geburt vielleicht ein Stern mehr.</summary>
[HarmonyPatch(typeof(Procreation), "Procreate")]
internal static class SpeciesProcreatePatch
{
	private static bool Prefix(Procreation __instance)
	{
		Character? character = __instance.m_character;
		return character == null || Species.Of(character) != Species.Bear || Species.Enabled(Species.Bear);
	}

	private static void Postfix() => Species.Mutate(Care.Birth);
}

/// <summary>Verrottetes Fleisch im Bären: Verwandlung in 30 Minuten.</summary>
[HarmonyPatch(typeof(Tameable), "OnConsumedItem")]
internal static class SpeciesBearAtePatch
{
	private static void Postfix(Tameable __instance, ItemDrop item) => Species.OnBearAte(__instance, item);
}

/// <summary>Bärenfrieden und der wilde Unbjørn aus zahmen Bären.</summary>
[HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IsEnemy), typeof(Character), typeof(Character))]
internal static class SpeciesEnemyPatch
{
	private static void Postfix(Character a, Character b, ref bool __result) => Species.EnemyRules(a, b, ref __result);
}

/// <summary>Bärenjunges: ein Drittel Schaden (Gorduan).</summary>
[HarmonyPatch(typeof(Character), nameof(Character.Damage))]
internal static class SpeciesCubDamagePatch
{
	private static void Prefix(HitData hit)
	{
		Character? attacker = hit?.GetAttacker();
		if (attacker != null && attacker.m_name == Species.CubName)
		{
			hit!.ApplyModifier(Species.CubScale);
		}
	}
}

/// <summary>Leiche eines Bärenjungen: so klein wie das Junge (Größe im Ragdoll-ZDO, die anderen Spieler lesen sie beim Erscheinen).</summary>
[HarmonyPatch(typeof(Humanoid), "OnRagdollCreated")]
internal static class SpeciesCubRagdollPatch
{
	private static void Postfix(Humanoid __instance, Ragdoll ragdoll)
	{
		if (ragdoll == null || __instance.m_name != Species.CubName)
		{
			return;
		}
		Transform body = ragdoll.transform;
		if (body.localScale.x > __instance.transform.localScale.x * 1.1f)
		{
			body.localScale *= Species.CubScale;
		}
		ZNetView? nview = ragdoll.GetComponent<ZNetView>();
		if (nview != null && nview.IsValid())
		{
			nview.GetZDO().Set(GcKeys.RagdollScaleHash, body.localScale.x); // der Kern gleicht die Leiche bei allen ab
		}
	}
}

/// <summary>Mehr Beute nach der Stufe (auch für die Beute, die eine Leiche später fallen lässt).</summary>
[HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
internal static class SpeciesLootPatch
{
	private static void Postfix(CharacterDrop __instance, List<KeyValuePair<GameObject, int>> __result) => Species.LevelLoot(__instance, __result);
}
