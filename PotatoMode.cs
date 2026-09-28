namespace CPUOptimization2;

/// <summary>
/// Super Potato Lighting. video checkbox, live Apply, RenderTune only.
/// PotatoUi has its own config key. this path never writes it.
/// </summary>
internal static class PotatoMode
{
	internal static bool RenderTuneOn =>
		Plugin.RenderTuneEnabled != null && Plugin.RenderTuneEnabled.Value;

	/// <summary>checkbox write. RenderTune only, apply or restore urp now</summary>
	internal static void SetRenderTune(bool on)
	{
		if (Plugin.RenderTuneEnabled != null)
			Plugin.RenderTuneEnabled.Value = on;

		Plugin.Instance?.Config.Save();

		if (on)
			RenderTune.Apply();
		else
			RenderTune.Restore();

		Plugin.Log.LogInfo(on
			? "Super Potato Lighting on — RenderTune URP applied"
			: "Super Potato Lighting off — stock URP restored");
	}
}
