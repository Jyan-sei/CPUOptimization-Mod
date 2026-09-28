using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CPUOptimization2;

/// <summary>
/// structure pieces (lifepod backgrounds, shower/heater etc) come from prefab kids in
/// GenerateEntityAtPos, not Resources.Load(childName).
/// live stream restore uses StructureTilemapVault for worldGrid tilemaps.
/// this catalog still feeds ghost-gen, and its the spawn fallback.
/// </summary>
internal static class StructureCatalog
{
	private static readonly string[] PrefabPaths =
	{
		"Lifepod",
		"BioContainer",
		"Structures/SteelBridge",
		"Structures/CratePod",
		"Structures/MiniPod",
		"Structures/MedicalBuilding",
		"Structures/BrickLoot",
		"Structures/LongCorridor",
		"Structures/CrystalSpawnPlatform",
		"Structures/SteelThing",
		"Structures/WoodCross",
		"Structures/WoodHorizontal",
	};

	private static Dictionary<string, string> _childToPrefab;
	private static Dictionary<string, GameObject> _childPrefabs;

	internal static void EnsureIndex()
	{
		if (_childToPrefab != null)
			return;

		_childToPrefab = new Dictionary<string, string>();
		_childPrefabs = new Dictionary<string, GameObject>();

		for (int i = 0; i < PrefabPaths.Length; i++)
		{
			string path = PrefabPaths[i];
			GameObject root = Resources.Load<GameObject>(path);
			if (root == null)
				continue;

			for (int c = 0; c < root.transform.childCount; c++)
			{
				Transform child = root.transform.GetChild(c);
				string name = Clean(child.name);
				if (string.IsNullOrEmpty(name))
					continue;

				// first match wins. lifepod is first so its "background" sticks
				if (_childToPrefab.ContainsKey(name))
					continue;

				bool isPiece = child.GetComponent<Tilemap>() != null
					|| child.GetComponent<BuildingEntity>() != null
					|| child.GetComponent<Item>() != null
					|| name == "DOSPAWN"
					|| name.StartsWith("lifepod", System.StringComparison.OrdinalIgnoreCase)
					|| name.IndexOf("background", System.StringComparison.OrdinalIgnoreCase) >= 0;

				if (!isPiece)
					continue;

				_childToPrefab[name] = path;
				_childPrefabs[name] = child.gameObject;
			}
		}

		Plugin.Log.LogInfo($"structure catalog indexed {_childToPrefab.Count} children from {PrefabPaths.Length} prefabs");
	}

	/// <summary>
	/// Lifepod / BioContainer child 0 is the block stamp. GenerateObjectAtPos already
	/// wrote it into worldBlocks. spawning it paints those terrain sprites over the
	/// real interior (KMPSR). MedicalBuilding stamps its root, so Back still gets placed.
	/// </summary>
	internal static bool IsBlockStamp(GameObject prefab, Transform child)
	{
		if (prefab == null || child == null || child.GetSiblingIndex() != 0)
			return false;
		if (child.GetComponent<Tilemap>() == null)
			return false;
		if (child.GetComponent<BuildingEntity>() != null || child.GetComponent<Item>() != null)
			return false;
		return StampsChildZero(prefab.name);
	}

	internal static bool IsBlockStampRecord(EntityRecord rec)
	{
		if (rec == null || string.IsNullOrEmpty(rec.Id))
			return false;
		if (!FileName(rec.Id).Equals("Map", System.StringComparison.OrdinalIgnoreCase))
			return false;
		return StampsChildZero(rec.StructurePrefab);
	}

	private static bool StampsChildZero(string prefabNameOrPath)
	{
		string file = FileName(prefabNameOrPath);
		return file.Equals("Lifepod", System.StringComparison.OrdinalIgnoreCase)
			|| file.Equals("BioContainer", System.StringComparison.OrdinalIgnoreCase)
			|| file.Equals("TutorialStructure", System.StringComparison.OrdinalIgnoreCase)
			|| file.Equals("LifepodStart", System.StringComparison.OrdinalIgnoreCase)
			|| file.Equals("LifepodCollapsed", System.StringComparison.OrdinalIgnoreCase);
	}

	private static string FileName(string prefabNameOrPath)
	{
		if (string.IsNullOrEmpty(prefabNameOrPath))
			return "";
		int slash = prefabNameOrPath.LastIndexOf('/');
		string file = slash >= 0 ? prefabNameOrPath.Substring(slash + 1) : prefabNameOrPath;
		const string clone = "(Clone)";
		if (file.EndsWith(clone))
			file = file.Substring(0, file.Length - clone.Length).TrimEnd();
		return file;
	}

	/// <summary>
	/// resource path for this prefab asset. child name "background" is on several pods,
	/// so the first catalog hit (lifepod) is the wrong interior for a crate/mini pod.
	/// </summary>
	internal static string PathForInstance(GameObject prefab)
	{
		if (prefab == null)
			return null;
		EnsureIndex();
		string name = Clean(prefab.name);
		for (int i = 0; i < PrefabPaths.Length; i++)
		{
			string path = PrefabPaths[i];
			int slash = path.LastIndexOf('/');
			string file = slash >= 0 ? path.Substring(slash + 1) : path;
			if (string.Equals(file, name, System.StringComparison.OrdinalIgnoreCase))
				return path;
		}
		if (name.IndexOf("Lifepod", System.StringComparison.OrdinalIgnoreCase) >= 0)
			return "Lifepod";
		return null;
	}

	internal static bool TryResolve(GameObject go, string idOrName, out string prefabPath)
	{
		EnsureIndex();
		prefabPath = null;
		if (go == null && string.IsNullOrEmpty(idOrName))
			return false;

		string name = !string.IsNullOrEmpty(idOrName) ? Clean(idOrName) : Clean(go.name);
		if (_childToPrefab.TryGetValue(name, out prefabPath))
			return true;

		// tilemaps under worldGrid are always structure kids
		if (go != null && go.GetComponent<Tilemap>() != null && go.GetComponent<ChunkScript>() == null)
		{
			if (_childToPrefab.TryGetValue("background", out prefabPath) && name.IndexOf("background", System.StringComparison.OrdinalIgnoreCase) >= 0)
				return true;
		}

		return false;
	}

	internal static GameObject InstantiateChild(string prefabPath, string childName, Vector3 position, float rotationZ, Transform worldGrid)
	{
		EnsureIndex();
		// "background" is on lifepod, crate pod, and mini pod. the name map keeps lifepod.
		// the record's prefab path is the one GenerateEntityAtPos actually stamped
		GameObject childPrefab = FindChildOnPrefab(prefabPath, childName);
		if (childPrefab == null)
			_childPrefabs.TryGetValue(Clean(childName), out childPrefab);

		if (childPrefab == null)
			return null;

		GameObject go = ChunkObjects.InstantiateInactive(childPrefab, position, Quaternion.Euler(0f, 0f, rotationZ));
		if (go.GetComponent<Tilemap>() != null && worldGrid != null)
			go.transform.SetParent(worldGrid, true);
		return go;
	}

	private static GameObject FindChildOnPrefab(string prefabPath, string childName)
	{
		if (string.IsNullOrEmpty(prefabPath) || string.IsNullOrEmpty(childName))
			return null;
		GameObject root = Resources.Load<GameObject>(prefabPath);
		if (root == null)
			return null;
		string want = Clean(childName);
		for (int c = 0; c < root.transform.childCount; c++)
		{
			Transform child = root.transform.GetChild(c);
			if (Clean(child.name) == want)
				return child.gameObject;
		}
		return null;
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
