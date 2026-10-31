using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Mods.Blitzspule;

/// <summary>
/// GC_Blitzspule (Gorduan), Stufe 4 „GC leicht“: Blitzspulen am Vernichter (Gorduan, 27.10.2026). Bauteile und Bauregeln in
/// <see cref="Coils"/>, Blitz und Wiederverwertung in <see cref="Recycling"/>. Stufe, Seite und Abhängigkeiten stehen in der .csproj.
/// </summary>
public partial class Plugin : BaseUnityPlugin
{
	internal static GcModule Module = null!;

	private void Awake()
	{
		Module = GcKern.Register(this, Logger, GcMod.Info);
		Coils.Init(Config, Logger);
		Module.TakeOverOldSettings();
		Module.IsOn = () => Coils.Enabled.Value;
		GcMod.PatchAll(new Harmony(GcMod.Guid)); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("gcbs_" + key))
			.Main("enabled", Coils.Enabled)
			.Sub("range", Coils.Range, 0.5, " m")
			.Sub("rate1", Coils.Rates[0], 5, " %")
			.Sub("rate2", Coils.Rates[1], 5, " %")
			.Sub("rate3", Coils.Rates[2], 5, " %")
			.Sub("epicmin", Coils.EpicMin, 5, " %", requires: () => Recycling.EpicLootLoaded)
			.Sub("epicmax", Coils.EpicMax, 5, " %", requires: () => Recycling.EpicLootLoaded);
	}
}
