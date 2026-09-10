using System.Collections.Generic;

namespace CombatInspector
{
    // Plain DTOs. Newtonsoft.Json (shipped by the game) serialises public fields by default.

    public sealed class CombatSnapshot
    {
        public string schema = "jhx9676.combat-inspector/1";
        public long timestampUnixMs;
        public float unityTime;
        public float ecsElapsedTime;
        public int frameCount;
        public float deltaTime;

        public bool worldReady;
        public bool isInGame;
        public bool inLobby;
        public int localSlot;
        public int stageIndex;
        public string mechType;
        public string sceneName;

        // The game ships a dev cheat MonoBehaviour (DevTestTools: F1-F5, numpad0-6). Whether it is
        // actually attached to a scene in this release decides if those keys already work for you.
        public bool devToolsPresent;
        public string devToolsDetail;

        // Diagnostics for the in-game windows (IMGUI positioning bugs are otherwise invisible).
        public int screenWidth;
        public int screenHeight;
        public string radarInfo;
        public string barsInfo;
        public int damageTrackerEntries;
        public string autoAimInfo;
        public string navInfo;

        public string error;
        public List<string> notes = new List<string>();

        public WorldStats world = new WorldStats();
        public List<PlayerSnapshot> players = new List<PlayerSnapshot>();
        public List<AllySnapshot> allies = new List<AllySnapshot>();
        public List<BossSnapshot> bosses = new List<BossSnapshot>();
        public List<EnemySnapshot> enemies = new List<EnemySnapshot>();
        public List<ProjectileSnapshot> projectiles = new List<ProjectileSnapshot>();

        // Present only when a deep dump was requested (reflective, every component of every field).
        public Dictionary<string, object> deep;

        // 只读战术建议层（不接管操作，只给出"打谁 / 往哪瞄 / 往哪走"）
        public AdvisorAdvice advice;
    }

    /// <summary>一个候选目标的打分明细，用来人工校验决策质量。</summary>
    public sealed class TargetCandidate
    {
        public int entityIndex;
        public string name;
        public float score;
        public float distance;
        public float hp;
        public float maxHp;
        public int hitsToKill = -1;
        public bool killableNow;
        public bool isElite;
        public bool isBoss;
        public string aiTags;
        public string why;
    }

    /// <summary>建议层输出。全部由快照数据推导，不写任何游戏状态。</summary>
    public sealed class AdvisorAdvice
    {
        public bool active;
        public string noTargetReason;

        public int targetEntityIndex = -1;
        public string targetName;
        public float targetDistance;
        public float targetHp;
        public float targetMaxHp;
        public int targetHitsToKill = -1;
        public bool targetKillableNow;
        public string targetWhy;

        public bool hasAim;
        public float aimX, aimY;
        public float aimLeadSeconds;

        // 游戏里实际的 MouseTarget 与建议瞄准点的距离。开启接管瞄准后应趋近 0，
        // 这是"接管到底有没有生效"的量化证据。
        public float aimDiff;

        public bool hasMove;
        public float moveX, moveY;
        public string moveLabel;

        // 走位的路径感知（边界 + 障碍）
        public float tacticalX, tacticalY;      // 只看敌人时想要的方向
        public bool moveAdjustedForWalls;        // 因为墙/障碍改过方向
        public bool trapped;                     // 16 个方向全被挡
        public float wallDistance = -1f;         // 到最近墙/边界的距离
        public float pathClearance;              // 选定方向上到第一个障碍的距离
        public bool navAvailable;

        public int threatCount;
        public int incomingCount;
        public float dangerScore;          // 0..1 综合危险度
        public string dangerLabel;         // 安全 / 注意 / 危险 / 危急

        public List<TargetCandidate> ranking = new List<TargetCandidate>();
    }

    public sealed class WorldStats
    {
        // -1 when the count could not be obtained on this Entities version.
        public int totalEntities = -1;
        public int playerCount;
        public int allyCount;
        public int enemyCount;
        public int bossCount;
        public int projectileCount;
        public int enemyCountElite;
        public int enemyCountAlive;
        public float enemyHpTotal;
    }

    public sealed class BuffEntry
    {
        public string type;
        public float level;
        public float timer;
    }

    public sealed class PerkEntry
    {
        public int perkId;
        public int level;
    }

    public sealed class PlayerSnapshot
    {
        public int entityIndex;
        public int entityVersion;
        public bool isLocal;
        public int mechSlot = -1;

        public float hp;
        public float maxHp;          // CharacterMaxHP.FinalValue (the effective cap)
        public float maxHpBase;      // CharacterMaxHP.Value
        public float hpPct;

        public int level;
        public float exp;
        public float maxExp;
        public float maxExpEffective;

        public float moveSpeed;
        public float moveSpeedBase;

