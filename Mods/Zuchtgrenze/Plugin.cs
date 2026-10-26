using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Server.ModZuchtgrenze;

/// <summary>
/// GC_Zuchtgrenze (Gorduan), Stufe 4 „GC leicht“: Mehr Tiere je Umkreis, bevor die Zucht stoppt. Mit GC_Tiere zählen angeleinte Tiere nur halb.
/// Früher Teil von GC_Server. Stufe, Seite und Abhängigkeiten stehen in der .csproj.
/// </summary>
public partial class Plugin : BaseUnityPlugin
{
	internal static GcModule Module = null!;

	private void Awake()
	{
		Module = GcKern.Register(this, Logger, GcMod.Info);
		GC.Server.ServerPlugin.Log = Logger;
		GC.Server.ServerPlugin.Module = Module;
		Harmony harmony = new(GcMod.Guid);
		Leash.BreedLimit.Init(Config);
		Module.TakeOverOldSettings();
		Module.IsOn = () => Leash.BreedLimit.Enabled.Value; // Boss-Fortschritt: nur, wenn eingeschaltet
		GcMod.PatchAll(harmony); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("gcsv_" + key))
			.Main("breed", Leash.BreedLimit.Enabled)
			.Sub("breed_factor", Leash.BreedLimit.LimitFactor, 0.1, " ×")
			.Sub("breed_radius", Leash.BreedLimit.RadiusFactor, 0.1, " ×")
			.Sub("breed_leashed", Leash.BreedLimit.LeashedCount, 0.1, requires: () => GcKern.ModLoaded("tiere"))
			.Sub("breed_chicken_factor", Leash.BreedLimit.ChickenLimitFactor, 0.1, " ×")
			.Sub("breed_chicken_radius", Leash.BreedLimit.ChickenRadiusFactor, 0.1, " ×")
			.Sub("breed_chicken_leashed", Leash.BreedLimit.ChickenLeashedCount, 0.1, requires: () => GcKern.ModLoaded("tiere"));
	}
}
