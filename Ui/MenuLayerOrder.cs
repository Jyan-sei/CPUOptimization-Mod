using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CPUOptimization2;

/// <summary>
/// qol draws the layer photo on a child of the continue button. a child always
/// paints after the button frame and before the label, so the photo covers the
/// frame. the frame has to be a later sibling than the photo.
/// </summary>
internal static class MenuLayerOrder
{
	const string FaceName = "ContinueFace";
	const string HeldCanvas = "CPUOpt2HeldCanvas";

	static FieldInfo _imageField;
	static bool _logged;
	static readonly List<GameObject> _heldCanvases = new List<GameObject>();

	internal static void Apply(Harmony harmony)
	{
		Type follower = AccessTools.TypeByName("QoL_Unknown.ContinueButtonCropFollower");
		if (follower != null)
		{
			_imageField = AccessTools.Field(follower, "image");
			MethodInfo late = AccessTools.Method(follower, "LateUpdate");
			if (_imageField != null && late != null)
				harmony.Patch(late, postfix: new HarmonyMethod(typeof(MenuLayerOrder), nameof(AfterCrop)));
		}

		// GameObject.Find("Canvas") returns the first active match. a canvas left
		// over from the run has that name and no RunSettings, so the selectors are skipped.
		Type menu = AccessTools.TypeByName("QoL_Unknown.SaveMenuPatcher");
		MethodInfo create = menu == null
			? null
			: AccessTools.Method(menu, "CreateQuickLoadButtons", new[] { typeof(PreRunScript) });
		if (create == null)
			return;
		harmony.Patch(
			create,
			prefix: new HarmonyMethod(typeof(MenuLayerOrder), nameof(HoldStrayCanvases)),
			postfix: new HarmonyMethod(typeof(MenuLayerOrder), nameof(ReleaseStrayCanvases)));
	}

	static void HoldStrayCanvases()
	{
		_heldCanvases.Clear();
		Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
		for (int i = 0; i < canvases.Length; i++)
		{
			Canvas canvas = canvases[i];
			if (canvas == null)
				continue;
			GameObject go = canvas.gameObject;
			if (go.name != "Canvas")
				continue;
			if (go.transform.Find("RunSettings") != null)
				continue;
			go.name = HeldCanvas;
			_heldCanvases.Add(go);
		}
	}

	static void ReleaseStrayCanvases()
	{
		for (int i = 0; i < _heldCanvases.Count; i++)
		{
			GameObject go = _heldCanvases[i];
			if (go != null && go.name == HeldCanvas)
				go.name = "Canvas";
		}
		_heldCanvases.Clear();
	}

	static void AfterCrop(Component __instance)
	{
		if (__instance == null || _imageField == null)
			return;
		Image layer = _imageField.GetValue(__instance) as Image;
		if (layer == null)
			return;
		Button button = layer.GetComponentInParent<Button>();
		if (button == null || layer.transform.parent != button.transform)
			return;
		Image rootImage = button.GetComponent<Image>();
		if (rootImage == null || rootImage == layer)
			return;

		Image face = Face(button, rootImage);
		if (face == null)
			return;
		if (layer.transform.GetSiblingIndex() != 0)
			layer.transform.SetAsFirstSibling();
		if (face.transform.GetSiblingIndex() != 1)
			face.transform.SetSiblingIndex(1);

		int faceIndex = face.transform.GetSiblingIndex();
		bool textBehind = false;
		TMP_Text[] texts = button.GetComponentsInChildren<TMP_Text>(true);
		for (int i = 0; i < texts.Length; i++)
		{
			if (texts[i] != null && texts[i].transform.parent == button.transform
				&& texts[i].transform.GetSiblingIndex() < faceIndex)
			{
				textBehind = true;
				break;
			}
		}
		if (!textBehind)
			return;
		for (int i = 0; i < texts.Length; i++)
		{
			if (texts[i] != null && texts[i].transform.parent == button.transform)
				texts[i].transform.SetAsLastSibling();
		}
	}

	// the root image is the button hit target, and it paints before every child.
	// a copy of that frame sits above the photo. the root keeps the clicks.
	static Image Face(Button button, Image rootImage)
	{
		Transform existing = button.transform.Find(FaceName);
		if (existing != null)
			return existing.GetComponent<Image>();

		GameObject go = new GameObject(FaceName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
		go.transform.SetParent(button.transform, false);
		Image face = go.GetComponent<Image>();
		face.sprite = rootImage.sprite;
		face.type = rootImage.type;
		face.color = rootImage.color;
		face.material = rootImage.material;
		face.preserveAspect = rootImage.preserveAspect;
		face.fillCenter = rootImage.fillCenter;
		face.pixelsPerUnitMultiplier = rootImage.pixelsPerUnitMultiplier;
		face.raycastTarget = false;
		RectTransform rect = face.rectTransform;
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.offsetMin = Vector2.zero;
		rect.offsetMax = Vector2.zero;
		rect.pivot = new Vector2(0.5f, 0.5f);
		rootImage.sprite = null;
		Color kept = rootImage.color;
		rootImage.color = new Color(kept.r, kept.g, kept.b, 0f);
		button.targetGraphic = face;
		if (!_logged)
		{
			_logged = true;
			Plugin.Log.LogInfo("continue button layer image sits under the frame");
		}
		return face;
	}
}
