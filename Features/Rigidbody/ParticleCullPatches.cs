using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization.Features.ChunkSim;

internal static class ParticleCullPatches
{
	internal static void Apply(Harmony harmony)
	{
		MethodInfo playBool = AccessTools.Method(typeof(ParticleSystem), "Play", new[] { typeof(bool) });
		if (playBool != null)
			harmony.Patch(playBool, postfix: new HarmonyMethod(typeof(ParticleCullPatches), nameof(RegisterPostfix)));
		else
			CpuLog.Warn("[CPUOpt] particle Play(bool) patch skipped - method not found");

		MethodInfo playNone = AccessTools.Method(typeof(ParticleSystem), "Play", System.Type.EmptyTypes);
		if (playNone != null)
			harmony.Patch(playNone, postfix: new HarmonyMethod(typeof(ParticleCullPatches), nameof(RegisterPostfix)));
	}

	private static void RegisterPostfix(ParticleSystem __instance)
	{
		if (__instance)
			ParticleCullRegistry.Register(__instance);
	}
}
