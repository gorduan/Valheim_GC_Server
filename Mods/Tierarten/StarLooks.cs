using System.Collections.Generic;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.Rendering;

namespace GC.Server.Leash;

/// <summary>
/// Sterne an Größe und Farbe (Gorduan, 02.11.2026): Jedes Tier der Tierarten ist mit einem Stern 10 % größer, mit zwei 20 %,
/// gemessen an seiner eigenen Größe, wild und zahm, erwachsen und jung. Die Farben wechseln mit den Sternen wie bei Valheim.
///
/// Valheims LevelEffects setzt die Größe als festen Wert auf sein Objekt (transform.localScale = m_scale). Beim Bären sitzt es
/// an der Wurzel (1,3 und 1,5): Das Bärenjunge (ein Drittel groß) wurde mit Stern größer als ein Bär ohne Stern und verlor dabei
/// seine blassere Farbe. Hier wird jede Größe aus der eigenen Größe des Objekts gerechnet, beim Jungen also aus dem Drittel.
///
/// Vorher in den Spieldaten (Valheim 1.0.20): Wolf, Wildschwein, Hirsch, Asksvin, Robbe 1,1 und 1,2; Bär und Aasbär 1,3 und
/// 1,5; Hase 1,1 und 1,4; Nixe 1,2 und 1,3; Elch nur Farbe; die Henne wurde mit Stern kleiner (ihr Modell steht auf 2,0, der
/// Stern setzte 1,1). Ohne jede Sternwirkung waren Lox, Lox-Kalb, Wolfswelpe, Ferkel und Küken: Sie bekommen die Farben ihrer
/// Eltern, Lox und Kalb eine eigene Tönung (Gorduan: ein Stern dunkler und rötlicher, zwei heller und grauer).
///
/// Bis fünf Sterne (Gorduan, 03.11.2026): größer nach Growth (seit 04.11. fünf Sterne +75 %); drei bis fünf eigene,
/// seltene Farben. Seit 04.11.2026 färbt StarColors beim Spieler: vom eigenen Ton mit jedem Stern kräftiger, ab zwei Sternen
/// immer mehr zu Rot, fünf ganz rot (die Werte unten gelten nur ohne Grafik). Valheim hat nur zwei Stufen der Sternwirkung
/// und zeigt bei mehr Sternen gar keine (LevelEffects.SetupLevelVisualization bricht ab): Alle anderen Kreaturen mit
/// Sternwirkung bekommen deshalb die Stufen drei bis fünf dazu (ExtendAll), mit derselben Kurve; ein und zwei Sterne bleiben dort
/// wie bei Valheim.
/// </summary>
internal static class StarLooks
{
	/// <summary>
	/// Größe mit einem bis fünf Sternen, gemessen an der eigenen Größe (Gorduan, 03.11.2026: ein Stern 7 %, zwei 20 %, fünf 100 %
	/// größer, „entsprechend soll die Kurve aussehen“; 04.11.2026 nach dem Test: „0,75 bei Stufe 5 zur ursprünglichen 100 %“):
	/// dieselbe Kurve mal 0,75, ein Stern +5,25 %, zwei +15 %, drei +30 %, vier +49,5 %, fünf +75 %.
	/// </summary>
	private static readonly float[] Growth = { 1.0525f, 1.15f, 1.3f, 1.495f, 1.75f };

	/// <summary>Lox und Kalb (Gorduan, 02.11.2026): ein Stern dunkler und rötlicher, zwei heller und grauer; drei bis fünf wie RareShift.</summary>
	private static readonly (float Hue, float Saturation, float Value)[] LoxTint =
	{
		(-0.03f, 0.15f, -0.12f), (0f, -0.45f, 0.1f), (0.04f, -0.05f, 0.15f), (0.08f, 0.35f, 0.2f), (0f, 0.05f, 0.04f),
	};

