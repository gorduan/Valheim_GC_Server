using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Server.ModLeiter;

/// <summary>
/// GC_Leiter (Gorduan), Stufe 4 „GC leicht“: Baubare Leiter, 1 und 2 m hoch, mit festem Vorne.
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
		Ladder.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		harmony.PatchAll(typeof(Plugin).Assembly);
		Module.MenuGroup(key => GcLoc.Get("gcsv_" + key))
			.Main("ladder", Ladder.Plugin.Enabled, () => Ladder.Plugin.Blocked, "GC_Ladder")
			.Sub("ladder_cost", Ladder.Plugin.WoodCost, 1)
			.Sub("ladder_exit", Ladder.Plugin.ExitDistance, 0.1, " m");
	}
}
