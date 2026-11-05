using BepInEx;
using GC.Shared;
using HarmonyLib;

// GC_Outfit nutzt die Vorratsleiste als Partner (Vorräte der Sätze am Kleiderschrank), ohne diese Mod läuft es weiter.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("GC_Outfit")]

namespace GC.Server.ModVorratsleiste;

/// <summary>
/// GC_Vorratsleiste (Gorduan), Stufe 1: Plätze nur für Vorräte unter dem Inventar (Gorduan, 03.11.2026; Standard acht, bis 64),
/// siehe <see cref="Supplies.SupplyBar"/> und <see cref="Supplies.SupplyChests"/>. Stufe, Seite und Abhängigkeiten stehen in der .csproj.
/// </summary>
public partial class Plugin : BaseUnityPlugin
{
	internal static GcModule Module = null!;

	private void Awake()
	{
		Module = GcKern.Register(this, Logger, GcMod.Info);
		Supplies.SupplyBar.Init(Config, Logger);
		Module.TakeOverOldSettings();
		Module.IsOn = () => Supplies.SupplyBar.Enabled.Value;
		GcMod.PatchAll(new Harmony(GcMod.Guid)); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("gcvl_" + key))
			.Main("enabled", Supplies.SupplyBar.Enabled)
			.Sub("slots", Supplies.SupplyBar.SlotCount, 1) // Gorduan, 03.11.2026: Anzahl für Rucksack und Schrank gleichermaßen
			.Sub("keepone", Supplies.SupplyBar.KeepOne); // Gorduan, 05.11.2026: ein Stück bleibt in der Kiste
	}
}