	/// <summary>
	/// Seltene Farben ab drei Sternen, auf die Farbe mit zwei Sternen (Gorduan, 04.11.2026: „die Farbabstufungen und Wechsel sollen
	/// einem logisch erkennbaren Schema entsprechen“, gewählt Schema A): jeder Stern ein gleicher Schritt kräftiger, etwas wärmer und
	/// kaum heller. Beim Spieler färbt StarColors (Weg zu Rot); die Werte hier gelten nur ohne Grafik. Vorher waren drei Sterne viel zu dunkel
	/// (Helligkeit −0,2; Valheim selbst geht höchstens −0,1) und vier „fahl wie Silber“, das wie ein Geist aussah. Im Test die
	/// Schritte verstärkt (Gorduan: „Stufe 2 und 3 sind farblich kaum auseinanderzuhalten, etwas intensiver darf der Farbwechsel sein“).
	/// </summary>
	private static readonly (float Hue, float Saturation, float Value)[] RareShift = { (0.04f, 0.4f, 0.05f), (0.08f, 0.8f, 0.1f), (0f, 0.5f, -0.06f) };

	/// <summary>Einmal nach dem Anlegen der Jungen: alle Tiere der Tierarten angleichen. Ergebnis: Zahl der angeglichenen Tiere.</summary>
	internal static int Apply()
	{
		int count = 0;
		foreach (Species.Kind kind in Species.All)
		{
			if (kind == Species.Other || kind.Prefabs.Length == 0)
			{
				continue;
			}
			GameObject? adult = PrefabManager.Instance.GetPrefab(kind.Prefabs[0]);
			LevelEffects? adultEffects = adult != null ? adult.GetComponentInChildren<LevelEffects>(true) : null;
			List<LevelEffects.LevelSetup>? colors = kind == Species.Lox ? null : adultEffects?.m_levelSetups;
			foreach (string name in kind.Prefabs)
			{
				GameObject? prefab = PrefabManager.Instance.GetPrefab(name);
				bool ownYoung = name == Species.CubPrefab || name == kind.Child?.Prefab;
				if (prefab != null && Normalize(prefab, colors, ownYoung))
				{
					count++;
				}
			}
		}
		return count;
	}

	/// <summary>
	/// Sterne eines Tiers: fehlt die Sternwirkung, kommt sie dazu (Farben der Eltern bzw. die Lox-Tönung); die Größe immer aus
	/// der eigenen Größe des Objekts. Eigene Junge behalten ihre blassere Farbe (Species.CubSaturation) auch mit Stern.
	/// </summary>
	private static bool Normalize(GameObject prefab, List<LevelEffects.LevelSetup>? colors, bool ownYoung)
	{
		LevelEffects? effects = prefab.GetComponentInChildren<LevelEffects>(true);
		if (effects == null)
		{
			effects = Add(prefab);
			if (effects == null)
			{
				return false;
			}
			for (int i = 0; i < Growth.Length; i++)
			{
				LevelEffects.LevelSetup setup = new();
				if (colors != null && colors.Count > 0)
				{
					LevelEffects.LevelSetup from = colors[Mathf.Min(i, colors.Count - 1)];
					setup.m_hue = from.m_hue;
					setup.m_saturation = from.m_saturation;
					setup.m_value = from.m_value;
				}
				else if (colors == null)
				{
					(setup.m_hue, setup.m_saturation, setup.m_value) = LoxTint[i];
				}
				effects.m_levelSetups.Add(setup);
			}
		}
		List<LevelEffects.LevelSetup> setups = effects.m_levelSetups;
		AddRare(setups, Growth.Length);
		float own = effects.transform.localScale.x;
		for (int i = 0; i < Growth.Length; i++)
		{
			setups[i].m_scale = own * Growth[i];
			if (ownYoung)
			{
				setups[i].m_saturation = Mathf.Clamp(setups[i].m_saturation + Species.CubSaturation, -1f, 1f);
			}
		}
		return true;
	}

