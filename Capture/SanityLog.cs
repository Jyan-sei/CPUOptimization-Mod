using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CPUOptimization2;

internal static class SanityLog
{
	internal static void LogObjectCapture()
	{
		Plugin.Log.LogInfo("=== CPUOptimization2 ===");
		Plugin.Log.LogInfo(
			$"biomeDepth={CaptureStore.BiomeDepth} grid={CaptureStore.ChunkWidth}x{CaptureStore.ChunkHeight} " +
			$"solidBlocks={CaptureStore.SolidBlocks}");

		bool failed = false;
		failed |= CheckGrid();
		failed |= CheckSolidBlocks();

		failed |= CheckCount("enemies", CaptureStore.Enemies.Count, min: 1, max: 20000);
		failed |= CheckCount("entities", CaptureStore.Entities.Count, min: 1, max: 20000);
		failed |= CheckCount("worldItems", CaptureStore.WorldItems.Count, min: 1, max: 20000);
		failed |= CheckSoftCap("lights", CaptureStore.Lights.Count, warnZero: false, failMax: 5000);

		if (CaptureStore.Traders.Count > 64)
		{
			Plugin.Log.LogError($"FAIL traders={CaptureStore.Traders.Count} (max 64)");
			failed = true;
		}
		else if (CaptureStore.Traders.Count == 0)
		{
			Plugin.Log.LogWarning("WARN traders=0 (lifepod trader is not guaranteed)");
		}
		else
		{
			Plugin.Log.LogInfo($"OK traders={CaptureStore.Traders.Count}");
		}

		LogBreakdown("traders", CaptureStore.Traders.Select(t => "trader" + t.Character));
		LogBreakdown("enemies", CaptureStore.Enemies.Select(e => e.Id));
		LogBreakdown("entities", CaptureStore.Entities.Select(e => e.Id));
		LogBreakdown("worldItems", CaptureStore.WorldItems.Select(e => e.Id));
		LogBreakdown("lights", CaptureStore.Lights.Select(e => e.Id));

		failed |= CheckPerIdCap("enemies", CaptureStore.Enemies.Select(e => e.Id));
		failed |= CheckPerIdCap("entities", CaptureStore.Entities.Select(e => e.Id));
		failed |= CheckPerIdCap("worldItems", CaptureStore.WorldItems.Select(e => e.Id));

		int crystals = CountEntitiesByIdContains("crystal");
		int barrels = CountWorldItemsByIdContains("minibarrel");
		int ropes = CountEntitiesByIdContains("rope") + CountEntitiesByIdContains("sandvine");
		failed |= CheckSoftCap("crystals", crystals, warnZero: true, failMax: 500);
		failed |= CheckSoftCap("minibarrels", barrels, warnZero: true, failMax: 500);
		failed |= CheckSoftCap("ropes/vines", ropes, warnZero: true, failMax: 5000);

		if (CaptureStore.BiomeDepth == 0)
		{
			// resident path. count live GOs, not CaptureStore entity rows
			int holes = WallholeResident.Count;
			if (holes < 90 || holes > 120)
			{
				Plugin.Log.LogError($"FAIL wallholesResident={holes} (expected ~90-120 on biome 0)");
				failed = true;
			}
			else
			{
				Plugin.Log.LogInfo($"OK wallholesResident={holes}");
			}
		}

		Plugin.Log.LogInfo(failed ? "=== CPUOptimization2 RESULT: FAIL ===" : "=== CPUOptimization2 RESULT: PASS ===");
	}

	internal static void LogTileCapture()
	{
		Plugin.Log.LogInfo("=== CPUOptimization2 tiles ===");
		int chunksWith = CaptureStore.TileFlips.Count;
		int cells = 0;
		for (int i = 0; i < CaptureStore.TileFlips.Count; i++)
			cells += CaptureStore.TileFlips[i].Cells.Count;

		Plugin.Log.LogInfo($"chunksWithMatrices={chunksWith} variedCells={cells}");

		bool failed = false;
		if (chunksWith == 0 || chunksWith > 80)
		{
			Plugin.Log.LogError($"FAIL chunksWithMatrices={chunksWith} (expected 1..80 after ~90 frames)");
			failed = true;
		}
		else
		{
			Plugin.Log.LogInfo($"OK chunksWithMatrices={chunksWith}");
		}

		Plugin.Log.LogInfo(failed ? "=== CPUOptimization2 tiles RESULT: FAIL ===" : "=== CPUOptimization2 tiles RESULT: PASS ===");
	}

