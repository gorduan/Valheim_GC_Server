using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Mods.Portalnamen;

/// <summary>
/// GC_Portalnamen (Gorduan), Stufe 1 „Sehr Vanilla“: Liste der Portalnamen beim Umbenennen: deine eigene und, wenn der Server die Mod hat, die Server-Liste.
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
		GC.Client.PortalNames.Plugin.Init(Config, harmony);
		GC.Server.PortalNames.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		GcMod.PatchAll(harmony); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("gccm_" + key))
			.Main("portalnames", GC.Client.PortalNames.Plugin.Enabled)
			.Sub("portalnames_click", GC.Client.PortalNames.Plugin.ClickRenames);
		Module.MenuGroup(key => GcLoc.Get("gcsv_" + key), "gck_mod_portalnamen_server")
			.Main("portalnames", GC.Server.PortalNames.Plugin.Enabled)
			.Sub("portalnames_editors", GC.Server.PortalNames.Plugin.EditBy);
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Portalnamen", GC.Client.PortalNames.Plugin.Update);
	}
}
