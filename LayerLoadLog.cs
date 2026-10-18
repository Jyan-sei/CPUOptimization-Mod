using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// one clock for a layer load. world gen, then each capture call, then bootstrap
/// step names. the summary is one line when the cull finishes.
/// </summary>
internal static class LayerLoadLog
{
	private static bool _open;
	private static bool _genMarked;
	private static bool _screenDropped;
	private static float _start;
	private static float _genSec;
	private static string _step;
	private static float _stepAt;
	private static readonly Dictionary<string, float> _calls = new Dictionary<string, float>();
	private static readonly Dictionary<string, float> _steps = new Dictionary<string, float>();
	private static readonly Dictionary<string, int> _stepCount = new Dictionary<string, int>();

	internal static bool ScreenDropped => _screenDropped;
	internal static bool Open => _open;

	private static string _phase;
	private static float _phaseAt;

	internal static void Begin()
	{
		if (_open)
			return;
		_open = true;
		_genMarked = false;
		_screenDropped = false;
		_phase = null;
		_phaseAt = 0f;
		_start = Time.realtimeSinceStartup;
		_genSec = 0f;
		_step = null;
		_calls.Clear();
		_steps.Clear();
		_stepCount.Clear();
		InstantLoad.OnGenBegan();
		Plugin.Log.LogInfo("layer load begin");
	}

	internal static void Abort()
	{
		if (!_open)
			return;
		CloseStep();
		Plugin.Log.LogInfo("layer load interrupted t=" + Elapsed().ToString("0.000") + "s");
		_open = false;
		_step = null;
		InstantLoad.OnGenFinished();
	}

	internal static void MarkGenDone()
	{
		if (!_open || _genMarked)
			return;
		_genMarked = true;
		_genSec = Elapsed();
		Plugin.Log.LogInfo("layer load world-gen " + (_genSec * 1000f).ToString("0") + "ms");
	}

	internal static void Span(string name, System.Action work)
	{
		float t = Time.realtimeSinceStartup;
		work();
		float ms = (Time.realtimeSinceStartup - t) * 1000f;
		_calls[name] = ms;
		Plugin.Log.LogInfo("layer load " + name + " " + ms.ToString("0.0") + "ms");
	}

	/// <summary>bootstrap announced a step. the previous step owns the time since.</summary>
	internal static void Step(string name)
	{
		if (!_open)
			return;
		CloseStep();
		_step = name;
		_stepAt = Time.realtimeSinceStartup;
	}

	internal static void CloseStep()
	{
		if (_step == null)
			return;
		float ms = (Time.realtimeSinceStartup - _stepAt) * 1000f;
		if (!_steps.TryGetValue(_step, out float acc))
			acc = 0f;
		_steps[_step] = acc + ms;
		if (!_stepCount.TryGetValue(_step, out int n))
			n = 0;
		_stepCount[_step] = n + 1;
		_step = null;
	}

	/// <summary>close the previous terrain phase and start this one. logs the time just spent.</summary>
	internal static void EnterPhase(string name)
	{
		if (!_open || string.IsNullOrEmpty(name))
			return;
		float now = Time.realtimeSinceStartup;
		if (_phase != null)
		{
			float ms = (now - _phaseAt) * 1000f;
			Plugin.Log.LogInfo("layer load phase " + _phase + " " + ms.ToString("0") + "ms");
		}
		_phase = name;
		_phaseAt = now;
	}

	internal static void Apply(HarmonyLib.Harmony harmony)
	{
		harmony.Patch(
			HarmonyLib.AccessTools.Method(typeof(WorldGeneration), "SetLoadingText"),
			prefix: new HarmonyLib.HarmonyMethod(typeof(LayerLoadLog), nameof(OnLoadingText)));
		harmony.Patch(
			HarmonyLib.AccessTools.Method(typeof(WorldGeneration), "GenerateOres"),
			prefix: new HarmonyLib.HarmonyMethod(typeof(LayerLoadLog), nameof(OnOres)),
			postfix: new HarmonyLib.HarmonyMethod(typeof(LayerLoadLog), nameof(OnAfterOres)));
		harmony.Patch(
			HarmonyLib.AccessTools.Method(typeof(WorldGeneration), "PlaceLiquids"),
			prefix: new HarmonyLib.HarmonyMethod(typeof(LayerLoadLog), nameof(OnLiquids)),
			postfix: new HarmonyLib.HarmonyMethod(typeof(LayerLoadLog), nameof(OnAfterLiquids)));
		harmony.Patch(
			HarmonyLib.AccessTools.Method(typeof(WorldGeneration), "WorldGenerateWorldBorders"),
			prefix: new HarmonyLib.HarmonyMethod(typeof(LayerLoadLog), nameof(OnBorders)));
	}

	private static void OnLoadingText(string localetext)
	{
		EnterPhase(localetext);
	}

	private static void OnOres()
	{
		EnterPhase("ores");
	}

	private static void OnAfterOres()
	{
		EnterPhase("streaks");
	}

	private static void OnLiquids()
	{
		EnterPhase("liquids");
	}

	private static void OnAfterLiquids()
	{
		EnterPhase("after-liquids");
	}

	private static void OnBorders()
	{
		EnterPhase("borders");
	}

	internal static void NoteScreenDropped()
	{
		if (_screenDropped)
			return;
		_screenDropped = true;
		Plugin.Log.LogInfo(
			"layer load screen dropped t=" + Elapsed().ToString("0.000")
			+ "s - teardown continues in play");
	}

	internal static void Finish(string why)
	{
		if (!_open)
			return;
		CloseStep();
		var sb = new StringBuilder();
		sb.Append("layer load done ").Append(why);
		sb.Append(" total=").Append(Elapsed().ToString("0.000")).Append("s");
		sb.Append(" screen=").Append(_screenDropped ? "play" : "load");
		sb.Append(" world-gen=").Append((_genSec * 1000f).ToString("0")).Append("ms");
		foreach (var kv in _calls)
			sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value.ToString("0")).Append("ms");

		var rows = new List<KeyValuePair<string, float>>(_steps);
		rows.Sort((a, b) => b.Value.CompareTo(a.Value));
		for (int i = 0; i < rows.Count; i++)
		{
			string key = rows[i].Key;
			_stepCount.TryGetValue(key, out int n);
			sb.Append(" | ").Append(key).Append(' ').Append(rows[i].Value.ToString("0")).Append("ms x").Append(n);
		}

		Plugin.Log.LogInfo(sb.ToString());
		_open = false;
		_step = null;
		InstantLoad.OnGenFinished();
	}

	private static float Elapsed()
	{
		return Time.realtimeSinceStartup - _start;
	}
}
