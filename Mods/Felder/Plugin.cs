using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Mods.Felder;

/// <summary>
/// GC_Felder (Gorduan), Stufe 3 „Vanilla+“: Felder mit dem Kultivator zeichnen und automatisch bepflanzen. Der Server speichert die Felder und gibt die Regeln vor.
/// Früher Teil von GC_Client und GC_Server. Stufe, Seite und Abhängigkeiten stehen in der .csproj.
/// </summary>
public partial class Plugin : BaseUnityPlugin
{
	internal static GcModule Module = null!;

	private void Awake()
	{
		Module = GcKern.Register(this, Logger, GcMod.Info);
		GC.Client.ClientPlugin.Log = Logger;
		GC.Client.ClientPlugin.Module = Module;
		GC.Server.ServerPlugin.Log = Logger;
		GC.Server.ServerPlugin.Module = Module;
		Harmony harmony = new(GcMod.Guid);
		GC.Client.Fields.Plugin.Init(Config, harmony);
		GC.Server.Fields.Plugin.Init(Config);
		Module.TakeOverOldSettings();
		harmony.PatchAll(typeof(Plugin).Assembly);
		Module.MenuGroup(key => GcLoc.Get("gcsv_" + key))
			.Main("fields", GC.Server.Fields.Plugin.Enabled)
			.Sub("fields_remove", GC.Server.Fields.Plugin.RemoveBy)
			.Sub("fields_points", GC.Server.Fields.Plugin.MaxPoints, 1)
			.Sub("fields_edge", GC.Server.Fields.Plugin.MaxEdge, 5, " m")
			.Sub("fields_reach", GC.Server.Fields.Plugin.Reach, 1, " m")
			.Sub("fields_interval", GC.Server.Fields.Plugin.Interval, 0.1, " s")
			.Sub("fields_bush", GC.Server.Fields.Plugin.SpacingBush, 0.1, " m")
			.Sub("fields_mushroom", GC.Server.Fields.Plugin.SpacingMushroom, 0.1, " m")
			.Sub("fields_other", GC.Server.Fields.Plugin.SpacingOther, 0.1, " m");
		Module.MenuGroup(key => GcLoc.Get("gccm_" + key), "gck_mod_felder_anzeige")
			.Main("fields", GC.Client.Fields.Plugin.Enabled)
			.Sub("fields_color", GC.Client.Fields.Plugin.Tint)
			.Sub("fields_fill", GC.Client.Fields.Plugin.FillOpacity, 5, " %")
			.Sub("fields_line", GC.Client.Fields.Plugin.LineOpacity, 5, " %");
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Felder", GC.Client.Fields.Plugin.Update);
	}
}
