using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Server.ModSpitzhacke;

/// <summary>
/// GC_Spitzhacke (Gorduan), Stufe 4 „GC leicht“: Spitzhacken schlagen wie ein Streitkolben und taugen als Waffe.
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
		Pickaxe.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		Module.IsOn = () => Pickaxe.Plugin.On; // Boss-Fortschritt: nur, wenn eingeschaltet
		GcMod.PatchAll(harmony); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("gcsv_" + key))
			.Main("pickaxe", Pickaxe.Plugin.Enabled, () => Pickaxe.Plugin.Blocked, "GC_Pickaxe")
			.Sub("pickaxe_enemy", Pickaxe.Plugin.EnemyPercent, 5, " %")
			.Sub("pickaxe_ore", Pickaxe.Plugin.OrePercent, 5, " %");
	}
}
