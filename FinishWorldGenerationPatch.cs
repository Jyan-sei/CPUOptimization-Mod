using System;
using System.Reflection;
using HarmonyLib;

namespace CPUOptimization2;

internal static class FinishWorldGenerationPatch
{
	internal static void Apply(Harmony harmony)
	{
		MethodInfo factory = AccessTools.Method(typeof(WorldGeneration), "FinishWorldGeneration", Type.EmptyTypes);
		if (factory == null)
		{
			Plugin.Log.LogError("WorldGeneration.FinishWorldGeneration() was not found.");
			return;
		}

		MethodInfo target = AccessTools.EnumeratorMoveNext(factory) ?? factory;
		harmony.Patch(target, postfix: new HarmonyMethod(typeof(FinishWorldGenerationPatch), nameof(Postfix)));
		Plugin.Log.LogInfo($"patched {target.DeclaringType?.Name}.{target.Name}");
	}

	private static void Postfix(object __instance, bool __result)
	{
		if (Plugin.Enabled == null || !Plugin.Enabled.Value)
			return;

		WorldGeneration world = ResolveWorld(__instance);
		if (world == null)
			return;

		if (world.generatingWorld)
		{
			if (CaptureStore.Captured || StreamState.Active)
			{
				CaptureStore.Clear();
				StreamController.Deactivate();
				StreamState.ResetForNewGenerate();
				RenderTune.Reset();
				GenGhostPlace.Reset();
				Plugin.Log.LogInfo("generatingWorld rose again — capture/stream cleared.");
			}
			return;
		}

		if (!world.worldExists || CaptureStore.Captured)
			return;

		if (world.biomeOverride == WorldGeneration.OverrideSceneType.Tutorial)
		{
			Plugin.Log.LogInfo("tutorial layer — capture skipped.");
			return;
		}

		// mp: always capture (puts GenGhostPlace ghosts back). stream unload/load is host-only.
		// clients must not destroy GOs the host owns.
		bool wantStream = Plugin.StreamEnabled != null && Plugin.StreamEnabled.Value;
		bool stream = wantStream && MpSession.AllowStream;
		if (wantStream && !stream)
			Plugin.Log.LogInfo("Krok client — capture on, stream deferred (host owns unload/load).");

		if (wantStream)
		{
			// inline Instantiates (ropes, bandages, remote traders, mini-barrels) go into the arrays, then get destroyed
			GenGhostPlace.CullLiveOutsideWindow(world);
			GenGhostPlace.LogSummary();
			WorldScanner.Capture(world, mergeLiveOnly: CaptureStore.GhostFilled);
		}
		else
		{
			WorldScanner.Capture(world);
		}

		SanityLog.LogObjectCapture();

		if (stream)
		{
			StreamController.ActivateBootstrap(world);
		}
		else
		{
			CaptureStore.FramesUntilTileCapture = 90;
		}

		GenGhostPlace.Reset();
	}

	private static WorldGeneration ResolveWorld(object instance)
	{
		if (instance is WorldGeneration world)
			return world;

		FieldInfo self = instance.GetType().GetField("<>4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		return self?.GetValue(instance) as WorldGeneration;
	}
}
