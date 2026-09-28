using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

/// <summary>
/// flip sprite/tilemap renderers off outside the ortho view.
/// box follows aspect, on/off sizes differ so edges dont flicker, batched so we dont thrash.
/// wallholes and ChunkBack stay off the list. dont track those, dont flip them either.
/// </summary>
internal static class ScreenCull
{
	private const int BatchSize = 96;

	private static readonly List<Renderer> Entries = new List<Renderer>(512);
	private static readonly HashSet<int> Known = new HashSet<int>();

	private static float _cullAge;
	private static float _rescanAge;
	private static int _cursor;
	private static int _lastOn;
	private static int _lastOff;
	private static float _telemetryAge;

	internal static void Clear()
	{
		Entries.Clear();
		Known.Clear();
		_cursor = 0;
		_cullAge = 0f;
		_rescanAge = 0f;
	}

	private static void ReleaseDisabled()
	{
		if (Entries.Count == 0)
			return;
		for (int i = 0; i < Entries.Count; i++)
		{
			Renderer r = Entries[i];
			if (r != null && !r.enabled)
				r.enabled = true;
		}
		Clear();
	}

	internal static void RegisterGameObject(GameObject go)
	{
		if (go == null || Plugin.ScreenCullEnabled == null || !Plugin.ScreenCullEnabled.Value)
			return;

		// whole tree under a wallhole, never track it
		if (IsWallholeBlacklisted(go.transform))
			return;

		SpriteRenderer[] sprites = go.GetComponentsInChildren<SpriteRenderer>(true);
		for (int i = 0; i < sprites.Length; i++)
			Add(sprites[i]);

		TilemapRenderer[] tiles = go.GetComponentsInChildren<TilemapRenderer>(true);
		for (int i = 0; i < tiles.Length; i++)
			Add(tiles[i]);
	}

	internal static void RegisterRenderer(Renderer r)
	{
		if (Plugin.ScreenCullEnabled == null || !Plugin.ScreenCullEnabled.Value)
			return;
		Add(r);
	}

	private static void Add(Renderer r)
	{
		if (r == null)
			return;
		if (IsNeverCull(r))
		{
			if (!r.enabled)
				r.enabled = true;
			return;
		}
		int id = r.GetInstanceID();
		if (!Known.Add(id))
			return;
		Entries.Add(r);
	}

	/// <summary>
	/// wallholes (name walk + resident ownership) and ChunkBack.
	/// name walk matters. rescan finds sprites before Discover claims them,
	/// and if they land in Entries they flicker forever.
	/// </summary>
	internal static bool IsNeverCull(Renderer r)
	{
		if (r == null)
			return false;
		if (IsChunkBackdrop(r))
			return true;
		// elder chunks stay drawable. BuildingEntity freezes the body when this renderer is off
		if (ElderResident.ProtectsRenderer(r))
			return true;
		return IsWallholeBlacklisted(r.transform);
	}

	internal static bool IsWallholeBlacklisted(Transform t)
	{
		if (t == null)
			return false;
		if (WallholeResident.OwnsTransform(t))
			return true;
		for (Transform cur = t; cur != null; cur = cur.parent)
		{
			if (WallholeResident.IsWallholeName(cur.name))
				return true;
			// resident holder root
			if (string.Equals(cur.name, "CPUOptimization2_WallholeResident", StringComparison.Ordinal))
				return true;
		}
		return false;
	}

	private static bool IsChunkBackdrop(Renderer r)
	{
		if (r == null)
			return false;
		Transform t = r.transform;
		if (t != null && string.Equals(t.name, "ChunkBack", StringComparison.Ordinal))
			return true;
		if (r is SpriteRenderer sr && sr.drawMode == SpriteDrawMode.Tiled)
		{
			Transform parent = t != null ? t.parent : null;
			if (parent != null && parent.GetComponent<ChunkScript>() != null)
				return true;
		}
		return false;
	}

