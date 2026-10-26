using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Server.ModWalkuerenTransport;

/// <summary>
/// GC_WalkuerenTransport (Gorduan), Stufe 5 „GC“: Mit der Walkürenpfeife neben einem Karren mit 20 Loxfleisch pfeifen,
/// eine Walküre holt ihn ab und bringt ihn später dorthin, wo du pfeifst. Danach ein Raid am Abholort, dazu ein Easteregg
/// (Server-Einstellung). Früher Teil von GC_Server. Braucht GC_Tiere (die Walkürenpfeife ist eine der Tierpfeifen).
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
		Courier.Transport.Init(Config);
		Module.TakeOverOldSettings();

		// Verbindungen zu GC_Tiere (Walkürenpfeife)
		GcHooks.CourierOn = () => Courier.Transport.On;
		GcHooks.CourierWhistle = Courier.Transport.OnWhistle;
		GcHooks.CourierTooltip = Courier.Transport.TooltipStatus;
		GcMod.PatchAll(harmony); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)

		Module.MenuGroup(key => GcLoc.Get("gcsv_" + key))
			.Main("courier", Courier.Transport.Enabled)
			.Sub("courier_meat", Courier.Transport.MeatAmount, 1)
			.Sub("courier_ymir", Courier.Transport.YmirAmount, 1)
			.Sub("courier_raid", Courier.Transport.RaidAfter)
			.Sub("courier_easter", Courier.Transport.EasterEgg);
	}

	private void Update()
	{
		// auch auf dem Server ohne Grafik: Reihe der Walküre, Raids danach
		Module.Guard("Walküren-Transport", Courier.Transport.Tick);
	}
}
