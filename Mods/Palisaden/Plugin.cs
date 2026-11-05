using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Server.ModPalisaden;

/// <summary>
/// GC_Palisaden (Gorduan), Stufe 3 „Vanilla+“: große Palisade und großes Tor aus Kernholz (Gorduan, 02.11.2026), siehe
/// <see cref="Palisaden.BigPalisades"/>. Stufe, Seite und Abhängigkeiten stehen in der .csproj.
/// </summary>
public partial class Plugin : BaseUnityPlugin
{
	internal static GcModule Module = null!;

	private void Awake()
	{
		Module = GcKern.Register(this, Logger, GcMod.Info);
		Palisaden.BigPalisades.Init(Config, Logger);
		Module.TakeOverOldSettings();
		Module.IsOn = () => Palisaden.BigPalisades.Enabled.Value;
		GcMod.PatchAll(new Harmony(GcMod.Guid)); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("gcpa_" + key))
			.Main("enabled", Palisaden.BigPalisades.Enabled);
	}
}
