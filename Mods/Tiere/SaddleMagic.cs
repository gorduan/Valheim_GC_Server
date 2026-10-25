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
/// Sättel als EpicLoot-Items (Gorduan, 20.10.2026): alle Sättel, Valheims Lox- und Asksvin-Sattel und die Sättel aus
/// GC_Tierarten (melden sich mit AddName an). Sie würfeln die Effekte der Tierpfeifen und eigene Sattel-Effekte
/// (Ausdauer). Die Verzauberung wirkt nur, solange geritten wird: Tempo, Leben, Schutz und Ausdauer am Reittier,
/// Schaden, Elemente, Lebensraub und Wucht an den Treffern des Reiters. Beim Aufsatteln steht das ganze Sattel-Item
/// (mit Verzauberung) im Tier; fällt der Sattel herunter, bekommt das neue Item die Daten zurück.
/// Seit dem Kern Teil von GC_Tiere (EpicLoot-Teil); Modelle und Rezepte der Bärensättel stehen in GC_Tierarten.
/// </summary>
internal static class Saddles
{
	internal const string BearSaddle = "GC_SaddleBjorn";
	internal const string UnbjornSaddle = "GC_SaddleUnbjorn";
	internal static readonly string[] VanillaSaddles = { "SaddleLox", "SaddleAsksvin" };

	/// <summary>Im Tier: das Sattel-Item (ZPackage, Base64) und nur seine EpicLoot-Verzauberung (schnell lesbar).</summary>
	internal static readonly int DataHash = "gc_saddle_data".GetStableHashCode();
	internal static readonly int MagicHash = "gc_saddle_magic".GetStableHashCode();
	private const string MagicKey = "randyknapp.mods.epicloot#EpicLoot.MagicItemComponent";

	/// <summary>Item-Namen aller Sättel (Valheims und die von GC_Tierarten).</summary>
	private static readonly HashSet<string> SaddleNames = new();

	internal static void Init(ConfigFile config)
	{
		PrefabManager.OnVanillaPrefabsAvailable += AddVanilla;
	}

	/// <summary>Weiterer Sattel (GC_Tierarten): Item-Name wie m_shared.m_name.</summary>
	internal static void AddName(string itemName) => SaddleNames.Add(itemName);

	private static void AddVanilla()
	{
		PrefabManager.OnVanillaPrefabsAvailable -= AddVanilla;
		foreach (string name in VanillaSaddles)
		{
			ItemDrop? vanilla = PrefabManager.Instance.GetPrefab(name)?.GetComponent<ItemDrop>();
			if (vanilla != null)
			{
				SaddleNames.Add(vanilla.m_itemData.m_shared.m_name);
			}
		}
	}

	// ------------------------------------------------------------------ Abfragen

	/// <summary>Ein Sattel (eigene und Valheims), erkannt am Item-Namen.</summary>
	internal static bool IsSaddle(ItemDrop.ItemData? item) => item?.m_shared != null && SaddleNames.Contains(item.m_shared.m_name);

	/// <summary>Das Tier, auf dem dieser Spieler gerade reitet (null: reitet nicht).</summary>
	internal static Character? RiddenMount(Player? player)
	{
		return player != null && player.m_doodadController is Sadle sadle && sadle.IsValid() ? sadle.GetCharacter() : null;
	}

	/// <summary>Verzauberung des Sattels auf diesem Tier (aus dem Tier-ZDO), sonst nichts.</summary>
	internal static BondStats Bond(Character? mount)
	{
		ZNetView? view = mount != null ? mount.m_nview : null;
		if (!LeashMagic.Available || view == null || !view.IsValid())
		{
			return BondStats.None;
		}
		string json = view.GetZDO().GetString(MagicHash);
		return json.Length > 0 ? LeashMagic.ReadJson(json) : BondStats.None;
	}

