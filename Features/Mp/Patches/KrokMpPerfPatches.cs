using System;
using System.Reflection;
using CPUOptimization.Features.ChunkSim;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization.Features.Mp.Patches;

internal static class KrokMpChunkGate
{
	internal static bool ShouldRunAt(Vector3 worldPos, bool usePresentationOnClient = true)
	{
		if (Plugin.KrokMpPerfGateChunkSim == null || !Plugin.KrokMpPerfGateChunkSim.Value)
			return true;
		if (!ChunkSimState.IsActive)
			return true;

		if (usePresentationOnClient && KrokMpOptional.IsPureClient)
			return ChunkSimState.ShouldSimulatePresentationWorldPos(worldPos);

		return ChunkSimState.ShouldSimulateAuthorityWorldPos(worldPos);
	}
}

internal static class OnWillRenderObjectPerfPatch
{
	internal static void Apply(Harmony harmony)
	{
		if (KrokMpReflect.OnWillRenderUpdate == null)
			return;

		harmony.Patch(
			KrokMpReflect.OnWillRenderUpdate,
			prefix: new HarmonyMethod(typeof(OnWillRenderObjectPerfPatch), nameof(UpdatePrefix)));

		ForceForMPLifecyclePatches.Apply(harmony);
		TryPatchAttachmentSites(harmony);
	}

	private static void TryPatchAttachmentSites(Harmony harmony)
	{
		MethodInfo buildingStart = AccessTools.Method(typeof(BuildingEntity), "Start");
		if (buildingStart != null)
		{
			harmony.Patch(
				buildingStart,
				postfix: new HarmonyMethod(typeof(OnWillRenderObjectPerfPatch), nameof(BuildingStartPostfix)));
		}

		var asm = KrokMpOptional.FindAssembly();
		if (asm == null)
			return;

		Type traderType = asm.GetType("KrokoshaCasualtiesMP.KrokoshaTraderTrackerComponent");
		if (traderType != null)
		{
			MethodInfo trackerAwake = traderType.GetMethod("TrackerAwake", BindingFlags.Instance | BindingFlags.NonPublic);
			if (trackerAwake != null)
			{
				harmony.Patch(
					trackerAwake,
					postfix: new HarmonyMethod(typeof(OnWillRenderObjectPerfPatch), nameof(TraderAwakePostfix)));
			}
		}
	}

	static bool UpdatePrefix()
	{
		if (Plugin.KrokMpPerfEnabled == null || !Plugin.KrokMpPerfEnabled.Value)
			return true;
		if (KrokMpReflect.IsKrokMpClient())
			return true;
		return false;
	}

	static void BuildingStartPostfix(BuildingEntity __instance)
	{
		if (!__instance)
			return;
		var force = __instance.GetComponent(KrokMpReflect.OnWillRenderType);
		if (force is MonoBehaviour mb)
			ForceForMPScheduler.Register(mb);
	}

	static void TraderAwakePostfix(MonoBehaviour __instance)
	{
		if (!__instance)
			return;
		var force = __instance.GetComponent(KrokMpReflect.OnWillRenderType);
		if (force is MonoBehaviour mb)
			ForceForMPScheduler.Register(mb);
	}
}

internal static class ItemDespawnerPerfPatch
{
	internal static void Apply(Harmony harmony)
	{
		if (KrokMpReflect.ItemDespawnerUpdate == null)
			return;
		harmony.Patch(
			KrokMpReflect.ItemDespawnerUpdate,
			prefix: new HarmonyMethod(typeof(ItemDespawnerPerfPatch), nameof(Prefix)),
			postfix: new HarmonyMethod(typeof(ItemDespawnerPerfPatch), nameof(Postfix)));
	}

	static bool Prefix(MonoBehaviour __instance, ref bool __state)
	{
		__state = false;
		if (Plugin.KrokMpPerfEnabled == null || !Plugin.KrokMpPerfEnabled.Value)
			return true;

		if (KrokMpReflect.IsNetworkActiveAndIsClient() || __instance.transform.parent)
			return true;

		if (!KrokMpChunkGate.ShouldRunAt(__instance.transform.position, usePresentationOnClient: false))
			return false;

		int skip = Plugin.KrokMpPerfDespawnerFrameSkip?.Value ?? 4;
		if (skip > 1 && Time.frameCount % skip != 0)
			return false;

		__state = skip > 1;
		return true;
	}

	static void Postfix(MonoBehaviour __instance, bool __state)
	{
		if (!__state || KrokMpReflect.ItemDespawnerTimerField == null)
			return;

		int skip = Plugin.KrokMpPerfDespawnerFrameSkip?.Value ?? 4;
		if (skip <= 1)
			return;

		try
		{
			float timer = (float)KrokMpReflect.ItemDespawnerTimerField.GetValue(__instance);
			KrokMpReflect.ItemDespawnerTimerField.SetValue(
				__instance,
				timer + Time.deltaTime * (skip - 1));
		}
		catch (System.Exception ex)
		{
			CpuLog.Warn($"[CPUOpt] ItemDespawner perf postfix: {ex.Message}");
		}
	}
}

internal static class SpiderTrackerPerfPatch
{
	internal static void Apply(Harmony harmony)
	{
		if (KrokMpReflect.SpiderTrackerLateUpdate == null)
			return;
		harmony.Patch(
			KrokMpReflect.SpiderTrackerLateUpdate,
			prefix: new HarmonyMethod(typeof(SpiderTrackerPerfPatch), nameof(Prefix)));
	}

	static bool Prefix(MonoBehaviour __instance)
	{
		if (Plugin.KrokMpPerfEnabled == null || !Plugin.KrokMpPerfEnabled.Value)
			return true;

		if (IsElderThornback(__instance))
			return true;

		if (!KrokMpChunkGate.ShouldRunAt(__instance.transform.position))
			return false;

		return true;
	}

	private static bool IsElderThornback(MonoBehaviour instance)
	{
		if (!instance)
			return false;

		if (instance.GetComponent<ElderThornbackBehaviour>())
			return true;

		BuildingEntity building = instance.GetComponent<BuildingEntity>();
		return building && building.GetComponent<ElderThornbackBehaviour>();
	}
}

internal static class VoicechatPerfPatch
{
	internal static void Apply(Harmony harmony)
	{
		if (KrokMpReflect.VoicechatUpdate == null)
			return;
		harmony.Patch(
			KrokMpReflect.VoicechatUpdate,
			prefix: new HarmonyMethod(typeof(VoicechatPerfPatch), nameof(Prefix)));
	}

	static bool Prefix()
	{
		if (Plugin.KrokMpPerfEnabled == null || !Plugin.KrokMpPerfEnabled.Value)
			return true;
		if (Plugin.KrokMpPerfVoicechatEarlyOut == null || !Plugin.KrokMpPerfVoicechatEarlyOut.Value)
			return true;

		if (!KrokMpReflect.VoiceChatRulesEnabled() && !KrokMpReflect.VoiceChatIsRecording())
			return false;

		return true;
	}
}
