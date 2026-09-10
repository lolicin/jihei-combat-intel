using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace CombatInspector
{
    /// <summary>
    /// In-game IMGUI readout. Toggle with the configured hotkey (default F9).
    /// Uses a dynamic OS font so the game's Chinese enemy names actually render.
    /// All labels are Chinese; enum values are translated through the maps below
    /// (taken from the game's own enums and InspectorName attributes).
    /// </summary>
    public sealed class Overlay
    {
        public bool Visible = true;
        public int MaxRows = 16;
        public bool ShowAdvice = true;

        private Rect _window = new Rect(14f, 14f, 560f, 640f);
        private Vector2 _scroll;
        private GUIStyle _mono;
        private GUIStyle _head;
        private GUIStyle _dim;

        // ------------------------------------------------------------ 中文映射

        private static readonly Dictionary<string, string> MechCn = new Dictionary<string, string>
        {
            { "Sun", "中型机" }, { "Storm", "轻型机" }, { "Iron", "重型机" }, { "Being", "6号机" },
            { "ReservedMech4", "4号机(预留)" }, { "ReservedMech5", "5号机(预留)" }
        };
        private static readonly Dictionary<string, string> AiCn = new Dictionary<string, string>
        {
            { "Melee", "近战" }, { "Ranged", "远程" }, { "FlyingMelee", "飞行近战" },
            { "DashMelee", "突进近战" }, { "SuicideBomber", "自爆兵" }, { "InterferenceFloater", "干扰浮空体" }
        };
        private static readonly Dictionary<string, string> BuffCn = new Dictionary<string, string>
        {
            { "None", "无" }, { "Slow", "减速" }
        };
        private static readonly Dictionary<string, string> CdCn = new Dictionary<string, string>
        {
            { "Single", "单次" }, { "Multiple", "多次(充能)" }
        };

        private static string Tr(Dictionary<string, string> map, string key)
        {
            string v;
            if (key != null && map.TryGetValue(key, out v)) return v;
            return key ?? "-";
        }

        private static string Mech(string key)
        {
            string v;
            if (key != null && MechCn.TryGetValue(key, out v)) return v + "·" + key;
            return key ?? "-";
        }

        // ------------------------------------------------------------ styles

        private void EnsureStyles()
        {
            // 统一走 Cjk：IMGUI 默认皮肤字体不含中文字形
            _mono = Cjk.LabelRich;
            _head = Cjk.Head;
            _dim = Cjk.Dim;
        }

        public void Draw(CombatSnapshot s, string httpInfo, string outDir)
        {
            EnsureStyles();
            _window = GUI.Window(778001, _window, id => DrawWindow(s, httpInfo, outDir),
                "战斗详情浮层 · 机骸：第九行星", Cjk.Window);
        }

        private void DrawWindow(CombatSnapshot s, string httpInfo, string outDir)
        {
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            if (s == null)
            {
                GUILayout.Label("等待首次抓取…", _mono);
                GUILayout.EndScrollView();
                GUI.DragWindow(new Rect(0, 0, 10000, 18));
                return;
            }

            // ---- 对局上下文
            if (!string.IsNullOrEmpty(s.error))
                GUILayout.Label("<color=#ff8080>" + s.error + "</color>", _mono);

            GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                "<b>对局</b>  局内={0}  大厅={1}  关卡={2}  机甲={3}  槽位={4}  场景={5}",
                s.isInGame ? "是" : "否", s.inLobby ? "是" : "否", s.stageIndex,
                Mech(s.mechType), s.localSlot, s.sceneName ?? "-"), _mono);
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                "时间={0:F2}秒  帧={1}  ECS时间={2:F2}秒  实体数={3}",
                s.unityTime, s.frameCount, s.ecsElapsedTime,
                s.world.totalEntities < 0 ? "?" : s.world.totalEntities.ToString(CultureInfo.InvariantCulture)), _dim);

            if (!string.IsNullOrEmpty(s.devToolsDetail))
                GUILayout.Label(s.devToolsPresent
                    ? "<color=#7fff7f>" + s.devToolsDetail + "</color>"
                    : s.devToolsDetail, _dim);

            GUILayout.Space(6);

            // ---- 自身
            var p = LocalPlayer(s);
            if (p == null)
            {
                GUILayout.Label("<color=#ffd27f>世界中尚无玩家实体</color>", _mono);
            }
            else
            {
                GUILayout.Label("<b>自身</b>  实体E" + p.entityIndex + (p.isLocal ? "（本地）" : "") +
                                (p.mechSlot >= 0 ? "  槽位=" + p.mechSlot : ""), _head);
                Bar("血量", p.hp, p.maxHp, new Color(0.35f, 0.85f, 0.45f));
                Bar("经验", p.exp, p.maxExpEffective > 0.001f ? p.maxExpEffective : p.maxExp, new Color(0.45f, 0.7f, 1f));
                if (p.platingMax > 0.001f) Bar("装甲", p.plating, p.platingMax, new Color(0.8f, 0.8f, 0.9f));
                if (p.spiritMax > 0.001f) Bar("精神", p.spirit, p.spiritMax, new Color(0.9f, 0.6f, 1f));

                GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                    "等级 {0}   攻击 {1:F1}（基础 {2:F1}）   攻击冷却 {3:F2}/{4:F2}秒   移速 {5:F2}（基础 {6:F2}）",
                    p.level, p.attackPower, p.attackPowerBase, p.attackCooldown, p.attackCooldownBase,
                    p.moveSpeed, p.moveSpeedBase), _mono);
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                    "技能 {0}/{1}  生效={2} 可用={3} 冷却中={4}  冷却 {5:F2}/{6:F2}  伤害 {7:F1}  充能 {8}/{9}",
                    Mech(p.skillType), Tr(CdCn, p.skillCdType),
                    p.skillActive ? "是" : "否", p.canSkill ? "是" : "否", p.cdingSkill ? "是" : "否",
                    p.skillCdTimer, p.skillCdTime, p.skillDamage, p.skillTimes, p.skillMaxCharges), _mono);

                if (p.stormMaxStack > 0)
                    GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                        "风暴叠层 {0}/{1}  计时 {2:F2}/{3:F2}", p.stormStack, p.stormMaxStack, p.stormTimer, p.stormDuration), _mono);
                if (p.passiveActivationDelay > 0.001f)
                    GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                        "脱战回血 {0:F2}/秒  延迟 {1:F2}/{2:F2}  生效={3}",
                        p.passiveRegenRate, p.passiveTimer, p.passiveActivationDelay,
                        p.passiveRegenActive ? "是" : "否"), _mono);

                GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                    "坐标 ({0:F2}, {1:F2}, {2:F2})   移动方向 ({3:F2}, {4:F2}){5}   击杀 {6}",
                    p.posX, p.posY, p.posZ, p.moveDirX, p.moveDirY,
                    p.hasAim ? string.Format(CultureInfo.InvariantCulture, "   瞄准 ({0:F2}, {1:F2})", p.aimX, p.aimY) : "",
                    p.killCount), _mono);

                if (p.infiniteSurvivalDevTag)
                    GUILayout.Label("<color=#7fff7f>无限血甲精神已启用（游戏自带 F2 无敌开着）</color>", _mono);

                if (p.damageTakenThisFrame.Count > 0)
                    GUILayout.Label("<color=#ff9090>本帧受到伤害：" + JoinFloats(p.damageTakenThisFrame) + "</color>", _mono);

                if (p.buffs.Count > 0)
                    GUILayout.Label("状态：" + DescribeBuffs(p.buffs), _mono);
                if (p.perks.Count > 0)
                    GUILayout.Label("天赋：" + p.perks.Count + " 个  [" + DescribePerks(p.perks) + "]", _dim);
            }

            // ---- 只读战术建议（不接管操作）
            if (ShowAdvice && s.advice != null)
            {
                var adv = s.advice;
                GUILayout.Space(6);
                GUILayout.Label("<b>战术建议</b>（只读，不接管操作）", _head);
                if (!adv.active)
                {
                    GUILayout.Label("暂无建议：" + (adv.noTargetReason ?? "无"), _dim);
                }
                else
                {
                    GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                        "<color=#40f2b8>集火</color>  {0}  实体E{1}  距离{2:F1}  血量{3:F0}/{4:F0}  {5}",
                        string.IsNullOrEmpty(adv.targetName) ? ("<无名 E" + adv.targetEntityIndex + ">") : adv.targetName,
                        adv.targetEntityIndex, adv.targetDistance, adv.targetHp, adv.targetMaxHp,
                        adv.targetKillableNow ? "<color=#ff8080><b>下一击可击杀</b></color>"
                                              : (adv.targetHitsToKill > 0 ? ("还需 " + adv.targetHitsToKill + " 击") : "")), _mono);
                    if (!string.IsNullOrEmpty(adv.targetWhy))
                        GUILayout.Label("  理由：" + adv.targetWhy, _dim);
                    if (adv.hasAim)
                        GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                            "  瞄准点 ({0:F2}, {1:F2})  提前量 {2:F3}s", adv.aimX, adv.aimY, adv.aimLeadSeconds), _mono);
                    GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                        "  走位：({0:F2}, {1:F2})  {2}    危险度 {3:F0}%  {4}    威胁 {5} 个 / 来袭弹 {6} 发",
                        adv.moveX, adv.moveY, adv.moveLabel ?? "-", adv.dangerScore * 100f,
                        adv.dangerLabel ?? "-", adv.threatCount, adv.incomingCount), _mono);

                    if (adv.navAvailable)
                    {
                        string tag = adv.trapped ? "<color=#ff8080>四面受阻</color>"
                                     : adv.moveAdjustedForWalls ? "<color=#9fd0ff>已避墙修正</color>"
                                     : "<color=#7fff7f>方向通畅</color>";
                        GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                            "  路径：墙距 {0}  选定方向通畅度 {1}  {2}",
                            adv.wallDistance < 0f ? "-" : (adv.wallDistance.ToString("F1", CultureInfo.InvariantCulture) + " 格"),
                            adv.pathClearance.ToString("F1", CultureInfo.InvariantCulture), tag), _mono);
                        if (adv.moveAdjustedForWalls)
                            GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                                "    原本想走 ({0:F2}, {1:F2})，因墙/障碍改为 ({2:F2}, {3:F2})",
                                adv.tacticalX, adv.tacticalY, adv.moveX, adv.moveY), _dim);
                    }

                    if (adv.ranking.Count > 1)
                    {
                        GUILayout.Label("  候选排名（分数越高越该先打）：", _dim);
                        for (int i = 0; i < adv.ranking.Count; i++)
                        {
                            var c = adv.ranking[i];
                            GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                                "    {0}. E{1} {2}  分{3:F0}  距{4:F1}  {5}击  {6}",
                                i + 1, c.entityIndex,
                                (string.IsNullOrEmpty(c.name) ? "<无名>" : (c.name.Length > 12 ? c.name.Substring(0, 11) + "…" : c.name)),
                                c.score, c.distance,
                                c.hitsToKill > 0 ? c.hitsToKill.ToString() : "-",
                                c.killableNow ? "<color=#ff8080>可秒杀</color>" : ""), _dim);
                        }
                    }
                }
            }

            // ---- 小队友军
            if (s.allies.Count > 0)
            {
                GUILayout.Space(6);
                GUILayout.Label("<b>小队友军</b>（" + s.allies.Count + " 名）", _head);
                for (int i = 0; i < s.allies.Count; i++)
                {
                    var a = s.allies[i];
                    GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                        "  槽位{0}  {1,-10}  实体E{2}  血量 {3,7:F0}/{4,7:F0}（{5,3:P0}）  距离={6,6:F1}{7}",
                        a.slotIndex, Mech(a.skillType), a.entityIndex, a.hp, a.maxHp, a.hpPct,
                        a.distanceToPlayer, a.downed ? "   <color=#ff8080>倒地待救</color>" : ""), _mono);
                }
            }

            // ---- 首领
            if (s.bosses.Count > 0)
            {
                GUILayout.Space(6);
                GUILayout.Label("<b>首领</b>（" + s.bosses.Count + " 个）", _head);
                for (int i = 0; i < s.bosses.Count; i++)
                {
                    var b = s.bosses[i];
                    Bar("血量", b.hp, b.maxHp, new Color(1f, 0.45f, 0.35f));
                    GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                        "  {0}  实体E{1}  第{2}关  AI模式 {3}  指令 {4}  坐标 ({5:F1}, {6:F1})  距离={7:F1}{8}",
                        string.IsNullOrEmpty(b.name) ? "<无名首领>" : b.name, b.entityIndex, b.stageIndex,
                        b.aiMode, b.pendingCommand, b.posX, b.posY, b.distanceToPlayer,
                        b.stunned ? "   <color=#ffe27f>硬直中</color>" : ""), _mono);
                }
            }

            // ---- 敌人
            GUILayout.Space(6);
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                "<b>敌人</b>  显示 {0} / 全场 {1}   精英={2}   总血量={3:F0}   弹幕={4}",
                Mathf.Min(s.enemies.Count, MaxRows), s.world.enemyCount, s.world.enemyCountElite,
                s.world.enemyHpTotal, s.world.projectileCount), _head);

            GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                "  {0,-22} {1,5} {2,13} {3,6} {4,6} {5}",
                "名字", "实体", "血量", "距离", "角度", "AI / 标记"), _dim);

            int rows = Mathf.Min(s.enemies.Count, MaxRows);
            for (int i = 0; i < rows; i++)
            {
                var e = s.enemies[i];
                string name = string.IsNullOrEmpty(e.name) ? "<无名>" : e.name;
                if (name.Length > 22) name = name.Substring(0, 21) + "…";

                var sb = new StringBuilder();
                for (int k = 0; k < e.aiTags.Count; k++) { if (k > 0) sb.Append('+'); sb.Append(Tr(AiCn, e.aiTags[k])); }
                if (e.isElite) sb.Append(" <color=#ffd27f>精英</color>");
                if (e.movementPaused) sb.Append(" <color=#9fd0ff>停顿</color>");
                if (e.hitStunned) sb.Append(" <color=#ffe27f>硬直</color>");
                if (e.buffs.Count > 0) sb.Append(" <color=#c9a0ff>[" + DescribeBuffs(e.buffs) + "]</color>");
                if (e.damageTakenThisFrame.Count > 0) sb.Append(" <color=#ff9090>-" + JoinFloats(e.damageTakenThisFrame) + "</color>");

                GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-22} {1,5} {2,7:F0}/{3,-5:F0} {4,6:F1} {5,6:F0} {6}",
                    name, e.entityIndex, e.hp, e.maxHp,
                    e.distanceToPlayer < 0f ? -1f : e.distanceToPlayer,
                    float.IsNaN(e.angleToPlayerDeg) ? 0f : e.angleToPlayerDeg,
                    sb.ToString()), _mono);
            }
            if (s.enemies.Count > rows)
                GUILayout.Label("  …其余 " + (s.enemies.Count - rows) + " 只见 JSON / HTTP 接口", _dim);

            if (s.notes.Count > 0)
            {
                GUILayout.Space(6);
                for (int i = 0; i < s.notes.Count && i < 4; i++)
                    GUILayout.Label("<color=#ffcc80>提示：" + s.notes[i] + "</color>", _dim);
            }

            GUILayout.Space(8);
            GUILayout.Label(httpInfo ?? "", _dim);
            GUILayout.Label("输出目录：" + (outDir ?? "-"), _dim);

            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 18));
        }

        // ---------------------------------------------------------------- helpers

        private static PlayerSnapshot LocalPlayer(CombatSnapshot s)
        {
            for (int i = 0; i < s.players.Count; i++) if (s.players[i].isLocal) return s.players[i];
            return s.players.Count > 0 ? s.players[0] : null;
        }

        private void Bar(string label, float v, float max, Color c)
        {
            float pct = max > 0.0001f ? Mathf.Clamp01(v / max) : 0f;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _dim, GUILayout.Width(52));
            var r = GUILayoutUtility.GetRect(10f, 16f, GUILayout.ExpandWidth(true));
            GUI.color = new Color(0.12f, 0.13f, 0.16f, 0.9f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = c;
            GUI.DrawTexture(new Rect(r.x + 1, r.y + 1, (r.width - 2) * pct, r.height - 2), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture, "{0:F0}/{1:F0}  {2:P0}", v, max, pct),
                _mono, GUILayout.Width(150));
            GUILayout.EndHorizontal();
        }

        private static string DescribeBuffs(List<BuffEntry> buffs)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < buffs.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(Tr(BuffCn, buffs[i].type));
                if (buffs[i].level != 0f) sb.Append(" ×").Append(buffs[i].level.ToString("F1", CultureInfo.InvariantCulture));
                if (buffs[i].timer > 0f) sb.Append("（").Append(buffs[i].timer.ToString("F1", CultureInfo.InvariantCulture)).Append("秒）");
            }
            return sb.ToString();
        }

        private static string DescribePerks(List<PerkEntry> perks)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < perks.Count && i < 24; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append("编号").Append(perks[i].perkId).Append("·").Append(perks[i].level).Append("级");
            }
            if (perks.Count > 24) sb.Append(" …");
            return sb.ToString();
        }

        private static string JoinFloats(List<float> vals)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < vals.Count && i < 12; i++)
            {
                if (i > 0) sb.Append('+');
                sb.Append(vals[i].ToString("F0", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
