using System.Collections.Generic;
using UnityEngine;

namespace CombatInspector
{
    /// <summary>
    /// 预测每个敌人"下一次受击后的血量"。
    ///
    /// 思路：不去复刻游戏的完整伤害公式（各机体分支太多、暴击是生成弹体时 roll 的随机值），
    /// 而是以实测为主 —— <c>DamageThisFrame</c> 是游戏伤害管线结算完写进受击者 buffer 的真值，
    /// 已经包含暴击、信标/芯片乘区和目标护甲减免。只要命中过一次就用它的滚动统计预测。
    /// 还没命中过时才退回模型：玩家 AttackPower × 期望暴击倍率 × pow(0.5, 护甲/100)。
    /// </summary>
    public static class DamageTracker
    {
        /// <summary>PerkConfig.ArmorPerHalfDamage：护甲每满此值，伤害减半。</summary>
        public const float ArmorPerHalfDamage = 100f;

        /// <summary>滚动窗口：最近多少次受击参与平均。</summary>
        public const int RecentWindow = 8;

        /// <summary>超过这么久没再受击，统计作废（避免拿几秒前的数据预测）。</summary>
        public const double HitForgetSeconds = 6.0;

        private const int MaxTracked = 4096;

        private sealed class Stat
        {
            public int version;
            public readonly Queue<float> recent = new Queue<float>();
            public float last;
            public float max;
            public int framesWithDamage;
            public double lastTime;
            public bool hasHp;
            public float lastHp;
            public float lastArmorMul = 1f;
        }

        private static readonly Dictionary<int, Stat> _stats = new Dictionary<int, Stat>(256);

        // 全局归一化掉血样本（除以目标护甲倍率后的"护甲前伤害"），给没有自己实测数据的敌人兜底。
        // 来源两类：存活怪的区间掉血 + 死亡怪（一刀死的小怪，从列表消失前最后一面 HP）。
        private const int GlobalWindow = 32;
        private static readonly Queue<float> _globalDrops = new Queue<float>();
        private static double _globalLastTime = -1;

        public static int GlobalSamples { get { return _globalDrops.Count; } }

        public static void Reset()
        {
            _stats.Clear();
            _globalDrops.Clear();
        }

        public static int TrackedCount { get { return _stats.Count; } }

        public static void Update(CombatSnapshot snap)
        {
            if (snap == null) return;

            double now = snap.ecsElapsedTime > 0f ? snap.ecsElapsedTime : snap.unityTime;

            var p = LocalPlayer(snap);
            float playerEstimate = p != null ? p.attackDamageEstimate : 0f;
            float atkCd = (p != null && p.attackCooldown > 0.01f) ? p.attackCooldown : 1f;

            for (int i = 0; i < snap.enemies.Count; i++)
                Predict(snap.enemies[i], playerEstimate, atkCd, now);

            // 死亡即样本：上次还在、这次从快照消失的敌人，把它最后的 HP 记为一刀致死的掉血。
            // 不做这个，20 血小飞鸟这种"一区间内死亡+实体回收"的怪永远攒不出实测样本。
            // 代价是"剩余血量"，低估真实单发伤害 —— 对"可秒杀"判断是保守方向，可接受。
            // （实体离开扫描范围也会走到这里，属于已接受的近似：战斗密集区外的离场极少。）
            var present = new HashSet<int>();
            for (int i = 0; i < snap.enemies.Count; i++) present.Add(snap.enemies[i].entityIndex);
            foreach (var kv in _stats)
            {
                var st = kv.Value;
                if (present.Contains(kv.Key)) continue;
                if (!st.hasHp || st.lastHp <= 0.01f) continue;
                if ((now - st.lastTime) > HitForgetSeconds * 2.0) continue;

                RecordSample(st, st.lastHp, st.lastArmorMul, now);
                st.hasHp = false;
                st.lastHp = 0f;
            }

            Prune(now);
        }

        private static PlayerSnapshot LocalPlayer(CombatSnapshot s)
        {
            for (int i = 0; i < s.players.Count; i++) if (s.players[i].isLocal) return s.players[i];
            return s.players.Count > 0 ? s.players[0] : null;
        }

