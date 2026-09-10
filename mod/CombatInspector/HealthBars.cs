using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace CombatInspector
{
    /// <summary>
    /// 敌人头顶血条（IMGUI 屏幕空间）。
    ///
    /// 用 Camera.main.WorldToScreenPoint 把怪物世界坐标投到屏幕，再画条 —— 不依赖任何游戏预制体，
    /// 所以能给"所有"敌人加条，而不只是游戏自己做了条的精英（EliteEnemyHudUI 只覆盖精英怪）。
    ///
    /// 每条同时显示：当前血量，以及"下一次受击后预计剩余"（用橙色段表示即将被扣掉的部分，
    /// 白线标出预计血线；能一击打死则整条转红并标"可秒杀"）。
    /// </summary>
    public sealed class HealthBars
    {
        public bool Enabled = true;
        public int MaxCount = 40;
        public float MaxDistance = 70f;
        public float HeadOffsetY = 0.7f;
        public bool ShowText = true;
        public bool ShowPrediction = true;
        public bool ShowLeaderLine = true;
        public bool MarkAdvice = true;
        public float BarWidth = 56f;
        public float BarHeight = 6f;

        private int _adviceTarget = -1;
        private static readonly Color AdviceGreen = new Color(0.25f, 0.95f, 0.72f);

        private readonly List<EnemySnapshot> _order = new List<EnemySnapshot>(64);
        private Texture2D _white;
        private int _lastFrame = -1;
        private int _drawn;
        private string _camError;

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "enabled={0} drawn={1} cam={2} frame={3}/{4}",
                Enabled, _drawn, _camError ?? "ok", _lastFrame, Time.frameCount);
        }

        private Texture2D White
        {
            get
            {
                if (_white == null)
                {
                    _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    _white.SetPixel(0, 0, Color.white);
                    _white.Apply();
                    _white.hideFlags = HideFlags.HideAndDontSave;
                }
                return _white;
            }
        }

        public void Draw(CombatSnapshot s)
        {
            if (!Enabled || s == null || !s.worldReady) return;

            var cam = Camera.main;
            if (cam == null)
            {
                _camError = "Camera.main 为空";
                return;
            }
            _camError = "ok";

            _adviceTarget = (MarkAdvice && s.advice != null && s.advice.active) ? s.advice.targetEntityIndex : -1;

            _order.Clear();
            for (int i = 0; i < s.enemies.Count; i++)
            {
                var e = s.enemies[i];
                if (!e.hasPosition) continue;
                if (e.hp <= 0f) continue;
                if (MaxDistance > 0f && e.distanceToPlayer >= 0f && e.distanceToPlayer > MaxDistance) continue;
                _order.Add(e);
            }
            // 远的先画，近的后画（压在上方）
            _order.Sort((a, b) =>
            {
                float da = a.distanceToPlayer < 0f ? float.MaxValue : a.distanceToPlayer;
                float db = b.distanceToPlayer < 0f ? float.MaxValue : b.distanceToPlayer;
                return db.CompareTo(da);
            });

            int limit = Math.Min(_order.Count, Math.Max(1, MaxCount));
            _drawn = 0;

            for (int i = _order.Count - 1; i >= 0 && _drawn < limit; i--)
            {
                var e = _order[i];
                if (DrawOne(cam, e)) _drawn++;
            }

            _lastFrame = Time.frameCount;
        }

        private bool DrawOne(Camera cam, EnemySnapshot e)
        {
            Vector3 world = new Vector3(e.posX, e.posY + HeadOffsetY, e.posZ);
            Vector3 sp;
            try { sp = cam.WorldToScreenPoint(world); }
            catch { return false; }
            if (sp.z <= 0.01f) return false;

            float x = sp.x;
            float y = Screen.height - sp.y;
            const float margin = 80f;
            if (x < -margin || x > Screen.width + margin || y < -margin || y > Screen.height + margin) return false;

            // 实体原点（无偏移）投影，用来画引线：偏移不准时也能看出条属于哪只怪
            float ox = x, oy = y;
            if (ShowLeaderLine)
            {
                Vector3 so = cam.WorldToScreenPoint(new Vector3(e.posX, e.posY, e.posZ));
                if (so.z > 0.01f) { ox = so.x; oy = Screen.height - so.y; }
            }

            // 远处条稍窄，避免满屏糊成一片
            float dist = e.distanceToPlayer < 0f ? 20f : e.distanceToPlayer;
            float scale = Mathf.Clamp(24f / Mathf.Max(6f, dist), 0.62f, 1.25f);
            float w = BarWidth * scale * (e.isBoss ? 1.7f : (e.isElite ? 1.18f : 1f));
            float h = BarHeight * (e.isBoss ? 1.5f : (e.isElite ? 1.2f : 1f));
            var rect = new Rect(x - w / 2f, y - h / 2f, w, h);

            // 引线 + 原点标记
            if (ShowLeaderLine)
            {
                float dy = oy - (rect.yMax);
                if (dy > 2f)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.28f);
                    GUI.DrawTexture(new Rect(ox - 0.5f, rect.yMax, 1f, Mathf.Min(dy, 200f)), White);
                    GUI.color = new Color(1f, 1f, 1f, 0.5f);
                    GUI.DrawTexture(new Rect(ox - 1.5f, oy - 1.5f, 3f, 3f), White);
                    GUI.color = Color.white;
                }
            }

            // 底
            GUI.color = new Color(0f, 0f, 0f, 0.62f);
            GUI.DrawTexture(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f), White);

            // 当前血量
            float pct = Mathf.Clamp01(e.hpPct);
            Color fill = e.isBoss ? new Color(0.94f, 0.30f, 0.30f)
                       : e.isElite ? new Color(1f, 0.72f, 0.22f)
                       : new Color(0.30f, 0.86f, 0.45f);
            if (e.willDieFromNextHit) fill = new Color(1f, 0.25f, 0.25f);
            GUI.color = fill;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width * pct, rect.height), White);

            // 预计下一击后：把"即将被扣掉"的那段画成橙色，并画一条预计血线
            if (ShowPrediction && e.predictedNextDamage > 0.01f && e.maxHp > 0.001f)
            {
                float afterPct = Mathf.Clamp01(e.predictedHpAfter / e.maxHp);
                if (afterPct < pct)
                {
                    GUI.color = new Color(1f, 0.55f, 0.15f, 0.85f);
                    GUI.DrawTexture(new Rect(rect.x + rect.width * afterPct, rect.y,
                                              rect.width * (pct - afterPct), rect.height), White);
                }
                GUI.color = new Color(1f, 1f, 1f, 0.95f);
                GUI.DrawTexture(new Rect(rect.x + rect.width * afterPct - 1f, rect.y - 1.5f,
                                          2f, rect.height + 3f), White);
            }

            // 建议层推荐目标：绿色方括号框住这条
            bool recommended = MarkAdvice && e.entityIndex == _adviceTarget;
            if (recommended)
            {
                GUI.color = AdviceGreen;
                float bl = h + 2f;
                GUI.DrawTexture(new Rect(rect.x - 4f, rect.y - bl * 0.5f, 1.5f, bl), White);
                GUI.DrawTexture(new Rect(rect.xMax + 2.5f, rect.y - bl * 0.5f, 1.5f, bl), White);
                GUI.color = Color.white;
            }

            if (!ShowText) return true;

            // 文字
            string line;
            if (e.willDieFromNextHit)
                line = string.Format(CultureInfo.InvariantCulture,
                    "{0:F0}/{1:F0}  可秒杀(-{2:F0})", e.hp, e.maxHp, e.predictedNextDamage);
            else if (e.predictedNextDamage > 0.01f)
                line = string.Format(CultureInfo.InvariantCulture,
                    "{0:F0}/{1:F0}  →{2:F0}  -{3:F0}{4}", e.hp, e.maxHp, e.predictedHpAfter,
                    e.predictedNextDamage, e.hitsToKill > 0 ? ("  " + e.hitsToKill + "击") : "");
            else
                line = string.Format(CultureInfo.InvariantCulture, "{0:F0}/{1:F0}", e.hp, e.maxHp);

            if (recommended) line = "推荐 " + line;

            var st = Cjk.Small;
            float tw = Mathf.Max(w, st.CalcSize(new GUIContent(line)).x + 6f);
            var textRect = new Rect(x - tw / 2f, rect.y - 15f, tw, 15f);
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(textRect, White);
            GUI.color = e.willDieFromNextHit ? new Color(1f, 0.45f, 0.45f) : Color.white;
            GUI.Label(textRect, line, st);
            GUI.color = Color.white;

            // 第二行：护甲与预测来源（小字，可选信息，帮助判断预测可信度）
            if (e.armor > 0.01f || !string.IsNullOrEmpty(e.predictedDamageSource))
            {
                string sub = string.Format(CultureInfo.InvariantCulture,
                    "甲{0:F0}({1:P0}) 源:{2}", e.armor, 1f - e.armorMultiplier, e.predictedDamageSource ?? "-");
                var subRect = new Rect(x - tw / 2f, rect.y + rect.height + 1f, tw, 13f);
                GUI.color = new Color(0.75f, 0.82f, 0.92f, 0.85f);
                GUI.Label(subRect, sub, st);
                GUI.color = Color.white;
            }

            return true;
        }
    }
}
