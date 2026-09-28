using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CPUOptimization2;

/// <summary>
/// cheapen urp / light2d without yanking the lights.
/// off unless RenderTune.Enabled. Apply/Restore is the live Super Potato toggle.
/// </summary>
internal static class RenderTune
{
	private static bool _potatoApplied;
	private static bool _stockCaptured;
	private static int _multiplyStyleIndex = -1;
	private static float _rescanAge;

	private static FieldInfo _lightRtScaleField;
	private static FieldInfo _useSortingTexField;
	private static FieldInfo _maxLightRtField;
	private static FieldInfo _maxShadowRtField;
	private static FieldInfo _normalQualityField;
	private static FieldInfo _blendModeField;
	private static bool _fieldsReady;

	private static int _stockMsaa = 1;
	private static bool _stockDepth;
	private static bool _stockOpaque;
	private static float _stockLightRt = 0.5f;
	private static bool _stockSortingTex;
	private static uint _stockMaxLightRt = 16u;
	private static uint _stockMaxShadowRt = 1u;
	private static bool _stockCamAllowMsaa = true;
	private static AntialiasingMode _stockAa = AntialiasingMode.None;
	private static CameraOverrideOption _stockReqDepth = CameraOverrideOption.UsePipelineSettings;
	private static CameraOverrideOption _stockReqColor = CameraOverrideOption.UsePipelineSettings;
	private static readonly List<ScriptableRendererFeature> _disabledFeatures = new List<ScriptableRendererFeature>();
	private static int _stockMultiplyBlendMode = -1;
	private static int _stockMultiplyStyleIndex = -1;

	private struct LightSnap
	{
		public int BlendStyleIndex;
		public float VolumeIntensity;
		public bool VolumeIntensityEnabled;
		public bool VolumetricShadowsEnabled;
		public int NormalQuality;
	}

	private static readonly Dictionary<int, LightSnap> _lightSnaps = new Dictionary<int, LightSnap>();

	internal static bool IsApplied => _potatoApplied;

	internal static void ResetForNewGenerate()
	{
		// new gen. drop per-light snaps. urp stock snapshot stays if potato is on
		_lightSnaps.Clear();
		_rescanAge = 0f;
	}

	/// <summary>old name, Plugin gen wipe still calls this</summary>
	internal static void Reset() => ResetForNewGenerate();

	internal static void Tick(float dt)
	{
		if (Plugin.RenderTuneEnabled == null || !Plugin.RenderTuneEnabled.Value)
		{
			if (_potatoApplied)
				Restore();
			return;
		}

		if (!_potatoApplied)
			Apply();

		if (Plugin.RenderTuneLights == null || !Plugin.RenderTuneLights.Value)
			return;

		_rescanAge += dt;
		float interval = Plugin.RenderTuneLightRescanSeconds != null
			? Plugin.RenderTuneLightRescanSeconds.Value
			: 2.5f;
		if (_rescanAge < interval)
			return;
		_rescanAge = 0f;
		TuneVisibleLights();
	}

	internal static void SetEnabled(bool on)
	{
		if (Plugin.RenderTuneEnabled != null)
			Plugin.RenderTuneEnabled.Value = on;
		if (on)
			Apply();
		else
			Restore();
	}

