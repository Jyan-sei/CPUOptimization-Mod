using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CPUOptimization2;

/// <summary>
/// the main overlay canvas keeps full-screen images enabled at alpha 0.
/// those still go into Canvas.Render. cull the renderer while it is invisible,
/// and put it back when the alpha returns.
/// </summary>
internal static class CanvasCull
{
	private const float Hidden = 0.004f;

	private sealed class Slot
	{
		public Graphic Graphic;
		public CanvasRenderer Renderer;
		public bool Skip;
	}

	private static readonly List<Slot> Slots = new List<Slot>();
	private static int _canvasId;
	private static float _refreshAt;

	internal static void Reset()
	{
		Slots.Clear();
		_canvasId = 0;
		_refreshAt = 0f;
	}

	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(
			AccessTools.Method(typeof(PlayerCamera), "LateUpdate"),
			postfix: new HarmonyMethod(typeof(CanvasCull), nameof(AfterLate)));
	}

	private static void AfterLate(PlayerCamera __instance)
	{
		if (__instance == null || __instance.mainCanvas == null)
			return;

		Canvas canvas = __instance.mainCanvas;
		int id = canvas.GetInstanceID();
		if (id != _canvasId || Time.unscaledTime >= _refreshAt)
		{
			Rebuild(canvas);
			_canvasId = id;
			_refreshAt = Time.unscaledTime + 1f;
		}

		for (int i = 0; i < Slots.Count; i++)
		{
			Slot slot = Slots[i];
			if (slot.Skip || slot.Graphic == null || slot.Renderer == null)
				continue;
			bool hide = slot.Renderer.GetAlpha() <= Hidden;
			if (slot.Renderer.cull != hide)
				slot.Renderer.cull = hide;
		}
	}

	private static void Rebuild(Canvas canvas)
	{
		Slots.Clear();
		Graphic[] graphics = canvas.GetComponentsInChildren<Graphic>(false);
		for (int i = 0; i < graphics.Length; i++)
		{
			Graphic graphic = graphics[i];
			if (graphic == null)
				continue;
			Slots.Add(new Slot
			{
				Graphic = graphic,
				Renderer = graphic.canvasRenderer,
				Skip = graphic.GetComponent<Mask>() != null || graphic.GetComponent<RectMask2D>() != null,
			});
		}
	}
}
