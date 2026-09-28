using System;
using System.Reflection;
using HarmonyLib;

namespace CPUOptimization2;

internal static class FieldAccess
{
	internal static T Get<T>(object target, string name)
	{
		if (target == null)
			return default;
		FieldInfo field = AccessTools.Field(target.GetType(), name);
		if (field == null)
			return default;
		object value = field.GetValue(target);
		if (value is T typed)
			return typed;
		return default;
	}

	internal static void Set(object target, string name, object value)
	{
		if (target == null)
			return;
		FieldInfo field = AccessTools.Field(target.GetType(), name);
		field?.SetValue(target, value);
	}

	internal static T GetStatic<T>(Type type, string name)
	{
		FieldInfo field = AccessTools.Field(type, name);
		if (field == null)
			return default;
		object value = field.GetValue(null);
		if (value is T typed)
			return typed;
		return default;
	}
}
