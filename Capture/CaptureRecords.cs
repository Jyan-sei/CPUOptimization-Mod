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
	internal BatterySnap Battery;
	/// <summary>world pose. missing means spawn on the parent</summary>
	internal bool HasPose;
	internal Vector3 Position;
	internal float RotationZ;
	internal Vector3? LocalScale;
	internal List<ItemContentRecord> Children = new List<ItemContentRecord>();
	internal List<LiquidRecord> Liquids = new List<LiquidRecord>();
	/// <summary>water container was live. empty list means drained, not "use prefab defaults"</summary>
	internal bool HasLiquids;
	/// <summary>rounds left in a mag. null when this item isnt ammo</summary>
	internal int? AmmoRounds;
	/// <summary>plush visual. Start/Awake would roll another sprite</summary>
	internal Sprite Sprite;
	internal bool? FlipX;
	internal int? PlushIndex;
	internal ModBehaviourState.ModState ModState;
	/// <summary>Together knetid. kept across a chunk cull so the rebuild rebinds that entity.</summary>
	internal bool HasNetId;
	internal ushort NetId;
}

/// <summary>shop row. ids alone used to dump every offer under Indifferent</summary>
internal sealed class TraderItemSnap
{
	internal string Id;
	internal bool Bought;
	internal int Value;
	internal TraderScript.TraderItemPreference Preference;
}

internal sealed class TraderRecord
{
	internal Vector2Int Chunk;
	internal Vector3 Position;
	internal float Health;
	/// <summary>prefab default is true. Custom Structures clears it, and CheckSeating then zeros health</summary>
	internal bool RequireGround = true;
	/// <summary>cell under the trader. a fresh prefab is (0,0), and CheckSeating treats air there as a failed seat</summary>
	internal bool HasBlockPlacedOn;
	internal Vector2Int BlockPlacedOn;
	/// <summary>MeetPlayer already ran. a fresh prefab is false, so walking up plays the intro and rerolls reputation</summary>
	internal bool StartedConvo;
	/// <summary>prefab suffix. trader1 is 1. TraderScript.character is 0..2 and is not this</summary>
	internal int Character;
	internal float Reputation;
	internal float Hostility;
	internal int ValueGiven;
	/// <summary>lifetime gifts. Start leaves this at 0, so a respawn forgets the cap</summary>
	internal int TotalValueGiven;
	/// <summary>live shop was copied, even if every row is bought. false means let Start roll</summary>
	internal bool HasItems;
	internal float MoveRangeMin;
	internal float MoveRangeMax;
	internal bool FarEnoughToMove;
	internal bool DidMove;
	/// <summary>TraderScript.heightMult. stock is 1. a new instance starts there.</summary>
	internal float HeightMult;
	/// <summary>root euler Z. spawn used to pass Quaternion.identity, so the placed facing came back at 0.</summary>
	internal float RotationZ;
	/// <summary>root localScale. gen flip is the sign of X. the prefab scale is not the placed size.</summary>
	internal Vector3 LocalScale;
	internal List<TraderItemSnap> Items = new List<TraderItemSnap>();
	/// <summary>still in the scene from gen. spawning the row would stack a copy.</summary>
	internal bool LiveAtCapture;
	/// <summary>body sprites as they were. a respawned traderN prefab would put the stock skin back</summary>
	internal List<StructureLiveState.BodySprite> BodySprites;
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
	/// <summary>still in the scene from gen. spawning the row would stack a copy.</summary>
	internal bool LiveAtCapture;
	/// <summary>full localScale when it isnt 1. flip Y and a real size, not just the sign of X</summary>
	internal Vector3? LocalScale;
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
	/// <summary>corpse / plush sprite picked on the live object. Awake would roll a new one</summary>
	internal Sprite Sprite;
	internal int? PlushIndex;
	/// <summary>Custom Structures crystal marker value. the mod's SetUpEffects patch reads it</summary>
	internal string CrystalValue;
	/// <summary>tiled sprite size. sandvine rope length lives here, not on a child scale</summary>
	internal Vector2? SpriteSize;
	/// <summary>root localScale.x. sandvines use this for thickness (0.15..1)</summary>
	internal float? RootScaleX;
	internal float? Pitch;
	internal bool? AnimalCorpse;
	/// <summary>OnWillRenderObject already played the seen line. private didComment</summary>
	internal bool CorpseSeen;
	/// <summary>SoundCannon on this building. spent is has-fired. barrel Z is child 0.</summary>
	internal bool HasSoundCannon;
	internal bool CannonSpent;
	internal bool CannonCharging;
	internal float CannonChargeTime;
	internal float CannonBarrelZ;
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
	/// <summary>full localScale when it isnt 1. wins over ScaleNegX / RootScaleX on respawn</summary>
	internal Vector3? LocalScale;
	/// <summary>still in the scene from gen. spawning the row would stack a copy.</summary>
	internal bool LiveAtCapture;
	/// <summary>door id/name. true means already backgrounded (open)</summary>
	internal bool? DoorOpen;
	internal List<StructureLiveState.LightSnap> Lights;
	internal List<StructureLiveState.MaskSnap> LightMasks;
	/// <summary>geothermal oil pipes. SuperSecret adds MagmaPipeTag so they spill magma, not oil</summary>
	internal bool MagmaPipe;
	/// <summary>mod behaviour fields, dropped spawn scripts, and the sprite/description they drive</summary>
	internal ModBehaviourState.ModState ModState;
	/// <summary>
	/// lifepod parent. buttons, controller, heater, and shower come back inside this one instantiate.
	/// </summary>
	internal bool LifepodGroup;
	/// <summary>
	/// heater or shower that was not under a lifepod. the stock fixtures stay inside the parent row.
	/// </summary>
	internal bool KeepLooseFixture;
	internal int LifepodHeatState;
	/// <summary>shower already fired. a fresh prefab would spray again</summary>
	internal bool LifepodShowerSpent;
	/// <summary>
	/// SurvivorNote. Start rolls a random locale line, so a custom wall note
	/// would come back as a different scrap of paper.
	/// </summary>
	internal bool HasNote;
	internal string NoteText;
	internal UnityEngine.Object NoteFont;
	internal Sprite NoteSprite;
	/// <summary>text painted on the note in the world. the click line is NoteText.</summary>
	internal string NoteWallText;
	internal float NoteWallSize;
	internal Color? NoteWallColor;
}

