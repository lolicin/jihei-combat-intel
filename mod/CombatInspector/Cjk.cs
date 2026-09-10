using UnityEngine;

namespace CombatInspector
{
    /// <summary>
    /// IMGUI's default skin font has no CJK glyphs, so any Chinese label drawn with a stock
    /// GUIStyle comes out as tofu boxes. The game's own text is uGUI and unaffected, but our
    /// IMGUI windows must explicitly use a dynamic OS font that falls back to a Chinese face.
    ///
    /// Shared by the main overlay and the radar so they stay consistent and only build the
    /// font once.
    /// </summary>
    public static class Cjk
    {
        private static Font _font;
        private static GUIStyle _label;
        private static GUIStyle _labelRich;
        private static GUIStyle _small;
        private static GUIStyle _window;
        private static GUIStyle _button;
        private static GUIStyle _toggle;

        public static Font Font
        {
            get
            {
                if (_font == null)
                {
                    try
                    {
                        _font = Font.CreateDynamicFontFromOSFont(
                            new[] { "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "SimHei", "Segoe UI", "Arial" },
                            13);
                    }
                    catch { _font = null; }
                    if (_font == null) _font = GUI.skin.font;
                }
                return _font;
            }
        }

        /// <summary>普通标签（不含富文本）。</summary>
        public static GUIStyle Label
        {
            get
            {
                if (_label == null)
                    _label = new GUIStyle(GUI.skin.label) { font = Font, fontSize = 13, richText = false };
                return _label;
            }
        }

        /// <summary>带 &lt;color&gt;/&lt;b&gt; 的标签。</summary>
        public static GUIStyle LabelRich
        {
            get
            {
                if (_labelRich == null)
                    _labelRich = new GUIStyle(GUI.skin.label) { font = Font, fontSize = 13, richText = true, wordWrap = false };
                return _labelRich;
            }
        }

        /// <summary>小号淡色标签（图例、坐标数字、名字）。</summary>
        public static GUIStyle Small
        {
            get
            {
                if (_small == null)
                    _small = new GUIStyle(GUI.skin.label)
                    {
                        font = Font,
                        fontSize = 11,
                        richText = false,
                        normal = { textColor = new Color(0.62f, 0.70f, 0.78f) }
                    };
                return _small;
            }
        }

        /// <summary>窗口标题栏样式（标题里的中文也靠它）。</summary>
        public static GUIStyle Window
        {
            get
            {
                if (_window == null)
                    _window = new GUIStyle(GUI.skin.window) { font = Font, fontSize = 13 };
                return _window;
            }
        }

        public static GUIStyle Button
        {
            get
            {
                if (_button == null)
                    _button = new GUIStyle(GUI.skin.button) { font = Font, fontSize = 12 };
                return _button;
            }
        }

        public static GUIStyle Toggle
        {
            get
            {
                if (_toggle == null)
                    _toggle = new GUIStyle(GUI.skin.toggle) { font = Font, fontSize = 12 };
                return _toggle;
            }
        }

        /// <summary>加粗标题。</summary>
        public static GUIStyle Head
        {
            get { return _head ?? (_head = new GUIStyle(LabelRich) { fontStyle = FontStyle.Bold, fontSize = 14 }); }
        }
        private static GUIStyle _head;

        /// <summary>淡色（用于次要信息行）。</summary>
        public static GUIStyle Dim
        {
            get { return _dim ?? (_dim = new GUIStyle(LabelRich) { normal = { textColor = new Color(0.65f, 0.72f, 0.8f) } }); }
        }
        private static GUIStyle _dim;
    }
}
