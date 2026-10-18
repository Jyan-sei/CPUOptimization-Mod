using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CPUOptimization2;

/// <summary>
/// HUD scripts assign the same sprite, color, position, and scale every frame.
/// those setters dirty canvases even when the value did not change.
/// </summary>
internal static class HudSettle
{
	private static readonly Dictionary<int, RectTransform> MoodleRects = new Dictionary<int, RectTransform>();
	private static readonly AccessTools.FieldRef<Moodle, Image> MoodleImg =
		AccessTools.FieldRefAccess<Moodle, Image>("img");
	private static readonly AccessTools.FieldRef<Moodle, Image> MoodleImg2 =
		AccessTools.FieldRefAccess<Moodle, Image>("img2");
	private static readonly AccessTools.FieldRef<AltHoverScript, bool> AltActive =
		AccessTools.FieldRefAccess<AltHoverScript, bool>("active");
	private static readonly AccessTools.FieldRef<AltHoverScript, List<ItemLabel>> AltSpawned =
		AccessTools.FieldRefAccess<AltHoverScript, List<ItemLabel>>("spawnedLabels");
	private static readonly AccessTools.FieldRef<AltHoverScript, List<ItemLabel>> AltShown =
		AccessTools.FieldRefAccess<AltHoverScript, List<ItemLabel>>("activeLabels");
	private static bool _altHidden;

	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(
			AccessTools.Method(typeof(PlayerCamera), "HandleHealthPanelButtonSprite"),
			prefix: new HarmonyMethod(typeof(HudSettle), nameof(HealthSprite)));
		harmony.Patch(
			AccessTools.Method(typeof(GlobalDark), "HandleTooltip"),
			prefix: new HarmonyMethod(typeof(HudSettle), nameof(Tooltip)));
		harmony.Patch(
			AccessTools.Method(typeof(Moodle), "Update"),
			prefix: new HarmonyMethod(typeof(HudSettle), nameof(MoodleUpdate)));
		harmony.Patch(
			AccessTools.Method(typeof(AltHoverScript), "Update"),
			prefix: new HarmonyMethod(typeof(HudSettle), nameof(AltLabels)));
	}

	private static bool HealthSprite(PlayerCamera __instance)
	{
		if (__instance == null || __instance.woundButton == null)
			return true;

		// the stock method assigns this twice. the second write is the one that sticks.
		bool flash = __instance.woundView != null
			&& !__instance.woundView.activeSelf
			&& __instance.body != null
			&& __instance.body.shouldCheckHealthPanel
			&& Mathf.PingPong(Time.unscaledTime, 0.3f) > 0.15f
			&& !WorldGeneration.unchipped;
		Sprite want = flash ? __instance.woundButtonFlash : __instance.woundButtonNormal;
		if (__instance.woundButton.sprite != want)
			__instance.woundButton.sprite = want;
		return false;
	}

	private static bool Tooltip(GlobalDark __instance)
	{
		if (__instance == null || __instance.tooltipRect == null)
			return true;

		// world hovers call SetTooltip before this Update. ui hovers are filled
		// by the raycast inside the original. either one has to run it.
		if (!string.IsNullOrEmpty(__instance.tooltipText.name))
			return true;
		(string name, string _) hit = UIUtil.GetUITooltip(UIUtil.GetEventSystemRaycastResults());
		if (!string.IsNullOrEmpty(hit.name))
			return true;

		GameObject go = __instance.tooltipRect.gameObject;
		if (go.activeSelf)
			go.SetActive(false);
		Vector3 hidden = new Vector3(0.1f, 0.1f, 1f);
		if ((__instance.tooltipRect.localScale - hidden).sqrMagnitude > 0.0001f)
			__instance.tooltipRect.localScale = hidden;
		return false;
	}

	private static bool MoodleUpdate(Moodle __instance)
	{
		if (__instance == null)
			return true;

		Image img = MoodleImg(__instance);
		Image img2 = MoodleImg2(__instance);
		if (__instance.isSide)
		{
			bool show = MoodleManager.showSideMoodles;
			SetEnabled(img, show);
			SetEnabled(img2, show);
			SetEnabled(__instance.flash, show);
		}

		if (__instance.unTransparentTime > 0f)
			__instance.unTransparentTime -= Time.deltaTime;
		if (__instance.unTransparentTime < 0f)
			__instance.unTransparentTime = 0f;

		RectTransform rt = MoodleRect(__instance);
		if (rt != null)
		{
			Vector2 scale = rt.localScale;
			Vector2 nextScale = Vector2.Lerp(scale, Vector2.one, Time.unscaledDeltaTime * 12f);
			if ((nextScale - scale).sqrMagnitude > 0.0000001f)
				rt.localScale = nextScale;

			float yTarget = __instance.doWarningFlash ? Mathf.Sin(Time.unscaledTime * 6f) * 4f : 0f;
			Vector2 pos = rt.anchoredPosition;
			float nextY = Mathf.Lerp(pos.y, yTarget, Time.unscaledDeltaTime * 12f);
			if (Mathf.Abs(nextY - pos.y) > 0.01f)
				rt.anchoredPosition = new Vector2(pos.x, nextY);
		}

		if (__instance.doWarningFlash && __instance.flash != null)
		{
			Color flash = new Color(1f, 1f, 1f, Mathf.PingPong(Time.unscaledTime * 2.5f, 1f));
			if (__instance.flash.color != flash)
				__instance.flash.color = flash;
		}

		PlayerCamera player = PlayerCamera.main;
		float black = player != null && player.blackAmount < PlayerCamera.GetUnconsciousBlack() ? 1f : 0f;
		SetColor(img, new Color(1f, 1f, 1f, black - __instance.unTransparentTime * 2f));
		SetColor(img2, new Color(1f, 1f, 1f, 1f - __instance.unTransparentTime * 2f));
		return false;
	}

	private static bool AltLabels(AltHoverScript __instance)
	{
		if (__instance == null)
			return true;

		// labels were deactivated every frame after alt view closed.
		KeyCode bind = KeyBinds.GetBind("altview");
		bool turningOn = AltHoverScript.toggleActivated && Input.GetKeyDown(bind);
		bool on = AltHoverScript.toggleActivated ? (AltActive(__instance) || turningOn) : Input.GetKey(bind);
		if (on)
		{
			_altHidden = false;
			return true;
		}
		if (_altHidden)
			return false;

		AltActive(__instance) = false;
		List<ItemLabel> labels = AltSpawned(__instance);
		if (labels != null)
		{
			for (int i = 0; i < labels.Count; i++)
			{
				ItemLabel label = labels[i];
				if (label != null && label.gameObject.activeSelf)
					label.gameObject.SetActive(false);
			}
		}
		List<ItemLabel> shown = AltShown(__instance);
		if (shown != null && shown.Count > 0)
			shown.Clear();
		_altHidden = true;
		return false;
	}

	private static RectTransform MoodleRect(Moodle moodle)
	{
		int id = moodle.GetInstanceID();
		if (MoodleRects.TryGetValue(id, out RectTransform rt) && rt != null)
			return rt;
		rt = moodle.GetComponent<RectTransform>();
		MoodleRects[id] = rt;
		return rt;
	}

	private static void SetEnabled(Behaviour behaviour, bool on)
	{
		if (behaviour != null && behaviour.enabled != on)
			behaviour.enabled = on;
	}

	private static void SetColor(Graphic graphic, Color color)
	{
		if (graphic != null && graphic.color != color)
			graphic.color = color;
	}
}
