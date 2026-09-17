using System;
using System.Collections.Generic;
using System.Reflection;
using CPUOptimization.Features.ChunkSim;
using CPUOptimization.Features.Mp;
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
	private static readonly List<MonoBehaviour> Trackers = new List<MonoBehaviour>(256);
	private static int _disabled;
	private static int _enabled;

	internal static void Apply(Harmony harmony)
	{
		if (KrokMpReflect.SpiderTrackerLateUpdate == null)
			return;
		harmony.Patch(
			KrokMpReflect.SpiderTrackerLateUpdate,
			prefix: new HarmonyMethod(typeof(SpiderTrackerPerfPatch), nameof(Prefix)));

		if (KrokMpReflect.SpiderTrackerType != null)
		{
			var start = AccessTools.Method(KrokMpReflect.SpiderTrackerType, "Start");
			if (start != null)
				harmony.Patch(start, postfix: new HarmonyMethod(typeof(SpiderTrackerPerfPatch), nameof(StartPostfix)));
		}
	}

	static void StartPostfix(MonoBehaviour __instance)
	{
		Register(__instance);
		SyncOne(__instance);
	}

	static bool Prefix(MonoBehaviour __instance)
	{
		if (Plugin.KrokMpPerfEnabled == null || !Plugin.KrokMpPerfEnabled.Value)
			return true;

		Register(__instance);

		if (IsElderTracker(__instance))
			return true;

		if (!KrokMpChunkGate.ShouldRunAt(__instance.transform.position))
		{
			if (__instance.enabled)
			{
				__instance.enabled = false;
				_disabled++;
			}
			return false;
		}

		return true;
	}

	internal static void SyncEnabled()
	{
		for (int i = Trackers.Count - 1; i >= 0; i--)
		{
			MonoBehaviour tracker = Trackers[i];
			if (!tracker)
			{
				Trackers.RemoveAt(i);
				continue;
			}
			SyncOne(tracker);
		}
	}

	private static void SyncOne(MonoBehaviour tracker)
	{
		if (!tracker)
			return;
		bool want = IsElderTracker(tracker)
		            || KrokMpChunkGate.ShouldRunAt(tracker.transform.position);
		if (want)
		{
			if (!tracker.enabled)
			{
				tracker.enabled = true;
				_enabled++;
			}
			return;
		}
		if (tracker.enabled)
		{
			tracker.enabled = false;
			_disabled++;
		}
	}

	private static void Register(MonoBehaviour tracker)
	{
		if (!tracker)
			return;
		for (int i = 0; i < Trackers.Count; i++)
		{
			if (Trackers[i] == tracker)
				return;
		}
		Trackers.Add(tracker);
	}

	private static bool IsElderTracker(MonoBehaviour instance)
	{
		return instance && ElderRegistry.IsOnGameObject(instance.gameObject);
	}

	internal static void ConsumeProbe(out int disabled, out int enabled)
	{
		disabled = _disabled;
		enabled = _enabled;
		_disabled = 0;
		_enabled = 0;
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

internal static class BodyPinDeathPatch
{
	internal static void Apply(Harmony harmony)
	{
		if (KrokMpReflect.NetBodyOnDeath == null)
			return;
		harmony.Patch(
			KrokMpReflect.NetBodyOnDeath,
			postfix: new HarmonyMethod(typeof(BodyPinDeathPatch), nameof(Postfix)));
	}

	static void Postfix(object __instance)
	{
		if (__instance is not MonoBehaviour mb)
			return;
		Body body = mb.GetComponent<Body>();
		if (!body)
			body = mb.GetComponentInParent<Body>();
		BodyPinCache.Pin(body);
	}
}