	internal static void ApplyHarmony(HarmonyLib.Harmony harmony)
	{
		try
		{
			harmony.Patch(
				HarmonyLib.AccessTools.Method(typeof(Light2D), "OnEnable"),
				postfix: new HarmonyLib.HarmonyMethod(typeof(RenderTune), nameof(LightOnEnablePostfix)));
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"RenderTune Light2D.OnEnable patch skipped: {ex.Message}");
		}
	}

	private static void LightOnEnablePostfix(Light2D __instance)
	{
		if (Plugin.RenderTuneEnabled == null || !Plugin.RenderTuneEnabled.Value)
			return;
		if (Plugin.RenderTuneLights == null || !Plugin.RenderTuneLights.Value)
			return;
		if (!_potatoApplied)
			return;
		TuneOne(__instance);
	}

	private static void EnsureFields()
	{
		if (_fieldsReady)
			return;
		_fieldsReady = true;
		Type r2d = typeof(Renderer2DData);
		_lightRtScaleField = r2d.GetField("m_LightRenderTextureScale", BindingFlags.Instance | BindingFlags.NonPublic);
		_useSortingTexField = r2d.GetField("m_UseCameraSortingLayersTexture", BindingFlags.Instance | BindingFlags.NonPublic);
		_maxLightRtField = r2d.GetField("m_MaxLightRenderTextureCount", BindingFlags.Instance | BindingFlags.NonPublic);
		_maxShadowRtField = r2d.GetField("m_MaxShadowRenderTextureCount", BindingFlags.Instance | BindingFlags.NonPublic);
		_normalQualityField = typeof(Light2D).GetField("m_NormalMapQuality", BindingFlags.Instance | BindingFlags.NonPublic);
		_blendModeField = typeof(Light2DBlendStyle).GetField("blendMode", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
	}

	internal static void Apply()
	{
		if (_potatoApplied)
			return;
		EnsureFields();
		try
		{
			UniversalRenderPipelineAsset asset = GetUrpAsset();
			if (asset == null)
			{
				Plugin.Log.LogWarning("RenderTune Apply — no URP asset yet");
				return;
			}

			CaptureStockIfNeeded(asset);

			if (Plugin.RenderTuneStripUrp == null || Plugin.RenderTuneStripUrp.Value)
			{
				asset.msaaSampleCount = 1;
				asset.supportsCameraDepthTexture = false;
				asset.supportsCameraOpaqueTexture = false;

				Renderer2DData r2d = FindRenderer2D(asset);
				if (r2d != null)
				{
					if (_lightRtScaleField != null)
					{
						float target = Plugin.RenderTuneLightRtScale != null
							? Plugin.RenderTuneLightRtScale.Value
							: 0.35f;
						_lightRtScaleField.SetValue(r2d, Mathf.Clamp(target, 0.15f, 1f));
					}
					if (_useSortingTexField != null)
						_useSortingTexField.SetValue(r2d, false);
					if (_maxLightRtField != null)
					{
						uint cap = Plugin.RenderTuneMaxLightRt != null
							? (uint)Mathf.Clamp(Plugin.RenderTuneMaxLightRt.Value, 4, 16)
							: 8u;
						_maxLightRtField.SetValue(r2d, cap);
					}
					if (_maxShadowRtField != null)
						_maxShadowRtField.SetValue(r2d, 1u);

					_multiplyStyleIndex = PreferMultiplyStyle(r2d);
					DisableHeavyRendererFeatures(r2d);
				}

				StripMainCameraExtras();
			}
			else
			{
				Renderer2DData r2d = FindRenderer2D(asset);
				if (r2d != null)
					_multiplyStyleIndex = FindMultiplyStyleIndex(r2d);
			}

			_potatoApplied = true;

			if (Plugin.RenderTuneLights != null && Plugin.RenderTuneLights.Value)
				TuneVisibleLights();

			Plugin.Log.LogInfo("RenderTune Apply — potato URP/lights on");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"RenderTune Apply failed: {ex.Message}");
		}
	}

	internal static void Restore()
	{
		if (!_potatoApplied && !_stockCaptured)
		{
			_potatoApplied = false;
			return;
		}

		EnsureFields();
		try
		{
			RestoreLights();

			UniversalRenderPipelineAsset asset = GetUrpAsset();
			if (asset != null && _stockCaptured)
			{
				asset.msaaSampleCount = _stockMsaa;
				asset.supportsCameraDepthTexture = _stockDepth;
				asset.supportsCameraOpaqueTexture = _stockOpaque;

				Renderer2DData r2d = FindRenderer2D(asset);
				if (r2d != null)
				{
					if (_lightRtScaleField != null)
						_lightRtScaleField.SetValue(r2d, _stockLightRt);
					if (_useSortingTexField != null)
						_useSortingTexField.SetValue(r2d, _stockSortingTex);
					if (_maxLightRtField != null)
						_maxLightRtField.SetValue(r2d, _stockMaxLightRt);
					if (_maxShadowRtField != null)
						_maxShadowRtField.SetValue(r2d, _stockMaxShadowRt);

					RestoreMultiplyStyle(r2d);
					RestoreRendererFeatures();
				}

				RestoreMainCameraExtras();
			}

			_potatoApplied = false;
			_lightSnaps.Clear();
			_multiplyStyleIndex = -1;
			Plugin.Log.LogInfo("RenderTune Restore — stock URP/lights");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"RenderTune Restore failed: {ex.Message}");
			_potatoApplied = false;
		}
	}

	private static void CaptureStockIfNeeded(UniversalRenderPipelineAsset asset)
	{
		if (_stockCaptured)
			return;

		_stockMsaa = asset.msaaSampleCount;
		_stockDepth = asset.supportsCameraDepthTexture;
		_stockOpaque = asset.supportsCameraOpaqueTexture;

		Renderer2DData r2d = FindRenderer2D(asset);
		if (r2d != null)
		{
			if (_lightRtScaleField != null)
				_stockLightRt = (float)_lightRtScaleField.GetValue(r2d);
			if (_useSortingTexField != null)
				_stockSortingTex = (bool)_useSortingTexField.GetValue(r2d);
			if (_maxLightRtField != null)
				_stockMaxLightRt = (uint)_maxLightRtField.GetValue(r2d);
			if (_maxShadowRtField != null)
				_stockMaxShadowRt = (uint)_maxShadowRtField.GetValue(r2d);

			_stockMultiplyStyleIndex = FindMultiplyStyleIndex(r2d);
			if (_stockMultiplyStyleIndex >= 0 && _blendModeField != null)
			{
				Light2DBlendStyle[] styles = r2d.lightBlendStyles;
				if (styles != null && _stockMultiplyStyleIndex < styles.Length)
				{
					object modeObj = _blendModeField.GetValue(styles[_stockMultiplyStyleIndex]);
					_stockMultiplyBlendMode = modeObj != null ? Convert.ToInt32(modeObj) : -1;
				}
			}
		}

		Camera cam = ResolveCamera();
		if (cam != null)
		{
			_stockCamAllowMsaa = cam.allowMSAA;
			var additional = cam.GetComponent<UniversalAdditionalCameraData>();
			if (additional != null)
			{
				try
				{
					_stockAa = additional.antialiasing;
					_stockReqDepth = additional.requiresDepthOption;
					_stockReqColor = additional.requiresColorOption;
				}
				catch
				{
					// ignore
				}
			}
		}

		_stockCaptured = true;
	}

	private static int PreferMultiplyStyle(Renderer2DData r2d)
	{
		int idx = FindMultiplyStyleIndex(r2d);
		if (idx < 0 || _blendModeField == null)
			return idx < 0 ? 0 : idx;

		Light2DBlendStyle[] styles = r2d.lightBlendStyles;
		object cur = _blendModeField.GetValue(styles[idx]);
		if (cur != null && Convert.ToInt32(cur) != 1)
		{
			object multiplyEnum = Enum.ToObject(_blendModeField.FieldType, 1);
			object boxed = styles[idx];
			_blendModeField.SetValue(boxed, multiplyEnum);
			styles[idx] = (Light2DBlendStyle)boxed;
		}
		return idx;
	}

	private static void RestoreMultiplyStyle(Renderer2DData r2d)
	{
		if (_stockMultiplyStyleIndex < 0 || _stockMultiplyBlendMode < 0 || _blendModeField == null)
			return;
		Light2DBlendStyle[] styles = r2d.lightBlendStyles;
		if (styles == null || _stockMultiplyStyleIndex >= styles.Length)
			return;
		object modeEnum = Enum.ToObject(_blendModeField.FieldType, _stockMultiplyBlendMode);
		object boxed = styles[_stockMultiplyStyleIndex];
		_blendModeField.SetValue(boxed, modeEnum);
		styles[_stockMultiplyStyleIndex] = (Light2DBlendStyle)boxed;
	}

	private static int FindMultiplyStyleIndex(Renderer2DData r2d)
	{
		Light2DBlendStyle[] styles = r2d.lightBlendStyles;
		if (styles == null || styles.Length == 0 || _blendModeField == null)
			return 0;

		for (int i = 0; i < styles.Length; i++)
		{
			object modeObj = _blendModeField.GetValue(styles[i]);
			int mode = modeObj != null ? Convert.ToInt32(modeObj) : -1;
			bool isMultiply = mode == 1
				|| (!string.IsNullOrEmpty(styles[i].name)
					&& styles[i].name.IndexOf("Multiply", StringComparison.OrdinalIgnoreCase) >= 0);
			if (isMultiply)
				return i;
		}
		return 0;
	}

	private static void DisableHeavyRendererFeatures(Renderer2DData r2d)
	{
		_disabledFeatures.Clear();
		try
		{
			PropertyInfo prop = typeof(ScriptableRendererData).GetProperty(
				"rendererFeatures",
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (prop == null)
				return;
			object listObj = prop.GetValue(r2d);
			if (!(listObj is System.Collections.IList list))
				return;

			for (int i = 0; i < list.Count; i++)
			{
				if (!(list[i] is ScriptableRendererFeature feature) || feature == null)
					continue;
				string n = feature.name ?? feature.GetType().Name;
				bool heavy =
					n.IndexOf("Debug", StringComparison.OrdinalIgnoreCase) >= 0
					|| (n.IndexOf("Blit", StringComparison.OrdinalIgnoreCase) >= 0
						&& n.IndexOf("HDR", StringComparison.OrdinalIgnoreCase) < 0)
					|| feature.GetType().Name.IndexOf("FullScreenPass", StringComparison.OrdinalIgnoreCase) >= 0;
				if (!heavy || !feature.isActive)
					continue;
				feature.SetActive(false);
				_disabledFeatures.Add(feature);
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"RenderTune renderer features: {ex.Message}");
		}
	}

	private static void RestoreRendererFeatures()
	{
		for (int i = 0; i < _disabledFeatures.Count; i++)
		{
			ScriptableRendererFeature feature = _disabledFeatures[i];
			if (feature != null)
				feature.SetActive(true);
		}
		_disabledFeatures.Clear();
	}

	private static void StripMainCameraExtras()
	{
		Camera cam = ResolveCamera();
		if (cam == null)
			return;
		cam.allowMSAA = false;
		var additional = cam.GetComponent<UniversalAdditionalCameraData>();
		if (additional == null)
			return;
		try
		{
			additional.antialiasing = AntialiasingMode.None;
			additional.requiresDepthOption = CameraOverrideOption.Off;
			additional.requiresColorOption = CameraOverrideOption.Off;
		}
		catch
		{
			// ignore
		}
	}

	private static void RestoreMainCameraExtras()
	{
		Camera cam = ResolveCamera();
		if (cam == null)
			return;
		cam.allowMSAA = _stockCamAllowMsaa;
		var additional = cam.GetComponent<UniversalAdditionalCameraData>();
		if (additional == null)
			return;
		try
		{
			additional.antialiasing = _stockAa;
			additional.requiresDepthOption = _stockReqDepth;
			additional.requiresColorOption = _stockReqColor;
		}
		catch
		{
			// ignore
		}
	}

	private static Camera ResolveCamera()
	{
		Camera cam = Camera.main;
		if (cam == null && PlayerCamera.main != null)
			cam = PlayerCamera.main.GetComponent<Camera>();
		return cam;
	}

	private static UniversalRenderPipelineAsset GetUrpAsset()
	{
		UniversalRenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
		if (asset == null)
			asset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
		return asset;
	}

	private static Renderer2DData FindRenderer2D(UniversalRenderPipelineAsset asset)
	{
		FieldInfo listField = typeof(UniversalRenderPipelineAsset).GetField(
			"m_RendererDataList",
			BindingFlags.Instance | BindingFlags.NonPublic);
		if (listField == null)
			return null;
		if (!(listField.GetValue(asset) is ScriptableRendererData[] list))
			return null;
		for (int i = 0; i < list.Length; i++)
		{
			if (list[i] is Renderer2DData r2d)
				return r2d;
		}
		return null;
	}

	private static void TuneVisibleLights()
	{
		Light2D[] lights = UnityEngine.Object.FindObjectsOfType<Light2D>();
		for (int i = 0; i < lights.Length; i++)
			TuneOne(lights[i]);
	}

	private static void RestoreLights()
	{
		Light2D[] lights = UnityEngine.Object.FindObjectsOfType<Light2D>();
		for (int i = 0; i < lights.Length; i++)
		{
			Light2D light = lights[i];
			if (light == null)
				continue;
			int id = light.GetInstanceID();
			if (!_lightSnaps.TryGetValue(id, out LightSnap snap))
				continue;
			try
			{
				light.blendStyleIndex = snap.BlendStyleIndex;
				light.volumeIntensity = snap.VolumeIntensity;
				light.volumeIntensityEnabled = snap.VolumeIntensityEnabled;
				light.volumetricShadowsEnabled = snap.VolumetricShadowsEnabled;
				if (_normalQualityField != null && snap.NormalQuality >= 0)
				{
					object nq = Enum.ToObject(_normalQualityField.FieldType, snap.NormalQuality);
					_normalQualityField.SetValue(light, nq);
				}
			}
			catch
			{
				// ignore
			}
		}
		_lightSnaps.Clear();
	}

	private static bool TuneOne(Light2D light)
	{
		if (light == null)
			return false;
		int id = light.GetInstanceID();
		if (_lightSnaps.ContainsKey(id))
			return false;

		EnsureFields();

		LightSnap snap = new LightSnap
		{
			BlendStyleIndex = light.blendStyleIndex,
			VolumeIntensity = light.volumeIntensity,
			VolumeIntensityEnabled = light.volumeIntensityEnabled,
			VolumetricShadowsEnabled = light.volumetricShadowsEnabled,
			NormalQuality = -1,
		};
		if (_normalQualityField != null)
		{
			object cur = _normalQualityField.GetValue(light);
			snap.NormalQuality = cur != null ? Convert.ToInt32(cur) : 0;
		}
		_lightSnaps[id] = snap;

		bool isGlobal = light.lightType == Light2D.LightType.Global;
		bool changed = false;

		if (!isGlobal
			&& Plugin.RenderTuneConsolidateBlend != null
			&& Plugin.RenderTuneConsolidateBlend.Value
			&& _multiplyStyleIndex >= 0
			&& light.blendStyleIndex != _multiplyStyleIndex
			&& LightLooksMultiplyFamily(light))
		{
			light.blendStyleIndex = _multiplyStyleIndex;
			changed = true;
		}

		if (!isGlobal
			&& Plugin.RenderTuneDisableVolumes != null
			&& Plugin.RenderTuneDisableVolumes.Value)
		{
			if (light.volumeIntensityEnabled || light.volumeIntensity > 0f)
			{
				light.volumeIntensityEnabled = false;
				light.volumeIntensity = 0f;
				changed = true;
			}
			if (light.volumetricShadowsEnabled)
			{
				light.volumetricShadowsEnabled = false;
				changed = true;
			}
		}

		if (!isGlobal
			&& Plugin.RenderTuneDisableNormals != null
			&& Plugin.RenderTuneDisableNormals.Value
			&& _normalQualityField != null
			&& snap.NormalQuality != 0)
		{
			object disabled = Enum.ToObject(_normalQualityField.FieldType, 0);
			_normalQualityField.SetValue(light, disabled);
			changed = true;
		}

		return changed;
	}

	private static bool LightLooksMultiplyFamily(Light2D light)
	{
		try
		{
			UniversalRenderPipelineAsset asset = GetUrpAsset();
			Renderer2DData r2d = asset != null ? FindRenderer2D(asset) : null;
			Light2DBlendStyle[] styles = r2d != null ? r2d.lightBlendStyles : null;
			int idx = light.blendStyleIndex;
			if (styles == null || idx < 0 || idx >= styles.Length || _blendModeField == null)
				return idx == _multiplyStyleIndex || idx == 0;
			object modeObj = _blendModeField.GetValue(styles[idx]);
			int mode = modeObj != null ? Convert.ToInt32(modeObj) : -1;
			if (mode == 1)
				return true;
			string name = styles[idx].name;
			return !string.IsNullOrEmpty(name)
				&& name.IndexOf("Multiply", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		catch
		{
			return false;
		}
	}
}
