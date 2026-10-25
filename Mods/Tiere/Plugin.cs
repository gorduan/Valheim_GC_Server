using BepInEx;
using GC.Shared;
using HarmonyLib;

namespace GC.Server.ModTiere;

/// <summary>
/// GC_Tiere (Gorduan), Stufe 5 „GC“: Tierleinen (Leine in der Hand: E an und ab, Linksklick hin, Rechtsklick angreifen),
/// vier Tierpfeifen, der Skill Tierpflege, Tier-Erfahrung, Zucht-Stufen, Jungtiere an der Leine und mit EpicLoot
/// verzauberbare Leinen, Pfeifen und Sättel. Früher Teil von GC_Server.
/// Läuft allein. Mit GC_Tierarten kommen Höchststufen je Art, weitere zähmbare Tiere und Bärensättel dazu, mit
/// GC_WalkuerenTransport die Walkürenpfeife.
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
		Leash.Care.Init(Config); // vor den Leinen-Items: sie bekommen den Skill Tierpflege
		Leash.Saddles.Init(Config); // Sättel als EpicLoot-Items (Valheims und die aus GC_Tierarten)
		Leash.Plugin.Init(Config, harmony);
		Module.TakeOverOldSettings();
		Module.IsOn = () => Leash.Plugin.On || Leash.Care.Enabled.Value; // Boss-Fortschritt: Tiere 5 %
		harmony.PatchAll(typeof(Plugin).Assembly);
		Module.MenuGroup(key => GcLoc.Get("gcsv_" + key))
			.Main("leash", Leash.Plugin.Enabled)
			.Sub("leash_slip", Leash.Plugin.SlipDistance, 1, " m")
			.Sub("leash_interval", Leash.Plugin.CheckInterval, 0.25, " s")
			.Sub("leash_durability", Leash.Plugin.DurabilityStep, 5)
			.Sub("leash_portal", Leash.Plugin.PortalTravel)
			.Sub("leash_chase", Leash.Plugin.ChaseDistance, 1, " m")
			.Sub("leash_cmd_level", Leash.Plugin.CommandMinLevel, 1)
			.Sub("leash_cmd_cooldown", Leash.Plugin.CommandCooldown, 5, " s")
			.Sub("leash_loki", Leash.Plugin.IllusionCooldown, 10, " s")
			.Sub("leash_whistles", Leash.Plugin.WhistlesEnabled)
			.Main("care", Leash.Care.Enabled)
			.Sub("care_radius", Leash.Care.Radius, 5, " m")
			.Sub("care_feed", Leash.Care.FeedXp, 0.5f)
			.Sub("care_feed_cd", Leash.Care.FeedCooldown, 10, " s")
			.Sub("care_pet", Leash.Care.PetXp, 0.5f)
			.Sub("care_pet_cd", Leash.Care.PetCooldown, 60, " s")
			.Sub("care_tame", Leash.Care.TameXp, 2.5f)
			.Sub("care_breed", Leash.Care.BreedXp, 2.5f)
			.Sub("care_kill", Leash.Care.KillXp, 1)
			.Sub("care_delay", Leash.Care.FollowDelay, 0.5, " s")
			.Sub("care_delay_level", Leash.Care.FollowDelayLevel, 5)
			.Sub("care_xp", Leash.Care.AnimalXp)
			.Sub("care_xp_factor", Leash.Care.AnimalXpFactor, 0.1, " ×")
			.Sub("care_xp_kill", Leash.Care.AnimalKillXp, 1);
	}

	private void Update()
	{
		if (Gc.IsHeadless)
		{
			return;
		}
		Module.Guard("Tierleinen", Leash.Plugin.Update);
	}
}