        private static void Predict(EnemySnapshot e, float playerEstimate, float atkCd, double now)
        {
            int key = e.entityIndex;
            Stat st;
            if (!_stats.TryGetValue(key, out st))
            {
                if (_stats.Count >= MaxTracked) _stats.Clear();
                st = new Stat();
                _stats[key] = st;
            }

            // 实体槽位会被回收复用，version 变了说明是另一只怪，旧统计作废
            if (st.version != e.entityVersion)
            {
                st.version = e.entityVersion;
                st.recent.Clear();
                st.last = 0f;
                st.max = 0f;
                st.framesWithDamage = 0;
                st.lastTime = 0.0;
                st.hasHp = false;
            }

            // 实测来源 = 相邻两次抓取之间的 HP 下降量。
            //
            // 为什么不用 DamageThisFrame：它是"每帧消费后即清空"的 buffer，而我们的抓取跑在
            // MonoBehaviour Update()，早于本帧的 ECS 模拟，所以永远只能读到"上一帧已被清空"的空值
            // —— 实测 5427 个样本零命中，整条实测路径从来没通过。HP 是持久状态，对时机完全免疫，
            // 而且拿到的就是结算后真值（暴击、信标/芯片乘区、护甲减免全在里面）。
            //
            // 语义因此是"区间伤害"而非"单击伤害"：4Hz 采样可能把相邻几次命中并进同一个区间。
            st.lastArmorMul = e.armorMultiplier;
            float drop = 0f;
            if (st.hasHp && st.version == e.entityVersion)
            {
                float d = st.lastHp - e.hp;
                if (d > 0.01f) drop = d;      // HP 上升是回血/升级，不是伤害
            }
            st.lastHp = e.hp;
            st.hasHp = true;
            st.lastTime = now;   // 每次看到就刷新，不然没掉血的帧 lastTime 停在 0 被 Prune 删掉

            if (drop > 0f)
            {
                RecordSample(st, drop, e.armorMultiplier, now);
            }

            e.lastHitDamage = st.last;
            e.maxHitDamage = st.max;
            e.hitsObserved = st.framesWithDamage;

            float avg = 0f;
            if (st.recent.Count > 0 && (now - st.lastTime) <= HitForgetSeconds)
            {
                float total = 0f;
                foreach (float v in st.recent) total += v;
                avg = total / st.recent.Count;
            }
            e.avgHitDamage = avg;

            // 该敌人自己没数据时，用全局归一化样本（别的怪的掉血 ÷ 它的护甲倍率）兜底
            float globalAvg = 0f;
            if (_globalDrops.Count > 0 && (now - _globalLastTime) <= HitForgetSeconds * 2.0)
            {
                float total = 0f;
                foreach (float v in _globalDrops) total += v;
                globalAvg = total / _globalDrops.Count * Mathf.Max(0.01f, e.armorMultiplier);
            }

            float model = playerEstimate * e.armorMultiplier;

            float pred;
            string src;
            if (avg > 0.01f)
            {
                pred = avg;
                src = "实测·区间";
            }
            else if (globalAvg > 0.01f)
            {
                pred = globalAvg;
                src = "实测·他样本";
            }
            else if (model > 0.01f)
            {
                pred = model;
                src = "模型";
            }
            else
            {
                pred = 0f;
                src = "-";
            }

            e.predictedNextDamage = pred;
            e.predictedDamageSource = src;
            e.predictedHpAfter = Mathf.Max(0f, e.hp - pred);
            e.willDieFromNextHit = pred > 0.01f && (e.hp - pred) <= 0.001f;

            if (pred > 0.01f && e.hp > 0f)
            {
                e.hitsToKill = Mathf.Max(1, Mathf.CeilToInt(e.hp / pred));
                e.timeToKill = e.hitsToKill * atkCd;
            }
            else
            {
                e.hitsToKill = (e.hp <= 0f) ? 0 : -1;
                e.timeToKill = -1f;
            }
        }

        /// <summary>记一个掉血样本：进 per-enemy 滚动窗，并归一化后进全局窗（除以护甲倍率）。</summary>
        private static void RecordSample(Stat st, float drop, float armorMul, double now)
        {
            st.last = drop;
            if (drop > st.max) st.max = drop;
            st.recent.Enqueue(drop);
            while (st.recent.Count > RecentWindow) st.recent.Dequeue();
            st.framesWithDamage++;
            st.lastTime = now;

            _globalDrops.Enqueue(drop / Mathf.Max(0.01f, armorMul));
            while (_globalDrops.Count > GlobalWindow) _globalDrops.Dequeue();
            _globalLastTime = now;
        }

        private static void Prune(double now)
        {
            if (_stats.Count == 0) return;
            List<int> dead = null;
            foreach (var kv in _stats)
            {
                // 只看过期，不看 recent 是否为空——活着的怪只是没掉过血，不该被删
                if ((now - kv.Value.lastTime) > HitForgetSeconds * 4.0)
                {
                    if (dead == null) dead = new List<int>(8);
                    dead.Add(kv.Key);
                }
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) _stats.Remove(dead[i]);
        }
    }
}
