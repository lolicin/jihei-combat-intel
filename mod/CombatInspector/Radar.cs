using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Mathematics;
using UnityEngine;

namespace CombatInspector
{
    /// <summary>
    /// In-game radar window (IMGUI). Separate from the main panel so it can sit in a corner
    /// on its own; defaults to the top-right of the screen and is draggable by its title bar.
    ///
    /// Enemy classes are drawn distinctly:
    ///   普通敌人  small filled dot, tinted by AI type
    ///   精英      larger dot + gold double ring
    ///   首领      big red diamond + red ring
    /// plus 自身 = blue arrow (rotated to move direction), 友军 = green dot, 弹幕 = tiny yellow dot.
    ///
    /// Everything is drawn with a handful of tiny generated textures (circle / ring / arrow),
    /// so there is no per-frame texture allocation and no UnityEditor-only API.
    /// </summary>
    public sealed class RadarOverlay
    {
        public bool Visible = true;
        public float Size = 300f;
        public float FixedRange = 0f;      // 0 = 自动
        public bool ShowNames = false;
        public bool ShowLegend = true;
        public bool ShowAdvice = true;

        // 诊断信息：IMGUI 窗口定位问题从游戏外是看不见的，所以把它暴露到 /state
        public string LastError;
        public int LastDrawFrame = -1;

        private Rect _win;
        private bool _posInit;

