using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

/// <summary>
/// counts the objects that feed CullScriptable. Unity's own markers stay in the
/// engine, so this log is the split we can see: enabled tilemaps are the bounds
/// and dirty-chunk jobs, backdrops and wallhole sprites are scene culling.
/// </summary>
internal static class CullCensus
{
	private static float _age;

	internal static void Tick(WorldGeneration world)
	{
		float interval = Plugin.CullCensusSeconds != null ? Plugin.CullCensusSeconds.Value : 0f;
		if (interval <= 0f || world == null || world.renderChunks == null)
			return;

		_age += Time.unscaledDeltaTime;
		if (_age < interval)
			return;
		_age = 0f;

		int x0 = 0;
		int y0 = 0;
		int x1 = -1;
		int y1 = -1;
		bool haveWindow = PlayerCamera.main != null;
		if (haveWindow)
			StreamController.GetWindowAround(world, PlayerCamera.main.transform.position, out x0, out y0, out x1, out y1);

		int tilemaps = 0;
		int tileOn = 0;
		int tileOut = 0;
		int tileVisible = 0;
		int backs = 0;
		int backsOn = 0;
		int backLeak = 0;

		int width = world.renderChunks.GetLength(0);
		int height = world.renderChunks.GetLength(1);
		for (int x = 0; x < width; x++)
		for (int y = 0; y < height; y++)
		{
			TilemapRenderer renderer = world.renderChunks[x, y];
			if (renderer == null)
				continue;
			tilemaps++;
			bool inWindow = haveWindow && x >= x0 && x <= x1 && y >= y0 && y <= y1;
			if (renderer.enabled)
			{
				tileOn++;
				if (haveWindow && !inWindow)
					tileOut++;
				if (renderer.isVisible)
					tileVisible++;
			}

			Transform root = renderer.transform;
			int children = root.childCount;
			for (int i = 0; i < children; i++)
			{
				Transform child = root.GetChild(i);
				if (child == null || child.name != "ChunkBack")
					continue;
				SpriteRenderer sprite = child.GetComponent<SpriteRenderer>();
				if (sprite == null)
					continue;
				backs++;
				if (!sprite.enabled)
					continue;
				backsOn++;
				if (!renderer.enabled)
					backLeak++;
			}
		}

		WallholeResident.CountDraw(out int wallRoots, out int wallSpritesOn, out int wallFxOn);
		ChunkRoster.CountRenderers(out int roster, out int rosterOn, out int rosterVisible);

		Plugin.Log.LogInfo(
			"cull census"
			+ " tileOn=" + tileOn + "/" + tilemaps
			+ " tileOut=" + tileOut
			+ " tileVisible=" + tileVisible
			+ " backOn=" + backsOn + "/" + backs
			+ " backLeak=" + backLeak
			+ " wallSprite=" + wallSpritesOn + "/" + wallRoots
			+ " wallFx=" + wallFxOn
			+ " rosterSprite=" + rosterOn + "/" + roster
			+ " rosterVisible=" + rosterVisible
			+ (haveWindow ? " window=" + x0 + "," + y0 + ".." + x1 + "," + y1 : " window=none"));
	}
}
