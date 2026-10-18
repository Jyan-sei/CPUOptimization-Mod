using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// column fill for every depth that generates terrain. workers start once the
/// seeds exist and sample the spawn column first, then the left half, then the
/// rest. the main thread walks the left rolls, places the player, and writes
/// the remaining columns a few per frame. depth 5 and deeper write nothing
/// in the stock method, so they stay on it.
/// </summary>
internal static class TerrainFill
{
	const int RestColumns = 8;
	// layer 0 lifepod circles reach 36 blocks. the right half is written after
	// the carve, so those columns have to land first or they paint the floor back.
	const int PodReach = 36;
	const int MarblePerFrame = 8;
	const int FreqStep = 32;
	const float CaveBaseFreq = 0.06f;

	static readonly FieldInfo FractalBound = AccessTools.Field(typeof(FastNoiseLite), "mFractalBounding");
	static readonly MethodInfo OresMethod = AccessTools.Method(typeof(WorldGeneration), "GenerateOres");
	static readonly MethodInfo StructuresMethod = AccessTools.Method(typeof(WorldGeneration), "WorldGenerateStructures");
	static readonly MethodInfo PlaceMethod = AccessTools.Method(typeof(WorldGeneration), "WorldPlacePlayer");
	static readonly FieldInfo BodyRb = AccessTools.Field(typeof(Body), "rb");

	static bool _skipNextPlace;
	static bool _playEarly;
	static Vector2 _heldVel;
	static bool _heldVelOn;

	static readonly List<MarbleBg> _marbles = new List<MarbleBg>();
	static int _marbleAt;
	static WorldGeneration _marbleWorld;
	static UnityEngine.Object _marblePrefab;
	static bool _marbleMiss;
	static ColumnGate _live;

	struct MarbleBg
	{
		internal int X;
		internal int Y;
		internal float Angle;
	}

	internal static void Apply(Harmony harmony)
	{
		MethodInfo terrain = AccessTools.Method(typeof(WorldGeneration), "WorldGenerateTerrain", Type.EmptyTypes);
		harmony.Patch(terrain, prefix: new HarmonyMethod(typeof(TerrainFill), nameof(TerrainPrefix)));
		harmony.CreateReversePatcher(terrain, new HarmonyMethod(typeof(TerrainFill), nameof(StockTerrain))).Patch();
		// together's place prefix always replaces the call. first priority so the
		// second place is skipped even when their prefix was registered earlier.
		var skipPlace = new HarmonyMethod(typeof(TerrainFill), nameof(SkipPlacePrefix));
		skipPlace.priority = Priority.First;
		harmony.Patch(PlaceMethod, prefix: skipPlace);
		harmony.Patch(
			AccessTools.Method(typeof(Body), "HandlePhysics"),
			prefix: new HarmonyMethod(typeof(TerrainFill), nameof(KeepVelocity)),
			postfix: new HarmonyMethod(typeof(TerrainFill), nameof(ReleaseBody)));
		harmony.Patch(
			AccessTools.Method(typeof(Body), "PlaceBody"),
			postfix: new HarmonyMethod(typeof(TerrainFill), nameof(NotePlaced)));
		harmony.Patch(
			AccessTools.Method(typeof(Body), "HandleBody"),
			prefix: new HarmonyMethod(typeof(TerrainFill), nameof(LetBodySim)),
			postfix: new HarmonyMethod(typeof(TerrainFill), nameof(RestoreBodySim)));
	}

	// together clients stay frozen until gen ends. the host spawn packet is the
	// position that matters, and walking before it lands desyncs the body.
	static bool MayMove()
	{
		if (MpSession.Running && !MpSession.IsServer)
			return false;
		return _playEarly || LateScatter.Holding;
	}

	// stock returns immediately while generatingWorld is set, so walk forces never run.
	// the flag is only cleared for this call. physics still sees the real value and freezes.
	static void LetBodySim(ref bool __state)
	{
		__state = false;
		if (!MayMove())
			return;
		WorldGeneration world = WorldGeneration.world;
		if (world == null || !world.generatingWorld)
			return;
		world.generatingWorld = false;
		__state = true;
	}

	static void RestoreBodySim(bool __state)
	{
		if (!__state)
			return;
		WorldGeneration world = WorldGeneration.world;
		if (world != null)
			world.generatingWorld = true;
	}

	static void NotePlaced()
	{
		if (MpSession.Running && !MpSession.IsServer)
			return;
		_playEarly = true;
	}

	static bool TerrainPrefix(WorldGeneration __instance, ref IEnumerator __result)
	{
		_skipNextPlace = false;
		_playEarly = false;
		// qol's seeded wrapper saves Random.state across each yield. this fill
		// rewinds that stream, so a seeded map has to stay on the stock generator.
		if (QolSeed.Active)
		{
			Plugin.Log.LogInfo("terrain fill left stock - qol seed");
			return true;
		}
		if (!Want(__instance))
			return true;
		__result = Run(__instance);
		return false;
	}

	static bool Want(WorldGeneration world)
	{
		if (world == null)
			return false;
		if (!Plugin.LayerLoadOn)
			return false;
		if (world.biomeOverride == WorldGeneration.OverrideSceneType.Tutorial
			|| world.biomeOverride == WorldGeneration.OverrideSceneType.Debug)
			return false;
		// stock writes nothing past the depth 4 branch
		return world.biomeDepth <= 4;
	}

