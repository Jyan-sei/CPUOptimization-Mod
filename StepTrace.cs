using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// one line per build/teardown step, plus a frame sum.
/// ms is the step itself. the file write and the BepInEx lines happen after the sum is closed.
/// file: CasuGame\cpuopt2-steps.log
/// grep: CPUOpt2 step
/// </summary>
internal static class StepTrace
{
	internal const string LogPath = @"C:\Users\Owner\Documents\CasuGame\cpuopt2-steps.log";

	private struct Bucket
	{
		internal string Step;
		internal double Ms;
		internal double UnchargedMs;
		internal int Count;
		internal int Charge;
		internal double Max;
		internal int Cx;
		internal int Cy;
		internal string What;
	}

	private static readonly StringBuilder Buf = new StringBuilder(4096);
	private static readonly Bucket[] Buckets = new Bucket[48];
	private static int _bucketCount;
	private static int[] _order = new int[48];
	private static bool _open;
	private static string _caller = "";
	private static int _frame;
	private static long _frameStart;
	private static double _workMs;
	private static bool _noted;
	private static int _createLeft;
	private static int _tearLeft;
	private static int _ops;
	private static bool _panic;
	private static string _name;

	private static bool _headerWritten;

	private static bool Logging => Plugin.StepTiming != null && Plugin.StepTiming.Value;

	internal static void ResetSession()
	{
		if (!Logging)
			return;
		_headerWritten = true;
		try
		{
			File.WriteAllText(LogPath,
				"CPUOpt2 step session " + System.DateTime.Now.ToString("o")
				+ " v=" + PluginInfo.Version + "\n"
				+ "CPUOpt2 step weights budget=configMult(4-10)*mpMult"
				+ " mpMult=1 solo-4, 1.5 for 5-9, 2 for 10+"
				+ " renderer/register/advance-trader-enemy-light-entity/release/remove-slice=1"
				+ " destroy-object/advance-item/collect=2 park-item=3"
				+ " writeback/store-loose/spawn=4 bootstrapTerrain=uncharged\n");
		}
		catch { }
	}

	internal static void Begin(string caller)
	{
		if (!Logging)
		{
			_open = false;
			return;
		}
		if (!_headerWritten)
			ResetSession();
		if (_open)
			End();
		_open = true;
		_caller = caller;
		_frame = Time.frameCount;
		_frameStart = Stopwatch.GetTimestamp();
		_workMs = 0;
		_bucketCount = 0;
		_noted = false;
		_panic = false;
		_name = null;
		Buf.Length = 0;
	}

	internal static void NoteQueue(int createLeft, int tearLeft, int ops, bool panic)
	{
		_noted = true;
		_createLeft = createLeft;
		_tearLeft = tearLeft;
		_ops = ops;
		_panic = panic;
	}

	internal static long Stamp()
	{
		if (!Logging)
			return 0;
		return Stopwatch.GetTimestamp();
	}

	internal static void ClearName() => _name = null;

	internal static void SetName(string what) => _name = what;

	internal static string TakeName()
	{
		string w = _name;
		_name = null;
		return w;
	}

	/// <summary>keep writes a detail line even when the step was cheap. slow bookkeeping (1ms+) is kept too.</summary>
	internal static void Mark(string lane, string step, int cx, int cy, int charge, int left, string what, long started, bool keep)
	{
		if (!_open)
			return;
		if (string.IsNullOrEmpty(what))
			what = TakeName();
		double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
		if (ms < 0)
			ms = 0;
		_workMs += ms;
		AddBucket(step, ms, charge, cx, cy, what);
		if (!keep && ms < 1.0)
			return;

		Buf.Append("CPUOpt2 step f=").Append(_frame);
		Buf.Append(" caller=").Append(_caller);
		Buf.Append(" lane=").Append(lane);
		if (cx >= 0 && cy >= 0)
			Buf.Append(" chunk=").Append(cx).Append(',').Append(cy);
		Buf.Append(" step=").Append(step);
		Buf.Append(" charge=").Append(charge);
		if (left >= 0)
			Buf.Append(" left=").Append(left);
		Buf.Append(" ms=").Append(ms.ToString("0.00", CultureInfo.InvariantCulture));
		if (!string.IsNullOrEmpty(what))
		{
			Buf.Append(" what=");
			AppendFlat(what);
		}
		Buf.Append('\n');
	}

