using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace UchSkyTint
{
    [BepInPlugin("local.uch.skytint", "UCH Sky Tint", "1.7.2")]
    public class Plugin : BaseUnityPlugin
    {
        private const int WindowId = 84210;
        private const float WindowWidth = 320f;
        private const int CustomSlots = 6;

        private sealed class FlatLayer
        {
            public SpriteRenderer Source;
            public GameObject Quad;
            public SpriteRenderer Renderer;

            // Where the original layer was, used to place extra stars.
            public Bounds Area;
            public float Z;
            public int Seed;
            public bool AllowStars;

            public GameObject StarsRoot;
            public int BuiltCopies = -1;
            public float BuiltOffset = -1f;
            public bool BuiltWithTemplate;
        }

        // A copy of one of the game's own star objects, kept so it can be reused on other backgrounds.
        private sealed class StarTemplate
        {
            public GameObject Prefab;
            public Vector2 Norm;       // position inside the sky area, -0.5 to 0.5
            public Vector3 WorldScale;
        }

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
        private ConfigEntry<float> _interval;
        private ConfigEntry<KeyCode> _menuKey;
        private ConfigEntry<bool> _ownCursor;
        private ConfigEntry<bool> _darkMode;
        private ConfigEntry<bool> _cfgHideStars;
        private ConfigEntry<bool> _cfgHideClouds;
        private ConfigEntry<bool> _cfgAddStars;
        private ConfigEntry<int> _cfgStarCopies;
        private ConfigEntry<float> _cfgStarOffset;
        private ConfigEntry<string> _cfgStarPrefab;
        private ConfigEntry<string> _cfgBgImage;
        private ConfigEntry<float> _cfgBgOpacity;
        private ConfigEntry<bool> _cfgBgFollow;
        private ConfigEntry<string> _cfgBgFit;
        private ConfigEntry<float> _cfgBgZoom;
        private ConfigEntry<float> _cfgBgOffX;
        private ConfigEntry<float> _cfgBgOffY;
        private ConfigEntry<int> _cfgBgOrder;
        private ConfigEntry<string> _cfgSkyNames;
        private ConfigEntry<string> _cfgPaperName;
        private ConfigEntry<string> _cfgPaperPrefix;
        private readonly ConfigEntry<string>[] _custom = new ConfigEntry<string>[CustomSlots];

        private string[] _skyNames = new string[0];

        // ---------- state ----------

        private Color _target = Color.white;
        private string _hexText = "#FFFFFF";
        private float _hue, _sat, _val;

        private bool _hideStars, _hideClouds;
        private bool _addStars;
        private int _starCopies = 2;
        private float _starOffset = 0.3f;
        private readonly List<StarTemplate> _template = new List<StarTemplate>();
        private GameObject _templateRoot;
        private int _scanCount, _prefabAttempts;

        // Custom background image drawn over every sky layer.
        private sealed class OverlayLayer
        {
            public SpriteRenderer Source;
            public GameObject Object;
            public SpriteRenderer Renderer;
        }

        private Dictionary<int, OverlayLayer> _overlays = new Dictionary<int, OverlayLayer>();
        private string _bgFolder = "";
        private string[] _bgFiles = new string[0];
        private string _bgName = "";
        private Texture2D _bgTexture;
        private Sprite _bgSprite;
        private float _bgOpacity = 1f;
        private bool _bgFollow;
        private BgFit _bgFit = BgFit.Cover;
        private float _bgZoom = 1f;
        private float _bgOffX, _bgOffY;

        private bool _showMenu;
        private bool _extrasOpen;
        private Rect _window = new Rect(40, 40, WindowWidth, 10);
        private float _timer;
        private string _status = "";

        private bool _cursorSaved;
        private bool _prevCursorVisible;
        private CursorLockMode _prevLock;
        private CursorOverlay _overlay;

        // Sky/paper layers we replaced with a flat color, and things we hid.
        private Dictionary<int, FlatLayer> _flat = new Dictionary<int, FlatLayer>();
        private readonly List<GameObject> _hiddenStars = new List<GameObject>();
        private readonly List<GameObject> _hiddenClouds = new List<GameObject>();
        private Sprite _white;
        private Sprite _dot;

        // ---------- styles ----------

        private Theme _theme = DarkTheme;
        private bool _stylesBuilt;
        private readonly List<Texture2D> _texs = new List<Texture2D>();
        private GUIStyle _sWindow, _sTitle, _sSub, _sStatus, _sLabel, _sSection, _sValue;
        private GUIStyle _sButton, _sAccent, _sField, _sSlider, _sThumb, _sSwatch, _sChipDark, _sChipLight;
        private GUIStyle _sSmall, _sEmpty;

        private void Awake()
        {
            _enabled = Config.Bind("General", "Enabled", true,
                "Turn the sky recolor on or off.");
            _hex = Config.Bind("General", "SkyColor", "#C4CFFF",
                "Sky color as a hex code, for example #C4CFFF. The in-game menu can save to this.");
            _menuKey = Config.Bind("Menu", "MenuKey", KeyCode.F6,
                "Key that opens and closes the color menu in game.");
            _darkMode = Config.Bind("Menu", "DarkMode", true,
                "Dark theme for the menu window. The menu's Light/Dark button changes this.");
            _ownCursor = Config.Bind("Menu", "DrawOwnCursor", true,
                "Draw a crosshair cursor in the menu instead of the system cursor.");

            for (int i = 0; i < CustomSlots; i++)
            {
                _custom[i] = Config.Bind("Custom presets", "Slot" + (i + 1), "",
                    "Hex color saved in custom slot " + (i + 1) + ". Leave empty for an unused slot.");
            }

            _cfgHideStars = Config.Bind("Extras", "HideStars", false,
                "Hide the stars in backgrounds that already have them.");
            _cfgHideClouds = Config.Bind("Extras", "HideClouds", false,
                "Hide the clouds in backgrounds that have them.");
            _cfgAddStars = Config.Bind("Extras", "AddStars", false,
                "Add extra stars to the sky of any background (needs Enabled = true).");
            _cfgStarCopies = Config.Bind("Extras", "StarCopies", 2,
                "How many extra copies of the game's own star layer to add (0 to 8).");
            _cfgStarOffset = Config.Bind("Extras", "StarOffset", 0.3f,
                "How far each copy is shifted, from 0 (barely) to 1 (a lot).");

            _cfgBgImage = Config.Bind("Background image", "Image", "",
                "File name of the picture (in the UchSkyTint Backgrounds folder) to draw over the sky. Empty for none.");
            _cfgBgOpacity = Config.Bind("Background image", "Opacity", 1f,
                "How solid the picture is, from 0 (invisible) to 1 (fully solid).");
            _cfgBgFollow = Config.Bind("Background image", "FollowCamera", false,
                "false: the picture stays in one place in the level. true: it moves with the camera.");
            _cfgBgFit = Config.Bind("Background image", "Fit", "Cover",
                "Cover: fills the screen and keeps its shape (edges may be cropped). Contain: shows the whole picture (may leave gaps). Stretch: fills the screen and squashes the picture.");
            _cfgBgZoom = Config.Bind("Background image", "Zoom", 1f,
                "Size of the picture compared to its fitted size, from 0.25 (small) to 3 (big).");
            _cfgBgOffX = Config.Bind("Background image", "OffsetX", 0f,
                "Move the picture sideways, from -1 (left edge) to 1 (right edge).");
            _cfgBgOffY = Config.Bind("Background image", "OffsetY", 0f,
                "Move the picture up or down, from -1 (bottom edge) to 1 (top edge).");
            _cfgBgOrder = Config.Bind("Background image", "SortingOffset", 0,
                "Move the picture in front of (higher) or behind (lower) other layers if it covers the game or sits behind something.");
            _cfgStarPrefab = Config.Bind("Advanced", "StarSourcePrefab", "NightSky",
                "Name of the game's background prefab whose stars get copied for the extra stars.");
            _cfgSkyNames = Config.Bind("Advanced", "SkyLayerNames", "skyGradient",
                "Comma-separated names of sky layers to recolor. Add more here for backgrounds that do not change.");
            _cfgPaperName = Config.Bind("Advanced", "PaperLayerName", "background",
                "Name of the paper background layer.");
            _cfgPaperPrefix = Config.Bind("Advanced", "PaperParentPrefix", "Paper",
                "The paper layer is only recolored when its parent's name starts with this.");
            _interval = Config.Bind("Advanced", "ScanInterval", 0.5f,
                "How often (seconds) to look for new background objects. Levels create them while running.");

            _skyNames = SplitNames(_cfgSkyNames.Value);

            Color c;
            if (TryParse(_hex.Value, out c))
                _target = new Color(c.r, c.g, c.b, 1f);
            else
                Logger.LogWarning("Could not read SkyColor '" + _hex.Value + "', using white.");

            _hexText = ToHex(_target);
            SyncHsv(_target);
            LoadExtrasFromConfig();

            _bgFolder = Path.Combine(Paths.ConfigPath, "UchSkyTint Backgrounds");
            try
            {
                Directory.CreateDirectory(_bgFolder);
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning("Could not create the backgrounds folder: " + ex.Message);
            }
            RefreshBackgroundList();
            SelectBackground(_cfgBgImage.Value);

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

            ApplyToScene();
        }

        private void LateUpdate()
        {
            // Runs after the game's own Update, so the game can't hide or lock the cursor under us.
            if (_showMenu)
                ApplyCursorState();
        }

        // ---------- extras settings ----------

        private void LoadExtrasFromConfig()
        {
            _hideStars = _cfgHideStars.Value;
            _hideClouds = _cfgHideClouds.Value;
            _addStars = _cfgAddStars.Value;
            _starCopies = Mathf.Clamp(_cfgStarCopies.Value, 0, 8);
            _starOffset = Mathf.Clamp01(_cfgStarOffset.Value);
            _bgOpacity = Mathf.Clamp01(_cfgBgOpacity.Value);
            _bgFollow = _cfgBgFollow.Value;
            _bgFit = ParseFit(_cfgBgFit.Value);
            _bgZoom = Mathf.Clamp(_cfgBgZoom.Value, 0.25f, 3f);
            _bgOffX = Mathf.Clamp(_cfgBgOffX.Value, -1f, 1f);
            _bgOffY = Mathf.Clamp(_cfgBgOffY.Value, -1f, 1f);
        }

        private void SaveExtrasToConfig()
        {
            _cfgHideStars.Value = _hideStars;
            _cfgHideClouds.Value = _hideClouds;
            _cfgAddStars.Value = _addStars;
            _cfgStarCopies.Value = _starCopies;
            _cfgStarOffset.Value = _starOffset;
            _cfgBgImage.Value = _bgName;
            _cfgBgOpacity.Value = _bgOpacity;
            _cfgBgFollow.Value = _bgFollow;
            _cfgBgFit.Value = _bgFit.ToString();
            _cfgBgZoom.Value = _bgZoom;
            _cfgBgOffX.Value = _bgOffX;
            _cfgBgOffY.Value = _bgOffY;
        }

        private void ReloadExtras()
        {
            ShowAll(_hiddenStars);
            ShowAll(_hiddenClouds);
            LoadExtrasFromConfig();
            SelectBackground(_cfgBgImage.Value);
        }

        // ---------- scene changes ----------

        private void ApplyToScene()
        {
            PruneDestroyed();

            // If extra stars are on but no stars have been seen yet, look for the game's prefab.
            _scanCount++;
            if (_addStars && _template.Count == 0 && _prefabAttempts < 12 && _scanCount % 6 == 0)
            {
                _prefabAttempts++;
                TryCaptureFromPrefabs();
            }

            if (!_enabled.Value)
                RemoveFlatLayers();

            if (_bgSprite == null)
                RemoveOverlays();

            bool needScan = _enabled.Value || _hideStars || _hideClouds || _bgSprite != null;
            if (!needScan)
                return;

            var all = FindObjectsOfType<Transform>();
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null)
                    continue;

                string n = t.name;

                if (_enabled.Value || _bgSprite != null)
                {
                    int kind = SkyKind(t, n);
                    if (kind != 0)
                    {
                        if (_enabled.Value)
                            ApplyFlat(t, kind == 1);
                        if (_bgSprite != null)
                            ApplyOverlay(t);
                        continue;
                    }
                }

                if (_hideStars && (n == "Stars" || n.StartsWith("Stars (", System.StringComparison.Ordinal)))
                {
                    Hide(t.gameObject, _hiddenStars);
                    continue;
                }

                if (_hideClouds && n == "Cloud_Controller")
                    Hide(t.gameObject, _hiddenClouds);
            }
        }

        // 0 = not a sky layer, 1 = sky layer, 2 = paper background layer.
        private int SkyKind(Transform t, string n)
        {
            for (int i = 0; i < _skyNames.Length; i++)
            {
                if (n == _skyNames[i])
                    return 1;
            }

            if (n == _cfgPaperName.Value && t.parent != null &&
                t.parent.name.StartsWith(_cfgPaperPrefix.Value, System.StringComparison.Ordinal))
                return 2;

            return 0;
        }

        // Replaces a sky/paper layer with a plain color layer of the same size, so the color
        // shows exactly as picked instead of being multiplied with the layer's own picture.
        private void ApplyFlat(Transform t, bool allowStars)
        {
            var src = t.GetComponent<SpriteRenderer>();
            if (src == null)
                return;

            int id = src.GetInstanceID();
            FlatLayer layer;
            if (!_flat.TryGetValue(id, out layer) || layer.Quad == null)
            {
                if (src.sprite == null)
                    return;

                Bounds b = src.bounds;
                var ls = src.transform.lossyScale;

                var go = new GameObject("UchSkyTint_Flat");
                go.layer = src.gameObject.layer;
                go.transform.SetParent(src.transform, false);
                go.transform.position = new Vector3(b.center.x, b.center.y, src.transform.position.z);
                go.transform.localScale = new Vector3(SafeDiv(b.size.x, ls.x), SafeDiv(b.size.y, ls.y), 1f);

                var q = go.AddComponent<SpriteRenderer>();
                q.sprite = WhiteSprite();
                q.sortingLayerID = src.sortingLayerID;
                q.sortingOrder = src.sortingOrder;

                layer = new FlatLayer
                {
                    Source = src,
                    Quad = go,
                    Renderer = q,
                    Area = b,
                    Z = src.transform.position.z,
                    Seed = id,
                    AllowStars = allowStars
                };
                _flat[id] = layer;
            }

            layer.Renderer.color = new Color(_target.r, _target.g, _target.b, 1f);

            if (src.enabled)
                src.enabled = false;

            SyncStars(layer);
        }

        // ---------- custom background image ----------

        private void RefreshBackgroundList()
        {
            var list = new List<string>();
            try
            {
                if (Directory.Exists(_bgFolder))
                {
                    foreach (var file in Directory.GetFiles(_bgFolder))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                            list.Add(Path.GetFileName(file));
                    }
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning("Could not read the backgrounds folder: " + ex.Message);
            }

            list.Sort();
            _bgFiles = list.ToArray();
        }

        private void SelectBackground(string fileName)
        {
            RemoveOverlays();

            if (_bgSprite != null)
                Destroy(_bgSprite);
            if (_bgTexture != null)
                Destroy(_bgTexture);
            _bgSprite = null;
            _bgTexture = null;
            _bgName = "";

            if (!string.IsNullOrEmpty(fileName))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(Path.Combine(_bgFolder, fileName));
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!tex.LoadImage(bytes))
                    {
                        Destroy(tex);
                        throw new System.Exception("not a readable PNG or JPG");
                    }

                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.filterMode = FilterMode.Bilinear;
                    tex.hideFlags = HideFlags.HideAndDontSave;

                    _bgTexture = tex;
                    _bgSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                        new Vector2(0.5f, 0.5f), tex.width, 0, SpriteMeshType.FullRect);
                    _bgSprite.hideFlags = HideFlags.HideAndDontSave;
                    _bgName = fileName;
                }
                catch (System.Exception ex)
                {
                    _status = "Could not load " + fileName + ": " + ex.Message;
                    Logger.LogError("Could not load background image " + fileName + ": " + ex);
                }
            }

            ApplyToScene();
        }

        // Puts the picture on top of every layer of one background. The picture is sized to what
        // the camera shows (not to the sky layer, which is far bigger than the visible area) and
        // an OverlayFollower keeps it matched as the camera moves or zooms.
        private void ApplyOverlay(Transform t)
        {
            var src = t.GetComponent<SpriteRenderer>();
            if (src == null || _bgSprite == null || _bgTexture == null)
                return;

            int id = src.GetInstanceID();
            OverlayLayer layer;
            if (!_overlays.TryGetValue(id, out layer) || layer.Object == null)
            {
                var go = new GameObject("UchSkyTint_Overlay");
                go.layer = src.gameObject.layer;
                go.transform.SetParent(src.transform, false);

                var r = go.AddComponent<SpriteRenderer>();
                r.sprite = _bgSprite;
                r.sortingLayerID = src.sortingLayerID;
                r.sortingOrder = TopSortingOrder(src) + _cfgBgOrder.Value;

                // The sprite is one world unit wide, so its height is the picture's aspect ratio.
                var follow = go.AddComponent<OverlayFollower>();
                follow.SpriteAspect = (float)_bgTexture.height / _bgTexture.width;
                follow.Follow = _bgFollow;
                follow.Mode = _bgFit;
                follow.Zoom = _bgZoom;
                follow.OffsetX = _bgOffX;
                follow.OffsetY = _bgOffY;
                follow.Z = src.transform.position.z;
                follow.Refresh();

                layer = new OverlayLayer { Source = src, Object = go, Renderer = r };
                _overlays[id] = layer;
            }

            layer.Renderer.color = new Color(1f, 1f, 1f, _bgOpacity);
        }

        // Sends the current fit, zoom and position to every picture that is already on screen.
        private void PushOverlaySettings()
        {
            foreach (var pair in _overlays)
            {
                if (pair.Value.Object == null)
                    continue;

                var f = pair.Value.Object.GetComponent<OverlayFollower>();
                if (f == null)
                    continue;

                f.Follow = _bgFollow;
                f.Mode = _bgFit;
                f.Zoom = _bgZoom;
                f.OffsetX = _bgOffX;
                f.OffsetY = _bgOffY;
                f.Refresh();
            }
        }

        private static BgFit ParseFit(string text)
        {
            string s = (text ?? "").Trim().ToLowerInvariant();
            if (s == "stretch")
                return BgFit.Stretch;
            if (s == "contain")
                return BgFit.Contain;
            return BgFit.Cover;
        }

        private static string FitLabel(BgFit fit)
        {
            switch (fit)
            {
                case BgFit.Stretch:
                    return "Fit: stretch (fills, squashes)";
                case BgFit.Contain:
                    return "Fit: whole picture (may leave gaps)";
                default:
                    return "Fit: fill (keeps shape, may crop)";
            }
        }

        // One above the highest sorting order used by this background's own layers.
        private static int TopSortingOrder(SpriteRenderer src)
        {
            int top = src.sortingOrder;
            var root = src.transform.parent != null ? src.transform.parent : src.transform;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || r.sortingLayerID != src.sortingLayerID)
                    continue;
                if (r.gameObject.name == "UchSkyTint_Overlay")
                    continue;
                if (r.sortingOrder > top)
                    top = r.sortingOrder;
            }
            return top + 1;
        }

        private void RemoveOverlays()
        {
            if (_overlays.Count == 0)
                return;

            foreach (var pair in _overlays)
            {
                if (pair.Value.Object != null)
                    Destroy(pair.Value.Object);
            }
            _overlays.Clear();
        }

        private static string Shorten(string text, int max)
        {
            if (text.Length <= max)
                return text;
            return text.Substring(0, max - 3) + "...";
        }

        private static float SafeDiv(float a, float b)
        {
            if (Mathf.Abs(b) < 0.0001f)
                return Mathf.Abs(a);
            return Mathf.Abs(a / b);
        }

        private Sprite WhiteSprite()
        {
            if (_white != null)
                return _white;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
            tex.Apply();

            _white = Sprite.Create(tex, new Rect(0f, 0f, 2f, 2f), new Vector2(0.5f, 0.5f), 2f);
            _white.hideFlags = HideFlags.HideAndDontSave;
            return _white;
        }

        // A soft round dot, one world unit wide, used for the extra stars.
        private Sprite DotSprite()
        {
            if (_dot != null)
                return _dot;

            const int n = 16;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            var px = new Color[n * n];
            float r = n * 0.5f;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - r;
                    float dy = y + 0.5f - r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01((r - d) / 2.5f);
                    px[y * n + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels(px);
            tex.Apply();

            _dot = Sprite.Create(tex, new Rect(0f, 0f, n, n), new Vector2(0.5f, 0.5f), n);
            _dot.hideFlags = HideFlags.HideAndDontSave;
            return _dot;
        }

        // ---------- extra stars ----------

        private void SyncStars(FlatLayer layer)
        {
            // Remember the game's own stars the first time we see them, so they can be copied.
            if (layer.AllowStars && layer.Source != null)
                CaptureStarTemplate(layer);

            bool want = _addStars && layer.AllowStars && _starCopies > 0 && layer.Source != null;

            if (!want)
            {
                if (layer.StarsRoot != null)
                    Destroy(layer.StarsRoot);
                layer.StarsRoot = null;
                layer.BuiltCopies = -1;
                return;
            }

            bool hasTemplate = _template.Count > 0;
            if (layer.StarsRoot != null && layer.BuiltCopies == _starCopies &&
                Mathf.Approximately(layer.BuiltOffset, _starOffset) && layer.BuiltWithTemplate == hasTemplate)
                return;

            if (layer.StarsRoot != null)
                Destroy(layer.StarsRoot);

            var src = layer.Source;
            var root = new GameObject("UchSkyTint_Stars");
            root.layer = src.gameObject.layer;

            // Parent the copies next to the game's own star objects (under the background's
            // art root), so they behave like the originals instead of following the sky layer.
            root.transform.SetParent(src.transform.parent != null ? src.transform.parent : src.transform, false);

            if (hasTemplate)
            {
                BuildTemplateStars(layer, root);
                layer.StarsRoot = root;
                layer.BuiltCopies = _starCopies;
                layer.BuiltOffset = _starOffset;
                layer.BuiltWithTemplate = true;
                return;
            }

            // No game stars to copy yet: fall back to simple round dots.
            int dotCount = _starCopies * 50;
            var ls = root.transform.lossyScale;
            float worldSize = layer.Area.size.y * Mathf.Lerp(0.003f, 0.02f, 0.3f);
            var rng = new System.Random(layer.Seed);
            var dot = DotSprite();

            for (int i = 0; i < dotCount; i++)
            {
                var star = new GameObject("Star");
                star.layer = root.layer;
                star.transform.SetParent(root.transform, false);

                float px = layer.Area.center.x + ((float)rng.NextDouble() - 0.5f) * layer.Area.size.x;
                float py = layer.Area.center.y + ((float)rng.NextDouble() - 0.5f) * layer.Area.size.y;
                star.transform.position = new Vector3(px, py, layer.Z);

                float s = worldSize * (0.5f + (float)rng.NextDouble());
                star.transform.localScale = new Vector3(SafeDiv(s, ls.x), SafeDiv(s, ls.y), 1f);

                var sr = star.AddComponent<SpriteRenderer>();
                sr.sprite = dot;
                sr.sortingLayerID = src.sortingLayerID;
                sr.sortingOrder = src.sortingOrder + 1;
                sr.color = new Color(1f, 1f, 1f, 0.55f + 0.45f * (float)rng.NextDouble());
            }

            layer.StarsRoot = root;
            layer.BuiltCopies = _starCopies;
            layer.BuiltOffset = _starOffset;
            layer.BuiltWithTemplate = false;
        }

        // Keeps a hidden copy of each of the game's star objects (the ones named "Stars",
        // "Stars (1)" and so on) so they can be reused, even on backgrounds without stars.
        private void CaptureStarTemplate(FlatLayer layer)
        {
            if (_template.Count > 0 || layer.Source == null)
                return;

            CaptureStarsFrom(layer.Source.transform.parent);
        }

        // Finds the sky layer and the star objects under a background's art root (a live
        // background or the game's own prefab) and keeps a hidden copy of each star object.
        private void CaptureStarsFrom(Transform parent)
        {
            if (parent == null || _template.Count > 0)
                return;

            SpriteRenderer sky = null;
            for (int j = 0; j < parent.childCount && sky == null; j++)
            {
                var candidate = parent.GetChild(j);
                for (int k = 0; k < _skyNames.Length; k++)
                {
                    if (candidate.name == _skyNames[k])
                    {
                        sky = candidate.GetComponent<SpriteRenderer>();
                        break;
                    }
                }
            }

            Vector2 areaCenter, areaSize;
            if (sky == null || !SpriteArea(sky, out areaCenter, out areaSize))
                return;
            if (areaSize.x < 0.0001f || areaSize.y < 0.0001f)
                return;

            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                string n = child.name;
                if (!(n == "Stars" || n.StartsWith("Stars (", System.StringComparison.Ordinal)))
                    continue;

                if (_templateRoot == null)
                {
                    _templateRoot = new GameObject("UchSkyTint_StarTemplates");
                    _templateRoot.SetActive(false);
                    DontDestroyOnLoad(_templateRoot);
                }

                var copy = Instantiate(child.gameObject, _templateRoot.transform);
                copy.name = n;
                copy.SetActive(false);

                Vector3 p = child.position;
                _template.Add(new StarTemplate
                {
                    Prefab = copy,
                    Norm = new Vector2((p.x - areaCenter.x) / areaSize.x, (p.y - areaCenter.y) / areaSize.y),
                    WorldScale = child.lossyScale
                });
            }
        }

        // Duplicates the game's star layer, each copy shifted a little from the original spot.
        private void BuildTemplateStars(FlatLayer layer, GameObject root)
        {
            var rng = new System.Random(layer.Seed);
            float maxShift = layer.Area.size.y * Mathf.Lerp(0.005f, 0.08f, _starOffset);
            var rootScale = root.transform.lossyScale;

            for (int copyIndex = 0; copyIndex < _starCopies; copyIndex++)
            {
                for (int i = 0; i < _template.Count; i++)
                {
                    var entry = _template[i];
                    if (entry.Prefab == null)
                        continue;

                    var clone = Instantiate(entry.Prefab, root.transform);
                    clone.name = "StarCopy";

                    float ox = ((float)rng.NextDouble() * 2f - 1f) * maxShift;
                    float oy = ((float)rng.NextDouble() * 2f - 1f) * maxShift;

                    clone.transform.position = new Vector3(
                        layer.Area.center.x + entry.Norm.x * layer.Area.size.x + ox,
                        layer.Area.center.y + entry.Norm.y * layer.Area.size.y + oy,
                        layer.Z);
                    clone.transform.localScale = new Vector3(
                        SafeRatio(entry.WorldScale.x, rootScale.x),
                        SafeRatio(entry.WorldScale.y, rootScale.y),
                        SafeRatio(entry.WorldScale.z, rootScale.z));

                    clone.SetActive(true);
                }
            }
        }

        private static float SafeRatio(float a, float b)
        {
            if (Mathf.Abs(b) < 0.0001f)
                return a;
            return a / b;
        }

        // Size and center of a sky layer from its sprite, which also works on inactive objects
        // and on the game's prefabs that are not in a level yet.
        private static bool SpriteArea(SpriteRenderer sr, out Vector2 center, out Vector2 size)
        {
            center = Vector2.zero;
            size = Vector2.zero;

            var sp = sr.sprite;
            if (sp == null)
                return false;

            var tr = sr.transform;
            Vector3 ls = tr.lossyScale;

            if (sr.drawMode == SpriteDrawMode.Simple)
            {
                Vector3 c = tr.TransformPoint(sp.bounds.center);
                center = new Vector2(c.x, c.y);
                size = new Vector2(Mathf.Abs(sp.bounds.size.x * ls.x), Mathf.Abs(sp.bounds.size.y * ls.y));
            }
            else
            {
                center = new Vector2(tr.position.x, tr.position.y);
                size = new Vector2(Mathf.Abs(sr.size.x * ls.x), Mathf.Abs(sr.size.y * ls.y));
            }
            return true;
        }

        // Looks for the game's own background prefab in memory so its stars can be copied
        // before any level with stars has been opened.
        private void TryCaptureFromPrefabs()
        {
            if (_template.Count > 0)
                return;

            string prefabName = _cfgStarPrefab.Value;
            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < all.Length; i++)
            {
                var go = all[i];
                if (go == null || go.scene.IsValid() || go.name != prefabName)
                    continue;

                CaptureStarsFrom(go.transform);
                if (_template.Count > 0)
                {
                    Logger.LogInfo("Copied the game's stars from the " + prefabName + " prefab.");
                    return;
                }
            }
        }

        private void RefreshStars()
        {
            if (_addStars && _template.Count == 0)
                TryCaptureFromPrefabs();

            if (_flat.Count == 0)
            {
                ApplyToScene();
                return;
            }

            foreach (var pair in _flat)
                SyncStars(pair.Value);
        }

        // ---------- cleanup ----------

        private void UpdateFlatColors()
        {
            var color = new Color(_target.r, _target.g, _target.b, 1f);
            foreach (var pair in _flat)
            {
                if (pair.Value.Renderer != null)
                    pair.Value.Renderer.color = color;
            }
        }

        private void RemoveFlatLayers()
        {
            if (_flat.Count == 0)
                return;

            foreach (var pair in _flat)
            {
                var layer = pair.Value;
                if (layer.Source != null)
                    layer.Source.enabled = true;
                if (layer.StarsRoot != null)
                    Destroy(layer.StarsRoot);
                if (layer.Quad != null)
                    Destroy(layer.Quad);
            }
            _flat.Clear();
        }

        private static void Hide(GameObject go, List<GameObject> record)
        {
            go.SetActive(false);
            record.Add(go);
        }

        private static void ShowAll(List<GameObject> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null)
                    list[i].SetActive(true);
            }
            list.Clear();
        }

        private void PruneDestroyed()
        {
            if (_flat.Count == 0)
                return;

            var keep = new Dictionary<int, FlatLayer>();
            foreach (var pair in _flat)
            {
                if (pair.Value.Source != null)
                    keep.Add(pair.Key, pair.Value);
            }
            _flat = keep;
        }

        // ---------- color changes ----------

        private void RefreshSky()
        {
            if (!_enabled.Value)
                return;

            if (_flat.Count == 0)
                ApplyToScene();
            else
                UpdateFlatColors();
        }

        private void SetTarget(Color c, bool updateText)
        {
            _target = new Color(c.r, c.g, c.b, 1f);
            SyncHsv(_target);
            if (updateText)
                _hexText = ToHex(_target);
            RefreshSky();
        }

        private void SetSaturation(float s)
        {
            _sat = Mathf.Clamp01(s);
            _target = Color.HSVToRGB(_hue, _sat, _val);
            _hexText = ToHex(_target);
            RefreshSky();
        }

        private void SetBrightness(float v)
        {
            _val = Mathf.Clamp01(v);
            _target = Color.HSVToRGB(_hue, _sat, _val);
            _hexText = ToHex(_target);
            RefreshSky();
        }

        // Remembers the hue even when the color is gray or black, so the saturation and
        // brightness sliders can bring the color back after being dragged to 0%.
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
            // Anything that changes which controls are drawn is captured once here, so the
            // layout is identical in every event of a frame.
            bool hasStatus = _status.Length > 0;
            bool extrasOpen = _extrasOpen;
            bool addStars = _addStars;
            bool hasTemplate = _template.Count > 0;
            string[] files = _bgFiles;
            bool hasBg = _bgSprite != null;
            var prevBg = GUI.backgroundColor;

            // Header
            GUILayout.BeginHorizontal();
            GUILayout.Label("Sky Tint", _sTitle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_darkMode.Value ? "Light mode" : "Dark mode", _sButton, GUILayout.Width(96f)))
            {
                _darkMode.Value = !_darkMode.Value;
                _stylesBuilt = false;
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
            GUILayout.Label("Hex", _sLabel, GUILayout.Width(40f));
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
            if (ChannelRow("Sat", ref sat, 100f, true))
                SetSaturation(sat);

            float bri = _val;
            if (ChannelRow("Bri", ref bri, 100f, true))
                SetBrightness(bri);

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

            // Extras (stars, clouds)
            if (GUILayout.Button(extrasOpen ? "Hide extras (stars, clouds, image)" : "Show extras (stars, clouds, image)", _sButton))
            {
                _extrasOpen = !_extrasOpen;
                _window.height = 10f;
            }

            if (extrasOpen)
                DrawExtras(addStars, hasTemplate, files, hasBg);

            GUILayout.Space(8f);
            Divider();
            GUILayout.Space(8f);

            // Recolor on/off
            bool on = _enabled.Value;
            if (GUILayout.Button(on ? "Sky recolor: ON" : "Sky recolor: OFF", on ? _sAccent : _sButton))
            {
                _enabled.Value = !on;
                if (_enabled.Value) ApplyToScene();
                else RemoveFlatLayers();
            }

            // Saved default
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Load saved default", _sButton))
            {
                Color saved;
                if (TryParse(_hex.Value, out saved))
                {
                    SetTarget(saved, true);
                    ReloadExtras();
                    _status = "Loaded saved default " + _hex.Value + ".";
                }
            }

            if (GUILayout.Button("Save as default", _sButton))
            {
                try
                {
                    _hex.Value = ToHex(_target);
                    SaveExtrasToConfig();
                    Config.Save();
                    _status = "Saved " + _hex.Value + " and extras to config.";
                    Logger.LogInfo("Saved sky color " + _hex.Value + " to " + Config.ConfigFilePath);
                }
                catch (System.Exception ex)
                {
                    _status = "Save failed: " + ex.Message;
                    Logger.LogError("Could not save config: " + ex);
                }
            }
            GUILayout.EndHorizontal();

            if (hasStatus)
                GUILayout.Label(_status, _sStatus);

            GUI.backgroundColor = prevBg;
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 48f));
        }

        private void DrawExtras(bool addStars, bool hasTemplate, string[] files, bool hasBg)
        {
            GUILayout.Space(4f);
            GUILayout.Label("ORIGINAL STARS AND CLOUDS", _sSection);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_hideStars ? "Stars: hidden" : "Stars: shown", _hideStars ? _sAccent : _sButton))
            {
                _hideStars = !_hideStars;
                if (_hideStars) ApplyToScene();
                else ShowAll(_hiddenStars);
            }
            if (GUILayout.Button(_hideClouds ? "Clouds: hidden" : "Clouds: shown", _hideClouds ? _sAccent : _sButton))
            {
                _hideClouds = !_hideClouds;
                if (_hideClouds) ApplyToScene();
                else ShowAll(_hiddenClouds);
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label("EXTRA STARS", _sSection);
            GUILayout.Label("Copies the game's stars. Needs Sky recolor ON.", _sSub);

            if (GUILayout.Button(_addStars ? "Extra stars: ON" : "Extra stars: OFF", _addStars ? _sAccent : _sButton))
            {
                _addStars = !_addStars;
                RefreshStars();
            }

            if (addStars)
            {
                float copies = Mathf.Clamp01(_starCopies / 8f);
                if (ChannelRow("Count", ref copies, 8f))
                {
                    _starCopies = Mathf.Clamp(Mathf.RoundToInt(copies * 8f), 0, 8);
                    RefreshStars();
                }

                float shift = _starOffset;
                if (ChannelRow("Shift", ref shift, 100f, true))
                {
                    _starOffset = Mathf.Clamp01(Mathf.Round(shift * 20f) / 20f);
                    RefreshStars();
                }

                if (!hasTemplate)
                    GUILayout.Label("Open a level with stars once so I can copy them. Until then, simple dots are used.", _sStatus);
            }

            GUILayout.Space(6f);
            GUILayout.Label("BACKGROUND IMAGE", _sSection);
            GUILayout.Label("Fills the screen, over every sky layer including stars and clouds. Put PNG or JPG files in BepInEx\\config\\UchSkyTint Backgrounds, then click Refresh list.", _sStatus);

            if (GUILayout.Button("None", !hasBg ? _sAccent : _sButton))
                SelectBackground("");

            int shown = Mathf.Min(files.Length, 8);
            for (int i = 0; i < shown; i++)
            {
                bool selected = hasBg && files[i] == _bgName;
                if (GUILayout.Button(Shorten(files[i], 32), selected ? _sAccent : _sButton) && !selected)
                    SelectBackground(files[i]);
            }

            if (files.Length > shown)
                GUILayout.Label("Showing the first 8 files.", _sSub);

            if (GUILayout.Button("Refresh list", _sSmall))
                RefreshBackgroundList();

            if (hasBg)
            {
                float alpha = _bgOpacity;
                if (ChannelRow("Alpha", ref alpha, 100f, true))
                {
                    _bgOpacity = Mathf.Clamp01(alpha);
                    ApplyToScene();
                }

                if (GUILayout.Button(_bgFollow ? "Position: follows camera" : "Position: fixed in place", _sButton))
                {
                    _bgFollow = !_bgFollow;
                    PushOverlaySettings();
                }

                if (GUILayout.Button("Re-snap to the current view", _sSmall))
                    PushOverlaySettings();

                float zoom = _bgZoom;
                if (ValueRow("Zoom", ref zoom, 0.25f, 3f, Mathf.RoundToInt(_bgZoom * 100f) + "%"))
                {
                    _bgZoom = Mathf.Clamp(zoom, 0.25f, 3f);
                    PushOverlaySettings();
                }

                float offX = _bgOffX;
                if (ValueRow("Left/right", ref offX, -1f, 1f, Mathf.RoundToInt(_bgOffX * 100f) + "%"))
                {
                    _bgOffX = Mathf.Clamp(offX, -1f, 1f);
                    PushOverlaySettings();
                }

                float offY = _bgOffY;
                if (ValueRow("Up/down", ref offY, -1f, 1f, Mathf.RoundToInt(_bgOffY * 100f) + "%"))
                {
                    _bgOffY = Mathf.Clamp(offY, -1f, 1f);
                    PushOverlaySettings();
                }

                if (GUILayout.Button("Reset zoom and position", _sSmall))
                {
                    _bgZoom = 1f;
                    _bgOffX = 0f;
                    _bgOffY = 0f;
                    PushOverlaySettings();
                }
            }
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

        private bool ChannelRow(string label, ref float value, float displayMax = 255f, bool percent = false)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _sLabel, GUILayout.Width(40f));
            float v = GUILayout.HorizontalSlider(value, 0f, 1f, _sSlider, _sThumb);
            string shown = Mathf.RoundToInt(v * displayMax).ToString() + (percent ? "%" : "");
            GUILayout.Label(shown, _sValue);
            GUILayout.EndHorizontal();

            if (Mathf.Approximately(v, value))
                return false;
            value = v;
            return true;
        }

        // A slider with its own range and a ready-made value label.
        private bool ValueRow(string label, ref float value, float min, float max, string shown)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _sLabel, GUILayout.Width(40f));
            float v = GUILayout.HorizontalSlider(value, min, max, _sSlider, _sThumb);
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
            _sValue.fixedWidth = 36f;
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

            // Color buttons: white backgrounds that get tinted with GUI.backgroundColor
            var chip = Shape(24, 24, 12f, 12f, 7f, Color.white, new Color(0.78f, 0.78f, 0.78f, 1f), 1f);
            var chipHover = Shape(24, 24, 12f, 12f, 7f, new Color(0.9f, 0.9f, 0.9f, 1f), new Color(0.7f, 0.7f, 0.7f, 1f), 1f);
            var chipActive = Shape(24, 24, 12f, 12f, 7f, new Color(0.78f, 0.78f, 0.78f, 1f), new Color(0.6f, 0.6f, 0.6f, 1f), 1f);
            _sChipDark = ButtonStyle(chip, chipHover, chipActive, Hex("#14171C"));
            _sChipLight = ButtonStyle(chip, chipHover, chipActive, Color.white);

            // Small buttons
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

        private static string[] SplitNames(string text)
        {
            var result = new List<string>();
            if (!string.IsNullOrEmpty(text))
            {
                var parts = text.Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    string p = parts[i].Trim();
                    if (p.Length > 0)
                        result.Add(p);
                }
            }
            return result.ToArray();
        }

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

    public enum BgFit
    {
        Cover,    // fills the screen and keeps the picture's shape (edges may be cropped)
        Stretch,  // fills the screen and squashes the picture
        Contain   // shows the whole picture (may leave gaps at the sides or top and bottom)
    }

    // Keeps the background picture exactly the size of what the camera shows. The sky layers
    // are much bigger than the visible area, so sizing the picture to them made it huge.
    public sealed class OverlayFollower : MonoBehaviour
    {
        public float SpriteAspect = 1f;   // picture height divided by picture width
        public BgFit Mode = BgFit.Cover;
        public float Zoom = 1f;           // 1 is the fitted size
        public float OffsetX, OffsetY;    // -1 to 1, as a fraction of half the screen
        public float Z;
        public bool Follow;               // false: the picture stays in one place in the world

        private Camera _cam;
        private bool _frozen;
        private float _freezeAt;

        private void OnEnable()
        {
            Camera.onPreCull += HandlePreCull;
        }

        private void OnDisable()
        {
            Camera.onPreCull -= HandlePreCull;
        }

        public void Refresh()
        {
            _cam = FindCamera();
            // Re-fit to what the camera shows right now, then stay there after a moment
            // (the camera often still settles for a second when a level loads).
            _frozen = false;
            _freezeAt = Time.unscaledTime + 1f;
            Fit();
        }

        private void LateUpdate()
        {
            if (_cam == null || !_cam.isActiveAndEnabled)
                _cam = FindCamera();

            Fit();
        }

        // The camera can move after our LateUpdate, so fit once more right before it draws.
        private void HandlePreCull(Camera c)
        {
            if (c != null && c == _cam)
                Fit();
        }

        private Camera FindCamera()
        {
            int mask = 1 << gameObject.layer;

            var main = Camera.main;
            if (main != null && main.isActiveAndEnabled && main.targetTexture == null &&
                (main.cullingMask & mask) != 0)
                return main;

            Camera best = null;
            var all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null || c.targetTexture != null || (c.cullingMask & mask) == 0)
                    continue;
                if (best == null || c.depth > best.depth)
                    best = c;
            }
            return best;
        }

        private void Fit()
        {
            if (_cam == null)
                return;

            if (!Follow)
            {
                if (_frozen)
                    return;
                if (Time.unscaledTime >= _freezeAt)
                    _frozen = true;   // do this last fit, then stop
            }

            Vector3 cp = _cam.transform.position;
            float height;
            if (_cam.orthographic)
            {
                height = _cam.orthographicSize * 2f;
            }
            else
            {
                float d = Mathf.Max(0.01f, Mathf.Abs(Z - cp.z));
                height = 2f * d * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            }
            float width = height * _cam.aspect;

            // The sprite is one world unit wide and SpriteAspect units tall.
            float sx = width;
            float sy = height / Mathf.Max(SpriteAspect, 0.0001f);
            if (Mode == BgFit.Cover)
            {
                float s = Mathf.Max(sx, sy);
                sx = s;
                sy = s;
            }
            else if (Mode == BgFit.Contain)
            {
                float s = Mathf.Min(sx, sy);
                sx = s;
                sy = s;
            }

            sx *= Zoom;
            sy *= Zoom;

            transform.rotation = Quaternion.identity;
            transform.position = new Vector3(
                cp.x + OffsetX * width * 0.5f,
                cp.y + OffsetY * height * 0.5f,
                Z);

            Vector3 ls = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
            transform.localScale = new Vector3(Div(sx, ls.x), Div(sy, ls.y), 1f);
        }

        private static float Div(float a, float b)
        {
            if (Mathf.Abs(b) < 0.0001f)
                return Mathf.Abs(a);
            return Mathf.Abs(a / b);
        }
    }
}
