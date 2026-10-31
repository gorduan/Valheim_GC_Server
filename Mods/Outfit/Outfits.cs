using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Shared;
using HarmonyLib;

namespace GC.Server.Outfit;

/// <summary>
/// Outfit (Gorduan, 30.10.2026: „das wir einen Kleiderschrank brauchen, andem man sein Outfit ändern kann. gegenstände kommen so
/// nicht ins inventar sondern nur ins outfit und können ohne Kleiderschrank auch nicht gewechselt werden, outfit verliert man
/// nicht beim sterben“):
///  * 18 Plätze (6 × 3): Helm, Brust, Beine, Umhang, Schild, Gürtel; je Waffenart einer (Schwert, Axt, Keule, Messer, Speer,
///    Stangenwaffe, Bihänder, Streitaxt, Hammer, Bogen, Armbrust, Stab). Rüstungsplätze nehmen nur ihre Art, Waffenplätze
///    jede Waffe; Werkzeuge und die Fackel nie (Gorduan: „keine Werkzeuge“, die Fackel bleibt echt, sonst wanderte ihr Licht mit).
///  * Ein Item im Outfit liegt nicht im Inventar: eigenes Inventar 6 × 3, gespeichert mit dem Charakter in
///    Player.m_customData (wie die Leinenleiste). Es zählt kein Gewicht und kommt beim Tod nicht ins Grab.
///  * Aussehen: Trägt der Spieler ein Teil, dessen Platz im Outfit belegt ist, zeigt sein Charakter das Outfit-Teil. Ein leerer
///    Platz zeigt das echte Teil, ohne getragenes Teil zeigt der Platz nichts. Jedes Rüstungsteil lässt sich ausblenden.
///  * Werte, Angriffe und Effekte kommen weiter vom echten Item: Getauscht wird nur, was Valheim an VisEquipment meldet. Das
///    landet im Spieler-ZDO; alle Mitspieler sehen das Outfit, auch ohne die Mod.
///  * Server-Regel Enabled (Standard an): aus = alle sehen die echte Ausrüstung, der Schrank lässt sich nicht bauen, und am
///    vorhandenen Schrank kann man Teile nur herausnehmen.
/// </summary>
internal static class Outfits
{
	internal const int Width = 6;
	internal const int Height = 3;
	internal const int Slots = Width * Height;
	internal const int ArmorSlots = 6;

	internal const int Helmet = 0;
	internal const int Chest = 1;
	internal const int Legs = 2;
	internal const int Cape = 3;
	internal const int Shield = 4;
	internal const int Belt = 5;
	internal const int Sword = 6;
	internal const int Axe = 7;
	internal const int Club = 8;
	internal const int Knife = 9;
	internal const int Spear = 10;
	internal const int Polearm = 11;
	internal const int Sword2H = 12;
	internal const int Axe2H = 13;
	internal const int Club2H = 14;
	internal const int Bow = 15;
	internal const int Crossbow = 16;
	internal const int Staff = 17;

	private const string DataKey = "gc_outfit";
	private const string HideKey = "gc_outfit_hide";
	private const string BrokenKey = "gc_outfit_kaputt";

	internal static ConfigEntry<bool> Enabled = null!;
	internal static ManualLogSource Log = null!;

	private static Player? owner;
	private static Inventory? outfit;
	private static int hidden;

	internal static bool On => Enabled.Value && ModOutfit.Plugin.Module.Allowed;

	internal static void Init(ConfigFile config, ManualLogSource log)
	{
		Log = log;
		Enabled = Gc.BindSynced(config, "1 - Outfit", "Enabled", true, "Kleiderschrank und Outfits (vom Server vorgegeben). Aus: alle sehen die echte Ausrüstung, der Schrank lässt sich nicht bauen, Teile lassen sich am Schrank nur herausnehmen.");
		Enabled.SettingChanged += (_, _) =>
		{
			Wardrobes.ApplyEnabled();
			Refresh(Player.m_localPlayer);
		};
	}

	internal static Vector2i Pos(int slot) => new(slot % Width, slot / Width);

