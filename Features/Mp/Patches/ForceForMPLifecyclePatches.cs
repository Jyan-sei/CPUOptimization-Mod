using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization.Features.Mp.Patches;

// register/unregister forceformp via addcomponent / enabled / destroy (no onenable on this unity build)
internal static class ForceForMPLifecyclePatches
{
	private static bool _applied;

	internal static void Apply(Harmony harmony)
	{
		if (_applied)
			return;

		MethodInfo addComponent = AccessTools.Method(typeof(GameObject), nameof(GameObject.AddComponent), new[] { typeof(Type) });
		if (addComponent != null)
		{
			harmony.Patch(
				addComponent,
				postfix: new HarmonyMethod(typeof(ForceForMPLifecyclePatches), nameof(AddComponentPostfix)));
		}

		MethodInfo setEnabled = AccessTools.PropertySetter(typeof(Behaviour), "enabled");
		if (setEnabled != null)
		{
			harmony.Patch(
				setEnabled,
				postfix: new HarmonyMethod(typeof(ForceForMPLifecyclePatches), nameof(EnabledSetterPostfix)));
		}

		harmony.Patch(
			AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy), new[] { typeof(UnityEngine.Object) }),
			prefix: new HarmonyMethod(typeof(ForceForMPLifecyclePatches), nameof(DestroyPrefix)));
		harmony.Patch(
			AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy), new[] { typeof(UnityEngine.Object), typeof(float) }),
			prefix: new HarmonyMethod(typeof(ForceForMPLifecyclePatches), nameof(DestroyPrefix)));

		_applied = true;
	}

	private static bool IsForceForMp(MonoBehaviour instance) =>
		instance && KrokMpReflect.OnWillRenderType != null
		&& instance.GetType() == KrokMpReflect.OnWillRenderType;

	static void AddComponentPostfix(Type __0, Component __result)
	{
		if (KrokMpReflect.OnWillRenderType == null || __0 != KrokMpReflect.OnWillRenderType)
			return;
		if (__result is MonoBehaviour mb)
			ForceForMPScheduler.Register(mb);
	}

	static void EnabledSetterPostfix(Behaviour __instance, bool value)
	{
		if (!value || __instance is not MonoBehaviour mb || !IsForceForMp(mb))
			return;
		ForceForMPScheduler.Register(mb);
	}

	static void DestroyPrefix(UnityEngine.Object obj)
	{
		if (!obj)
			return;

		if (obj is MonoBehaviour mb && IsForceForMp(mb))
		{
			ForceForMPScheduler.Unregister(mb);
			return;
		}

		GameObject go = obj as GameObject;
		if (!go && obj is Component component)
			go = component.gameObject;
		if (!go || KrokMpReflect.OnWillRenderType == null)
			return;

		Component force = go.GetComponent(KrokMpReflect.OnWillRenderType);
		if (force is MonoBehaviour forceMb)
			ForceForMPScheduler.Unregister(forceMb);
	}
}
