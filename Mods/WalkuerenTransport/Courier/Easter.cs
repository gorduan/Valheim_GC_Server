using System;
using System.Collections;
using System.Collections.Generic;
using GC.Shared;
using UnityEngine;
using UnityEngine.UI;

namespace GC.Server.Courier;

/// <summary>
/// Easteregg des Walküren-Transports (Gorduan, 19.10.2026: „völlig übertriebene Effekte, nicht vom Schaden oder der Zerstörung
/// her … tobe dich aus und sei kreativ“, „greif tief in die Trickkiste, vorhandene Events wie Nebel in einem wilden Mix,
/// szenisch nacheinander“). Statt 5 s Gewitter läuft ein Schauspiel in acht Szenen, das sich steigert, und am Ende kommt
/// statt der Walküre eine Möwe. Alles nur zum Ansehen und Hören; Schaden macht wie immer nur der eine Vernichter-Blitz.
/// Gorduan 19.10.2026: Auch das Easteregg muss zu Valheim passen und darf nicht nach Meme klingen: Donner, Heulen, Möwe und die
/// tiefen Töne kommen aus Valheim (Opferstein-Ruf, Faders Brüllen), keine Sirene, kein Lachen, keine Posaune.
///
/// 0 Stille: schwarze Nacht, Herzschlag, ein fernes Heulen.
/// 1 Erwachen: Ruf des Opfersteins und Faders Brüllen, Erdbeben, Staubfontänen, der Karren schwebt.
/// 2 Götterzorn: roter Himmel, wilde Blitze ringsum, der echte Blitz, blaues Feuer.
/// 3 Wilder Mix: Valheims Raid-Ansagen im Sekundentakt mit ihrem Wetter, Nebel, Bossrufe.
/// 4 Feuer am Himmel: Feuerwerk, Kometen, Explosionen hoch oben, goldener Himmel.
/// 5 Geisterstunde: grüner Nebel, Geister und Untote steigen auf, Schreie.
/// 6 Höhepunkt: alles zugleich, Blitzlicht, Götter-Explosion, dann Totenstille.
/// 7 Ausklang: ein Chor, „Die Walküre naht …“, und es kommt eine Möwe.
/// </summary>
internal static class Easter
{
	internal const float CallSeconds = 44f;
	internal const string VoidEnv = "GC_CourierVoid";
	internal const string BloodEnv = "GC_CourierBlood";
	internal const string ToxicEnv = "GC_CourierToxic";
	internal const string GoldEnv = "GC_CourierGold";

	private static readonly string[] MixEnvs =
	{
		"Misty", "Bonemass", "SnowStorm", "Ashlands_storm", "Mistlands_thunder", "GDKing", "Eikthyr", "GoblinKing",
		"Crypt", "DeepForest Mist", "Darklands_dark", "Twilight_SnowStorm", "Moder",
	};

	private static readonly string[] Fireworks =
	{
		"vfx_Firework_Rocket_Red", "vfx_Firework_Rocket_Blue", "vfx_Firework_Rocket_Green", "vfx_Firework_Rocket_Yellow",
		"vfx_Firework_Rocket_Purple", "vfx_Firework_Rocket_Cyan", "vfx_FireWork_ThunderStone", "vfx_FireWork_BlackCore",
		"vfx_FireWork_SurtlingCore",
	};

	private static readonly string[] SkyBlasts = { "fx_goblinking_meteor_hit", "fx_fader_meteor_hit", "fx_dynamite_explosion", "fx_siegebomb_explosion" };
	private static readonly string[] Fogs = { "vfx_ground_fog", "vfx_swamp_mist", "vfx_mistlands_mist", "vfx_darkland_groundfog" };
	private static readonly string[] BossCries = { "sfx_dragon_scream", "sfx_gdking_scream", "sfx_Bonemass_alert", "sfx_eikthyr_alert", "fx_goblinking_vo_meteors1", "sfx_frozenking_voice_scream", "sfx_fenring_howl" };
	private static readonly string[] Risers = { "vfx_ghost_spawn", "vfx_DraugrSpawn", "fx_summon_skeleton_spawn", "vfx_odin_despawn", "fx_raven_despawn" };

	private static float shakeUntil;
	private static float shakeStrength;
	private static Image? flash;

