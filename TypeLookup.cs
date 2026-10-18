using System;
using System.Collections.Generic;
using System.Reflection;

namespace CPUOptimization2;

/// <summary>
/// name to Type, including a miss. GetTypes runs once per assembly.
/// a ReflectionTypeLoadException keeps the types that did load and is not repeated.
/// misses from Awake are forgotten once on the first Update so a plugin that
/// loaded after us is visible. after that a miss stays a miss.
/// </summary>
internal static class TypeLookup
{
	private static readonly Dictionary<string, Type> ByName = new Dictionary<string, Type>(16);
	private static readonly HashSet<string> Miss = new HashSet<string>();
	private static readonly Dictionary<Assembly, Type[]> ByAsm = new Dictionary<Assembly, Type[]>();
	private static bool _pluginsReady;
	private static bool _missesCleared;

	internal static bool PluginsReady => _pluginsReady;

	internal static void NotePluginsReady()
	{
		if (_pluginsReady)
			return;
		_pluginsReady = true;
		if (_missesCleared)
			return;
		_missesCleared = true;
		Miss.Clear();
	}

	internal static Type Find(string name)
	{
		if (string.IsNullOrEmpty(name))
			return null;
		if (ByName.TryGetValue(name, out Type hit))
			return hit;
		if (Miss.Contains(name))
			return null;

		Type type = Type.GetType(name, throwOnError: false);
		if (type == null)
		{
			Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
			for (int i = 0; i < asms.Length && type == null; i++)
			{
				Type[] types = TypesOf(asms[i]);
				for (int t = 0; t < types.Length; t++)
				{
					Type candidate = types[t];
					if (candidate == null)
						continue;
					if (candidate.FullName == name || candidate.Name == name)
					{
						type = candidate;
						break;
					}
				}
			}
		}

		if (type != null)
			ByName[name] = type;
		else
			Miss.Add(name);
		return type;
	}

	internal static Type[] TypesOf(Assembly asm)
	{
		if (asm == null)
			return Array.Empty<Type>();
		if (ByAsm.TryGetValue(asm, out Type[] cached))
			return cached;

		Type[] types;
		try
		{
			types = asm.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			types = ex.Types ?? Array.Empty<Type>();
		}
		catch
		{
			types = Array.Empty<Type>();
		}

		if (types == null)
			types = Array.Empty<Type>();
		ByAsm[asm] = types;
		return types;
	}
}
