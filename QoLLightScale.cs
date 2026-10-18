using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CPUOptimization2;

/// <summary>
/// qol's light slider multiplies Light2D intensity. 100 leaves it alone.
/// a spawned light is new, so qol multiplies whatever we write.
/// keep the pre-slider value and mark that one write so the slider runs once.
/// </summary>
internal static class QoLLightScale
{
	private static bool _bound;
	private static bool _present;
	private static bool _logged;
	private static bool _typeWarned;
	private static FieldInfo _scale;
	private static FieldInfo _bases;
	private static FieldInfo _rawNext;
	private static FieldInfo _jumpPads;

	internal static float Authored(Light2D light)
	{
		if (light == null)
			return 0f;
		float shown = light.intensity;
		if (!Bind() || IsJumpPad(light.GetInstanceID()))
			return shown;
		if (TryBase(light.GetInstanceID(), out float authored))
			return authored;
		float factor = Factor();
		if (factor <= 0.0001f || Mathf.Approximately(factor, 1f))
			return shown;
		return shown / factor;
	}

	internal static void Apply(Light2D light, float authored)
	{
		if (light == null)
			return;
		if (Bind() && !IsJumpPad(light.GetInstanceID()))
			MarkAuthored(light.GetInstanceID());
		light.intensity = authored;
	}

	private static bool Bind()
	{
		if (_bound)
			return _present;
		_bound = true;
		Type type = AccessTools.TypeByName("QoL_Unknown.AntiGlarePatcher");
		if (type == null)
			return false;
		_scale = AccessTools.Field(type, "MasterLightScale");
		_bases = AccessTools.Field(type, "BaseIntensities");
		_rawNext = AccessTools.Field(type, "RawNextLightSetIds");
		_jumpPads = AccessTools.Field(type, "JumpPadLightIds");
		_present = _scale != null && _rawNext != null;
		if (_present && !_logged)
		{
			_logged = true;
			Plugin.Log.LogInfo("qol light scale: store authored intensity, slider applies once on spawn");
		}
		return _present;
	}

	private static float Factor()
	{
		if (_scale == null)
			return 1f;
		object value = _scale.GetValue(null);
		if (!(value is float scale))
			return 1f;
		return scale * 0.01f;
	}

	private static bool IsJumpPad(int id)
	{
		return _jumpPads?.GetValue(null) is HashSet<int> pads && pads.Contains(id);
	}

	private static bool TryBase(int id, out float authored)
	{
		authored = 0f;
		if (!(_bases?.GetValue(null) is IDictionary bases) || !bases.Contains(id))
			return false;
		if (!(bases[id] is float raw))
			return false;
		authored = raw;
		return true;
	}

	private static void MarkAuthored(int id)
	{
		if (_rawNext?.GetValue(null) is HashSet<int> next)
		{
			next.Add(id);
			return;
		}
		if (_typeWarned)
			return;
		_typeWarned = true;
		Plugin.Log.LogWarning("qol RawNextLightSetIds was not a HashSet<int>. spawn leaves the slider write alone.");
	}
}
