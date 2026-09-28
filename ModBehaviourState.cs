using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CPUOptimization2;

/// <summary>
/// simple fields on mod behaviours (not Unity, not the game, not CUCoreLib).
/// captured in memory and written back after Awake, before Start.
///
/// Start that already ran is not run again. that is the world-edit half:
/// altar disks and smelter footprints live in worldBlocks, so a second Start
/// would paint them over. a spawn component the original dropped (voodoo doll
/// after magma) is not put back.
///
/// bool side effects that are not fields get one replay: SetActive() when
/// active is true, SetOn(bool) when on is true. sprites, description, and a
/// Light2D enabled flag are copied too.
/// </summary>
internal static class ModBehaviourState
{
	internal sealed class ModState
	{
		internal List<BehaviourSnap> Behaviours = new List<BehaviourSnap>();
		internal List<string> DroppedTypes = new List<string>();
		internal string Description;
		internal bool HasDescription;
		internal List<SpriteSnap> Sprites = new List<SpriteSnap>();
		internal bool? LightEnabled;
	}

	internal sealed class BehaviourSnap
	{
		internal string TypeName;
		internal bool Foreign;
		internal bool Started;
		internal List<FieldSnap> Fields = new List<FieldSnap>();
	}

	internal sealed class FieldSnap
	{
		internal string Name;
		internal object Value;
	}

	internal sealed class SpriteSnap
	{
		internal string Path;
		internal Sprite Sprite;
		internal Color Color;
		internal Vector3 LocalScale;
		internal bool Child;
	}

	private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

	private static readonly HashSet<int> DidStart = new HashSet<int>();
	private static readonly HashSet<int> ObjectStarted = new HashSet<int>();
	private static readonly HashSet<int> SkipStart = new HashSet<int>();
	private static readonly HashSet<string> PatchedStarts = new HashSet<string>();
	private static readonly Dictionary<int, ModState> Pending = new Dictionary<int, ModState>();

	private static Harmony _harmony;
	private static bool _scannedAfterUpdate;
	private static bool _registryLooked;
	private static FieldInfo _itemRegistry;
	private static PropertyInfo _buildingView;

	internal static void Install(Harmony harmony)
	{
		_harmony = harmony;
		MethodInfo itemStart = AccessTools.Method(typeof(Item), "Start");
		if (itemStart != null)
		{
			harmony.Patch(itemStart, postfix: new HarmonyMethod(typeof(ModBehaviourState), nameof(AfterItemStart))
			{
				priority = Priority.Last,
			});
		}
		Scan();
	}

	internal static void NotifyUpdate()
	{
		if (_scannedAfterUpdate)
			return;
		_scannedAfterUpdate = true;
		Scan();
	}

	internal static void ClearRuntime()
	{
		// DidStart / ObjectStarted stay. capture Clear runs at the start of a new
		// scan, and Start may already have run on this layer. stale ids are dead objects.
		SkipStart.Clear();
		Pending.Clear();
	}

	internal static ModState Capture(GameObject go)
	{
		if (go == null)
			return null;

		ModState state = new ModState();
		MonoBehaviour[] behaviours = go.GetComponents<MonoBehaviour>();
		for (int i = 0; i < behaviours.Length; i++)
		{
			MonoBehaviour mb = behaviours[i];
			if (mb == null)
				continue;
			Type type = mb.GetType();
			bool foreign = IsForeign(type);
			bool usable = type.Name == "UsableObject";
			if (!foreign && !usable)
				continue;

			BehaviourSnap snap = new BehaviourSnap
			{
				TypeName = type.AssemblyQualifiedName,
				Foreign = foreign,
				Started = foreign && DidStart.Contains(mb.GetInstanceID()),
			};
			FieldInfo[] fields = type.GetFields(Members);
			for (int f = 0; f < fields.Length; f++)
			{
				FieldInfo field = fields[f];
				if (field.IsStatic || field.IsLiteral || field.IsInitOnly)
					continue;
				if (field.Name.IndexOf('<') >= 0)
					continue;
				if (!Storable(field.FieldType))
					continue;
				try
				{
					snap.Fields.Add(new FieldSnap { Name = field.Name, Value = field.GetValue(mb) });
				}
				catch (Exception ex)
				{
					Plugin.Log.LogWarning($"mod state read {type.Name}.{field.Name} failed: {ex.Message}");
				}
			}
			if (foreign || snap.Fields.Count > 0)
				state.Behaviours.Add(snap);
		}

		// registry spawn scripts that are gone. only after this object has started,
		// otherwise a doll captured before Item.Start looks like it already fired.
		if (ObjectStarted.Contains(go.GetInstanceID()) || go.GetComponent<Item>() == null)
			CollectDropped(go, state);

		bool interesting = state.Behaviours.Exists(b => b.Foreign) || state.DroppedTypes.Count > 0;
		if (!interesting)
			return null;

		BuildingEntity build = go.GetComponent<BuildingEntity>();
		if (build != null)
		{
			state.HasDescription = true;
			state.Description = build.description;
		}

		SpriteRenderer[] renderers = go.GetComponentsInChildren<SpriteRenderer>(true);
		for (int i = 0; i < renderers.Length; i++)
		{
			SpriteRenderer sr = renderers[i];
			if (sr == null)
				continue;
			bool child = sr.gameObject != go;
			state.Sprites.Add(new SpriteSnap
			{
				Path = child ? PathOf(go.transform, sr.transform) : "",
				Sprite = sr.sprite,
				Color = sr.color,
				LocalScale = sr.transform.localScale,
				Child = child,
			});
		}

		Light2D light = go.GetComponent<Light2D>();
		if (light != null)
			state.LightEnabled = light.enabled;

		return state;
	}

