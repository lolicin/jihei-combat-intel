using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
#if BEPINEX6
using BepInEx.Unity.Mono;
#endif
using UnityEngine;
using UnityEngine.InputSystem;

namespace CombatInspector
{
    /// <summary>
    /// BepInEx entry point. This is the ONLY file that touches the loader API, so switching between
    /// BepInEx 5 (default) and BepInEx 6 Unity.Mono is just -p:Loader=bepinex6 at build time.
    /// </summary>
    [BepInPlugin(GuidId, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GuidId = "com.jhx9676.mechcore.combatinspector";
        public const string Name = "CombatInspector";
        public const string Version = "1.0.0";

        private static ManualLogSource _log;
        private static Runner _runner;

        internal static void LogInfo(string msg)
        {
            if (_log != null) _log.LogInfo(msg);
            else Debug.Log("[CombatInspector] " + msg);
        }

        internal static void LogWarn(string msg)
        {
            if (_log != null) _log.LogWarning(msg);
            else Debug.LogWarning("[CombatInspector] " + msg);
        }

        internal static void LogError(string msg)
        {
            if (_log != null) _log.LogError(msg);
            else Debug.LogError("[CombatInspector] " + msg);
        }

        private void Awake()
        {
            _log = Logger;
            LogInfo("==========================================================");
            LogInfo("CombatInspector " + Version + " loading");
            LogInfo("  unity   : " + Application.unityVersion);
            LogInfo("  platform: " + Application.platform + " / " + SystemInfo.operatingSystem);
            LogInfo("  dataPath: " + Application.dataPath);
#if BEPINEX6
            LogInfo("  loader  : BepInEx 6 (Unity.Mono)");
#else
            LogInfo("  loader  : BepInEx 5");
#endif

            try
            {
                Install();
                LogInfo("CombatInspector installed successfully.");
            }
            catch (Exception ex)
            {
                LogError("CombatInspector failed to install: " + ex);
            }
            LogInfo("==========================================================");
        }

        private void Install()
        {
            string defaultOut = Path.Combine(Paths.PluginPath, "CombatInspectorOut");

            var cOutDir = Config.Bind("Output", "Directory", defaultOut,
                "Where latest.json / latest_deep.json / enemies.csv are written.");
            var cWriteJson = Config.Bind("Output", "WriteJson", true, "Periodically write latest.json.");
            var cWriteCsv = Config.Bind("Output", "WriteCsv", true, "Periodically write enemies.csv.");

            var cInterval = Config.Bind("Capture", "IntervalSeconds", 0.25f,
                "How often the main-thread capture runs. 0.25 = 4 snapshots per second.");
            var cFileInterval = Config.Bind("Capture", "FileWriteIntervalSeconds", 1.0f,
                "How often latest.json / enemies.csv are rewritten.");
            var cMaxEnemies = Config.Bind("Capture", "MaxEnemies", 600, "Cap on enemies captured per snapshot.");
            var cMaxProjectiles = Config.Bind("Capture", "MaxProjectiles", 400, "Cap on projectiles captured per snapshot.");
            var cComponentNames = Config.Bind("Capture", "IncludeComponentNames", false,
                "Also list every component type name on each captured entity (verbose).");

            var cHttp = Config.Bind("Http", "Enabled", true, "Serve the state on a local HTTP endpoint.");
            var cPort = Config.Bind("Http", "Port", 8790, "TCP port on 127.0.0.1.");

            var cOverlayKey = Config.Bind("Hotkeys", "OverlayKey", "F9", "Toggle the in-game overlay.");
            var cDumpKey = Config.Bind("Hotkeys", "DumpKey", "F10", "Force a snapshot + deep dump to disk.");
            var cRadarKey = Config.Bind("Hotkeys", "RadarKey", "F11", "Toggle the radar window.");
            var cOverlayVisible = Config.Bind("Hotkeys", "OverlayVisibleByDefault", true, "");

            var cRadarVisible = Config.Bind("Radar", "VisibleByDefault", true, "Show the radar window on start.");
            var cRadarSize = Config.Bind("Radar", "Size", 300f, "Radar size in pixels (160-700).");
            var cRadarRange = Config.Bind("Radar", "Range", 0f, "0 = auto-fit to enemies; otherwise fixed world units.");
            var cRadarNames = Config.Bind("Radar", "ShowNames", false, "Draw enemy names next to their dots.");

            var cBarsOn = Config.Bind("Bars", "Enabled", true, "HP bar above EVERY enemy head (game only does elites natively).");
            var cBarsMax = Config.Bind("Bars", "MaxCount", 40, "Max bars drawn per frame (nearest first).");
            var cBarsDist = Config.Bind("Bars", "MaxDistance", 70f, "Skip enemies farther than this many world units. 0 = no limit.");
            var cBarsOffset = Config.Bind("Bars", "HeadOffsetY", 0.7f, "World-space Y offset above the entity origin.");
            var cBarsText = Config.Bind("Bars", "ShowText", true, "Show the numeric HP / predicted-after-hit line.");
            var cBarsPred = Config.Bind("Bars", "ShowPrediction", true, "Orange segment + white tick showing where HP lands after the next hit.");
            var cBarsLeader = Config.Bind("Bars", "ShowLeaderLine", true, "Thin line from the bar down to the entity origin, so it is clear which monster a bar belongs to.");

            var cBarsKey = Config.Bind("Hotkeys", "BarsKey", "F12", "Toggle the enemy health bars.");
            var cAdvKey = Config.Bind("Hotkeys", "AdvisorKey", "F8", "Toggle the read-only tactical suggestions.");

            var cAdvOn = Config.Bind("Advisor", "Enabled", true,
                "Read-only suggestions: which enemy to focus, where to aim (with lead), where to move. Never writes to the game.");
            var cAdvEngage = Config.Bind("Advisor", "MaxEngageDistance", 40f, "Targets farther than this are heavily penalised.");
            var cAdvThreat = Config.Bind("Advisor", "ThreatRadius", 9f, "Enemies inside this radius shape the movement suggestion.");
            var cAdvKite = Config.Bind("Advisor", "KiteDistance", 8f, "Preferred stand-off distance from the focus target.");

            var cAimKey = Config.Bind("Hotkeys", "AutoAimKey", "F7",
                "Toggle aim takeover. It only writes MouseTarget (crosshair); movement and skills stay yours.");
            var cAimOn = Config.Bind("AutoAim", "Enabled", false,
                "Start with aim takeover on. Off by default because it moves your crosshair.");
            var cAimDist = Config.Bind("AutoAim", "MaxDistance", 45f, "Never aim at targets farther than this many world units.");

            var go = new GameObject("CombatInspector.Runner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);

            _runner = go.AddComponent<Runner>();
            _runner.OutDir = cOutDir.Value;
            _runner.CaptureInterval = Mathf.Clamp(cInterval.Value, 0.02f, 60f);
            _runner.FileWriteInterval = Mathf.Clamp(cFileInterval.Value, 0.2f, 600f);
            _runner.MaxEnemies = Mathf.Clamp(cMaxEnemies.Value, 1, 20000);
            _runner.MaxProjectiles = Mathf.Clamp(cMaxProjectiles.Value, 0, 20000);
            _runner.IncludeComponentNames = cComponentNames.Value;
            _runner.WriteJsonFile = cWriteJson.Value;
            _runner.WriteCsvFile = cWriteCsv.Value;
            _runner.HttpEnabled = cHttp.Value;
            _runner.HttpPort = Mathf.Clamp(cPort.Value, 1024, 65535);
            _runner.OverlayKey = ParseKey(cOverlayKey.Value, Key.F9);
            _runner.DumpKey = ParseKey(cDumpKey.Value, Key.F10);
            _runner.RadarKey = ParseKey(cRadarKey.Value, Key.F11);
            _runner.RadarVisible = cRadarVisible.Value;
            _runner.RadarSize = Mathf.Clamp(cRadarSize.Value, 160f, 700f);
            _runner.RadarRange = Mathf.Max(0f, cRadarRange.Value);
            _runner.RadarNames = cRadarNames.Value;
            _runner.BarsEnabled = cBarsOn.Value;
            _runner.BarsMaxCount = Mathf.Clamp(cBarsMax.Value, 1, 400);
            _runner.BarsMaxDistance = Mathf.Max(0f, cBarsDist.Value);
            _runner.BarsHeadOffsetY = cBarsOffset.Value;
            _runner.BarsShowText = cBarsText.Value;
            _runner.BarsShowPrediction = cBarsPred.Value;
            _runner.BarsLeaderLine = cBarsLeader.Value;
            _runner.BarsKey = ParseKey(cBarsKey.Value, Key.F12);
            _runner.AdvisorKey = ParseKey(cAdvKey.Value, Key.F8);
            _runner.AdvisorEnabled = cAdvOn.Value;
            _runner.AdvisorMaxEngage = Mathf.Max(5f, cAdvEngage.Value);
            _runner.AdvisorThreatRadius = Mathf.Max(2f, cAdvThreat.Value);
            _runner.AdvisorKiteDistance = Mathf.Max(1f, cAdvKite.Value);
            _runner.AutoAimKey = ParseKey(cAimKey.Value, Key.F7);
            _runner.AutoAimEnabled = cAimOn.Value;
            _runner.AutoAimMaxDistance = Mathf.Max(5f, cAimDist.Value);
            _runner.enabled = true;

            // Awake already ran with field defaults; re-apply now that config values are in.
            _runner.ApplySettings();

            TryInstallAimPatch();

            // Overlay default visibility is applied after Awake has built the Overlay instance.
            var start = go.AddComponent<ApplyOverlayVisibility>();
            start.visible = cOverlayVisible.Value;

            LogInfo("  outDir  : " + Safe(cOutDir.Value));
            LogInfo("  capture : every " + _runner.CaptureInterval.ToString("F2", CultureInfo.InvariantCulture) + "s");
            if (_runner.HttpEnabled)
                LogInfo("  http    : http://127.0.0.1:" + _runner.HttpPort + "/  (set Http/Enabled=false to disable)");
            LogInfo("  hotkeys : " + _runner.OverlayKey + " = overlay, " + _runner.DumpKey + " = dump to disk, " +
                    _runner.RadarKey + " = radar, " + _runner.BarsKey + " = health bars, " +
                    _runner.AdvisorKey + " = advisor");
            LogInfo("  advisor : " + (_runner.AdvisorEnabled ? "on (read-only suggestions)" : "off"));
            LogInfo("  bars    : " + (_runner.BarsEnabled ? "on (max " + _runner.BarsMaxCount + ", dist " + _runner.BarsMaxDistance + ")" : "off"));
        }

        /// <summary>
        /// 挂接管瞄准的 Harmony postfix。手动 Patch 而不用 PatchAll：目标方法找不到时要**明确报出来**，
        /// 而不是静默不生效 —— 这一整个排错过程最大的教训就是静默失败。
        /// </summary>
        private void TryInstallAimPatch()
        {
            try
            {
                var t = AccessTools.TypeByName("MouseInputSystem");
                if (t == null)
                {
                    LogWarn("接管瞄准不可用：找不到 MouseInputSystem 类型");
                    AimOverrideSystem.LastStatus = "补丁未挂载（找不到 MouseInputSystem）";
                    return;
                }

                // 优先 Entities 为 unmanaged ISystem 生成的静态入口（postfix 不涉及 struct 装箱），
                // 退而求其次试用户写的 OnUpdate(ref SystemState)。
                MethodInfo target = AccessTools.Method(t, "__codegen__OnUpdate")
                                 ?? AccessTools.Method(t, "OnUpdate");
                if (target == null)
                {
                    LogWarn("接管瞄准不可用：MouseInputSystem 上没有可补丁的 OnUpdate");
                    AimOverrideSystem.LastStatus = "补丁未挂载（无可补丁方法）";
                    return;
                }

                var postfix = AccessTools.Method(typeof(AimTakeoverPatch), "Postfix");
                var harmony = new Harmony("com.jhx9676.mechcore.combatinspector.autoaim");
                harmony.Patch(target, null, new HarmonyMethod(postfix));

                _aimPatched = true;
                LogInfo("  aimpatch: " + t.Name + "." + target.Name + " postfix 已挂载（F7 开启接管瞄准）");
            }
            catch (Exception ex)
            {
                LogWarn("挂接管瞄准补丁失败：" + ex.GetType().Name + ": " + ex.Message);
                AimOverrideSystem.LastStatus = "补丁挂载失败：" + ex.GetType().Name;
            }
        }

        private bool _aimPatched;

        private static string Safe(string p)
        {
            try { return Path.GetFullPath(p); } catch { return p; }
        }

        private static Key ParseKey(string s, Key fallback)
        {
            Key k;
            if (!string.IsNullOrEmpty(s) && Enum.TryParse(s.Trim(), true, out k)) return k;
            LogWarn("unknown hotkey '" + s + "', falling back to " + fallback);
            return fallback;
        }
    }

    /// <summary>Applies config-driven overlay visibility once Runner has constructed its Overlay.</summary>
    public sealed class ApplyOverlayVisibility : MonoBehaviour
    {
        public bool visible = true;

        private void Start()
        {
            if (Runner.Instance != null) Runner.Instance.SetOverlayVisible(visible);
            Destroy(this);
        }
    }
}