	/// <summary>Werte des Sattels für die Treffer des Reiters (nur der eigene Spieler, nur beim Reiten).</summary>
	internal static bool RiderBond(Character? rider, out BondStats bond)
	{
		bond = BondStats.None;
		if (rider == null || rider != Player.m_localPlayer)
		{
			return false;
		}
		Character? mount = RiddenMount(Player.m_localPlayer);
		if (mount == null)
		{
			return false;
		}
		bond = Bond(mount);
		return bond.AnimalAny;
	}

	// ------------------------------------------------------------------ Sattel-Item im Tier

	/// <summary>Das ganze Item (Qualität, Hersteller, alle Item-Daten samt EpicLoot) als Text fürs Tier-ZDO.</summary>
	internal static string Pack(ItemDrop.ItemData item)
	{
		ZPackage pkg = new();
		pkg.Write(1); // Fassung
		pkg.Write(item.m_quality);
		pkg.Write(item.m_variant);
		pkg.Write(item.m_durability);
		pkg.Write(item.m_crafterID);
		pkg.Write(item.m_crafterName ?? "");
		pkg.Write((int)item.m_worldLevel);
		Dictionary<string, string> data = item.m_customData ?? new Dictionary<string, string>();
		pkg.Write(data.Count);
		foreach (KeyValuePair<string, string> entry in data)
		{
			pkg.Write(entry.Key);
			pkg.Write(entry.Value ?? "");
		}
		return pkg.GetBase64();
	}

	internal static void Unpack(string text, ItemDrop.ItemData item)
	{
		ZPackage pkg = new(text);
		if (pkg.ReadInt() != 1)
		{
			return;
		}
		item.m_quality = pkg.ReadInt();
		item.m_variant = pkg.ReadInt();
		item.m_durability = pkg.ReadSingle();
		item.m_crafterID = pkg.ReadLong();
		item.m_crafterName = pkg.ReadString();
		item.m_worldLevel = (byte)pkg.ReadInt();
		int count = pkg.ReadInt();
		item.m_customData = new Dictionary<string, string>();
		for (int i = 0; i < count; i++)
		{
			string key = pkg.ReadString();
			item.m_customData[key] = pkg.ReadString();
		}
	}

	/// <summary>Beim Aufsatteln: der Spieler übernimmt das Tier (wie beim Reiten), damit er die Daten ins Tier schreiben darf.</summary>
	internal static string? BeforeUse(Tameable tame, ItemDrop.ItemData item)
	{
		ZNetView? view = tame.m_nview;
		if (tame.m_saddleItem == null || item?.m_shared == null || view == null || !view.IsValid() || !tame.IsTamed()
			|| item.m_shared.m_name != tame.m_saddleItem.m_itemData.m_shared.m_name || view.GetZDO().GetBool(ZDOVars.s_haveSaddleHash))
		{
			return null;
		}
		if (!view.IsOwner())
		{
			view.ClaimOwnership();
		}
		return Pack(item);
	}

	internal static void AfterUse(Tameable tame, string? packed, bool used)
	{
		ZNetView? view = tame.m_nview;
		if (packed == null || !used || view == null || !view.IsValid() || !view.GetZDO().GetBool(ZDOVars.s_haveSaddleHash))
		{
			return;
		}
		ItemDrop.ItemData copy = new() { m_shared = tame.m_saddleItem.m_itemData.m_shared };
		Unpack(packed, copy);
		view.GetZDO().Set(DataHash, packed);
		view.GetZDO().Set(MagicHash, copy.m_customData.TryGetValue(MagicKey, out string json) ? json : "");
	}

	/// <summary>Während Valheim den Sattel als Item fallen lässt (Absatteln, Tod): diese Daten gehören ins neue Item.</summary>
	internal static string? Dropping;

	internal static void OnSaddleItemAwake(ItemDrop item)
	{
		string? packed = Dropping;
		if (packed == null || !IsSaddle(item.m_itemData) || item.m_nview == null || !item.m_nview.IsValid())
		{
			return;
		}
		Dropping = null;
		Unpack(packed, item.m_itemData);
		item.Save();
	}
}