	/// <summary>
	/// Neue Sternwirkung am Modell („Visual“, sonst an der Wurzel) mit dem größten Renderer, dessen Material Farbe kennt
	/// (Valheims Kreaturen-Shader hat _Hue, _Saturation, _Value). Ohne so einen Renderer wächst das Tier nur.
	/// </summary>
	private static LevelEffects? Add(GameObject prefab)
	{
		Transform target = prefab.transform.Find("Visual") ?? prefab.transform;
		LevelEffects effects = target.gameObject.AddComponent<LevelEffects>();
		effects.m_levelSetups = new List<LevelEffects.LevelSetup>();
		float biggest = -1f;
		foreach (SkinnedMeshRenderer renderer in target.GetComponentsInChildren<SkinnedMeshRenderer>(true))
		{
			Material? material = renderer.sharedMaterials.Length > 0 ? renderer.sharedMaterials[0] : null;
			if (material == null || !material.HasProperty("_Hue") || renderer.sharedMesh == null)
			{
				continue;
			}
			Vector3 size = renderer.sharedMesh.bounds.size;
			float volume = size.x * size.y * size.z;
			if (volume > biggest)
			{
				biggest = volume;
				effects.m_mainRender = renderer;
			}
		}
		if (effects.m_mainRender == null)
		{
			ServerPlugin.Log.LogInfo($"Tierarten: {prefab.name} hat keinen Renderer mit Sternfarben, mit Stern wird es nur größer.");
		}
		return effects;
	}

	/// <summary>
	/// Fehlende Stufen bis count anhängen: die zweite wie die erste, ab der dritten die Farbe mit zwei Sternen plus RareShift.
	/// Größe setzt der Aufrufer.
	/// </summary>
	private static void AddRare(List<LevelEffects.LevelSetup> setups, int count)
	{
		while (setups.Count < count)
		{
			int index = setups.Count;
			LevelEffects.LevelSetup baseSetup = setups.Count >= 2 ? setups[1] : setups.Count > 0 ? setups[0] : new LevelEffects.LevelSetup();
			LevelEffects.LevelSetup setup = new()
			{
				m_scale = setups.Count > 0 ? setups[setups.Count - 1].m_scale : 1f,
				m_hue = baseSetup.m_hue,
				m_saturation = baseSetup.m_saturation,
				m_value = baseSetup.m_value,
			};
			if (index >= 2)
			{
				(float hue, float saturation, float value) = RareShift[Mathf.Min(index - 2, RareShift.Length - 1)];
				setup.m_hue = Wrap(setup.m_hue + hue);
				setup.m_saturation = Mathf.Clamp(setup.m_saturation + saturation, -1f, 1f);
				setup.m_value = Mathf.Clamp(setup.m_value + value, -1f, 1f);
			}
			setups.Add(setup);
		}
	}

	private static float Wrap(float hue) => hue > 0.5f ? hue - 1f : hue < -0.5f ? hue + 1f : hue;

	/// <summary>
	/// Alle übrigen Kreaturen mit Sternwirkung (Gegner, andere Tiere): Stufen drei bis fünf dazu, Größe nach Growth aus der
	/// eigenen Größe (mindestens so groß wie die Stufe davor), Farben wie AddRare. Ergebnis: Zahl der erweiterten Kreaturen.
	/// </summary>
	internal static int ExtendAll(ZNetScene scene)
	{
		int count = 0;
		foreach (GameObject prefab in scene.m_prefabs)
		{
			if (prefab == null || prefab.GetComponent<Character>() == null)
			{
				continue;
			}
			LevelEffects? effects = prefab.GetComponentInChildren<LevelEffects>(true);
			if (effects == null || effects.m_levelSetups == null || effects.m_levelSetups.Count == 0 || effects.m_levelSetups.Count >= Growth.Length)
			{
				continue;
			}
			List<LevelEffects.LevelSetup> setups = effects.m_levelSetups;
			float own = effects.transform.localScale.x;
			int had = setups.Count;
			AddRare(setups, Growth.Length);
			for (int i = had; i < setups.Count; i++)
			{
				setups[i].m_scale = Mathf.Max(own * Growth[i], setups[i - 1].m_scale + 0.05f * own);
			}
			count++;
		}
		return count;
	}
}

