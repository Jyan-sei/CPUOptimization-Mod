using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CPUOptimization.Features.Lights;

// keeps some trap light2ds disabled, copies flash onto existing sprites (not spike - that needs the blink)
internal sealed class FxLightToEmission : MonoBehaviour
{
	internal string Kind;
	internal bool UseEnabled;

	private Light2D[] _lights;
	private SpriteRenderer[] _sprites;
	private Color[] _baseColors;

	internal static void Attach(GameObject go, string kind, bool useEnabled)
	{
		if (!go || go.GetComponent<FxLightToEmission>())
			return;

		var fx = go.AddComponent<FxLightToEmission>();
		fx.Kind = kind;
		fx.UseEnabled = useEnabled;
		fx.Cache();
		fx.Apply();
		Light2D[] attachedLights = go.GetComponentsInChildren<Light2D>(true);
		for (int i = 0; i < attachedLights.Length; i++)
			LightCullHost.RegisterLight(attachedLights[i]);
		LightCullHost.NoteFxConverted(kind, go.name);
	}

	private void Cache()
	{
		_lights = GetComponentsInChildren<Light2D>(true);
		_sprites = GetComponentsInChildren<SpriteRenderer>(true);
		_baseColors = new Color[_sprites.Length];
		for (int i = 0; i < _sprites.Length; i++)
			_baseColors[i] = _sprites[i] ? _sprites[i].color : Color.white;
	}

	internal void Apply()
	{
		if (_lights == null)
			Cache();

		float flash = 0f;
		for (int i = 0; i < _lights.Length; i++)
		{
			Light2D light = _lights[i];
			if (!light)
				continue;

			if (UseEnabled)
			{
				if (light.enabled)
					flash = Mathf.Max(flash, Mathf.Max(light.intensity, 0.4f));
			}
			else
				flash = Mathf.Max(flash, light.intensity);

			light.enabled = false;
		}

		flash = Mathf.Clamp01(flash);
		if (_sprites == null)
			return;

		for (int i = 0; i < _sprites.Length; i++)
		{
			SpriteRenderer sprite = _sprites[i];
			if (!sprite)
				continue;
			Color baseColor = _baseColors[i];
			sprite.color = Color.Lerp(baseColor, baseColor * 1.75f + new Color(0.2f, 0.2f, 0.15f, 0f), flash);
		}
	}
}
