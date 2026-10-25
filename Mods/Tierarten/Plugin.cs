using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Server.ModTierarten;

/// <summary>
/// GC_Tierarten (Gorduan), Stufe 5 „GC“: Höchststufen je Tierart (Lox und Unbjørn 300, Wolf und Bär 200, sonst 150),
/// Sterne bei der Geburt, fließend mehr Beute nach der Stufe, weitere zähmbare Tiere (Nixe, Hirsch, Hase, Robbe, Bär;
/// Standard aus), Bärenjunge, Verwandlung in einen Unbjørn, Bären- und Unbjørn-Sattel. Früher Teil von GC_Server.
/// Braucht GC_Tiere. Ohne GC_Tierarten läuft GC_Tiere mit Höchststufe 150 für alle.
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
		Leash.Species.Init(Config);
		Leash.BearSaddles.Init(Config); // nach Species: Bär und Unbjørn haben dann ihr Tameable, das Junge ist schon geklont
		Module.TakeOverOldSettings();
		Module.IsOn = () => Leash.Plugin.On || Leash.Care.Enabled.Value; // wie GC_Tiere (Boss-Fortschritt)
		AddLocalization();

		// Verbindungen zu GC_Tiere (über den Kern)
		GcHooks.SpeciesMaxLevel = prefab => Leash.Species.MaxLevel(prefab);
		GcHooks.SpeciesEffectCap = () => Leash.Species.EffectCap.Value;
		GcHooks.SpeciesEnemyForOrder = Leash.Species.EnemyForOrder;
		GcHooks.SpeciesTaming = animal =>
		{
			Leash.Species.Kind kind = Leash.Species.Of(animal);
			if (kind == Leash.Species.Unbjorn || (kind.Optional && !Leash.Species.Enabled(kind)))
			{
				return GcTaming.Blocked;
			}
			return kind.Optional ? GcTaming.SimpleAI : GcTaming.Normal;
		};
		harmony.PatchAll(typeof(Plugin).Assembly);

		GcMenuGroup species = Module.MenuGroup(key => GcLoc.Get("gcsv_" + key))
			.Main("species_cap", Leash.Species.EffectCap, step: 10)
			.Main("species_star1", Leash.Species.StarChance1, step: 0.1, unit: " %")
			.Main("species_star2", Leash.Species.StarChance2, step: 0.1, unit: " %");
		foreach (Leash.Species.Kind kind in Leash.Species.All)
		{
			string key = kind.Key.ToLowerInvariant();
			if (kind.Tameable != null)
			{
				species.Main("species_tame_" + key, kind.Tameable).Sub("species_max_" + key, kind.MaxLevel, 10);
			}
			else
			{
				species.Main("species_max_" + key, kind.MaxLevel, step: 10);
			}
		}
	}

	/// <summary>Valheim-Texte ($-Schlüssel) für Sättel und Bärenjunges (fehlten bisher, zeigten rohe Schlüssel).</summary>
	private static void AddLocalization()
	{
		Jotunn.Entities.CustomLocalization localization = Jotunn.Managers.LocalizationManager.Instance.GetLocalization();
		foreach (string key in new[] { "gc_saddle_bjorn", "gc_saddle_bjorn_desc", "gc_saddle_unbjorn", "gc_saddle_unbjorn_desc", "gc_bjorn_cub" })
		{
			foreach (System.Collections.Generic.KeyValuePair<string, string> pair in GcLoc.AllLanguages(key))
			{
				localization.AddTranslation(pair.Key, key, pair.Value);
			}
		}
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Tierarten", Leash.Species.Tick); // Verwandlung Bär → Unbjørn
	}
}
