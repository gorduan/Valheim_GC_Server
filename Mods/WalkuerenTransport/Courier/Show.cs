using System;
using System.Collections;
using System.Collections.Generic;
using GC.Shared;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace GC.Server.Courier;

/// <summary>Läuft die Abläufe (Coroutinen) der Effekte; gehört zur Spielsitzung und verschwindet beim Ausloggen mit der Szene.</summary>
internal sealed class CourierRunner : MonoBehaviour
{
	/// <summary>Ausgeloggt mitten in der Totenstille: die Musik-Lautstärke sofort zurück (Prüfung 19.10.).</summary>
	private void OnDestroy() => Show.Unhush();
}

/// <summary>
/// Was die Spieler sehen und hören (nur auf Spielern, nie auf dem Server ohne Grafik). Alles hier ist örtlich bei jedem
/// Spieler im Umkreis von 150 m: Unwetter, blaues Feuer, die Walküre (oder die Möwe) mit dem Seil, der fallende Karren.
/// Echt (für alle über das Netz, mit Schaden) ist nur der Vernichter-Blitz: ihn setzt der Spieler, der gepfiffen hat.
/// Die Fracht wirft nur der abliefernde Spieler ab.
/// </summary>
internal static class Show
{
	internal const float NormalCallSeconds = 5.5f;

	/// <summary>
	/// Gorduan, 24.10.2026: „lasse den Blitz erst 3 sek nach dem Pfeifen einschlagen bei beidem“. Beim Abholen und beim Abliefern
	/// schlägt der Vernichter-Blitz (400 Blitzschaden am Einschlag) erst so lange nach dem Pfiff ein: Zeit zum Ausweichen.
	/// Vorher kam er beim Abholen nach 1,2 s und beim Abliefern sofort, genau auf den Pfeifenden.
	/// </summary>
	internal const float StrikeDelaySeconds = 3f;
	/// <summary>Easteregg: Chor und „Die Walküre naht …“, dann erst die Möwe.</summary>
	internal const float FinaleSeconds = 4.5f;
	internal const float ApproachSeconds = 12f;
	internal const float GrabSeconds = 2.5f;
	internal const float DepartSeconds = 10f;
	internal const float RopeLength = 6f;

	/// <summary>
	/// Gorduan, 24.10.2026: „Möwe und Walküre dürfen auch deutlich niedriger an den Karren ranfliegen (0,5 m), wenn sie den Karren
	/// abholen“. So weit über dem Griff des Karrens packt die Hand das Seil (vorher schwebte sie eine Seillänge, 6 m, darüber).
	/// </summary>
	internal const float GrabGap = 0.5f;

	/// <summary>Danach steigt der Träger gerade hoch, bis das Seil gespannt ist; erst dann hebt der Karren ab.</summary>
	internal const float LiftSeconds = 1.5f;
	private const string StormEnv = "GC_CourierStorm";

	internal static float CallSeconds(bool easter) => easter ? Easter.CallSeconds : NormalCallSeconds;

	internal static float PickupSeconds(bool easter) => ApproachSeconds + GrabSeconds + LiftSeconds + DepartSeconds + 0.5f;

	/// <summary>Ein wartender Karren (nur zum Ansehen) und was man zum Zerschellen und für das Seil braucht.</summary>
	internal sealed class Dummy
	{
		public GameObject Root = null!;
		public LineRenderer? Rope;
		public Transform? Line0;
		public Transform? Line1;
		public Transform? Attach;
		public GameObject[] Fragments = Array.Empty<GameObject>();
		public EffectList? Destroyed;
		public float Born;
		/// <summary>So lange wartet der Karren höchstens auf die Abholung (Easteregg: den ganzen Wächter-Kampf).</summary>
		public float Lifetime = 600f;
	}

	private static CourierRunner? runner;
	private static readonly Dictionary<long, Dummy> Waiting = new();
	private static readonly Dictionary<long, (ZDOID record, ZPackage cargo)> Cargo = new();
	private static string? env;
	private static float envUntil;
	private static float envTransition = 1.2f;
	private static Vector3 envCenter;
	private static float envRange;
	private static bool transitionChanged;
	private static float savedTransition = 2f;
	private static string? music;
	private static float musicUntil;
	private static Vector3 musicCenter;
	private static float musicRange;
	private static string? raidEvent;
	private static Vector3 raidPosition;
	private static bool envsReady;
	private static GameObject? valkyriePrefab;
	private static bool valkyrieLoaded;

	internal static void Reset()
	{
		Unhush();
		Waiting.Clear();
		FinaleAt.Clear();
		Cargo.Clear();
		env = null;
		music = null;
		raidEvent = null;
		envsReady = false;
		runner = null;
		valkyrieLoaded = false; // Review 27.10.2026: ein gescheiterter Ladeversuch gilt nur für eine Sitzung (das geladene Prefab bleibt)
		if (!Gc.IsHeadless)
		{
			Sounds.Load();
		}
	}

	internal static CourierRunner Runner()
	{
		if (runner == null)
		{
			GameObject go = new("GC_Courier");
			runner = go.AddComponent<CourierRunner>();
		}
		return runner;
	}

	private static bool FromServer(long sender) => sender == Transport.ServerUid();

	private static bool Near(Vector3 position, float range = Transport.ShowRange)
	{
		Player? me = Player.m_localPlayer;
		return me != null && Vector3.Distance(me.transform.position, position) <= range;
	}

	private static bool Mine(long peer) => ZNet.instance != null && peer == ZNet.GetUID();

	// =================================================================== Wetter und Musik

	/// <summary>Wetter für ein paar Sekunden (Valheims eigener weicher Wechsel, hier schneller); danach kommt das Wetter von vorher wieder.</summary>
	internal static void Sky(string name, float seconds, float transition = 1.2f, Vector3 center = default, float range = 0f)
	{
		EnsureEnvironments();
		if (EnvMan.instance == null || EnvMan.instance.GetEnv(name) == null)
		{
			return;
		}
		env = name; // der letzte Aufruf gilt (das Easteregg wechselt schnell hintereinander)
		envUntil = Time.time + seconds;
		envTransition = transition;
		envCenter = center;
		envRange = range;
	}

	internal static void ClearSky()
	{
		envUntil = 0f;
	}

	/// <summary>Für den Patch auf EnvMan: das Wetter der Effekte oder null.</summary>
	internal static string? CurrentEnv()
	{
		EnvMan? man = EnvMan.instance;
		Player? me = Player.m_localPlayer;
		bool inRange = envRange <= 0f || (me != null && Vector3.Distance(me.transform.position, envCenter) <= envRange);
		if (env != null && Time.time < envUntil && inRange)
		{
			if (man != null)
			{
				if (!transitionChanged)
				{
					savedTransition = man.m_transitionDuration;
					transitionChanged = true;
				}
				man.m_transitionDuration = envTransition;
			}
			return env;
		}
		if (Time.time >= envUntil)
		{
			env = null; // abgelaufen; nur zu weit weg: gilt wieder, wenn man zurückkommt
		}
		if (transitionChanged && man != null && man.m_transitionDuration != savedTransition)
		{
			man.m_transitionDuration = Mathf.Max(savedTransition, 2f); // zurück langsam: das alte Wetter kommt sanft wieder
			transitionChanged = false;
		}
		return null;
	}

	/// <summary>Musik für ein paar Sekunden; mit range nur, solange man so nah am Ort ist (Wächter-Kampf).</summary>
	internal static void Music(string name, float seconds, Vector3 center = default, float range = 0f)
	{
		if (Sounds.HasMusic(name))
		{
			music = name;
			musicUntil = Time.time + seconds;
			musicCenter = center;
			musicRange = range;
		}
	}