	internal static void Tick(WorldGeneration world)
	{
		if (Plugin.ScreenCullEnabled == null || !Plugin.ScreenCullEnabled.Value)
		{
			// cfg flipped off mid-run. put back anything we hid, then drop the list
			ReleaseDisabled();
			return;
		}
		// bootstrap is nuking chunks, dont poke renderers mid-destroy
		if (world == null || world.generatingWorld || !StreamState.Active || StreamState.Bootstrapping)
			return;

		float dt = Time.unscaledDeltaTime;
		_cullAge += dt;
		_rescanAge += dt;
		_telemetryAge += dt;

		float rescan = Plugin.ScreenCullRescanSeconds != null ? Plugin.ScreenCullRescanSeconds.Value : 2.5f;
		if (rescan < 0.5f)
			rescan = 0.5f;
		if (_rescanAge >= rescan)
		{
			_rescanAge = 0f;
			RescanLive(world);
		}

		float interval = Plugin.ScreenCullIntervalSeconds != null ? Plugin.ScreenCullIntervalSeconds.Value : 0.35f;
		if (interval < 0.05f)
			interval = 0.05f;
		if (_cullAge < interval)
			return;
		_cullAge = 0f;

		if (!TryGetView(out Vector3 cam, out float onHalfW, out float onHalfH, out float offHalfW, out float offHalfH))
			return;

		Transform playerRoot = null;
		if (PlayerCamera.main != null && PlayerCamera.main.body != null)
			playerRoot = PlayerCamera.main.body.transform;

		int count = Entries.Count;
		if (count == 0)
			return;
		if (_cursor >= count)
			_cursor = 0;

		int batch = count <= BatchSize ? count : BatchSize;
		int on = 0;
		int off = 0;
		int flips = 0;
		int maxFlips = Plugin.ScreenCullMaxFlipsPerPass != null ? Plugin.ScreenCullMaxFlipsPerPass.Value : 32;
		if (maxFlips < 1)
			maxFlips = 1;

		for (int i = 0; i < batch; i++)
		{
			int index = _cursor + i;
			if (index >= count)
				index -= count;

			Renderer r = Entries[index];
			if (r == null)
				continue;

			// purge blacklisted stuff that slipped in before Discover, or from a race
			if (IsNeverCull(r))
			{
				if (!r.enabled)
					r.enabled = true;
				Entries[index] = null;
				Known.Remove(r.GetInstanceID());
				continue;
			}

			if (playerRoot != null && r.transform.IsChildOf(playerRoot))
			{
				if (!r.enabled)
				{
					if (flips >= maxFlips)
						continue;
					r.enabled = true;
					flips++;
				}
				on++;
				continue;
			}

			bool wantOn = WantEnabled(r, cam, onHalfW, onHalfH, offHalfW, offHalfH);
			if (r.enabled != wantOn)
			{
				if (flips >= maxFlips)
					continue;
				r.enabled = wantOn;
				flips++;
			}
			if (wantOn)
				on++;
			else
				off++;
		}

		_cursor += batch;
		_lastOn = on;
		_lastOff = off;

		float tel = Plugin.ScreenCullTelemetrySeconds != null ? Plugin.ScreenCullTelemetrySeconds.Value : 0f;
		if (tel > 0f && _telemetryAge >= tel)
		{
			_telemetryAge = 0f;
			Plugin.Log.LogInfo(
				$"screen cull — tracked={Entries.Count} batchOn~{_lastOn} batchOff~{_lastOff} " +
				$"view={onHalfW:0.#}x{onHalfH:0.#} offPad={offHalfW - onHalfW:0.#}");
		}
	}

	private static bool WantEnabled(
		Renderer r,
		Vector3 cam,
		float onHalfW,
		float onHalfH,
		float offHalfW,
		float offHalfH)
	{
		Vector3 p = r.transform.position;
		float halfW;
		float halfH;
		GetObjectHalfExtents(r, out halfW, out halfH);

		// on uses the bigger off-box so edges dont flicker. off waits for the smaller on-box
		float boxW = r.enabled ? offHalfW : onHalfW;
		float boxH = r.enabled ? offHalfH : onHalfH;

		float dx = Mathf.Abs(p.x - cam.x) - halfW;
		float dy = Mathf.Abs(p.y - cam.y) - halfH;
		if (dx < 0f)
			dx = 0f;
		if (dy < 0f)
			dy = 0f;
		return dx <= boxW && dy <= boxH;
	}