	internal static void End()
	{
		if (!_open)
			return;
		double wall = (Stopwatch.GetTimestamp() - _frameStart) * 1000.0 / Stopwatch.Frequency;
		if (wall < 0)
			wall = 0;
		_open = false;
		bool anyDetail = Buf.Length > 0;
		if (!anyDetail && _workMs < 2.0 && wall < 2.0)
		{
			_bucketCount = 0;
			return;
		}

		double gap = wall - _workMs;
		if (gap < 0)
			gap = 0;
		Buf.Append("CPUOpt2 step sum f=").Append(_frame);
		Buf.Append(" caller=").Append(_caller);
		Buf.Append(" wall=").Append(wall.ToString("0.00", CultureInfo.InvariantCulture));
		Buf.Append(" work=").Append(_workMs.ToString("0.00", CultureInfo.InvariantCulture));
		Buf.Append(" gap=").Append(gap.ToString("0.00", CultureInfo.InvariantCulture));
		if (_noted)
		{
			Buf.Append(" createLeft=").Append(_createLeft);
			Buf.Append(" tearLeft=").Append(_tearLeft);
			Buf.Append(" ops=").Append(_ops);
			Buf.Append(" panic=").Append(_panic ? 1 : 0);
		}
		AppendBuckets();
		Buf.Append('\n');

		string text = Buf.ToString();
		Buf.Length = 0;
		_bucketCount = 0;
		try
		{
			File.AppendAllText(LogPath, text);
		}
		catch { }
		if (Plugin.Log == null)
			return;
		int start = 0;
		for (int i = 0; i < text.Length; i++)
		{
			if (text[i] != '\n')
				continue;
			if (i > start)
				Plugin.Log.LogInfo(text.Substring(start, i - start));
			start = i + 1;
		}
	}

	private static void AddBucket(string step, double ms, int charge, int cx, int cy, string what)
	{
		for (int i = 0; i < _bucketCount; i++)
		{
			if (Buckets[i].Step != step)
				continue;
			Bucket b = Buckets[i];
			b.Ms += ms;
			b.Count++;
			b.Charge += charge;
			if (charge == 0)
				b.UnchargedMs += ms;
			if (ms >= b.Max)
			{
				b.Max = ms;
				b.Cx = cx;
				b.Cy = cy;
				b.What = what;
			}
			Buckets[i] = b;
			return;
		}
		if (_bucketCount >= Buckets.Length)
			return;
		Buckets[_bucketCount++] = new Bucket
		{
			Step = step,
			Ms = ms,
			UnchargedMs = charge == 0 ? ms : 0,
			Count = 1,
			Charge = charge,
			Max = ms,
			Cx = cx,
			Cy = cy,
			What = what,
		};
	}

	private static void AppendBuckets()
	{
		int n = _bucketCount;
		if (n == 0)
			return;
		if (_order.Length < n)
			_order = new int[n];
		for (int i = 0; i < n; i++)
			_order[i] = i;
		for (int i = 1; i < n; i++)
		{
			int key = _order[i];
			double keyMs = Buckets[key].Ms;
			int j = i - 1;
			while (j >= 0 && Buckets[_order[j]].Ms < keyMs)
			{
				_order[j + 1] = _order[j];
				j--;
			}
			_order[j + 1] = key;
		}
		for (int i = 0; i < n; i++)
		{
			Bucket b = Buckets[_order[i]];
			Buf.Append(" | ").Append(b.Step);
			Buf.Append(' ').Append(b.Ms.ToString("0.00", CultureInfo.InvariantCulture));
			Buf.Append("ms x").Append(b.Count);
			Buf.Append(" charge=").Append(b.Charge);
			if (b.UnchargedMs >= 0.05)
				Buf.Append(" uncharged=").Append(b.UnchargedMs.ToString("0.00", CultureInfo.InvariantCulture));
			Buf.Append(" max=").Append(b.Max.ToString("0.00", CultureInfo.InvariantCulture));
			if (b.Cx >= 0 && b.Cy >= 0)
				Buf.Append('@').Append(b.Cx).Append(',').Append(b.Cy);
			if (!string.IsNullOrEmpty(b.What))
			{
				Buf.Append(' ');
				AppendFlat(b.What);
			}
		}
	}

	private static void AppendFlat(string what)
	{
		int n = what.Length;
		if (n > 96)
			n = 96;
		for (int i = 0; i < n; i++)
		{
			char c = what[i];
			if (c == '\n' || c == '\r' || c == '|')
				c = ' ';
			Buf.Append(c);
		}
	}
}