	/// <summary>Für den Patch auf RandEventSystem.GetMusicOverride: Musik der Effekte, sonst die Kampfmusik im Raid nach dem Abholen.</summary>
	internal static string? CurrentMusic()
	{
		if (music != null && Time.time < musicUntil)
		{
			Player? me = Player.m_localPlayer;
			if (musicRange <= 0f || (me != null && Vector3.Distance(me.transform.position, musicCenter) <= musicRange))
			{
				return music;
			}
			return null; // zu weit weg: Valheims Musik, aber die Musik der Effekte gilt wieder, wenn man zurückkommt
		}
		music = null;
		if (raidEvent != null && RandEventSystem.instance != null)
		{
			RandomEvent? active = RandEventSystem.instance.GetActiveEvent();
			if (active != null && active.m_name == raidEvent && Vector3.Distance(active.m_pos, raidPosition) < 2f && Sounds.HasMusic(Sounds.MusicBattle))
			{
				return Sounds.MusicBattle;
			}
			if (RandEventSystem.instance.GetCurrentRandomEvent() == null)
			{
				raidEvent = null;
			}
		}
		return null;
	}

	/// <summary>Eigene Wetter: dunkles Gewitter (aus Valheims ThunderStorm) und die Farben des Eastereggs.</summary>
	private static void EnsureEnvironments()
	{
		EnvMan? man = EnvMan.instance;
		if (envsReady || man == null)
		{
			return;
		}
		envsReady = true;
		try
		{
			EnvSetup? storm = man.GetEnv("ThunderStorm");
			if (storm != null)
			{
				Add(man, Dark(storm.Clone(), StormEnv, new Color(0.05f, 0.06f, 0.09f), 0.035f, 0.12f));
				Add(man, Dark(storm.Clone(), Easter.VoidEnv, new Color(0.005f, 0.005f, 0.008f), 0.09f, 0.01f));
				Add(man, Dark(storm.Clone(), Easter.BloodEnv, new Color(0.30f, 0.02f, 0.02f), 0.05f, 0.25f));
				Add(man, Dark(storm.Clone(), Easter.ToxicEnv, new Color(0.08f, 0.28f, 0.04f), 0.05f, 0.25f));
				Add(man, Dark(storm.Clone(), Easter.GoldEnv, new Color(0.45f, 0.32f, 0.05f), 0.03f, 0.6f));
			}
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Walküren-Transport: eigene Wetter nicht angelegt: {e.Message}");
		}
	}

	private static void Add(EnvMan man, EnvSetup setup)
	{
		if (man.GetEnv(setup.m_name) != null)
		{
			return;
		}
		// eigene Liste mit denselben Regen-Partikeln: EnvMan vergleicht die Listen, nicht die Partikel (Prüfung 19.10.)
		if (setup.m_psystems != null)
		{
			setup.m_psystems = (GameObject[])setup.m_psystems.Clone();
		}
		man.AppendEnvironment(setup);
		// AppendEnvironment schaltet die (geteilten) Gewitter-Partikel aus: beim nächsten Bild neu einschalten lassen,
		// sonst hörte ein gerade laufendes Gewitter auf zu regnen
		man.m_currentPSystems = null;
		man.m_currentEnvObject = null;
	}

	/// <summary>Ein Wetter abdunkeln und einfärben: immer dunkel, dichter Nebel in der Farbe, wenig Sonne.</summary>
	private static EnvSetup Dark(EnvSetup env, string name, Color fog, float density, float light)
	{
		env.m_name = name;
		env.m_default = false;
		env.m_alwaysDark = true;
		env.m_ambColorDay = env.m_ambColorNight = fog * 1.3f;
		env.m_fogColorDay = env.m_fogColorMorning = env.m_fogColorEvening = env.m_fogColorNight = fog;
		env.m_fogColorSunDay = env.m_fogColorSunMorning = env.m_fogColorSunEvening = env.m_fogColorSunNight = fog;
		env.m_fogDensityDay = env.m_fogDensityMorning = env.m_fogDensityEvening = env.m_fogDensityNight = density;
		env.m_sunColorDay = env.m_sunColorMorning = env.m_sunColorEvening = env.m_sunColorNight = fog * 2f;
		env.m_lightIntensityDay = light;
		env.m_lightIntensityNight = light * 0.5f;
		return env;
	}

	// =================================================================== Bausteine

	private static readonly HashSet<string> Missing = new();

	/// <summary>
	/// Valheim-Prefab nach Namen aus ZNetScene (mit den Effekten ohne Netz), null wenn es ihn dort nicht gibt. Keine Suche über
	/// alle geladenen Objekte (Jötunns Cache): die ließe das Spiel beim ersten Mal stocken (Prüfung 19.10.).
	/// </summary>
	internal static GameObject? Prefab(string name)
	{
		GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
		if (prefab == null && Missing.Add(name))
		{
			ServerPlugin.Log.LogInfo($"Walküren-Transport: Effekt „{name}“ gibt es nicht, er entfällt.");
		}
		return prefab;
	}

	/// <summary>
	/// Effekt nur bei diesem Spieler (auch wenn das Prefab ein Netzobjekt wäre: dann ohne ZDO). Gorduan: Die Effekte machen keinen
	/// Schaden an Spielern oder der Umgebung, Schaden macht nur der Vernichter-Blitz (Strike). Deshalb entsteht die Kopie unter
	/// einem ausgeschalteten Behälter (Prüfung 2: so läuft noch kein Awake, TerrainOp und SpawnAbility wirken sonst schon beim
	/// Instanziieren). Erst ohne Schadensflächen (Aoe), Geschosse, Krater (TerrainOp, TerrainModifier), Brand- und
	/// Wirkungsflächen (EffectArea, Fire, CinderSpawner), Herbeirufen (SpawnAbility) und Nachgeburten (InstantiatePrefab) wird
	/// sie eingeschaltet. Nach lifetime Sekunden ist der Effekt sicher wieder weg (0: der Aufrufer räumt selbst auf).
	/// </summary>
	internal static GameObject? Local(string name, Vector3 position, Quaternion rotation, float scale = 1f, Transform? parent = null, float lifetime = 20f)
	{
		GameObject? prefab = Prefab(name);
		if (prefab == null)
		{
			return null;
		}
		GameObject holder = new("GC_CourierHolder");
		holder.SetActive(false);
		if (parent != null)
		{
			holder.transform.SetParent(parent, false); // gleiche Größe wie unter parent selbst
		}
		GameObject go = UnityEngine.Object.Instantiate(prefab, position, rotation, holder.transform);
		foreach (MonoBehaviour part in go.GetComponentsInChildren<MonoBehaviour>(true))
		{
			if (part is Aoe or Projectile or TerrainOp or TerrainModifier or EffectArea or SpawnAbility or CinderSpawner or Fire or InstantiatePrefab)
			{
				UnityEngine.Object.DestroyImmediate(part);
			}
		}
		bool terrainOps = TerrainOp.m_forceDisableTerrainOps;
		ZNetView.m_forceDisableInit = true;
		TerrainOp.m_forceDisableTerrainOps = true;
		try
		{
			go.transform.SetParent(parent, false); // erst jetzt eingeschaltet, Awake läuft hier
		}
		finally
		{
			ZNetView.m_forceDisableInit = false;
			TerrainOp.m_forceDisableTerrainOps = terrainOps;
			UnityEngine.Object.Destroy(holder);
		}
		if (lifetime > 0f)
		{
			UnityEngine.Object.Destroy(go, lifetime); // auch Effekte ohne eigenes Aufräumen (Nebel, Wetter-Partikel) verschwinden sicher
		}
		if (scale != 1f)
		{
			go.transform.localScale *= scale;
		}
		return go;
	}

