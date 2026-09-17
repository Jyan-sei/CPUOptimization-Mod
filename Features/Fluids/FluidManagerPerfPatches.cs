using System;
using System.Collections.Generic;
using System.Reflection;
using CPUOptimization.Features.ChunkSim;
using CPUOptimization.Features.Mp;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization.Features.Fluids;

internal static class FluidManagerPerfBootstrap
{
	internal static void Apply(Harmony harmony)
	{
		Type fluidType = AccessTools.TypeByName("FluidManager");
		if (fluidType == null)
		{
			CpuLog.Warn("[CPUOpt] FluidManager type not found - fluid perf skipped.");
			return;
		}

		MethodInfo simRange = AccessTools.Method(fluidType, "SimulationRange");
		if (simRange != null)
		{
			harmony.Patch(
				simRange,
				postfix: new HarmonyMethod(typeof(FluidManagerPerfPatchLogic), nameof(FluidManagerPerfPatchLogic.SimulationRangePostfix)));
		}

		MethodInfo simRangeIndex = AccessTools.Method(fluidType, "SimulationRangeIndex");
		if (simRangeIndex != null)
		{
			harmony.Patch(
				simRangeIndex,
				postfix: new HarmonyMethod(typeof(FluidManagerPerfPatchLogic), nameof(FluidManagerPerfPatchLogic.SimulationRangeIndexPostfix)));
		}

		MethodInfo update = AccessTools.Method(fluidType, "Update");
		if (update != null)
		{
			harmony.Patch(
				update,
				prefix: new HarmonyMethod(typeof(FluidManagerPerfPatchLogic), nameof(FluidManagerPerfPatchLogic.UpdatePrefix)));
		}

		MethodInfo render = AccessTools.Method(fluidType, "RenderFluids");
		if (render != null)
		{
			harmony.Patch(
				render,
				prefix: new HarmonyMethod(typeof(FluidManagerPerfPatchLogic), nameof(FluidManagerPerfPatchLogic.RenderFluidsPrefix)));
		}

		MethodInfo fixedUpdate = AccessTools.Method(fluidType, "FixedUpdate");
		if (fixedUpdate != null)
		{
			harmony.Patch(
				fixedUpdate,
				prefix: new HarmonyMethod(typeof(FluidManagerPerfPatchLogic), nameof(FluidManagerPerfPatchLogic.FixedUpdatePrefix)));
		}

		MethodInfo simStep = AccessTools.Method(fluidType, "SimulationStep");
		if (simStep != null)
		{
			harmony.Patch(
				simStep,
				prefix: new HarmonyMethod(typeof(FluidManagerPerfPatchLogic), nameof(FluidManagerPerfPatchLogic.SimulationStepPrefix)));
		}

		CpuLog.Info("[CPUOpt] fluid perf patches armed");
	}
}

internal static class FluidManagerPerfPatchLogic
{
	private static readonly AccessTools.FieldRef<FluidManager, float> CheckTimeRef =
		AccessTools.FieldRefAccess<FluidManager, float>("checkTime");
	private static readonly AccessTools.FieldRef<FluidManager, int> SimIndexRef =
		AccessTools.FieldRefAccess<FluidManager, int>("simIndex");
	private static readonly AccessTools.FieldRef<FluidManager, byte[,]> FluidRef =
		AccessTools.FieldRefAccess<FluidManager, byte[,]>("fluid");
	private static readonly AccessTools.FieldRef<FluidManager, List<ParticleSystem>> LiquidParticlesRef =
		AccessTools.FieldRefAccess<FluidManager, List<ParticleSystem>>("liquidParticles");

	private static int _renderRevision = -1;
	private static int _renderColumn;
	private static int _renderMinX;
	private static int _renderMaxX;
	private static RangeI _renderRangeY;
	private static List<ParticleSystem.Particle>[] _renderBuckets;
	private static bool _renderActive;

	private static int _simStepCounter;

	public static void SimulationRangePostfix(ref (RangeI, RangeI) __result)
	{
		if (!IsEnabled() || !ChunkSimBlockBounds.TryGetSimulationRanges(WorldGeneration.world, out RangeI rx, out RangeI ry))
			return;

		if (KrokMpOptional.IsNetworkRunning && KrokMpOptional.IsServer)
			return;

		RangeI x = __result.Item1;
		RangeI y = __result.Item2;
		Intersect(ref x, rx);
		Intersect(ref y, ry);
		__result = (x, y);
	}

	public static void SimulationRangeIndexPostfix(FluidManager __instance, ref (RangeI, RangeI) __result)
	{
		if (!IsEnabled() || !ChunkSimBlockBounds.TryGetSimulationRanges(WorldGeneration.world, out RangeI rx, out RangeI ry))
			return;

		// krokmp host fixedupdate forces per-chunk slices via forcenext - don't clobber
		if (KrokMpOptional.IsNetworkRunning && KrokMpOptional.IsServer)
			return;

		// sp / mp client: vanilla camera-relative sweep, clamp to chunk window
		RangeI x = __result.Item1;
		RangeI y = __result.Item2;
		Intersect(ref x, rx);
		Intersect(ref y, ry);
		__result = (x, y);
	}

