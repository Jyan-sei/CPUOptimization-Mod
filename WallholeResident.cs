using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CPUOptimization2;

/// <summary>
/// stock Special/wallholes stay alive for the whole run, no stream destroy/respawn.
/// reparent them off chunk tilemaps so DestroyRenderer cant take them with the chunk.
/// sprites and particles stay on. only Light2D / AudioSource follow the stream window.
/// </summary>
internal static class WallholeResident
{
	private sealed class Entry
	{
		internal GameObject Go;
		internal Vector2Int Chunk;
		internal Light2D[] Lights;
		internal AudioSource[] Audios;
		internal ParticleSystem[] Particles;
		internal bool ExpensiveOn = true;
	}

	private static readonly List<Entry> Entries = new List<Entry>(128);
	private static readonly HashSet<int> OwnedRoots = new HashSet<int>();
	private static readonly HashSet<int> OwnedTransforms = new HashSet<int>();

	private static Transform _root;
	private static int _lastX0 = int.MinValue;
	private static int _lastY0 = int.MinValue;
	private static int _lastX1 = int.MinValue;
	private static int _lastY1 = int.MinValue;

	internal static int Count
	{
		get
		{
			int n = 0;
			for (int i = 0; i < Entries.Count; i++)
			{
				if (Entries[i] != null && Entries[i].Go != null)
					n++;
			}
			return n;
		}
	}

	internal static void Clear()
	{
		Entries.Clear();
		OwnedRoots.Clear();
		OwnedTransforms.Clear();
		_lastX0 = _lastY0 = _lastX1 = _lastY1 = int.MinValue;
		// live GOs die with the world, only drop the holder
		if (_root != null)
		{
			Object.Destroy(_root.gameObject);
			_root = null;
		}
	}

	/// <summary>
	/// find every wallholes GO, claim the transforms, reparent under a resident root.
	/// once, at capture, before the bootstrap chunk cull.
	/// </summary>
	internal static void Discover(WorldGeneration world, HashSet<int> claimedTransforms = null)
	{
		if (world == null)
			return;

		EnsureRoot(world);

		Transform[] all = Object.FindObjectsOfType<Transform>();
		int added = 0;
		for (int i = 0; i < all.Length; i++)
		{
			Transform t = all[i];
			if (t == null)
				continue;
			if (!IsWallholeName(t.name))
				continue;
			// only the root prop. kids are "Light 2D (N)" etc
			if (t.parent != null && IsWallholeName(t.parent.name))
				continue;
			if (OwnedRoots.Contains(t.GetInstanceID()))
				continue;

			Register(world, t.gameObject, claimedTransforms);
			added++;
		}

		Plugin.Log.LogInfo($"wallhole resident — tracked={Entries.Count} newlyRegistered={added}");
	}

	internal static void SyncWindow(WorldGeneration world, int x0, int y0, int x1, int y1)
	{
		if (Entries.Count == 0)
			return;

		bool windowSame = x0 == _lastX0 && y0 == _lastY0 && x1 == _lastX1 && y1 == _lastY1;
		_lastX0 = x0;
		_lastY0 = y0;
		_lastX1 = x1;
		_lastY1 = y1;

		int on = 0;
		int off = 0;
		for (int i = Entries.Count - 1; i >= 0; i--)
		{
			Entry e = Entries[i];
			if (e == null || e.Go == null)
			{
				Forget(i);
				continue;
			}

			// sprites must not stay ScreenCull-disabled
			EnsureSpritesOn(e);

			bool wantOn = e.Chunk.x >= x0 && e.Chunk.x <= x1
				&& e.Chunk.y >= y0 && e.Chunk.y <= y1;
			if (e.ExpensiveOn == wantOn)
			{
				if (wantOn)
					on++;
				else
					off++;
				continue;
			}

			SetExpensive(e, wantOn);
			if (wantOn)
				on++;
			else
				off++;
		}

		if (!windowSame && Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value)
		{
			Plugin.Log.LogInfo(
				$"wallhole resident cull window {x0},{y0}..{x1},{y1} expensiveOn={on} off={off}");
		}
	}

	internal static bool OwnsRenderer(Renderer r)
	{
		if (r == null)
			return false;
		return OwnsTransform(r.transform);
	}

