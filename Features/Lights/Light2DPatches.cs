using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CPUOptimization.Features.Lights;

[HarmonyPatch(typeof(Light2D), "OnEnable")]
internal static class Light2DEnablePatch
{
	static void Postfix(Light2D __instance)
	{
		if (Plugin.LightsEnabled == null || !Plugin.LightsEnabled.Value)
			return;
		LightCullRegistry.Register(__instance);
	}
}

// unity calls ondestroy natively - no managed ondestroy to patch. unregister before object.destroy
internal static class Light2DDestroyPatch
{
	internal static void UnregisterFromObject(UnityEngine.Object obj)
	{
		if (!obj)
			return;

		if (obj is Light2D light)
		{
			LightCullRegistry.Unregister(light);
			return;
		}

		GameObject go = obj as GameObject;
		if (!go && obj is Component component)
			go = component.gameObject;

		if (!go)
			return;

		Light2D[] lights = go.GetComponentsInChildren<Light2D>(true);
		for (int i = 0; i < lights.Length; i++)
			LightCullRegistry.Unregister(lights[i]);
	}
}

[HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy), new[] { typeof(UnityEngine.Object) })]
internal static class Light2DDestroyObjectPatch
{
	static void Prefix(UnityEngine.Object obj) => Light2DDestroyPatch.UnregisterFromObject(obj);
}

[HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy), new[] { typeof(UnityEngine.Object), typeof(float) })]
internal static class Light2DDestroyDelayedPatch
{
	static void Prefix(UnityEngine.Object obj) => Light2DDestroyPatch.UnregisterFromObject(obj);
}