	public static bool UpdatePrefix(FluidManager __instance)
	{
		if (!IsEnabled())
			return true;

		Vector2 vector = PlayerCamera.main.GetComponent<Camera>()
			.WorldToViewportPoint(PlayerCamera.main.body.transform.position + Vector3.down * 2.5f);
		__instance.waterMat.SetFloat("_ReflecHeight", vector.y);

		byte[,] fluid = FluidRef(__instance);
		if (fluid == null || fluid.Length < 64)
			return true;

		if (_renderActive)
		{
			AdvanceSpreadRender(__instance);
			return false;
		}

		float interval = Plugin.FluidsRenderIntervalSeconds?.Value ?? 0.15f;
		float now = Time.unscaledTime;
		if (now - CheckTimeRef(__instance) <= interval)
			return false;

		CheckTimeRef(__instance) = now;

		if (Plugin.FluidsSpreadRender?.Value != false)
		{
			BeginSpreadRender(__instance);
			return false;
		}

		return true;
	}

	public static bool RenderFluidsPrefix(FluidManager __instance)
	{
		if (!IsEnabled() || Plugin.FluidsSpreadRender?.Value == false)
			return true;

		return !_renderActive;
	}

	public static bool FixedUpdatePrefix()
	{
		if (!IsEnabled())
			return true;

		int interval = Plugin.FluidsSimIntervalFixedFrames?.Value ?? 1;
		if (interval <= 1)
			return true;

		if (++_simStepCounter % interval != 0)
			return false;

		return true;
	}

	public static bool SimulationStepPrefix(FluidManager __instance)
	{
		if (!IsEnabled())
			return true;

		byte[,] fluid = FluidRef(__instance);
		if (fluid == null)
			return true;

		if (!ChunkSimBlockBounds.TryGetSimulationRanges(WorldGeneration.world, out RangeI rx, out RangeI ry))
			return true;

		int w = fluid.GetLength(0);
		int h = fluid.GetLength(1);

		for (int i = rx.min; i < rx.max; i++)
		{
			if (i < 0 || i >= w) continue;
			for (int j = ry.min; j < ry.max; j++)
			{
				if (j < 0 || j >= h) continue;
				if (fluid[i, j] != 0)
					return true;
			}
		}

		return false;
	}

	private static bool IsEnabled() =>
		Plugin.Enabled != null && Plugin.Enabled.Value
		&& Plugin.FluidsEnabled != null && Plugin.FluidsEnabled.Value;

	private static void Intersect(ref RangeI slice, RangeI bounds)
	{
		slice.min = Mathf.Max(slice.min, bounds.min);
		slice.max = Mathf.Min(slice.max, bounds.max);
		if (slice.min > slice.max)
			slice.max = slice.min;
	}

	private static void BeginSpreadRender(FluidManager __instance)
	{
		int revision = ChunkSimState.WindowRevision;
		if (_renderActive && _renderRevision == revision)
			return;

		(RangeI, RangeI) tuple = __instance.SimulationRange();
		_renderRevision = revision;
		_renderMinX = tuple.Item1.min;
		_renderMaxX = tuple.Item1.max;
		_renderRangeY = tuple.Item2;
		_renderColumn = _renderMinX;
		_renderActive = true;

		int prefabCount = __instance.LiquidParticlePrefabs != null
			? __instance.LiquidParticlePrefabs.Count
			: 0;
		_renderBuckets = new List<ParticleSystem.Particle>[Mathf.Max(1, prefabCount)];
		for (int i = 0; i < _renderBuckets.Length; i++)
			_renderBuckets[i] = new List<ParticleSystem.Particle>();

		AdvanceSpreadRender(__instance);
	}

	private static void AdvanceSpreadRender(FluidManager __instance)
	{
		if (!_renderActive || _renderBuckets == null)
			return;

		byte[,] fluid = FluidRef(__instance);
		if (fluid == null)
		{
			_renderActive = false;
			return;
		}

		int columnsPerFrame = Mathf.Max(1, Plugin.FluidsRenderColumnsPerFrame?.Value ?? 32);
		int endColumn = Mathf.Min(_renderColumn + columnsPerFrame, _renderMaxX);

		for (int j = _renderColumn; j < endColumn; j++)
		{
			for (int k = _renderRangeY.min; k < _renderRangeY.max; k++)
			{
				if (fluid[j, k] == 0)
					continue;

				int bucket = fluid[j, k] - 1;
				if (bucket < 0 || bucket >= _renderBuckets.Length)
					continue;

				bool flag = fluid[j, k + 1] == 0 && (fluid[j + 1, k] == 0 || fluid[j - 1, k] == 0);
				_renderBuckets[bucket].Add(new ParticleSystem.Particle
				{
					position = WorldGeneration.world.BlockToWorldPos(new Vector2Int(j, k))
					             + (flag ? new Vector2(0f, -0.3125f) : Vector2.zero),
					startLifetime = 999f,
					remainingLifetime = 999f,
					startColor = Color.white,
					startSize3D = new Vector2(1.25f, flag ? 0.625f : 1.25f)
				});
			}
		}

		_renderColumn = endColumn;
		if (_renderColumn >= _renderMaxX)
			FinishSpreadRender(__instance);
	}

	private static void FinishSpreadRender(FluidManager __instance)
	{
		List<ParticleSystem> particles = LiquidParticlesRef(__instance);
		if (particles != null && _renderBuckets != null)
		{
			int count = Mathf.Min(particles.Count, _renderBuckets.Length);
			for (int i = 0; i < count; i++)
				particles[i].SetParticles(_renderBuckets[i].ToArray());
		}

		_renderActive = false;
		_renderBuckets = null;
	}

	internal static void TickSpreadRender(FluidManager __instance)
	{
		if (_renderActive)
			AdvanceSpreadRender(__instance);
	}
}