	internal static int SlotAt(Vector2i pos) => pos.x >= 0 && pos.x < Width && pos.y >= 0 && pos.y < Height ? pos.y * Width + pos.x : -1;

	internal static bool IsArmor(int slot) => slot >= 0 && slot < ArmorSlots;

	// ------------------------------------------------------------------ Inventar des Outfits

	/// <summary>Outfit des eigenen Spielers (andere Spieler haben hier keins), beim ersten Zugriff aus dem Charakter geladen.</summary>
	internal static Inventory? Of(Player? player)
	{
		if (player == null || player != Player.m_localPlayer)
		{
			return null;
		}
		if (owner != player || outfit == null)
		{
			owner = player;
			outfit = Load(player);
		}
		return outfit;
	}

	internal static bool IsOutfit(Inventory? inventory) => inventory != null && inventory == outfit;

	private static Inventory Load(Player player)
	{
		Inventory inventory = new("GC_Outfit", null, Width, Height);
		hidden = player.m_customData.TryGetValue(HideKey, out string mask) && int.TryParse(mask, out int bits) ? bits : 0;
		if (player.m_customData.TryGetValue(DataKey, out string data) && data.Length > 0)
		{
			try
			{
				ZPackage pkg = new(Convert.FromBase64String(data));
				int saved = SavedCount(data);
				inventory.Load(pkg);
				foreach (ItemDrop.ItemData item in inventory.GetAllItems())
				{
					item.m_equipped = false;
				}
				if (inventory.NrOfItems() < saved)
				{
					// ein Teil kannte das Spiel nicht (Mod entfernt): nichts verlieren, der alte Stand bleibt daneben liegen
					player.m_customData[BrokenKey] = data;
					Log.LogWarning($"Outfit: {saved - inventory.NrOfItems()} Teil(e) unbekannt, der alte Stand liegt unter {BrokenKey}.");
				}
			}
			catch (Exception e)
			{
				player.m_customData[BrokenKey] = data;
				Log.LogWarning($"Outfit: gespeichertes Outfit nicht lesbar ({e.Message}), der alte Stand liegt unter {BrokenKey}.");
			}
		}
		inventory.m_onChanged += () =>
		{
			Save(player, inventory);
			Refresh(player);
		};
		return inventory;
	}

	/// <summary>Zahl der Items im gespeicherten Stand (Inventory.Save: Version, Anzahl, Items).</summary>
	private static int SavedCount(string data)
	{
		try
		{
			ZPackage pkg = new(Convert.FromBase64String(data));
			pkg.ReadInt();
			return pkg.ReadInt();
		}
		catch
		{
			return 0;
		}
	}

