using System.Collections.Generic;
using GC.Shared;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace GC.Server.Leash;

/// <summary>
/// Sterne über dem Lebensbalken bis fünf (Gorduan, 03.11.2026): Valheims EnemyHud kennt nur „level_2“ (ein Stern) und „level_3“
/// (zwei) und zeigt bei mehr Sternen gar keinen. Ab drei Sternen erscheint eine Kopie von „level_3“ mit so vielen Sternen wie
/// nötig, im Abstand von Valheims zwei Sternen und um dieselbe Mitte.
/// </summary>
internal static class StarHud
{
	private const string Prefix = "GC_Stars";

	private static bool reported;

	internal static void Update(EnemyHud hud)
	{
		foreach (KeyValuePair<Character, EnemyHud.HudData> pair in hud.m_huds)
		{
			EnemyHud.HudData data = pair.Value;
			Character character = pair.Key;
			if (data == null || data.m_gui == null || !data.m_gui.activeSelf || character == null)
			{
				continue;
			}
			int stars = character.GetLevel() - 1;
			if (stars > 5 && data.m_name != null && !character.IsPlayer())
			{
				// Boss-Leiste ab sechs Sternen (StarBossBar): dort gibt es keine Sternbilder, die Zahl steht beim Namen
				data.m_name.text = GcLoc.Get("gcs_boss_stars", Localization.instance.Localize(character.GetHoverName()), stars.ToString());
			}
			if (data.m_level3 == null)
			{
				continue;
			}
			Transform? shown = null;
			foreach (Transform child in data.m_gui.transform)
			{
				if (!child.name.StartsWith(Prefix, System.StringComparison.Ordinal))
				{
					continue;
				}
				bool wanted = stars >= 3 && child.name == Prefix + stars;
				if (child.gameObject.activeSelf != wanted)
				{
					child.gameObject.SetActive(wanted);
				}
				if (wanted)
				{
					shown = child;
				}
			}
			if (stars >= 3 && shown == null)
			{
				Build(data, stars);
			}
		}
	}

	/// <summary>Kopie von „level_3“ mit stars Sternen (die Sternbilder sind ihre Kinder; Abstand wie zwischen Valheims zwei).</summary>
	private static void Build(EnemyHud.HudData data, int stars)
	{
		RectTransform source = data.m_level3;
		GameObject copy = Object.Instantiate(source.gameObject, source.parent, false);
		copy.name = Prefix + stars;
		List<RectTransform> images = new();
		foreach (Transform child in copy.transform)
		{
			if (child.GetComponent<Image>() != null)
			{
				images.Add((RectTransform)child);
			}
		}
		if (images.Count < 2)
		{
			if (!reported)
			{
				reported = true;
				ServerPlugin.Log.LogWarning($"Tierarten: Valheims „level_3“ hat {images.Count} Sternbilder, mehr als zwei Sterne zeigt der Lebensbalken dann wie zwei.");
			}
			copy.SetActive(true);
			return;
		}
		Vector2 first = images[0].anchoredPosition;
		Vector2 step = images[1].anchoredPosition - first;
		RectTransform template = images[images.Count - 1];
		for (int i = images.Count; i < stars; i++)
		{
			GameObject star = Object.Instantiate(template.gameObject, template.parent, false);
			((RectTransform)star.transform).anchoredPosition = first + step * i;
		}
		// um dieselbe Mitte wie Valheims zwei Sterne
		((RectTransform)copy.transform).anchoredPosition = source.anchoredPosition - step * ((stars - images.Count) / 2f);
		copy.SetActive(true);
	}
}

/// <summary>Nach Valheims Lebensbalken: drei bis fünf Sterne zeigen.</summary>
[HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
internal static class StarHudPatch
{
	private static void Postfix(EnemyHud __instance) => StarHud.Update(__instance);
}