	/// <summary>Totenstille: Valheims Musik für ein paar Sekunden fast stumm, dann wie vorher.</summary>
	internal static IEnumerator Hush(float seconds)
	{
		if (hushedFrom < 0f)
		{
			hushedFrom = MusicMan.m_masterMusicVolume;
		}
		MusicMan.m_masterMusicVolume = hushedFrom * 0.05f;
		yield return new WaitForSeconds(seconds);
		Unhush();
	}

	/// <summary>Musik wieder wie vorher (auch beim nächsten Spielstart, falls man mitten in der Stille ausgeloggt hat).</summary>
	internal static void Unhush()
	{
		if (hushedFrom >= 0f)
		{
			MusicMan.m_masterMusicVolume = hushedFrom;
			hushedFrom = -1f;
		}
	}

	private static float hushedFrom = -1f;

	/// <summary>Valheims Effektliste nur bei diesem Spieler abspielen.</summary>
	internal static void LocalEffects(EffectList? effects, Vector3 position, Quaternion rotation)
	{
		if (effects == null)
		{
			return;
		}
		ZNetView.m_forceDisableInit = true;
		try
		{
			effects.Create(position, rotation);
		}
		finally
		{
			ZNetView.m_forceDisableInit = false;
		}
	}

	/// <summary>
	/// Nur zum Ansehen: eine Kopie ohne Spiel-Logik. Sie entsteht unter einem ausgeschalteten Behälter (dann läuft kein Awake),
	/// Skripte (außer Valheims AnimationEffect für Flügelschläge), Gelenke, Körper und Kollider kommen weg, erst dann wird sie
	/// eingeschaltet. read: Verweise lesen, solange die Skripte noch da sind.
	/// </summary>
	internal static GameObject? Visual(GameObject? prefab, Vector3 position, Quaternion rotation, Action<GameObject>? read = null, bool keepAudio = true)
	{
		if (prefab == null)
		{
			return null;
		}
		GameObject holder = new("GC_CourierHolder");
		holder.SetActive(false);
		GameObject copy = UnityEngine.Object.Instantiate(prefab, position, rotation, holder.transform);
		try
		{
			read?.Invoke(copy);
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Walküren-Transport: {prefab.name}: {e.Message}");
		}
		foreach (Joint joint in copy.GetComponentsInChildren<Joint>(true))
		{
			UnityEngine.Object.DestroyImmediate(joint);
		}
		MonoBehaviour[] scripts = copy.GetComponentsInChildren<MonoBehaviour>(true);
		for (int i = scripts.Length - 1; i >= 0; i--)
		{
			if (scripts[i] != null && scripts[i] is not AnimationEffect && scripts[i] is not ZNetView)
			{
				UnityEngine.Object.DestroyImmediate(scripts[i]);
			}
		}
		foreach (ZNetView view in copy.GetComponentsInChildren<ZNetView>(true))
		{
			UnityEngine.Object.DestroyImmediate(view);
		}
		foreach (Rigidbody body in copy.GetComponentsInChildren<Rigidbody>(true))
		{
			UnityEngine.Object.DestroyImmediate(body);
		}
		foreach (Collider collider in copy.GetComponentsInChildren<Collider>(true))
		{
			UnityEngine.Object.DestroyImmediate(collider);
		}
		if (!keepAudio)
		{
			foreach (AudioSource audio in copy.GetComponentsInChildren<AudioSource>(true))
			{
				audio.playOnAwake = false;
				audio.enabled = false;
			}
		}
		copy.transform.SetParent(null, true);
		UnityEngine.Object.Destroy(holder);
		copy.SetActive(true);
		return copy;
	}

	/// <summary>Ein Karren zum Ansehen (Valheims „Cart“): Ladung nach Füllstand, neu aussehend, Seil vorbereitet, Bruchstücke gemerkt.</summary>
	internal static Dummy? Cart(Vector3 position, Quaternion rotation, float fill)
	{
		Dummy dummy = new() { Born = Time.time };
		GameObject? root = Visual(Prefab(Transport.CartPrefab), position, rotation, copy =>
		{
			Vagon? vagon = copy.GetComponent<Vagon>();
			if (vagon != null)
			{
				foreach (Vagon.LoadData load in vagon.m_loadVis)
				{
					if (load.m_gameobject != null)
					{
						load.m_gameobject.SetActive(fill >= load.m_minPercentage);
					}
				}
				dummy.Line0 = vagon.m_lineAttachPoints0;
				dummy.Line1 = vagon.m_lineAttachPoints1;
				dummy.Attach = vagon.m_attachPoint;
			}
			WearNTear? wear = copy.GetComponent<WearNTear>();
			if (wear != null)
			{
				if (wear.m_new != null)
				{
					wear.m_new.SetActive(true);
				}
				if (wear.m_worn != null)
				{
					wear.m_worn.SetActive(false);
				}
				if (wear.m_broken != null)
				{
					wear.m_broken.SetActive(false);
				}
				dummy.Destroyed = wear.m_destroyedEffect;
				dummy.Fragments = wear.m_fragmentRoots ?? Array.Empty<GameObject>();
				foreach (GameObject fragment in dummy.Fragments)
				{
					if (fragment != null)
					{
						fragment.SetActive(false);
					}
				}
			}
			dummy.Rope = copy.GetComponent<LineRenderer>();
		}, keepAudio: false);
		if (root == null)
		{
			return null;
		}
		dummy.Root = root;
		if (dummy.Rope != null)
		{
			dummy.Rope.enabled = false;
		}
		return dummy;
	}

	/// <summary>Seil vom Karren zu einem Punkt (wie beim Ziehen: Valheims Seil-Linie des Karrens mit drei Punkten).</summary>
	internal static void Rope(Dummy cart, Vector3 to)
	{
		if (cart.Rope == null || cart.Line0 == null || cart.Line1 == null)
		{
			return;
		}
		cart.Rope.enabled = true;
		cart.Rope.positionCount = 3;
		cart.Rope.SetPosition(0, cart.Line0.position);
		cart.Rope.SetPosition(1, to);
		cart.Rope.SetPosition(2, cart.Line1.position);
	}

	private static GameObject? strikePrefab;
	private static bool strikeSearched;

	/// <summary>
	/// Der Vernichter-Blitz mit seinem Schaden (Gorduan: Schaden wie beim Vernichter). Ist er ein Netzobjekt, setzt ihn nur der
	/// Spieler, der gepfiffen hat (alle sehen ihn über das Netz). Sonst setzt ihn jeder Spieler bei sich: Valheims Aoe ohne
	/// ZNetView trifft dann nur Figuren, die diesem Spieler gehören, also jeden genau einmal.
	/// </summary>
	internal static void Strike(Vector3 position, bool mine)
	{
		if (!strikeSearched)
		{
			strikeSearched = true;
			// der Vernichter heißt im Spiel „incinerator“; sein Blitz ist m_lightingAOEs
			Incinerator? incinerator = (Prefab("incinerator") ?? Prefab("piece_incinerator"))?.GetComponent<Incinerator>();
			strikePrefab = incinerator != null && incinerator.m_lightingAOEs != null ? incinerator.m_lightingAOEs : Prefab("lightningAOE");
			ServerPlugin.Log.LogInfo(strikePrefab == null ? "Walküren-Transport: Vernichter-Blitz nicht gefunden."
				: $"Walküren-Transport: Vernichter-Blitz „{strikePrefab.name}“ ({(strikePrefab.GetComponent<ZNetView>() != null ? "Netzobjekt" : "örtlich")}).");
		}
		if (strikePrefab == null)
		{
			return;
		}
		bool networked = strikePrefab.GetComponent<ZNetView>() != null;
		if (networked && !mine)
		{
			return;
		}
		UnityEngine.Object.Instantiate(strikePrefab, position, Quaternion.identity);
	}

