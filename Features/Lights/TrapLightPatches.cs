using HarmonyLib;

namespace CPUOptimization.Features.Lights;

[HarmonyPatch(typeof(JumpPadScript), "Start")]
internal static class JumpPadLightStartPatch
{
	static void Postfix(JumpPadScript __instance)
	{
		if (!LightOptBootstrap.ReplaceTraps)
			return;
		FxLightToEmission.Attach(__instance.gameObject, "jumppad", useEnabled: false);
	}
}

[HarmonyPatch(typeof(JumpPadScript), "Update")]
internal static class JumpPadLightUpdatePatch
{
	static void Postfix(JumpPadScript __instance)
	{
		if (!LightOptBootstrap.ReplaceTraps)
			return;
		__instance.GetComponent<FxLightToEmission>()?.Apply();
	}
}

[HarmonyPatch(typeof(CoilScript), "Awake")]
internal static class CoilLightAwakePatch
{
	static void Postfix(CoilScript __instance)
	{
		if (!LightOptBootstrap.ReplaceTraps)
			return;
		FxLightToEmission.Attach(__instance.gameObject, "coil", useEnabled: false);
	}
}

[HarmonyPatch(typeof(CoilScript), "Update")]
internal static class CoilLightUpdatePatch
{
	static void Postfix(CoilScript __instance)
	{
		if (!LightOptBootstrap.ReplaceTraps)
			return;
		__instance.GetComponent<FxLightToEmission>()?.Apply();
	}
}

[HarmonyPatch(typeof(TurretScript), "Start")]
internal static class TurretLightStartPatch
{
	static void Postfix(TurretScript __instance)
	{
		if (!LightOptBootstrap.ReplaceTraps)
			return;
		FxLightToEmission.Attach(__instance.gameObject, "turret", useEnabled: false);
	}
}

[HarmonyPatch(typeof(TurretScript), "Update")]
internal static class TurretLightUpdatePatch
{
	static void Postfix(TurretScript __instance)
	{
		if (!LightOptBootstrap.ReplaceTraps)
			return;
		__instance.GetComponent<FxLightToEmission>()?.Apply();
	}
}

[HarmonyPatch(typeof(SpikeStabberScript), "Start")]
internal static class SidestabberLightStartPatch
{
	static void Postfix(SpikeStabberScript __instance)
	{
		if (!LightOptBootstrap.ReplaceTraps)
			return;
		if (!IsSidestabber(__instance))
			return;
		FxLightToEmission.Attach(__instance.gameObject, "sidestabber", useEnabled: true);
	}

	internal static bool IsSidestabber(SpikeStabberScript instance)
	{
		if (!instance)
			return false;
		string name = instance.gameObject.name;
		return name.StartsWith("sidestabber", System.StringComparison.OrdinalIgnoreCase);
	}
}

[HarmonyPatch(typeof(SpikeStabberScript), "OnWillRenderObject")]
internal static class SidestabberLightRenderPatch
{
	static void Postfix(SpikeStabberScript __instance)
	{
		if (!LightOptBootstrap.ReplaceTraps)
			return;
		if (!SidestabberLightStartPatch.IsSidestabber(__instance))
			return;
		__instance.GetComponent<FxLightToEmission>()?.Apply();
	}
}
