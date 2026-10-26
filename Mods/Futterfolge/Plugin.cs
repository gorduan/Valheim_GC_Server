using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Mods.Futterfolge;

/// <summary>
/// GC_Futterfolge (Gorduan), Stufe 4 „GC leicht“: Zahme Tiere sehen Futter doppelt so weit, das Tier mit der höchsten Stufe
/// frisst zuerst, und bei der Zucht zählt der beste satte Partner (mit GC_Tiere). Beschreibung in <see cref="FeedOrder"/>.
/// Stufe, Seite und Abhängigkeiten stehen in der .csproj.
/// </summary>
public partial class Plugin : BaseUnityPlugin
{
	internal static GcModule Module = null!;

	private void Awake()
	{
		Module = GcKern.Register(this, Logger, GcMod.Info);
		FeedOrder.Init(Config, Logger);
		Module.TakeOverOldSettings();
		Module.IsOn = () => FeedOrder.Enabled.Value; // Boss-Fortschritt: nur, wenn eingeschaltet
		GcMod.PatchAll(new Harmony(GcMod.Guid)); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)
		GcHooks.MateLevel = FeedOrder.MateLevelForBirth;
		Module.MenuGroup(key => GcLoc.Get("gcff_" + key))
			.Main("enabled", FeedOrder.Enabled)
			.Sub("range", FeedOrder.RangeFactor, 0.25, " ×")
			.Sub("wait", FeedOrder.WaitSeconds, 10, " s")
			.Sub("pregnant", FeedOrder.PregnantLast)
			.Sub("partner", FeedOrder.BestPartner, requires: () => GcKern.ModLoaded("tiere"))
			.Sub("wild", FeedOrder.Wild)
			.Sub("rank", FeedOrder.ShowRank);
	}
}