	/// <summary>Ein Blitz nur zum Ansehen (ohne Schaden): Valheims Blitz-Effekt, dazu Donner.</summary>
	internal static void Bolt(Vector3 ground, bool thunder = true, float scale = 1f)
	{
		Local("fx_Lightning", ground, Quaternion.identity, scale);
		if (thunder)
		{
			Local("sfx_thunder", ground, Quaternion.identity);
		}
	}

	internal static void Shake(Vector3 position, float strength, float range = 60f)
	{
		if (GameCamera.instance != null)
		{
			GameCamera.instance.AddShake(position, range, strength, false);
		}
	}

	/// <summary>Blaues Feuer auf dem Karren (Valheims „blau brennend“), nach seconds erlischt es langsam.</summary>
	internal static IEnumerator BlueFire(GameObject? target, float seconds, float scale = 2.2f)
	{
		if (target == null)
		{
			yield break;
		}
		List<GameObject> fires = new();
		foreach (Vector3 offset in new[] { new Vector3(0f, 0.6f, 0.2f), new Vector3(0.45f, 0.7f, -0.5f), new Vector3(-0.45f, 0.7f, -0.5f) })
		{
			GameObject? fire = Local("vfx_Burning_blue", target.transform.TransformPoint(offset), target.transform.rotation, scale, target.transform, lifetime: 0f);
			if (fire != null)
			{
				fires.Add(fire);
			}
		}
		GameObject glow = new("GC_CourierGlow");
		glow.transform.SetParent(target.transform, false);
		glow.transform.localPosition = new Vector3(0f, 1.2f, 0f);
		Light light = glow.AddComponent<Light>();
		light.type = LightType.Point;
		light.color = new Color(0.35f, 0.55f, 1f);
		light.range = 14f;
		float end = Time.time + seconds;
		while (Time.time < end && glow != null)
		{
			light.intensity = 2.5f + Mathf.PerlinNoise(Time.time * 7f, 0.3f) * 2f;
			yield return null;
		}
		foreach (GameObject fire in fires)
		{
			if (fire == null)
			{
				continue;
			}
			foreach (ParticleSystem particles in fire.GetComponentsInChildren<ParticleSystem>())
			{
				particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
			}
			UnityEngine.Object.Destroy(fire, 2.5f);
		}
		float fade = 0f;
		while (fade < 1.5f && glow != null)
		{
			fade += Time.deltaTime;
			light.intensity = Mathf.Lerp(2.5f, 0f, fade / 1.5f);
			yield return null;
		}
		if (glow != null)
		{
			UnityEngine.Object.Destroy(glow);
		}
		if (target != null)
		{
			Local("vfx_Smoked", target.transform.position + Vector3.up, Quaternion.identity, 1f, null, 8f); // Rauch beim Erlöschen
		}
	}

	// =================================================================== Abholen (Event 1)

	internal static void OnCall(long sender, ZPackage pkg)
	{
		if (Gc.IsHeadless || !FromServer(sender))
		{
			return;
		}
		long playerId = pkg.ReadLong();
		long whistler = pkg.ReadLong();
		Vector3 position = pkg.ReadVector3();
		Quaternion rotation = pkg.ReadQuaternion();
		float fill = pkg.ReadSingle();
		bool easter = pkg.ReadBool();
		int waiting = pkg.ReadInt();
		bool mine = Mine(whistler);
		if (!mine && !Near(position))
		{
			return;
		}
		if (!easter)
		{
			ValkyriePrefab(); // jetzt nachladen (im Gewitter fällt das kurze Stocken nicht auf), nicht erst beim Anflug
		}
		Dummy? cart = Cart(position, rotation, fill);
		if (cart != null)
		{
			if (Waiting.TryGetValue(playerId, out Dummy old) && old.Root != null)
			{
				UnityEngine.Object.Destroy(old.Root);
			}
			if (easter)
			{
				cart.Lifetime = 3600f; // wartet den ganzen Wächter-Kampf
			}
			Waiting[playerId] = cart;
		}
		Runner().StartCoroutine(easter ? Easter.Call(position, cart, mine, playerId) : NormalCall(position, cart, mine, waiting));
	}

	/// <summary>Normaler Ruf: 5 s dunkel und Gewitter, nach 3 s der Vernichter-Blitz in den Karren, blaues Feuer, dann das alte Wetter.</summary>
	private static IEnumerator NormalCall(Vector3 position, Dummy? cart, bool mine, int waiting)
	{
		Sky(StormEnv, NormalCallSeconds - 0.5f, 1.0f);
		Local("sfx_distant thunder", position + Vector3.up * 40f, Quaternion.identity);
		Shake(position, 0.6f);
		yield return new WaitForSeconds(StrikeDelaySeconds);
		Strike(position, mine);
		Bolt(position, thunder: false, scale: 1.4f);
		Local(Sounds.ValheimThunder, position + Vector3.up * 3f, Quaternion.identity);
		Local(Sounds.ValheimThunderMist, position + Vector3.up * 10f, Quaternion.identity);
		Shake(position, 3f);
		if (cart != null)
		{
			Runner().StartCoroutine(BlueFire(cart.Root, 3.2f));
		}
		yield return new WaitForSeconds(Mathf.Max(0f, NormalCallSeconds - StrikeDelaySeconds)); // der Ruf dauert weiter 5,5 s, danach holt der Server den Karren
		Local("sfx_thunder", position + new Vector3(30f, 50f, -20f), Quaternion.identity);
		if (mine && waiting > 0)
		{
			Gc.Center(GcLoc.Get("gccr_queued", waiting));
		}
	}

	internal static void OnPickup(long sender, ZPackage pkg)
	{
		if (Gc.IsHeadless || !FromServer(sender))
		{
			return;
		}
		long playerId = pkg.ReadLong();
		Vector3 position = pkg.ReadVector3();
		Quaternion rotation = pkg.ReadQuaternion();
		float fill = pkg.ReadSingle();
		bool easter = pkg.ReadBool();
		float delay = pkg.ReadSingle();
		if (!Near(position, Transport.ShowRange + 60f))
		{
			if (Waiting.TryGetValue(playerId, out Dummy far) && far.Root != null)
			{
				UnityEngine.Object.Destroy(far.Root);
			}
			Waiting.Remove(playerId);
			return;
		}
		Runner().StartCoroutine(Pickup(playerId, position, rotation, fill, easter, delay));
	}

