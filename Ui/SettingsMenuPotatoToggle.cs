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
		"Live chunk window. 2x2/2x3 measures ortho (ultrawide). 3x3 and 4x4 are fixed. Applies mid-run.";

	private const string BudgetLabel = "Chunk Streaming Budget (Higher = Faster spawns, might cause lag)";
	private const string BudgetTip =
		"Points spent per frame (configMult). Default 4. Only 4 through 10.\n" +
		"Register renderers: 1\n" +
		"Advance trader / enemy / light: 1\n" +
		"Release live items: 1\n" +
		"Renderer on / off: 1\n" +
		"Remove slice: 1\n" +
		"Advance entity list: 1\n" +
		"Destroy one object: 2\n" +
		"Advance item list: 2\n" +
		"Collect targets: 2\n" +
		"Park one item: 3\n" +
		"Writeback: 4\n" +
		"Store loose items: 4\n" +
		"Spawn one item: 4\n" +
		"A trader, enemy, entity, or light spawn is also 4.\n" +
		"Build/teardown colliders, when enabled: the whole frame budget.\n" +
		"Build/teardown chunk renderers, when enabled: the whole frame budget.\n" +
		"Multiplayer mpMult: solo through 4 players stays at this setting. Lobbies of 5, 6, 7, 8, or 9 multiply it by 1.5. Lobbies of 10 or more multiply it by 2. Budget is configMult * mpMult, so 10 with 10 players is 20.";

	private const string HeavyTip =
		"These operations are heavy. Enabling will improve overall FPS, but may cause stutters on weak computers.";

	private const string ColliderLabel = "Build/Teardown Colliders";
	private const string RendererLabel = "Build/Teardown Chunk Renderers";
	private const string LayerLoadLabel = "Layer Load Optimizations [fragile]";
	private const string LayerLoadTip =
		"Off uses stock terrain, structures, and the load screen.\nOn spreads terrain across frames and places you once the spawn column exists. Applies on the next layer. This path is fragile.";

	[HarmonyPostfix]
	private static void Postfix(SettingsMenu __instance, Setting.SettingCategory category)
	{
		if (__instance == null || Plugin.RenderTuneEnabled == null)
			return;

		try
		{
			var content = Traverse.Create(__instance).Field<RectTransform>("content").Value;
			var spawnedSettings = Traverse.Create(__instance).Field<List<GameObject>>("spawnedSettings").Value;
			if (content == null || spawnedSettings == null)
				return;

			if (category == Setting.SettingCategory.Video)
			{
				AppendBoolRow(content, spawnedSettings);
				AppendWindowDropdown(content, spawnedSettings);
				AppendBudgetDropdown(content, spawnedSettings);
			}
			else if (category == Setting.SettingCategory.Game)
			{
				AppendToggle(content, spawnedSettings, ColliderLabel, Plugin.BuildColliders);
				AppendToggle(content, spawnedSettings, RendererLabel, Plugin.BuildRenderers);
				AppendToggle(content, spawnedSettings, LayerLoadLabel, Plugin.LayerLoadOptimizations, LayerLoadTip);
			}
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning($"settings rows failed: {ex.Message}");
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

	private static void AppendBudgetDropdown(RectTransform content, List<GameObject> spawnedSettings)
	{
		if (Plugin.StreamBudget == null)
			return;

		float y = content.sizeDelta.y;
		GameObject row = Utils.Create("Special/GameSettingDropdown", content);
		if (row == null)
		{
			Plugin.Log.LogWarning("failed to create GameSettingDropdown for Chunk Streaming Budget");
			return;
		}

		TextMeshProUGUI title = row.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
		if (title != null)
			title.text = BudgetLabel + " [?]";

		TMP_Dropdown dropdown = row.transform.GetChild(1).GetComponent<TMP_Dropdown>();
		if (dropdown != null)
		{
			dropdown.ClearOptions();
			var opts = new List<TMP_Dropdown.OptionData>(7);
			for (int n = 4; n <= 10; n++)
				opts.Add(new TMP_Dropdown.OptionData(n.ToString()));
			dropdown.AddOptions(opts);

			int current = Plugin.StreamBudget.Value;
			if (current < 4)
				current = 4;
			if (current > 10)
				current = 10;
			dropdown.SetValueWithoutNotify(current - 4);
			dropdown.onValueChanged.RemoveAllListeners();
			dropdown.onValueChanged.AddListener(value =>
			{
				int picked = value + 4;
				if (picked < 4)
					picked = 4;
				if (picked > 10)
					picked = 10;
				Plugin.StreamBudget.Value = picked;
				PlayClick();
			});
		}

		AttachTip(row.transform.GetChild(0).gameObject, BudgetLabel, BudgetTip);
		LayoutRow(row, content, y, spawnedSettings);
	}

	private static void AppendToggle(RectTransform content, List<GameObject> spawnedSettings, string label, BepInEx.Configuration.ConfigEntry<bool> entry, string tip = null)
	{
		if (entry == null)
			return;

		float y = content.sizeDelta.y;
		GameObject row = Utils.Create("Special/GameSettingBool", content);
		if (row == null)
		{
			Plugin.Log.LogWarning("failed to create GameSettingBool for " + label);
			return;
		}

		TextMeshProUGUI title = row.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
		if (title != null)
			title.text = label + " [?]";

		Toggle toggle = row.transform.GetChild(1).GetComponent<Toggle>();
		if (toggle != null)
		{
			toggle.SetIsOnWithoutNotify(entry.Value);
			toggle.onValueChanged.RemoveAllListeners();
			toggle.onValueChanged.AddListener(value =>
			{
				bool previous = entry.Value;
				entry.Value = value;
				if (previous != value)
				{
					if (ReferenceEquals(entry, Plugin.BuildRenderers))
						StreamController.NotifyRendererTeardown(value);
					else if (ReferenceEquals(entry, Plugin.BuildColliders))
						StreamController.NotifyColliderTeardown(value);
				}
				PlayClick();
			});
		}

		AttachTip(row.transform.GetChild(0).gameObject, label, tip ?? HeavyTip);
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
