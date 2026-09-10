using UnityEngine;

namespace CPUOptimization.Features.Lights;

internal static class CameraViewMetrics
{
	internal static bool TryGetCullRadii(out float onRadius, out float offRadius)
	{
		onRadius = Plugin.LightsCullRadius?.Value ?? 72f;
		offRadius = onRadius + (Plugin.LightsCullHysteresis?.Value ?? 24f);

		if (Plugin.LightsCullUseCameraView != null && !Plugin.LightsCullUseCameraView.Value)
			return false;

		Camera cam = null;
		if (PlayerCamera.main)
			cam = PlayerCamera.main.GetComponent<Camera>();
		if (!cam)
			cam = Camera.main;
		if (!cam || !cam.orthographic)
			return false;

		float margin = Plugin.LightsCullCameraMargin?.Value ?? 1.1f;
		if (margin < 1f)
			margin = 1f;

		float halfH = cam.orthographicSize;
		float halfW = halfH * cam.aspect;
		float cornerDist = Mathf.Sqrt(halfW * halfW + halfH * halfH);
		onRadius = cornerDist * margin;

		float configOn = Plugin.LightsCullRadius?.Value ?? 72f;
		if (onRadius < configOn * 0.25f)
			onRadius = configOn * 0.25f;

		float hysteresis = Plugin.LightsCullHysteresis?.Value ?? 24f;
		if (configOn > 0.01f)
			hysteresis *= onRadius / configOn;
		offRadius = onRadius + hysteresis;
		return true;
	}

	internal static bool TryGetVisibleHalfExtents(out float halfWidth, out float halfHeight)
	{
		halfWidth = 0f;
		halfHeight = 0f;

		Camera cam = PlayerCamera.main
			? PlayerCamera.main.GetComponent<Camera>()
			: Camera.main;
		if (!cam || !cam.orthographic)
			return false;

		halfHeight = cam.orthographicSize;
		halfWidth = halfHeight * cam.aspect;
		return true;
	}
}
