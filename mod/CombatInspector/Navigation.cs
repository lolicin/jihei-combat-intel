using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;

namespace CombatInspector
{
    /// <summary>
    /// 地图边界与障碍感知。走位建议必须知道"哪里走不进去"，否则被围时很可能建议往墙角里退，
    /// 把自己卡死 —— 这是自动战斗的致命失误。
    ///
    /// 三个真实数据源（都不是猜的）：
    ///   1. IronPlatingRuntime.TryGetCameraPlayArea —— 游戏自己的 public static，返回可玩区矩形，
    ///      它自己刷怪、画弹射、生成事件都在用；
    ///   2. MapBoundaryWallData —— 每段墙的内侧线段 + 法线，能表达斜墙；
    ///   3. PhysicsWorldSingleton 的 OverlapAabb —— 沿每个方向逐步外推小方框，覆盖所有碰撞体
    ///      （帐篷、集装箱、岩石…不需要预先知道名字和尺寸）。
    ///
    /// 为什么用 OverlapAabb 而不是 CastRay：RaycastInput 的 QueryContext 是 internal 字段，
    /// 用对象初始化器构造时它保持全零，导致 CastRay 在本项目里永远返回 false
    /// （实测 725 次刷新、上万次探测零命中且零异常）。而 OverlapAabb 是游戏自己在
    /// BeingFieldSystem 里用出效果的 API。顺带一提，方框探测比射线更贴合"身体能不能挤过去"。
    /// </summary>
    public static class Navigation
    {
        public const int Directions = 16;

        public static float ProbeDistance = 3.2f;   // 每个方向探多远
        public static int ProbeSteps = 4;           // 分几段外推（分辨率 = ProbeDistance / ProbeSteps）
        public static float ProbeHalf = 0.30f;      // 探测方框半边长（近似角色身体半径）
        public static float WallMargin = 2.2f;      // 离墙多近开始排斥

        private struct Wall
        {
            public float2 a;
            public float2 b;
            public float2 inward;
            public byte side;
        }

        private static readonly List<Wall> _walls = new List<Wall>(16);
        private static readonly bool[] _blocked = new bool[Directions];
        private static readonly float[] _clear = new float[Directions];
        private static readonly bool[] _isCreature = new bool[Directions];
        private static readonly int[] _blockers = new int[Directions];   // 每个方向的阻挡实体 index，-1=无

        private static float _minX, _minY, _maxX, _maxY;
        private static bool _hasPlayArea;

        // ------------------------------------------------------------ 对外诊断
        public static bool HasPlayArea { get { return _hasPlayArea; } }
        public static float2 PlayMin { get { return new float2(_minX, _minY); } }
        public static float2 PlayMax { get { return new float2(_maxX, _maxY); } }
        public static int WallCount { get { return _walls.Count; } }
        public static int BlockedCount { get; private set; }
        public static int CreatureBlockedCount { get; private set; }
        public static bool AnyOpen { get; private set; }
        public static int Directions_Total { get { return Directions; } }

        public static long TotalRayHits { get; private set; }
        public static long RefreshCount { get; private set; }
        public static bool PhysicsSingletonFound { get; private set; }
        public static string LastError { get; private set; }
        public static long ProbeExceptionCount { get; private set; }

        private static void NoteError(string where, Exception ex)
        {
            if (LastError != null) return;
            LastError = where + " " + ex.GetType().Name + ": " + ex.Message;
        }

        public static bool BlockedAt(int i) { return i >= 0 && i < Directions && _blocked[i]; }
        public static bool CreatureAt(int i) { return i >= 0 && i < Directions && _isCreature[i]; }
        public static float BlockedFraction(int i) { return (i >= 0 && i < Directions) ? _clear[i] : 0f; }

        /// <summary>当前各方向的阻挡实体 index 列表（逗号分隔），用于诊断"挡住我的到底是什么"。</summary>
        public static string DescribeBlockers()
        {
            var sb = new System.Text.StringBuilder();
            bool first = true;
            for (int i = 0; i < Directions; i++)
            {
                if (_blockers[i] < 0) continue;
                if (!first) sb.Append(',');
                sb.Append(_isCreature[i] ? 'c' : 't');   // creature / terrain
                sb.Append(_blockers[i]);
                first = false;
            }
            return sb.ToString();
        }