	/// <summary>Der ganze Ablauf bei einem Spieler in der Nähe. mine: dieser Spieler hat gepfiffen (setzt den echten Blitz).</summary>
	internal static IEnumerator Call(Vector3 center, Show.Dummy? cart, bool mine, long playerId)
	{
		float start = Time.time;
		Transform? cartTransform = cart?.Root != null ? cart.Root.transform : null;
		Vector3 cartPosition = cartTransform != null ? cartTransform.position : center;
		Quaternion cartRotation = cartTransform != null ? cartTransform.rotation : Quaternion.identity;
		CourierRunner runner = Show.Runner();
		runner.StartCoroutine(Quake(center));
		Coroutine? float_ = cartTransform != null ? runner.StartCoroutine(Levitate(cartTransform, cartPosition, cartRotation, start)) : null;
		GameCamera? camera = GameCamera.instance;
		float fov = camera != null ? camera.m_fov : 65f;

		// 0 Stille
		Show.Sky(VoidEnv, 4f, 0.3f);
		Sounds.Play(Sounds.Heartbeat, center, 1f, 200f, global: true);
		Message("gccr_egg_1");
		yield return Until(start, 1.8f);
		Show.Local("sfx_fenring_howl", center + Ring(60f) + Vector3.up * 10f, Quaternion.identity);
		Sounds.Play(Sounds.Heartbeat, center, 1f, 200f, global: true);
		yield return Until(start, 3.5f);

		// 1 Erwachen
		Show.Local(Sounds.ValheimSummon, center, Quaternion.identity);
		Show.Local(Sounds.ValheimRoar, center + Vector3.up * 20f, Quaternion.identity);
		Show.Music(Sounds.MusicEaster, CallSeconds + Show.PickupSeconds(true));
		Sounds.Play(Sounds.Quake, center, 0.9f, 250f, global: true);
		Shake(4.5f, 3.5f);
		Message("gccr_egg_2");
		for (float t = 3.5f; t < 8f; t += 0.35f)
		{
			Vector3 spot = center + Ring(UnityEngine.Random.Range(4f, 18f));
			Ground(ref spot);
			Show.Local(UnityEngine.Random.value < 0.5f ? "vfx_gdking_stomp" : "fx_eikthyr_stomp", spot, Quaternion.identity, 1f);
			yield return Until(start, t);
		}

		// 2 Götterzorn
		Show.Sky(BloodEnv, 6.5f, 0.5f);
		Message("gccr_egg_3");
		bool struck = false;
		for (float t = 8f; t < 14f; t += UnityEngine.Random.Range(0.18f, 0.45f))
		{
			if (!struck && t >= 9f)
			{
				struck = true;
				Show.Strike(center, mine); // der einzige echte Blitz: Schaden wie beim Vernichter
				Show.Local(Sounds.ValheimThunder, center + Vector3.up * 3f, Quaternion.identity);
				Show.Local(Sounds.ValheimThunderMist, center + Vector3.up * 10f, Quaternion.identity);
				Flash(new Color(0.75f, 0.85f, 1f), 0.35f);
				if (cart?.Root != null)
				{
					runner.StartCoroutine(Show.BlueFire(cart.Root, 30f, 3.2f));
				}
			}
			Vector3 spot = center + Ring(UnityEngine.Random.Range(7f, 35f));
			Ground(ref spot);
			Show.Local(UnityEngine.Random.value < 0.6f ? "fx_Lightning" : "fx_Lightning_red", spot, Quaternion.identity, UnityEngine.Random.Range(0.8f, 1.6f));
			Show.Local("sfx_thunder", spot, Quaternion.identity);
			if (UnityEngine.Random.value < 0.25f)
			{
				Flash(Color.white, 0.12f);
			}
			yield return Until(start, t);
		}

		// 3 Wilder Mix: Valheims Raid-Ansagen mit ihrem Wetter, Nebel, Bossrufe
		List<(string message, string env)> raids = RaidAnnouncements();
		for (int i = 0; i < 6; i++)
		{
			float t = 14f + i * 1.15f;
			(string message, string env) = raids.Count > 0 ? raids[i % raids.Count] : ("", "");
			Show.Sky(env.Length > 0 && EnvMan.instance != null && EnvMan.instance.GetEnv(env) != null ? env : MixEnvs[UnityEngine.Random.Range(0, MixEnvs.Length)], 1.4f, 0.35f);
			if (message.Length > 0)
			{
				MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, Localization.instance.Localize(message));
			}
			Vector3 spot = center + Ring(UnityEngine.Random.Range(5f, 20f));
			Ground(ref spot);
			Show.Local(Fogs[i % Fogs.Length], spot, Quaternion.identity, 2f, lifetime: 9f); // Nebel nur für diese Szene
			Show.Local(BossCries[i % BossCries.Length], center + Ring(40f) + Vector3.up * 15f, Quaternion.identity);
			if (i == 2)
			{
				Show.Local(Sounds.ValheimHowl, center + Ring(25f), Quaternion.identity);
			}
			yield return Until(start, t + 1.15f);
		}