	internal static bool OwnsTransform(Transform t)
	{
		if (t == null)
			return false;
		// name walk still works before Discover fills OwnedRoots (ScreenCull race)
		if (IsWallholeName(t.name))
			return true;
		for (Transform cur = t; cur != null; cur = cur.parent)
		{
			if (OwnedRoots.Contains(cur.GetInstanceID()))
				return true;
			if (IsWallholeName(cur.name))
				return true;
			if (string.Equals(cur.name, "CPUOptimization2_WallholeResident", System.StringComparison.Ordinal))
				return true;
		}
		return OwnedTransforms.Contains(t.GetInstanceID());
	}

	internal static bool IsWallholeName(string name)
	{
		if (string.IsNullOrEmpty(name))
			return false;
		string clean = Clean(name);
		return clean.IndexOf("wallholes", System.StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static void Register(WorldGeneration world, GameObject go, HashSet<int> claimedTransforms)
	{
		if (go == null)
			return;

		Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(go.transform.position));

		// pull off the chunk tilemap before stream DestroyRenderer eats the parent
		if (_root != null && go.transform.parent != _root)
			go.transform.SetParent(_root, true);

		Entry e = new Entry
		{
			Go = go,
			Chunk = chunk,
			Lights = go.GetComponentsInChildren<Light2D>(true),
			Audios = go.GetComponentsInChildren<AudioSource>(true),
			Particles = go.GetComponentsInChildren<ParticleSystem>(true),
			ExpensiveOn = true,
		};

		OwnedRoots.Add(go.GetInstanceID());
		ClaimTree(go.transform, claimedTransforms);
		// sprites have to stay drawable. undo a ScreenCull disable if one already landed
		SpriteRenderer[] sprites = go.GetComponentsInChildren<SpriteRenderer>(true);
		for (int i = 0; i < sprites.Length; i++)
		{
			if (sprites[i] != null && !sprites[i].enabled)
				sprites[i].enabled = true;
		}

		Entries.Add(e);
	}

	private static void ClaimTree(Transform root, HashSet<int> claimedTransforms)
	{
		if (root == null)
			return;
		Transform[] nodes = root.GetComponentsInChildren<Transform>(true);
		for (int i = 0; i < nodes.Length; i++)
		{
			if (nodes[i] == null)
				continue;
			int id = nodes[i].GetInstanceID();
			OwnedTransforms.Add(id);
			if (claimedTransforms != null)
				claimedTransforms.Add(id);
		}
	}

	private static void EnsureSpritesOn(Entry e)
	{
		if (e == null || e.Go == null)
			return;
		SpriteRenderer[] sprites = e.Go.GetComponentsInChildren<SpriteRenderer>(true);
		for (int i = 0; i < sprites.Length; i++)
		{
			if (sprites[i] != null && !sprites[i].enabled)
				sprites[i].enabled = true;
		}
		// particles are part of the hole look, dont gate them on the window
		if (e.Particles == null)
			return;
		for (int i = 0; i < e.Particles.Length; i++)
		{
			ParticleSystem ps = e.Particles[i];
			if (ps == null)
				continue;
			var emission = ps.emission;
			if (!emission.enabled)
				emission.enabled = true;
			if (!ps.isPlaying)
				ps.Play();
		}
	}

	private static void SetExpensive(Entry e, bool on)
	{
		e.ExpensiveOn = on;
		if (e.Lights != null)
		{
			for (int i = 0; i < e.Lights.Length; i++)
			{
				if (e.Lights[i] != null)
					e.Lights[i].enabled = on;
			}
		}
		if (e.Audios != null)
		{
			for (int i = 0; i < e.Audios.Length; i++)
			{
				AudioSource a = e.Audios[i];
				if (a == null)
					continue;
				a.enabled = on;
				if (!on && a.isPlaying)
					a.Stop();
			}
		}
	}

	private static void Forget(int index)
	{
		Entry e = Entries[index];
		if (e != null && e.Go != null)
			OwnedRoots.Remove(e.Go.GetInstanceID());
		Entries.RemoveAt(index);
	}

	private static void EnsureRoot(WorldGeneration world)
	{
		if (_root != null)
			return;

		GameObject go = new GameObject("CPUOptimization2_WallholeResident");
		if (world != null && world.worldGrid != null)
			go.transform.SetParent(world.worldGrid.transform, false);
		else
			Object.DontDestroyOnLoad(go);
		go.hideFlags = HideFlags.HideAndDontSave;
		_root = go.transform;
	}

	private static string Clean(string name)
	{
		if (string.IsNullOrEmpty(name))
			return "unknown";
		const string clone = "(Clone)";
		if (name.EndsWith(clone))
			name = name.Substring(0, name.Length - clone.Length).TrimEnd();
		return name;
	}
}
