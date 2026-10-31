using System.Collections.Generic;
using System.Linq;
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
		Leash.YoungFighters.Init(Config); // vor GcMod.PatchAll: Die Patches der Jungtiere lesen seine Einstellungen (Review 27.10.2026)
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
		GcMod.PatchAll(harmony); // nur die Patches dieser Mod (alle Mods des Pakets in einer DLL)

		GcMenuGroup species = Module.MenuGroup(key => GcLoc.Get("gcsv_" + key))
			.Main("species_cap", Leash.Species.EffectCap, step: 10)
			.Main("species_star1", Leash.Species.StarChance1, step: 0.1, unit: " %")
			.Main("species_star2", Leash.Species.StarChance2, step: 0.1, unit: " %")
			.Main("species_young_fight", Leash.YoungFighters.Enabled) // Gorduan, 27.10.2026: Jungtiere kämpfen mit
			.Sub("species_young_damage", Leash.YoungFighters.DamagePercent, 5, " %");
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

	/// <summary>
	/// Valheim-Texte ($-Schlüssel) für Sättel, Reiten, Bärenjunges und die Jungen der zuschaltbaren Arten (fehlten bisher,
	/// zeigten rohe Schlüssel; Gorduan, 30.10.2026: „[gc_deer_fawn]“, „[gc_ride]“). Die Jungen kommen aus Species.All, damit ein
	/// neues Junges nicht wieder vergessen wird.
	/// </summary>
	private static void AddLocalization()
	{
		Jotunn.Entities.CustomLocalization localization = Jotunn.Managers.LocalizationManager.Instance.GetLocalization();
		IEnumerable<string> young = Leash.Species.All.Where(kind => kind.Child != null).Select(kind => kind.Child!.Name.TrimStart('$'));
		foreach (string key in new[] { "gc_saddle_bjorn", "gc_saddle_bjorn_desc", "gc_saddle_unbjorn", "gc_saddle_unbjorn_desc", "gc_bjorn_cub", "gc_ride" }.Concat(young))
		{
			foreach (KeyValuePair<string, string> pair in GcLoc.AllLanguages(key))
			{
				localization.AddTranslation(pair.Key, key, pair.Value);
			}
		}
	}

	/// <summary>
	/// Verwandlung Bär → Unbjørn und Aufräumen beim Besitzer, auch auf dem dedizierten Server (Review 27.10.2026): Er besitzt
	/// die Zonen um den Weltmittelpunkt dauerhaft; Bären dort verwandelten sich sonst nie.
	/// </summary>
	private void Update()
	{
		Module.Guard("Tierarten", Leash.Species.Tick);
	}
}