/// <summary>
/// Nach ZNetScene.Awake (jede Welt, die Prefabs sind dann vollständig; beim Anlegen der Tierarten war die Liste noch leer):
/// übrige Kreaturen auf fünf Sterne erweitern. Tierarten haben dann schon fünf Stufen und werden übersprungen.
/// </summary>
[HarmonyPatch(typeof(ZNetScene), "Awake")]
internal static class StarLooksScenePatch
{
	private static bool logged;

	private static void Postfix(ZNetScene __instance)
	{
		int count = StarLooks.ExtendAll(__instance);
		if (!logged || count > 0)
		{
			logged = true;
			ServerPlugin.Log.LogInfo($"Tierarten: {count} weitere Kreaturen zeigen jetzt auch drei bis fünf Sterne.");
		}
	}
}

/// <summary>
/// Sternfarben als Weg vom eigenen Ton zu Rot (Gorduan, 04.11.2026: „die Tiere sollten immer von ihrem Originalton Stufe 0 erkennbar
/// zu einem anderen Farbton bis Stufe 5 gehen, ab Stufe 2 sollten sie zusätzlich immer mehr zur Farbe Rot gehen, wo sie am Ende
/// wie bei den Loxen enden“; zuvor „Stufe 5 sollte immer einen roten, bedrohlichen Farbton haben“):
///  * Kräftiger mit jedem Stern: Sättigung bis +0,45 und Helligkeit bis −0,06 bei fünf Sternen (Strength).
///  * Ab zwei Sternen dreht der Farbton zu Rot, bei fünf ganz (zwei ein Viertel, drei die Hälfte, vier drei Viertel).
///  * Ab sechs Sternen (nur per Befehl, bis 15; Gorduan, 05.11.2026: „von 5 auf 6 langsam von Rot auf Gelb, bis es bei 15 alle anderen
///    Farben schleichend durchgelaufen hat“): sechs gelb, dann je Stern 30° weiter im Farbkreis (acht grün, zehn türkis, zwölf
///    blau, 14 magenta, 15 pink), so kräftig wie fünf.
/// Valheims Sternfarben drehen nur den Farbton (Shader _Hue, 1 = ganzer Kreis), ein festes Rot gibt es nicht: Einmal je Textur
/// liest der Spieler sie verkleinert (32 × 32) und bestimmt die Drehung, die ihren vorherrschenden Farbton auf Rot bringt. Graue
/// Tiere (kaum Farbe) werden über die Sättigung rot. Gilt für die Tierarten mit ihren Jungen (eigene Junge bleiben blasser); bei
/// allen anderen Kreaturen bleiben ein und zwei Sterne wie bei Valheim, ab drei Sternen derselbe Weg. Die Leiche bekommt dieselben
/// Werte (GetColorChanges). Ohne Grafik (dedizierter Server) gelten die Werte der LevelSetups.
/// </summary>
internal static class StarColors
{
	private const float RedSaturation = 0.45f;
	private const float RedValue = -0.06f;
	private static readonly float[] Strength = { 0.3f, 0.45f, 0.6f, 0.8f, 1f };
	private static readonly Dictionary<Texture, float> Shifts = new();
	private static readonly HashSet<Material> Done = new();

	/// <summary>Nach Valheims SetupLevelVisualization: das Material, das Valheim für Art und Stufe angelegt hat, umfärben.</summary>
	internal static void Apply(LevelEffects effects, int level)
	{
		if (effects.m_character == null || effects.m_levelSetups.Count < level - 1 || !Values(effects, level, out float hue, out float saturation, out float value))
		{
			return;
		}
		Material material = effects.m_mainRender.sharedMaterials[0];
		if (Done.Contains(material) || !LevelEffects.m_materials.ContainsValue(material))
		{
			return; // schon gefärbt, oder nicht Valheims eigene Kopie (dann wäre es das Material aller Tiere dieser Art)
		}
		material.SetFloat("_Hue", hue);
		material.SetFloat("_Saturation", saturation);
		material.SetFloat("_Value", value);
		Done.Add(material);
	}

