using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CPUOptimization2;

internal static class TraderPatches
{
	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(
			AccessTools.Method(typeof(TraderScript), "Start"),
			postfix: new HarmonyMethod(typeof(TraderPatches), nameof(StartPostfix)));
	}

	/// <summary>
	/// stock Start resets rep to 100 and remakes inventory.
	/// let it run. krok's postfix adds the mp tracker, and Talker.Start makes the text over the head.
	/// then put the capture back.
	/// </summary>
	private static void StartPostfix(TraderScript __instance)
	{
		if (__instance == null)
			return;
		int id = __instance.GetInstanceID();
		if (!StreamState.TraderRestore.TryGetValue(id, out StreamState.TraderSpawnState snap))
			return;
		StreamState.TraderRestore.Remove(id);

		__instance.character = snap.GameCharacter;
		__instance.reputation = snap.Reputation;
		__instance.hostility = snap.Hostility;
		__instance.startedConvo = snap.StartedConvo;
		__instance.valueGiven = snap.ValueGiven;
		__instance.totalValueGiven = snap.TotalValueGiven;
		__instance.farEnoughToMove = snap.FarEnoughToMove;
		__instance.didMove = snap.DidMove;

		float min = snap.MoveRangeMin;
		float max = snap.MoveRangeMax;
		if (min == 0f && max == 0f)
		{
			float x = __instance.transform.position.x;
			min = x - 5f;
			max = x + 5f;
		}
		__instance.MoveRange = new RangeF(min, max);
		__instance.heightMult = snap.HeightMult;
		ApplyPose(__instance, snap.RotationZ, snap.LocalScale);
		FieldAccess.Set(__instance, "desiredPos", (Vector2)__instance.transform.position);
		FieldAccess.Set(__instance, "standAmount", 0f);

		// HasItems is the live shop, including one the player bought out.
		// an empty list used to fall through and keep the inventory Start just rolled.
		if (snap.HasItems)
		{
			__instance.items = new List<TraderItem>();
			if (snap.Items == null)
				return;
			for (int j = 0; j < snap.Items.Count; j++)
			{
				TraderItemSnap row = snap.Items[j];
				if (row == null || string.IsNullOrEmpty(row.Id))
					continue;
				__instance.items.Add(new TraderItem
				{
					id = row.Id,
					bought = row.Bought,
					value = row.Value,
					preference = row.Preference,
				});
			}
		}
	}

	/// <summary>
	/// heightMult is the limb height. euler Z is the root facing. localScale is the placed size.
	/// bonusRot is the lean toward the player and OnWillRenderObject overwrites it, so it is not stored.
	/// </summary>
	internal static void ApplyPose(TraderScript trader, float rotationZ, Vector3 localScale)
	{
		if (trader == null)
			return;
		Vector3 euler = trader.transform.eulerAngles;
		euler.z = rotationZ;
		trader.transform.eulerAngles = euler;
		if (localScale.sqrMagnitude > 0.0001f)
			trader.transform.localScale = localScale;
	}
}