		// 4 Feuer am Himmel
		Show.Sky(GoldEnv, 8f, 0.6f);
		Message("gccr_egg_4");
		for (float t = 21f; t < 29f; t += 0.3f)
		{
			Vector3 ground = center + Ring(UnityEngine.Random.Range(3f, 25f));
			Ground(ref ground);
			Show.Local(Fireworks[UnityEngine.Random.Range(0, Fireworks.Length)], ground, Quaternion.Euler(UnityEngine.Random.Range(-8f, 8f), 0f, UnityEngine.Random.Range(-8f, 8f)));
			if (UnityEngine.Random.value < 0.35f)
			{
				Vector3 sky = center + Ring(UnityEngine.Random.Range(20f, 70f)) + Vector3.up * UnityEngine.Random.Range(45f, 90f);
				Show.Local(SkyBlasts[UnityEngine.Random.Range(0, SkyBlasts.Length)], sky, Quaternion.identity, UnityEngine.Random.Range(2f, 4f));
				Sounds.Play(Sounds.BoomBig, sky, 0.8f, 400f);
			}
			if (UnityEngine.Random.value < 0.22f)
			{
				runner.StartCoroutine(Comet(center));
			}
			yield return Until(start, t);
		}

		// 5 Geisterstunde
		Show.Sky(ToxicEnv, 6.5f, 0.5f);
		Message("gccr_egg_5");
		for (float t = 29f; t < 35f; t += 0.4f)
		{
			Vector3 spot = center + Ring(UnityEngine.Random.Range(6f, 14f));
			Ground(ref spot);
			Show.Local(Risers[UnityEngine.Random.Range(0, Risers.Length)], spot, Quaternion.identity, 1.5f);
			if (UnityEngine.Random.value < 0.3f)
			{
				Show.Local("sfx_ghost_alert", spot + Vector3.up * 2f, Quaternion.identity);
			}
			if (t > 31f && t < 31.5f)
			{
				Sounds.Play(Sounds.ScreamWail, center + Ring(15f), 1f, 250f, global: true);
			}
			yield return Until(start, t);
		}

		// 6 Höhepunkt: alles zugleich
		Message("gccr_egg_6");
		Show.Local("fx_bossstone_activate_Yagluth", center, Quaternion.identity);
		Show.Local(Sounds.ValheimRoar, center + Vector3.up * 20f, Quaternion.identity);
		Shake(6f, 6f);
		string[] flicker = { VoidEnv, BloodEnv, GoldEnv, ToxicEnv };
		int f = 0;
		for (float t = 35f; t < 40.5f; t += 0.15f)
		{
			Show.Sky(flicker[f++ % flicker.Length], 0.6f, 0.1f);
			Vector3 spot = center + Ring(UnityEngine.Random.Range(5f, 30f));
			Ground(ref spot);
			Show.Local("fx_Lightning", spot, Quaternion.identity, 1.3f);
			if (f % 3 == 0)
			{
				Show.Local(Fireworks[UnityEngine.Random.Range(0, Fireworks.Length)], spot, Quaternion.identity);
			}
			if (f % 5 == 0)
			{
				Flash(Color.white, 0.1f);
				Show.Local("sfx_thunder", spot, Quaternion.identity);
			}
			if (camera != null)
			{
				camera.m_fov = fov + Mathf.Sin((t - 35f) * 3f) * 12f; // die Welt pumpt
			}
			yield return Until(start, t);
		}
		if (camera != null)
		{
			camera.m_fov = fov;
		}
		Show.Local("vfx_GodExplosion", center + Vector3.up * 2f, Quaternion.identity, 1f);
		Sounds.Play(Sounds.BoomBig, center, 1f, 400f, global: true);
		Flash(Color.white, 0.6f);
		Shake(1.2f, 9f);

