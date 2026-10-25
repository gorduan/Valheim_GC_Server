using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Server.ModParade;

/// <summary>
/// GC_Parade (Gorduan), Stufe 4 „GC leicht“: Eine Parade wirft Pfeile und Geschosse zurück.
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
		Deflect.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		Module.IsOn = () => Deflect.Plugin.On; // Boss-Fortschritt: nur, wenn eingeschaltet
		harmony.PatchAll(typeof(Plugin).Assembly);
		Module.MenuGroup(key => GcLoc.Get("gcsv_" + key))
			.Main("deflect", Deflect.Plugin.Enabled, () => Deflect.Plugin.Blocked, "GC_Deflect")
			.Sub("deflect_arrows", Deflect.Plugin.DeflectArrows)
			.Sub("deflect_damage", Deflect.Plugin.DamageFactor, 0.1, " ×");
	}
}
