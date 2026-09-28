using System.Collections.Generic;
using UnityEngine;

namespace CPUOptimization2;

internal sealed class LiquidRecord
{
	internal string LiquidId;
	internal float Amount;
}

internal sealed class ItemDropRecord
{
	internal string Id;
	internal float Chance;
	internal float ConditionMin;
	internal float ConditionMax;
}

internal sealed class ItemContentRecord
{
	internal string Id;
	internal float Condition;
	/// <summary>world pose. missing means spawn on the parent</summary>
	internal bool HasPose;
	internal Vector3 Position;
	internal float RotationZ;
	internal Vector3? LocalScale;
	internal List<ItemContentRecord> Children = new List<ItemContentRecord>();
	internal List<LiquidRecord> Liquids = new List<LiquidRecord>();
	internal ModBehaviourState.ModState ModState;
}

internal sealed class TraderRecord
{
	internal Vector2Int Chunk;
	internal Vector3 Position;
	internal float Health;
	internal int Character;
	internal float Reputation;
	internal float Hostility;
	internal int ValueGiven;
	internal float MoveRangeMin;
	internal float MoveRangeMax;
	internal bool FarEnoughToMove;
	internal bool DidMove;
	internal List<string> ItemIds = new List<string>();
}

internal sealed class EnemyRecord
{
	internal Vector2Int Chunk;
	internal string Id;
	internal float Health;
	/// <summary>prefab default is true. Custom Structures clears it, and CheckSeating then zeros health</summary>
	internal bool RequireGround = true;
	internal Vector3 Position;
	internal float RotationZ;
	/// <summary>spawn with localScale.x flipped (DistributeEntities randomFlip)</summary>
	internal bool? ScaleNegX;
}

internal sealed class EntityRecord
{
	internal Vector2Int Chunk;
	internal string Id;
	internal float Health;
	/// <summary>prefab default is true. Custom Structures clears it, and CheckSeating then zeros health</summary>
	internal bool RequireGround = true;
	internal Vector3 Position;
	internal float RotationZ;
	/// <summary>block the trap sits on. barbed wire / stalactites check this when the chunk updates</summary>
	internal bool HasBlockPlacedOn;
	internal Vector2Int BlockPlacedOn;
	internal bool? StalactiteDropped;
	internal float? ScrapAmount;
	internal float? CrystalSize;
	internal List<string> EffectNames = new List<string>();
	internal Vector2? RopePointA;
	internal Vector2? RopePointB;
	internal float? LengthScale;
	internal float? DownwardsVelocity;
	internal Color? Color;
	internal bool? FlipX;
	/// <summary>tiled sprite size. sandvine rope length lives here, not on a child scale</summary>
	internal Vector2? SpriteSize;
	/// <summary>root localScale.x. sandvines use this for thickness (0.15..1)</summary>
	internal float? RootScaleX;
	internal float? Pitch;
	internal bool? AnimalCorpse;
	internal List<ItemContentRecord> NestedItems = new List<ItemContentRecord>();
	/// <summary>
	/// drop table as it was on the live building, including empties.
	/// structure chests roll lootbox rows into alwaysDrop and blank the rest.
	/// a respawned prefab would put the stock table back.
	/// </summary>
	internal bool HasDropTable;
	internal float DropChanceMultiplier = 1f;
	internal int GuaranteedDropAmount;
	internal List<ItemDropRecord> ItemsDropOnDestroy = new List<ItemDropRecord>();
	internal List<ItemDropRecord> AlwaysDrop = new List<ItemDropRecord>();
	internal List<string> ItemCategoriesToAdd = new List<string>();
	/// <summary>spawn from this structure prefab's child named Id (lifepod/background etc)</summary>
	internal string StructurePrefab;
	/// <summary>
	/// StructureTilemapVault template key for worldGrid tilemaps that arent chunk
	/// tilemaps (structure backgrounds etc). use this over StructurePrefab when >= 0.
	/// </summary>
	internal int StructureVaultKey = -1;
	/// <summary>spawn with localScale.x flipped (DistributeEntities randomFlip)</summary>
	internal bool? ScaleNegX;
	/// <summary>geothermal oil pipes. SuperSecret adds MagmaPipeTag so they spill magma, not oil</summary>
	internal bool MagmaPipe;
	/// <summary>mod behaviour fields, dropped spawn scripts, and the sprite/description they drive</summary>
	internal ModBehaviourState.ModState ModState;
}

internal sealed class WorldItemRecord
{
	internal Vector2Int Chunk;
	internal string Id;
	internal float Condition;
	internal Vector3 Position;
	internal float RotationZ;
	/// <summary>structure kids (small medcrates) arent scale 1. the loose prefab is.</summary>
	internal Vector3? LocalScale;
	internal List<ItemContentRecord> Children = new List<ItemContentRecord>();
	internal List<LiquidRecord> Liquids = new List<LiquidRecord>();
	/// <summary>mod behaviour fields, dropped spawn scripts, and the sprite/description they drive</summary>
	internal ModBehaviourState.ModState ModState;
}

/// <summary>
/// free-floating world light2d. ambient / player / building / item / trader / prop lights
/// ride their entity records instead
/// </summary>
internal sealed class LightRecord
{
	internal Vector2Int Chunk;
	internal string Id;
	internal Vector3 Position;
	internal float RotationZ;
	internal float Intensity;
	internal Color Color;
	internal float OuterRadius;
	internal float InnerRadius;
	internal int LightType;
	internal float FalloffIntensity;
	internal bool ShadowEnabled;
}

internal sealed class TileCellFlip
{
	internal byte LocalX;
	internal byte LocalY;
	internal sbyte FlipX;
	internal sbyte FlipY;
	internal byte RotStep;
}

internal sealed class ChunkTileFlipRecord
{
	internal Vector2Int Chunk;
	internal List<TileCellFlip> Cells = new List<TileCellFlip>();
}
