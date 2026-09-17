using System;
using CPUOptimization.Features.Mp;
using CPUOptimization.Features.Mp.Patches;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

internal static class ChunkSimBootstrap
{
	internal static void Register(Harmony harmony, GameObject host)
	{
		harmony.PatchAll(typeof(ItemChunkSimPatch));
		harmony.PatchAll(typeof(WaterContainerItemChunkSimPatch));
		harmony.PatchAll(typeof(BuildingChunkSimPatch));
		harmony.PatchAll(typeof(SpiderChunkSimPatch));
		harmony.PatchAll(typeof(SpiderFixedUpdateChunkSimPatch));
		harmony.PatchAll(typeof(SpiderCollisionChunkSimPatch));
		harmony.PatchAll(typeof(IkHandleChunkSimPatch));
		harmony.PatchAll(typeof(JumpPadChunkSimPatch));
		harmony.PatchAll(typeof(CoilChunkSimPatch));
		harmony.PatchAll(typeof(TurretChunkSimPatch));
		harmony.PatchAll(typeof(MineChunkSimPatch));
		harmony.PatchAll(typeof(BearTrapChunkSimPatch));
		harmony.PatchAll(typeof(DropperParticleRegisterPatch));
		harmony.PatchAll(typeof(DropperChunkSimPatch));
		harmony.PatchAll(typeof(CaveTickSpawnerChunkSimPatch));
		harmony.PatchAll(typeof(GlowshroomOscillateChunkSimPatch));
		harmony.PatchAll(typeof(ChunkSimLifecyclePatches));
		harmony.PatchAll(typeof(DamageableBuildingHealthPatch));
		harmony.PatchAll(typeof(BodyAttackBuildingHealthPatch));
		TryApplyKrokMpBuildingBypass(harmony);

		TryApplyParticles(harmony);

		if (host && !host.GetComponent<ChunkSimHost>())
			host.AddComponent<ChunkSimHost>();

		if (host && !host.GetComponent<ParticleCullHost>())
			host.AddComponent<ParticleCullHost>();

		if (Plugin.KrokMpPerfEnabled != null && Plugin.KrokMpPerfEnabled.Value)
			KrokMpPerfDeferredHost.Ensure(harmony, host);

		string spiderAnim = Plugin.SpiderAnimThrottleEnabled != null && Plugin.SpiderAnimThrottleEnabled.Value
			? $"1/{Plugin.SpiderAnimThrottleFrames?.Value ?? 2}f"
			: "0";

		CpuLog.Info(
			$"[CPUOpt] chunk-sim patches armed colliders={(Plugin.ChunkSimColliders?.Value == true ? 1 : 0)} " +
			$"freezeCrates={(Plugin.ChunkSimFreezeCrates?.Value == true ? 1 : 0)} " +
			$"mpUnion={(Plugin.ChunkSimMpUnionEnabled?.Value == true ? 1 : 0)} " +
			$"mpClientLocal={(Plugin.ChunkSimMpClientLocalSim?.Value == true ? 1 : 0)} " +
			$"items={(Plugin.ChunkSimItemsEnabled?.Value == true ? 1 : 0)} " +
			$"bldgs={(Plugin.ChunkSimBuildingsEnabled?.Value == true ? 1 : 0)} " +
			$"particles={(Plugin.ChunkSimParticlesEnabled?.Value == true ? 1 : 0)} " +
			$"soundcannons={(Plugin.ChunkSimSoundCannonsEnabled?.Value == true ? 1 : 0)} " +
			$"forceformp={(Plugin.ChunkSimForceForMpEnabled?.Value == true ? 1 : 0)} " +
			$"bldHealth={(Plugin.ChunkSimBuildingHealthEnabled?.Value == true ? 1 : 0)} " +
			$"krokBypass={(Plugin.ChunkSimKrokMpBypassEnabled?.Value == true ? 1 : 0)} " +
			$"spiderAnim={spiderAnim} " +
			$"logOnChange={(Plugin.ChunkSimLogOnChange?.Value == true ? 1 : 0)} sync=window disableOffWindow=1");
	}

	private static void TryApplyKrokMpBuildingBypass(Harmony harmony)
	{
		try
		{
			harmony.PatchAll(typeof(KrokMpBuildingOptimizeBypass));
		}
		catch (Exception ex)
		{
			CpuLog.Warn($"[CPUOpt] KrokMP building timeScale bypass skipped: {ex.Message}");
		}
	}

	private static void TryApplyParticles(Harmony harmony)
	{
		try
		{
			ParticleCullPatches.Apply(harmony);
		}
		catch (Exception ex)
		{
			CpuLog.Warn($"[CPUOpt] particle cull patches skipped: {ex.Message}");
		}
	}

}