	/// <summary>items grow spawn scripts inside Item.Start. apply on the postfix, after that add.</summary>
	internal static void Expect(GameObject go, ModState state)
	{
		if (go == null || state == null)
			return;
		Pending[go.GetInstanceID()] = state;
	}

	internal static void Apply(GameObject go, ModState state)
	{
		if (go == null || state == null)
			return;

		for (int i = 0; i < state.DroppedTypes.Count; i++)
		{
			Type type = Type.GetType(state.DroppedTypes[i], throwOnError: false);
			if (type == null)
				continue;
			Component extra = go.GetComponent(type);
			if (extra == null)
				continue;
			if (extra is Behaviour behaviour)
				behaviour.enabled = false;
			UnityEngine.Object.Destroy(extra);
		}

		for (int i = 0; i < state.Behaviours.Count; i++)
		{
			BehaviourSnap snap = state.Behaviours[i];
			Type type = Type.GetType(snap.TypeName, throwOnError: false);
			if (type == null)
				continue;
			Component component = go.GetComponent(type);
			if (component == null)
				continue;
			WriteFields(component, snap.Fields);
			if (snap.Foreign)
			{
				ReplaySetters(component);
				if (snap.Started)
					SkipStart.Add(component.GetInstanceID());
			}
		}

		if (state.HasDescription)
		{
			BuildingEntity build = go.GetComponent<BuildingEntity>();
			if (build != null)
				build.description = state.Description;
		}

		for (int i = 0; i < state.Sprites.Count; i++)
		{
			SpriteSnap snap = state.Sprites[i];
			Transform target = snap.Child ? go.transform.Find(snap.Path) : go.transform;
			if (target == null)
				continue;
			SpriteRenderer sr = target.GetComponent<SpriteRenderer>();
			if (sr == null)
				continue;
			if (snap.Sprite != null)
				sr.sprite = snap.Sprite;
			sr.color = snap.Color;
			if (snap.Child)
				target.localScale = snap.LocalScale;
		}

		if (state.LightEnabled.HasValue)
		{
			Light2D light = go.GetComponent<Light2D>();
			if (light != null)
				light.enabled = state.LightEnabled.Value;
		}
	}

	private static void AfterItemStart(Item __instance)
	{
		if (__instance == null)
			return;
		int goId = __instance.gameObject.GetInstanceID();
		ObjectStarted.Add(goId);
		if (!Pending.TryGetValue(goId, out ModState state))
			return;
		Pending.Remove(goId);
		Apply(__instance.gameObject, state);
	}

	private static bool StartPrefix(object __instance)
	{
		Component component = __instance as Component;
		if (component == null)
			return true;
		if (!SkipStart.Remove(component.GetInstanceID()))
			return true;
		return false;
	}

	private static void StartPostfix(object __instance)
	{
		Component component = __instance as Component;
		if (component == null)
			return;
		DidStart.Add(component.GetInstanceID());
		ObjectStarted.Add(component.gameObject.GetInstanceID());
	}

	private static void Scan()
	{
		if (_harmony == null)
			return;
		Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
		for (int i = 0; i < asms.Length; i++)
		{
			Type[] types;
			try
			{
				types = asms[i].GetTypes();
			}
			catch (ReflectionTypeLoadException ex)
			{
				types = ex.Types;
			}
			catch
			{
				continue;
			}
			if (types == null)
				continue;
			for (int t = 0; t < types.Length; t++)
			{
				Type type = types[t];
				if (type == null || !type.IsClass || type.IsAbstract)
					continue;
				if (!typeof(MonoBehaviour).IsAssignableFrom(type))
					continue;
				if (!IsForeign(type))
					continue;
				EnsureStartPatch(type);
			}
		}
	}

	private static void EnsureStartPatch(Type type)
	{
		string key = type.AssemblyQualifiedName;
		if (string.IsNullOrEmpty(key) || !PatchedStarts.Add(key))
			return;
		MethodInfo start = AccessTools.Method(type, "Start");
		if (start == null)
			return;
		_harmony.Patch(start,
			prefix: new HarmonyMethod(typeof(ModBehaviourState), nameof(StartPrefix)),
			postfix: new HarmonyMethod(typeof(ModBehaviourState), nameof(StartPostfix)));
	}

	private static void CollectDropped(GameObject go, ModState state)
	{
		IList names = SpawnComponentNames(go);
		if (names == null)
			return;
		for (int i = 0; i < names.Count; i++)
		{
			string typeName = names[i] as string;
			if (string.IsNullOrEmpty(typeName))
				continue;
			Type type = Type.GetType(typeName, throwOnError: false);
			if (type == null)
				continue;
			if (go.GetComponent(type) != null)
				continue;
			state.DroppedTypes.Add(typeName);
		}
	}