        public Rect WindowRect { get { return _win; } }
        public bool PositionInitialized { get { return _posInit; } }

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "visible={0} rect=({1:F0},{2:F0} {3:F0}x{4:F0}) posInit={5} lastDrawFrame={6}/{7} err={8}",
                Visible, _win.x, _win.y, _win.width, _win.height,
                _posInit, LastDrawFrame, Time.frameCount, LastError ?? "-");
        }

        private Texture2D _dot;
        private Texture2D _ring;
        private Texture2D _arrow;
        private bool _texReady;

        private static readonly string[] RangeLabels = { "20", "40", "80", "自动" };
        private static readonly float[] RangeValues = { 20f, 40f, 80f, 0f };
        private int _rangeIdx = 3;

        private static readonly Dictionary<string, Color> AiColor = new Dictionary<string, Color>
        {
            { "Melee", new Color(0.97f, 0.44f, 0.44f) },
            { "Ranged", new Color(0.65f, 0.55f, 0.98f) },
            { "FlyingMelee", new Color(0.98f, 0.57f, 0.24f) },
            { "DashMelee", new Color(0.96f, 0.45f, 0.71f) },
            { "SuicideBomber", new Color(0.98f, 0.80f, 0.09f) },
            { "InterferenceFloater", new Color(0.22f, 0.74f, 0.97f) }
        };
        private static readonly Color EliteGold = new Color(1f, 0.84f, 0.30f);
        private static readonly Color BossRed = new Color(0.94f, 0.27f, 0.27f);
        private static readonly Color AllyGreen = new Color(0.20f, 0.83f, 0.60f);
        private static readonly Color SelfBlue = new Color(0.35f, 0.66f, 1f);
        private static readonly Color ProjYellow = new Color(0.99f, 0.90f, 0.55f);

        private static readonly Dictionary<string, string> AiCn = new Dictionary<string, string>
        {
            { "Melee", "近战" }, { "Ranged", "远程" }, { "FlyingMelee", "飞行近战" },
            { "DashMelee", "突进近战" }, { "SuicideBomber", "自爆兵" }, { "InterferenceFloater", "干扰浮空体" }
        };

        // ---------------------------------------------------------------- textures

        private void EnsureTextures()
        {
            if (_texReady) return;
            _dot = MakeCircle(16, false, 0f);
            _ring = MakeCircle(64, true, 5f);
            _arrow = MakeArrow(26);
            _texReady = true;
        }

        private static Texture2D MakeCircle(int size, bool outline, float thickness)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            var px = new Color[size * size];
            float c = (size - 1) / 2f;
            float rOut = size / 2f - 1f;
            float rIn = rOut - thickness;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float a;
                    if (outline) a = Mathf.Clamp01(Mathf.Min(rOut + 0.5f - d, d - rIn + 0.5f));
                    else a = Mathf.Clamp01(rOut + 0.5f - d);
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            t.SetPixels(px);
            t.Apply(false, false);
            return t;
        }

        private static Texture2D MakeArrow(int size)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            float h = size / 2f;
            // 箭头朝 +X：尖端在右，尾部内凹
            var poly = new[] {
                new Vector2(size - 1f, h),
                new Vector2(2f, 2f),
                new Vector2(h * 0.62f, h),
                new Vector2(2f, size - 3f)
            };
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    px[y * size + x] = new Color(1f, 1f, 1f, Inside(poly, x + 0.5f, y + 0.5f) ? 1f : 0f);
            t.SetPixels(px);
            t.Apply(false, false);
            return t;
        }

        private static bool Inside(Vector2[] poly, float x, float y)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if ((poly[i].y > y) != (poly[j].y > y) &&
                    x < (poly[j].x - poly[i].x) * (y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }

        // ---------------------------------------------------------------- draw

        public void Draw(CombatSnapshot s)
        {
            EnsureTextures();
            if (!_posInit)
            {
                _posInit = true;
                float w = Size + 26f;
                float h = Size + (ShowLegend ? 96f : 58f);
                _win = new Rect(Mathf.Max(4f, Screen.width - w - 14f), 14f, w, h);
            }

            // 每帧钳制进屏幕：分辨率或 DPI 缩放变化后窗口不能跑到屏幕外
            _win.width = Mathf.Clamp(_win.width, 180f, Mathf.Max(180f, Screen.width));
            _win.height = Mathf.Clamp(_win.height, 180f, Mathf.Max(180f, Screen.height));
            _win.x = Mathf.Clamp(_win.x, 0f, Mathf.Max(0f, Screen.width - _win.width));
            _win.y = Mathf.Clamp(_win.y, 0f, Mathf.Max(0f, Screen.height - _win.height));

            try
            {
                _win = GUI.Window(778002, _win, id => DrawBody(s), "战场雷达（XY 平面）", Cjk.Window);
                LastDrawFrame = Time.frameCount;
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                throw;
            }
        }

        private void DrawBody(CombatSnapshot s)
        {
            // 工具条：范围 + 名字（全部显式使用中文字体样式）
            GUILayout.BeginHorizontal();
            GUILayout.Label("范围", Cjk.Label, GUILayout.Width(34));
            int picked = GUILayout.Toolbar(_rangeIdx, RangeLabels, Cjk.Button, GUILayout.Width(Size * 0.62f));
            if (picked != _rangeIdx) { _rangeIdx = picked; FixedRange = RangeValues[picked]; }
            bool names = GUILayout.Toggle(ShowNames, "名字", Cjk.Toggle);
            if (names != ShowNames) ShowNames = names;
            GUILayout.EndHorizontal();

            var area = GUILayoutUtility.GetRect(Size, Size, GUILayout.Width(Size), GUILayout.Height(Size));
            DrawRadar(area, s);

            if (s == null || !s.worldReady)
            {
                GUILayout.Label("等待 ECS 世界就绪…", Cjk.Label);
            }
            else
            {
                var p = LocalPlayer(s);
                if (p == null) GUILayout.Label("尚无玩家实体", Cjk.Label);
                else
                {
                    var near = NearestEnemy(s, p);
                    if (near != null)
                        GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                            "最近：{0}　距离 {1:F1}　血量 {2:F0}/{3:F0}",
                            string.IsNullOrEmpty(near.name) ? ("实体E" + near.entityIndex) : near.name,
                            near.distanceToPlayer, near.hp, near.maxHp), Cjk.Label);
                    else
                        GUILayout.Label("范围内暂无敌人", Cjk.Label);
                }
            }

            if (ShowLegend)
            {
                GUILayout.Label("小圆点=普通敌人（按AI着色）　金环=精英　红菱=首领", Cjk.Small);
                GUILayout.Label("蓝箭头=自身　绿点=友军　小黄点=弹幕　黄环=索敌半径", Cjk.Small);
                if (ShowAdvice)
                    GUILayout.Label("青色双环=推荐目标　青叉=建议瞄准点　青箭头=建议走位", Cjk.Small);
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 18));
        }

        private static PlayerSnapshot LocalPlayer(CombatSnapshot s)
        {
            for (int i = 0; i < s.players.Count; i++) if (s.players[i].isLocal) return s.players[i];
            return s.players.Count > 0 ? s.players[0] : null;
        }

        private static EnemySnapshot NearestEnemy(CombatSnapshot s, PlayerSnapshot p)
        {
            EnemySnapshot best = null;
            for (int i = 0; i < s.enemies.Count; i++)
            {
                var e = s.enemies[i];
                if (e.distanceToPlayer < 0f) continue;
                if (best == null || e.distanceToPlayer < best.distanceToPlayer) best = e;
            }
            return best;
        }

        private void DrawRadar(Rect area, CombatSnapshot s)
        {
            // 背景
            var prevColor = GUI.color;
            GUI.color = new Color(0.03f, 0.045f, 0.06f, 0.92f);
            GUI.DrawTexture(area, Texture2D.whiteTexture);
            GUI.color = prevColor;

            float cx = area.x + area.width / 2f;
            float cy = area.y + area.height / 2f;
            float R = Mathf.Min(area.width, area.height) / 2f - 6f;

            var p = (s != null && s.worldReady) ? LocalPlayer(s) : null;

            // 范围
            float range = FixedRange;
            if (range <= 0f)
            {
                range = 20f;
                if (s != null && p != null)
                {
                    for (int i = 0; i < s.enemies.Count; i++)
                    {
                        var e = s.enemies[i];
                        if (e.hasPosition && e.distanceToPlayer > range) range = e.distanceToPlayer;
                    }
                    range = Mathf.Ceil(range * 1.15f / 10f) * 10f;
                }
            }
            float sc = R / range;

            // 距离环 + 十字线
            GUI.color = new Color(0.22f, 0.30f, 0.42f, 0.9f);
            for (int i = 1; i <= 4; i++)
            {
                float rr = R * i / 4f;
                GUI.DrawTexture(new Rect(cx - rr, cy - rr, rr * 2f, rr * 2f), _ring);
            }
            GUI.color = new Color(0.22f, 0.30f, 0.42f, 0.7f);
            GUI.DrawTexture(new Rect(area.x + 2f, cy - 0.5f, area.width - 4f, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 0.5f, area.y + 2f, 1f, area.height - 4f), Texture2D.whiteTexture);
            GUI.color = prevColor;

            // 环上的距离数字
            var numStyle = Cjk.Small;
            for (int i = 1; i <= 4; i++)
            {
                float rr = R * i / 4f;
                GUI.Label(new Rect(cx + rr - 14f, cy - 14f, 30f, 14f),
                    (range * i / 4f).ToString("F0", CultureInfo.InvariantCulture), numStyle);
            }

            if (s == null || p == null || !p.hasPosition)
            {
                GUI.Label(new Rect(cx - 60f, cy + 8f, 140f, 20f), "等待玩家实体…", numStyle);
                return;
            }

            // 索敌半径（虚线效果用半透明环近似）
            if (p.detectionRadius > 0f)
            {
                float dr = p.detectionRadius * sc;
                if (dr < R * 1.4f)
                {
                    GUI.color = new Color(0.98f, 0.75f, 0.20f, 0.35f);
                    GUI.DrawTexture(new Rect(cx - dr, cy - dr, dr * 2f, dr * 2f), _ring);
                    GUI.color = prevColor;
                }
            }

            // 瞄准线
            if (p.hasAim)
            {
                float ax = cx + (p.aimX - p.posX) * sc;
                float ay = cy - (p.aimY - p.posY) * sc;
                DrawLine(cx, cy, ax, ay, new Color(0.35f, 0.66f, 1f, 0.45f), 1f);
                GUI.color = new Color(0.35f, 0.66f, 1f, 0.8f);
                GUI.DrawTexture(new Rect(ax - 2.5f, ay - 2.5f, 5f, 5f), _ring);
                GUI.color = prevColor;
            }

            // 弹幕
            for (int i = 0; i < s.projectiles.Count; i++)
            {
                var q = s.projectiles[i];
                float x = cx + (q.posX - p.posX) * sc;
                float y = cy - (q.posY - p.posY) * sc;
                if (Mathf.Abs(x - cx) > R || Mathf.Abs(y - cy) > R) continue;
                GUI.color = ProjYellow;
                GUI.DrawTexture(new Rect(x - 1.5f, y - 1.5f, 3f, 3f), _dot);
            }
            GUI.color = prevColor;

            // 友军
            for (int i = 0; i < s.allies.Count; i++)
            {
                var a = s.allies[i];
                float x = cx + (a.posX - p.posX) * sc;
                float y = cy - (a.posY - p.posY) * sc;
                if (Off(x, y, cx, cy, R)) continue;
                GUI.color = AllyGreen;
                GUI.DrawTexture(new Rect(x - 3.5f, y - 3.5f, 7f, 7f), _dot);
            }
            GUI.color = prevColor;

            // 敌人：普通 / 精英 / 首领
            for (int i = 0; i < s.enemies.Count; i++)
            {
                var e = s.enemies[i];
                if (!e.hasPosition) continue;

                float dx = (e.posX - p.posX) * sc;
                float dy = -(e.posY - p.posY) * sc;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                bool off = dist > R;
                if (off) { float k = (R - 5f) / dist; dx *= k; dy *= k; }
                float x = cx + dx, y = cy + dy;

                Color c = AiColorOf(e);
                float alpha = off ? 0.38f : 1f;

                if (e.isBoss)
                {
                    // 首领：红菱 + 红环
                    GUI.color = new Color(BossRed.r, BossRed.g, BossRed.b, alpha);
                    DrawRotated(new Rect(x - 7f, y - 7f, 14f, 14f), 45f, Texture2D.whiteTexture);
                    GUI.color = new Color(BossRed.r, BossRed.g, BossRed.b, alpha * 0.85f);
                    GUI.DrawTexture(new Rect(x - 11f, y - 11f, 22f, 22f), _ring);
                    if (ShowNames && !off)
                        GUI.Label(new Rect(x + 12f, y - 7f, 90f, 14f), "首领", numStyle);
                }
                else if (e.isElite)
                {
                    // 精英：稍大圆点 + 金色双环
                    GUI.color = new Color(c.r, c.g, c.b, alpha);
                    GUI.DrawTexture(new Rect(x - 4.5f, y - 4.5f, 9f, 9f), _dot);
                    GUI.color = new Color(EliteGold.r, EliteGold.g, EliteGold.b, alpha);
                    GUI.DrawTexture(new Rect(x - 7.5f, y - 7.5f, 15f, 15f), _ring);
                    GUI.DrawTexture(new Rect(x - 10f, y - 10f, 20f, 20f), _ring);
                    if (ShowNames && !off)
                        GUI.Label(new Rect(x + 11f, y - 7f, 90f, 14f), Trunc(e.name, 8), numStyle);
                }
                else
                {
                    // 普通：小圆点，按 AI 着色
                    GUI.color = new Color(c.r, c.g, c.b, alpha);
                    GUI.DrawTexture(new Rect(x - 3f, y - 3f, 6f, 6f), _dot);
                    if (ShowNames && !off)
                        GUI.Label(new Rect(x + 6f, y - 7f, 90f, 14f), Trunc(e.name, 8), numStyle);
                }

                // 血条弧：残血时在外圈画一段
                if (!off && e.hpPct < 0.999f && e.hpPct > 0.001f)
                {
                    float rr = e.isBoss ? 13f : (e.isElite ? 11.5f : 8f);
                    GUI.color = new Color(0f, 0f, 0f, 0.55f);
                    GUI.DrawTexture(new Rect(x - rr, y - rr, rr * 2f, rr * 2f), _ring);
                    GUI.color = new Color(0.29f, 0.87f, 0.50f, alpha);
                    DrawArc(x, y, rr, e.hpPct);
                }
            }
            GUI.color = prevColor;

            // 自身：蓝箭头，朝移动方向
            float ang = Mathf.Atan2(p.moveDirY, p.moveDirX) * Mathf.Rad2Deg;
            if (float.IsNaN(ang) || float.IsInfinity(ang) || (p.moveDirX == 0f && p.moveDirY == 0f)) ang = 90f;
            GUI.color = SelfBlue;
            DrawRotated(new Rect(cx - 9f, cy - 9f, 18f, 18f), -ang, _arrow);
            GUI.color = prevColor;

            DrawNav(cx, cy, sc, p);
            DrawAdvice(s, p, cx, cy, sc, R);
        }

        private readonly List<float4> _segs = new List<float4>(8);

        /// <summary>可玩区矩形、墙段，以及（开启建议层时）16 方向障碍探测结果。</summary>
        private void DrawNav(float cx, float cy, float sc, PlayerSnapshot p)
        {
            if (p == null) return;

            if (Navigation.HasPlayArea)
            {
                float2 mn = Navigation.PlayMin, mx = Navigation.PlayMax;
                if (mx.x > mn.x && mx.y > mn.y)
                {
                    float x0 = cx + (mn.x - p.posX) * sc, y0 = cy - (mn.y - p.posY) * sc;
                    float x1 = cx + (mx.x - p.posX) * sc, y1 = cy - (mx.y - p.posY) * sc;
                    GUI.color = new Color(0.45f, 0.75f, 1f, 0.30f);
                    GUI.DrawTexture(new Rect(x0, y1, x1 - x0, 1.2f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(x0, y0, x1 - x0, 1.2f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(x0, y0, 1.2f, y1 - y0), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(x1, y0, 1.2f, y1 - y0), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }
            }

            if (Navigation.WallCount > 0)
            {
                Navigation.CopyWallSegments(_segs);
                GUI.color = new Color(1f, 0.45f, 0.25f, 0.75f);
                for (int i = 0; i < _segs.Count; i++)
                {
                    float4 s = _segs[i];
                    DrawLine(cx + (s.x - p.posX) * sc, cy - (s.y - p.posY) * sc,
                             cx + (s.z - p.posX) * sc, cy - (s.w - p.posY) * sc,
                             new Color(1f, 0.45f, 0.25f, 0.75f), 1.8f);
                }
                GUI.color = Color.white;
            }

            if (!ShowAdvice) return;

            // 16 方向探测：绿=走得通，红=被挡（生物挡的偏黄）
            for (int i = 0; i < Navigation.Directions; i++)
            {
                float ang = (360f * i / Navigation.Directions) * Mathf.Deg2Rad;
                float dx = Mathf.Cos(ang), dy = Mathf.Sin(ang);
                float clear = Mathf.Clamp(Navigation.BlockedFraction(i), 0.15f, 4f);
                Color c = !Navigation.BlockedAt(i) ? new Color(0.3f, 0.95f, 0.55f, 0.5f)
                          : Navigation.CreatureAt(i) ? new Color(1f, 0.8f, 0.2f, 0.45f)
                          : new Color(1f, 0.35f, 0.35f, 0.5f);
                DrawLine(cx, cy, cx + dx * clear * sc, cy - dy * clear * sc, c, 1f);
            }
        }

        private static readonly Color AdviceGreen = new Color(0.25f, 0.95f, 0.72f);

        /// <summary>只读建议层：推荐目标（青双环）、瞄准点（青叉+连线）、走位方向（青箭头）。</summary>
        private void DrawAdvice(CombatSnapshot s, PlayerSnapshot p, float cx, float cy, float sc, float R)
        {
            var adv = s.advice;
            if (!ShowAdvice || adv == null || !adv.active) return;

            if (adv.hasAim)
            {
                float ax = cx + (adv.aimX - p.posX) * sc;
                float ay = cy - (adv.aimY - p.posY) * sc;
                DrawLine(cx, cy, ax, ay, new Color(AdviceGreen.r, AdviceGreen.g, AdviceGreen.b, 0.35f), 1f);
                GUI.color = AdviceGreen;
                GUI.DrawTexture(new Rect(ax - 6f, ay - 0.8f, 12f, 1.6f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(ax - 0.8f, ay - 6f, 1.6f, 12f), Texture2D.whiteTexture);
                GUI.color = Color.white;
            }

            for (int i = 0; i < s.enemies.Count; i++)
            {
                var e = s.enemies[i];
                if (e.entityIndex != adv.targetEntityIndex || !e.hasPosition) continue;

                float dx = (e.posX - p.posX) * sc;
                float dy = -(e.posY - p.posY) * sc;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                bool off = d > R;
                if (off) { float k = (R - 6f) / Mathf.Max(0.001f, d); dx *= k; dy *= k; }
                float x = cx + dx, y = cy + dy;
                float rr = e.isBoss ? 13f : (e.isElite ? 11f : 9f);

                GUI.color = AdviceGreen;
                GUI.DrawTexture(new Rect(x - rr, y - rr, rr * 2f, rr * 2f), _ring);
                GUI.DrawTexture(new Rect(x - rr - 4f, y - rr - 4f, (rr + 4f) * 2f, (rr + 4f) * 2f), _ring);
                GUI.Label(new Rect(x + rr + 6f, y - 8f, 120f, 14f), off ? "推荐目标(界外)" : "推荐目标", Cjk.Small);
                GUI.color = Color.white;
                break;
            }

            if (adv.hasMove)
            {
                float len = Mathf.Min(R * 0.55f, 62f);
                float ex = cx + adv.moveX * len;
                float ey = cy - adv.moveY * len;
                DrawLine(cx, cy, ex, ey, AdviceGreen, 2.5f);
                DrawRotated(new Rect(ex - 7f, ey - 7f, 14f, 14f),
                            Mathf.Atan2(-adv.moveY, adv.moveX) * Mathf.Rad2Deg, _arrow);
                GUI.color = Color.white;
                GUI.Label(new Rect(ex + 8f, ey - 7f, 140f, 14f), adv.moveLabel ?? "", Cjk.Small);
            }
        }

        private static Color AiColorOf(EnemySnapshot e)
        {
            for (int i = 0; i < e.aiTags.Count; i++)
            {
                Color c;
                if (AiColor.TryGetValue(e.aiTags[i], out c)) return c;
            }
            return new Color(0.62f, 0.68f, 0.76f);
        }

        private static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "<无名>";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        private static bool Off(float x, float y, float cx, float cy, float R)
        {
            float dx = x - cx, dy = y - cy;
            return dx * dx + dy * dy > R * R;
        }

        private static void DrawRotated(Rect r, float angleDeg, Texture2D tex)
        {
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(angleDeg, r.center);
            GUI.DrawTexture(r, tex);
            GUI.matrix = m;
        }

        private static void DrawLine(float x0, float y0, float x1, float y1, Color c, float w)
        {
            float dx = x1 - x0, dy = y1 - y0;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) return;
            float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
            var m = GUI.matrix;
            GUI.color = c;
            GUIUtility.RotateAroundPivot(ang, new Vector2(x0, y0));
            GUI.DrawTexture(new Rect(x0, y0 - w / 2f, len, w), Texture2D.whiteTexture);
            GUI.matrix = m;
        }

        /// <summary>用一圈小方块近似血条弧（IMGUI 没有画弧的原语）。</summary>
        private static void DrawArc(float cx, float cy, float r, float pct)
        {
            int steps = Mathf.Max(3, Mathf.RoundToInt(24f * pct));
            float start = -90f;
            for (int i = 0; i < steps; i++)
            {
                float a = (start + 360f * pct * i / Mathf.Max(1, 24f * pct)) * Mathf.Deg2Rad;
                float x = cx + Mathf.Cos(a) * r;
                float y = cy + Mathf.Sin(a) * r;
                GUI.DrawTexture(new Rect(x - 1f, y - 1f, 2f, 2f), Texture2D.whiteTexture);
            }
        }
    }
}
