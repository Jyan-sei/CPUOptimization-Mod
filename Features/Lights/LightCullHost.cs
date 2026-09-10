using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CPUOptimization.Features.Lights;

internal sealed class LightCullHost : MonoBehaviour
{
	private const int CullBatchSize = 128;

	private float _cullAge;
	private float _logAge;
	private bool _wasGenerating = true;
	private bool _loggedFirstScan;
	private int _compactCullTicks;
	private int _cullCursor;
	private int _passOn;
	private int _passCulled;
	private int _passProt;
	private int _passFx;
	private int _passLive;

	private static int _fxConverted;
	private static string _lastFxKind;

	internal static int LastTotal;
	internal static int LastOn;
	internal static int LastCulled;
	internal static int LastProtected;
	internal static int LastFx;

	internal static void NoteFxConverted(string kind, string name)
	{
		_fxConverted++;
		_lastFxKind = kind;
		if (Plugin.LightsVerboseLogging != null && Plugin.LightsVerboseLogging.Value)
			CpuLog.Info($"[CPUOpt] lights fx convert {kind} name={name} n={_fxConverted}");
		else if (_fxConverted == 1)
			CpuLog.Info($"[CPUOpt] lights fx convert first {kind} name={name}");
	}

	internal static void RegisterLight(Light2D light) => LightCullRegistry.Register(light);

	internal static void RequestRescan()
	{
		LightCullRegistry.CompactDead();
	}

	private void Update()
	{
		if (Plugin.LightsEnabled == null || !Plugin.LightsEnabled.Value)
			return;

		WorldGeneration world = WorldGeneration.world;
		bool generating = world && world.generatingWorld;
		if (_wasGenerating && !generating && world)
		{
			LightCullRegistry.CompactDead();
			CpuLog.Info("[CPUOpt] lights registry compacted (world gen finished)");
		}
		_wasGenerating = generating;
		if (generating)
			return;

		float dt = Time.unscaledDeltaTime;
		_cullAge += dt;
		_logAge += dt;

		float cullEvery = Plugin.LightsCullIntervalSeconds?.Value ?? 0.35f;
		if (cullEvery < 0.05f)
			cullEvery = 0.05f;
		if ((Plugin.LightsCullEnabled == null || Plugin.LightsCullEnabled.Value) && _cullAge >= cullEvery)
		{
			_cullAge = 0f;
			CullBatch();
		}

		float logEvery = Plugin.LightsTelemetryWindowSeconds?.Value ?? 10f;
		if (logEvery < 2f)
			logEvery = 2f;
		if (_logAge >= logEvery)
		{
			_logAge = 0f;
			LogWindow();
		}
	}

	private void CullBatch()
	{
		if (++_compactCullTicks % 8 == 0)
			LightCullRegistry.CompactDead();

		if (!TryOrigin(out Vector3 origin))
			return;

		IReadOnlyList<LightCullEntry> entries = LightCullRegistry.All;
		int count = entries.Count;
		if (count == 0)
			return;

		if (_cullCursor >= count)
			_cullCursor = 0;

		if (_cullCursor == 0)
		{
			_passOn = 0;
			_passCulled = 0;
			_passProt = 0;
			_passFx = 0;
			_passLive = 0;
		}

		CameraViewMetrics.TryGetCullRadii(out float onR, out float offR);
		float onSq = onR * onR;
		float offSq = offR * offR;

		WorldGeneration world = WorldGeneration.world;
		Transform localBody = PlayerCamera.main && PlayerCamera.main.body
			? PlayerCamera.main.body.transform
			: null;

		int batch = count <= CullBatchSize ? count : CullBatchSize;
		for (int n = 0; n < batch; n++)
		{
			int index = _cullCursor + n;
			if (index >= count)
				index -= count;
			ProcessEntry(entries[index], world, localBody, origin, onSq, offSq);
		}

		_cullCursor += batch;
		if (_cullCursor >= count)
		{
			_cullCursor = 0;
			CommitPassStats(count, onR);
		}
	}

	private void ProcessEntry(
		LightCullEntry entry,
		WorldGeneration world,
		Transform localBody,
		Vector3 origin,
		float onSq,
		float offSq)
	{
		Light2D light = entry.Light;
		if (!light)
			return;

		_passLive++;

		if ((entry.Flags & LightCullFlags.FxConverted) != 0)
		{
			if (light.enabled)
				light.enabled = false;
			_passFx++;
			return;
		}

		if (IsProtected(entry, light, world, localBody))
		{
			_passProt++;
			if (light.enabled)
				_passOn++;
			return;
		}

		float distSq = (light.transform.position - origin).sqrMagnitude;
		bool wantOn = light.enabled ? distSq <= offSq : distSq <= onSq;
		if (light.enabled != wantOn)
			light.enabled = wantOn;

		if (wantOn)
			_passOn++;
		else
			_passCulled++;
	}

	private static bool IsProtected(
		LightCullEntry entry,
		Light2D light,
		WorldGeneration world,
		Transform localBody)
	{
		if ((entry.Flags & LightCullFlags.Global) != 0)
			return true;
		if (world && world.ambientLight == light)
			return true;
		if ((entry.Flags & LightCullFlags.LightItem) != 0)
			return true;
		if (localBody && light.transform.IsChildOf(localBody))
			return true;
		return false;
	}

	private void CommitPassStats(int live, float onR)
	{
		LastTotal = live;
		LastOn = _passOn;
		LastCulled = _passCulled;
		LastProtected = _passProt;
		LastFx = _passFx;

		if (!_loggedFirstScan && live > 0)
		{
			_loggedFirstScan = true;
			CameraViewMetrics.TryGetCullRadii(out float logOnR, out _);
			bool fromCam = Plugin.LightsCullUseCameraView == null || Plugin.LightsCullUseCameraView.Value;
			CpuLog.Info(
				$"[CPUOpt] lights first-cull total={live} on={_passOn} culled={_passCulled} " +
				$"protected={_passProt} fx={_passFx} radius={logOnR:F1} src={(fromCam ? "camera" : "config")} " +
				$"batch={CullBatchSize} (incremental registry)");
		}
	}

	private static bool TryOrigin(out Vector3 origin)
	{
		if (PlayerCamera.main)
		{
			origin = PlayerCamera.main.transform.position;
			return true;
		}
		if (Camera.main)
		{
			origin = Camera.main.transform.position;
			return true;
		}
		origin = Vector3.zero;
		return false;
	}

	private void LogWindow()
	{
		if (Plugin.LightsDebugLog != null && !Plugin.LightsDebugLog.Value &&
		    (Plugin.LightsVerboseLogging == null || !Plugin.LightsVerboseLogging.Value))
			return;

		CameraViewMetrics.TryGetCullRadii(out float onR, out _);
		CpuLog.Info(
			$"[CPUOpt] lights total={LastTotal} on={LastOn} culled={LastCulled} " +
			$"protected={LastProtected} fx={LastFx} converted={_fxConverted} " +
			$"lastFx={_lastFxKind ?? "-"} radius={onR:F1} " +
			$"cull={(Plugin.LightsCullEnabled != null && Plugin.LightsCullEnabled.Value ? 1 : 0)} " +
			$"traps={(Plugin.LightsReplaceTrapLights != null && Plugin.LightsReplaceTrapLights.Value ? 1 : 0)}");
	}
}
