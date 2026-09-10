using System.Collections.Generic;

namespace CPUOptimization.Features.ChunkSim;

// spread building/item sync across frames after mp union changes
internal static class ChunkSimBodySpread
{
	private static bool _buildingsPending;
	private static bool _itemsPending;
	private static int _buildingIndex;
	private static int _itemIndex;
	private static bool _resyncPending;

	internal static bool IsActive => _buildingsPending || _itemsPending;

	internal static int BuildingsRemaining
	{
		get
		{
			if (!_buildingsPending)
				return 0;
			return BuildingSimRegistry.Count - _buildingIndex;
		}
	}

	internal static int ItemsRemaining
	{
		get
		{
			if (!_itemsPending)
				return 0;
			return Item.allItems.Count - _itemIndex;
		}
	}

	internal static void Schedule()
	{
		if (!IsActive)
		{
			_buildingsPending = true;
			_itemsPending = true;
			_buildingIndex = 0;
			_itemIndex = 0;
			_resyncPending = false;
			return;
		}

		_resyncPending = true;
	}

	internal static void Clear()
	{
		_buildingsPending = false;
		_itemsPending = false;
		_buildingIndex = 0;
		_itemIndex = 0;
		_resyncPending = false;
	}

	internal static void ProcessBatch(int batchSize)
	{
		if (!ChunkSimState.IsActive)
		{
			Clear();
			return;
		}

		batchSize = UnityEngine.Mathf.Max(1, batchSize);
		IReadOnlyList<BuildingEntity> buildings = BuildingSimRegistry.All;

		if (_buildingsPending)
		{
			if (_buildingIndex == 0)
				BuildingSimRegistry.ResetCounts();

			int end = UnityEngine.Mathf.Min(_buildingIndex + batchSize, buildings.Count);
			for (int i = _buildingIndex; i < end; i++)
			{
				BuildingEntity building = buildings[i];
				if (!building)
					continue;

				ChunkSimTrackState track = ChunkSimTrackTable.Get(building);
				track.BuildingClass = BuildingSimClass.None;
				ChunkSimBodySync.ApplyBuilding(building);
				ChunkSimBodySync.SyncBuildingUpdateEnabled(building, track);
			}

			_buildingIndex = end;
			if (_buildingIndex >= buildings.Count)
				_buildingsPending = false;
		}

		if (!_buildingsPending && _itemsPending)
		{
			if (_itemIndex == 0)
			{
				ChunkSimBodySync.ResetItemCounts();
			}

			int end = UnityEngine.Mathf.Min(_itemIndex + batchSize, Item.allItems.Count);
			for (int i = _itemIndex; i < end; i++)
			{
				Item item = Item.allItems[i];
				if (!item || item.transform.parent)
					continue;

				if (ChunkSimBodySync.ApplyItem(item))
					ChunkSimBodySync.IncrementItemsSim();
				else
					ChunkSimBodySync.IncrementItemsSleep();
			}

			_itemIndex = end;
			if (_itemIndex >= Item.allItems.Count)
				_itemsPending = false;
		}

		if (!_buildingsPending && !_itemsPending)
		{
			ChunkSimSoundCannonSync.SyncAll();
			ChunkSimForceForMPSync.SyncAll();

			if (_resyncPending)
			{
				_resyncPending = false;
				_buildingsPending = true;
				_itemsPending = true;
				_buildingIndex = 0;
				_itemIndex = 0;
			}
		}
	}
}