		// 7 Totenstille, dann erheben sich die Wächter (Gorduan): Grauzwerg, Berserker und Schamane, groß wie Bosse
		shakeUntil = 0f;
		Show.Sky("Clear", 2f, 0.2f);
		runner.StartCoroutine(Show.Hush(2.6f));
		if (float_ != null)
		{
			runner.StopCoroutine(float_);
		}
		if (cartTransform != null)
		{
			runner.StartCoroutine(Settle(cartTransform, cartPosition, cartRotation));
		}
		yield return Until(start, 42f);
		Show.Sky("GDKing", 1800f, 1.5f, center, Transport.ShowRange + 100f); // Wetter des Ältesten, Herr der Grauzwerge: bis die Wächter fallen
		Show.Music(Sounds.MusicEaster, 1800f, center, Transport.ShowRange + 100f);
		Show.Local("fx_bossstone_activate_TheElder", center, Quaternion.identity);
		Message("gccr_egg_guards");
		// wie Valheims Boss-Beschwörung am Opferaltar (OfferingBowl): erst das Vorzeichen, 5 s später erscheint der Boss
		Vector3[] spots = Guards.Spots(center, playerId);
		foreach (Vector3 spot in spots)
		{
			Show.Local("vfx_prespawn", spot, Quaternion.identity, 2.5f, null, 8f);
			Show.Local("sfx_prespawn", spot, Quaternion.identity);
		}
		yield return Until(start, 47f);
		Shake(2.5f, 4f);
		foreach (Vector3 spot in spots)
		{
			Show.Local("vfx_spawn_large", spot, Quaternion.identity, 2.5f);
			Show.Local("sfx_gdking_spawn", spot, Quaternion.identity);
			Show.Local("fx_gdking_rootspawn", spot, Quaternion.identity, 1.5f);
		}
		if (mine && !(Show.FinaleAt.TryGetValue(playerId, out float finale) && finale >= start))
		{
			// nur der Spieler, der gepfiffen hat, ruft sie herbei (sonst gäbe es sie mehrfach); ist er weit fort, entfallen sie.
			// Kam das Finale schon (der Server hat zu lange nichts gehört), entfallen sie auch: sonst blieben sie ohne Kampf übrig.
			Player? me = Player.m_localPlayer;
			List<ZDOID> guards = me != null && Vector3.Distance(me.transform.position, center) < 120f ? Guards.Spawn(center, playerId) : new List<ZDOID>();
			Transport.ReportGuards(playerId, guards);
		}
	}

	/// <summary>Warten bis start + seconds (auch wenn ein Bild länger dauert, bleibt der Takt).</summary>
	private static WaitForSeconds? Until(float start, float seconds)
	{
		float wait = start + seconds - Time.time;
		return wait > 0f ? new WaitForSeconds(wait) : null;
	}

	private static void Message(string key)
	{
		MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, GcLoc.Get(key));
	}

	private static Vector3 Ring(float radius)
	{
		Vector2 dir = UnityEngine.Random.insideUnitCircle.normalized;
		return new Vector3(dir.x, 0f, dir.y) * radius;
	}

	private static void Ground(ref Vector3 point)
	{
		if (ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(point, out float height))
		{
			point.y = height;
		}
	}

	private static void Shake(float seconds, float strength)
	{
		shakeUntil = Time.time + seconds;
		shakeStrength = strength;
	}

	/// <summary>Erdbeben: solange gesetzt, rüttelt die Kamera jedes Bild (Valheims Kamera-Wackeln, dauerhaft).</summary>
	private static IEnumerator Quake(Vector3 center)
	{
		float end = Time.time + CallSeconds;
		while (Time.time < end)
		{
			if (Time.time < shakeUntil && GameCamera.instance != null)
			{
				GameCamera.instance.AddShake(center, 80f, shakeStrength * (0.7f + Mathf.PerlinNoise(Time.time * 3f, 0f) * 0.6f), true);
			}
			yield return null;
		}
	}

	/// <summary>Der Karren zittert, hebt sich und dreht sich, im Höhepunkt immer schneller.</summary>
	private static IEnumerator Levitate(Transform cart, Vector3 position, Quaternion rotation, float start)
	{
		float spin = 0f;
		while (cart != null)
		{
			float t = Time.time - start;
			float lift = t < 3.5f ? 0f : Mathf.Min(2.2f, (t - 3.5f) * 0.45f) + (t > 35f ? Mathf.Min(2f, (t - 35f) * 0.8f) : 0f);
			float speed = t < 8f ? 0f : t < 35f ? 25f : 160f;
			spin += speed * Time.deltaTime;
			Vector3 jitter = t < 3.5f ? UnityEngine.Random.insideUnitSphere * 0.02f : UnityEngine.Random.insideUnitSphere * 0.05f;
			cart.position = position + Vector3.up * (lift + Mathf.Sin(t * 2f) * 0.15f * Mathf.Clamp01(lift)) + jitter;
			cart.rotation = rotation * Quaternion.Euler(Mathf.Sin(t * 1.3f) * 6f * Mathf.Clamp01(lift), spin, Mathf.Cos(t * 1.7f) * 6f * Mathf.Clamp01(lift));
			yield return null;
		}
	}

	/// <summary>Nach dem Höhepunkt sinkt der Karren an seinen Platz zurück (dort holt ihn die Möwe).</summary>
	private static IEnumerator Settle(Transform cart, Vector3 position, Quaternion rotation)
	{
		if (cart == null)
		{
			yield break; // der Karren ist schon fort (Prüfung 19.10.)
		}
		Vector3 from = cart.position;
		Quaternion turn = cart.rotation;
		float t = 0f;
		while (t < 1f && cart != null)
		{
			t += Time.deltaTime / 0.8f;
			float k = t * t;
			cart.position = Vector3.Lerp(from, position, k);
			cart.rotation = Quaternion.Slerp(turn, rotation, k);
			yield return null;
		}
		if (cart != null)
		{
			cart.position = position;
			cart.rotation = rotation;
			Show.Local("sfx_cart_hit", position, Quaternion.identity);
		}
	}

	/// <summary>Ein Komet: brennende Kugel quer über den Himmel, am Ende eine Explosion hoch oben.</summary>
	private static IEnumerator Comet(Vector3 center)
	{
		Vector3 from = center + Ring(160f) + Vector3.up * UnityEngine.Random.Range(70f, 110f);
		Vector3 to = center - (from - center) * 0.6f + Vector3.up * UnityEngine.Random.Range(40f, 70f);
		to.y = Mathf.Max(to.y, center.y + 40f);
		GameObject comet = new("GC_CourierComet");
		comet.transform.position = from;
		string[] fires = { "vfx_Burning", "vfx_Burning_blue", "vfx_Burning_green" };
		Show.Local(fires[UnityEngine.Random.Range(0, fires.Length)], from, Quaternion.identity, 4f, comet.transform);
		Light light = comet.AddComponent<Light>();
		light.type = LightType.Point;
		light.range = 40f;
		light.intensity = 3f;
		light.color = new Color(1f, 0.6f, 0.25f);
		float seconds = UnityEngine.Random.Range(2.2f, 3.5f);
		float t = 0f;
		while (t < seconds && comet != null)
		{
			t += Time.deltaTime;
			float k = t / seconds;
			comet.transform.position = Vector3.Lerp(from, to, k) + Vector3.up * Mathf.Sin(k * Mathf.PI) * 15f;
			yield return null;
		}
		if (comet != null)
		{
			Show.Local(SkyBlasts[UnityEngine.Random.Range(0, SkyBlasts.Length)], comet.transform.position, Quaternion.identity, 3f);
			Sounds.Play(Sounds.BoomBig, comet.transform.position, 0.7f, 400f);
			UnityEngine.Object.Destroy(comet);
		}
	}

	/// <summary>Die Ansagen von Valheims Raids (Text und ihr Wetter), gemischt.</summary>
	private static List<(string, string)> RaidAnnouncements()
	{
		List<(string, string)> list = new();
		if (RandEventSystem.instance != null)
		{
			foreach (RandomEvent ev in RandEventSystem.instance.m_events)
			{
				if (ev != null && !string.IsNullOrEmpty(ev.m_startMessage) && ev.m_name != RaidHens.EventName)
				{
					list.Add((ev.m_startMessage, ev.m_forceEnvironment ?? ""));
				}
			}
		}
		for (int i = list.Count - 1; i > 0; i--)
		{
			int j = UnityEngine.Random.Range(0, i + 1);
			(list[i], list[j]) = (list[j], list[i]);
		}
		return list;
	}

	/// <summary>Blitzlicht: der ganze Bildschirm leuchtet kurz in einer Farbe auf.</summary>
	private static void Flash(Color color, float seconds)
	{
		if (flash == null)
		{
			GameObject canvasGo = new("GC_CourierFlash");
			canvasGo.transform.SetParent(Show.Runner().transform, false);
			Canvas canvas = canvasGo.AddComponent<Canvas>();
			canvas.renderMode = RenderMode.ScreenSpaceOverlay;
			canvas.sortingOrder = 30000;
			GameObject imageGo = new("Flash");
			imageGo.transform.SetParent(canvasGo.transform, false);
			flash = imageGo.AddComponent<Image>();
			flash.raycastTarget = false;
			RectTransform rect = flash.rectTransform;
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = Vector2.zero;
			rect.offsetMax = Vector2.zero;
		}
		Show.Runner().StartCoroutine(FadeFlash(color, seconds));
	}

	private static IEnumerator FadeFlash(Color color, float seconds)
	{
		float t = 0f;
		while (t < seconds && flash != null)
		{
			t += Time.deltaTime;
			Color c = color;
			c.a = Mathf.Lerp(0.85f, 0f, t / seconds);
			flash.color = c;
			yield return null;
		}
		if (flash != null)
		{
			flash.color = Color.clear;
		}
	}
}
