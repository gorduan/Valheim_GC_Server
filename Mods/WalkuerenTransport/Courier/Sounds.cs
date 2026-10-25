using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using GC.Server.Leash;
using UnityEngine;
using UnityEngine.Networking;

namespace GC.Server.Courier;

/// <summary>
/// Geräusche und Musik des Walküren-Transports (Gorduan: „freie Sounds und epische Kampfmusik aus dem Internet, laute Basstöne
/// wie in Krieg der Welten, gruseliges Lachen, geheulte Schreie“). Die Dateien (OGG, frei lizenziert, Quellen in
/// Assets\Sounds\QUELLEN.md und im README) stecken in der DLL. Beim ersten Spielstart werden sie in BepInEx\cache\GC_Server
/// entpackt und mit Unitys eigenem Lader gelesen. Geräusche laufen über Valheims Geräusch-Regler, Musik über Valheims
/// Musik (MusicMan) und damit über den Musik-Regler. Fehlt eine Datei, bleibt es bei Valheims eigenen Geräuschen.
/// </summary>
internal static class Sounds
{
	// Gorduan 19.10.2026: Donner, Heulen, Möwe und Basston kommen aus Valheim selbst; keine Sirene, kein Nebelhorn, kein Lachen,
	// keine Posaune (es soll zu Valheim passen und nicht nach Meme klingen)
	internal const string ValkyrieScreech = "valk_screech";
	internal const string ScreamWail = "scream_wail";
	internal const string Quake = "quake_rumble";
	internal const string BoomBig = "boom_big";
	internal const string Heartbeat = "heartbeat";
	internal const string Choir = "choir_doom";

	/// <summary>Valheims eigene Geräusche und Effekte für dieselben Momente.</summary>
	internal const string ValheimThunder = "sfx_thunder";
	internal const string ValheimThunderFar = "sfx_distant thunder";
	internal const string ValheimThunderMist = "sfx_mistlands_thunder";
	internal const string ValheimHowl = "sfx_fenring_howl";
	internal const string ValheimGull = "sfx_seagull_idle";
	internal const string ValheimSummon = "fx_bossstone_activate_Eikthyr"; // Opferstein: tiefer Boss-Ruf statt Horn
	internal const string ValheimRoar = "fx_Fader_Roar";

	/// <summary>Musikstücke: Name in Valheims Musikliste (MusicMan) = gc_ + Dateiname.</summary>
	internal const string MusicValkyrie = "gc_music_valkyrie";
	internal const string MusicBattle = "gc_music_battle";
	internal const string MusicEaster = "gc_music_easter";

	private static readonly Dictionary<string, AudioClip> Clips = new();
	private static readonly HashSet<string> Failed = new();
	private static bool loading;
	private static CourierRunner? loader;

	/// <summary>Beim Spielstart (nur mit Grafik): alle Dateien laden, die noch fehlen (auch nach einem Ausloggen mitten im Laden).</summary>
	internal static void Load()
	{
		if (loading && loader != null)
		{
			return;
		}
		loading = true;
		loader = Show.Runner();
		loader.StartCoroutine(LoadAll());
	}

