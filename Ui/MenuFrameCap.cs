using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// menus (main / pause / settings) dont need uncapped 1k fps.
/// force targetFrameRate 60 while one is open, put the video setting back after.
/// </summary>
internal static class MenuFrameCap
{
	private const int Cap = 60;

	private static bool _capping;

	internal static void Tick()
	{
		if (WantCap())
		{
			if (Application.targetFrameRate != Cap)
				Application.targetFrameRate = Cap;
			_capping = true;
			return;
		}

		if (!_capping)
			return;

		RestoreStock();
		_capping = false;
	}

	private static bool WantCap()
	{
		if (PauseHandler.paused)
			return true;
		if (SettingsMenu.instance != null)
			return true;

		WorldGeneration world = WorldGeneration.world;
		// main menu / PreGen, no live world yet
		if (world == null || !world.worldExists)
			return true;

		return false;
	}

	private static void RestoreStock()
	{
		try
		{
			SettingInt setting = Settings.Get<SettingInt>("framerate");
			int value = setting != null ? setting.value : 0;
			Application.targetFrameRate = value == 0 ? -1 : Mathf.Max(10, value);
		}
		catch (System.Exception ex)
		{
			Application.targetFrameRate = -1;
			Plugin.Log.LogWarning($"menu frame cap restore failed: {ex.Message}");
		}
	}
}
