using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace UchSkyTint
{
    [BepInPlugin("local.uch.skytint", "UCH Sky Tint", "1.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        private const int WindowId = 84210;
        private const float WindowWidth = 320f;
        private const int CustomSlots = 6;

        // ---------- presets ----------

        private static readonly string[] PresetNames =
            { "Pale blue", "Night", "Sunset", "Mint", "Pink", "White" };
        private static readonly string[] PresetHex =
            { "#C4CFFF", "#0B1D3A", "#FF9E6B", "#B8F0D0", "#FFC4E1", "#FFFFFF" };
        private Color[] _presetColors;

        // ---------- themes ----------

        private sealed class Theme
        {
            public Color Window, Border, Text, SubText, Accent, AccentText;
            public Color Button, ButtonHover, ButtonActive, Field, Track;
        }

        private static Color Hex(string h)
        {
            Color c;
            ColorUtility.TryParseHtmlString(h, out c);
            return c;
        }

        private static readonly Theme DarkTheme = new Theme
        {
            Window = Hex("#1E2128"), Border = Hex("#3A404C"),
            Text = Hex("#E8EAF0"), SubText = Hex("#9AA1B0"),
            Accent = Hex("#6C8CFF"), AccentText = Hex("#FFFFFF"),
            Button = Hex("#31363F"), ButtonHover = Hex("#3B414C"), ButtonActive = Hex("#454C59"),
            Field = Hex("#16191E"), Track = Hex("#3A404C")
        };

        private static readonly Theme LightTheme = new Theme
        {
            Window = Hex("#F6F7FA"), Border = Hex("#D3D8E2"),
            Text = Hex("#1F2430"), SubText = Hex("#6B7385"),
            Accent = Hex("#4A6CF7"), AccentText = Hex("#FFFFFF"),
            Button = Hex("#E7EAF1"), ButtonHover = Hex("#DCE1EB"), ButtonActive = Hex("#CDD3E0"),
            Field = Hex("#FFFFFF"), Track = Hex("#CDD3E0")
        };

        // ---------- config ----------

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<string> _hex;
        private ConfigEntry<string> _objectName;
        private ConfigEntry<float> _interval;
        private ConfigEntry<KeyCode> _menuKey;
        private ConfigEntry<bool> _ownCursor;
        private ConfigEntry<bool> _darkMode;

        // ---------- state ----------

        private Color _target = Color.white;
        private string _hexText = "#FFFFFF";
        private bool _showMenu;
        private Rect _window = new Rect(40, 40, WindowWidth, 10);
        private float _timer;
        private string _status = "";
        private float _hue, _sat, _val;

        private bool _cursorSaved;
        private bool _prevCursorVisible;
        private CursorLockMode _prevLock;

        // Remembers each sky layer's original color so it can be restored.
        private Dictionary<SpriteRenderer, Color> _originals = new Dictionary<SpriteRenderer, Color>();

        // ---------- styles ----------

        private Theme _theme = DarkTheme;
        private bool _stylesBuilt;
        private readonly List<Texture2D> _texs = new List<Texture2D>();
        private GUIStyle _sWindow, _sTitle, _sSub, _sStatus, _sLabel, _sSection, _sValue;
        private GUIStyle _sButton, _sAccent, _sField, _sSlider, _sThumb, _sSwatch, _sChipDark, _sChipLight;
        private GUIStyle _sSmall, _sEmpty;

        private readonly ConfigEntry<string>[] _custom = new ConfigEntry<string>[CustomSlots];
        private CursorOverlay _overlay;

        private void Awake()
        {
            _enabled = Config.Bind("General", "Enabled", true,
                "Turn the sky recolor on or off.");
            _hex = Config.Bind("General", "SkyColor", "#C4CFFF",
                "Sky tint as a hex color, for example #C4CFFF. The in-game menu can save to this.");
            _menuKey = Config.Bind("Menu", "MenuKey", KeyCode.F6,
                "Key that opens and closes the color menu in game.");
            _darkMode = Config.Bind("Menu", "DarkMode", true,
                "Dark theme for the menu window. The menu's Light/Dark button changes this.");
            _ownCursor = Config.Bind("Menu", "DrawOwnCursor", true,
                "Draw a crosshair cursor in the menu instead of the system cursor. Fixes the cursor flickering when the game keeps hiding it.");
            for (int i = 0; i < CustomSlots; i++)
            {
                _custom[i] = Config.Bind("Custom presets", "Slot" + (i + 1), "",
                    "Hex color saved in custom slot " + (i + 1) + ". Leave empty for an unused slot.");
            }

            _objectName = Config.Bind("Advanced", "ObjectName", "skyGradient",
                "Name of the background objects to recolor.");
            _interval = Config.Bind("Advanced", "ScanIntervalSeconds", 1f,
                "How often to look for new background objects (levels create them while running).");

            Color c;
            if (TryParse(_hex.Value, out c))
                _target = new Color(c.r, c.g, c.b, 1f);
            else
                Logger.LogWarning("Could not read SkyColor '" + _hex.Value + "', using white.");

            _hexText = ToHex(_target);

            SyncHsv(_target);

            _presetColors = new Color[PresetHex.Length];
            for (int i = 0; i < PresetHex.Length; i++)
                TryParse(PresetHex[i], out _presetColors[i]);

            _overlay = gameObject.AddComponent<CursorOverlay>();

            Logger.LogInfo("UCH Sky Tint loaded. Press " + _menuKey.Value + " in game for the color menu.");
        }

        private void OnDestroy()
        {
            DestroyTextures();
        }

        private void Update()
        {
            _timer += Time.unscaledDeltaTime;
            if (_timer < _interval.Value)
                return;
            _timer = 0f;

            if (_enabled.Value)
                ApplyToScene();
            else
                RestoreOriginals();
        }

        private void LateUpdate()
        {
            // Runs after the game's own Update, so the game can't hide or lock the cursor under us.
            if (_showMenu)
                ApplyCursorState();
        }

        // ---------- recoloring ----------

        private void ApplyToScene()
        {
            PruneDestroyed();

            var renderers = FindObjectsOfType<SpriteRenderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                var sr = renderers[i];
                if (sr == null || sr.gameObject.name != _objectName.Value)
                    continue;

                var current = sr.color;
                if (!_originals.ContainsKey(sr))
                    _originals[sr] = current;

                // Keep the layer's own transparency, only change the color.
                var wanted = new Color(_target.r, _target.g, _target.b, current.a);
                if (current != wanted)
                    sr.color = wanted;
            }
        }

        private void RestoreOriginals()
        {
            if (_originals.Count == 0)
                return;

            foreach (var pair in _originals)
            {
                if (pair.Key == null)
                    continue;
                var cur = pair.Key.color;
                pair.Key.color = new Color(pair.Value.r, pair.Value.g, pair.Value.b, cur.a);
            }
            _originals.Clear();
        }

        private void PruneDestroyed()
        {
            if (_originals.Count == 0)
                return;

            var keep = new Dictionary<SpriteRenderer, Color>();
            foreach (var pair in _originals)
            {
                if (pair.Key != null)
                    keep.Add(pair.Key, pair.Value);
            }
            _originals = keep;
        }

        private void SetTarget(Color c, bool updateText)
        {
            _target = new Color(c.r, c.g, c.b, 1f);
            SyncHsv(_target);
            if (updateText)
                _hexText = ToHex(_target);
            if (_enabled.Value)
                ApplyToScene();
        }

        private void SetSaturation(float s)
        {
            _sat = Mathf.Clamp01(s);
            _target = Color.HSVToRGB(_hue, _sat, _val);
            _hexText = ToHex(_target);
            if (_enabled.Value)
                ApplyToScene();
        }

        // Remembers the hue even when the color is gray, so the saturation slider
        // can bring the color back after it has been dragged to 0%.
        private void SyncHsv(Color c)
        {
            float h, s, v;
            Color.RGBToHSV(c, out h, out s, out v);
            if (s > 0.001f && v > 0.001f)
                _hue = h;
            _sat = s;
            _val = v;
        }

        // ---------- menu ----------

        private void OnGUI()
        {
            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.keyCode == _menuKey.Value)
            {
                _showMenu = !_showMenu;
                e.Use();

                if (!_showMenu)
                    RestoreCursor();
            }

            if (_overlay != null)
                _overlay.Show = _showMenu && _ownCursor.Value;

            if (!_showMenu)
                return;

            ShowCursor();

            if (!_stylesBuilt)
                BuildStyles();

            var settings = GUI.skin.settings;
            var oldCursor = settings.cursorColor;
            var oldSelection = settings.selectionColor;
            settings.cursorColor = _theme.Text;
            settings.selectionColor = new Color(_theme.Accent.r, _theme.Accent.g, _theme.Accent.b, 0.45f);

            _window = GUILayout.Window(WindowId, _window, DrawWindow, "", _sWindow, GUILayout.Width(WindowWidth));

            settings.cursorColor = oldCursor;
            settings.selectionColor = oldSelection;
        }

        private void DrawWindow(int id)
        {
            var prevBg = GUI.backgroundColor;

            // Header: title and theme switch
            GUILayout.BeginHorizontal();
            GUILayout.Label("Sky Tint", _sTitle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_darkMode.Value ? "Light mode" : "Dark mode", _sButton, GUILayout.Width(96f)))
            {
                _darkMode.Value = !_darkMode.Value;
                _stylesBuilt = false; // restyled at the start of the next frame
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Press " + _menuKey.Value + " to close. Drag here to move.", _sSub);
            GUILayout.Space(6f);
            Divider();
            GUILayout.Space(6f);

            // Color swatch
            var swatch = GUILayoutUtility.GetRect(10f, 40f, GUILayout.ExpandWidth(true));
            GUI.backgroundColor = _target;
            GUI.Box(swatch, GUIContent.none, _sSwatch);
            GUI.backgroundColor = prevBg;

            GUILayout.Space(6f);

            // Hex box
            GUILayout.BeginHorizontal();
            GUILayout.Label("Hex", _sLabel, GUILayout.Width(34f));
            string typed = GUILayout.TextField(_hexText, 7, _sField);
            GUILayout.EndHorizontal();
            if (typed != _hexText)
            {
                _hexText = typed;
                Color parsed;
                if (TryParse(typed, out parsed))
                    SetTarget(parsed, false);
            }

            GUILayout.Space(4f);

            // Sliders
            Color t = _target;
            bool changed = false;
            changed |= ChannelRow("R", ref t.r);
            changed |= ChannelRow("G", ref t.g);
            changed |= ChannelRow("B", ref t.b);
            if (changed)
                SetTarget(t, true);

            GUILayout.Space(4f);
            float sat = _sat;
            if (ChannelRow("Sat", ref sat, true))
                SetSaturation(sat);

            GUILayout.Space(8f);
            GUILayout.Label("PRESETS", _sSection);

            for (int i = 0; i < PresetNames.Length; i++)
            {
                if (i % 3 == 0)
                    GUILayout.BeginHorizontal();

                Color pc = _presetColors[i];
                GUI.backgroundColor = pc;
                var chip = Luminance(pc) > 0.55f ? _sChipDark : _sChipLight;
                if (GUILayout.Button(PresetNames[i], chip))
                    SetTarget(pc, true);
                GUI.backgroundColor = prevBg;

                if (i % 3 == 2 || i == PresetNames.Length - 1)
                    GUILayout.EndHorizontal();
            }

            GUILayout.Space(8f);
            GUILayout.Label("YOUR COLORS", _sSection);
            GUILayout.Label("Click to use. Save here stores the current color.", _sSub);
            GUILayout.Space(2f);

            for (int i = 0; i < CustomSlots; i++)
            {
                if (i % 3 == 0)
                    GUILayout.BeginHorizontal();

                GUILayout.BeginVertical();
                DrawCustomSlot(i, prevBg);
                GUILayout.EndVertical();

                if (i % 3 == 2)
                    GUILayout.EndHorizontal();
            }

            GUILayout.Space(8f);
            Divider();
            GUILayout.Space(8f);

            // Recolor on/off
            bool on = _enabled.Value;
            if (GUILayout.Button(on ? "Sky recolor: ON" : "Sky recolor: OFF", on ? _sAccent : _sButton))
            {
                _enabled.Value = !on;
                if (_enabled.Value) ApplyToScene();
                else RestoreOriginals();
            }

            // Saved default
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Load saved default", _sButton))
            {
                Color saved;
                if (TryParse(_hex.Value, out saved))
                {
                    SetTarget(saved, true);
                    _status = "Loaded saved default " + _hex.Value + ".";
                }
            }

            if (GUILayout.Button("Save as default", _sButton))
            {
                try
                {
                    _hex.Value = ToHex(_target);
                    Config.Save();
                    _status = "Saved " + _hex.Value + " to config.";
                    Logger.LogInfo("Saved sky color " + _hex.Value + " to " + Config.ConfigFilePath);
                }
                catch (System.Exception ex)
                {
                    _status = "Save failed: " + ex.Message;
                    Logger.LogError("Could not save config: " + ex);
                }
            }
            GUILayout.EndHorizontal();

            if (_status.Length > 0)
                GUILayout.Label(_status, _sStatus);

            GUI.backgroundColor = prevBg;
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 48f));
        }

        private void DrawCustomSlot(int index, Color prevBg)
        {
            Color c;
            bool has = TryParse(_custom[index].Value, out c);

            // Two controls per slot whether it is filled or empty, so the layout stays stable.
            if (has)
            {
                GUI.backgroundColor = c;
                var chip = Luminance(c) > 0.55f ? _sChipDark : _sChipLight;
                if (GUILayout.Button(ToHex(c), chip))
                    SetTarget(c, true);
                GUI.backgroundColor = prevBg;
            }
            else
            {
                GUILayout.Button("Empty", _sEmpty);
            }

            if (GUILayout.Button("Save here", _sSmall))
            {
                string hex = ToHex(_target);
                _custom[index].Value = hex;
                _status = "Saved " + hex + " to your colors.";
            }
        }

        private bool ChannelRow(string label, ref float value, bool percent = false)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _sLabel, GUILayout.Width(30f));
            float v = GUILayout.HorizontalSlider(value, 0f, 1f, _sSlider, _sThumb);
            string shown = percent
                ? Mathf.RoundToInt(v * 100f) + "%"
                : Mathf.RoundToInt(v * 255f).ToString();
            GUILayout.Label(shown, _sValue);
            GUILayout.EndHorizontal();

            if (Mathf.Approximately(v, value))
                return false;
            value = v;
            return true;
        }

        private void Divider()
        {
            var r = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            var prev = GUI.color;
            GUI.color = _theme.Border;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = prev;
        }

        // ---------- styling ----------

        private void BuildStyles()
        {
            DestroyTextures();
            _theme = _darkMode.Value ? DarkTheme : LightTheme;
            var t = _theme;

            // Window
            var winTex = Shape(32, 32, 16f, 16f, 12f, t.Window, t.Border, 1.5f);
            _sWindow = new GUIStyle();
            SetStates(_sWindow, winTex, winTex, winTex, winTex, t.Text);
            _sWindow.border = new RectOffset(14, 14, 14, 14);
            _sWindow.padding = new RectOffset(16, 16, 14, 16);

            // Text styles
            _sTitle = TextStyle(18, FontStyle.Bold, t.Text, TextAnchor.MiddleLeft);
            _sTitle.stretchHeight = true;
            _sSub = TextStyle(11, FontStyle.Normal, t.SubText, TextAnchor.UpperLeft);
            _sStatus = TextStyle(11, FontStyle.Normal, t.SubText, TextAnchor.UpperLeft);
            _sStatus.wordWrap = true;
            _sStatus.margin = new RectOffset(2, 2, 6, 0);
            _sLabel = TextStyle(13, FontStyle.Normal, t.Text, TextAnchor.MiddleLeft);
            _sLabel.stretchHeight = true;
            _sSection = TextStyle(11, FontStyle.Bold, t.SubText, TextAnchor.UpperLeft);
            _sValue = TextStyle(11, FontStyle.Normal, t.SubText, TextAnchor.MiddleRight);
            _sValue.fixedWidth = 32f;
            _sValue.stretchHeight = true;

            // Buttons
            var btn = Shape(24, 24, 12f, 12f, 7f, t.Button, t.Border, 1f);
            var btnHover = Shape(24, 24, 12f, 12f, 7f, t.ButtonHover, t.Border, 1f);
            var btnActive = Shape(24, 24, 12f, 12f, 7f, t.ButtonActive, t.Border, 1f);
            _sButton = ButtonStyle(btn, btnHover, btnActive, t.Text);

            var acc = Shape(24, 24, 12f, 12f, 7f, t.Accent, t.Accent, 1f);
            var accHover = Shape(24, 24, 12f, 12f, 7f, Color.Lerp(t.Accent, Color.white, 0.18f), t.Accent, 1f);
            var accActive = Shape(24, 24, 12f, 12f, 7f, Color.Lerp(t.Accent, Color.black, 0.18f), t.Accent, 1f);
            _sAccent = ButtonStyle(acc, accHover, accActive, t.AccentText);

            // Preset chips: white backgrounds that get tinted with GUI.backgroundColor
            var chip = Shape(24, 24, 12f, 12f, 7f, Color.white, new Color(0.78f, 0.78f, 0.78f, 1f), 1f);
            var chipHover = Shape(24, 24, 12f, 12f, 7f, new Color(0.9f, 0.9f, 0.9f, 1f), new Color(0.7f, 0.7f, 0.7f, 1f), 1f);
            var chipActive = Shape(24, 24, 12f, 12f, 7f, new Color(0.78f, 0.78f, 0.78f, 1f), new Color(0.6f, 0.6f, 0.6f, 1f), 1f);
            _sChipDark = ButtonStyle(chip, chipHover, chipActive, Hex("#14171C"));
            _sChipLight = ButtonStyle(chip, chipHover, chipActive, Color.white);

            // Custom slot buttons
            _sSmall = ButtonStyle(btn, btnHover, btnActive, t.Text);
            _sSmall.fontSize = 11;
            _sSmall.padding = new RectOffset(6, 6, 3, 3);
            _sEmpty = ButtonStyle(btn, btn, btn, t.SubText);

            // Swatch
            var swatchTex = Shape(24, 24, 12f, 12f, 8f, Color.white, new Color(0.8f, 0.8f, 0.8f, 1f), 1f);
            _sSwatch = new GUIStyle();
            SetStates(_sSwatch, swatchTex, swatchTex, swatchTex, swatchTex, t.Text);
            _sSwatch.border = new RectOffset(10, 10, 10, 10);

            // Text field
            var field = Shape(24, 24, 12f, 12f, 7f, t.Field, t.Border, 1f);
            var fieldFocus = Shape(24, 24, 12f, 12f, 7f, t.Field, t.Accent, 1.5f);
            _sField = new GUIStyle(GUI.skin.textField);
            SetStates(_sField, field, field, field, fieldFocus, t.Text);
            _sField.border = new RectOffset(8, 8, 8, 8);
            _sField.padding = new RectOffset(10, 10, 6, 6);
            _sField.margin = new RectOffset(3, 3, 3, 3);
            _sField.fontSize = 14;
            _sField.alignment = TextAnchor.MiddleLeft;

            // Sliders
            var track = Shape(24, 16, 12f, 3f, 3f, t.Track, t.Track, 0f);
            _sSlider = new GUIStyle();
            _sSlider.normal.background = track;
            _sSlider.border = new RectOffset(8, 8, 0, 0);
            _sSlider.fixedHeight = 16f;
            _sSlider.margin = new RectOffset(4, 4, 6, 6);

            var thumb = Shape(16, 16, 8f, 8f, 8f, t.Accent, t.Window, 2.5f);
            _sThumb = new GUIStyle();
            _sThumb.normal.background = thumb;
            _sThumb.hover.background = thumb;
            _sThumb.active.background = thumb;
            _sThumb.fixedWidth = 16f;
            _sThumb.fixedHeight = 16f;

            _stylesBuilt = true;
        }

        private static GUIStyle TextStyle(int size, FontStyle fontStyle, Color color, TextAnchor anchor)
        {
            var s = new GUIStyle(GUI.skin.label);
            s.fontSize = size;
            s.fontStyle = fontStyle;
            s.alignment = anchor;
            s.wordWrap = false;
            s.normal.textColor = color;
            s.hover.textColor = color;
            s.active.textColor = color;
            return s;
        }

        private static GUIStyle ButtonStyle(Texture2D normal, Texture2D hover, Texture2D active, Color text)
        {
            var s = new GUIStyle(GUI.skin.button);
            SetStates(s, normal, hover, active, normal, text);
            s.border = new RectOffset(8, 8, 8, 8);
            s.padding = new RectOffset(10, 10, 7, 7);
            s.margin = new RectOffset(3, 3, 3, 3);
            s.fontSize = 13;
            s.fontStyle = FontStyle.Normal;
            s.alignment = TextAnchor.MiddleCenter;
            return s;
        }

        private static void SetStates(GUIStyle s, Texture2D normal, Texture2D hover, Texture2D active,
            Texture2D focused, Color text)
        {
            s.normal.background = normal;     s.normal.textColor = text;
            s.hover.background = hover;       s.hover.textColor = text;
            s.active.background = active;     s.active.textColor = text;
            s.focused.background = focused;   s.focused.textColor = text;
            s.onNormal.background = normal;   s.onNormal.textColor = text;
            s.onHover.background = hover;     s.onHover.textColor = text;
            s.onActive.background = active;   s.onActive.textColor = text;
            s.onFocused.background = focused; s.onFocused.textColor = text;
        }

        // Builds a small anti-aliased rounded rectangle (with an optional outline) that
        // the menu stretches with 9-slice borders.
        private Texture2D Shape(int w, int h, float halfW, float halfH, float radius,
            Color fill, Color border, float borderWidth)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            var px = new Color[w * h];
            float cx0 = w * 0.5f;
            float cy0 = h * 0.5f;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float qx = Mathf.Abs(x + 0.5f - cx0) - (halfW - radius);
                    float qy = Mathf.Abs(y + 0.5f - cy0) - (halfH - radius);
                    float ox = Mathf.Max(qx, 0f);
                    float oy = Mathf.Max(qy, 0f);
                    float sd = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;

                    float alpha = Mathf.Clamp01(0.5f - sd);
                    float edge = Mathf.Clamp01(sd + borderWidth + 0.5f);

                    Color c = Color.Lerp(fill, border, edge);
                    c.a = alpha * Mathf.Lerp(fill.a, border.a, edge);
                    px[y * w + x] = c;
                }
            }

            tex.SetPixels(px);
            tex.Apply();
            _texs.Add(tex);
            return tex;
        }

        private void DestroyTextures()
        {
            for (int i = 0; i < _texs.Count; i++)
            {
                if (_texs[i] != null)
                    Destroy(_texs[i]);
            }
            _texs.Clear();
        }

        private static float Luminance(Color c)
        {
            return 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
        }

        // ---------- cursor ----------

        private void ShowCursor()
        {
            if (!_cursorSaved)
            {
                _prevCursorVisible = Cursor.visible;
                _prevLock = Cursor.lockState;
                _cursorSaved = true;
            }
            ApplyCursorState();
        }

        private void ApplyCursorState()
        {
            // With our own cursor drawn, hide the system one so it can't flicker.
            Cursor.visible = !_ownCursor.Value;
            Cursor.lockState = CursorLockMode.None;
        }

        internal static void DrawCrosshair()
        {
            var e = Event.current;
            if (e == null)
                return;

            Vector2 m = e.mousePosition;
            var prev = GUI.color;

            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(m.x - 2f, m.y - 12f, 4f, 24f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(m.x - 12f, m.y - 2f, 24f, 4f), Texture2D.whiteTexture);

            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(m.x - 1f, m.y - 10f, 2f, 20f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(m.x - 10f, m.y - 1f, 20f, 2f), Texture2D.whiteTexture);

            GUI.color = prev;
        }

        private void RestoreCursor()
        {
            if (!_cursorSaved)
                return;
            Cursor.visible = _prevCursorVisible;
            Cursor.lockState = _prevLock;
            _cursorSaved = false;
        }

        // ---------- helpers ----------

        private static string ToHex(Color c)
        {
            return "#" + ColorUtility.ToHtmlStringRGB(c);
        }

        private static bool TryParse(string text, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(text))
                return false;
            if (!text.StartsWith("#"))
                text = "#" + text;
            if (text.Length != 7)
                return false;
            return ColorUtility.TryParseHtmlString(text, out color);
        }
    }

    // Draws the crosshair from its own script with a very low GUI depth, so it is
    // painted after (on top of) the menu window.
    public sealed class CursorOverlay : MonoBehaviour
    {
        public bool Show;

        private void OnGUI()
        {
            if (!Show)
                return;

            GUI.depth = -10000;
            Plugin.DrawCrosshair();
        }
    }
}