internal sealed class WorldItemRecord
{
	internal Vector2Int Chunk;
	internal string Id;
	internal float Condition;
	internal BatterySnap Battery;
	internal Vector3 Position;
	internal float RotationZ;
	/// <summary>structure kids (small medcrates) arent scale 1. the loose prefab is.</summary>
	internal Vector3? LocalScale;
	/// <summary>rounds left in a mag. null when this item isnt ammo</summary>
	internal int? AmmoRounds;
	/// <summary>plush visual. Start/Awake would roll another sprite</summary>
	internal Sprite Sprite;
	internal bool? FlipX;
	internal int? PlushIndex;
	internal List<ItemContentRecord> Children = new List<ItemContentRecord>();
	internal List<LiquidRecord> Liquids = new List<LiquidRecord>();
	/// <summary>water container was live. empty list means drained, not "use prefab defaults"</summary>
	internal bool HasLiquids;
	/// <summary>still in the scene from gen. spawning the row would stack a copy.</summary>
	internal bool LiveAtCapture;
	/// <summary>parent building is not culled (lifepod). the scene object stays, so spawn must not add another.</summary>
	internal bool HeldInPlace;
	/// <summary>Together knetid. kept across a chunk cull so the rebuild reuses that object.</summary>
	internal bool HasNetId;
	internal ushort NetId;
	/// <summary>written from a live object on unload. false means the gen ghost row.</summary>
	internal bool FromWriteback;
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
	/// <summary>still in the scene from gen. spawning the row would stack a copy.</summary>
	internal bool LiveAtCapture;
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
