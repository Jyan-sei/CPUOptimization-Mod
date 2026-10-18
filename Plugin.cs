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
	/// <summary>bootstrap phase lines. default on. play does not use it.</summary>
	internal static ConfigEntry<bool> GenStepLogging;
	/// <summary>0=Dynamic2x2, 1=Fixed3x3, 2=Fixed4x4. see StreamWindowPolicy.Mode</summary>
	internal static ConfigEntry<int> StreamWindowMode;
	/// <summary>configMult. 4-10. play budget is this times mpMult.</summary>
	internal static ConfigEntry<int> StreamBudget;
	/// <summary>points per frame for the post-spawn structure and entity pass. default 8, twice the default chunk budget.</summary>
	internal static ConfigEntry<int> ScatterBudget;
	/// <summary>points per frame for the post-gen cull. default 8. does not follow ChunkStreamingBudget or mpMult.</summary>
	internal static ConfigEntry<int> CullBudget;
	/// <summary>off. per-action timings in cpuopt2-steps.log.</summary>
	internal static ConfigEntry<bool> StepTiming;
	/// <summary>off. bag children stay on the bag through a stream unload when on. see BagVirt.</summary>
	internal static ConfigEntry<bool> VirtualBagContents;
	/// <summary>off. when on, a queue busy for 2s doubles create and teardown budgets until it drains.</summary>
	internal static ConfigEntry<bool> QueuePanic;
	/// <summary>off. when on, chunk collider build and teardown run again. each one spends the whole frame budget.</summary>
	internal static ConfigEntry<bool> BuildColliders;
	/// <summary>off. when on, chunk renderers are destroyed and rebuilt. each one spends the whole frame budget.</summary>
	internal static ConfigEntry<bool> BuildRenderers;
	/// <summary>on. off drops the load screen once the spawn window is placed. teardown continues in play.</summary>
	internal static ConfigEntry<bool> LoadingScreen;
	/// <summary>off in the dll. paced terrain, early place, and the instant-load ground probes.</summary>
	internal static ConfigEntry<bool> LayerLoadOptimizations;

	internal static bool LayerLoadOn =>
		LayerLoadOptimizations != null && LayerLoadOptimizations.Value;

	internal static ConfigEntry<bool> ScreenCullEnabled;
	internal static ConfigEntry<float> ScreenCullIntervalSeconds;
	internal static ConfigEntry<float> ScreenCullRescanSeconds;
	internal static ConfigEntry<float> ScreenCullMargin;
	internal static ConfigEntry<float> ScreenCullHysteresis;
	internal static ConfigEntry<float> ScreenCullSpritePad;
	internal static ConfigEntry<int> ScreenCullMaxFlipsPerPass;
	internal static ConfigEntry<float> ScreenCullTelemetrySeconds;
	/// <summary>seconds between cull census lines. 0 = off.</summary>
	internal static ConfigEntry<float> CullCensusSeconds;

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
		GenStepLogging = Config.Bind("Stream", "GenStepLogging", true,
			"log each bootstrap phase step as 'CPUOpt2 gen'. on by default so a stuck load screen has a last line. quiet once bootstrap finishes");
		StreamWindowMode = Config.Bind("Stream", "WindowMode", 1,
			"0=2x2/2x3 ultrawide dynamic, 1=fixed 3x3, 2=fixed 4x4. video dropdown writes this mid-run");
		StreamBudget = Config.Bind("Stream", "ChunkStreamingBudget", 4,
			new ConfigDescription(
				"Chunk Streaming Budget (Higher = Faster spawns, might cause lag). configMult, 4-10. play points per frame are this times mpMult: 1 for solo through 4 players, 1.5 for 5-9, 2 for 10 or more. max 20.",
				new AcceptableValueRange<int>(4, 10)));
		ScatterBudget = Config.Bind("Stream", "ScatterBudget", 8,
			new ConfigDescription(
				"Structure and entity pass, points per frame after you spawn. Default 8, twice the default Chunk Streaming Budget. A missed placement spends 1. A spawn or block circle spends 4, the same as a play spawn. Higher finishes props sooner.",
				new AcceptableValueRange<int>(1, 40)));
		CullBudget = Config.Bind("Stream", "CullBudget", 8,
			new ConfigDescription(
				"Teardown cull after generation finishes, points per frame. Default 8, twice the default Chunk Streaming Budget. Does not follow that setting or the multiplayer multiplier. A collider or renderer teardown spends 4. Store-loose spends 4. A cheap renderer hide spends 1. Higher finishes the cull sooner.",
				new AcceptableValueRange<int>(1, 40)));
		StepTiming = Config.Bind("Stream", "StepTiming", false,
			"off. write per-action frame timings to cpuopt2-steps.log. leave off; the file write costs time.");
		VirtualBagContents = Config.Bind("Stream", "VirtualBagContents", false,
			"off. items inside a virtualized bag stay on that bag and are not stored or respawned as loose world items beside it");
		QueuePanic = Config.Bind("Stream", "QueuePanic", false,
			"off. when on, a stream queue that stays busy for 2s doubles the create and teardown budgets until the queue drains.");
		BuildColliders = Config.Bind("Stream", "BuildTeardownColliders", false,
			"off. Build/Teardown Colliders. These operations are heavy. Enabling will improve overall FPS, but may cause stutters on weak computers. Each one spends the whole frame budget.");
		BuildRenderers = Config.Bind("Stream", "BuildTeardownChunkRenderers", false,
			"off. Build/Teardown Chunk Renderers. These operations are heavy. Enabling will improve overall FPS, but may cause stutters on weak computers. Each one spends the whole frame budget.");
		LoadingScreen = Config.Bind("Stream", "LoadingScreen", false,
			"unused. the mod no longer shows a loading screen.");
		LayerLoadOptimizations = Config.Bind("Stream", "LayerLoadOptimizations", false,
			"Layer Load Optimizations. Off uses stock terrain, structures, and the load screen. On is fragile: workers sample terrain when the layer starts, you are placed once the spawn column exists, and structures are paced. Applies on the next layer.");

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
		CullCensusSeconds = Config.Bind("CullCensus", "Seconds", 2f,
			"one log line of tilemap, backdrop, wallhole, and roster renderer counts. 0 = off. the walk is this log, not the cull.");

		RenderTuneEnabled = Config.Bind("RenderTune", "Enabled", false,
			"master switch for potato URP/lights. off by default - Super Potato Lighting video checkbox flips this only");
		RenderTuneStripUrp = Config.Bind("RenderTune", "StripUrp", true,
			"when RenderTune is on: msaa off, depth/opaque off, sorting-layer rt off. screen overlays stay on");
		RenderTuneLights = Config.Bind("RenderTune", "TuneLights", true,
			"when RenderTune is on: cheapen light2d - one multiply slot, kill volumes/normals");
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
		StructureLiveState.Install(_harmony);
		GenGhostPlace.Apply(_harmony);
		InstantLoad.Apply(_harmony);
		TerrainFill.Apply(_harmony);
		LateScatter.Apply(_harmony);
		LayerLoadLog.Apply(_harmony);
		ClimbablePatches.Apply(_harmony);
		ElderThornbackPatches.Apply(_harmony);
		PreStartDestroyPatches.Apply(_harmony);
		ElderResident.TryHookSpawn(_harmony);
		// these have to land even when the mod scan throws on a Krok-less prosthetics type
		ChunkUpdateGuard.Apply(_harmony);
		ItemGoneLog.Apply(_harmony);
		LayerAlertHold.Apply(_harmony);
		StreamController.Apply(_harmony);
		ChunkSeams.Install(_harmony);
		CameraSettle.Apply(_harmony);
		HudSettle.Apply(_harmony);
		CanvasCull.Apply(_harmony);
		MenuLayerOrder.Apply(_harmony);
		ModBehaviourState.Install(_harmony);
		NetHold.TryPatch(_harmony);
		RenderTune.ApplyHarmony(_harmony);
		_harmony.PatchAll(typeof(SettingsMenuPotatoToggle));

		if (RenderTuneEnabled.Value)
			RenderTune.Apply();
		if (PotatoUiEnabled.Value)
			PotatoUi.Apply();

		StepTrace.ResetSession();
		Log.LogInfo(
			$"loaded v{PluginInfo.Version} renderTune={(RenderTuneEnabled.Value ? 1 : 0)} " +
			$"potatoUi={(PotatoUiEnabled.Value ? 1 : 0)} " +
			$"bagVirt={(VirtualBagContents != null && VirtualBagContents.Value ? 1 : 0)} " +
			$"genStep={(GenStepLogging != null && GenStepLogging.Value ? 1 : 0)} " +
			$"queuePanic={(QueuePanic != null && QueuePanic.Value ? 1 : 0)} " +
			$"budget={(StreamBudget != null ? StreamBudget.Value : 4)} " +
			$"scatter={(ScatterBudget != null ? ScatterBudget.Value : 8)} " +
			$"cull={(CullBudget != null ? CullBudget.Value : 8)} " +
			$"colliders={(BuildColliders != null && BuildColliders.Value ? 1 : 0)} " +
			$"renderers={(BuildRenderers != null && BuildRenderers.Value ? 1 : 0)} " +
			$"loadScreen={(LoadingScreen != null && LoadingScreen.Value ? 1 : 0)} " +
			$"layerLoad={(LayerLoadOn ? 1 : 0)} " +
			$"stepTiming={(StepTiming != null && StepTiming.Value ? 1 : 0)}");
	}

	private float _noteAt;
	private bool _tickedThisUpdate;

	private void Note(string where)
	{
		float now = UnityEngine.Time.realtimeSinceStartup;
		if (now - _noteAt < 2f)
			return;
		_noteAt = now;
		try
		{
			WorldGeneration world = WorldGeneration.world;
			string line = System.DateTime.Now.ToString("HH:mm:ss")
				+ " " + where
				+ " " + StreamController.DebugLine()
				+ " world=" + (world != null ? 1 : 0)
				+ " gen=" + (world != null && world.generatingWorld ? 1 : 0)
				+ " exists=" + (world != null && world.worldExists ? 1 : 0)
				+ " needs=" + (StreamController.NeedsTick ? 1 : 0)
				+ " enabled=" + (enabled ? 1 : 0)
				+ "\n";
			System.IO.File.AppendAllText(@"C:\Users\Owner\Documents\CasuGame\boot-alive.txt", line);
		}
		catch { }
	}

	private void Update()
	{
		_tickedThisUpdate = false;
		if (Enabled == null || !Enabled.Value)
		{
			Note("disabled");
			return;
		}
		StreamController.HoldBootstrapTime();

		// one refresh of type-lookup misses after every plugin has awoken. not per frame.
		TypeLookup.NotePluginsReady();
		StructureLiveState.TryPatchLate();
		StructureFallbackLog.TryInstall(_harmony);
		ElderResident.TryHookSpawn(_harmony);
		ModBehaviourState.NotifyUpdate();

		// menus first. works even when WorldGeneration.world is null (main menu)
		MenuFrameCap.Tick();
		PhysicsStepCap.Tick();
		LayerAlertHold.Tick();

		RenderTune.Tick(UnityEngine.Time.unscaledDeltaTime);
		PotatoUi.Tick();

		WorldGeneration world = WorldGeneration.world;
		TerrainFill.TickMarbles(world);
		if (world == null)
			return;

		if (world.generatingWorld)
		{
			// new gen started. wipe the finished capture, dont touch in-progress ghost records
			if (CaptureStore.Captured || StreamState.Active || StreamState.Bootstrapping)
			{
				CaptureStore.Clear();
				ItemGoneLog.Clear();
				StructureLiveState.ResetLayer();
				StreamController.Deactivate();
				StreamState.ResetForNewGenerate();
				RenderTune.Reset();
				PotatoUi.Reset();
				GenGhostPlace.Reset();
				CanvasCull.Reset();
				ChunkTerrain.ResetDrawPrep();
				LayerLoadLog.Abort();
				Log.LogInfo("generatingWorld rose again - capture/stream cleared.");
			}
			LayerLoadLog.Begin();
			return;
		}

		if (!world.worldExists)
			return;

		ChunkTerrain.PrepareDraw(world);

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
		{
			StepTrace.Begin("update");
			try
			{
				StreamController.BeginFrame();
				StreamController.MaintainElders(world);
				// settled means skip the tick, visibility Sync already bails early
				if (StreamController.NeedsTick)
				{
					_tickedThisUpdate = true;
					StreamController.Tick(world);
				}
				else if (!StreamState.Bootstrapping)
				{
					// elders can open or close a collider while the window itself is settled
					int seamBudget = 0;
					ChunkSeams.Refresh(world, ref seamBudget, false);
				}
			}
			finally
			{
				StepTrace.End();
			}
		}

		ScreenCull.Tick(world);
		CullCensus.Tick(world);
		ItemGoneLog.Tick(world);
	}

	private void LateUpdate()
	{
		if (Enabled == null || !Enabled.Value)
			return;
		StreamController.HoldBootstrapTime();
		WorldGeneration world = WorldGeneration.world;
		if (StreamState.Bootstrapping && !_tickedThisUpdate)
		{
			if (world != null && !world.generatingWorld && world.worldExists && StreamController.NeedsTick)
			{
				StepTrace.Begin("late");
				try
				{
					StreamController.BeginFrame();
					StreamController.Tick(world);
				}
				finally
				{
					StepTrace.End();
				}
			}
		}
		else if (StreamState.Active && !StreamState.Bootstrapping
			&& world != null && !world.generatingWorld && world.worldExists)
		{
			// stock visibility only runs after the camera moves. the body can
			// already be off the loaded 3x3 by then, so follow it every frame.
			StepTrace.Begin("late");
			try
			{
				StreamController.Sync(world);
			}
			finally
			{
				StepTrace.End();
			}
		}
		if (StreamState.Active && world != null && world.worldExists)
			ElderResident.KeepBodiesMoving();
		Note("late ticked=" + (_tickedThisUpdate ? 1 : 0));
	}

	private void OnDisable()
	{
		try
		{
			System.IO.File.AppendAllText(@"C:\Users\Owner\Documents\CasuGame\boot-alive.txt",
				System.DateTime.Now.ToString("HH:mm:ss") + " DISABLED " + StreamController.DebugLine() + "\n");
		}
		catch { }
	}

	private void OnDestroy()
	{
		try
		{
			System.IO.File.AppendAllText(@"C:\Users\Owner\Documents\CasuGame\boot-alive.txt",
				System.DateTime.Now.ToString("HH:mm:ss") + " DESTROYED\n");
		}
		catch { }
		PhysicsStepCap.Restore();
		if (Instance == this)
			Instance = null;
		_harmony?.UnpatchSelf();
	}
}