	private static void GetObjectHalfExtents(Renderer r, out float halfW, out float halfH)
	{
		if (r is TilemapRenderer)
		{
			float chunkHalf = WorldGeneration.CHUNKSIZE * 0.5f;
			halfW = chunkHalf;
			halfH = chunkHalf;
			return;
		}

		// tiled sprites (ChunkBack, structure fills). use draw size, not the +-2 prop pad
		if (r is SpriteRenderer sr && sr.drawMode == SpriteDrawMode.Tiled)
		{
			Vector2 size = sr.size;
			halfW = size.x > 0.5f ? size.x * 0.5f : WorldGeneration.CHUNKSIZE * 0.5f;
			halfH = size.y > 0.5f ? size.y * 0.5f : WorldGeneration.CHUNKSIZE * 0.5f;
			return;
		}

		// skip the bounds rebuild, the pad covers most sprites
		float pad = Plugin.ScreenCullSpritePad != null ? Plugin.ScreenCullSpritePad.Value : 2f;
		halfW = pad;
		halfH = pad;
	}

	private static bool TryGetView(
		out Vector3 cam,
		out float onHalfW,
		out float onHalfH,
		out float offHalfW,
		out float offHalfH)
	{
		cam = Vector3.zero;
		onHalfW = onHalfH = offHalfW = offHalfH = 0f;

		Camera unityCam = null;
		if (PlayerCamera.main != null)
			unityCam = PlayerCamera.main.GetComponent<Camera>();
		if (unityCam == null)
			unityCam = Camera.main;
		if (unityCam == null || !unityCam.orthographic)
			return false;

		cam = unityCam.transform.position;
		float margin = Plugin.ScreenCullMargin != null ? Plugin.ScreenCullMargin.Value : 1.15f;
		if (margin < 1f)
			margin = 1f;
		float hyst = Plugin.ScreenCullHysteresis != null ? Plugin.ScreenCullHysteresis.Value : 4f;
		if (hyst < 0f)
			hyst = 0f;

		onHalfH = unityCam.orthographicSize * margin;
		onHalfW = onHalfH * unityCam.aspect;
		offHalfH = onHalfH + hyst;
		offHalfW = onHalfW + hyst;
		return true;
	}

	private static void RescanLive(WorldGeneration world)
	{
		// drop dead refs and any blacklisted that snuck in
		for (int i = Entries.Count - 1; i >= 0; i--)
		{
			Renderer r = Entries[i];
			if (r == null || IsNeverCull(r))
			{
				if (r != null && !r.enabled)
					r.enabled = true;
				Entries.RemoveAt(i);
			}
		}
		Known.Clear();
		for (int i = 0; i < Entries.Count; i++)
		{
			if (Entries[i] != null)
				Known.Add(Entries[i].GetInstanceID());
		}
		_cursor = 0;

		// live chunk tilemaps
		if (world.renderChunks != null)
		{
			int w = world.renderChunks.GetLength(0);
			int h = world.renderChunks.GetLength(1);
			for (int x = 0; x < w; x++)
			for (int y = 0; y < h; y++)
			{
				TilemapRenderer tr = world.renderChunks[x, y];
				if (tr != null)
					Add(tr);
			}
		}

		// sprites only while stream is up. rescan interval spreads out the Find
		SpriteRenderer[] sprites = UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
		for (int i = 0; i < sprites.Length; i++)
		{
			SpriteRenderer sr = sprites[i];
			if (sr == null)
				continue;
			if (IsNeverCull(sr))
			{
				if (!sr.enabled)
					sr.enabled = true;
				continue;
			}
			Add(sr);
		}
	}
}
