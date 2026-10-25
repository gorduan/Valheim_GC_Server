using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GC.Server;
using GC.Shared;
using HarmonyLib;

namespace GC.Server.PortalNames;

public enum Editors
{
	Admins,
	Everyone
}

/// <summary>
/// Server-Liste der Portalnamen (neu in GC_Server 1.1.0). Die Liste liegt auf dem Server, eine Datei pro Welt
/// (BepInEx/config/GC_Server/portalnames_&lt;Welt&gt;.txt). GC_Client zeigt sie im Portal-Dialog neben der eigenen Liste.
/// Ablauf: Beim Öffnen des Dialogs fragt der Client die Liste an, der Server schickt sie. Änderungen prüft der Server
/// (Standard: nur Admins) und schickt die neue Liste an alle. Ohne GC_Client passiert sichtbar nichts.
/// </summary>
internal static class Plugin
{
	internal const string RpcRequest = "GC_PortalNames_Request";
	internal const string RpcEdit = "GC_PortalNames_Edit";
	internal const string RpcList = "GC_PortalNames_List";

	internal static ManualLogSource Log = null!;
	internal static ConfigEntry<bool> Enabled = null!;
	internal static ConfigEntry<Editors> EditBy = null!;
	internal static bool On => Enabled.Value && ServerPlugin.OnServer;

	// Server
	private static readonly List<string> Stored = new();
	private static string? loadedFile;

	// Client
	private static List<string> received = new();
	private static bool haveList;
	private static int version;

	internal static void Init(ConfigFile Config, Harmony harmony)
	{
		Log = ServerPlugin.Log;
		Enabled = Gc.BindSynced(Config, "6 - Portal Names", "Enabled", true, "Gemeinsame Server-Liste der Portalnamen für den Portal-Dialog von GC_Client (wird vom Server vorgegeben).");
		EditBy = Gc.BindSynced(Config, "6 - Portal Names", "EditBy", Editors.Admins, "Wer die Server-Liste bearbeiten darf: Admins oder Everyone (alle Spieler).");
		GcPortalNames.Register("server", 10, () => GcLoc.Get("gcsv_pn_title"), () => On && haveList, () => new List<string>(received), CanEditLocal,
			name => Send(true, name), name => Send(false, name), Request, () => version);
	}

	private static bool CanEditLocal()
	{
		return On && ZNet.instance != null && (EditBy.Value == Editors.Everyone || GcMenu.CanChangeServer); // Jötunns Admin-Status, wie der Server ihn prüft
	}

	// ------------------------------------------------------------------ Client

	private static void Request()
	{
		if (On && ZRoutedRpc.instance != null && ZNet.instance != null)
		{
			ZRoutedRpc.instance.InvokeRoutedRPC(RpcRequest); // ohne Ziel: an den Server
		}
	}

	private static void Send(bool add, string name)
	{
		if (CanEditLocal() && ZRoutedRpc.instance != null)
		{
			ZRoutedRpc.instance.InvokeRoutedRPC(RpcEdit, add, GcPortalNames.Clean(name));
		}
	}

	/// <summary>Neue Spielsitzung: alte Liste eines anderen Servers vergessen.</summary>
	internal static void ResetClient()
	{
		received = new List<string>();
		haveList = false;
		version++;
	}

	internal static void OnList(long sender, ZPackage pkg)
	{
		int count = pkg.ReadInt();
		List<string> names = new(count);
		for (int i = 0; i < count; i++)
		{
			names.Add(pkg.ReadString());
		}
		received = names;
		haveList = true;
		version++;
	}

	// ------------------------------------------------------------------ Server

	internal static void OnRequest(long sender)
	{
		if (ZNet.instance == null || !ZNet.instance.IsServer() || !On)
		{
			return;
		}
		EnsureLoaded();
		SendList(sender);
	}

	internal static void OnEdit(long sender, bool add, string name)
	{
		if (ZNet.instance == null || !ZNet.instance.IsServer() || !On)
		{
			return;
		}
		if (!MayEdit(sender))
		{
			Log.LogWarning($"Portalnamen: Änderung von {sender} abgelehnt (keine Rechte).");
			return;
		}
		EnsureLoaded();
		string clean = GcPortalNames.Clean(name);
		bool changed = add ? !Stored.Contains(clean) : Stored.Contains(clean);
		if (!changed)
		{
			SendList(sender);
			return;
		}
		if (add && Stored.Count >= GcPortalNames.MaxNames)
		{
			SendList(sender);
			return;
		}
		if (add)
		{
			Stored.Add(clean);
		}
		else
		{
			Stored.Remove(clean);
		}
		List<string> sorted = GcPortalNames.Normalize(Stored);
		Stored.Clear();
		Stored.AddRange(sorted);
		Save();
		Log.LogInfo($"Portalnamen: '{clean}' {(add ? "hinzugefügt" : "entfernt")} von {sender}.");
		SendList(ZRoutedRpc.Everybody);
	}

	private static bool MayEdit(long sender)
	{
		if (EditBy.Value == Editors.Everyone || (sender == ZNet.GetUID() && !ZNet.instance.IsDedicated()))
		{
			return true; // alle oder der Host selbst (nur Listen-Server; ein dedizierter Server spielt nicht mit)
		}
		ZNetPeer? peer = ZNet.instance.GetPeer(sender);
		return peer != null && peer.m_socket != null && ZNet.instance.IsAdmin(peer.m_socket.GetHostName());
	}

	private static void SendList(long target)
	{
		ZPackage pkg = new();
		pkg.Write(Stored.Count);
		foreach (string name in Stored)
		{
			pkg.Write(name);
		}
		ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcList, pkg);
	}

	private static string FileName()
	{
		string world = ZNet.World != null && !string.IsNullOrEmpty(ZNet.World.m_name) ? ZNet.World.m_name : "world";
		foreach (char c in Path.GetInvalidFileNameChars())
		{
			world = world.Replace(c, '_');
		}
		return Path.Combine(Path.Combine(Paths.ConfigPath, "GC_Server"), $"portalnames_{world}.txt");
	}

	private static void EnsureLoaded()
	{
		string file = FileName();
		if (loadedFile == file)
		{
			return;
		}
		loadedFile = file;
		Stored.Clear();
		try
		{
			if (File.Exists(file))
			{
				Stored.AddRange(GcPortalNames.Normalize(File.ReadAllLines(file, Encoding.UTF8)));
			}
		}
		catch (Exception e)
		{
			Log.LogWarning($"Portalnamen konnten nicht gelesen werden ({file}): {e.Message}");
		}
	}

	private static void Save()
	{
		try
		{
			string file = FileName();
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			File.WriteAllLines(file, Stored, Encoding.UTF8);
		}
		catch (Exception e)
		{
			Log.LogWarning($"Portalnamen konnten nicht gespeichert werden: {e.Message}");
		}
	}
}

[HarmonyPatch(typeof(Game), nameof(Game.Start))]
internal static class GameStartPatch
{
	private static void Postfix()
	{
		Plugin.ResetClient();
		ZRoutedRpc.instance.Register(Plugin.RpcRequest, Plugin.OnRequest);
		ZRoutedRpc.instance.Register<bool, string>(Plugin.RpcEdit, Plugin.OnEdit);
		ZRoutedRpc.instance.Register<ZPackage>(Plugin.RpcList, Plugin.OnList);
	}
}
