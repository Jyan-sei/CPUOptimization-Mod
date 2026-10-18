using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CPUOptimization2;

/// <summary>
/// PlayerCamera keeps writing the follow and a couple of canvases after they have settled.
/// skip the follow once the body, look, and zoom are still and the lerp has arrived.
/// skip scale and world-canvas position writes when the value is already there.
/// </summary>
internal static class CameraSettle
{
	private static Vector3 _anchor;
	private static Vector3 _mouse;
	private static float _ortho = float.NaN;
	private static bool _look;
	private static Vector3 _lastCam;
	private static bool _haveCam;

	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(
			AccessTools.Method(typeof(PlayerCamera), "HandleCameraPosition"),
			prefix: new HarmonyMethod(typeof(CameraSettle), nameof(SkipSettledFollow)));
		harmony.Patch(
			AccessTools.Method(typeof(PlayerCamera), "LateUpdate"),
			transpiler: new HarmonyMethod(typeof(CameraSettle), nameof(QuietLateWrites)));
	}

	private static bool SkipSettledFollow(PlayerCamera __instance)
	{
		if (__instance == null || __instance.body == null)
			return true;
		if (__instance.isFreecam)
			return true;
		if (__instance.shaker != null && __instance.shaker.pos.sqrMagnitude > 0.0001f)
			return true;

		Transform anchor = __instance.following != null ? __instance.following : __instance.body.transform;
		Vector3 anchorPos = anchor != null ? anchor.position : Vector3.zero;
		Vector3 mouse = Input.mousePosition;
		bool look = LookOffsetActive(__instance);
		Camera cam = __instance.GetComponent<Camera>();
		float ortho = cam != null ? cam.orthographicSize : 0f;

		bool anchorMoved = (_anchor - anchorPos).sqrMagnitude > 0.000001f;
		bool mouseMoved = look && (_mouse - mouse).sqrMagnitude > 0.25f;
		bool zoomMoved = float.IsNaN(_ortho) || Mathf.Abs(_ortho - ortho) > 0.001f;
		bool lookChanged = look != _look;
		_anchor = anchorPos;
		_mouse = mouse;
		_ortho = ortho;
		_look = look;

		Vector3 camPos = __instance.transform.position;
		bool camMoving = _haveCam && (camPos - _lastCam).sqrMagnitude > 0.000001f;
		_lastCam = camPos;
		_haveCam = true;

		if (anchorMoved || mouseMoved || zoomMoved || lookChanged || camMoving)
			return true;
		return false;
	}

	private static bool LookOffsetActive(PlayerCamera cam)
	{
		if (cam.body == null || !cam.body.conscious)
			return false;
		if (cam.craftingPanel != null && cam.craftingPanel.activeSelf)
			return false;
		if (cam.woundView != null && cam.woundView.activeSelf)
			return false;
		MinigameBase mini = MinigameBase.main;
		if (mini != null && mini.currentMinigame != null)
			return false;
		return true;
	}

	private static void SetLocalScaleIfChanged(Transform transform, Vector3 value)
	{
		if (transform == null)
			return;
		if ((transform.localScale - value).sqrMagnitude < 0.0000001f)
			return;
		transform.localScale = value;
	}

	private static void SetPositionIfChanged(Transform transform, Vector3 value)
	{
		if (transform == null)
			return;
		if ((transform.position - value).sqrMagnitude < 0.0000001f)
			return;
		transform.position = value;
	}

	private static void SetColorIfChanged(Graphic graphic, Color value)
	{
		if (graphic == null || graphic.color == value)
			return;
		graphic.color = value;
	}

	private static IEnumerable<CodeInstruction> QuietLateWrites(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo scaleSetter = AccessTools.PropertySetter(typeof(Transform), nameof(Transform.localScale));
		MethodInfo posSetter = AccessTools.PropertySetter(typeof(Transform), nameof(Transform.position));
		MethodInfo colorSetter = AccessTools.PropertySetter(typeof(Graphic), nameof(Graphic.color));
		MethodInfo scaleCall = AccessTools.Method(typeof(CameraSettle), nameof(SetLocalScaleIfChanged));
		MethodInfo posCall = AccessTools.Method(typeof(CameraSettle), nameof(SetPositionIfChanged));
		MethodInfo colorCall = AccessTools.Method(typeof(CameraSettle), nameof(SetColorIfChanged));
		foreach (CodeInstruction ins in instructions)
		{
			MethodInfo swap = null;
			if (ins.opcode == OpCodes.Callvirt && Equals(ins.operand, scaleSetter))
				swap = scaleCall;
			else if (ins.opcode == OpCodes.Callvirt && Equals(ins.operand, posSetter))
				swap = posCall;
			else if (ins.opcode == OpCodes.Callvirt && Equals(ins.operand, colorSetter))
				swap = colorCall;
			if (swap != null)
			{
				// pin color is the target of br.s. the branch operand points at this
				// instruction object. a clone is a different object, so the label never gets marked.
				ins.opcode = OpCodes.Call;
				ins.operand = swap;
			}
			yield return ins;
		}
	}
}