        public float attackPower;
        public float attackPowerBase;
        public float attackCooldown;
        public float attackCooldownBase;
        public float attackMoveSpeed;
        public float detectionRadius;

        public string skillType;
        public string skillCdType;
        public bool skillActive;
        public bool canSkill;
        public bool cdingSkill;
        public float skillCdTimer;
        public float skillCdTime;
        public float skillCdTimeBase;
        public float skillDamage;
        public int skillTimes;
        public int skillMaxCharges;

        public float plating;
        public float platingMax;
        public float platingMaxBase;

        public float spirit;
        public float spiritMax;
        public float spiritGrowth;

        public float passiveTimer;
        public float passiveActivationDelay;
        public float passiveRegenRate;
        public bool passiveRegenActive;

        public int stormStack;
        public int stormMaxStack;
        public float stormTimer;
        public float stormDuration;

        public int killCount;

        // 伤害模型输入（"预计下次受击后生命"在没实测数据时的兜底估算）
        public float critRate;
        public float critMult;
        public float attackDamageEstimate;   // AttackPower × 期望暴击倍率，未计目标护甲
        public float armor;                  // 自身 InjuryMitigation.Value
        public float armorMultiplier = 1f;   // pow(0.5, armor/100)，用于估算敌人打我有多疼
        public float projectileSpeed;        // PlayerAttackData.AttackMoveSpeed，算提前量用

        public float posX, posY, posZ;
        public bool hasPosition;
        public float moveDirX, moveDirY;
        public float aimX, aimY, aimZ;
        public bool hasAim;

        public bool infiniteSurvivalDevTag;

        public List<BuffEntry> buffs = new List<BuffEntry>();
        public List<PerkEntry> perks = new List<PerkEntry>();
        public List<float> damageTakenThisFrame = new List<float>();
        public List<string> tags = new List<string>();
        public List<string> components;
    }

    public sealed class AllySnapshot
    {
        public int entityIndex;
        public int slotIndex;
        public string skillType;
        public float hp;
        public float maxHp;
        public float hpPct;
        public float posX, posY, posZ;
        public float distanceToPlayer;
        public bool downed;
        public List<string> tags = new List<string>();
    }

    public sealed class BossSnapshot
    {
        public int entityIndex;
        public string name;
        public int stageIndex;
        public float hp;
        public float maxHp;
        public float hpPct;
        public float posX, posY, posZ;
        public float distanceToPlayer;
        public byte aiMode;
        public byte pendingCommand;
        public float subTimer;
        public bool stunned;
        public List<string> tags = new List<string>();
        public List<BuffEntry> buffs = new List<BuffEntry>();
        public List<float> damageTakenThisFrame = new List<float>();
        public List<string> components;
    }

    public sealed class EnemySnapshot
    {
        public int entityIndex;
        public int entityVersion;
        public string name;
        public List<string> aiTags = new List<string>();

        public float hp;
        public float maxHp;
        public float maxHpBase;
        public float hpPct;

        public float posX, posY, posZ;
        public bool hasPosition;
        public float distanceToPlayer = -1f;
        public float angleToPlayerDeg = float.NaN;

        public float moveSpeed;
        public float moveSpeedBase;
        public float moveDirX, moveDirY;

        public int attackDamage;
        public float attackCooldown;

        public int targetEntityIndex = -1;
        public int lastHitByEntityIndex = -1;

        public bool isElite;
        public bool isBoss;
        public int bossStageIndex = -1;
        public bool movementPaused;
        public bool hitStunned;
        public float hitFlashTimer;

        public List<BuffEntry> buffs = new List<BuffEntry>();
        public List<float> damageTakenThisFrame = new List<float>();
        public List<string> tags = new List<string>();
        public List<string> components;

        // ---- 伤害预测：实测为主（DamageThisFrame 是游戏结算后的真值，已含暴击/乘区/护甲），模型兜底
        public float armor;                  // InjuryMitigation.Value
        public float armorMultiplier = 1f;   // pow(0.5, armor / ArmorPerHalfDamage)
        public float lastHitDamage;
        public float avgHitDamage;
        public float maxHitDamage;
        public int hitsObserved;
        public float predictedNextDamage;
        public string predictedDamageSource;   // "实测" | "实测+模型" | "模型"
        public float predictedHpAfter;
        public bool willDieFromNextHit;
        public int hitsToKill = -1;
        public float timeToKill = -1f;         // 秒，按玩家攻击间隔粗估
    }

    public sealed class ProjectileSnapshot
    {
        public int entityIndex;
        public int ownerEntityIndex = -1;
        public string attackType;
        public float damage;
        public float moveSpeed;
        public float posX, posY, posZ;
        public float distanceToPlayer = -1f;
        public float lifeRemaining;
    }
}
