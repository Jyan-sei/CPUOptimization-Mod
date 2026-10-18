using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// soft ref to together, then old krokmp. no compile time dependancy.
/// clients stream their own window. NetHold keeps a Together replica and destroys a local gen object.
/// </summary>
internal static class MpSession
{
	private static bool _resolved;
	private static Type _netType;
	private static Type _netPlayerType;
	private static PropertyInfo _runningProp;
	private static FieldInfo _runningField;
	private static PropertyInfo _isServerProp;
	private static FieldInfo _isServerField;
	private static FieldInfo _allLivingPlayersField;
	private static FieldInfo _allDeadPlayersField;
	private static PropertyInfo _posProp;
	private static PropertyInfo _cameraPosProp;
	private static FieldInfo _bodyField;

	internal static bool Running
	{
		get
		{
			Resolve();
			if (_netType == null)
				return false;
			if (_runningProp != null && _runningProp.GetValue(null, null) is bool on)
				return on;
			return _runningField?.GetValue(null) is bool flagged && flagged;
		}
	}

	internal static bool IsServer
	{
		get
		{
			Resolve();
			if (_netType == null)
				return false;
			if (_isServerProp != null && _isServerProp.GetValue(null, null) is bool on)
				return on;
			return _isServerField?.GetValue(null) is bool flagged && flagged;
		}
	}

	/// <summary>sp, host, and client. a client streams the local window only.</summary>
	internal static bool AllowStream => true;

	/// <summary>
	/// living player anchors for the union windows. camerapos, then body, then pos.
	/// clears dest, writes what it found, returns how many.
	/// </summary>
	internal static int CollectLivingPlayerAnchors(List<Vector3> dest)
	{
		dest.Clear();
		Resolve();
		if (!Running || _allLivingPlayersField == null)
			return 0;

		object listObj;
		try
		{
			listObj = _allLivingPlayersField.GetValue(null);
		}
		catch
		{
			return 0;
		}

		if (listObj is not IList list || list.Count == 0)
			return 0;

		for (int i = 0; i < list.Count; i++)
		{
			object player = list[i];
			if (player == null)
				continue;
			if (!TryReadAnchor(player, out Vector3 anchor))
				continue;
			dest.Add(anchor);
		}

		return dest.Count;
	}

	/// <summary>
	/// dead player bodies only. local body, plus the mp dead list.
	/// no scene search. a respawn or disconnect drops them off this list.
	/// </summary>
	internal static void CollectDeadPlayerBodies(List<Body> dest)
	{
		dest.Clear();
		Body local = PlayerCamera.main != null ? PlayerCamera.main.body : null;
		if (local != null && !local.alive)
			dest.Add(local);

		Resolve();
		if (!Running || _allDeadPlayersField == null || _bodyField == null)
			return;

		object listObj;
		try
		{
			listObj = _allDeadPlayersField.GetValue(null);
		}
		catch
		{
			return;
		}

		if (listObj is not IList list)
			return;

		for (int i = 0; i < list.Count; i++)
		{
			object player = list[i];
			if (player == null)
				continue;
			try
			{
				if (_bodyField.GetValue(player) is Body body && body != null && !body.alive && body != local)
					dest.Add(body);
			}
			catch
			{
				// stale player
			}
		}
	}

	private static bool TryReadAnchor(object player, out Vector3 anchor)
	{
		anchor = Vector3.zero;

		// together names this CameraViewPos. old krok used camerapos.
		if (_cameraPosProp != null)
		{
			try
			{
				object v = _cameraPosProp.GetValue(player, null);
				if (v is Vector2 cam2)
				{
					anchor = new Vector3(cam2.x, cam2.y, 0f);
					return true;
				}
				if (v is Vector3 cam3)
				{
					anchor = cam3;
					return true;
				}
			}
			catch
			{
				// fall through
			}
		}

		if (_bodyField != null)
		{
			try
			{
				if (_bodyField.GetValue(player) is Component body && body != null)
				{
					anchor = body.transform.position;
					return true;
				}
			}
			catch
			{
				// fall through
			}
		}

		if (_posProp != null)
		{
			try
			{
				object v = _posProp.GetValue(player, null);
				if (v is Vector2 pos2)
				{
					anchor = new Vector3(pos2.x, pos2.y, 0f);
					return true;
				}
				if (v is Vector3 pos3)
				{
					anchor = pos3;
					return true;
				}
			}
			catch
			{
				// fall through
			}
		}

		return false;
	}

	private static void Resolve()
	{
		if (_resolved)
			return;

		// together 5: Net.IsRunning / Net.IsServer, players on ScavPlayer.
		// IsServer is true for host and dedicated, false for a client.
		_netType = TypeLookup.Find("Together.Net") ?? TypeLookup.Find("KrokoshaCasualtiesMP.Net");
		_netPlayerType = TypeLookup.Find("Together.ScavPlayer") ?? TypeLookup.Find("KrokoshaCasualtiesMP.NetPlayer");
		if (_netType == null && _netPlayerType == null && !TypeLookup.PluginsReady)
			return;
		_resolved = true;

		if (_netType != null)
		{
			bool together = _netType.FullName != null
				&& _netType.FullName.StartsWith("Together.", StringComparison.Ordinal);
			if (together)
			{
				_runningProp = AccessTools.Property(_netType, "IsRunning");
				_isServerProp = AccessTools.Property(_netType, "IsServer");
			}
			else
			{
				// properties first. AccessTools.Field warns on missing names and Net uses props
				_runningProp = AccessTools.Property(_netType, "running");
				_isServerProp = AccessTools.Property(_netType, "is_server");
				if (_runningProp == null)
					_runningField = _netType.GetField("running", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				if (_isServerProp == null)
					_isServerField = _netType.GetField("is_server", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			}
		}

		if (_netPlayerType == null)
			return;

		const BindingFlags stat = BindingFlags.Public | BindingFlags.Static;
		const BindingFlags inst = BindingFlags.Public | BindingFlags.Instance;
		_allLivingPlayersField = _netPlayerType.GetField("AllLivingPlayers", stat);
		_allDeadPlayersField = _netPlayerType.GetField("AllDeadPlayers", stat);
		_posProp = _netPlayerType.GetProperty("pos", inst);
		_cameraPosProp = _netPlayerType.GetProperty("camerapos", inst)
			?? _netPlayerType.GetProperty("cameraPos", inst)
			?? _netPlayerType.GetProperty("CameraViewPos", inst);
		_bodyField = _netPlayerType.GetField("body", inst);
	}
}