// ---------------------------------------------------------------------- Patches

/// <summary>Aufsatteln: das Sattel-Item (mit Verzauberung) ins Tier.</summary>
[HarmonyPatch(typeof(Tameable), nameof(Tameable.UseItem))]
internal static class SaddleUsePatch
{
	private static void Prefix(Tameable __instance, ItemDrop.ItemData item, out string? __state) => __state = Saddles.BeforeUse(__instance, item);

	private static void Postfix(Tameable __instance, string? __state, bool __result) => Saddles.AfterUse(__instance, __state, __result);
}

/// <summary>Absatteln oder Tod: das neue Sattel-Item bekommt die Daten aus dem Tier zurück.</summary>
[HarmonyPatch(typeof(Tameable), "SpawnSaddle")]
internal static class SaddleSpawnPatch
{
	private static void Prefix(Tameable __instance)
	{
		ZNetView? view = __instance.m_nview;
		string packed = view != null && view.IsValid() ? view.GetZDO().GetString(Saddles.DataHash) : "";
		Saddles.Dropping = packed.Length > 0 ? packed : null;
	}

	private static void Postfix(Tameable __instance)
	{
		ZNetView? view = __instance.m_nview;
		if (view != null && view.IsValid() && view.IsOwner())
		{
			view.GetZDO().Set(Saddles.DataHash, "");
			view.GetZDO().Set(Saddles.MagicHash, "");
		}
	}

	private static void Finalizer() => Saddles.Dropping = null;
}

[HarmonyPatch(typeof(ItemDrop), "Awake")]
internal static class SaddleItemAwakePatch
{
	private static void Postfix(ItemDrop __instance)
	{
		if (Saddles.Dropping != null)
		{
			Saddles.OnSaddleItemAwake(__instance);
		}
	}
}

/// <summary>Sattel-Effekt „Ausdauer“: mehr Ausdauer des Reittiers.</summary>
[HarmonyPatch(typeof(Sadle), nameof(Sadle.GetMaxStamina))]
internal static class SaddleMaxStaminaPatch
{
	private static void Postfix(Sadle __instance, ref float __result)
	{
		BondStats bond = Saddles.Bond(__instance.m_character);
		if (bond.MountStamina > 0f)
		{
			__result *= 1f + bond.MountStamina / 100f;
		}
	}
}

/// <summary>Sattel-Effekt „Sparsam“: weniger Ausdauer beim Rennen und Schwimmen.</summary>
[HarmonyPatch(typeof(Sadle), "UseStamina")]
internal static class SaddleUseStaminaPatch
{
	private static void Prefix(Sadle __instance, ref float v)
	{
		BondStats bond = Saddles.Bond(__instance.m_character);
		if (bond.MountStaminaSave > 0f)
		{
			v *= Mathf.Max(0.1f, 1f - bond.MountStaminaSave / 100f);
		}
	}
}

/// <summary>Sattel-Effekt „Ausdauer-Erholung“: das Reittier erholt sich schneller.</summary>
[HarmonyPatch(typeof(Sadle), "UpdateStamina")]
internal static class SaddleRegenPatch
{
	private static void Prefix(Sadle __instance, out Vector2 __state)
	{
		__state = new Vector2(__instance.m_staminaRegen, __instance.m_staminaRegenHungry);
		BondStats bond = Saddles.Bond(__instance.m_character);
		if (bond.MountStaminaRegen > 0f)
		{
			float factor = 1f + bond.MountStaminaRegen / 100f;
			__instance.m_staminaRegen *= factor;
			__instance.m_staminaRegenHungry *= factor;
		}
	}

	private static void Postfix(Sadle __instance, Vector2 __state)
	{
		__instance.m_staminaRegen = __state.x;
		__instance.m_staminaRegenHungry = __state.y;
	}
}