	/// <summary>In den Charakter schreiben (bei jeder Änderung und vor jedem Speichern).</summary>
	internal static void Save(Player player, Inventory inventory)
	{
		if (inventory.NrOfItems() == 0)
		{
			player.m_customData.Remove(DataKey);
		}
		else
		{
			ZPackage pkg = new();
			inventory.Save(pkg);
			player.m_customData[DataKey] = Convert.ToBase64String(pkg.GetArray());
		}
		if (hidden == 0)
		{
			player.m_customData.Remove(HideKey);
		}
		else
		{
			player.m_customData[HideKey] = hidden.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	/// <summary>Der Charakter wurde (neu) geladen: Outfit beim nächsten Zugriff aus seinen Daten lesen.</summary>
	internal static void Forget(Player player)
	{
		if (owner == player || player == Player.m_localPlayer)
		{
			owner = null;
			outfit = null;
			hidden = 0;
		}
	}

	/// <summary>Ausgeblendet: Rüstung zeigt dort nichts, eine Waffe dieser Art ist in der Hand und auf dem Rücken unsichtbar (Gorduan, 31.10.2026: Augen auch bei Waffen).</summary>
	internal static bool IsHidden(int slot) => slot >= 0 && slot < Slots && (hidden & (1 << slot)) != 0;

	internal static void ToggleHidden(Player player, int slot)
	{
		if (slot < 0 || slot >= Slots || Of(player) is not Inventory inventory)
		{
			return;
		}
		hidden ^= 1 << slot;
		Save(player, inventory);
		Refresh(player);
	}

	// ------------------------------------------------------------------ Welcher Platz?

	/// <summary>Platz eines Items: Rüstung nach Art, Waffe nach Waffenart; Werkzeuge, Fackel und alles andere -1.</summary>
	internal static int SlotOf(ItemDrop.ItemData? item)
	{
		if (item?.m_shared == null || item.m_dropPrefab == null)
		{
			return -1;
		}
		switch (item.m_shared.m_itemType)
		{
			case ItemDrop.ItemData.ItemType.Helmet: return Helmet;
			case ItemDrop.ItemData.ItemType.Chest: return Chest;
			case ItemDrop.ItemData.ItemType.Legs: return Legs;
			case ItemDrop.ItemData.ItemType.Shoulder: return Cape;
			case ItemDrop.ItemData.ItemType.Shield: return Shield;
			case ItemDrop.ItemData.ItemType.Utility: return Belt;
		}
		return WeaponSlot(item);
	}

	/// <summary>Waffenplatz nach Fertigkeit und Ein-/Zweihand; Spitzhacken, Angeln, Fäuste und Werkzeuge haben keinen.</summary>
	internal static int WeaponSlot(ItemDrop.ItemData item)
	{
		ItemDrop.ItemData.ItemType type = item.m_shared.m_itemType;
		if (type != ItemDrop.ItemData.ItemType.OneHandedWeapon && type != ItemDrop.ItemData.ItemType.TwoHandedWeapon
			&& type != ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft && type != ItemDrop.ItemData.ItemType.Bow)
		{
			return -1;
		}
		bool twoHanded = type == ItemDrop.ItemData.ItemType.TwoHandedWeapon || type == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;
		switch (item.m_shared.m_skillType)
		{
			case Skills.SkillType.Swords: return twoHanded ? Sword2H : Sword;
			case Skills.SkillType.Axes: return twoHanded ? Axe2H : Axe;
			case Skills.SkillType.Clubs: return twoHanded ? Club2H : Club;
			case Skills.SkillType.Knives: return Knife;
			case Skills.SkillType.Spears: return Spear;
			case Skills.SkillType.Polearms: return Polearm;
			case Skills.SkillType.Bows: return Bow;
			case Skills.SkillType.Crossbows: return Crossbow;
			case Skills.SkillType.ElementalMagic:
			case Skills.SkillType.BloodMagic: return Staff;
			default: return -1;
		}
	}

	/// <summary>Passt das Item in diesen Platz? Rüstung nur in ihren, jede Waffe in jeden Waffenplatz (Gorduan).</summary>
	internal static bool Fits(int slot, ItemDrop.ItemData? item)
	{
		if (item == null || slot < 0 || slot >= Slots || item.m_shared.m_questItem)
		{
			return false;
		}
		int own = SlotOf(item);
		return IsArmor(slot) ? own == slot : own >= ArmorSlots;
	}

	/// <summary>Aussehen für ein getragenes Item: null = echtes Teil, hide = nichts zeigen.</summary>
	internal static ItemDrop.ItemData? LookFor(Inventory inventory, ItemDrop.ItemData? real, out bool hide)
	{
		hide = false;
		int slot = SlotOf(real);
		if (slot < 0)
		{
			return null;
		}
		if (IsHidden(slot))
		{
			hide = true;
			return null;
		}
		Vector2i pos = Pos(slot);
		ItemDrop.ItemData? look = inventory.GetItemAt(pos.x, pos.y);
		return look?.m_dropPrefab != null ? look : null;
	}

	/// <summary>Aussehen neu an VisEquipment melden (nach jeder Änderung am Outfit oder an der Regel).</summary>
	internal static void Refresh(Player? player)
	{
		if (player != null && player == Player.m_localPlayer && player.m_visEquipment != null)
		{
			player.SetupVisEquipment(player.m_visEquipment, false);
		}
	}

	internal static int Hash(ItemDrop.ItemData item) => item.m_dropPrefab.name.GetStableHashCode();
}

// ---------------------------------------------------------------------- Valheim-Haken

/// <summary>
/// Nach Valheims SetupVisEquipment (eigener Spieler, auch für die Leiche): getragene Teile mit belegtem Outfit-Platz zeigen
/// das Outfit-Teil, ausgeblendete Rüstung nichts. Die Fackel bleibt immer echt (SlotOf kennt sie nicht).
/// </summary>
[HarmonyPatch(typeof(Player), nameof(Player.SetupVisEquipment))]
internal static class OutfitLookPatch
{
	private static void Postfix(Player __instance, VisEquipment visEq, bool isRagdoll)
	{
		if (visEq == null || __instance != Player.m_localPlayer || !Outfits.On || Outfits.Of(__instance) is not Inventory outfit)
		{
			return;
		}
		ItemDrop.ItemData? look;
		bool hide;
		if (!isRagdoll)
		{
			if (__instance.m_leftItem != null && ((look = Outfits.LookFor(outfit, __instance.m_leftItem, out hide)) != null || hide))
			{
				visEq.SetLeftItem(hide ? 0 : Outfits.Hash(look!), hide ? 0 : look!.m_variant, hide ? 0 : look!.m_quality);
			}
			if (__instance.m_rightItem != null && ((look = Outfits.LookFor(outfit, __instance.m_rightItem, out hide)) != null || hide))
			{
				visEq.SetRightItem(hide ? 0 : Outfits.Hash(look!), hide ? 0 : look!.m_quality);
			}
			if (__instance.m_hiddenLeftItem != null && ((look = Outfits.LookFor(outfit, __instance.m_hiddenLeftItem, out hide)) != null || hide))
			{
				visEq.SetLeftBackItem(hide ? 0 : Outfits.Hash(look!), hide ? 0 : look!.m_variant, hide ? 0 : look!.m_quality);
			}
			if (__instance.m_hiddenRightItem != null && ((look = Outfits.LookFor(outfit, __instance.m_hiddenRightItem, out hide)) != null || hide))
			{
				visEq.SetRightBackItem(hide ? 0 : Outfits.Hash(look!), hide ? 0 : look!.m_quality);
			}
		}
		if (__instance.m_chestItem != null && ((look = Outfits.LookFor(outfit, __instance.m_chestItem, out hide)) != null || hide))
		{
			visEq.SetChestItem(hide ? 0 : Outfits.Hash(look!));
		}
		if (__instance.m_legItem != null && ((look = Outfits.LookFor(outfit, __instance.m_legItem, out hide)) != null || hide))
		{
			visEq.SetLegItem(hide ? 0 : Outfits.Hash(look!));
		}
		if (__instance.m_helmetItem != null && ((look = Outfits.LookFor(outfit, __instance.m_helmetItem, out hide)) != null || hide))
		{
			visEq.SetHelmetItem(hide ? 0 : Outfits.Hash(look!));
		}
		if (__instance.m_shoulderItem != null && ((look = Outfits.LookFor(outfit, __instance.m_shoulderItem, out hide)) != null || hide))
		{
			visEq.SetShoulderItem(hide ? 0 : Outfits.Hash(look!), hide ? 0 : look!.m_variant, hide ? 0 : look!.m_quality);
		}
		if (__instance.m_utilityItem != null && ((look = Outfits.LookFor(outfit, __instance.m_utilityItem, out hide)) != null || hide))
		{
			visEq.SetUtilityItem(hide ? 0 : Outfits.Hash(look!));
		}
	}
}

/// <summary>Vor dem Speichern des Charakters: Outfit hineinschreiben.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.Save))]
internal static class OutfitSavePatch
{
	private static void Prefix(Player __instance)
	{
		if (__instance == Player.m_localPlayer && Outfits.Of(__instance) is Inventory inventory)
		{
			Outfits.Save(__instance, inventory);
		}
	}
}

/// <summary>Neuer Spieler (Laden des Charakters): Outfit aus dessen Daten neu lesen, danach Aussehen melden.</summary>
[HarmonyPatch(typeof(Player), nameof(Player.Load))]
internal static class OutfitLoadPatch
{
	private static void Postfix(Player __instance)
	{
		Outfits.Forget(__instance);
		if (__instance == Player.m_localPlayer)
		{
			Outfits.Refresh(__instance);
		}
	}
}
