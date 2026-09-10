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
        }

        private static readonly Dictionary<int, Stat> _stats = new Dictionary<int, Stat>(256);

        public static void Reset()
        {
            _stats.Clear();
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
            }

            if (e.damageTakenThisFrame != null && e.damageTakenThisFrame.Count > 0)
            {
                float sum = 0f;
                for (int i = 0; i < e.damageTakenThisFrame.Count; i++) sum += e.damageTakenThisFrame[i];
                if (sum > 0f)
                {
                    st.last = sum;
                    if (sum > st.max) st.max = sum;
                    st.recent.Enqueue(sum);
                    while (st.recent.Count > RecentWindow) st.recent.Dequeue();
                    st.framesWithDamage++;
                    st.lastTime = now;
                }
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

            float model = playerEstimate * e.armorMultiplier;

            float pred;
            string src;
            if (avg > 0.01f)
            {
                pred = avg;
                src = (model > 0.01f) ? "实测" : "实测";
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

        private static void Prune(double now)
        {
            if (_stats.Count == 0) return;
            List<int> dead = null;
            foreach (var kv in _stats)
            {
                if (kv.Value.recent.Count == 0 || (now - kv.Value.lastTime) > HitForgetSeconds * 4.0)
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
