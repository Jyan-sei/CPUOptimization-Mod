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
				StructureLiveState.ResetLayer();
				StreamController.Deactivate();
				StreamState.ResetForNewGenerate();
				RenderTune.Reset();
				GenGhostPlace.Reset();
				LayerLoadLog.Abort();
				Plugin.Log.LogInfo("generatingWorld rose again - capture/stream cleared.");
			}
			LayerLoadLog.Begin();
			return;
		}

		if (!world.worldExists || CaptureStore.Captured)
			return;

		LayerLoadLog.Begin();
		LayerLoadLog.MarkGenDone();

		if (world.biomeOverride == WorldGeneration.OverrideSceneType.Tutorial)
		{
			Plugin.Log.LogInfo("tutorial layer - capture skipped.");
			// stream never starts, but pod interiors still cull on their own
			StructureTilemapVault.WakeAll(world);
			LayerLoadLog.Finish("tutorial");
			return;
		}

		// capture keeps the ghost rows. the stream then drops everything outside the window.
		// a client uses her own window. NetHold refuses to destroy a Together replica.
		bool wantStream = Plugin.StreamEnabled != null && Plugin.StreamEnabled.Value;
		bool stream = wantStream && MpSession.AllowStream;

		if (wantStream)
		{
			// inline Instantiates (ropes, bandages, remote traders, mini-barrels) go into the arrays, then get destroyed
			LayerLoadLog.Span("ghost", () =>
			{
				GenGhostPlace.CullLiveOutsideWindow(world);
				GenGhostPlace.LogSummary();
			});
			LayerLoadLog.Span("capture", () => WorldScanner.Capture(world, mergeLiveOnly: CaptureStore.GhostFilled));
			// corpse and structure pockets land in WorldItems once. later Start and loot markers stay off.
			LayerLoadLog.Span("bake-loot", () => StructureLiveState.BakeLayerLoot(world));
		}
		else
		{
			LayerLoadLog.Span("capture", () => WorldScanner.Capture(world));
		}

		SanityLog.LogObjectCapture();

		if (stream)
		{
			StreamController.ActivateBootstrap(world);
		}
		else
		{
			CaptureStore.FramesUntilTileCapture = 90;
			LayerLoadLog.Finish(wantStream ? "client" : "stream-off");
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