	// original generator, prefixes skipped. not an iterator: harmony replaces this body.
	static IEnumerator StockTerrain(WorldGeneration world)
	{
		throw new NotImplementedException();
	}

	static IEnumerator RunStock(WorldGeneration world, UnityEngine.Random.State beforeRoll)
	{
		Plugin.Log.LogError("terrain fill fell back to stock");
		UnityEngine.Random.state = beforeRoll;
		IEnumerator stock = StockTerrain(world);
		while (stock != null && stock.MoveNext())
			yield return stock.Current;
	}

	// the stock place already ran at the player column. the later call would drop a second pod.
	static bool SkipPlacePrefix(ref IEnumerator __result)
	{
		if (!_skipNextPlace)
			return true;
		_skipNextPlace = false;
		__result = Done();
		return false;
	}

	static IEnumerator Done()
	{
		yield break;
	}

	// FreezeAll clears velocity. physics already stepped this frame, so keep that
	// velocity and put it back after the lock is lifted. otherwise each step
	// starts from zero and you fall in slow motion with no walk cycle.
	static void KeepVelocity(Body __instance)
	{
		_heldVelOn = false;
		if (__instance == null || !MayMove())
			return;
		WorldGeneration world = WorldGeneration.world;
		if (world == null || !world.generatingWorld)
			return;
		Rigidbody2D rb = BodyRb != null ? BodyRb.GetValue(__instance) as Rigidbody2D : null;
		if (rb == null)
			return;
		_heldVel = rb.velocity;
		_heldVelOn = true;
	}

	// generatingWorld freezes the body. once placed, let that body move.
	static void ReleaseBody(Body __instance)
	{
		if (__instance == null)
			return;
		if (!MayMove())
			return;
		WorldGeneration world = WorldGeneration.world;
		if (world == null || !world.generatingWorld)
		{
			_playEarly = false;
			_heldVelOn = false;
			return;
		}
		Rigidbody2D rb = BodyRb != null ? BodyRb.GetValue(__instance) as Rigidbody2D : null;
		if (rb != null && rb.constraints == RigidbodyConstraints2D.FreezeAll)
			rb.constraints = RigidbodyConstraints2D.FreezeRotation;
		if (_heldVelOn && rb != null)
			rb.velocity = _heldVel;
		_heldVelOn = false;
		// together relays SetTimeScale. a direct write here never reaches clients.
		if (!MpSession.Running && !PauseHandler.paused && Time.timeScale < 1f)
			Time.timeScale = 1f;
	}

	internal static void TickMarbles(WorldGeneration world)
	{
		if (world == null || world != _marbleWorld)
		{
			_marbles.Clear();
			_marbleAt = 0;
			_marbleWorld = null;
			return;
		}
		if (_marbleAt >= _marbles.Count)
			return;
		UnityEngine.Object prefab = MarblePrefab();
		if (prefab == null)
		{
			_marbles.Clear();
			_marbleAt = 0;
			return;
		}
		int n = 0;
		while (n < MarblePerFrame && _marbleAt < _marbles.Count)
		{
			MarbleBg m = _marbles[_marbleAt++];
			UnityEngine.Object.Instantiate(
				prefab,
				world.BlockToWorldPos(new Vector2Int(m.X, m.Y)),
				Quaternion.Euler(0f, 0f, m.Angle));
			n++;
		}
		if (_marbleAt >= _marbles.Count)
		{
			_marbles.Clear();
			_marbleAt = 0;
		}
	}

