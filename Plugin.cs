using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using CPUOptimization.Features.Lights;
using CPUOptimization.Features.ChunkSim;
using CPUOptimization.Features.Fluids;
using CPUOptimization.Features.Mp;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization;

[BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
[BepInDependency("KrokoshaCasualtiesMP", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
	internal static ManualLogSource Log;

	internal static ConfigEntry<bool> Enabled;

	internal static ConfigEntry<bool> LightsEnabled;
	internal static ConfigEntry<bool> LightsReplaceTrapLights;
	internal static ConfigEntry<bool> LightsCullEnabled;
	internal static ConfigEntry<float> LightsCullRadius;
	internal static ConfigEntry<bool> LightsCullUseCameraView;
	internal static ConfigEntry<float> LightsCullCameraMargin;
	internal static ConfigEntry<float> LightsCullHysteresis;
	internal static ConfigEntry<float> LightsCullIntervalSeconds;
	internal static ConfigEntry<float> LightsRescanSeconds;
	internal static ConfigEntry<bool> LightsDebugLog;
	internal static ConfigEntry<bool> LightsVerboseLogging;
	internal static ConfigEntry<float> LightsTelemetryWindowSeconds;

	internal static ConfigEntry<bool> ChunkSimEnabled;
	internal static ConfigEntry<bool> ChunkSimMpUnionEnabled;
	internal static ConfigEntry<bool> ChunkSimMpClientLocalSim;
	internal static ConfigEntry<bool> ChunkSimColliders;
	internal static ConfigEntry<bool> ChunkSimLogOnChange;
	internal static ConfigEntry<float> ChunkSimTelemetrySeconds;
	internal static ConfigEntry<bool> SpiderAnimThrottleEnabled;
	internal static ConfigEntry<int> SpiderAnimThrottleFrames;
	internal static ConfigEntry<int> ChunkUnionApplyBatchPerFrame;
	internal static ConfigEntry<int> ChunkUnionBodySyncBatchPerFrame;

	internal static ConfigEntry<bool> FluidsEnabled;
	internal static ConfigEntry<float> FluidsRenderIntervalSeconds;
	internal static ConfigEntry<bool> FluidsSpreadRender;
	internal static ConfigEntry<int> FluidsRenderColumnsPerFrame;

	internal static ConfigEntry<bool> KrokMpPerfEnabled;
	internal static ConfigEntry<int> KrokMpPerfOnWillRenderInterval;
	internal static ConfigEntry<int> KrokMpPerfOnWillRenderInvokeBudget;
	internal static ConfigEntry<int> KrokMpPerfOnWillRenderTrapBudget;
	internal static ConfigEntry<int> KrokMpPerfOnWillRenderTraderBudget;
	internal static ConfigEntry<int> KrokMpPerfDespawnerFrameSkip;
	internal static ConfigEntry<bool> KrokMpPerfGateChunkSim;
	internal static ConfigEntry<bool> KrokMpPerfVoicechatEarlyOut;

	private Harmony _harmony;

	private void Awake()
	{
		Log = Logger;

		Enabled = Config.Bind("General", "Enabled", true,
			"master switch for all cpu opt features");

		LightsEnabled = Config.Bind("Lights", "Enabled", true,
			"local light2d cull + trap sprite flash");
		LightsReplaceTrapLights = Config.Bind("Lights", "ReplaceTrapLights", true,
			"kill jumppad/coil/turret light2d, flash sprites instead (spike trap keeps blinking light)");
		LightsCullEnabled = Config.Bind("Lights", "CullEnabled", true,
			"turn off decorative light2d past cull radius from camera");
		LightsCullRadius = Config.Bind("Lights", "CullRadius", 72f,
			"world units from camera where lights stay on (fallback if cull use camera view is off)");
		LightsCullUseCameraView = Config.Bind("Lights", "CullUseCameraView", true,
			"derive cull radius from ortho view (1.1x visible corner). off = fixed cull radius");
		LightsCullCameraMargin = Config.Bind("Lights", "CullCameraMargin", 1.1f,
			"multiplier on visible corner distance for light cull on-radius");
		LightsCullHysteresis = Config.Bind("Lights", "CullHysteresis", 24f,
			"extra distance before a culled light turns off (stops flicker)");
		LightsCullIntervalSeconds = Config.Bind("Lights", "CullIntervalSeconds", 0.35f,
			"how often to re-check light on/off");
		LightsRescanSeconds = Config.Bind("Lights", "RescanSeconds", 2.5f,
			"deprecated, ignored. light2d register via onenable now");
		LightsDebugLog = Config.Bind("Lights", "DebugLog", true,
			"periodic light cull counter logs");
		LightsVerboseLogging = Config.Bind("Lights", "VerboseLogging", false,
			"log every trap light conversion");
		LightsTelemetryWindowSeconds = Config.Bind("Lights", "TelemetryWindowSeconds", 10f,
			"seconds between light telemetry lines");

		ChunkSimEnabled = Config.Bind("ChunkSim", "Enabled", true,
			"sim rb only in active chunk window (sp: 2x2 around camera; mp: union per player)");
		ChunkSimMpUnionEnabled = Config.Bind("ChunkSim", "MpUnionEnabled", true,
			"when krokmp is running, host uses union of each living player's 2x2 window");
		ChunkSimMpClientLocalSim = Config.Bind("ChunkSim", "MpClientLocalSim", true,
			"mp clients: sim/cull local 2x2 only. host/listen server always uses union");
		ChunkSimColliders = Config.Bind("ChunkSim", "ChunkColliders", true,
			"enable composite collider2d only on active 2x2 chunks (252 others off)");
		ChunkSimLogOnChange = Config.Bind("ChunkSim", "LogOnWindowChange", true,
			"log camera/block/chunk coords when 2x2 window moves");
		ChunkSimTelemetrySeconds = Config.Bind("ChunkSim", "TelemetrySeconds", 10f,
			"periodic chunk-sim stats. 0 = window-change logs only");
		SpiderAnimThrottleEnabled = Config.Bind("ChunkSim", "SpiderAnimThrottleEnabled", true,
			"frame-skip idle spider leg/ik updates. elders/combat exempt");
		SpiderAnimThrottleFrames = Config.Bind("ChunkSim", "SpiderAnimThrottleFrames", 2,
			"run idle spider anim every n frames (2 = half rate)");
		ChunkUnionApplyBatchPerFrame = Config.Bind("ChunkSim", "ChunkUnionApplyBatchPerFrame", 2,
			"mp union collider toggles per frame (spread union rebuild hitches)");
		ChunkUnionBodySyncBatchPerFrame = Config.Bind("ChunkSim", "ChunkUnionBodySyncBatchPerFrame", 128,
			"mp union building/item sync entries per frame after union change");

		FluidsEnabled = Config.Bind("Fluids", "Enabled", true,
			"align fluid sim/render range with chunksim window, spread renderfluids");
		FluidsRenderIntervalSeconds = Config.Bind("Fluids", "RenderIntervalSeconds", 0.15f,
			"min seconds between fluid particle render passes (vanilla 0.1)");
		FluidsSpreadRender = Config.Bind("Fluids", "SpreadRender", true,
			"spread renderfluids across frames by column batches");
		FluidsRenderColumnsPerFrame = Config.Bind("Fluids", "RenderColumnsPerFrame", 32,
			"block columns per frame when spread render is on");

		KrokMpPerfEnabled = Config.Bind("KrokMpPerf", "Enabled", true,
			"harmony perf patches for krokmp host/client overhead");
		KrokMpPerfOnWillRenderInterval = Config.Bind("KrokMpPerf", "OnWillRenderIntervalFrames", 6,
			"run krokosha onwillrender forceformp every n frames (host). 1 = every frame");
		KrokMpPerfOnWillRenderInvokeBudget = Config.Bind("KrokMpPerf", "OnWillRenderInvokeBudgetPerTick", 8,
			"max forced onwillrender invokes per tick for unknown target types");
		KrokMpPerfOnWillRenderTrapBudget = Config.Bind("KrokMpPerf", "OnWillRenderTrapInvokeBudgetPerTick", 6,
			"max gunmine/stalactite forceformp invokes per tick");
		KrokMpPerfOnWillRenderTraderBudget = Config.Bind("KrokMpPerf", "OnWillRenderTraderInvokeBudgetPerTick", 2,
			"max trader forceformp invokes per tick");
		KrokMpPerfDespawnerFrameSkip = Config.Bind("KrokMpPerf", "DespawnerFrameSkip", 4,
			"run item despawner if untouched every n frames; timer scaled to match");
		KrokMpPerfGateChunkSim = Config.Bind("KrokMpPerf", "GateChunkSim", true,
			"skip krokmp perf targets outside cpuopt chunk window");
		KrokMpPerfVoicechatEarlyOut = Config.Bind("KrokMpPerf", "VoicechatEarlyOut", true,
			"skip voicechat.update when vc disabled and mic not recording");

		if (!Enabled.Value)
		{
			CpuLog.Info("[CPUOpt] disabled via config.");
			return;
		}

		_harmony = new Harmony(PluginInfo.GUID);

		if (LightsEnabled.Value)
			LightOptBootstrap.Register(_harmony, gameObject);

		if (ChunkSimEnabled.Value)
			ChunkSimBootstrap.Register(_harmony, gameObject);

		if (FluidsEnabled.Value)
			FluidManagerPerfBootstrap.Apply(_harmony);

		KrokMpOptional.Resolve();
		CpuLog.Info(
			$"[CPUOpt] loaded v{PluginInfo.Version} lights={(LightsEnabled.Value ? 1 : 0)} " +
			$"chunkSim={(ChunkSimEnabled.Value ? 1 : 0)} mpUnion={(ChunkSimMpUnionEnabled.Value ? 1 : 0)} " +
			$"mpClientLocal={(ChunkSimMpClientLocalSim.Value ? 1 : 0)} fluids={(FluidsEnabled.Value ? 1 : 0)} " +
			$"krokPerf={(KrokMpPerfEnabled.Value ? 1 : 0)} krokmp={(KrokMpOptional.IsPresent ? 1 : 0)}");

		try
		{
			KrokMpPerfBootstrap.EnsureDeferred(_harmony, gameObject);
			if (!gameObject.GetComponent<KrokMpPerfTickHost>())
				gameObject.AddComponent<KrokMpPerfTickHost>();
		}
		catch (Exception ex)
		{
			CpuLog.Warn($"[CPUOpt] KrokMP perf bootstrap host failed: {ex.Message}");
		}
	}

	private void OnDestroy()
	{
		_harmony?.UnpatchSelf();
	}
}

internal static class PluginInfo
{
	public const string GUID = "com.local.cpu.optimization";
	public const string Name = "CPUOptimization";
	public const string Version = "0.5.18";
}
