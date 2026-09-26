using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CombatInspector
{
    /// <summary>
    /// The MonoBehaviour that owns the capture loop, the hotkeys, the file output and the HTTP
    /// endpoint. All ECS access happens here, on the main thread, inside Update().
    /// </summary>
    public sealed class Runner : MonoBehaviour
    {
        public static Runner Instance { get; private set; }

        [Header("capture")]
        public float CaptureInterval = 0.25f;
        public float FileWriteInterval = 1f;
        public int MaxEnemies = 600;
        public int MaxProjectiles = 400;
        public bool IncludeComponentNames = false;

        [Header("http")]
        public bool HttpEnabled = true;
        public int HttpPort = 8790;

        [Header("output")]
        public string OutDir;
        public bool WriteJsonFile = true;
        public bool WriteCsvFile = true;

        [Header("hotkeys")]
        public Key DumpKey = Key.F10;
        public Key BarsKey = Key.F12;
        public Key AdvisorKey = Key.F8;
        public Key AutoAimKey = Key.F7;

        [Header("advisor（只读建议层）")]
        public bool AdvisorEnabled = true;
        public float AdvisorMaxEngage = 40f;
        public float AdvisorThreatRadius = 9f;
        public float AdvisorKiteDistance = 8f;

        [Header("auto-aim（第②步：只接管瞄准）")]
        public bool AutoAimEnabled = false;
        public float AutoAimMaxDistance = 45f;

        [Header("health bars")]
        public bool BarsEnabled = true;
        public int BarsMaxCount = 40;
        public float BarsMaxDistance = 70f;
        public float BarsHeadOffsetY = 0.95f;
        public bool BarsShowText = true;
        public bool BarsShowPrediction = true;
        public bool BarsLeaderLine = true;

        private readonly HealthBars _bars = new HealthBars();
        private StateHttpServer _http;
        private CombatSnapshot _latest;
        private float _nextCapture;
        private float _nextFileWrite;
        private string _statusLine = "starting...";
        private long _captureCount;
        private string _lastError;

        /// <summary>
        /// Push the public config fields into the sub-windows / scanner.
        /// Called from Awake AND again by Plugin after it assigns the config values —
        /// AddComponent runs Awake immediately with field defaults, so without the second
        /// call a config like Radar/VisibleByDefault=false would be silently ignored.
        /// </summary>
        public void ApplySettings()
        {
            CombatScanner.MaxEnemies = Mathf.Clamp(MaxEnemies, 1, 20000);
            CombatScanner.MaxProjectiles = Mathf.Max(0, MaxProjectiles);
            CombatScanner.IncludeComponentNames = IncludeComponentNames;

            _bars.Enabled = BarsEnabled;
            _bars.MaxCount = Mathf.Clamp(BarsMaxCount, 1, 400);
            _bars.MaxDistance = Mathf.Max(0f, BarsMaxDistance);
            _bars.HeadOffsetY = BarsHeadOffsetY;
            _bars.ShowText = BarsShowText;
            _bars.ShowPrediction = BarsShowPrediction;
            _bars.ShowLeaderLine = BarsLeaderLine;

            TacticsAdvisor.MaxEngageDistance = Mathf.Max(5f, AdvisorMaxEngage);
            TacticsAdvisor.ThreatRadius = Mathf.Max(2f, AdvisorThreatRadius);
            TacticsAdvisor.KiteDistance = Mathf.Max(1f, AdvisorKiteDistance);
            _bars.MarkAdvice = AdvisorEnabled;

            AimOverrideSystem.MaxAimDistance = Mathf.Max(5f, AutoAimMaxDistance);
            AimOverrideSystem.Active = AutoAimEnabled && AdvisorEnabled;
        }

        /// <summary>
        /// 把快照里的 entityIndex 解析成真正的 Entity 句柄，交给接管瞄准用。
        /// 每次抓取（4Hz）做一次即可：目标身份 4Hz 更新，但每帧都会重读它的当前位置，
        /// 所以准星依然 60fps 平滑跟随。
        /// </summary>
        private void ResolveAimEntities(CombatSnapshot snap)
        {
            AimOverrideSystem.MechEntity = Entity.Null;
            AimOverrideSystem.TargetEntity = Entity.Null;

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;

            var p = LocalPlayerOf(snap);
            if (p != null)
            {
                using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<PlayerTag>()))
                {
                    if (!q.IsEmptyIgnoreFilter)
                    {
                        using (var arr = q.ToEntityArray(Allocator.Temp))
                        {
                            for (int i = 0; i < arr.Length; i++)
                            {
                                if (arr[i].Index == p.entityIndex)
                                {
                                    AimOverrideSystem.MechEntity = arr[i];
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            var adv = snap.advice;
            if (adv != null && adv.active)
            {
                int ti = adv.targetEntityIndex;
                int tv = -1;
                for (int i = 0; i < snap.enemies.Count; i++)
                    if (snap.enemies[i].entityIndex == ti) { tv = snap.enemies[i].entityVersion; break; }

                using (var q2 = em.CreateEntityQuery(ComponentType.ReadOnly<EnemyTag>()))
                {
                    if (!q2.IsEmptyIgnoreFilter)
                    {
                        using (var arr2 = q2.ToEntityArray(Allocator.Temp))
                        {
                            for (int i = 0; i < arr2.Length; i++)
                            {
                                if (arr2[i].Index == ti && (tv < 0 || arr2[i].Version == tv))
                                {
                                    AimOverrideSystem.TargetEntity = arr2[i];
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------- unity lifecycle

        private void Awake()
        {
            Instance = this;
            if (string.IsNullOrEmpty(OutDir))
                OutDir = Path.Combine(Application.dataPath, "..", "CombatInspectorOut");

            ApplySettings();

            try { Directory.CreateDirectory(OutDir); }
            catch (Exception ex) { Log("could not create output dir: " + ex.Message); }

            if (HttpEnabled)
            {
                _http = new StateHttpServer(HttpPort);
                _http.Log = Log;
                _http.DashboardHtml = LoadDashboardHtml();
                if (!_http.Start()) { _http = null; HttpEnabled = false; }
            }

            Log("CombatInspector ready. " + DumpKey + " = dump files, " +
                BarsKey + " = health bars, " + AdvisorKey + " = advisor, " + AutoAimKey + " = auto aim. out=" + OutDir);
        }

        private void OnDestroy()
        {
            try { if (_http != null) _http.Dispose(); } catch { }
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            try
            {
                HandleHotkeys();
                DrainHttp();

                if (Time.unscaledTime >= _nextCapture)
                {
                    _nextCapture = Time.unscaledTime + Mathf.Max(0.02f, CaptureInterval);
                    DoCapture(deep: false, enemies: 0, depth: 0, indent: false);
                }

                if (WriteJsonFile || WriteCsvFile)
                {
                    if (Time.unscaledTime >= _nextFileWrite)
                    {
                        _nextFileWrite = Time.unscaledTime + Mathf.Max(0.2f, FileWriteInterval);
                        WriteFiles(deep: false);
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.GetType().Name + ": " + ex.Message;
                Log("Update failed: " + _lastError);
            }
        }

        private void OnGUI()
        {
            // 只画血条（含预期伤害/可秒杀标记）。信息浮层与雷达已按需求移除。
            if (_latest != null)
            {
                try { _bars.Draw(_latest); }
                catch (Exception ex) { Log("health bars draw failed: " + ex); }
            }
        }

        // ---------------------------------------------------------------- hotkeys

        private void HandleHotkeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            if (WasPressed(kb, DumpKey))
            {
                try
                {
                    WriteFiles(deep: true, deepEnemies: 8, deepDepth: 4);
                    Log("wrote snapshot + deep dump to " + OutDir);
                }
                catch (Exception ex) { Log("dump failed: " + ex.Message); }
            }
            if (WasPressed(kb, BarsKey))
            {
                _bars.Enabled = !_bars.Enabled;
                Log("health bars " + (_bars.Enabled ? "shown" : "hidden"));
            }
            if (WasPressed(kb, AdvisorKey))
            {
                AdvisorEnabled = !AdvisorEnabled;
                _bars.MarkAdvice = AdvisorEnabled;
                Log("advisor " + (AdvisorEnabled ? "on" : "off"));
            }
            if (WasPressed(kb, AutoAimKey))
            {
                AutoAimEnabled = !AutoAimEnabled;

                // 接管瞄准依赖建议层给出的目标，开自动瞄准时顺带把建议层打开
                if (AutoAimEnabled && !AdvisorEnabled)
                {
                    AdvisorEnabled = true;
                    _bars.MarkAdvice = true;
                }

                AimOverrideSystem.Active = AutoAimEnabled && AdvisorEnabled;
                AimOverrideSystem.MaxAimDistance = Mathf.Max(5f, AutoAimMaxDistance);
                Log("自动瞄准 " + (AimOverrideSystem.Active
                    ? "开：接管 MouseTarget（移动和技能仍由你操作）"
                    : "关：鼠标已交还给你"));
            }
        }

        private static bool WasPressed(Keyboard kb, Key k)
        {
            try
            {
                var c = kb[k];
                return c.wasPressedThisFrame;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- capture

        private string DoCapture(bool deep, int enemies, int depth, bool indent)
        {
            CombatScanner.MaxEnemies = MaxEnemies;
            CombatScanner.MaxProjectiles = MaxProjectiles;
            CombatScanner.IncludeComponentNames = IncludeComponentNames;

            var snap = CombatScanner.Capture();
            _latest = snap;
            _captureCount++;

            // 先算预测，再序列化，这样 JSON / 仪表盘 / 血条看到的是同一份预测值
            try { DamageTracker.Update(snap); }
            catch (Exception ex) { snap.notes.Add("damage prediction failed: " + ex.Message); }

            if (AdvisorEnabled)
            {
                try { TacticsAdvisor.Compute(snap); }
                catch (Exception ex) { snap.notes.Add("advisor failed: " + ex.Message); }
            }

            try
            {
                ResolveAimEntities(snap);
                WireAutoAim(snap);
            }
            catch (Exception ex) { snap.notes.Add("autoaim wiring failed: " + ex.Message); }

            snap.screenWidth = Screen.width;
            snap.screenHeight = Screen.height;
            snap.barsInfo = _bars.Describe();
            snap.damageTrackerEntries = DamageTracker.TrackedCount;

            if (deep)
            {
                string err;
                var d = DeepDumper.CaptureDeep(enemies, depth, out err);
                if (!string.IsNullOrEmpty(err)) snap.notes.Add("deep: " + err);
                snap.deep = d;
            }

            string json = MiniJson.Serialize(snap, indent);
            if (_http != null && !deep && !indent)
                _http.LatestStateJson = json;

            _statusLine = snap.worldReady
                ? string.Format(CultureInfo.InvariantCulture,
                    "ok  players={0} allies={1} enemies={2} bosses={3} projectiles={4}",
                    snap.players.Count, snap.allies.Count, snap.enemies.Count, snap.bosses.Count, snap.projectiles.Count)
                : "world not ready";

            return json;
        }

        /// <summary>把建议目标参数交给接管瞄准，并记录量化诊断（接管到底有没有生效）。</summary>
        private void WireAutoAim(CombatSnapshot snap)
        {
            var p = LocalPlayerOf(snap);
            var adv = snap.advice;

            if (adv != null && adv.active)
            {
                if (p != null && p.projectileSpeed > 1f) AimOverrideSystem.ProjectileSpeed = p.projectileSpeed;

                // 游戏里实际的 MouseTarget 与建议瞄准点差多少：接管生效时应明显收敛
                if (p != null && p.hasAim && adv.hasAim)
                {
                    float dx = p.aimX - adv.aimX, dy = p.aimY - adv.aimY;
                    adv.aimDiff = Mathf.Sqrt(dx * dx + dy * dy);
                }
            }

            Entity mech = AimOverrideSystem.MechEntity;
            Entity tgt = AimOverrideSystem.TargetEntity;

            snap.autoAimInfo = "enabled=" + AimOverrideSystem.Active +
                " 状态=" + AimOverrideSystem.LastStatus +
                " 补丁执行=" + AimOverrideSystem.PatchRunCount +
                " 本帧写入=" + AimOverrideSystem.LastWrites +
                " 累计写入=" + AimOverrideSystem.TotalWrites +
                " 机甲=" + (mech == Entity.Null ? "无" : ("E" + mech.Index + "v" + mech.Version)) +
                " 目标=" + (tgt == Entity.Null ? "无" : ("E" + tgt.Index + "v" + tgt.Version)) +
                " 上次瞄准=(" + AimOverrideSystem.LastAimX.ToString("F2", CultureInfo.InvariantCulture) +
                ", " + AimOverrideSystem.LastAimY.ToString("F2", CultureInfo.InvariantCulture) + ")" +
                " 提前=" + AimOverrideSystem.LastLeadSeconds.ToString("F3", CultureInfo.InvariantCulture) + "s" +
                " 弹速=" + AimOverrideSystem.ProjectileSpeed.ToString("F1", CultureInfo.InvariantCulture) +
                (string.IsNullOrEmpty(AimOverrideSystem.PatchError) ? "" : " 异常=" + AimOverrideSystem.PatchError);

            float2 pm = Navigation.PlayMin, pM = Navigation.PlayMax;
            snap.navInfo = "可玩区=" + (Navigation.HasPlayArea
                    ? ("(" + pm.x.ToString("F1", CultureInfo.InvariantCulture) + "," + pm.y.ToString("F1", CultureInfo.InvariantCulture) + ")~(" +
                       pM.x.ToString("F1", CultureInfo.InvariantCulture) + "," + pM.y.ToString("F1", CultureInfo.InvariantCulture) + ")")
                    : "未取得") +
                " 墙段=" + Navigation.WallCount +
                " 受阻方向=" + Navigation.BlockedCount + "/" + Navigation.Directions +
                " 其中生物=" + Navigation.CreatureBlockedCount +
                " 有通路=" + Navigation.AnyOpen +
                " 障碍命中=" + Navigation.TotalRayHits + "/" + Navigation.RefreshCount + "次刷新" +
                " 物理单例=" + (Navigation.PhysicsSingletonFound ? "已取到" : "没取到") +
                " 探测异常=" + Navigation.ProbeExceptionCount +
                " 地面跳过=" + Navigation.GroundSkippedCount +
                " 阻挡=[" + Navigation.DescribeBlockers() + "]" +
                " 全局样本=" + DamageTracker.GlobalSamples +
                (string.IsNullOrEmpty(Navigation.LastError) ? "" : " 首个异常=" + Navigation.LastError);
        }

        private static PlayerSnapshot LocalPlayerOf(CombatSnapshot s)
        {
            if (s == null || s.players == null) return null;
            for (int i = 0; i < s.players.Count; i++) if (s.players[i].isLocal) return s.players[i];
            return s.players.Count > 0 ? s.players[0] : null;
        }

        private void DrainHttp()
        {
            if (_http == null) return;
            MainThreadRequest req;
            while (_http.Pending.TryDequeue(out req))
            {
                try
                {
                    switch (req.Kind)
                    {
                        case "snapshot":
                            req.ResultJson = DoCapture(deep: false, enemies: 0, depth: 0, indent: false);
                            break;
                        case "deep":
                            req.ResultJson = DoCapture(deep: true, enemies: req.Enemies, depth: req.Depth, indent: true);
                            break;
                        case "dump":
                            WriteFiles(deep: true, deepEnemies: Math.Max(1, req.Enemies), deepDepth: Math.Max(1, req.Depth));
                            req.ResultJson = "{\"ok\":true,\"dir\":\"" + StateHttpServer.JsonEscape(Norm(OutDir)) + "\"}";
                            break;
                        default:
                            req.Error = "unknown request kind: " + req.Kind;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    req.Error = ex.GetType().Name + ": " + ex.Message;
                    Log("http request '" + req.Kind + "' failed: " + req.Error);
                }
                finally
                {
                    try { req.Done.Set(); } catch { }
                }
            }
        }

        // ---------------------------------------------------------------- files

        private void WriteFiles(bool deep, int deepEnemies = 8, int deepDepth = 4)
        {
            if (string.IsNullOrEmpty(OutDir)) return;
            try { Directory.CreateDirectory(OutDir); } catch { return; }

            var snap = _latest;
            if (snap == null) return;

            if (deep)
            {
                var fresh = DoCapture(deep: true, enemies: deepEnemies, depth: deepDepth, indent: true);
                WriteAllTextSafe(Path.Combine(OutDir, "latest_deep.json"), fresh);
                snap = _latest;
            }

            if (WriteJsonFile && snap != null)
            {
                string json = MiniJson.Serialize(snap, true);
                WriteAllTextSafe(Path.Combine(OutDir, "latest.json"), json);
            }

            if (WriteCsvFile && snap != null)
                WriteAllTextSafe(Path.Combine(OutDir, "enemies.csv"), BuildCsv(snap));
        }

        private void WriteAllTextSafe(string path, string content)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex) { Log("write failed for " + path + ": " + ex.Message); }
        }

        private static string BuildCsv(CombatSnapshot s)
        {
            var sb = new StringBuilder();
            sb.Append("entityIndex,name,hp,maxHp,hpPct,posX,posY,posZ,distanceToPlayer,angleToPlayerDeg,")
              .Append("aiTags,isElite,isBoss,moveSpeed,attackDamage,attackCooldown,targetEntityIndex,")
              .Append("lastHitByEntityIndex,hitStunned,movementPaused,buffs,damageTakenThisFrame,tags\n");

            var inv = CultureInfo.InvariantCulture;
            for (int i = 0; i < s.enemies.Count; i++)
            {
                var e = s.enemies[i];
                sb.Append(e.entityIndex).Append(',')
                  .Append(Csv(e.name)).Append(',')
                  .Append(e.hp.ToString("F2", inv)).Append(',')
                  .Append(e.maxHp.ToString("F2", inv)).Append(',')
                  .Append(e.hpPct.ToString("F4", inv)).Append(',')
                  .Append(e.posX.ToString("F3", inv)).Append(',')
                  .Append(e.posY.ToString("F3", inv)).Append(',')
                  .Append(e.posZ.ToString("F3", inv)).Append(',')
                  .Append(e.distanceToPlayer.ToString("F3", inv)).Append(',')
                  .Append((float.IsNaN(e.angleToPlayerDeg) ? 0f : e.angleToPlayerDeg).ToString("F1", inv)).Append(',')
                  .Append(Csv(string.Join("+", e.aiTags.ToArray()))).Append(',')
                  .Append(e.isElite ? 1 : 0).Append(',')
                  .Append(e.isBoss ? 1 : 0).Append(',')
                  .Append(e.moveSpeed.ToString("F3", inv)).Append(',')
                  .Append(e.attackDamage).Append(',')
                  .Append(e.attackCooldown.ToString("F3", inv)).Append(',')
                  .Append(e.targetEntityIndex).Append(',')
                  .Append(e.lastHitByEntityIndex).Append(',')
                  .Append(e.hitStunned ? 1 : 0).Append(',')
                  .Append(e.movementPaused ? 1 : 0).Append(',')
                  .Append(Csv(JoinBuffs(e.buffs))).Append(',')
                  .Append(Csv(JoinFloats(e.damageTakenThisFrame))).Append(',')
                  .Append(Csv(string.Join("+", e.tags.ToArray())))
                  .Append('\n');
            }
            return sb.ToString();
        }

        private static string JoinBuffs(List<BuffEntry> b)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < b.Count; i++) { if (i > 0) sb.Append('|'); sb.Append(b[i].type); }
            return sb.ToString();
        }

        private static string JoinFloats(List<float> v)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < v.Count; i++) { if (i > 0) sb.Append('|'); sb.Append(v[i].ToString("F1", CultureInfo.InvariantCulture)); }
            return sb.ToString();
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string Norm(string p)
        {
            try { return Path.GetFullPath(p).Replace('\\', '/'); } catch { return p; }
        }

        private void Log(string msg)
        {
            try { Plugin.LogInfo(msg); }
            catch { Debug.Log("[CombatInspector] " + msg); }
        }

        /// <summary>The web dashboard ships inside the mod assembly, so there is nothing to deploy.</summary>
        private static string LoadDashboardHtml()
        {
            try
            {
                var asm = typeof(Runner).Assembly;
                using (var s = asm.GetManifestResourceStream("CombatInspector.dashboard.html"))
                {
                    if (s == null) return null;
                    using (var sr = new StreamReader(s, Encoding.UTF8)) return sr.ReadToEnd();
                }
            }
            catch { return null; }
        }
    }
}