	static IEnumerator Run(WorldGeneration world)
	{
		_marbles.Clear();
		_marbleAt = 0;
		_marbleWorld = world;
		world.SetLoadingText("gencreatingterrain");
		yield return null;

		ushort[,] blocks = WorldArrays.WorldBlocks(world);
		if (blocks == null || OresMethod == null || StructuresMethod == null)
		{
			Plugin.Log.LogError("terrain fill missing blocks or gen methods");
			yield break;
		}

		int width = blocks.GetLength(0);
		int height = blocks.GetLength(1);
		int depth = world.biomeDepth;
		int playerColumn = world.WorldToBlockPos(Vector2.zero).x;
		if (playerColumn < 0)
			playerColumn = 0;
		if (playerColumn >= width)
			playerColumn = width - 1;

		var samples = new Samples(depth, width, height);
		var beforeRoll = UnityEngine.Random.state;
		RollSeeds(depth, samples);
		if (depth <= 1)
			samples.Freq = BuildFrequency(samples.FreqSeed, width, height, out samples.FreqW, out samples.FreqH);

		// qol's FastNoise constructor reads Time.frameCount. workers cannot construct it.
		NoiseSet[] pool = null;
		Exception noiseError = null;
		try
		{
			pool = BuildPool(samples);
		}
		catch (Exception ex)
		{
			noiseError = ex;
		}
		if (noiseError != null)
		{
			Plugin.Log.LogError("terrain fill noise failed " + noiseError);
			IEnumerator failed = RunStock(world, beforeRoll);
			while (failed.MoveNext())
				yield return failed.Current;
			yield break;
		}

		var gate = new ColumnGate(width);
		if (_live != null)
			Volatile.Write(ref _live.Cancel, 1);
		_live = gate;

		float started = Time.realtimeSinceStartup;
		var state = UnityEngine.Random.state;
		Task.Run(() => SampleAll(gate, samples, playerColumn, pool));
		Plugin.Log.LogInfo(
			"terrain fill workers depth=" + depth + " column=" + playerColumn + " rest=" + RestColumns);

		bool spawned = false;
		try
		{
			while (Volatile.Read(ref gate.LeftReady) == 0)
			{
				if (Volatile.Read(ref gate.Faulted) != 0)
				{
					Plugin.Log.LogError("terrain fill workers failed " + gate.Error);
					IEnumerator failed = RunStock(world, beforeRoll);
					while (failed.MoveNext())
						yield return failed.Current;
					yield break;
				}
				yield return null;
				UnityEngine.Random.state = state;
			}
			if (Volatile.Read(ref gate.Faulted) != 0)
			{
				Plugin.Log.LogError("terrain fill workers failed " + gate.Error);
				IEnumerator failed = RunStock(world, beforeRoll);
				while (failed.MoveNext())
					yield return failed.Current;
				yield break;
			}

			Plugin.Log.LogInfo(
				"terrain fill left ready " + ((Time.realtimeSinceStartup - started) * 1000f).ToString("0") + "ms");

			int marbleCount = 0;
			ApplyRange(world, blocks, samples, 0, playerColumn + 1, ref marbleCount);
			InstantLoad.RefreshPainted(world, 0, playerColumn + 1);

			// a together client waits inside their place for the host packet.
			// doing that mid-fill stalls the columns and carves around world x = 0.
			bool togetherClient = MpSession.Running && !MpSession.IsServer;
			bool wantEarly = !togetherClient
				&& InstantLoad.Active
				&& PlayerCamera.main != null
				&& PlayerCamera.main.body != null
				&& PlaceMethod != null;
			bool restampPod = false;
			var podStamp = UnityEngine.Random.state;
			Vector2 podPos = Vector2.zero;
			int resumeX = playerColumn + 1;
			int protectLo = -1;
			int protectHi = -1;
			if (wantEarly)
			{
				// the lifepod carve sits on columns to the right of the player.
				// write those before the carve, and do not walk them again.
				if (depth == 0)
				{
					int podEnd = Math.Min(width, playerColumn + 1 + PodReach);
					for (int c = playerColumn + 1; c < podEnd; c++)
					{
						while (Volatile.Read(ref gate.Ready[c]) == 0)
						{
							if (Volatile.Read(ref gate.Faulted) != 0)
							{
								Plugin.Log.LogError("terrain fill workers failed " + gate.Error);
								yield break;
							}
							var wait = UnityEngine.Random.state;
							yield return null;
							UnityEngine.Random.state = wait;
						}
					}
					if (podEnd > playerColumn + 1)
					{
						ApplyRange(world, blocks, samples, playerColumn + 1, podEnd, ref marbleCount);
						InstantLoad.RefreshPainted(world, playerColumn + 1, podEnd);
					}
					resumeX = podEnd;
				}

				// place rolls its own stream. rewind so the columns still to the right stay in terrain order.
				var terrain = UnityEngine.Random.state;
				podStamp = terrain;
				IEnumerator place = (IEnumerator)PlaceMethod.Invoke(world, null);
				while (place != null && place.MoveNext())
				{
					var held = UnityEngine.Random.state;
					yield return place.Current;
					UnityEngine.Random.state = held;
				}
				UnityEngine.Random.state = terrain;
				podPos = PlayerCamera.main.body.transform.position;
				restampPod = depth == 0;
				_skipNextPlace = true;
				_playEarly = true;
				spawned = true;
				if (!MpSession.Running && Time.timeScale == 0f)
					Time.timeScale = 1f;
				// together keep-x moves the body off world x = 0. the pod disk is
				// that position, and later columns must not paint it back.
				if (restampPod)
				{
					int bodyCol = world.WorldToBlockPos(podPos).x;
					if (bodyCol < 0)
						bodyCol = 0;
					if (bodyCol >= width)
						bodyCol = width - 1;
					protectLo = Math.Max(0, bodyCol - PodReach);
					protectHi = Math.Min(width, bodyCol + 1 + PodReach);
					int writeLo = Math.Max(protectLo, resumeX);
					if (writeLo < protectHi)
					{
						for (int c = writeLo; c < protectHi; c++)
						{
							while (Volatile.Read(ref gate.Ready[c]) == 0)
							{
								if (Volatile.Read(ref gate.Faulted) != 0)
								{
									Plugin.Log.LogError("terrain fill workers failed " + gate.Error);
									yield break;
								}
								var wait = UnityEngine.Random.state;
								yield return null;
								UnityEngine.Random.state = wait;
							}
						}
						ApplyRange(world, blocks, samples, writeLo, protectHi, ref marbleCount);
						InstantLoad.RefreshPainted(world, writeLo, protectHi);
					}
				}
				Plugin.Log.LogInfo(
					"terrain fill early place column=" + playerColumn
					+ " podThrough=" + resumeX
					+ " protect=" + protectLo + ".." + protectHi
					+ " t=" + ((Time.realtimeSinceStartup - started) * 1000f).ToString("0") + "ms");
			}

			int x = resumeX;
			while (x < width)
			{
				if (protectLo >= 0 && x >= protectLo && x < protectHi)
				{
					x = protectHi;
					continue;
				}
				int end = Math.Min(width, x + RestColumns);
				if (protectLo >= 0 && x < protectLo && end > protectLo)
					end = protectLo;
				for (int c = x; c < end; c++)
				{
					while (Volatile.Read(ref gate.Ready[c]) == 0)
					{
						if (Volatile.Read(ref gate.Faulted) != 0)
						{
							Plugin.Log.LogError("terrain fill workers failed " + gate.Error);
							yield break;
						}
						var wait = UnityEngine.Random.state;
						yield return null;
						UnityEngine.Random.state = wait;
					}
				}
				ApplyRange(world, blocks, samples, x, end, ref marbleCount);
				InstantLoad.RefreshPainted(world, x, end);
				state = UnityEngine.Random.state;
				yield return null;
				UnityEngine.Random.state = state;
				x = end;
			}

			OresMethod.Invoke(world, null);
			if (depth <= 1)
				Streaks(blocks, width, height, depth, (int)world.chunkWidth, (int)world.chunkHeight);
			else if (depth == 2 || depth == 3)
				StreaksCavern(blocks, width, height, depth, (int)world.chunkWidth, (int)world.chunkHeight);
			InstantLoad.RefreshPainted(world, 0, width);
			world.UpdateWorld();

			// deeper layers still scatter after borders. the early place would
			// otherwise run those props in the middle of this fill.
			if (spawned && depth > 1)
				LateScatter.HoldStructures();
			IEnumerator structures = (IEnumerator)StructuresMethod.Invoke(world, null);
			if (structures != null)
			{
				while (structures.MoveNext())
				{
					var held = UnityEngine.Random.state;
					yield return structures.Current;
					UnityEngine.Random.state = held;
				}
			}
			InstantLoad.RefreshPainted(world, 0, width);
			PlaceLayerLiquids(world, depth);
			if (restampPod)
				RestampPod(world, podPos, podStamp);
			InstantLoad.RefreshPainted(world, 0, width);
		}
		finally
		{
			Volatile.Write(ref gate.Cancel, 1);
			if (_live == gate)
				_live = null;
		}

		Plugin.Log.LogInfo(
			"terrain fill done " + ((Time.realtimeSinceStartup - started) * 1000f).ToString("0")
			+ "ms early=" + (spawned ? 1 : 0) + " depth=" + depth);
	}

