namespace CPUOptimization2;

/// <summary>
/// potato ui / hud tweaks. PotatoUi.Enabled, default off.
/// super potato lighting must not turn this flag on.
/// a manual cfg edit still can. Tick/Apply stay behind that gate.
/// </summary>
internal static class PotatoUi
{
	internal static bool IsApplied { get; private set; }

	internal static bool Wanted =>
		Plugin.PotatoUiEnabled != null && Plugin.PotatoUiEnabled.Value;

	internal static void Tick()
	{
		if (Wanted)
		{
			if (!IsApplied)
				Apply();
		}
		else if (IsApplied)
		{
			Restore();
		}
	}

	internal static void SetEnabled(bool on)
	{
		if (Plugin.PotatoUiEnabled != null)
			Plugin.PotatoUiEnabled.Value = on;
		if (on)
			Apply();
		else
			Restore();
	}

	internal static void Apply()
	{
		if (IsApplied)
			return;
		// no hud/canvas/moodle/text patches in here yet
		IsApplied = true;
		Plugin.Log.LogInfo("PotatoUi Apply — flag on (no HUD patches in this build)");
	}

	internal static void Restore()
	{
		if (!IsApplied)
			return;
		IsApplied = false;
		Plugin.Log.LogInfo("PotatoUi Restore — stock UI");
	}

	internal static void Reset()
	{
		// gen wipe. nothing persisted past the flag
	}
}
