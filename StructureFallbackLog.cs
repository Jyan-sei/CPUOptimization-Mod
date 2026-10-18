using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// two ways a structure cell keeps a terrain tile where a prop was expected.
/// grep LogOutput for "structure tile fallback" and "structure leftover terrain".
/// fallback: SpawnEntity placed a tile and did not spawn the entity.
/// leftover: the shape character is in neither the entity map nor the block map,
/// so the cell is never written and the cave tile stays. was=2 is gravel.
/// </summary>
internal static class StructureFallbackLog
{
	sealed class CellNote
	{
		internal string Name;
		internal char Char;
		internal int X;
		internal int Y;
		internal bool Loot;
	}

	static readonly ConditionalWeakTable<object, List<CellNote>> Notes =
		new ConditionalWeakTable<object, List<CellNote>>();

	static bool _installed;
	static string _preparingName;
	static string _genName;
	static int _spawnDepth;
	static string _spawnEntity;
	static string _spawnFallback;
	static int _spawnGridX;
	static int _spawnGridY;
	static object _spawnCells;
	static int _spawnOriginX;
	static int _spawnOriginY;

	internal static void TryInstall(Harmony harmony)
	{
		if (_installed || harmony == null)
			return;

		System.Type helper = TypeLookup.Find("Custom_Structures.StructureHelper");
		System.Type patcher = TypeLookup.Find("Custom_Structures.WorldGenerationPatcher");
		if (helper == null || patcher == null)
			return;

		MethodInfo parse = AccessTools.Method(helper, "ParseStringGrid");
		MethodInfo place = AccessTools.Method(helper, "PlaceStructureIncremental");
		MethodInfo spawn = AccessTools.Method(helper, "SpawnEntity");
		MethodInfo tile = AccessTools.Method(helper, "TryPlaceCustomTile");
		MethodInfo prepare = AccessTools.Method(patcher, "PrepareStructureDefinition");
		if (parse == null || place == null || spawn == null || tile == null || prepare == null)
		{
			Plugin.Log.LogWarning("structure fallback log missed a Custom Structures method");
			return;
		}

		harmony.Patch(prepare,
			prefix: new HarmonyMethod(typeof(StructureFallbackLog), nameof(PreparePrefix)),
			postfix: new HarmonyMethod(typeof(StructureFallbackLog), nameof(PreparePostfix)));
		harmony.Patch(parse, postfix: new HarmonyMethod(typeof(StructureFallbackLog), nameof(ParsePostfix)));
		harmony.Patch(place, prefix: new HarmonyMethod(typeof(StructureFallbackLog), nameof(PlacePrefix)));
		harmony.Patch(spawn,
			prefix: new HarmonyMethod(typeof(StructureFallbackLog), nameof(SpawnPrefix)),
			postfix: new HarmonyMethod(typeof(StructureFallbackLog), nameof(SpawnPostfix)));
		harmony.Patch(tile, postfix: new HarmonyMethod(typeof(StructureFallbackLog), nameof(TilePostfix)));

		foreach (MethodInfo method in patcher.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
		{
			if (!method.Name.StartsWith("GenerateStructure"))
				continue;
			ParameterInfo[] parameters = method.GetParameters();
			if (parameters.Length == 0 || parameters[0].ParameterType != typeof(string))
				continue;
			harmony.Patch(method, prefix: new HarmonyMethod(typeof(StructureFallbackLog), nameof(NoteGen)));
		}

		_installed = true;
		Plugin.Log.LogInfo("structure fallback log on");
	}

	static void PreparePrefix(object[] __args)
	{
		_preparingName = ArgString(__args, 0);
	}

	static void PreparePostfix()
	{
		_preparingName = null;
	}

	static void NoteGen(object[] __args)
	{
		string key = ArgString(__args, 0);
		if (!string.IsNullOrEmpty(key))
			_genName = key;
	}

	static void ParsePostfix(object __result, object[] __args)
	{
		if (__result == null || __args == null || __args.Length < 14)
			return;
		string[] rows = __args[0] as string[];
		if (rows == null || rows.Length == 0 || rows[0] == null)
			return;

		object blockMap = __args[1];
		object entityMap = __args[2];
		object liquidMap = __args[10];
		object lootRules = __args[11];
		object customTiles = __args[13];

		int width = rows[0].Length;
		int flip = rows.Length - 1;
		List<CellNote> notes = null;
		string name = _preparingName ?? _genName ?? "?";
		for (int row = 0; row < rows.Length; row++)
		{
			string line = rows[row];
			if (line == null)
				continue;
			int n = line.Length < width ? line.Length : width;
			for (int col = 0; col < n; col++)
			{
				char c = line[col];
				if (Known(c, blockMap, entityMap, liquidMap, customTiles))
					continue;
				if (notes == null)
					notes = new List<CellNote>();
				notes.Add(new CellNote
				{
					Name = name,
					Char = c,
					X = col,
					Y = flip - row,
					Loot = HasKey(lootRules, c),
				});
			}
		}
		if (notes != null && !Notes.TryGetValue(__result, out _))
			Notes.Add(__result, notes);
	}

	static void PlacePrefix(object[] __args)
	{
		if (__args == null || __args.Length < 2)
			return;
		if (!(__args[0] is Vector2 worldPos))
			return;
		object struc = __args[1];
		if (struc == null || !Notes.TryGetValue(struc, out List<CellNote> notes) || notes == null)
			return;

		WorldGeneration world = WorldGeneration.world;
		if (world == null)
			return;

		int width = FieldInt(struc, "Width");
		int height = FieldInt(struc, "Height");
		Vector2Int origin = world.WorldToBlockPos(worldPos);
		int startX = origin.x - width / 2;
		int startY = origin.y - height / 2;
		int worldW = (int)world.width;
		int worldH = (int)world.height;
		for (int i = 0; i < notes.Count; i++)
		{
			CellNote note = notes[i];
			int x = startX + note.X;
			int y = startY + note.Y;
			string was = "out";
			if (x >= 0 && y >= 0 && x < worldW && y < worldH)
				was = world.GetBlock(new Vector2Int(x, y)).ToString();
			Plugin.Log.LogInfo(
				"structure leftover terrain: " + note.Name
				+ " char='" + Show(note.Char) + "'"
				+ " block=" + x + "," + y
				+ " was=" + was
				+ " loot=" + (note.Loot ? 1 : 0));
		}
	}

	static void SpawnPrefix(object[] __args)
	{
		_spawnDepth++;
		object def = __args != null && __args.Length > 0 ? __args[0] : null;
		_spawnEntity = FieldString(def, "ItemID");
		_spawnFallback = FieldString(def, "TileFallbackID");
		_spawnGridX = FieldInt(def, "GridX");
		_spawnGridY = FieldInt(def, "GridY");
		_spawnCells = __args != null && __args.Length > 2 ? __args[2] : null;
		_spawnOriginX = ArgInt(__args, 3);
		_spawnOriginY = ArgInt(__args, 4);
	}

	static void SpawnPostfix()
	{
		if (_spawnDepth > 0)
			_spawnDepth--;
		if (_spawnDepth != 0)
			return;
		if (string.IsNullOrWhiteSpace(_spawnEntity) || string.IsNullOrWhiteSpace(_spawnFallback))
			return;
		var cell = new Vector2Int(_spawnOriginX + _spawnGridX, _spawnOriginY + _spawnGridY);
		if (!HasCell(_spawnCells, cell))
			return;
		Plugin.Log.LogInfo(
			"structure tile fallback: entity=" + _spawnEntity
			+ " tile=already-placed block=" + cell.x + "," + cell.y);
	}

	static void TilePostfix(bool __result, object[] __args)
	{
		if (_spawnDepth <= 0 || !__result || __args == null || __args.Length < 3)
			return;
		if (!(__args[1] is Vector2Int position))
			return;
		string tile = __args[2] as string;
		Plugin.Log.LogInfo(
			"structure tile fallback: entity=" + (_spawnEntity ?? "-")
			+ " tile=" + (string.IsNullOrEmpty(tile) ? "-" : tile)
			+ " block=" + position.x + "," + position.y);
	}

	static bool Known(char c, object blockMap, object entityMap, object liquidMap, object customTiles)
	{
		if (c == '*' || c == '0')
			return true;
		return HasKey(entityMap, c) || HasKey(customTiles, c) || HasKey(liquidMap, c) || HasKey(blockMap, c);
	}

	static bool HasKey(object map, char c)
	{
		IDictionary dictionary = map as IDictionary;
		return dictionary != null && dictionary.Contains(c);
	}

	static bool HasCell(object set, Vector2Int cell)
	{
		if (set == null)
			return false;
		MethodInfo contains = set.GetType().GetMethod("Contains", new[] { typeof(Vector2Int) });
		if (contains == null)
			return false;
		object result = contains.Invoke(set, new object[] { cell });
		return result is bool yes && yes;
	}

	static string ArgString(object[] args, int index)
	{
		if (args == null || index < 0 || index >= args.Length)
			return null;
		return args[index] as string;
	}

	static int ArgInt(object[] args, int index)
	{
		if (args == null || index < 0 || index >= args.Length || !(args[index] is int value))
			return 0;
		return value;
	}

	static string FieldString(object obj, string name)
	{
		FieldInfo field = Field(obj, name);
		if (field == null)
			return null;
		return field.GetValue(obj) as string;
	}

	static int FieldInt(object obj, string name)
	{
		FieldInfo field = Field(obj, name);
		if (field == null)
			return 0;
		object value = field.GetValue(obj);
		return value is int number ? number : 0;
	}

	static FieldInfo Field(object obj, string name)
	{
		if (obj == null || string.IsNullOrEmpty(name))
			return null;
		return obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
	}

	static string Show(char c)
	{
		if (c >= 32 && c < 127 && c != '\'')
			return c.ToString();
		return "u+" + ((int)c).ToString("X4");
	}
}
