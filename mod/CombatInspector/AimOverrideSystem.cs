using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CombatInspector
{
    /// <summary>
    /// 第②步：接管瞄准。每帧把本地机甲的 <c>MouseTarget.WorldPosition</c> 写成"建议瞄准点"。
    ///
    /// 为什么必须做成 ECS 系统：MouseTarget 由 MouseInputSystem 每帧从真实鼠标写入，
    /// 而 PlayerAttackSystem 在同一帧的后面读它发起普攻。MonoBehaviour 的 Update() 跑在
    /// 整个 ECS 模拟之前，在那里写会被同帧的 MouseInputSystem 覆盖掉，所以必须插在两者之间：
    ///
    ///     MouseInputSystem  →  本系统  →  PlayerAttackSystem
    ///
    /// 只写瞄准，绝不碰移动和技能，且默认关闭、可随时热键退出。
    /// </summary>
    // 只挂组，不用 unmanaged ISystem 做排序锚点（托管 SystemBase 拿 MouseInputSystem /
    // PlayerAttackSystem 当锚点会让排序图把本系统整个丢掉，表现为注册成功但 OnUpdate 永不执行）。
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public sealed class AimOverrideSystem : SystemBase
    {
        // ------------------------------------------------------------ 控制接口（主线程写，系统读）
        // 注意命名成 Active：ComponentSystemBase 已有 Enabled 实例属性，避免歧义。
        public static volatile bool Active;
        public static int TargetIndex = -1;
        public static int TargetVersion;
        public static int LocalSlot = -1;              // -1 = 所有 PlayerTag 都接管（单机只有一个）
        public static float ProjectileSpeed = 20f;
        public static float MaxAimDistance = 45f;

        // ------------------------------------------------------------ 诊断输出
        public static string LastStatus = "未运行（OnUpdate 从未执行）";
        public static int LastWrites;                  // 最近一次 OnUpdate 成功写入的机甲数
        public static long TotalWrites;
        public static float LastAimX, LastAimY;
        public static float LastLeadSeconds;
        public static int FramesSinceValidTarget;

        private EntityQuery _mechs;
        private EntityQuery _targets;

        protected override void OnCreate()
        {
            _mechs = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PlayerTag>(),
                ComponentType.ReadWrite<MouseTarget>(),
                ComponentType.ReadOnly<LocalTransform>());

            _targets = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<EnemyTag>(),
                ComponentType.ReadOnly<LocalTransform>());
        }

        protected override void OnUpdate()
        {
            LastWrites = 0;
            if (!Active) { LastStatus = "已注册·未启用"; FramesSinceValidTarget = 0; return; }
            if (TargetIndex < 0) { LastStatus = "无目标"; FramesSinceValidTarget++; return; }
            if (_mechs.IsEmptyIgnoreFilter) { LastStatus = "无玩家实体"; return; }

            EntityManager em = EntityManager;

            // 目标必须还存在、还活着、还是同一只（version 一致）
            Entity target = Entity.Null;
            using (var es = _targets.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < es.Length; i++)
                {
                    if (es[i].Index == TargetIndex && es[i].Version == TargetVersion)
                    {
                        target = es[i];
                        break;
                    }
                }
            }
            if (target == Entity.Null)
            {
                LastStatus = "目标已消失";
                FramesSinceValidTarget++;
                TargetIndex = -1;
                return;
            }

            em.CompleteDependencyBeforeRO<LocalTransform>();
            em.CompleteDependencyBeforeRW<MouseTarget>();

            if (em.HasComponent<CharacterCurrentHP>(target) &&
                em.GetComponentData<CharacterCurrentHP>(target).Value <= 0f)
            {
                LastStatus = "目标已死亡";
                TargetIndex = -1;
                FramesSinceValidTarget++;
                return;
            }

            float3 tp = em.GetComponentData<LocalTransform>(target).Position;

            float2 vel = default(float2);
            if (em.HasComponent<CharacterMoveSpeed>(target) && em.HasComponent<CharacterMoveDirection>(target))
            {
                float2 dir = em.GetComponentData<CharacterMoveDirection>(target).Value;
                float sp = em.GetComponentData<CharacterMoveSpeed>(target).Value;
                float n = math.length(dir);
                if (n > 1e-4f && sp > 0f) vel = dir / n * sp;
            }

            float maxSq = MaxAimDistance * MaxAimDistance;

            using (var ms = _mechs.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < ms.Length; i++)
                {
                    Entity mech = ms[i];

                    // 联机时只接管自己那台机甲
                    if (LocalSlot >= 0 && em.HasComponent<NetworkMechSlot>(mech) &&
                        em.GetComponentData<NetworkMechSlot>(mech).Value != LocalSlot)
                        continue;

                    float3 mp = em.GetComponentData<LocalTransform>(mech).Position;
                    float2 rel = new float2(tp.x - mp.x, tp.y - mp.y);
                    if (math.lengthsq(rel) > maxSq) continue;

                    float t;
                    float2 hit = SolveLead(rel, vel, ProjectileSpeed, out t);

                    em.SetComponentData(mech, new MouseTarget
                    {
                        WorldPosition = new float3(mp.x + hit.x, mp.y + hit.y, mp.z)
                    });

                    LastAimX = mp.x + hit.x;
                    LastAimY = mp.y + hit.y;
                    LastLeadSeconds = t;
                    LastWrites++;
                    TotalWrites++;
                }
            }

            FramesSinceValidTarget = 0;
            LastStatus = LastWrites > 0 ? ("接管中（写入 " + LastWrites + "）") : "跳过（超距/无匹配）";
        }

        /// <summary>
        /// 解 |rel + v·t| = speed·t 的最小正根，得到命中运动目标所需的提前量。
        /// 目标比弹速快或无解时退化为瞄准当前位置。
        /// </summary>
        private static float2 SolveLead(float2 rel, float2 vel, float speed, out float tOut)
        {
            tOut = 0f;
            if (speed <= 1f) return rel;

            float a = math.dot(vel, vel) - speed * speed;
            float b = 2f * math.dot(rel, vel);
            float c = math.dot(rel, rel);

            float t = -1f;
            if (math.abs(a) < 1e-4f)
            {
                if (math.abs(b) > 1e-4f) t = -c / b;
            }
            else
            {
                float disc = b * b - 4f * a * c;
                if (disc >= 0f)
                {
                    float sq = math.sqrt(disc);
                    float t1 = (-b - sq) / (2f * a);
                    float t2 = (-b + sq) / (2f * a);
                    t = PositiveMin(t1, t2);
                }
            }

            if (t > 0f && t < 3f)
            {
                tOut = t;
                return rel + vel * t;
            }
            return rel;
        }

        private static float PositiveMin(float t1, float t2)
        {
            bool o1 = t1 > 0f, o2 = t2 > 0f;
            if (o1 && o2) return math.min(t1, t2);
            if (o1) return t1;
            if (o2) return t2;
            return -1f;
        }
    }
}
