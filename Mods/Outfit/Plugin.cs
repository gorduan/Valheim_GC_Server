using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Server.ModOutfit;

/// <summary>
/// GC_Outfit (Gorduan), Stufe 1 „Sehr Vanilla“: Kleiderschrank und Outfit (Gorduan, 30.10.2026). Daten und Aussehen in
/// <see cref="Outfit.Outfits"/>, Schrank in <see cref="Outfit.Wardrobes"/>, Fenster in <see cref="Outfit.WardrobeWindow"/>.
/// Stufe, Seite und Abhängigkeiten stehen in der .csproj.
/// </summary>
public partial class Plugin : BaseUnityPlugin
{
	internal static GcModule Module = null!;

	private void Awake()
	{
		Module = GcKern.Register(this, Logger, GcMod.Info);
		Outfit.Outfits.Init(Config, Logger);
		Outfit.Wardrobes.Init();
		Module.TakeOverOldSettings();
		Module.IsOn = () => Outfit.Outfits.Enabled.Value;
		GcMod.PatchAll(new Harmony(GcMod.Guid)); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		Module.MenuGroup(key => GcLoc.Get("gcof_" + key))
			.Main("enabled", Outfit.Outfits.Enabled);
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Kleiderschrank", Outfit.WardrobeWindow.Tick);
	}
}
