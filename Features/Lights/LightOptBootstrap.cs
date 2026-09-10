using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CPUOptimization.Features.Lights;

internal static class LightOptBootstrap
{
	internal static bool ReplaceTraps =>
		Plugin.Enabled != null && Plugin.Enabled.Value
		&& Plugin.LightsEnabled != null && Plugin.LightsEnabled.Value
		&& Plugin.LightsReplaceTrapLights != null && Plugin.LightsReplaceTrapLights.Value;

	internal static void Register(Harmony harmony, GameObject host)
	{
		if (Plugin.LightsReplaceTrapLights != null && Plugin.LightsReplaceTrapLights.Value)
		{
			foreach (var t in new[]
			{
				typeof(JumpPadLightStartPatch),
				typeof(JumpPadLightUpdatePatch),
				typeof(CoilLightAwakePatch),
				typeof(CoilLightUpdatePatch),
				typeof(TurretLightStartPatch),
				typeof(TurretLightUpdatePatch),
			})
			{
				try
				{
					harmony.PatchAll(t);
				}
				catch (Exception ex)
				{
					CpuLog.Warn($"[CPUOpt] lights patch skipped {t.Name}: {ex.Message}");
				}
			}
		}

		try
		{
			harmony.PatchAll(typeof(Light2DEnablePatch));
			harmony.PatchAll(typeof(Light2DDestroyObjectPatch));
			harmony.PatchAll(typeof(Light2DDestroyDelayedPatch));
			CpuLog.Info("[CPUOpt] lights destroy hook=Object.Destroy (2 overloads)");
		}
		catch (Exception ex)
		{
			CpuLog.Warn($"[CPUOpt] lights registry patch skipped: {ex.Message}");
		}

		if (host && !host.GetComponent<LightCullHost>())
			host.AddComponent<LightCullHost>();

		CameraViewMetrics.TryGetCullRadii(out float onR, out float offR);
		bool camView = Plugin.LightsCullUseCameraView == null || Plugin.LightsCullUseCameraView.Value;
		CpuLog.Info(
			$"[CPUOpt] lights armed trap-emission={(Plugin.LightsReplaceTrapLights?.Value == true ? 1 : 0)} " +
			$"cull={(Plugin.LightsCullEnabled?.Value == true ? 1 : 0)} " +
			$"registry=incremental radius={onR:F1} off={offR:F1} src={(camView ? "camera" : "config")} " +
			$"margin={Plugin.LightsCullCameraMargin?.Value ?? 1.1f:F2}");
	}
}