	private static IList SpawnComponentNames(GameObject go)
	{
		EnsureRegistry();
		Item item = go.GetComponent<Item>();
		if (item != null && !string.IsNullOrEmpty(item.id) && _itemRegistry != null)
		{
			object dict = _itemRegistry.GetValue(null);
			object info = DictGet(dict, item.id);
			if (info != null)
				return FieldList(info, "SpawnComponents");
		}

		BuildingEntity build = go.GetComponent<BuildingEntity>();
		if (build != null && !string.IsNullOrEmpty(build.id) && _buildingView != null)
		{
			object view = _buildingView.GetValue(null, null);
			object def = DictGet(view, build.id);
			if (def != null)
				return FieldList(def, "SpawnComponents");
		}
		return null;
	}

	private static void EnsureRegistry()
	{
		if (_registryLooked)
			return;
		_registryLooked = true;
		Type items = AccessTools.TypeByName("CUCoreLib.Registries.ItemRegistry");
		if (items != null)
			_itemRegistry = AccessTools.Field(items, "RegisteredItems");
		Type buildings = AccessTools.TypeByName("CUCoreLib.Registries.BuildingEntityRegistry");
		if (buildings != null)
			_buildingView = AccessTools.Property(buildings, "RegisteredDefinitionsView");
	}

	private static object DictGet(object dict, string key)
	{
		if (dict == null || key == null)
			return null;
		MethodInfo tryGet = dict.GetType().GetMethod("TryGetValue");
		if (tryGet == null)
			return null;
		object[] args = { key, null };
		try
		{
			object ok = tryGet.Invoke(dict, args);
			if (ok is bool found && found)
				return args[1];
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"mod state registry lookup '{key}' failed: {ex.Message}");
		}
		return null;
	}

	private static IList FieldList(object owner, string fieldName)
	{
		FieldInfo field = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		return field?.GetValue(owner) as IList;
	}

	private static void WriteFields(Component component, List<FieldSnap> fields)
	{
		Type type = component.GetType();
		for (int i = 0; i < fields.Count; i++)
		{
			FieldSnap snap = fields[i];
			FieldInfo field = type.GetField(snap.Name, Members);
			if (field == null)
				continue;
			try
			{
				field.SetValue(component, snap.Value);
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning($"mod state write {type.Name}.{snap.Name} failed: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// active / on are the stored bit. the sprite, prompt, light, and crafting
	/// quality are applied by the setter, so a raw field write leaves them stale.
	/// </summary>
	private static void ReplaySetters(Component component)
	{
		Type type = component.GetType();
		try
		{
			FieldInfo active = type.GetField("active", Members);
			MethodInfo setActive = type.GetMethod("SetActive", Members, null, Type.EmptyTypes, null);
			if (active != null && active.FieldType == typeof(bool) && setActive != null && (bool)active.GetValue(component))
				setActive.Invoke(component, null);

			FieldInfo on = type.GetField("on", Members);
			MethodInfo setOn = type.GetMethod("SetOn", Members, null, new[] { typeof(bool) }, null);
			if (on != null && on.FieldType == typeof(bool) && setOn != null && (bool)on.GetValue(component))
			{
				on.SetValue(component, false);
				try
				{
					setOn.Invoke(component, new object[] { true });
				}
				catch
				{
					on.SetValue(component, true);
					throw;
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"mod state setter replay {type.Name} failed: {ex.Message}");
		}
	}

	private static bool IsForeign(Type type)
	{
		string asm = type.Assembly.GetName().Name;
		if (string.IsNullOrEmpty(asm))
			return false;
		if (asm == "Assembly-CSharp" || asm == "CPUOptimization2" || asm == "CUCoreLib")
			return false;
		if (asm == "mscorlib" || asm == "netstandard")
			return false;
		if (asm.StartsWith("System", StringComparison.Ordinal))
			return false;
		if (asm.StartsWith("Unity", StringComparison.Ordinal))
			return false;
		if (asm.StartsWith("BepInEx", StringComparison.Ordinal))
			return false;
		if (asm.StartsWith("0Harmony", StringComparison.Ordinal) || asm.StartsWith("HarmonyLib", StringComparison.Ordinal))
			return false;
		if (asm.StartsWith("MonoMod", StringComparison.Ordinal))
			return false;
		return true;
	}

	private static bool Storable(Type type)
	{
		if (type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
			return true;
		if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4))
			return true;
		if (type == typeof(Vector2Int) || type == typeof(Vector3Int))
			return true;
		if (type == typeof(Color) || type == typeof(Quaternion) || type == typeof(Rect))
			return true;
		return false;
	}

	private static string PathOf(Transform root, Transform target)
	{
		List<string> parts = new List<string>();
		Transform cur = target;
		while (cur != null && cur != root)
		{
			parts.Add(cur.name);
			cur = cur.parent;
		}
		parts.Reverse();
		return string.Join("/", parts);
	}
}