	// ores, streaks, and liquids land after the early carve and would fill the
	// pod back in. replay the same three circles, then put the terrain stream back.
	static void RestampPod(WorldGeneration world, Vector2 pos, UnityEngine.Random.State stamp)
	{
		var now = UnityEngine.Random.state;
		UnityEngine.Random.state = stamp;
		world.GenerateBlockCircle(pos, 30, 3, 0.8f, 0f, true);
		world.GenerateBlockCircle(pos, 36, 4, 0.3f, 0f, true);
		world.GenerateBlockCircle(pos, 30, 0, 0.15f, 0f, true);
		UnityEngine.Random.state = now;
	}

	static void PlaceLayerLiquids(WorldGeneration world, int depth)
	{
		if (depth == 0)
			world.PlaceLiquids(128f, 1, 32);
		else if (depth == 1)
		{
			world.PlaceLiquids(10f, 1, 400);
			world.PlaceLiquids(18f, 2, 128);
		}
		else if (depth == 2 || depth == 3)
		{
			world.PlaceLiquids(50f, 1, 26);
			world.PlaceLiquids(15f, 3, 128);
		}
		else if (depth == 4)
		{
			world.PlaceLiquids(30f, 1, 128);
			world.PlaceLiquids(10f, 2, 50);
		}
	}

	static void RollSeeds(int depth, Samples samples)
	{
		if (depth <= 1)
		{
			samples.CaveSeed = UnityEngine.Random.Range(0, int.MaxValue);
			samples.DirtSeed = UnityEngine.Random.Range(0, int.MaxValue);
			samples.FreqSeed = UnityEngine.Random.Range(0, int.MaxValue);
			samples.BiomeSeed = UnityEngine.Random.Range(0, int.MaxValue);
			return;
		}
		if (depth == 2 || depth == 3)
		{
			samples.BiomeSeed = UnityEngine.Random.Range(0, int.MaxValue);
			samples.FreqSeed = UnityEngine.Random.Range(0, int.MaxValue);
			samples.DirtSeed = UnityEngine.Random.Range(0, int.MaxValue);
			samples.CaveSeed = UnityEngine.Random.Range(0, int.MaxValue);
			samples.ToxicSeed = UnityEngine.Random.Range(0, int.MaxValue);
			samples.Biome2Seed = UnityEngine.Random.Range(0, int.MaxValue);
			samples.MarbleSeed = UnityEngine.Random.Range(0, int.MaxValue);
			return;
		}
		samples.MarbleSeed = UnityEngine.Random.Range(0, int.MaxValue);
		samples.Biome2Seed = UnityEngine.Random.Range(0, int.MaxValue);
	}

	static readonly MethodInfo MemberClone = typeof(object).GetMethod(
		"MemberwiseClone",
		BindingFlags.Instance | BindingFlags.NonPublic);

	static NoiseSet[] BuildPool(Samples samples)
	{
		// qol's constructor prefix reads Time.frameCount and may rewrite the seed.
		// build one set on the main thread, then copy it. workers only sample.
		NoiseSet first = MakeNoise(samples);
		int n = Math.Max(8, Environment.ProcessorCount * 2 + 4);
		var pool = new NoiseSet[n];
		pool[0] = first;
		for (int i = 1; i < n; i++)
			pool[i] = CopySet(first);
		return pool;
	}

