using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// one line when a loose item we just spawned is destroyed, disabled, or leaves
/// its chunk before that chunk's next writeback snap. not per frame.
/// writeback and spawn are unchanged. this only watches.
/// </summary>
internal static class ItemGoneLog
{
	private sealed class Row
	{
		internal int ItemId;
		internal int GoId;
		internal int Cx;
		internal int Cy;
		internal Item Item;
		internal Transform Transform;
		internal string ItemKey;
		internal string ItemName;
		internal string DestroyCaller;
		internal bool DestroyLogged;
		internal bool DisableLogged;
		internal bool LeftLogged;
	}

	private static readonly List<Row> Rows = new List<Row>(64);
	private static readonly Dictionary<int, Row> ByItem = new Dictionary<int, Row>(64);
	private static readonly Dictionary<int, Row> ByGo = new Dictionary<int, Row>(64);

	internal static void Apply(Harmony harmony)
	{
		if (harmony == null)
			return;
		TryPatch(harmony, AccessTools.Method(typeof(Item), "OnDestroy"), nameof(ItemOnDestroyPrefix));
		TryPatch(harmony, AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy), new[] { typeof(UnityEngine.Object) }), nameof(DestroyPrefix));
		TryPatch(harmony, AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Destroy), new[] { typeof(UnityEngine.Object), typeof(float) }), nameof(DestroyPrefix));
		TryPatch(harmony, AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.DestroyImmediate), new[] { typeof(UnityEngine.Object) }), nameof(DestroyPrefix));
		TryPatch(harmony, AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.DestroyImmediate), new[] { typeof(UnityEngine.Object), typeof(bool) }), nameof(DestroyPrefix));
		TryPatch(harmony, AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive), new[] { typeof(bool) }), nameof(SetActivePrefix));
		MethodInfo enabled = AccessTools.PropertySetter(typeof(Behaviour), "enabled");
		TryPatch(harmony, enabled, nameof(EnabledPrefix));
	}

	private static void TryPatch(Harmony harmony, MethodInfo method, string prefix)
	{
		if (method == null)
			return;
		try
		{
			harmony.Patch(method, prefix: new HarmonyMethod(typeof(ItemGoneLog), prefix));
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("CPUOpt2 item watch patch " + method.Name + " failed: " + ex.Message);
		}
	}

	/// <summary>loose root on a spawn. children under another item are not the scene count.</summary>
	internal static void Watch(int cx, int cy, GameObject go)
	{
		if (go == null)
			return;
		Item[] items = go.GetComponentsInChildren<Item>(true);
		for (int i = 0; i < items.Length; i++)
		{
			Item item = items[i];
			if (item == null)
				continue;
			Transform parent = item.transform.parent;
			if (parent != null && parent.GetComponent<Item>() != null)
				continue;
			int itemId = item.GetInstanceID();
			if (ByItem.ContainsKey(itemId))
				continue;
			var row = new Row
			{
				ItemId = itemId,
				GoId = item.gameObject.GetInstanceID(),
				Cx = cx,
				Cy = cy,
				Item = item,
				Transform = item.transform,
				ItemKey = item.id ?? "",
				ItemName = item.gameObject.name ?? "",
			};
			Rows.Add(row);
			ByItem[itemId] = row;
			ByGo[row.GoId] = row;
		}
	}

	/// <summary>
	/// snap ran for this chunk. a Destroy that already ran this frame is logged here,
	/// because OnDestroy is still queued. DestroyStep's own Destroy is after this call.
	/// </summary>
	internal static void ReleaseChunk(int cx, int cy)
	{
		WorldGeneration world = WorldGeneration.world;
		for (int i = Rows.Count - 1; i >= 0; i--)
		{
			Row row = Rows[i];
			if (row.Cx != cx || row.Cy != cy)
				continue;
			if (!row.DestroyLogged && row.DestroyCaller != null)
			{
				row.DestroyLogged = true;
				Log(row, "destroy", 0, 0, row.DestroyCaller);
			}
			else if (!row.LeftLogged && !row.DestroyLogged && row.Item != null && row.Transform != null && world != null)
			{
				Vector2Int now = world.BlockToChunkPos(world.WorldToBlockPos(row.Transform.position));
				if (now.x != cx || now.y != cy)
					Log(row, "left", now.x, now.y, null);
			}
			Forget(row);
		}
	}

	internal static void Clear()
	{
		Rows.Clear();
		ByItem.Clear();
		ByGo.Clear();
	}

	internal static void Tick(WorldGeneration world)
	{
		if (world == null || Rows.Count == 0)
			return;
		for (int i = Rows.Count - 1; i >= 0; i--)
		{
			Row row = Rows[i];
			if (row.DestroyLogged)
			{
				Forget(row);
				continue;
			}
			if (row.Item == null)
			{
				// OnDestroy did not run. still say the object is gone.
				Log(row, "destroy", 0, 0, row.DestroyCaller);
				Forget(row);
				continue;
			}
			if (row.LeftLogged || row.Transform == null)
				continue;
			Vector2Int now = world.BlockToChunkPos(world.WorldToBlockPos(row.Transform.position));
			if (now.x == row.Cx && now.y == row.Cy)
				continue;
			row.LeftLogged = true;
			Log(row, "left", now.x, now.y, null);
		}
	}

	private static void ItemOnDestroyPrefix(Item __instance)
	{
		if (__instance == null || !ByItem.TryGetValue(__instance.GetInstanceID(), out Row row))
			return;
		if (row.DestroyLogged)
			return;
		row.DestroyLogged = true;
		Log(row, "destroy", 0, 0, row.DestroyCaller);
		Forget(row);
	}

	private static void DestroyPrefix(UnityEngine.Object __0)
	{
		if (__0 == null || Rows.Count == 0)
			return;
		string caller = null;
		GameObject go = __0 as GameObject;
		if (go == null && __0 is Component component)
			go = component.gameObject;
		int goId = go != null ? go.GetInstanceID() : 0;
		Transform root = go != null ? go.transform : null;
		for (int i = 0; i < Rows.Count; i++)
		{
			Row row = Rows[i];
			if (row.DestroyLogged)
				continue;
			bool hit = __0 is Item item && item.GetInstanceID() == row.ItemId;
			if (!hit && goId != 0 && goId == row.GoId)
				hit = true;
			if (!hit && root != null && row.Transform != null && row.Transform.IsChildOf(root))
				hit = true;
			if (!hit)
				continue;
			if (row.DestroyCaller != null)
				continue;
			if (caller == null)
				caller = Caller();
			row.DestroyCaller = caller;
		}
	}

	private static void SetActivePrefix(GameObject __instance, bool __0)
	{
		if (__0 || __instance == null || Rows.Count == 0)
			return;
		NoteDisable(__instance);
	}

	private static void EnabledPrefix(Behaviour __instance, bool __0)
	{
		if (__0 || Rows.Count == 0 || !(__instance is Item item))
			return;
		if (!ByItem.TryGetValue(item.GetInstanceID(), out Row row) || row.DisableLogged || row.DestroyLogged)
			return;
		row.DisableLogged = true;
		Log(row, "disable", 0, 0, Caller());
	}

	private static void NoteDisable(GameObject go)
	{
		int goId = go.GetInstanceID();
		Transform root = go.transform;
		string caller = null;
		for (int i = 0; i < Rows.Count; i++)
		{
			Row row = Rows[i];
			if (row.DisableLogged || row.DestroyLogged)
				continue;
			bool hit = goId == row.GoId;
			if (!hit && row.Transform != null && row.Transform.IsChildOf(root))
				hit = true;
			if (!hit)
				continue;
			if (caller == null)
				caller = Caller();
			row.DisableLogged = true;
			Log(row, "disable", 0, 0, caller);
		}
	}

	private static void Forget(Row row)
	{
		Rows.Remove(row);
		ByItem.Remove(row.ItemId);
		if (ByGo.TryGetValue(row.GoId, out Row current) && current == row)
			ByGo.Remove(row.GoId);
	}

	private static void Log(Row row, string kind, int nowX, int nowY, string caller)
	{
		string id = row.ItemKey;
		string name = row.ItemName;
		string pos = "";
		string active = "";
		string cond = "";
		int chunkX = row.Cx;
		int chunkY = row.Cy;
		Item item = row.Item;
		if (item != null)
		{
			try
			{
				if (!string.IsNullOrEmpty(item.id))
					id = item.id;
				if (item.gameObject != null)
				{
					name = item.gameObject.name;
					active = item.gameObject.activeSelf ? "1" : "0";
				}
				cond = item.condition.ToString("0.00", CultureInfo.InvariantCulture);
				if (row.Transform != null)
				{
					Vector3 p = row.Transform.position;
					pos = p.x.ToString("0.0", CultureInfo.InvariantCulture)
						+ ","
						+ p.y.ToString("0.0", CultureInfo.InvariantCulture);
					WorldGeneration world = WorldGeneration.world;
					if (world != null)
					{
						Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(p));
						chunkX = chunk.x;
						chunkY = chunk.y;
					}
				}
			}
			catch (Exception)
			{
				// object is mid destroy. cached id and name still go out.
			}
		}

		string line = "CPUOpt2 item " + kind
			+ " " + row.Cx + "," + row.Cy
			+ " id=" + Clean(id)
			+ " name=" + Clean(name)
			+ " pos=" + pos
			+ " chunk=" + chunkX + "," + chunkY
			+ " active=" + active
			+ " cond=" + cond;
		if (kind == "left")
			line += " now=" + nowX + "," + nowY;
		if (!string.IsNullOrEmpty(caller))
			line += " caller=" + caller;
		Plugin.Log.LogInfo(line);
	}

	private static string Clean(string value)
	{
		if (string.IsNullOrEmpty(value))
			return "";
		return value.Replace('\r', ' ').Replace('\n', ' ');
	}

	/// <summary>first frame that is not this watcher or the Destroy/SetActive trampoline.</summary>
	private static string Caller()
	{
		try
		{
			StackFrame[] frames = new StackTrace(1, false).GetFrames();
			if (frames == null)
				return null;
			for (int i = 0; i < frames.Length; i++)
			{
				MethodBase method = frames[i].GetMethod();
				Type decl = method != null ? method.DeclaringType : null;
				if (decl == null)
					continue;
				if (decl == typeof(ItemGoneLog))
					continue;
				string full = decl.FullName ?? "";
				if (full.StartsWith("HarmonyLib", StringComparison.Ordinal))
					continue;
				if (decl == typeof(UnityEngine.Object) || decl == typeof(GameObject) || decl == typeof(Behaviour))
					continue;
				return decl.Name + "." + method.Name;
			}
		}
		catch (Exception)
		{
			return null;
		}
		return null;
	}
}