	internal static void LogBootstrapDone()
	{
		Plugin.Log.LogInfo("=== CPUOptimization2 stream bootstrap ===");
		Plugin.Log.LogInfo(
			$"OK live window is {StreamWindowPolicy.FormatSpans()}; remaining chunks live only as capture arrays + worldBlocks/fluid. " +
			"Tile flips are packed lazily on unload / re-rolled by ChunkScript on first show.");
		Plugin.Log.LogInfo("=== CPUOptimization2 stream bootstrap RESULT: PASS ===");
	}

	private static bool CheckGrid()
	{
		if (CaptureStore.ChunkWidth != 16 || CaptureStore.ChunkHeight != 16)
		{
			Plugin.Log.LogError(
				$"FAIL grid={CaptureStore.ChunkWidth}x{CaptureStore.ChunkHeight} (expected 16x16; disable LagDestroyer9000 for a full-world capture)");
			return true;
		}
		Plugin.Log.LogInfo("OK grid=16x16");
		return false;
	}

	private static bool CheckSolidBlocks()
	{
		if (CaptureStore.SolidBlocks <= 0)
		{
			Plugin.Log.LogError($"FAIL solidBlocks={CaptureStore.SolidBlocks}");
			return true;
		}
		Plugin.Log.LogInfo($"OK solidBlocks={CaptureStore.SolidBlocks}");
		return false;
	}

	private static bool CheckCount(string label, int count, int min, int max)
	{
		if (count < min || count > max)
		{
			Plugin.Log.LogError($"FAIL {label}={count} (expected {min}..{max})");
			return true;
		}
		Plugin.Log.LogInfo($"OK {label}={count}");
		return false;
	}

	private static bool CheckSoftCap(string label, int count, bool warnZero, int failMax)
	{
		if (count > failMax)
		{
			Plugin.Log.LogError($"FAIL {label}={count} (max {failMax})");
			return true;
		}
		if (warnZero && count == 0)
		{
			Plugin.Log.LogWarning($"WARN {label}=0");
			return false;
		}
		Plugin.Log.LogInfo($"OK {label}={count}");
		return false;
	}

	private static bool CheckPerIdCap(string label, IEnumerable<string> ids)
	{
		bool failed = false;
		foreach (var kv in ids.GroupBy(x => x ?? "null").OrderByDescending(g => g.Count()))
		{
			if (kv.Count() > 5000)
			{
				Plugin.Log.LogError($"FAIL {label} id={kv.Key} count={kv.Count()} (max 5000)");
				failed = true;
			}
		}
		return failed;
	}

	private static void LogBreakdown(string label, IEnumerable<string> ids)
	{
		StringBuilder sb = new StringBuilder();
		sb.Append("  ").Append(label).Append(" by id: ");
		bool any = false;
		foreach (var kv in ids.GroupBy(x => x ?? "null").OrderByDescending(g => g.Count()))
		{
			if (any)
				sb.Append(", ");
			sb.Append(kv.Key).Append('=').Append(kv.Count());
			any = true;
		}
		if (!any)
			sb.Append("(none)");
		Plugin.Log.LogInfo(sb.ToString());
	}

	private static int CountEntitiesByIdContains(string needle)
	{
		int n = 0;
		for (int i = 0; i < CaptureStore.Entities.Count; i++)
		{
			string id = CaptureStore.Entities[i].Id ?? "";
			if (id.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0)
				n++;
		}
		return n;
	}

	private static int CountWorldItemsByIdContains(string needle)
	{
		int n = 0;
		for (int i = 0; i < CaptureStore.WorldItems.Count; i++)
		{
			string id = CaptureStore.WorldItems[i].Id ?? "";
			if (id.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0)
				n++;
		}
		return n;
	}
}