	static NoiseSet CopySet(NoiseSet src)
	{
		return new NoiseSet
		{
			Cave = CopyNoise(src.Cave),
			Dirt = CopyNoise(src.Dirt),
			Biome = CopyNoise(src.Biome),
			Freq = CopyNoise(src.Freq),
			Marble = CopyNoise(src.Marble),
			Biome2 = CopyNoise(src.Biome2),
			Toxic = CopyNoise(src.Toxic),
		};
	}

	static FastNoiseLite CopyNoise(FastNoiseLite noise)
	{
		if (noise == null)
			return null;
		return (FastNoiseLite)MemberClone.Invoke(noise, null);
	}

	static void SampleAll(ColumnGate gate, Samples samples, int playerColumn, NoiseSet[] pool)
	{
		ThreadLocal<NoiseSet> locals = null;
		try
		{
			int taken = -1;
			locals = new ThreadLocal<NoiseSet>(() =>
			{
				int i = Interlocked.Increment(ref taken);
				if ((uint)i >= (uint)pool.Length)
					throw new InvalidOperationException("terrain noise pool");
				return pool[i];
			});
			var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };
			SampleColumn(gate, samples, locals, playerColumn);
			if (playerColumn > 0)
			{
				Parallel.For(0, playerColumn, options, (col, loop) =>
				{
					if (Volatile.Read(ref gate.Cancel) != 0)
					{
						loop.Stop();
						return;
					}
					SampleColumn(gate, samples, locals, col);
				});
			}
			Volatile.Write(ref gate.LeftReady, 1);
			if (playerColumn + 1 < samples.Width && Volatile.Read(ref gate.Cancel) == 0)
			{
				Parallel.For(playerColumn + 1, samples.Width, options, (col, loop) =>
				{
					if (Volatile.Read(ref gate.Cancel) != 0)
					{
						loop.Stop();
						return;
					}
					SampleColumn(gate, samples, locals, col);
				});
			}
		}
		catch (Exception ex)
		{
			gate.Error = ex;
			Volatile.Write(ref gate.Faulted, 1);
			Volatile.Write(ref gate.LeftReady, 1);
		}
		finally
		{
			if (locals != null)
				locals.Dispose();
		}
	}

	static void SampleColumn(ColumnGate gate, Samples samples, ThreadLocal<NoiseSet> locals, int x)
	{
		NoiseSet noise = locals.Value;
		int height = samples.Height;
		int row = x * height;
		if (samples.Depth <= 1)
		{
			for (int y = 0; y < height; y++)
			{
				float wobble = SampleFreq(samples.Freq, samples.FreqW, samples.FreqH, x, y);
				// cellular has no skew, so a fixed frequency plus scaled coordinates matches SetFrequency
				float scale = (CaveBaseFreq + wobble * 0.01f) / CaveBaseFreq;
				int i = row + y;
				samples.Cave[i] = noise.Cave.GetNoise(x * scale, y * scale);
				samples.Dirt[i] = noise.Dirt.GetNoise(x, y);
				samples.Biome[i] = noise.Biome.GetNoise(x, y);
			}
		}
		else if (samples.Depth == 2 || samples.Depth == 3)
		{
			for (int y = 0; y < height; y++)
			{
				int i = row + y;
				samples.Biome[i] = noise.Biome.GetNoise(x, y);
				samples.FreqNoise[i] = noise.Freq.GetNoise(x, y);
				samples.Marble[i] = noise.Marble.GetNoise(x, y);
				samples.Dirt[i] = noise.Dirt.GetNoise(x, y);
				samples.Cave[i] = noise.Cave.GetNoise(x, y);
				samples.Biome2[i] = noise.Biome2.GetNoise(x, y);
				if (samples.Toxic != null)
					samples.Toxic[i] = noise.Toxic.GetNoise(x, y);
			}
		}
		else
		{
			for (int y = 0; y < height; y++)
			{
				noise.Marble.SetFrequency(0.0189f - (float)y / height * 0.002f);
				int i = row + y;
				samples.Marble[i] = noise.Marble.GetNoise(x, y);
				samples.Biome2[i] = noise.Biome2.GetNoise(x, y);
			}
		}
		Volatile.Write(ref gate.Ready[x], 1);
	}

	static void ApplyRange(
		WorldGeneration world, ushort[,] blocks, Samples samples, int x0, int x1, ref int marbleCount)
	{
		int height = samples.Height;
		int depth = samples.Depth;
		float half = world.halfHeight;
		for (int x = x0; x < x1; x++)
		{
			int row = x * height;
			for (int y = 0; y < height; y++)
			{
				int i = row + y;
				if (depth <= 1)
					blocks[x, y] = Decide(depth, height, y, samples.Cave[i], samples.Dirt[i], samples.Biome[i]);
				else if (depth == 2 || depth == 3)
					blocks[x, y] = ApplyCavern(samples, i, depth, y, height, half, x, ref marbleCount);
				else
					blocks[x, y] = ApplyDeepMarble(samples.Marble[i], samples.Biome2[i]);
			}
		}
	}

	static ushort ApplyCavern(
		Samples samples, int i, int depth, int y, int height, float half, int x, ref int marbleCount)
	{
		float biome = samples.Biome[i];
		float freq = samples.FreqNoise[i] * 0.25f + 0.1f;
		float minMarble = depth == 2 ? 0.45f : 1f;
		ushort block;
		if (samples.Marble[i] <= minMarble)
		{
			// dirt is only read when the biome clears the frequency, same short-circuit as the stock ternary
			if (biome > freq && samples.Dirt[i] < -0.4f)
				block = biome < freq + 0.1f ? (ushort)12 : (ushort)13;
			else
				block = 0;
			if (samples.Cave[i] > 0.65f)
				block = 17;
			if (biome > 0.75f)
				block = 15;
			if (samples.Biome2[i] > 0.1f)
				block = (ushort)UnityEngine.Random.Range(3, 5);
			if (depth == 3 && block > 0 && UnityEngine.Random.value < 0.1f)
				block = (ushort)(15 + UnityEngine.Random.Range(0, 2));
		}
		else
		{
			marbleCount++;
			if (biome > freq)
				block = samples.Dirt[i] < -0.1f ? (ushort)18 : (ushort)19;
			else
				block = 0;
			if (marbleCount > 100)
			{
				marbleCount = 0;
				_marbles.Add(new MarbleBg
				{
					X = x,
					Y = y,
					Angle = UnityEngine.Random.value * 360f,
				});
			}
		}
		if (depth == 3 && samples.Toxic[i] < -0.8f && UnityEngine.Random.value > 0.5f)
			block = 22;
		if (depth == 3 && block > 0 && UnityEngine.Random.value > (y + half) / height)
			block = 23;
		return block;
	}

	static ushort ApplyDeepMarble(float marble, float biome2)
	{
		float n = marble + UnityEngine.Random.Range(-0.1f, 0.1f);
		ushort block = 0;
		if (n > 0.15f && n < 0.25f)
			block = 23;
		else if (n >= 0.25f && n < 0.45f)
			block = 16;
		else if (n >= 0.45f && n < 0.66f)
			block = 15;
		else if (n >= 0.66f)
			block = 19;
		if (biome2 < -0.735f)
			block = 0;
		return block;
	}

	static ushort Decide(int depth, int height, int y, float cave, float dirt, float biome)
	{
		ushort block = cave > -0.715f ? (ushort)1 : (ushort)0;
		if (block > 0 && dirt < -0.1f)
			block = (double)dirt < -0.33 ? (ushort)16 : (ushort)2;
		if (block > 0 && UnityEngine.Random.Range(0f, 1f) > 0.99f)
			block = (ushort)UnityEngine.Random.Range(1, 5);
		if (biome > 0.1f)
			block = (ushort)UnityEngine.Random.Range(3, 5);
		if (block > 0 && biome < -0.8f)
			block = 15;
		if (depth == 1 && y < height * 0.5f)
		{
			float band = (float)y / height * 2f;
			if (UnityEngine.Random.Range(0f, 1f) > band && block == 2)
				block = 12;
			if (y < height * 0.33f && UnityEngine.Random.Range(0f, 1f) > band * 3f && block == 1)
				block = 13;
		}
		return block;
	}

	// the frequency field's period is thousands of cells. knots every 32 match the wobble.
	static float[] BuildFrequency(int seed, int width, int height, out int knotsX, out int knotsY)
	{
		var noise = new FastNoiseLite(seed);
		noise.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
		noise.SetFrequency(0.00037f);
		knotsX = (width - 1) / FreqStep + 2;
		knotsY = (height - 1) / FreqStep + 2;
		var grid = new float[knotsX * knotsY];
		for (int gy = 0; gy < knotsY; gy++)
		for (int gx = 0; gx < knotsX; gx++)
			grid[gy * knotsX + gx] = noise.GetNoise(gx * FreqStep, gy * FreqStep);
		return grid;
	}

	static float SampleFreq(float[] grid, int knotsX, int knotsY, int x, int y)
	{
		float fx = x / (float)FreqStep;
		float fy = y / (float)FreqStep;
		int x0 = (int)fx;
		int y0 = (int)fy;
		if (x0 >= knotsX - 1)
			x0 = knotsX - 2;
		if (y0 >= knotsY - 1)
			y0 = knotsY - 2;
		if (x0 < 0)
			x0 = 0;
		if (y0 < 0)
			y0 = 0;
		float tx = fx - x0;
		float ty = fy - y0;
		if (tx < 0f)
			tx = 0f;
		if (tx > 1f)
			tx = 1f;
		if (ty < 0f)
			ty = 0f;
		if (ty > 1f)
			ty = 1f;
		int row = y0 * knotsX;
		float a = grid[row + x0];
		float b = grid[row + x0 + 1];
		float c = grid[row + knotsX + x0];
		float d = grid[row + knotsX + x0 + 1];
		float top = a + (b - a) * tx;
		float bot = c + (d - c) * tx;
		return top + (bot - top) * ty;
	}

	static NoiseSet MakeNoise(Samples samples)
	{
		var set = new NoiseSet();
		int depth = samples.Depth;
		if (depth <= 1)
		{
			set.Cave = MakeCave(samples.CaveSeed);
			set.Dirt = MakeDirt(samples.DirtSeed);
			set.Biome = MakeBiome(samples.BiomeSeed);
			return set;
		}
		if (depth == 2 || depth == 3)
		{
			set.Biome = MakeCavernBiome(samples.BiomeSeed, depth);
			set.Freq = MakeCavernFreq(samples.FreqSeed);
			set.Dirt = MakeCavernDirt(samples.DirtSeed);
			set.Cave = MakeCavernCave(samples.CaveSeed);
			set.Toxic = MakeCavernToxic(samples.ToxicSeed);
			set.Biome2 = MakeCavernBiome2(samples.Biome2Seed);
			set.Marble = MakeCavernMarble(samples.MarbleSeed, depth);
			return set;
		}
		set.Marble = MakeDeepMarble(samples.MarbleSeed);
		set.Biome2 = MakeDeepBiome(samples.Biome2Seed);
		return set;
	}

	static FastNoiseLite MakeCave(int seed)
	{
		var noise = new FastNoiseLite(seed);
		noise.SetNoiseType(FastNoiseLite.NoiseType.Cellular);
		noise.SetFrequency(CaveBaseFreq);
		noise.SetFractalOctaves(3);
		noise.SetFractalType(FastNoiseLite.FractalType.FBm);
		noise.SetFractalLacunarity(1.5f);
		return noise;
	}

	// five octaves, weighted like the original seven, so the blobs stay the same size.
	static FastNoiseLite MakeDirt(int seed)
	{
		var noise = new FastNoiseLite(seed);
		noise.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
		noise.SetFractalType(FastNoiseLite.FractalType.FBm);
		noise.SetFractalOctaves(7);
		noise.SetFrequency(0.035f);
		float bound = FractalBound != null ? (float)FractalBound.GetValue(noise) : 0f;
		noise.SetFractalOctaves(5);
		noise.SetFrequency(0.035f);
		if (FractalBound != null && bound > 0f)
			FractalBound.SetValue(noise, bound);
		return noise;
	}

	static FastNoiseLite MakeBiome(int seed)
	{
		var noise = new FastNoiseLite(seed);
		noise.SetNoiseType(FastNoiseLite.NoiseType.Cellular);
		noise.SetFrequency(0.04f);
		noise.SetCellularDistanceFunction(FastNoiseLite.CellularDistanceFunction.EuclideanSq);
		noise.SetCellularReturnType(FastNoiseLite.CellularReturnType.Distance);
		noise.SetCellularJitter(1f);
		noise.SetFractalType(FastNoiseLite.FractalType.Ridged);
		noise.SetFractalLacunarity(1.5f);
		return noise;
	}

	static FastNoiseLite MakeCavernBiome(int seed, int depth)
	{
		var noise = new FastNoiseLite(seed);
		noise.SetNoiseType(FastNoiseLite.NoiseType.Value);
		noise.SetFrequency(0.086f);
		noise.SetFractalType(FastNoiseLite.FractalType.FBm);
		noise.SetFractalOctaves(depth == 2 ? 2 : 3);
		noise.SetFractalGain(0.49f);
		noise.SetFractalWeightedStrength(2.34f);
		noise.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
		noise.SetDomainWarpAmp(22f);
		return noise;
	}

	static FastNoiseLite MakeCavernFreq(int seed)
	{
		var noise = new FastNoiseLite(seed);
		noise.SetFrequency(0.006f);
		return noise;
	}

	static FastNoiseLite MakeCavernDirt(int seed)
	{
		var noise = new FastNoiseLite(seed);
		noise.SetNoiseType(FastNoiseLite.NoiseType.Cellular);
		noise.SetFrequency(0.02f);
		noise.SetFractalType(FastNoiseLite.FractalType.Ridged);
		noise.SetFractalGain(0.65f);
		return noise;
	}

	static FastNoiseLite MakeCavernCave(int seed)
	{
		var noise = new FastNoiseLite(seed);
		noise.SetFrequency(0.005f);
		noise.SetFractalType(FastNoiseLite.FractalType.PingPong);
		noise.SetFractalGain(0.35f);
		noise.SetDomainWarpType(FastNoiseLite.DomainWarpType.BasicGrid);
		noise.SetDomainWarpAmp(40f);
		return noise;
	}

	static FastNoiseLite MakeCavernToxic(int seed)
	{
		var noise = new FastNoiseLite(seed);
		noise.SetFrequency(0.012f);
		noise.SetFractalType(FastNoiseLite.FractalType.PingPong);
		noise.SetFractalGain(0.3f);
		noise.SetDomainWarpType(FastNoiseLite.DomainWarpType.BasicGrid);
		noise.SetDomainWarpAmp(50f);
		return noise;
	}

	static FastNoiseLite MakeCavernBiome2(int seed)
	{
		var noise = new FastNoiseLite(seed);
		noise.SetNoiseType(FastNoiseLite.NoiseType.Cellular);
		noise.SetFrequency(0.05f);
		noise.SetCellularDistanceFunction(FastNoiseLite.CellularDistanceFunction.EuclideanSq);
		noise.SetCellularReturnType(FastNoiseLite.CellularReturnType.Distance);
		noise.SetCellularJitter(1f);
		noise.SetFractalType(FastNoiseLite.FractalType.Ridged);
		noise.SetFractalLacunarity(1.5f);
		return noise;
	}

	static FastNoiseLite MakeCavernMarble(int seed, int depth)
	{
		var noise = new FastNoiseLite(seed);
		noise.SetFrequency(depth == 2 ? 0.007f : 0.035f);
		noise.SetNoiseType(FastNoiseLite.NoiseType.Perlin);
		noise.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
		noise.SetDomainWarpAmp(100f);
		return noise;
	}

	static FastNoiseLite MakeDeepMarble(int seed)
	{
		var noise = new FastNoiseLite(seed);
		noise.SetNoiseType(FastNoiseLite.NoiseType.Value);
		noise.SetFractalType(FastNoiseLite.FractalType.Ridged);
		noise.SetFractalOctaves(3);
		noise.SetFractalLacunarity(2.29f);
		noise.SetFractalGain(4f);
		noise.SetFractalWeightedStrength(1.2f);
		noise.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
		noise.SetDomainWarpAmp(41f);
		return noise;
	}

	static FastNoiseLite MakeDeepBiome(int seed)
	{
		var noise = new FastNoiseLite(seed);
		noise.SetFrequency(0.02f);
		noise.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
		noise.SetDomainWarpAmp(25f);
		return noise;
	}

	static UnityEngine.Object MarblePrefab()
	{
		if (_marblePrefab != null)
			return _marblePrefab;
		_marblePrefab = Resources.Load("Special/marbleBackground");
		if (_marblePrefab == null && !_marbleMiss)
		{
			_marbleMiss = true;
			Plugin.Log.LogWarning("terrain fill missed Special/marbleBackground");
		}
		return _marblePrefab;
	}

	static void Streaks(ushort[,] blocks, int width, int height, int depth, int chunkW, int chunkH)
	{
		UnityEngine.Random.Range(0.35f, 0.5f);
		if (depth > 0)
		{
			Stamp(blocks, width, height, chunkW, chunkH, 0.35f, 0.5f, 6, 64, 3, true, 5);
			Stamp(blocks, width, height, chunkW, chunkH, 0.35f, 0.5f, 6, 60, 3, false, 5);
		}
		else
		{
			Stamp(blocks, width, height, chunkW, chunkH, 0.35f, 0.4f, 6, 48, 2, true, 11);
			Stamp(blocks, width, height, chunkW, chunkH, 0.35f, 0.4f, 6, 48, 2, false, 11);
		}
	}

	static void StreaksCavern(ushort[,] blocks, int width, int height, int depth, int chunkW, int chunkH)
	{
		Stamp(blocks, width, height, chunkW, chunkH, 0.25f, 0.3f, 6, 48, 2, true, 11);
		Stamp(blocks, width, height, chunkW, chunkH, 0.24f, 0.3f, 6, 48, 2, false, 11);
		if (depth != 3)
			return;
		float count = (float)(chunkW * chunkH) * UnityEngine.Random.Range(0.5f, 0.6f);
		for (int n = 0; (float)n < count; n++)
		{
			int ox = UnityEngine.Random.Range(0, width);
			int oy = UnityEngine.Random.Range(0, height);
			int side = UnityEngine.Random.Range(4, 16);
			for (int a = 0; a < side; a++)
			for (int b = 0; b < side; b++)
			{
				int x = ox + a;
				int y = oy + b;
				if ((uint)x < (uint)width && (uint)y < (uint)height)
					blocks[x, y] = 20;
			}
		}
	}

	static void Stamp(
		ushort[,] blocks, int width, int height, int chunkW, int chunkH,
		float countMin, float countMax, int lenMin, int lenMax, int thick, bool horizontal, ushort id)
	{
		float count = (float)(chunkW * chunkH) * UnityEngine.Random.Range(countMin, countMax);
		for (int n = 0; (float)n < count; n++)
		{
			int ox = UnityEngine.Random.Range(0, width);
			int oy = UnityEngine.Random.Range(0, height);
			int len = UnityEngine.Random.Range(lenMin, lenMax);
			for (int a = 0; a < len; a++)
			for (int b = 0; b < thick; b++)
			{
				int x = horizontal ? ox + a : ox + b;
				int y = horizontal ? oy + b : oy + a;
				if ((uint)x < (uint)width && (uint)y < (uint)height)
					blocks[x, y] = id;
			}
		}
	}

	sealed class ColumnGate
	{
		internal readonly int[] Ready;
		internal int LeftReady;
		internal int Faulted;
		internal int Cancel;
		internal Exception Error;

		internal ColumnGate(int width)
		{
			Ready = new int[width];
		}
	}

	sealed class NoiseSet
	{
		internal FastNoiseLite Cave;
		internal FastNoiseLite Dirt;
		internal FastNoiseLite Biome;
		internal FastNoiseLite Freq;
		internal FastNoiseLite Marble;
		internal FastNoiseLite Biome2;
		internal FastNoiseLite Toxic;
	}

	sealed class Samples
	{
		internal readonly int Depth;
		internal readonly int Width;
		internal readonly int Height;
		internal int CaveSeed;
		internal int DirtSeed;
		internal int FreqSeed;
		internal int BiomeSeed;
		internal int Biome2Seed;
		internal int MarbleSeed;
		internal int ToxicSeed;
		internal float[] Freq;
		internal int FreqW;
		internal int FreqH;
		internal readonly float[] Cave;
		internal readonly float[] Dirt;
		internal readonly float[] Biome;
		internal readonly float[] FreqNoise;
		internal readonly float[] Marble;
		internal readonly float[] Biome2;
		internal readonly float[] Toxic;

		internal Samples(int depth, int width, int height)
		{
			Depth = depth;
			Width = width;
			Height = height;
			int n = width * height;
			if (depth <= 1)
			{
				Cave = new float[n];
				Dirt = new float[n];
				Biome = new float[n];
				return;
			}
			if (depth == 2 || depth == 3)
			{
				Biome = new float[n];
				FreqNoise = new float[n];
				Marble = new float[n];
				Dirt = new float[n];
				Cave = new float[n];
				Biome2 = new float[n];
				if (depth == 3)
					Toxic = new float[n];
				return;
			}
			Marble = new float[n];
			Biome2 = new float[n];
		}
	}
}
