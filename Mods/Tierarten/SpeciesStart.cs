using HarmonyLib;

namespace GC.Server.Leash;

/// <summary>Bei jedem Spielstart die RPCs der Tierarten registrieren (Meldungen: Sterne, Unbjørn).</summary>
[HarmonyPatch(typeof(Game), nameof(Game.Start))]
internal static class SpeciesGameStartPatch
{
	private static void Postfix() => Species.RegisterRpcs();
}
