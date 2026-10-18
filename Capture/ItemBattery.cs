using UnityEngine;

namespace CPUOptimization2;

/// <summary>
/// gravbag condition is charge. an empty cell is notSpawnWithBattery and a blank batteryType.
/// a fresh prefab Awake installs a cell, then condition 0 explodes.
/// </summary>
internal struct BatterySnap
{
	internal bool Captured;
	internal bool NoCell;
	internal string Type;
	internal float MaxCharge;
	internal float MaxAllowed;

	internal static BatterySnap Read(Item item)
	{
		BatterySnap snap = default;
		if (item == null || item.battery == null)
			return snap;
		BatteryItem battery = item.battery;
		snap.Captured = true;
		snap.Type = battery.batteryType;
		snap.MaxCharge = battery.maxCharge;
		snap.MaxAllowed = battery.maxAllowedCharge;
		snap.NoCell = battery.notSpawnWithBattery || string.IsNullOrEmpty(battery.batteryType);
		return snap;
	}

	/// <summary>call while the object is inactive. Awake must not install a default cell.</summary>
	internal void Prime(GameObject go, float condition)
	{
		if (go == null)
			return;
		Item item = go.GetComponent<Item>();
		if (item == null)
			return;
		item.condition = condition;
		if (!Captured || item.battery == null)
			return;
		BatteryItem battery = item.battery;
		battery.notSpawnWithBattery = true;
		if (NoCell)
		{
			battery.batteryType = "";
			battery.maxCharge = 0f;
			return;
		}
		battery.batteryType = Type ?? "";
		battery.maxCharge = MaxCharge;
		if (MaxAllowed > 0f)
			battery.maxAllowedCharge = MaxAllowed;
	}

	/// <summary>Awake has run. put the captured cell flag back.</summary>
	internal void Finish(GameObject go)
	{
		if (!Captured || go == null)
			return;
		Item item = go.GetComponent<Item>();
		if (item == null || item.battery == null)
			return;
		BatteryItem battery = item.battery;
		battery.notSpawnWithBattery = NoCell;
		if (NoCell)
		{
			battery.batteryType = "";
			battery.maxCharge = 0f;
			return;
		}
		if (!string.IsNullOrEmpty(Type))
			battery.batteryType = Type;
		battery.maxCharge = MaxCharge;
	}
}