	/// <summary>Die Walküre (Easteregg: die Möwe) kommt von weit oben, packt das Seil und fliegt mit dem Karren davon.</summary>
	private static IEnumerator Pickup(long playerId, Vector3 position, Quaternion rotation, float fill, bool easter, float delay)
	{
		if (delay > 0f)
		{
			yield return new WaitForSeconds(delay);
		}
		if (!Waiting.TryGetValue(playerId, out Dummy cart) || cart.Root == null)
		{
			Dummy? fresh = Cart(position, rotation, fill);
			if (fresh == null)
			{
				yield break;
			}
			cart = fresh;
		}
		Waiting.Remove(playerId);
		Carrier? carrier = easter ? Carrier.Seagull(position) : Carrier.Valkyrie(position);
		if (carrier == null)
		{
			// ohne Träger (Modell fehlt): der Karren steigt einfach in den Himmel
			yield return Rise(cart);
			yield break;
		}
		if (easter)
		{
			Local(Sounds.ValheimGull, carrier.Root.transform.position, Quaternion.identity, 1f, carrier.Root.transform);
			Music(Sounds.MusicEaster, PickupSeconds(true) + 2f);
		}
		else
		{
			Sounds.Play(Sounds.ValkyrieScreech, position + Vector3.up * 60f, 1f, 600f, global: true);
			Local("sfx_fallenvalkyrie_screech", carrier.Root.transform.position, Quaternion.identity, 1f, carrier.Root.transform);
			Music(Sounds.MusicValkyrie, PickupSeconds(false) + 2f);
		}
		Vector3 away = (carrier.Root.transform.position - position);
		away.y = 0f;
		away = away.sqrMagnitude > 1f ? away.normalized : Vector3.forward;
		Vector3 side = Vector3.Cross(Vector3.up, away);
		// Gorduan, 24.10.2026: bis auf 0,5 m an den Griff heran; HoverHeight ist der ungefähre Abstand Körper–Hand des Modells
		Vector3 grip = (cart.Attach != null ? cart.Attach.position : position + Vector3.up * 0.8f) + Vector3.up * GrabGap;
		Vector3 hover = grip + Vector3.up * carrier.HoverHeight;
		Vector3 start = carrier.Root.transform.position;
		Vector3 control = (start + hover) * 0.5f + Vector3.up * 40f + side * 70f;
		float t = 0f;
		// Anflug: Bogen von weit oben, am Ende langsamer
		while (t < ApproachSeconds && carrier.Root != null)
		{
			t += Time.deltaTime;
			float k = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / ApproachSeconds), 2f);
			Vector3 point = Bezier(start, control, hover, k);
			Vector3 ahead = Bezier(start, control, hover, Mathf.Min(1f, k + 0.02f));
			float clearance = Mathf.Clamp(Utils.DistanceXZ(point, hover) - 25f, 0f, 14f); // über Berge hinweg, am Karren tief
			carrier.Fly(point, ahead - point, Time.deltaTime, clearance);
			yield return null;
		}
		if (carrier.Root == null)
		{
			yield break;
		}
		if (!easter)
		{
			Local("sfx_fallenvalkyrie_screech", carrier.Root.transform.position, Quaternion.identity, 1f, carrier.Root.transform);
		}
		else
		{
			Local("sfx_seagull_idle", carrier.Root.transform.position, Quaternion.identity, 1f, carrier.Root.transform);
		}
		// Packen: dicht über dem Karren schweben, das Seil erscheint. Die Hand sitzt genau GrabGap über dem Griff: Der Versatz
		// des Modells wird gemessen und in der ersten halben Sekunde sanft ausgeglichen. Leichtes Wippen, ohne in den Karren zu tauchen.
		t = 0f;
		Vector3 facing = -away;
		while (t < GrabSeconds && carrier.Root != null)
		{
			t += Time.deltaTime;
			Vector3 bob = Vector3.up * Mathf.Sin(t * 2.4f) * 0.15f;
			carrier.Fly(hover + bob, facing, Time.deltaTime);
			carrier.Root.transform.position += (grip + bob - carrier.Hand) * Mathf.Clamp01(t / 0.5f);
			Rope(cart, carrier.Hand);
			yield return null;
		}
		// Anheben: gerade nach oben, bis das Seil gespannt ist (eine Seillänge über dem Griff); der Karren steht so lange
		Vector3 low = carrier.Root != null ? carrier.Root.transform.position : hover;
		float rise = Mathf.Max(0f, RopeLength - GrabGap);
		t = 0f;
		while (t < LiftSeconds && carrier.Root != null)
		{
			t += Time.deltaTime;
			carrier.Fly(low + Vector3.up * (rise * Mathf.SmoothStep(0f, 1f, t / LiftSeconds)), facing, Time.deltaTime);
			Rope(cart, carrier.Hand);
			yield return null;
		}
		// Abflug: nach oben und weg, der Karren hängt am Seil und pendelt
		Vector3 from = carrier.Root != null ? carrier.Root.transform.position : hover;
		Vector3 end = position - away * 380f + Vector3.up * 230f;
		Vector3 control2 = from + facing * 70f + Vector3.up * 25f;
		t = 0f;
		float pitch = 0f;
		while (t < DepartSeconds && carrier.Root != null && cart.Root != null)
		{
			t += Time.deltaTime;
			float k = Mathf.Pow(Mathf.Clamp01(t / DepartSeconds), 1.6f);
			Vector3 point = Bezier(from, control2, end, k);
			Vector3 ahead = Bezier(from, control2, end, Mathf.Min(1f, k + 0.02f));
			carrier.Fly(point, ahead - point, Time.deltaTime, (RopeLength + 6f) * Mathf.Clamp01(t / 2.5f)); // der Karren hängt darunter
			pitch = Mathf.MoveTowards(pitch, 50f, 40f * Time.deltaTime);
			Hang(cart, carrier.Hand, ahead - point, pitch, t, Mathf.Clamp01(t / 1f)); // in der ersten Sekunde weich vom Boden ans Seil
			Rope(cart, carrier.Hand);
			yield return null;
		}
		if (carrier.Root != null)
		{
			UnityEngine.Object.Destroy(carrier.Root);
		}
		if (cart.Root != null)
		{
			UnityEngine.Object.Destroy(cart.Root);
		}
	}

	/// <summary>Ohne Träger: der Karren steigt mit blauem Leuchten in den Himmel und verschwindet.</summary>
	private static IEnumerator Rise(Dummy cart)
	{
		float t = 0f;
		Vector3 from = cart.Root.transform.position;
		while (t < 8f && cart.Root != null)
		{
			t += Time.deltaTime;
			cart.Root.transform.position = from + Vector3.up * (t * t * 4f);
			yield return null;
		}
		if (cart.Root != null)
		{
			UnityEngine.Object.Destroy(cart.Root);
		}
	}

	/// <summary>Der Karren hängt am Seil unter dem Träger: vorn (Zugstange) oben, er pendelt etwas.</summary>
	/// <summary>Karren am Seil unter der Hand. blend &lt; 1: noch auf dem Weg vom Boden dorthin (Drehung und Lage gleiten mit).</summary>
	private static void Hang(Dummy cart, Vector3 hand, Vector3 direction, float pitch, float time, float blend = 1f)
	{
		direction.y = 0f;
		if (direction.sqrMagnitude < 0.0001f)
		{
			direction = cart.Root.transform.forward;
		}
		float swing = Mathf.Sin(time * 1.7f) * 6f;
		Quaternion rotation = Quaternion.LookRotation(direction.normalized) * Quaternion.Euler(-pitch, 0f, swing);
		if (blend < 1f)
		{
			rotation = Quaternion.Slerp(cart.Root.transform.rotation, rotation, blend);
		}
		cart.Root.transform.rotation = rotation;
		Vector3 anchor = cart.Attach != null ? cart.Attach.position - cart.Root.transform.position : rotation * new Vector3(0f, 0.8f, 1.6f);
		Vector3 target = hand + Vector3.down * RopeLength - anchor;
		cart.Root.transform.position = blend < 1f ? Vector3.Lerp(cart.Root.transform.position, target, blend) : target;
	}

	private static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, float t)
	{
		float u = 1f - t;
		return u * u * a + 2f * u * t * b + t * t * c;
	}

	// =================================================================== Abliefern (Event 2)

	internal static void SetCargo(long playerId, ZDOID record, ZPackage cargo)
	{
		Cargo[playerId] = (record, cargo);
	}

	internal static void OnDrop(long sender, ZPackage pkg)
	{
		if (Gc.IsHeadless || !FromServer(sender))
		{
			return;
		}
		long playerId = pkg.ReadLong();
		long whistler = pkg.ReadLong();
		Vector3 target = pkg.ReadVector3();
		float fill = pkg.ReadSingle();
		bool mine = Mine(whistler);
		if (mine)
		{
			// Review 27.10.2026: nur die Fracht dieser Lieferung abwerfen. Der Server schickt sie gleich nach dieser Nachricht
			// (RpcCargo); eine ältere, die zu spät für ein gescheitertes Abwerfen kam, darf hier nicht mehr fallen.
			Cargo.Remove(playerId);
		}
		if (!mine && !Near(target))
		{
			return;
		}
		Runner().StartCoroutine(Drop(playerId, target, fill, mine));
	}

	/// <summary>
	/// Der Schrei, das Warnzeichen am Boden, dann fällt der Karren aus großer Höhe und zerschellt. Der Blitz an der Stelle kommt
	/// erst 3 s nach dem Pfiff, also während der Karren fällt (Gorduan, 24.10.2026). Die Pfeife bleibt heil (Gorduan, 25.10.2026).
	/// </summary>
	private static IEnumerator Drop(long playerId, Vector3 target, float fill, bool mine)
	{
		Runner().StartCoroutine(DropStrike(target, mine));
		yield return new WaitForSeconds(1.0f);
		Sounds.Play(Sounds.ValkyrieScreech, target + Vector3.up * 90f, 0.9f, 600f, global: true);
		Local("sfx_fallenvalkyrie_screech", target + Vector3.up * 60f, Quaternion.identity);
		yield return new WaitForSeconds(0.8f);
		const float height = 140f;
		const float gravity = 30f;
		Quaternion spin = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
		Dummy? cart = Cart(target + Vector3.up * height, spin, fill);
		// Warnung: Der Aufprall trifft hart (Gorduan), Valheims Vorzeichen eines Erscheinens markiert die Stelle
		Local("vfx_prespawn", target, Quaternion.identity, 2f, null, 5f);
		if (mine)
		{
			Gc.Center(GcLoc.Get("gccr_drop_warning"));
		}
		float ground = target.y;
		// Boden unter den Füßen suchen (2 m darüber anfangen): im Haus der Fußboden, nicht das Dach (Prüfung 19.10.)
		if (ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(target, out float solid, 2))
		{
			ground = Mathf.Max(ground - 0.5f, solid);
		}
		float t = 0f;
		float tumble = UnityEngine.Random.Range(-40f, 40f);
		while (cart != null && cart.Root != null)
		{
			t += Time.deltaTime;
			float y = target.y + height - 0.5f * gravity * t * t;
			if (y <= ground)
			{
				break;
			}
			cart.Root.transform.position = new Vector3(target.x, y, target.z);
			cart.Root.transform.rotation = spin * Quaternion.Euler(Mathf.Sin(t * 1.3f) * 12f, tumble * t, Mathf.Cos(t * 1.1f) * 10f);
			yield return null;
		}
		Vector3 impact = new(target.x, ground, target.z);
		Shatter(cart, impact);
		Sounds.Play(Sounds.BoomBig, impact, 0.7f, 220f);
		Local("sfx_ship_destroyed", impact, Quaternion.identity);
		Shake(impact, 4f);
		if (mine)
		{
			float wait = 0f;
			while (!Cargo.ContainsKey(playerId) && wait < 15f)
			{
				wait += Time.deltaTime;
				yield return null;
			}
			if (!Cargo.TryGetValue(playerId, out (ZDOID record, ZPackage cargo) delivery))
			{
				Transport.DeliveryFailed(playerId); // nichts angekommen: die Fracht bleibt in der Luft
				yield break;
			}
			Cargo.Remove(playerId);
			// Review 27.10.2026 (Verdopplung): Gegenstände erzeugen und im selben Bild melden, vor allem anderen. Vorher kam die
			// Meldung 1,5 s später: Wer dazwischen auslog, schickte die Gegenstände trotzdem (Valheim sendet beim Ausloggen alle
			// eigenen ZDOs, ZDOMan.FlushClientObjects), die Meldung nie, und nach dem nächsten Login fiel dieselbe Fracht noch
			// einmal. Starb man am Aufprall, lehnte der Server die späte Meldung ab (Spieler-ZDO fehlt), mit derselben Folge.
			// Jetzt geht die Meldung vor den neuen ZDOs in die Leitung (Transport.RPC_Delivered).
			if (!Unload(delivery.cargo, impact))
			{
				Transport.DeliveryFailed(playerId); // nichts abgeworfen: die Fracht bleibt in der Luft
				yield break;
			}
			Transport.ConfirmDelivered(playerId, delivery.record);
			try
			{
				Impact(impact, CartWeight(delivery.cargo)); // trifft nur Figuren, nicht die Gegenstände am Boden
			}
			catch (Exception e)
			{
				ServerPlugin.Log.LogWarning($"Walküren-Transport: Aufprall-Schaden nicht verteilt: {e.Message}");
			}
		}
	}

	/// <summary>Abliefern: der Vernichter-Blitz erst StrikeDelaySeconds nach dem Pfiff (Gorduan, 24.10.2026).</summary>
	private static IEnumerator DropStrike(Vector3 target, bool mine)
	{
		yield return new WaitForSeconds(StrikeDelaySeconds);
		Strike(target, mine);
		Bolt(target, thunder: false);
		Local(Sounds.ValheimThunder, target + Vector3.up * 3f, Quaternion.identity);
		Shake(target, 2f);
	}

	/// <summary>Gewicht des Karrens wie in Valheim (Vagon.UpdateMass): Grundgewicht plus Gewicht der Ladung.</summary>
	private static float CartWeight(ZPackage cargo)
	{
		cargo.SetPos(0);
		int width = cargo.ReadInt();
		int height = cargo.ReadInt();
		cargo.ReadSingle();
		byte[] data = cargo.ReadByteArray();
		Inventory inventory = new("GC_CourierWeight", null, Mathf.Max(1, width), Mathf.Max(1, height));
		inventory.Load(new ZPackage(data));
		Vagon? vagon = Prefab(Transport.CartPrefab)?.GetComponent<Vagon>();
		float baseMass = vagon != null ? vagon.m_baseMass : 20f;
		float factor = vagon != null ? vagon.m_itemWeightMassFactor : 1f;
		return baseMass + inventory.GetTotalWeight() * factor;
	}

	/// <summary>Fläche, Wucht und Taumeln wie Valheims Vorschlaghammer (Eisen), sonst Ersatzwerte.</summary>
	private static (float radius, float push, float stagger) SledgeArea()
	{
		ItemDrop? sledge = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("SledgeIron")?.GetComponent<ItemDrop>() : null;
		Attack? attack = sledge != null ? sledge.m_itemData.m_shared.m_attack : null;
		if (sledge != null && attack != null && attack.m_attackRayWidth > 0.5f)
		{
			return (attack.m_attackRayWidth + attack.m_attackRayWidthCharExtra, sledge.m_itemData.m_shared.m_attackForce, attack.m_staggerMultiplier);
		}
		return (4f, 100f, 1f);
	}

	/// <summary>
	/// Aufprall des Karrens (Gorduan, 19.10.2026): Stumpfschaden so hoch wie das Gewicht des Karrens, als Fläche wie beim
	/// Vorschlaghammer, für Spieler und Gegner. Valheims eigene Schadensrechnung beim Getroffenen: Rüstung, Blocken (zum
	/// Aufprall gewandt), Ausweichen, Resistenzen senken ihn, Anfälligkeiten erhöhen ihn. Zahme Tiere und Gebäude bleiben
	/// verschont. Nur beim abliefernden Spieler (sonst träfe es jeden mehrfach).
	/// </summary>
	private static void Impact(Vector3 impact, float weight)
	{
		(float radius, float push, float stagger) = SledgeArea();
		int hits = 0;
		foreach (Character character in new List<Character>(Character.GetAllCharacters()))
		{
			if (character == null || character.IsDead() || character.IsTamed())
			{
				continue;
			}
			Vector3 offset = character.GetCenterPoint() - impact;
			if (offset.magnitude > radius + character.GetRadius())
			{
				continue;
			}
			Vector3 dir = new(offset.x, 0f, offset.z);
			dir = dir.sqrMagnitude > 0.01f ? dir.normalized : -character.transform.forward;
			HitData hit = new();
			hit.m_damage.m_blunt = weight;
			hit.m_point = character.GetCenterPoint();
			hit.m_dir = dir;
			hit.m_pushForce = push;
			hit.m_staggerMultiplier = stagger;
			hit.m_blockable = true;
			hit.m_dodgeable = true;
			hit.m_hitType = HitData.HitType.Cart;
			character.Damage(hit);
			hits++;
		}
		ServerPlugin.Log.LogInfo($"Walküren-Transport: Aufprall {weight:0} Stumpfschaden im Umkreis von {radius:0.#} m, {hits} getroffen.");
	}

	/// <summary>Zerschellen wie ein zerstörter Karren in Valheim: seine Zerstörungs-Effekte und seine Bruchstücke fliegen auseinander.</summary>
	private static void Shatter(Dummy? cart, Vector3 impact)
	{
		if (cart == null || cart.Root == null)
		{
			return;
		}
		cart.Root.transform.position = impact;
		LocalEffects(cart.Destroyed, impact, cart.Root.transform.rotation);
		bool broke = false;
		foreach (GameObject fragment in cart.Fragments)
		{
			if (fragment == null)
			{
				continue;
			}
			fragment.SetActive(true);
			Destructible.CreateFragments(fragment, false);
			broke = true;
		}
		if (!broke)
		{
			Destructible.CreateFragments(cart.Root, false);
		}
		Local("vfx_RockDestroyed_large", impact, Quaternion.identity);
		UnityEngine.Object.Destroy(cart.Root);
	}

	/// <summary>Beim abliefernden Spieler: die Fracht fällt um den Aufschlag herum zu Boden (Valheims ItemDrop, wie aus einer zerstörten Kiste).</summary>
	/// <summary>
	/// Wirft die Fracht ab. false: nichts abgeworfen (Daten unlesbar), die Fracht bleibt dann in der Luft. Einzelne Stapel, die
	/// das Spiel nicht kennt (Mod entfernt), stehen im Log; sie gingen auch in einer Valheim-Kiste verloren.
	/// Review 27.10.2026: Sobald die Daten gelesen sind, gilt die Fracht als abgeworfen (true), auch wenn danach etwas
	/// schiefgeht, denn ein Teil kann schon am Boden liegen. Lieber ein Stapel verloren (der Server hebt den Datensatz 7 Tage
	/// für den Admin auf) als dieselbe Fracht ein zweites Mal.
	/// </summary>
	private static bool Unload(ZPackage cargo, Vector3 impact)
	{
		Inventory inventory;
		int stored;
		try
		{
			cargo.SetPos(0);
			int width = cargo.ReadInt();
			int height = cargo.ReadInt();
			cargo.ReadSingle();
			byte[] data = cargo.ReadByteArray();
			ZPackage header = new(data);
			header.ReadInt(); // Version
			stored = header.ReadUShort();
			inventory = new Inventory("GC_Courier", null, Mathf.Max(1, width), Mathf.Max(1, height));
			inventory.Load(new ZPackage(data));
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogError($"Walküren-Transport: Fracht nicht lesbar, sie bleibt in der Luft: {e}");
			return false;
		}
		int count = 0;
		try
		{
			foreach (ItemDrop.ItemData item in inventory.GetAllItems())
			{
				if (item?.m_dropPrefab == null)
				{
					continue;
				}
				try
				{
					Vector2 circle = UnityEngine.Random.insideUnitCircle * 1.6f;
					Vector3 spot = impact + new Vector3(circle.x, 0.8f, circle.y);
					ItemDrop drop = ItemDrop.DropItem(item, 0, spot, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
					Rigidbody? body = drop != null ? drop.GetComponent<Rigidbody>() : null;
					if (body != null)
					{
						body.linearVelocity = new Vector3(circle.x * 1.5f, UnityEngine.Random.Range(3f, 6f), circle.y * 1.5f);
					}
					count++;
				}
				catch (Exception e)
				{
					ServerPlugin.Log.LogError($"Walküren-Transport: {item.m_shared?.m_name} nicht abgeworfen: {e.Message}");
				}
			}
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogError($"Walküren-Transport: Abwerfen nach {count} Stapeln abgebrochen, der Rest fehlt (Datensatz beim Server): {e}");
		}
		if (count < stored)
		{
			ServerPlugin.Log.LogWarning($"Walküren-Transport: {stored - count} von {stored} Stapeln kennt dieses Spiel nicht (Mod fehlt?), sie fehlen.");
		}
		ServerPlugin.Log.LogInfo($"Walküren-Transport: {count} Stapel abgeworfen.");
		return true;
	}

	// =================================================================== Raid danach

	/// <summary>Easteregg: Die Wächter sind gefallen. Chor, „Die Walküre naht …“, Himmel klart auf; dann holt die Möwe den Karren.</summary>
	/// <summary>Wann das Finale je Spieler kam (Time.time): Danach ruft der Pfeifende keine Wächter mehr herbei (Prüfung 2).</summary>
	internal static readonly Dictionary<long, float> FinaleAt = new();

	internal static void OnFinale(long sender, ZPackage pkg)
	{
		if (Gc.IsHeadless || !FromServer(sender))
		{
			return;
		}
		long playerId = pkg.ReadLong();
		FinaleAt[playerId] = Time.time;
		Vector3 position = pkg.ReadVector3();
		EndGuardFight(position); // vor der Abstandsprüfung (Review 27.10.2026)
		if (!Near(position, Transport.ShowRange + 60f))
		{
			return;
		}
		ClearSky();
		Sounds.Play(Sounds.Choir, position + Vector3.up * 20f, 1f, 300f, global: true);
		MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, GcLoc.Get("gccr_egg_7"));
		Music(Sounds.MusicEaster, FinaleSeconds + PickupSeconds(true) + 2f);
	}

	/// <summary>
	/// Wetter und Musik des Wächter-Kampfs enden bei jedem Spieler, auch bei dem, der gerade weit weg ist (Review 27.10.2026):
	/// Easter.Call setzt sie für 30 Minuten nur am Ort (Wetter des Ältesten, Musik des Eastereggs); bisher kamen sie bei
	/// Spielern, die beim Finale fort waren, wieder, sobald sie zurückkamen. Nur diese beiden, erkannt am Ort des Kampfs.
	/// </summary>
	private static void EndGuardFight(Vector3 center)
	{
		if (env != null && envRange > 0f && Utils.DistanceXZ(envCenter, center) < 1f)
		{
			envUntil = 0f;
		}
		if (music != null && musicRange > 0f && Utils.DistanceXZ(musicCenter, center) < 1f)
		{
			musicUntil = 0f;
		}
	}

	internal static void OnRaid(long sender, Vector3 position, string name)
	{
		if (Gc.IsHeadless || !FromServer(sender))
		{
			return;
		}
		raidEvent = name;
		raidPosition = position;
		if (Near(position, Transport.ShowRange + 50f))
		{
			Local(Sounds.ValheimSummon, position, Quaternion.identity); // Valheims Boss-Ruf kündigt den Raid an
			Shake(position, 1.5f, 120f);
		}
	}

	// =================================================================== Träger

	/// <summary>Wer den Karren trägt: die Walküre aus dem Intro oder (Easteregg) eine Möwe, nur zum Ansehen.</summary>
	internal sealed class Carrier
	{
		public GameObject Root = null!;
		public Transform? HandPoint;
		public Animator? Animator;
		public float HoverHeight = 3f;
		public bool Bird;
		private float flapTimer;
		private float bank;

		public Vector3 Hand => HandPoint != null ? HandPoint.position : Root.transform.position + Vector3.down * 2f;

		/// <summary>Valheims Intro-Walküre (Player.m_valkyrie, nachgeladen), ohne ihre Intro-Logik.</summary>
		public static Carrier? Valkyrie(Vector3 cart)
		{
			GameObject? prefab = ValkyriePrefab();
			if (prefab == null)
			{
				return null;
			}
			Vector2 dir = UnityEngine.Random.insideUnitCircle.normalized;
			Vector3 start = cart + new Vector3(dir.x, 0f, dir.y) * 330f + Vector3.up * 170f;
			Carrier carrier = new() { HoverHeight = 4f };
			GameObject? root = Visual(prefab, start, Quaternion.LookRotation(cart - start), copy =>
			{
				global::Valkyrie? valkyrie = copy.GetComponent<global::Valkyrie>();
				if (valkyrie != null)
				{
					carrier.HandPoint = valkyrie.m_attachPoint;
				}
			});
			if (root == null)
			{
				return null;
			}
			carrier.Root = root;
			carrier.Animator = root.GetComponentInChildren<Animator>();
			if (carrier.Animator != null)
			{
				carrier.Animator.SetBool("dropped", false);
			}
			return carrier;
		}

		/// <summary>Valheims Möwe („Seagal“), dreieinhalbmal so groß, im Flug mit Flügelschlag.</summary>
		public static Carrier? Seagull(Vector3 cart)
		{
			GameObject? prefab = Prefab("Seagal");
			if (prefab == null)
			{
				return null;
			}
			Vector2 dir = UnityEngine.Random.insideUnitCircle.normalized;
			Vector3 start = cart + new Vector3(dir.x, 0f, dir.y) * 200f + Vector3.up * 90f;
			Carrier carrier = new() { HoverHeight = 1.5f, Bird = true };
			GameObject? root = Visual(prefab, start, Quaternion.LookRotation(cart - start), copy =>
			{
				RandomFlyingBird? bird = copy.GetComponent<RandomFlyingBird>();
				if (bird != null)
				{
					if (bird.m_landedModel != null)
					{
						bird.m_landedModel.SetActive(false);
					}
					if (bird.m_flyingModel != null)
					{
						bird.m_flyingModel.SetActive(true);
					}
				}
			});
			if (root == null)
			{
				return null;
			}
			root.transform.localScale *= 3.5f;
			carrier.Root = root;
			carrier.Animator = root.GetComponentInChildren<Animator>();
			if (carrier.Animator != null)
			{
				carrier.Animator.SetBool("flying", true);
				carrier.Animator.SetBool("flapping", true);
			}
			return carrier;
		}

		/// <summary>
		/// Fliegen: Position setzen (über Bergen mit Abstand zum Boden, clearance), in Flugrichtung drehen, in Kurven schräg legen;
		/// die Möwe schlägt abwechselnd mit den Flügeln.
		/// </summary>
		public void Fly(Vector3 position, Vector3 direction, float dt, float clearance = 0f)
		{
			if (Root == null)
			{
				return;
			}
			if (clearance > 0f && ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(position, out float ground))
			{
				position.y = Mathf.Max(position.y, ground + clearance);
			}
			Vector3 flat = direction;
			flat.y = 0f;
			if (flat.sqrMagnitude > 0.0001f)
			{
				Vector3 oldForward = Root.transform.forward;
				oldForward.y = 0f;
				float turn = oldForward.sqrMagnitude > 0.0001f ? Vector3.SignedAngle(oldForward, flat, Vector3.up) : 0f;
				bank = Mathf.Lerp(bank, Mathf.Clamp(-turn * 8f, -35f, 35f), dt * 2f);
				Quaternion look = Quaternion.LookRotation(direction.normalized.y > -0.9f ? direction.normalized : flat.normalized);
				Root.transform.rotation = Quaternion.Slerp(Root.transform.rotation, look * Quaternion.Euler(0f, 0f, bank), dt * 3f);
			}
			Root.transform.position = position;
			if (Bird && Animator != null)
			{
				flapTimer -= dt;
				if (flapTimer <= 0f)
				{
					flapTimer = UnityEngine.Random.Range(0.4f, 0.9f);
					Animator.SetBool("flapping", !Animator.GetBool("flapping") || direction.y > 0f);
				}
			}
		}
	}

	/// <summary>Die Intro-Walküre steckt in einem nachladbaren Paket (SoftReference): einmal laden und für die Sitzung behalten.</summary>
	private static GameObject? ValkyriePrefab()
	{
		if (valkyriePrefab != null)
		{
			return valkyriePrefab;
		}
		if (valkyrieLoaded)
		{
			return null;
		}
		Player? player = Player.m_localPlayer;
		if (player == null)
		{
			// Review 27.10.2026: ohne eigenen Spieler (tot, beim Laden) war das bisher schon „geladen“, und die Walküre fehlte
			// für die ganze Sitzung. Jetzt beim nächsten Mal noch einmal versuchen.
			return null;
		}
		valkyrieLoaded = true;
		try
		{
			player.m_valkyrie.Load();
			valkyriePrefab = player.m_valkyrie.Asset;
		}
		catch (Exception e)
		{
			ServerPlugin.Log.LogWarning($"Walküren-Transport: Walküre nicht geladen: {e.Message}");
		}
		if (valkyriePrefab == null)
		{
			ServerPlugin.Log.LogWarning("Walküren-Transport: Walküre nicht gefunden, der Karren steigt allein auf.");
		}
		return valkyriePrefab;
	}

	/// <summary>Karren, die zu lange auf eine Abholung warten (Server neu gestartet), verschwinden nach 10 Minuten.</summary>
	internal static void Tick()
	{
		if (Waiting.Count == 0)
		{
			return;
		}
		List<long>? old = null;
		foreach (KeyValuePair<long, Dummy> pair in Waiting)
		{
			if (pair.Value.Root == null || Time.time - pair.Value.Born > pair.Value.Lifetime)
			{
				(old ??= new List<long>()).Add(pair.Key);
			}
		}
		if (old == null)
		{
			return;
		}
		foreach (long key in old)
		{
			if (Waiting[key].Root != null)
			{
				Local("vfx_Smoked", Waiting[key].Root.transform.position + Vector3.up, Quaternion.identity);
				UnityEngine.Object.Destroy(Waiting[key].Root);
			}
			Waiting.Remove(key);
		}
	}
}

/// <summary>Wetter der Effekte: EnvMan fragt hier zuerst (Valheims weicher Wechsel, danach kommt das alte Wetter zurück).</summary>
[HarmonyPatch(typeof(EnvMan), "GetEnvironmentOverride")]
internal static class CourierEnvPatch
{
	private static void Postfix(ref string __result)
	{
		string? env = Show.CurrentEnv();
		if (env != null && (Player.m_localPlayer == null || !Player.m_localPlayer.InIntro()))
		{
			__result = env;
		}
	}
}

/// <summary>Musik der Effekte (vor jeder anderen Musik, wie die eines Raids).</summary>
[HarmonyPatch(typeof(RandEventSystem), nameof(RandEventSystem.GetMusicOverride))]
internal static class CourierMusicPatch
{
	private static void Postfix(ref string __result)
	{
		string? music = Show.CurrentMusic();
		if (music != null)
		{
			__result = music;
		}
	}
}