	/// <summary>Für die Leiche (Character.OnDeath → Ragdoll.Setup): dieselben Werte wie das lebende Tier.</summary>
	internal static void Colors(LevelEffects effects, ref float hue, ref float saturation, ref float value)
	{
		if (effects.m_character != null && Values(effects, effects.m_character.GetLevel(), out float h, out float s, out float v))
		{
			hue = h;
			saturation = s;
			value = v;
		}
	}

	private static bool Values(LevelEffects effects, int level, out float hue, out float saturation, out float value)
	{
		hue = 0f;
		saturation = 0f;
		value = 0f;
		int stars = level - 1;
		Character? character = effects.m_character;
		if (character == null || stars < 1)
		{
			return false;
		}
		if (stars < 3 && Species.Of(character) == Species.Other)
		{
			return false; // Gegner und fremde Tiere: ein und zwei Sterne wie Valheim
		}
		if (!TryShift(effects, out float red))
		{
			return false;
		}
		float strength = Strength[Mathf.Min(stars, Strength.Length) - 1]; // über fünf Sterne (per Befehl): so kräftig wie fünf
		if (stars > Strength.Length)
		{
			// sechs Sterne gelb, dann je Stern 30° weiter im Farbkreis bis pink bei 15 (red = −vorherrschender Farbton)
			float target = (60f + 30f * (stars - 6)) / 360f;
			hue = Wrap(target + red);
		}
		else
		{
			hue = red * Mathf.Clamp01((stars - 1) / 4f);
		}
		saturation = Mathf.Clamp(RedSaturation * strength + (Species.YoungScale.ContainsKey(character.m_name) ? Species.CubSaturation : 0f), -1f, 1f);
		value = RedValue * strength;
		return true;
	}

	private static float Wrap(float hue)
	{
		hue %= 1f;
		return hue > 0.5f ? hue - 1f : hue < -0.5f ? hue + 1f : hue;
	}

	private static bool TryShift(LevelEffects effects, out float hue)
	{
		hue = 0f;
		Renderer? renderer = effects.m_mainRender;
		Material? material = renderer != null && renderer.sharedMaterials.Length > 0 ? renderer.sharedMaterials[0] : null;
		Texture? texture = material != null && material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
		if (texture == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
		{
			return false;
		}
		if (!Shifts.TryGetValue(texture, out hue))
		{
			hue = RedShift(texture);
			Shifts[texture] = hue;
		}
		return true;
	}

	/// <summary>Drehung des Farbtons, die den vorherrschenden Farbton der Textur (nach Farbigkeit gewichtet) auf Rot bringt.</summary>
	private static float RedShift(Texture texture)
	{
		const int size = 32;
		RenderTexture target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
		RenderTexture previous = RenderTexture.active;
		Texture2D read = new(size, size, TextureFormat.RGBA32, false);
		try
		{
			Graphics.Blit(texture, target);
			RenderTexture.active = target;
			read.ReadPixels(new Rect(0, 0, size, size), 0, 0);
			read.Apply();
			float x = 0f;
			float y = 0f;
			foreach (Color pixel in read.GetPixels())
			{
				Color.RGBToHSV(pixel, out float h, out float s, out float v);
				float weight = s * v * pixel.a;
				x += Mathf.Cos(h * 2f * Mathf.PI) * weight;
				y += Mathf.Sin(h * 2f * Mathf.PI) * weight;
			}
			if (x * x + y * y < 0.01f)
			{
				return 0f; // grau: kein Farbton, das Rot kommt über die Sättigung
			}
			float dominant = Mathf.Atan2(y, x) / (2f * Mathf.PI);
			float shift = -dominant;
			return shift > 0.5f ? shift - 1f : shift < -0.5f ? shift + 1f : shift;
		}
		catch (System.Exception e)
		{
			ServerPlugin.Log.LogWarning($"Tierarten: Sternfarbe nicht bestimmbar ({texture.name}): {e.Message}");
			return 0f;
		}
		finally
		{
			RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(target);
			Object.Destroy(read);
		}
	}
}

/// <summary>
/// Mehr als fünf Sterne, per Befehl gespawnt (Gorduan, 04.11.2026: „das ist okay, diese sollten dann aber auch die Größen, Farben und
/// HP-, Stärkezunahmen befolgen“): Leben (× Stufe) und Schaden (+50 % je Stern) rechnet Valheim für jede Stufe. Die Sternwirkung
/// endete aber bei fünf (SetupLevelVisualization bricht ab, wenn die Stufe fehlt). Fehlende Stufen kommen am Tier dazu: je Stern
/// ein Zehntel der Größe mit fünf Sternen mehr, bei 15 doppelt so groß (Gorduan, 05.11.2026); Farbe StarColors. Mehr als 15 Sterne
/// gibt es nicht (StarCap).
/// </summary>
internal static class StarOverflow
{
	private const int Known = 5;

