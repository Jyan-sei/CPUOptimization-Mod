using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CPUOptimization.Features.Mp;

// krokmp via reflection - no compile-time dependency
internal static class KrokMpOptional
{
	private static bool _present;
	private static Assembly _assembly;
	private static Type _netType;
	private static Type _netPlayerType;
	private static FieldInfo _allLivingPlayersField;
	private static FieldInfo _clientIdToPlayerDictField;
	private static PropertyInfo _posProp;
	private static FieldInfo _bodyField;
	private static bool _playerFieldsResolved;

	internal static bool IsPresent
	{
		get
		{
			Resolve();
			return _present;
		}
	}

	internal static Assembly FindAssembly()
	{
		Resolve();
		return _assembly;
	}

	internal static void Invalidate()
	{
		_present = false;
		_assembly = null;
		_netType = null;
		_netPlayerType = null;
		_playerFieldsResolved = false;
	}

	internal static void Resolve()
	{
		if (_present)
			return;

		_assembly = ScanForAssembly();
		if (_assembly == null)
			return;

		_netType = _assembly.GetType("KrokoshaCasualtiesMP.Net");
		_netPlayerType = _assembly.GetType("KrokoshaCasualtiesMP.NetPlayer");
		_present = _netType != null && _netPlayerType != null;
	}

	private static Assembly ScanForAssembly()
	{
		Assembly fallback = null;
		foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
		{
			string name = asm.GetName().Name;
			if (name == "KrokoshaCasualtiesMP"
			    || name == "KrokMP"
			    || (name != null && name.StartsWith("KrokoshaCasualtiesMP", StringComparison.Ordinal)))
				return asm;

			if (name == "KrokoshaCasualtiesUtils")
				fallback = asm;
		}

		return fallback;
	}

	internal static bool IsNetworkRunning => GetStaticBool(_netType, "running");

	internal static bool IsServer => GetStaticBool(_netType, "is_server");

	internal static bool IsClient => GetStaticBool(_netType, "is_client");

	// listen host is client + server - not a pure client
	internal static bool IsPureClient => IsNetworkRunning && IsClient && !IsServer;

	internal static void CollectLivingPlayerPositions(List<Vector3> dest)
	{
		if (dest == null)
			return;
		dest.Clear();
		if (!IsNetworkRunning || _netPlayerType == null)
			return;

		EnsurePlayerFields();
		var seen = new HashSet<int>();

		void consider(object player)
		{
			if (player == null)
				return;
			try
			{
				int key = player.GetHashCode();
				if (!seen.Add(key))
					return;

				Vector2 pos = ReadPos(player);
				if (pos.sqrMagnitude < 0.0001f)
				{
					if (_bodyField?.GetValue(player) is Body body && body)
						pos = body.transform.position;
				}
				dest.Add(new Vector3(pos.x, pos.y, 0f));
			}
			catch
			{
				// ignore bad player
			}
		}

		try
		{
			if (_allLivingPlayersField?.GetValue(null) is IEnumerable living)
			{
				foreach (object player in living)
					consider(player);
			}
		}
		catch
		{
			// ignore
		}

		if (dest.Count > 0)
			return;

		try
		{
			if (_clientIdToPlayerDictField?.GetValue(null) is IDictionary dict)
			{
				foreach (object player in dict.Values)
					consider(player);
			}
		}
		catch
		{
			// ignore
		}
	}

	private static Vector2 ReadPos(object player)
	{
		if (_posProp == null)
			return Vector2.zero;
		try
		{
			if (_posProp.GetValue(player) is Vector2 pos)
				return pos;
		}
		catch
		{
			// ignore
		}
		return Vector2.zero;
	}

	private static void EnsurePlayerFields()
	{
		if (_playerFieldsResolved || _netPlayerType == null)
			return;
		_playerFieldsResolved = true;
		const BindingFlags stat = BindingFlags.Public | BindingFlags.Static;
		const BindingFlags inst = BindingFlags.Public | BindingFlags.Instance;
		_allLivingPlayersField = _netPlayerType.GetField("AllLivingPlayers", stat);
		_clientIdToPlayerDictField = _netPlayerType.GetField("ClientIdToPlayerDict", stat);
		_posProp = _netPlayerType.GetProperty("pos", inst);
		_bodyField = _netPlayerType.GetField("body", inst);
	}

	private static bool GetStaticBool(Type type, string name)
	{
		if (type == null)
			return false;
		const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
		try
		{
			PropertyInfo prop = type.GetProperty(name, flags);
			if (prop != null && prop.GetIndexParameters().Length == 0)
				return prop.GetValue(null, null) is bool pb && pb;
		}
		catch
		{
			// fall through
		}

		try
		{
			FieldInfo field = type.GetField(name, flags);
			return field != null && field.GetValue(null) is bool b && b;
		}
		catch
		{
			return false;
		}
	}
}
