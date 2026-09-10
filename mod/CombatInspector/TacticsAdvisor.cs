using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CombatInspector
{
    /// <summary>
    /// 只读战术建议层：从快照推导"该打谁 / 往哪瞄 / 往哪走 / 现在多危险"。
    ///
    /// 刻意不写任何游戏状态 —— 这是自动战斗的第一步：先把决策逻辑暴露出来供人工验证，
    /// 确认判断质量之后再谈接管瞄准（MouseTarget）和操作。
    /// </summary>
    public static class TacticsAdvisor
    {
        // 可调权重（由 Runner 从配置写入）
        public static float MaxEngageDistance = 40f;   // 超过这个距离基本不优先考虑
        public static float ThreatRadius = 9f;         // 进入此范围的敌人算威胁，影响走位
        public static float KiteDistance = 8f;         // 与焦点目标希望保持的距离
        public static float ContactDistance = 3.5f;    // 进入此距离视为贴脸，优先转火
        public static float ProjectileRadius = 7f;     // 敌弹预警半径

        public static void Compute(CombatSnapshot snap)
        {
            if (snap == null) return;

            var adv = new AdvisorAdvice();
            snap.advice = adv;

            var p = LocalPlayer(snap);
            if (p == null || !p.hasPosition || !snap.worldReady)
            {
                adv.noTargetReason = "无玩家实体或世界未就绪";
                return;
            }

            float myArmorMul = p.armorMultiplier > 0f ? p.armorMultiplier : 1f;

            // ---- 路径感知：可玩区、墙段、16 方向障碍射线
            bool navOk = false;
            float wallDist = float.MaxValue;
            try
            {
                var w = World.DefaultGameObjectInjectionWorld;
                if (w != null && w.IsCreated)
                {
                    var em = w.EntityManager;
                    Entity pe = Entity.Null;
                    using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerTag>()))
                    {
                        if (!q.IsEmptyIgnoreFilter)
                        {
                            using (var arr = q.ToEntityArray(Allocator.Temp))
                            {
                                for (int i = 0; i < arr.Length; i++)
                                    if (arr[i].Index == p.entityIndex) { pe = arr[i]; break; }
                            }
                        }
                    }

                    Navigation.Refresh(em, pe, new float3(p.posX, p.posY, p.posZ));
                    navOk = Navigation.PhysicsSingletonFound || Navigation.HasPlayArea || Navigation.WallCount > 0;
                    adv.navAvailable = navOk;
                }
            }
            catch { adv.navAvailable = false; }

            // ------------------------------------------------ 候选打分
            var cands = new List<TargetCandidate>(snap.enemies.Count);
            for (int i = 0; i < snap.enemies.Count; i++)
            {
                var e = snap.enemies[i];
                if (e.hp <= 0f || !e.hasPosition) continue;

                float d = e.distanceToPlayer < 0f ? 999f : e.distanceToPlayer;
                float score = 0f;
                var why = new List<string>(6);

                if (e.willDieFromNextHit)
                {
                    score += 400f;
                    why.Add("下一击可击杀+400");
                }
                else if (e.hitsToKill > 0)
                {
                    score += 260f / e.hitsToKill;
                    why.Add(e.hitsToKill + "击击杀+" + (260f / e.hitsToKill).ToString("F0", CultureInfo.InvariantCulture));
                }

                float prox = 120f * Mathf.Clamp01(1f - d / Mathf.Max(1f, MaxEngageDistance));
                score += prox;
                if (prox > 1f) why.Add("距离" + d.ToString("F1", CultureInfo.InvariantCulture) + " + " + prox.ToString("F0", CultureInfo.InvariantCulture));

                if (d > MaxEngageDistance)
                {
                    score -= 400f;
                    why.Add("超出交战距离-400");
                }

                // 注意：生存类里几乎所有敌人都会锁定玩家，所以"正在打我"没有判别力，不加这项。
                // 真正有判别力的是"它现在能给我造成多少伤害"（DPS 贡献）与"是否已经贴脸"。
                float dpsOnMe = 0f;
                if (d <= ThreatRadius && e.attackDamage > 0)
                {
                    float cd = e.attackCooldown > 0.2f ? e.attackCooldown : 1f;
                    dpsOnMe = (e.attackDamage * myArmorMul) / cd;
                    float w = Mathf.Min(320f, dpsOnMe * 9f);
                    score += w;
                    why.Add("对我DPS约" + dpsOnMe.ToString("F1", CultureInfo.InvariantCulture) + " +" + w.ToString("F0", CultureInfo.InvariantCulture));
                }
                if (d < ContactDistance && dpsOnMe > 4f)
                {
                    score += 520f;
                    why.Add("贴脸输出中+520");
                }

                // 是否正在朝我逼近
                if (e.hasPosition && d > 0.01f)
                {
                    float tox = (p.posX - e.posX) / d, toy = (p.posY - e.posY) / d;
                    float closing = e.moveDirX * tox + e.moveDirY * toy;
                    if (closing > 0.35f && e.moveSpeed > 0.05f)
                    {
                        score += 40f * Mathf.Clamp01(closing);
                        why.Add("逼近中+" + (40f * Mathf.Clamp01(closing)).ToString("F0", CultureInfo.InvariantCulture));
                    }
                }

                if (e.isElite) { score += 50f; why.Add("精英+50"); }
                if (e.isBoss) { score += 120f; why.Add("首领+120"); }

                for (int k = 0; k < e.aiTags.Count; k++)
                {
                    switch (e.aiTags[k])
                    {
                        case "SuicideBomber": score += 150f; why.Add("自爆兵必须优先处理+150"); break;
                        case "Ranged": score += 40f; why.Add("远程+40"); break;
                        case "InterferenceFloater": score += 60f; why.Add("干扰单位+60"); break;
                        case "FlyingMelee": score += 30f; why.Add("飞行近战+30"); break;
                        case "DashMelee": score += 45f; why.Add("突进近战+45"); break;
                    }
                }

                cands.Add(new TargetCandidate
                {
                    entityIndex = e.entityIndex,
                    name = e.name,
                    score = score,
                    distance = d,
                    hp = e.hp,
                    maxHp = e.maxHp,
                    hitsToKill = e.hitsToKill,
                    killableNow = e.willDieFromNextHit,
                    isElite = e.isElite,
                    isBoss = e.isBoss,
                    aiTags = string.Join("+", e.aiTags.ToArray()),
                    why = string.Join("，", why.ToArray())
                });
            }

            if (cands.Count == 0)
            {
                adv.noTargetReason = "场上没有可选敌人";
                adv.dangerLabel = "安全";
                return;
            }

            cands.Sort((a, b) => b.score.CompareTo(a.score));
            for (int i = 0; i < cands.Count && i < 6; i++) adv.ranking.Add(cands[i]);

            var best = cands[0];
            adv.active = true;
            adv.targetEntityIndex = best.entityIndex;
            adv.targetName = best.name;
            adv.targetDistance = best.distance;
            adv.targetHp = best.hp;
            adv.targetMaxHp = best.maxHp;
            adv.targetHitsToKill = best.hitsToKill;
            adv.targetKillableNow = best.killableNow;
            adv.targetWhy = best.why;

            EnemySnapshot focus = null;
            for (int i = 0; i < snap.enemies.Count; i++)
                if (snap.enemies[i].entityIndex == best.entityIndex) { focus = snap.enemies[i]; break; }

            // ------------------------------------------------ 瞄准提前量
            if (focus != null)
            {
                float3 aim;
                float lead;
                if (TryLead(p, focus, out aim, out lead))
                {
                    adv.hasAim = true;
                    adv.aimX = aim.x; adv.aimY = aim.y;
                    adv.aimLeadSeconds = lead;
                }
            }

            // ------------------------------------------------ 走位建议
            float mx = 0f, my = 0f;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                var e = snap.enemies[i];
                if (!e.hasPosition || e.hp <= 0f) continue;
                float d = e.distanceToPlayer;
                if (d < 0.01f || d > ThreatRadius) continue;

                float push = (e.attackDamage * myArmorMul) * (1f - d / ThreatRadius);
                if (push <= 0f) continue;
                mx += (p.posX - e.posX) / d * push;
                my += (p.posY - e.posY) / d * push;
            }

            if (focus != null)
            {
                float d = focus.distanceToPlayer;
                if (d > 0.01f)
                {
                    float tx = (focus.posX - p.posX) / d;
                    float ty = (focus.posY - p.posY) / d;
                    if (d < KiteDistance)
                    {
                        float push = (KiteDistance - d) * 0.6f;
                        mx -= tx * push; my -= ty * push;
                    }
                    else
                    {
                        // 超出理想距离就持续前压（旧实现在 KiteDistance~2.2 倍之间是死区，
                        // 导致怪多时永远只退不进）
                        float pull = Mathf.Min(1.6f, (d - KiteDistance) * 0.14f);
                        mx += tx * pull; my += ty * pull;
                    }
                }
            }

            int incoming = 0;
            for (int i = 0; i < snap.projectiles.Count; i++)
            {
                var q = snap.projectiles[i];
                if (p != null && q.ownerEntityIndex == p.entityIndex) continue;
                float d = q.distanceToPlayer;
                if (d < 0.01f || d > ProjectileRadius) continue;
                incoming++;
                if (q.moveSpeed <= 0.01f) continue;

                // 垂直于弹道、背离弹体方向闪避
                float vx = q.posX - p.posX, vy = q.posY - p.posY;
                float n = Mathf.Sqrt(vx * vx + vy * vy);
                if (n < 0.01f) continue;
                vx /= n; vy /= n;
                float px = -vy, py = vx;
                float side = (px * q.moveSpeed >= 0f) ? 1f : -1f;
                float w = (1f - d / ProjectileRadius) * 2.2f;
                mx += px * side * w;
                my += py * side * w;
            }
            adv.incomingCount = incoming;

            // ---- 墙体 / 可玩区边界的推离，避免建议往墙里、往角落里退
            if (navOk)
            {
                float2 wr = Navigation.WallRepulsion(new float2(p.posX, p.posY), out wallDist);
                mx += wr.x; my += wr.y;
            }

            float mlen = Mathf.Sqrt(mx * mx + my * my);
            if (mlen > 0.0001f)
            {
                float dxn = mx / mlen, dyn = my / mlen;
                adv.tacticalX = dxn;
                adv.tacticalY = dyn;

                float cx2 = dxn, cy2 = dyn;
                if (navOk)
                {
                    float2 chosen; float clearance; bool trapped; bool deviated;
                    Navigation.ChooseSafeDirection(new float2(dxn, dyn), out chosen, out clearance, out trapped, out deviated);
                    cx2 = chosen.x; cy2 = chosen.y;
                    adv.pathClearance = clearance;
                    adv.trapped = trapped;
                    adv.moveAdjustedForWalls = deviated;
                }

                adv.hasMove = true;
                adv.moveX = cx2;
                adv.moveY = cy2;
                adv.moveLabel = DescribeMove(p, focus, cx2, cy2, mlen);
                if (adv.trapped) adv.moveLabel = "四面受阻·找缝";
                else if (adv.moveAdjustedForWalls) adv.moveLabel += "（避墙修正）";
            }
            else
            {
                adv.moveLabel = "保持";
            }
            adv.wallDistance = (wallDist < float.MaxValue) ? wallDist : -1f;

            // ------------------------------------------------ 危险度
            float danger = 0f;
            int threats = 0;
            for (int i = 0; i < snap.enemies.Count; i++)
            {
                var e = snap.enemies[i];
                if (!e.hasPosition || e.hp <= 0f) continue;
                float d = e.distanceToPlayer;
                if (d < 0.01f || d > ThreatRadius * 1.6f) continue;
                threats++;
                danger += (e.attackDamage * myArmorMul) * (1f - d / (ThreatRadius * 1.6f));
            }
            danger += incoming * 8f;
            adv.threatCount = threats;

            float hp = p.hp > 0.01f ? p.hp : 1f;
            adv.dangerScore = Mathf.Clamp01(danger / (hp * 0.9f));
            adv.dangerLabel = adv.dangerScore > 0.75f ? "危急"
                            : adv.dangerScore > 0.45f ? "危险"
                            : adv.dangerScore > 0.18f ? "注意" : "安全";
        }

        /// <summary>
        /// 解 |targetPos + v*t - playerPos| = projSpeed * t 的最小正根，得到命中运动目标所需的提前量。
        /// 目标比弹速快时无解，退化为瞄准当前位置。
        /// </summary>
        private static bool TryLead(PlayerSnapshot p, EnemySnapshot e, out float3 aim, out float lead)
        {
            aim = new float3(e.posX, e.posY, e.posZ);
            lead = 0f;

            float projSpeed = p.projectileSpeed > 1f ? p.projectileSpeed : 0f;
            if (projSpeed <= 1f) return false;

            float rx = e.posX - p.posX, ry = e.posY - p.posY;
            float vlen = Mathf.Sqrt(e.moveDirX * e.moveDirX + e.moveDirY * e.moveDirY);
            if (vlen > 0.0001f)
            {
                // moveDir 是方向向量，乘移速得到速度
                float sx = e.moveDirX / vlen * e.moveSpeed;
                float sy = e.moveDirY / vlen * e.moveSpeed;

                float a = sx * sx + sy * sy - projSpeed * projSpeed;
                float b = 2f * (rx * sx + ry * sy);
                float c = rx * rx + ry * ry;

                float t = -1f;
                if (Mathf.Abs(a) < 1e-4f)
                {
                    if (Mathf.Abs(b) > 1e-4f) t = -c / b;
                }
                else
                {
                    float disc = b * b - 4f * a * c;
                    if (disc >= 0f)
                    {
                        float sq = Mathf.Sqrt(disc);
                        float t1 = (-b - sq) / (2f * a);
                        float t2 = (-b + sq) / (2f * a);
                        t = PositiveMin(t1, t2);
                    }
                }

                if (t > 0f && t < 3f)
                {
                    lead = t;
                    aim = new float3(e.posX + sx * t, e.posY + sy * t, e.posZ);
                    return true;
                }
            }
            return true;   // 无提前量也给出当前指向
        }

        private static float PositiveMin(float t1, float t2)
        {
            bool o1 = t1 > 0f, o2 = t2 > 0f;
            if (o1 && o2) return Mathf.Min(t1, t2);
            if (o1) return t1;
            if (o2) return t2;
            return -1f;
        }

        private static string DescribeMove(PlayerSnapshot p, EnemySnapshot focus, float nx, float ny, float mag)
        {
            if (focus == null || !focus.hasPosition) return "规避";
            float dx = focus.posX - p.posX, dy = focus.posY - p.posY;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) return "调整";
            dx /= len; dy /= len;
            float dot = dx * nx + dy * ny;

            if (mag < 0.6f) return "轻微调整";
            if (dot > 0.55f) return "前压接近目标";
            if (dot < -0.55f) return "后撤拉开距离";
            if (dot > -0.15f) return "侧向绕圈";
            return "规避";
        }

        private static PlayerSnapshot LocalPlayer(CombatSnapshot s)
        {
            if (s.players == null) return null;
            for (int i = 0; i < s.players.Count; i++) if (s.players[i].isLocal) return s.players[i];
            return s.players.Count > 0 ? s.players[0] : null;
        }
    }
}