	/// <summary>Höchstens so viele Sterne (Gorduan, 05.11.2026: „bis zu 15 Sterne sind erlaubt“), also Stufe 16.</summary>
	internal const int MaxStars = 15;

	internal static void Extend(LevelEffects effects, int level)
	{
		List<LevelEffects.LevelSetup>? setups = effects.m_levelSetups;
		if (setups == null || setups.Count < Known || level - 1 <= setups.Count)
		{
			return;
		}
		float five = setups[Known - 1].m_scale;
		while (setups.Count < level - 1 && setups.Count < MaxStars)
		{
			LevelEffects.LevelSetup top = setups[setups.Count - 1];
			setups.Add(new LevelEffects.LevelSetup
			{
				m_scale = five * (1f + (setups.Count + 1 - Known) / 10f),
				m_hue = top.m_hue,
				m_saturation = top.m_saturation,
				m_value = top.m_value,
				m_setEmissiveColor = top.m_setEmissiveColor,
				m_emissiveColor = top.m_emissiveColor,
			});
		}
	}
}

/// <summary>Mehr als 15 Sterne (per Befehl) werden zu 15.</summary>
[HarmonyPatch(typeof(Character), nameof(Character.SetLevel))]
internal static class StarCapPatch
{
	private static void Prefix(ref int level) => level = Mathf.Min(level, StarOverflow.MaxStars + 1);
}

/// <summary>
/// Boss-Leiste ab sechs Sternen (Gorduan, 05.11.2026: „ab 6 Sterne gibt es immer eine Boss-Leiste“): Nur solange Valheims Lebensbalken
/// rechnet (EnemyHud.LateUpdate), gelten sie als Boss: großer Balken oben, sichtbar solange sie alarmiert sind. Sonst bleiben sie
/// normale Kreaturen (keine Boss-Musik, keine Boss-Regeln; ShowingBossHud und GetActiveBoss außerhalb sehen sie nicht).
/// </summary>
internal static class StarBossBar
{
	internal static int Depth;

	internal static bool Applies(Character character) => Depth > 0 && character.GetLevel() > 6 && !character.IsPlayer();
}

[HarmonyPatch(typeof(EnemyHud), "LateUpdate")]
internal static class StarBossBarScopePatch
{
	private static void Prefix() => StarBossBar.Depth++;

	private static System.Exception? Finalizer(System.Exception? __exception)
	{
		StarBossBar.Depth = Mathf.Max(0, StarBossBar.Depth - 1);
		return __exception;
	}
}

[HarmonyPatch(typeof(Character), nameof(Character.IsBoss))]
internal static class StarBossBarPatch
{
	private static void Postfix(Character __instance, ref bool __result)
	{
		if (!__result && StarBossBar.Applies(__instance))
		{
			__result = true;
		}
	}
}

[HarmonyPatch(typeof(LevelEffects), nameof(LevelEffects.SetupLevelVisualization))]
internal static class StarColorsPatch
{
	private static void Prefix(LevelEffects __instance, int level) => StarOverflow.Extend(__instance, level);

	private static void Postfix(LevelEffects __instance, int level) => StarColors.Apply(__instance, level);
}

[HarmonyPatch(typeof(LevelEffects), nameof(LevelEffects.GetColorChanges))]
internal static class StarColorsCorpsePatch
{
	private static void Postfix(LevelEffects __instance, ref float hue, ref float saturation, ref float value) => StarColors.Colors(__instance, ref hue, ref saturation, ref value);
}
