using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CombatInspector
{
    /// <summary>
    /// 接管瞄准的状态与执行逻辑。
    ///
    /// 为什么是个静态类而不是 ECS 系统：这里原本是一个托管 SystemBase，想靠
    /// [UpdateInGroup(SimulationSystemGroup)] + [UpdateAfter(MouseInputSystem)]
    /// + [UpdateBefore(PlayerAttackSystem)] 插在"输入写 MouseTarget"和"攻击读 MouseTarget"
    /// 之间。实测三种注册方式（AddSystemManaged / GetOrCreateSystemManaged / 只留
    /// UpdateInGroup+OrderLast）之后，OnUpdate 一次都没执行过 —— 这个世界的模拟由
    /// unmanaged ISystem 驱动，托管 SystemBase 不进更新循环，而且失败是静默的
    /// （注册"成功"、日志漂亮、什么都不干）。详见 README §7.4。
    ///
    /// 现在由 AimTakeoverPatch 用 Harmony postfix 驱动 ApplyAim()，时机正好在
    /// MouseInputSystem 自己写完 MouseTarget 之后、PlayerAttackSystem 读它之前。
    ///
    /// 只写瞄准，绝不碰移动和技能；默认关闭，热键随时退出。
    /// </summary>
    public static class AimOverrideSystem
    {
        // -------------------------------------------------------- 控制状态（主线程写）
        public static volatile bool Active;
        public static Entity MechEntity;      // 本地机甲，由 Runner 每次抓取时解析
        public static Entity TargetEntity;    // 当前焦点敌人，同上
        public static float ProjectileSpeed = 20f;
        public static float MaxAimDistance = 45f;

        // -------------------------------------------------------- 诊断输出
        public static string LastStatus = "补丁未执行";
        public static int LastWrites;
        public static long TotalWrites;
        public static long PatchRunCount;
        public static string PatchError;
        public static float LastAimX, LastAimY;
        public static float LastLeadSeconds;

        /// <summary>
        /// 一帧的接管逻辑：读焦点敌人当前位置、算拦截提前量、写 MouseTarget。
        /// 由 Harmony postfix 在 MouseInputSystem 之后调用。
        /// </summary>
        public static void ApplyAim(EntityManager em)
        {
            LastWrites = 0;
            if (!Active) { LastStatus = "已注册·未启用"; return; }
            if (em == null) { LastStatus = "EntityManager 不可用"; return; }

            Entity mech = MechEntity;
            if (mech == Entity.Null) { LastStatus = "无本地机甲实体"; return; }

            Entity target = TargetEntity;
            if (target == Entity.Null) { LastStatus = "无焦点目标"; return; }

            try
            {
                if (!em.Exists(mech) || !em.HasComponent<MouseTarget>(mech))
                {
                    LastStatus = "机甲实体已失效";
                    MechEntity = Entity.Null;
                    return;
                }
                if (!em.Exists(target) || !em.HasComponent<LocalTransform>(target))
                {
                    LastStatus = "目标已消失";
                    TargetEntity = Entity.Null;
                    return;
                }
                if (em.HasComponent<CharacterCurrentHP>(target) &&
                    em.GetComponentData<CharacterCurrentHP>(target).Value <= 0f)
                {
                    LastStatus = "目标已死亡";
                    TargetEntity = Entity.Null;
                    return;
                }

                float3 mp = em.GetComponentData<LocalTransform>(mech).Position;
                float3 tp = em.GetComponentData<LocalTransform>(target).Position;

                float2 rel = new float2(tp.x - mp.x, tp.y - mp.y);
                if (math.lengthsq(rel) > MaxAimDistance * MaxAimDistance)
                {
                    LastStatus = "目标超出接管距离";
                    return;
                }

                float2 vel = default(float2);
                if (em.HasComponent<CharacterMoveSpeed>(target) && em.HasComponent<CharacterMoveDirection>(target))
                {
                    float2 dir = em.GetComponentData<CharacterMoveDirection>(target).Value;
                    float sp = em.GetComponentData<CharacterMoveSpeed>(target).Value;
                    float n = math.length(dir);
                    if (n > 1e-4f && sp > 0f) vel = dir / n * sp;
                }

                float t;
                float2 hit = SolveLead(rel, vel, ProjectileSpeed, out t);
                float2 aim = mp.xy + hit;

                em.SetComponentData(mech, new MouseTarget
                {
                    WorldPosition = new float3(aim.x, aim.y, mp.z)
                });

                LastAimX = aim.x;
                LastAimY = aim.y;
                LastLeadSeconds = t;
                LastWrites = 1;
                TotalWrites++;
                LastStatus = "接管中（补丁写入）";
            }
            catch (System.Exception ex)
            {
                if (PatchError == null)
                    PatchError = "ApplyAim " + ex.GetType().Name + ": " + ex.Message;
                LastStatus = "写入异常（见 PatchError）";
            }
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
                    t = PositiveMin((-b - sq) / (2f * a), (-b + sq) / (2f * a));
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