        public static int CopyWallSegments(List<float4> dst)
        {
            dst.Clear();
            for (int i = 0; i < _walls.Count; i++)
                dst.Add(new float4(_walls[i].a.x, _walls[i].a.y, _walls[i].b.x, _walls[i].b.y));
            return dst.Count;
        }

        // ------------------------------------------------------------ 每帧刷新

        public static void Refresh(EntityManager em, Entity playerEntity, float3 playerPos)
        {
            BlockedCount = 0;
            CreatureBlockedCount = 0;
            AnyOpen = false;
            for (int i = 0; i < Directions; i++)
            {
                _blocked[i] = false;
                _isCreature[i] = false;
                _clear[i] = ProbeDistance;
                _blockers[i] = -1;
            }

            // ---- 1) 可玩区矩形
            try
            {
                float2 mn, mx;
                if (IronPlatingRuntime.TryGetCameraPlayArea(out mn, out mx))
                {
                    _hasPlayArea = true;
                    _minX = mn.x; _minY = mn.y; _maxX = mx.x; _maxY = mx.y;
                }
                else _hasPlayArea = false;
            }
            catch (Exception ex) { _hasPlayArea = false; NoteError("取可玩区失败:", ex); }

            // ---- 2) 墙段
            _walls.Clear();
            try
            {
                using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<MapBoundaryWallData>()))
                {
                    if (!q.IsEmptyIgnoreFilter)
                    {
                        using (var arr = q.ToEntityArray(Allocator.Temp))
                        {
                            for (int i = 0; i < arr.Length; i++)
                            {
                                if (!em.HasComponent<MapBoundaryWallData>(arr[i])) continue;
                                MapBoundaryWallData d = em.GetComponentData<MapBoundaryWallData>(arr[i]);
                                _walls.Add(new Wall
                                {
                                    a = d.SegmentStart,
                                    b = d.SegmentEnd,
                                    inward = d.InwardNormal,
                                    side = (byte)d.Side
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { NoteError("读墙段失败:", ex); }

            // ---- 3) 物理世界
            PhysicsWorldSingleton phys = default(PhysicsWorldSingleton);
            bool havePhys = false;
            try
            {
                using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<PhysicsWorldSingleton>()))
                {
                    if (!q.IsEmptyIgnoreFilter) { phys = q.GetSingleton<PhysicsWorldSingleton>(); havePhys = true; }
                }
            }
            catch (Exception ex) { havePhys = false; NoteError("取 PhysicsWorldSingleton 失败:", ex); }
            PhysicsSingletonFound = havePhys;

            if (havePhys)
            {
                for (int i = 0; i < Directions; i++)
                {
                    float ang = (360f * i / Directions) * Mathf.Deg2Rad;
                    float2 dir = new float2(math.cos(ang), math.sin(ang));

                    float d; Entity hitEnt;
                    bool got = ProbeAlongDirection(phys, em, playerEntity, playerPos, dir, out d, out hitEnt);
                    if (got)
                    {
                        TotalRayHits++;
                        _clear[i] = d;
                        _blockers[i] = hitEnt.Index;

                        bool creature = false;
                        try
                        {
                            if (em.Exists(hitEnt) &&
                                (em.HasComponent<EnemyTag>(hitEnt) || em.HasComponent<PlayerTag>(hitEnt) ||
                                 em.HasComponent<SquadAllyTag>(hitEnt)))
                                creature = true;
                        }
                        catch { }

                        // 生物挡的不算死路，只作次选降权；只有地形/墙才置 blocked。
                        if (creature) { _isCreature[i] = true; CreatureBlockedCount++; }
                        else { _blocked[i] = true; BlockedCount++; }
                    }

                    if (!_blocked[i]) AnyOpen = true;
                }
            }
            else
            {
                for (int i = 0; i < Directions; i++) AnyOpen = true;
            }

            // ---- 4) 墙段也当阻挡看（墙数据比探测更权威，能补上探针步长漏掉的薄墙）
            for (int i = 0; i < Directions; i++)
            {
                float ang = (360f * i / Directions) * Mathf.Deg2Rad;
                float2 dir = new float2(math.cos(ang), math.sin(ang));
                float wd = DistanceAlongDirToWall(playerPos.xy, dir);
                if (wd < ProbeDistance)
                {
                    if (!_blocked[i]) { _clear[i] = wd; _blocked[i] = true; BlockedCount++; }
                    else if (wd < _clear[i]) _clear[i] = wd;
                }
                if (!_blocked[i]) AnyOpen = true;
            }

            RefreshCount++;
        }

        /// <summary>沿 dir 逐步外推小方框，返回第一次"不是自己"的命中距离与实体。</summary>
        private static bool ProbeAlongDirection(PhysicsWorldSingleton phys, EntityManager em, Entity self,
                                                float3 origin, float2 dir, out float dist, out Entity hitEntity)
        {
            dist = ProbeDistance;
            hitEntity = Entity.Null;

            for (int s = 1; s <= ProbeSteps; s++)
            {
                float dd = ProbeDistance * s / ProbeSteps;
                float2 c = origin.xy + dir * dd;
                var box = new Aabb
                {
                    Min = new float3(c.x - ProbeHalf, c.y - ProbeHalf, origin.z - 1f),
                    Max = new float3(c.x + ProbeHalf, c.y + ProbeHalf, origin.z + 1f)
                };

                var hits = new NativeList<int>(Allocator.Temp);
                bool any = false;
                try
                {
                    any = phys.OverlapAabb(new OverlapAabbInput { Aabb = box, Filter = CollisionFilter.Default }, ref hits);
                }
                catch (Exception ex)
                {
                    hits.Dispose();
                    ProbeExceptionCount++;
                    NoteError("OverlapAabb 失败:", ex);
                    return false;
                }

                bool blocked = false;
                for (int h = 0; h < hits.Length; h++)
                {
                    int bi = hits[h];
                    if (bi < 0 || bi >= phys.Bodies.Length) continue;
                    Entity he = phys.Bodies[bi].Entity;
                    if (he.Equals(self)) continue;           // 自己的碰撞体
                    if (!em.Exists(he)) continue;

                    // 排除 trigger / 不响应碰撞的体积（不阻挡移动，比如新加的机库大结构上的检测区）——
                    // 之前"四面受阻"的元凶嫌疑。读不到时宁可算阻挡（fail-safe 方向）。
                    try
                    {
                        var crp = phys.Bodies[bi].Collider.Value.GetCollisionResponse();
                        if (crp == CollisionResponsePolicy.RaiseTriggerEvents || crp == CollisionResponsePolicy.None)
                            continue;
                    }
                    catch (Exception ex) { NoteError("读 CollisionResponse 失败:", ex); }

                    hitEntity = he;
                    blocked = true;
                    break;
                }
                hits.Dispose();

                if (blocked) { dist = dd; return true; }
            }
            return false;
        }

        /// <summary>沿 dir 走多远会撞墙（用内侧法线判断这个方向是否在朝墙走）。</summary>
        private static float DistanceAlongDirToWall(float2 p, float2 dir)
        {
            float best = float.MaxValue;
            for (int i = 0; i < _walls.Count; i++)
            {
                Wall w = _walls[i];
                float2 ab = w.b - w.a;
                float len2 = math.lengthsq(ab);
                if (len2 < 1e-6f) continue;
                float t = math.clamp(math.dot(p - w.a, ab) / len2, 0f, 1f);
                float2 closest = w.a + ab * t;
                float toward = math.dot(dir, -w.inward);       // >0 表示这个方向在朝墙走
                if (toward <= 0.25f) continue;
                float signed = math.dot(p - closest, w.inward); // >0 在墙内侧
                float d = signed > 0f ? signed : math.length(p - closest);
                if (d < best) best = d;
            }
            return best == float.MaxValue ? ProbeDistance : best;
        }

        /// <summary>靠近墙/边界时的推离方向，以及到最近墙的距离。</summary>
        public static float2 WallRepulsion(float2 p, out float nearestWallDistance)
        {
            float2 sum = float2.zero;
            nearestWallDistance = float.MaxValue;

            for (int i = 0; i < _walls.Count; i++)
            {
                Wall w = _walls[i];
                float2 ab = w.b - w.a;
                float len2 = math.lengthsq(ab);
                if (len2 < 1e-6f) continue;
                float t = math.clamp(math.dot(p - w.a, ab) / len2, 0f, 1f);
                float2 closest = w.a + ab * t;
                float d = math.length(p - closest);
                if (d < 0.0001f) continue;
                if (d < nearestWallDistance) nearestWallDistance = d;
                if (d < WallMargin)
                {
                    float wgt = (WallMargin - d) / WallMargin;
                    sum += (p - closest) / d * wgt * 2.4f;
                }
            }

            if (_hasPlayArea)
            {
                float2 mn = new float2(_minX, _minY), mx = new float2(_maxX, _maxY);
                if (p.x < mn.x + WallMargin) sum.x += (mn.x + WallMargin - p.x) * 1.2f;
                if (p.x > mx.x - WallMargin) sum.x -= (p.x - (mx.x - WallMargin)) * 1.2f;
                if (p.y < mn.y + WallMargin) sum.y += (mn.y + WallMargin - p.y) * 1.2f;
                if (p.y > mx.y - WallMargin) sum.y -= (p.y - (mx.y - WallMargin)) * 1.2f;

                float dMin = math.min(math.min(p.x - mn.x, mx.x - p.x), math.min(p.y - mn.y, mx.y - p.y));
                if (dMin < nearestWallDistance) nearestWallDistance = dMin;
            }
            return sum;
        }

        /// <summary>
        /// 在期望方向附近找一个走得通的方向：按角度偏移 0,±1,±2… 搜索。
        /// 第一轮要求"地形和生物都不挡"，第二轮允许穿过生物但仍然不穿墙。
        /// </summary>
        public static bool ChooseSafeDirection(float2 desired, out float2 chosen, out float clearance,
                                               out bool trapped, out bool deviated)
        {
            chosen = desired;
            clearance = 0f;
            trapped = true;
            deviated = false;

            float dl = math.length(desired);
            if (dl < 1e-4f) return false;

            float2 dn = desired / dl;
            int baseIdx = DirIndexOf(dn);

            int idx = Scan(baseIdx, requireOpenField: true);
            if (idx < 0) idx = Scan(baseIdx, requireOpenField: false);

            if (idx >= 0)
            {
                chosen = DirVector(idx);
                clearance = _clear[idx];
                trapped = false;
                // 只有真的偏离了"期望方向最近的那一格"才算避墙修正，
                // 否则只是 16 方向量化吸附，不能冒充成在避障。
                deviated = (idx != baseIdx);
                return true;
            }

            int best = baseIdx;
            float bestClear = -1f;
            for (int i = 0; i < Directions; i++)
                if (_clear[i] > bestClear) { bestClear = _clear[i]; best = i; }
            chosen = DirVector(best);
            clearance = bestClear;
            trapped = true;
            deviated = (best != baseIdx);
            return false;
        }

        private static int Scan(int baseIdx, bool requireOpenField)
        {
            for (int step = 0; step < Directions; step++)
            {
                for (int s = 0; s < 2; s++)
                {
                    if (step == 0 && s == 1) continue;
                    int delta = (s == 0) ? step : -step;
                    int idx = ((baseIdx + delta) % Directions + Directions) % Directions;
                    if (_blocked[idx]) continue;
                    if (requireOpenField && _isCreature[idx]) continue;
                    return idx;
                }
            }
            return -1;
        }

        private static int DirIndexOf(float2 dir)
        {
            float ang = math.atan2(dir.y, dir.x);
            if (ang < 0f) ang += math.PI * 2f;
            return (int)math.round(ang / (math.PI * 2f) * Directions) % Directions;
        }

        private static float2 DirVector(int i)
        {
            float ang = (360f * i / Directions) * Mathf.Deg2Rad;
            return new float2(math.cos(ang), math.sin(ang));
        }
    }
}
