using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// one line per chunk unload or return. prefix is CPUOpt2 item.
/// not per item and not per frame.
/// spawn jobs for two chunks can share a frame, so each chunk keeps its own tally
/// until that chunk's world-item list finishes.
/// </summary>
internal static class ItemTrace
{
	private sealed class Tally
	{
		internal int Cx, Cy;
		internal int SpawnedGhost, SpawnedWriteback, Fail, LiveSkip, Missing;
		internal readonly List<string> Ids = new List<string>(4);
		internal readonly List<Vector3> LivePos = new List<Vector3>(32);
		internal readonly List<string> LiveIds = new List<string>(32);
	}

	private static readonly Dictionary<long, Tally> Open = new Dictionary<long, Tally>();
	private static readonly StringBuilder Sb = new StringBuilder(256);
	private static long _key;
	private static Tally _cur;

	internal static void Begin(int cx, int cy)
	{
		long key = Key(cx, cy);
		if (_cur != null && _key == key)
			return;
		_key = key;
		if (!Open.TryGetValue(key, out _cur))
		{
			_cur = new Tally { Cx = cx, Cy = cy };
			Open[key] = _cur;
		}
	}

	internal static void Spawned(WorldItemRecord rec)
	{
		if (_cur == null)
			return;
		if (rec != null && rec.FromWriteback)
			_cur.SpawnedWriteback++;
		else
			_cur.SpawnedGhost++;
	}

	internal static void Fail(WorldItemRecord rec)
	{
		if (_cur == null)
			return;
		_cur.Fail++;
		NoteId(rec != null ? rec.Id : null);
	}

	internal static void LiveSkip(WorldItemRecord rec)
	{
		if (_cur == null)
			return;
		_cur.LiveSkip++;
		if (rec == null || _cur.LivePos.Count >= 32)
			return;
		_cur.LivePos.Add(rec.Position);
		_cur.LiveIds.Add(rec.Id ?? "");
	}

	internal static void Flush(int cx, int cy)
	{
		Begin(cx, cy);
		if (_cur == null)
			return;
		Tally row = _cur;
		Open.Remove(_key);
		_cur = null;

		if (row.LivePos.Count > 0)
		{
			for (int i = 0; i < row.LivePos.Count; i++)
			{
				if (ChunkRoster.AnyItemNear(row.LivePos[i], 16f))
					continue;
				row.Missing++;
				NoteId(row, row.LiveIds[i]);
			}
		}

		int spawned = row.SpawnedGhost + row.SpawnedWriteback;
		if (spawned == 0 && row.Fail == 0 && row.Missing == 0)
			return;

		Sb.Length = 0;
		Sb.Append("CPUOpt2 item spawn ").Append(row.Cx).Append(',').Append(row.Cy);
		Sb.Append(" spawned=").Append(spawned);
		Sb.Append(" ghost=").Append(row.SpawnedGhost);
		Sb.Append(" writeback=").Append(row.SpawnedWriteback);
		Sb.Append(" liveSkip=").Append(row.LiveSkip);
		Sb.Append(" missing=").Append(row.Missing);
		Sb.Append(" fail=").Append(row.Fail);
		if (row.LiveSkip > row.LivePos.Count)
			Sb.Append(" sampled=").Append(row.LivePos.Count);
		if (row.Ids.Count > 0)
		{
			Sb.Append(" ids=");
			for (int i = 0; i < row.Ids.Count; i++)
			{
				if (i > 0)
					Sb.Append('|');
				Sb.Append(row.Ids[i]);
			}
		}
		Plugin.Log.LogInfo(Sb.ToString());
	}

	internal static void Snap(int cx, int cy, int scene, int world, int claimed, int child, int carry, int lift, int kept)
	{
		if (scene == 0 && world == 0 && claimed == 0 && lift == 0 && kept == 0)
			return;
		Plugin.Log.LogInfo(
			"CPUOpt2 item snap " + cx + "," + cy
			+ " scene=" + scene
			+ " world=" + world
			+ " claimed=" + claimed
			+ " child=" + child
			+ " carry=" + carry
			+ " lift=" + lift
			+ " kept=" + kept
			+ " row=live");
	}

	private static void NoteId(string id)
	{
		if (_cur != null)
			NoteId(_cur, id);
	}

	private static void NoteId(Tally row, string id)
	{
		if (row == null || string.IsNullOrEmpty(id) || row.Ids.Count >= 4)
			return;
		row.Ids.Add(id);
	}

	private static bool NearItem(Item[] items, Vector3 pos)
	{
		if (items == null)
			return false;
		for (int i = 0; i < items.Length; i++)
		{
			Item item = items[i];
			if (item == null)
				continue;
			if ((item.transform.position - pos).sqrMagnitude <= 16f)
				return true;
		}
		return false;
	}

	private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
}
