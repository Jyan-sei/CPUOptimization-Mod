using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CPUOptimization2;

/// <summary>
/// video tab: Super Potato Lighting (RenderTune only) and the stream window dropdown.
/// both apply right away, mid-run.
/// </summary>
[HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.SelectTab), typeof(Setting.SettingCategory))]
internal static class SettingsMenuPotatoToggle
{
	private const string PotatoLabel = "Super Potato Lighting";
	private const string PotatoTip =
		"Cheap URP lighting (lower light RT, Multiply consolidate, no volumes/normals). Applies immediately; uncheck restores stock URP. Does not change PotatoUi.";

	private const string WindowLabel = "Stream Window";
	private const string WindowTip =
		"Live chunk window. 2x2/2x3 measures ortho (ultrawide). 3x3 and 4x4 are fixed. Larger windows pace object spawn/destroy (128 / 64 / 40 per frame). Spawns run first. Applies mid-run.";

	[HarmonyPostfix]
	private static void Postfix(SettingsMenu __instance, Setting.SettingCategory category)
	{
		if (category != Setting.SettingCategory.Video || __instance == null)
			return;
		if (Plugin.RenderTuneEnabled == null)
			return;

		try
		{
			var content = Traverse.Create(__instance).Field<RectTransform>("content").Value;
			var spawnedSettings = Traverse.Create(__instance).Field<List<GameObject>>("spawnedSettings").Value;
			if (content == null || spawnedSettings == null)
				return;

			AppendBoolRow(content, spawnedSettings);
			AppendWindowDropdown(content, spawnedSettings);
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning($"video settings rows failed: {ex.Message}");
		}
	}

	private static void AppendBoolRow(RectTransform content, List<GameObject> spawnedSettings)
	{
		float y = content.sizeDelta.y;
		GameObject row = Utils.Create("Special/GameSettingBool", content);
		if (row == null)
		{
			Plugin.Log.LogWarning("failed to create GameSettingBool for Super Potato Lighting");
			return;
		}

		TextMeshProUGUI title = row.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
		if (title != null)
			title.text = PotatoLabel;

		Toggle toggle = row.transform.GetChild(1).GetComponent<Toggle>();
		if (toggle != null)
		{
			toggle.SetIsOnWithoutNotify(PotatoMode.RenderTuneOn);
			toggle.onValueChanged.RemoveAllListeners();
			toggle.onValueChanged.AddListener(value =>
			{
				PotatoMode.SetRenderTune(value);
				PlayClick();
			});
		}

		AttachTip(row.transform.GetChild(0).gameObject, PotatoLabel, PotatoTip);
		LayoutRow(row, content, y, spawnedSettings);
	}

	private static void AppendWindowDropdown(RectTransform content, List<GameObject> spawnedSettings)
	{
		float y = content.sizeDelta.y;
		GameObject row = Utils.Create("Special/GameSettingDropdown", content);
		if (row == null)
		{
			Plugin.Log.LogWarning("failed to create GameSettingDropdown for Stream Window");
			return;
		}

		TextMeshProUGUI title = row.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
		if (title != null)
			title.text = WindowLabel;

		TMP_Dropdown dropdown = row.transform.GetChild(1).GetComponent<TMP_Dropdown>();
		if (dropdown != null)
		{
			dropdown.ClearOptions();
			var opts = new List<TMP_Dropdown.OptionData>();
			for (int i = 0; i < StreamWindowPolicy.ModeLabels.Length; i++)
				opts.Add(new TMP_Dropdown.OptionData(StreamWindowPolicy.ModeLabels[i]));
			dropdown.AddOptions(opts);

			int current = (int)StreamWindowPolicy.Current;
			if (current < 0)
				current = 0;
			if (current >= StreamWindowPolicy.ModeLabels.Length)
				current = StreamWindowPolicy.ModeLabels.Length - 1;
			dropdown.SetValueWithoutNotify(current);
			dropdown.onValueChanged.RemoveAllListeners();
			dropdown.onValueChanged.AddListener(value =>
			{
				StreamWindowPolicy.SetMode((StreamWindowPolicy.Mode)value, "video dropdown");
				PlayClick();
			});
		}

		AttachTip(row.transform.GetChild(0).gameObject, WindowLabel, WindowTip);
		LayoutRow(row, content, y, spawnedSettings);
	}

	private static void AttachTip(GameObject titleGo, string name, string desc)
	{
		UITooltip tipComp = titleGo.GetComponent<UITooltip>();
		if (tipComp == null)
			tipComp = titleGo.AddComponent<UITooltip>();
		tipComp.skipLocale = true;
		tipComp.tipName = name;
		tipComp.tipDesc = desc;
	}

	private static void LayoutRow(GameObject row, RectTransform content, float y, List<GameObject> spawnedSettings)
	{
		RectTransform rt = row.GetComponent<RectTransform>();
		float height = rt.sizeDelta.y;
		rt.anchoredPosition = new Vector2(0f, 0f - y - height * 0.5f);
		content.sizeDelta = new Vector2(content.sizeDelta.x, y + height);
		spawnedSettings.Add(row);
	}

	private static void PlayClick()
	{
		try
		{
			PlayerCamera.PlayUISound("miniClick");
		}
		catch
		{
			// ignore
		}
	}
}
