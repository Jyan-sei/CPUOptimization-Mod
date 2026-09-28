using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace CPUOptimization2;

[BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
public class Plugin : BaseUnityPlugin
{
	internal static Plugin Instance;
	internal static BepInEx.Logging.ManualLogSource Log;
	internal static ConfigEntry<bool> Enabled;
	internal static ConfigEntry<bool> StreamEnabled;
	internal static ConfigEntry<bool> VerboseLogging;
	/// <summary>0=Dynamic2x2, 1=Fixed3x3, 2=Fixed4x4. see StreamWindowPolicy.Mode</summary>
	internal static ConfigEntry<int> StreamWindowMode;

	internal static ConfigEntry<bool> ScreenCullEnabled;
	internal static ConfigEntry<float> ScreenCullIntervalSeconds;
	internal static ConfigEntry<float> ScreenCullRescanSeconds;
	internal static ConfigEntry<float> ScreenCullMargin;
	internal static ConfigEntry<float> ScreenCullHysteresis;
	internal static ConfigEntry<float> ScreenCullSpritePad;
	internal static ConfigEntry<int> ScreenCullMaxFlipsPerPass;
	internal static ConfigEntry<float> ScreenCullTelemetrySeconds;

	internal static ConfigEntry<bool> RenderTuneEnabled;
	internal static ConfigEntry<bool> RenderTuneStripUrp;
	internal static ConfigEntry<bool> RenderTuneLights;
	internal static ConfigEntry<float> RenderTuneLightRtScale;
	internal static ConfigEntry<int> RenderTuneMaxLightRt;
	internal static ConfigEntry<bool> RenderTuneConsolidateBlend;
	internal static ConfigEntry<bool> RenderTuneDisableVolumes;
	internal static ConfigEntry<bool> RenderTuneDisableNormals;
	internal static ConfigEntry<float> RenderTuneLightRescanSeconds;

	internal static ConfigEntry<bool> PotatoUiEnabled;

	private Harmony _harmony;

	private void Awake()
	{
		Instance = this;
		Log = Logger;
		Enabled = Config.Bind("General", "Enabled", true,
			"capture world objects after gen and sanity-check counts in the log");
		StreamEnabled = Config.Bind("Stream", "Enabled", true,
			"ghost place outside the cam window during gen, cull leftover terrain on the load screen, 5s unload grace in play. MP: host streams union of per-player windows; clients capture only");
		VerboseLogging = Config.Bind("Stream", "VerboseLogging", false,
			"log every chunk load/unload. off by default, spam costs frame time");
		StreamWindowMode = Config.Bind("Stream", "WindowMode", 1,
			"0=2x2/2x3 ultrawide dynamic, 1=fixed 3x3, 2=fixed 4x4. object spawn/destroy caps: 128 / 64 / 40 per frame. spawns eat the budget first. video dropdown writes this mid-run");

		ScreenCullEnabled = Config.Bind("ScreenCull", "Enabled", false,
			"off by default. chunk window decides whats loaded. flip this to hide sprites and tilemaps outside the ortho view");
		// old builds wrote Enabled=true as the default and bepinex keeps that.
		// first launch after 0.7.13 forces it off once. flip Enabled back on if you want the culler.
		var cullOffMigrated = Config.Bind("ScreenCull", "OffByDefault", false,
			"set once when the in-frame culler default flipped off. leave this alone");
		if (!cullOffMigrated.Value)
		{
			ScreenCullEnabled.Value = false;
			cullOffMigrated.Value = true;
		}
		ScreenCullIntervalSeconds = Config.Bind("ScreenCull", "IntervalSeconds", 0.35f,
			"how often a batch of tracked renderers gets rechecked");
		ScreenCullRescanSeconds = Config.Bind("ScreenCull", "RescanSeconds", 2.5f,
			"how often to refresh the tracked renderer list");
		ScreenCullMargin = Config.Bind("ScreenCull", "Margin", 1.15f,
			"multiply ortho half-extents for the on box (>1 = slack)");
		ScreenCullHysteresis = Config.Bind("ScreenCull", "Hysteresis", 4f,
			"extra world units on the off box so edge stuff doesnt flicker");
		ScreenCullSpritePad = Config.Bind("ScreenCull", "SpritePad", 2f,
			"half-extent pad when testing sprites against the view box");
		ScreenCullMaxFlipsPerPass = Config.Bind("ScreenCull", "MaxFlipsPerPass", 32,
			"cap enable/disable writes per batch so we dont thrash");
		ScreenCullTelemetrySeconds = Config.Bind("ScreenCull", "TelemetrySeconds", 0f,
			"log cull stats every n seconds. 0 = off");

		RenderTuneEnabled = Config.Bind("RenderTune", "Enabled", false,
			"master switch for potato URP/lights. off by default — Super Potato Lighting video checkbox flips this only");
		RenderTuneStripUrp = Config.Bind("RenderTune", "StripUrp", true,
			"when RenderTune is on: msaa off, depth/opaque off, sorting-layer rt off, trim heavy renderer features");
		RenderTuneLights = Config.Bind("RenderTune", "TuneLights", true,
			"when RenderTune is on: cheapen light2d — one multiply slot, kill volumes/normals");
		RenderTuneLightRtScale = Config.Bind("RenderTune", "LightRtScale", 0.35f,
			"urp 2d light rt scale (stock is often 0.5). lower = cheaper multiply pass");
		RenderTuneMaxLightRt = Config.Bind("RenderTune", "MaxLightRenderTextures", 8,
			"cap concurrent light rts (stock often 16)");
		RenderTuneConsolidateBlend = Config.Bind("RenderTune", "ConsolidateMultiplyBlend", true,
			"remap multiply-family lights onto one blend style index");
		RenderTuneDisableVolumes = Config.Bind("RenderTune", "DisableLightVolumes", true,
			"zero light2d volumetric intensity");
		RenderTuneDisableNormals = Config.Bind("RenderTune", "DisableLightNormals", true,
			"force light2d normal maps off");
		RenderTuneLightRescanSeconds = Config.Bind("RenderTune", "LightRescanSeconds", 2.5f,
			"how often to scan for new lights to tune");

		PotatoUiEnabled = Config.Bind("PotatoUi", "Enabled", false,
			"potato HUD/UI tweaks. default off; Super Potato Lighting never sets this. manual cfg flip only");

		if (!Enabled.Value)
		{
			Log.LogInfo("disabled via config.");
			return;
		}

		_harmony = new Harmony(PluginInfo.GUID);
		FinishWorldGenerationPatch.Apply(_harmony);
		VisibilityStreamPatch.Apply(_harmony);
		TraderPatches.Apply(_harmony);
		GenGhostPlace.Apply(_harmony);
		ClimbablePatches.Apply(_harmony);
		ElderThornbackPatches.Apply(_harmony);
		PreStartDestroyPatches.Apply(_harmony);
		ElderResident.TryHookSpawn(_harmony);
		ModBehaviourState.Install(_harmony);
		ChunkUpdateGuard.Apply(_harmony);
		RenderTune.ApplyHarmony(_harmony);
		_harmony.PatchAll(typeof(SettingsMenuPotatoToggle));

		if (RenderTuneEnabled.Value)
			RenderTune.Apply();
		if (PotatoUiEnabled.Value)
			PotatoUi.Apply();

		Log.LogInfo(
			$"loaded v{PluginInfo.Version} renderTune={(RenderTuneEnabled.Value ? 1 : 0)} " +
			$"potatoUi={(PotatoUiEnabled.Value ? 1 : 0)}");
	}

	private void Update()
	{
		if (Enabled == null || !Enabled.Value)
			return;

		// one retry after every plugin has awoken. no per-frame type scan after that.
		ElderResident.NotifyUpdate();
		ElderResident.TryHookSpawn(_harmony);
		AltarBlessing.NotifyUpdate();
		ModBehaviourState.NotifyUpdate();

		// menus first. works even when WorldGeneration.world is null (main menu)
		MenuFrameCap.Tick();

		RenderTune.Tick(UnityEngine.Time.unscaledDeltaTime);
		PotatoUi.Tick();

		WorldGeneration world = WorldGeneration.world;
		if (world == null)
			return;

		if (world.generatingWorld)
		{
			// new gen started. wipe the finished capture, dont touch in-progress ghost records
			if (CaptureStore.Captured || StreamState.Active || StreamState.Bootstrapping)
			{
				CaptureStore.Clear();
				StreamController.Deactivate();
				StreamState.ResetForNewGenerate();
				RenderTune.Reset();
				PotatoUi.Reset();
				GenGhostPlace.Reset();
				Log.LogInfo("generatingWorld rose again — capture/stream cleared.");
			}
			return;
		}

		if (!world.worldExists)
			return;

		// bootstrap arms right after capture. tile wait is capture-only mode
		if (!StreamState.Active)
			TileMatrixCapture.CaptureIfReady(world);

		if (StreamEnabled != null && StreamEnabled.Value && CaptureStore.Captured && !StreamState.Active
			&& CaptureStore.TileCaptureDone && MpSession.AllowStream)
		{
			StreamController.Activate(world);
		}

		// elders move while the window is settled, so this runs even when the stream tick is idle
		if (StreamState.Active)
			StreamController.MaintainElders(world);

		// settled means skip the tick, visibility Sync already bails early
		if (StreamController.NeedsTick)
			StreamController.Tick(world);

		ScreenCull.Tick(world);
	}

	private void OnDestroy()
	{
		if (Instance == this)
			Instance = null;
		_harmony?.UnpatchSelf();
	}
}