	private static IEnumerator LoadAll()
	{
		Assembly assembly = typeof(Sounds).Assembly;
		string[] resources = assembly.GetManifestResourceNames().Where(r => r.Contains(".Sounds.") && r.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)).ToArray();
		string folder = Path.Combine(Paths.CachePath, "GC_Server", "sounds");
		try
		{
			Directory.CreateDirectory(folder);
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Walküren-Transport: Ordner für Geräusche nicht angelegt: {e.Message}");
			loading = false;
			yield break;
		}
		int count = 0;
		foreach (string resource in resources)
		{
			string file = resource.Substring(resource.LastIndexOf(".Sounds.", StringComparison.Ordinal) + ".Sounds.".Length);
			string name = Path.GetFileNameWithoutExtension(file);
			if (Clips.ContainsKey(name) || Failed.Contains(name))
			{
				continue;
			}
			string path = Path.Combine(folder, file);
			try
			{
				using Stream? stream = assembly.GetManifestResourceStream(resource);
				if (stream == null)
				{
					continue;
				}
				if (!File.Exists(path) || new FileInfo(path).Length != stream.Length)
				{
					using FileStream output = File.Create(path);
					stream.CopyTo(output);
				}
			}
			catch (Exception e)
			{
				ServerPlugin.Log.LogWarning($"Walküren-Transport: {file} nicht entpackt: {e.Message}");
				Failed.Add(name);
				continue;
			}
			using UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.OGGVORBIS);
			if (request.downloadHandler is DownloadHandlerAudioClip handler)
			{
				handler.compressed = name.StartsWith("music_", StringComparison.Ordinal); // Musik bleibt gepackt im Speicher
				handler.streamAudio = false;
			}
			yield return request.SendWebRequest();
			if (request.result != UnityWebRequest.Result.Success)
			{
				ServerPlugin.Log.LogWarning($"Walküren-Transport: {file} nicht gelesen: {request.error}");
				Failed.Add(name);
				continue;
			}
			AudioClip? clip = DownloadHandlerAudioClip.GetContent(request);
			if (clip == null)
			{
				Failed.Add(name);
				continue;
			}
			clip.name = "GC_" + name;
			Clips[name] = clip;
			count++;
		}
		loading = false;
		RegisterMusic();
		ServerPlugin.Log.LogInfo($"Walküren-Transport: {Clips.Count} Geräusche und Musikstücke bereit ({count} neu geladen).");
	}

	/// <summary>Die Musikstücke in Valheims Musikliste eintragen (einmal; MusicMan bleibt über Spielstarts hinweg bestehen).</summary>
	internal static void RegisterMusic()
	{
		MusicMan? man = MusicMan.instance;
		if (man == null)
		{
			return;
		}
		Add(man, MusicValkyrie, "music_valkyrie", loop: false, volume: 0.9f);
		Add(man, MusicBattle, "music_battle", loop: true, volume: 0.85f);
		Add(man, MusicEaster, "music_easter", loop: true, volume: 1f);
	}

	private static void Add(MusicMan man, string musicName, string clipName, bool loop, float volume)
	{
		int hash = musicName.GetStableHashCode();
		if (!Clips.TryGetValue(clipName, out AudioClip clip) || man.m_musicHashes.ContainsKey(hash))
		{
			return;
		}
		MusicMan.NamedMusic music = new()
		{
			m_name = musicName,
			m_clips = new[] { clip },
			m_volume = volume,
			m_fadeInTime = 1.5f,
			m_alwaysFadeout = true,
			m_loop = loop,
			m_resume = false,
			m_enabled = true,
			m_ambientMusic = false,
		};
		man.m_music.Add(music);
		man.m_musicHashes[hash] = music; // MusicMan sucht nur in dieser Tabelle (beim Start aus m_music gebaut)
	}

	internal static bool HasMusic(string musicName)
	{
		MusicMan? man = MusicMan.instance;
		if (man == null || string.IsNullOrEmpty(musicName))
		{
			return false;
		}
		RegisterMusic();
		return man.m_musicHashes.ContainsKey(musicName.GetStableHashCode());
	}

	/// <summary>
	/// Ein Geräusch abspielen (Valheims Geräusch-Regler). global: fast überall gleich laut (Hörner, Herzschlag im Easteregg),
	/// sonst räumlich bis range Meter. follow: wandert mit (Schrei der fliegenden Walküre).
	/// </summary>
	internal static void Play(string name, Vector3 position, float volume, float range, bool global = false, Transform? follow = null)
	{
		if (!Clips.TryGetValue(name, out AudioClip clip) || clip == null)
		{
			return;
		}
		GameObject go = new("GC_Courier_" + name);
		go.transform.position = position;
		if (follow != null)
		{
			go.transform.SetParent(follow, true);
		}
		AudioSource source = go.AddComponent<AudioSource>();
		source.clip = clip;
		source.volume = Mathf.Clamp01(volume);
		source.outputAudioMixerGroup = Whistles.SfxGroup();
		source.spatialBlend = global ? 0.25f : 1f;
		source.rolloffMode = AudioRolloffMode.Linear;
		source.minDistance = global ? range * 0.5f : Mathf.Max(5f, range * 0.12f);
		source.maxDistance = range;
		source.dopplerLevel = 0f;
		source.priority = 16;
		source.Play();
		UnityEngine.Object.Destroy(go, clip.length + 0.5f);
	}
}
