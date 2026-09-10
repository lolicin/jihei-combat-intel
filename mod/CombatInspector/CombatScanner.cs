using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace CombatInspector
{
    /// <summary>
    /// Reads the live combat state straight out of the game's ECS world.
    ///
    /// Safety model: this is only ever called from a MonoBehaviour Update() on the main thread.
    /// Unity Entities runs its world simulation later in the player loop (PreLateUpdate), so at
    /// Update() time every job from the previous frame has already completed. This is exactly the
    /// access pattern the game's own DevTestTools MonoBehaviour uses, so it is proven in this build.
    /// </summary>
    public static class CombatScanner
    {
        public static int MaxEnemies = 600;
        public static int MaxProjectiles = 400;
        public static bool IncludeComponentNames = false;

        // ---------------------------------------------------------------- public entry

        public static CombatSnapshot Capture()
        {
            var snap = new CombatSnapshot
            {
                timestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                unityTime = Time.time,
                frameCount = Time.frameCount,
                deltaTime = Time.deltaTime
            };

            try { snap.sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
            catch { /* scene manager not ready */ }

            ReadRunContext(snap);

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                snap.worldReady = false;
                snap.error = "ECS world not ready (World.DefaultGameObjectInjectionWorld is null). Are you in a run?";
                return snap;
            }

            snap.worldReady = true;
            var em = world.EntityManager;
            try { snap.ecsElapsedTime = (float)world.Time.ElapsedTime; } catch { }

            float3 playerPos = default(float3);
            bool hasPlayerPos = false;

            Guard(snap, "players", () => { hasPlayerPos = ReadPlayers(em, snap, out playerPos); });
            Guard(snap, "allies", () => ReadAllies(em, snap, playerPos, hasPlayerPos));
            Guard(snap, "enemies", () => ReadEnemies(em, snap, playerPos, hasPlayerPos));
            Guard(snap, "bosses", () => ReadBosses(em, snap, playerPos, hasPlayerPos));
            Guard(snap, "projectiles", () => ReadProjectiles(em, snap, playerPos, hasPlayerPos));
            Guard(snap, "worldStats", () => ComputeWorldStats(em, snap));

            return snap;
        }

        private static void Guard(CombatSnapshot snap, string what, Action body)
        {
            try { body(); }
            catch (Exception ex)
            {
                snap.notes.Add("capture of " + what + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- run context

        private static void ReadRunContext(CombatSnapshot snap)
        {
            try
            {
                var gm = GameManager.Instance;
                if (gm != null)
                {
                    snap.isInGame = gm.isInGaming;
                    snap.stageIndex = gm.CurrentStageIndex;
                    snap.mechType = gm.CurrentMechSkillType.ToString();
                }
            }
            catch (Exception ex) { snap.notes.Add("GameManager read failed: " + ex.Message); }

            try
            {
                var gp = SteamNetGameplay.Instance;
                if (gp != null) snap.localSlot = gp.LocalSlot;
            }
            catch { /* networking not initialised in single player */ }

            try
            {
                var sm = SteamManager.Instance;
                if (sm != null) snap.inLobby = sm.IsInLobby;
            }
            catch { }

            // DevTestTools is the game's own dev cheat panel (F1 level ups, F2 god mode, F3 force
            // boss, F4 perk picker, F5 event picker, numpad0-6 boss AI). Report whether it is
            // actually alive in this build - if it is, you get those cheats for free.
            try
            {
                var dev = UnityEngine.Object.FindAnyObjectByType<DevTestTools>();
                snap.devToolsPresent = dev != null;
                snap.devToolsDetail = dev != null
                    ? "DevTestTools is LIVE on '" + dev.gameObject.name +
                      "' - F1/F2/F3/F4/F5 and numpad 0-6 dev cheats are active in this build"
                    : "DevTestTools not present in the loaded scenes (dev cheats are not wired up in this build)";
            }
            catch (Exception ex) { snap.devToolsDetail = "DevTestTools probe failed: " + ex.Message; }
        }

        // ---------------------------------------------------------------- players

        private static bool ReadPlayers(EntityManager em, CombatSnapshot snap, out float3 playerPos)
        {
            playerPos = default(float3);
            bool hasPos = false;

            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerTag>()))
            {
                if (q.IsEmptyIgnoreFilter) { snap.notes.Add("no PlayerTag entity found"); return false; }
                using (var arr = q.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < arr.Length; i++)
                        snap.players.Add(ReadPlayer(em, arr[i], snap.localSlot));
                }
            }

            // Resolve which one is "me": prefer the entity whose NetworkMechSlot matches the local
            // Steam slot; with no networking component there is only one candidate anyway.
            PlayerSnapshot local = null;
            for (int i = 0; i < snap.players.Count; i++)
                if (snap.players[i].isLocal) { local = snap.players[i]; break; }
            if (local == null && snap.players.Count > 0)
            {
                local = snap.players[0];
                local.isLocal = true;
            }

            snap.world.playerCount = snap.players.Count;
            if (local != null && local.hasPosition)
            {
                playerPos = new float3(local.posX, local.posY, local.posZ);
                hasPos = true;
            }
            return hasPos;
        }

        private static PlayerSnapshot ReadPlayer(EntityManager em, Entity e, int localSlot)
        {
            var s = new PlayerSnapshot { entityIndex = e.Index, entityVersion = e.Version };

            if (em.HasComponent<NetworkMechSlot>(e))
            {
                s.mechSlot = em.GetComponentData<NetworkMechSlot>(e).Value;
                s.isLocal = s.mechSlot == localSlot;
            }

            if (em.HasComponent<CharacterCurrentHP>(e)) s.hp = em.GetComponentData<CharacterCurrentHP>(e).Value;
            if (em.HasComponent<CharacterMaxHP>(e))
            {
                var m = em.GetComponentData<CharacterMaxHP>(e);
                s.maxHpBase = m.Value;
                s.maxHp = m.FinalValue > 0.0001f ? m.FinalValue : m.Value;
            }
            s.hpPct = s.maxHp > 0.0001f ? Mathf.Clamp01(s.hp / s.maxHp) : 0f;

            if (em.HasComponent<PlayerLevel>(e))
            {
                var lv = em.GetComponentData<PlayerLevel>(e);
                s.level = lv.Level; s.exp = lv.Exp; s.maxExp = lv.MaxExp; s.maxExpEffective = lv.MaxExp;
            }
            if (em.HasComponent<ExpRequiredFactorData>(e))
            {
                var f = em.GetComponentData<ExpRequiredFactorData>(e);
                s.maxExpEffective = s.maxExp * f.bonus1 * f.bonus2 * f.bonus3 * f.bonus4 * f.bonus5;
            }

            if (em.HasComponent<CharacterMoveSpeed>(e))
            {
                var ms = em.GetComponentData<CharacterMoveSpeed>(e);
                s.moveSpeed = ms.Value; s.moveSpeedBase = ms.ValueBase;
            }

            if (em.HasComponent<PlayerAttackData>(e))
            {
                var a = em.GetComponentData<PlayerAttackData>(e);
                s.attackPower = a.AttackPower;
                s.attackPowerBase = a.AttackPowerBase;
                s.attackCooldown = a.CooldownTime;
                s.attackCooldownBase = a.CooldownTimeBase;
                s.attackMoveSpeed = a.AttackMoveSpeed;
                s.detectionRadius = a.DetectionRadius;
            }

            if (em.HasComponent<PlayerSkill>(e))
            {
                var sk = em.GetComponentData<PlayerSkill>(e);
                s.skillType = sk.Type.ToString();
                s.skillCdType = sk.CDType.ToString();
                s.skillActive = sk.IsActive;
                s.canSkill = sk.CanSkill;
                s.cdingSkill = sk.CDingSkill;
                s.skillCdTimer = (float)sk.CDtimer;
                s.skillCdTime = (float)sk.CDtime;
                s.skillCdTimeBase = (float)sk.CDtimeBase;
                s.skillDamage = sk.Damage;
                s.skillTimes = sk.SkillTimes;
                s.skillMaxCharges = sk.MaxChargingTimes;
            }

            if (em.HasComponent<IronPlatingData>(e))
            {
                var p = em.GetComponentData<IronPlatingData>(e);
                s.plating = p.Value; s.platingMax = p.MaxValue; s.platingMaxBase = p.MaxValueBase;
            }

            if (em.HasComponent<BeingSpiritData>(e))
            {
                var sp = em.GetComponentData<BeingSpiritData>(e);
                s.spirit = sp.Value; s.spiritMax = sp.MaxValue; s.spiritGrowth = sp.GrowthBase;
            }

            if (em.HasComponent<PassiveRegenData>(e))
            {
                var pr = em.GetComponentData<PassiveRegenData>(e);
                s.passiveRegenRate = pr.RegenRate;
                s.passiveActivationDelay = pr.ActivationDelay;
                s.passiveTimer = pr.Timer;
                s.passiveRegenActive = pr.IsActive;
            }

            if (em.HasComponent<StormPassiveAttackSpeedData>(e))
            {
                var st = em.GetComponentData<StormPassiveAttackSpeedData>(e);
                s.stormStack = st.Stack; s.stormMaxStack = st.MaxStack;
                s.stormTimer = st.Timer; s.stormDuration = st.Duration;
            }

            if (em.HasComponent<PlayerKillCount>(e)) s.killCount = em.GetComponentData<PlayerKillCount>(e).Value;

            // 伤害模型输入：普攻基线 = AttackPower（游戏里 AttackData.AttackDamage 直接取它），
            // 暴击是生成弹体时 roll 的，所以这里只能用期望倍率折算。
            if (em.HasComponent<CritRateData>(e)) s.critRate = em.GetComponentData<CritRateData>(e).Value;
            if (em.HasComponent<CritDamageData>(e))
            {
                var cd = em.GetComponentData<CritDamageData>(e);
                s.critMult = Mathf.Max(cd.Value, cd.ChipValue);
            }
            float expectedCritMul = 1f + Mathf.Max(0f, s.critRate) * Mathf.Max(0f, s.critMult - 1f);
            s.attackDamageEstimate = s.attackPower * expectedCritMul;

            if (em.HasComponent<InjuryMitigation>(e))
            {
                s.armor = Mathf.Max(0f, em.GetComponentData<InjuryMitigation>(e).Value);
                s.armorMultiplier = Mathf.Pow(0.5f, s.armor / DamageTracker.ArmorPerHalfDamage);
            }
            s.projectileSpeed = s.attackMoveSpeed;

            float3 pos;
            if (TryGetPosition(em, e, out pos))
            {
                s.hasPosition = true; s.posX = pos.x; s.posY = pos.y; s.posZ = pos.z;
            }

            if (em.HasComponent<CharacterMoveDirection>(e))
            {
                var d = em.GetComponentData<CharacterMoveDirection>(e).Value;
                s.moveDirX = d.x; s.moveDirY = d.y;
            }

            if (em.HasComponent<MouseTarget>(e))
            {
                var mt = em.GetComponentData<MouseTarget>(e).WorldPosition;
                s.hasAim = true; s.aimX = mt.x; s.aimY = mt.y; s.aimZ = mt.z;
            }

            try
            {
                if (em.HasComponent<DevInfiniteSurvivalTag>(e))
                    s.infiniteSurvivalDevTag = em.IsComponentEnabled<DevInfiniteSurvivalTag>(e);
            }
            catch { }

            ReadBuffs(em, e, s.buffs);
            ReadDamageThisFrame(em, e, s.damageTakenThisFrame);

            if (em.HasComponent<PlayerPerk>(e))
            {
                try
                {
                    var buf = em.GetBuffer<PlayerPerk>(e, true);
                    for (int i = 0; i < buf.Length; i++)
                        s.perks.Add(new PerkEntry { perkId = buf[i].PerkID, level = buf[i].Level });
                }
                catch { }
            }

            CollectTagNames(em, e, s.tags);
            if (IncludeComponentNames) s.components = ListComponentNames(em, e);
            return s;
        }

        // ---------------------------------------------------------------- squad allies

        private static void ReadAllies(EntityManager em, CombatSnapshot snap, float3 playerPos, bool hasPlayerPos)
        {
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<SquadAllyTag>()))
            {
                if (q.IsEmptyIgnoreFilter) return;
                using (var arr = q.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < arr.Length && i < 16; i++)
                    {
                        var e = arr[i];
                        var a = new AllySnapshot { entityIndex = e.Index };

                        if (em.HasComponent<SquadAllyBrain>(e)) a.slotIndex = em.GetComponentData<SquadAllyBrain>(e).SlotIndex;
                        if (em.HasComponent<PlayerSkill>(e)) a.skillType = em.GetComponentData<PlayerSkill>(e).Type.ToString();
                        if (em.HasComponent<CharacterCurrentHP>(e)) a.hp = em.GetComponentData<CharacterCurrentHP>(e).Value;
                        if (em.HasComponent<CharacterMaxHP>(e))
                        {
                            var m = em.GetComponentData<CharacterMaxHP>(e);
                            a.maxHp = m.FinalValue > 0.0001f ? m.FinalValue : m.Value;
                        }
                        a.hpPct = a.maxHp > 0.0001f ? Mathf.Clamp01(a.hp / a.maxHp) : 0f;

                        float3 pos;
                        if (TryGetPosition(em, e, out pos))
                        {
                            a.posX = pos.x; a.posY = pos.y; a.posZ = pos.z;
                            if (hasPlayerPos) a.distanceToPlayer = XyDistance(pos, playerPos);
                        }

                        a.downed = em.HasComponent<SquadAllyDownedData>(e);
                        CollectTagNames(em, e, a.tags);
                        snap.allies.Add(a);
                    }
                }
            }
            snap.world.allyCount = snap.allies.Count;
        }

        // ---------------------------------------------------------------- enemies

        private static void ReadEnemies(EntityManager em, CombatSnapshot snap, float3 playerPos, bool hasPlayerPos)
        {
            float hpTotal = 0f;
            int elite = 0;

            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<EnemyTag>()))
            {
                if (!q.IsEmptyIgnoreFilter)
                {
                    using (var arr = q.ToEntityArray(Allocator.Temp))
                    {
                        for (int i = 0; i < arr.Length && snap.enemies.Count < MaxEnemies; i++)
                        {
                            var e = arr[i];
                            // Skip entities that are actually bosses; they get their own focused list.
                            if (em.HasComponent<BossTag>(e)) continue;

                            var s = ReadEnemyCore(em, e, playerPos, hasPlayerPos);
                            snap.enemies.Add(s);
                            hpTotal += s.hp;
                            if (s.isElite) elite++;
                        }
                        snap.notes.Add("EnemyTag entity count this frame: " + arr.Length);
                    }
                }
            }

            // Nearest first is far more useful for a dashboard than chunk order.
            snap.enemies.Sort((x, y) =>
            {
                float dx = x.distanceToPlayer < 0f ? float.MaxValue : x.distanceToPlayer;
                float dy = y.distanceToPlayer < 0f ? float.MaxValue : y.distanceToPlayer;
                return dx.CompareTo(dy);
            });

            snap.world.enemyCount = snap.enemies.Count;
            snap.world.enemyCountElite = elite;
            snap.world.enemyCountAlive = snap.enemies.Count;
            snap.world.enemyHpTotal = hpTotal;
        }

        private static EnemySnapshot ReadEnemyCore(EntityManager em, Entity e, float3 playerPos, bool hasPlayerPos)
        {
            var s = new EnemySnapshot { entityIndex = e.Index, entityVersion = e.Version };

            if (em.HasComponent<EnemyDisplayName>(e))
                s.name = em.GetComponentData<EnemyDisplayName>(e).Value.ToString();

            if (em.HasComponent<CharacterCurrentHP>(e)) s.hp = em.GetComponentData<CharacterCurrentHP>(e).Value;
            if (em.HasComponent<CharacterMaxHP>(e))
            {
                var m = em.GetComponentData<CharacterMaxHP>(e);
                s.maxHpBase = m.Value;
                s.maxHp = m.FinalValue > 0.0001f ? m.FinalValue : m.Value;
            }
            s.hpPct = s.maxHp > 0.0001f ? Mathf.Clamp01(s.hp / s.maxHp) : 0f;

            float3 pos;
            if (TryGetPosition(em, e, out pos))
            {
                s.hasPosition = true; s.posX = pos.x; s.posY = pos.y; s.posZ = pos.z;
                if (hasPlayerPos)
                {
                    s.distanceToPlayer = XyDistance(pos, playerPos);
                    var d = new float2(playerPos.x - pos.x, playerPos.y - pos.y);
                    if (math.lengthsq(d) > 1e-6f)
                        s.angleToPlayerDeg = math.degrees(math.atan2(d.y, d.x));
                }
            }

            if (em.HasComponent<CharacterMoveSpeed>(e))
            {
                var ms = em.GetComponentData<CharacterMoveSpeed>(e);
                s.moveSpeed = ms.Value; s.moveSpeedBase = ms.ValueBase;
            }
            if (em.HasComponent<CharacterMoveDirection>(e))
            {
                var md = em.GetComponentData<CharacterMoveDirection>(e).Value;
                s.moveDirX = md.x; s.moveDirY = md.y;
            }
            if (em.HasComponent<EnemyAttackData>(e))
            {
                var a = em.GetComponentData<EnemyAttackData>(e);
                s.attackDamage = a.Damage; s.attackCooldown = a.CooldownTime;
            }
            if (em.HasComponent<EnemyCurrentTarget>(e)) s.targetEntityIndex = em.GetComponentData<EnemyCurrentTarget>(e).Value.Index;
            if (em.HasComponent<LastHitBy>(e)) s.lastHitByEntityIndex = em.GetComponentData<LastHitBy>(e).Value.Index;
            if (em.HasComponent<EnemyHitFlashTimer>(e)) s.hitFlashTimer = em.GetComponentData<EnemyHitFlashTimer>(e).Remaining;
            if (em.HasComponent<HitStunTimer>(e))
            {
                float r = em.GetComponentData<HitStunTimer>(e).Remaining;
                s.hitStunned = r > 0f;
            }

            // 护甲：游戏公式 damage *= pow(0.5, armor / PerkConfig.ArmorPerHalfDamage)，阈值 100
            if (em.HasComponent<InjuryMitigation>(e))
            {
                s.armor = Mathf.Max(0f, em.GetComponentData<InjuryMitigation>(e).Value);
                s.armorMultiplier = Mathf.Pow(0.5f, s.armor / DamageTracker.ArmorPerHalfDamage);
            }

            s.isElite = em.HasComponent<EliteEnemyTag>(e);
            s.isBoss = em.HasComponent<BossTag>(e);
            if (s.isBoss) s.bossStageIndex = em.GetComponentData<BossTag>(e).StageIndex;

            if (em.HasComponent<MeleeEnemyTag>(e)) s.aiTags.Add("Melee");
            if (em.HasComponent<RangedEnemyTag>(e)) s.aiTags.Add("Ranged");
            if (em.HasComponent<FlyingMeleeEnemyTag>(e)) s.aiTags.Add("FlyingMelee");
            if (em.HasComponent<DashMeleeEnemyTag>(e)) s.aiTags.Add("DashMelee");
            if (em.HasComponent<SuicideBomberEnemyTag>(e)) s.aiTags.Add("SuicideBomber");

            try
            {
                if (em.HasComponent<EnemyMovementPaused>(e))
                    s.movementPaused = em.IsComponentEnabled<EnemyMovementPaused>(e);
            }
            catch { }

            ReadBuffs(em, e, s.buffs);
            ReadDamageThisFrame(em, e, s.damageTakenThisFrame);
            CollectTagNames(em, e, s.tags);
            if (IncludeComponentNames) s.components = ListComponentNames(em, e);
            return s;
        }

        // ---------------------------------------------------------------- bosses

        private static void ReadBosses(EntityManager em, CombatSnapshot snap, float3 playerPos, bool hasPlayerPos)
        {
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<BossTag>()))
            {
                if (q.IsEmptyIgnoreFilter) return;
                using (var arr = q.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var e = arr[i];
                        var b = new BossSnapshot
                        {
                            entityIndex = e.Index,
                            stageIndex = em.GetComponentData<BossTag>(e).StageIndex
                        };
                        if (em.HasComponent<EnemyDisplayName>(e)) b.name = em.GetComponentData<EnemyDisplayName>(e).Value.ToString();
                        if (em.HasComponent<CharacterCurrentHP>(e)) b.hp = em.GetComponentData<CharacterCurrentHP>(e).Value;
                        if (em.HasComponent<CharacterMaxHP>(e))
                        {
                            var m = em.GetComponentData<CharacterMaxHP>(e);
                            b.maxHp = m.FinalValue > 0.0001f ? m.FinalValue : m.Value;
                        }
                        b.hpPct = b.maxHp > 0.0001f ? Mathf.Clamp01(b.hp / b.maxHp) : 0f;

                        float3 pos;
                        if (TryGetPosition(em, e, out pos))
                        {
                            b.posX = pos.x; b.posY = pos.y; b.posZ = pos.z;
                            if (hasPlayerPos) b.distanceToPlayer = XyDistance(pos, playerPos);
                        }

                        if (em.HasComponent<BossBrainData>(e))
                        {
                            var br = em.GetComponentData<BossBrainData>(e);
                            b.aiMode = br.Mode; b.pendingCommand = br.PendingCommand; b.subTimer = br.SubTimer;
                        }
                        try { b.stunned = BossAnimRuntime.IsStunned(em, e); } catch { }

                        ReadBuffs(em, e, b.buffs);
                        ReadDamageThisFrame(em, e, b.damageTakenThisFrame);
                        CollectTagNames(em, e, b.tags);
                        if (IncludeComponentNames) b.components = ListComponentNames(em, e);
                        snap.bosses.Add(b);
                    }
                }
            }
            snap.world.bossCount = snap.bosses.Count;
        }

        // ---------------------------------------------------------------- projectiles / attacks

        private static void ReadProjectiles(EntityManager em, CombatSnapshot snap, float3 playerPos, bool hasPlayerPos)
        {
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<AttackData>()))
            {
                if (q.IsEmptyIgnoreFilter) return;
                using (var arr = q.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < arr.Length && snap.projectiles.Count < MaxProjectiles; i++)
                    {
                        var e = arr[i];
                        var ad = em.GetComponentData<AttackData>(e);
                        var p = new ProjectileSnapshot
                        {
                            entityIndex = e.Index,
                            attackType = ad.AttackType.ToString(),
                            damage = ad.AttackDamage,
                            moveSpeed = ad.MoveSpeed
                        };
                        if (em.HasComponent<AttackOwner>(e)) p.ownerEntityIndex = em.GetComponentData<AttackOwner>(e).Value.Index;
                        if (em.HasComponent<AttackOverTimer>(e)) p.lifeRemaining = em.GetComponentData<AttackOverTimer>(e).Value;

                        float3 pos;
                        if (TryGetPosition(em, e, out pos))
                        {
                            p.posX = pos.x; p.posY = pos.y; p.posZ = pos.z;
                            if (hasPlayerPos) p.distanceToPlayer = XyDistance(pos, playerPos);
                        }
                        snap.projectiles.Add(p);
                    }
                }
            }
            snap.world.projectileCount = snap.projectiles.Count;
        }

        // ---------------------------------------------------------------- helpers

        private static void ComputeWorldStats(EntityManager em, CombatSnapshot snap)
        {
            try
            {
                using (var all = em.CreateEntityQuery(new ComponentType[0]))
                    snap.world.totalEntities = all.CalculateEntityCount();
            }
            catch { snap.world.totalEntities = -1; }
        }

        private static bool TryGetPosition(EntityManager em, Entity e, out float3 pos)
        {
            if (em.HasComponent<LocalToWorld>(e)) { pos = em.GetComponentData<LocalToWorld>(e).Position; return true; }
            if (em.HasComponent<LocalTransform>(e)) { pos = em.GetComponentData<LocalTransform>(e).Position; return true; }
            pos = default(float3);
            return false;
        }

        /// <summary>
        /// Gameplay happens on the XY plane (the game's own helpers resolve targets with
        /// "ResolveTargetXy"); Z is only render/sort depth. So distance is measured in XY.
        /// </summary>
        private static float XyDistance(float3 a, float3 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return math.sqrt(dx * dx + dy * dy);
        }

        private static void ReadBuffs(EntityManager em, Entity e, List<BuffEntry> dst)
        {
            if (!em.HasComponent<BuffState>(e)) return;
            try
            {
                var buf = em.GetBuffer<BuffState>(e, true);
                for (int i = 0; i < buf.Length; i++)
                {
                    var b = buf[i];
                    dst.Add(new BuffEntry { type = b.BuffType.ToString(), level = b.BuffLevel, timer = b.BuffTimer });
                }
            }
            catch { }
        }

        private static void ReadDamageThisFrame(EntityManager em, Entity e, List<float> dst)
        {
            if (!em.HasComponent<DamageThisFrame>(e)) return;
            try
            {
                var buf = em.GetBuffer<DamageThisFrame>(e, true);
                for (int i = 0; i < buf.Length && i < 64; i++) dst.Add(buf[i].Value);
            }
            catch { }
        }

        /// <summary>Zero-sized marker components, i.e. the tags. Cheap and very informative.</summary>
        private static void CollectTagNames(EntityManager em, Entity e, List<string> dst)
        {
            try
            {
                var types = em.GetComponentTypes(e);
                for (int i = 0; i < types.Length; i++)
                {
                    var ct = types[i];
                    if (ct.IsZeroSized && !ct.IsSharedComponent && !ct.IsChunkComponent)
                        dst.Add(ct.ToString());
                }
                types.Dispose();
            }
            catch { }
        }

        private static List<string> ListComponentNames(EntityManager em, Entity e)
        {
            var dst = new List<string>();
            try
            {
                var types = em.GetComponentTypes(e);
                for (int i = 0; i < types.Length; i++) dst.Add(types[i].ToString());
                types.Dispose();
            }
            catch (Exception ex) { dst.Add("<error: " + ex.Message + ">"); }
            return dst;
        }
    }
}
