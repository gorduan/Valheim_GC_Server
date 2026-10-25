using GC.Shared;

namespace GC.Server.Leash;

/// <summary>
/// Verbindung zu GC_Tierarten (wenn installiert, über GcHooks im Kern): Höchststufe je Art, Grenze der Boni, Feindschaft
/// beim Angriffsbefehl, Zähmen. Ohne GC_Tierarten gilt wie vor den Tierarten: Höchststufe 150 für alle, Grenze 150,
/// Valheims Feindschaft und Valheims Zähmen.
/// </summary>
internal static class SpeciesLink
{
	internal const float DefaultMax = 150f;

	internal static float MaxLevel(int prefabHash) => GcHooks.SpeciesMaxLevel != null ? GcHooks.SpeciesMaxLevel(prefabHash) : DefaultMax;

	internal static float MaxLevel(Character? character)
	{
		if (GcHooks.SpeciesMaxLevel == null || character == null)
		{
			return DefaultMax;
		}
		ZNetView? view = character.m_nview;
		ZDO? zdo = view != null && view.IsValid() ? view.GetZDO() : null;
		return zdo != null ? GcHooks.SpeciesMaxLevel(zdo.GetPrefab()) : DefaultMax;
	}

	internal static float EffectCap => GcHooks.SpeciesEffectCap != null ? GcHooks.SpeciesEffectCap() : DefaultMax;

	internal static bool EnemyForOrder(Character animal, Character target)
	{
		return GcHooks.SpeciesEnemyForOrder != null ? GcHooks.SpeciesEnemyForOrder(animal, target) : BaseAI.IsEnemy(animal, target);
	}

	internal static GcTaming Taming(Character animal) => GcHooks.SpeciesTaming != null ? GcHooks.SpeciesTaming(animal) : GcTaming.Normal;
}
