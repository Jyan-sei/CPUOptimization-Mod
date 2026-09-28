using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// DemonAltarBehaviour.OnDestroy blesses cobalt when the player is within 30 and triggered is still false.
/// a stream delete is Object.Destroy, so that runs at end of frame, after the drop-suppress flag is already clear.
/// set triggered first. a real break never comes through here, and a break that already dropped health still blesses.
/// </summary>
internal static class AltarBlessing
{
	private static bool _sawUpdate;
	private static bool _absent;
	private static Type _type;
	private static FieldInfo _triggered;

	internal static void NotifyUpdate()
	{
		_sawUpdate = true;
	}

	internal static void Suppress(GameObject go)
	{
		if (go == null || _absent)
			return;

		Ensure();
		if (_type == null || _triggered == null)
			return;

		Component[] found;
		try
		{
			found = go.GetComponentsInChildren(_type, true);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"altar lookup failed: {ex.Message}");
			return;
		}

		if (found == null)
			return;

		for (int i = 0; i < found.Length; i++)
		{
			Component altar = found[i];
			if (altar == null)
				continue;
			BuildingEntity build = altar.GetComponent<BuildingEntity>()
				?? altar.GetComponentInParent<BuildingEntity>();
			if (build != null && build.health < 0.5f)
				continue;
			try
			{
				_triggered.SetValue(altar, true);
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning($"altar stream destroy skip failed: {ex.Message}");
			}
		}
	}

	private static void Ensure()
	{
		if (_absent || (_type != null && _triggered != null))
			return;

		if (_type == null)
			_type = AccessTools.TypeByName("DemonAltarBehaviour");
		if (_type == null)
		{
			if (_sawUpdate)
				_absent = true;
			return;
		}

		if (_triggered == null)
			_triggered = AccessTools.Field(_type, "triggered");
		if (_triggered == null && _sawUpdate)
		{
			_absent = true;
			Plugin.Log.LogWarning("DemonAltarBehaviour has no triggered field. stream delete can still bless.");
		}
	}
}
