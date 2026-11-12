using Godot;

namespace Lightlag.Game;

/// <summary>Windowed, borderless full screen (fills the screen, quick to switch away from) or exclusive full screen.</summary>
public enum WindowStyle { Windowed, Borderless, Fullscreen }

/// <summary>
/// The display settings, kept in display.cfg in the data folder: window style, window size (windowed only) and
/// interface size. The interface size scales everything drawn (menus, text and the map) so the game reads the same
/// on a 4K screen as on a 1080p one. Full screen always draws at the screen's own resolution.
/// F11 or Alt+Enter switches between the window and full screen.
/// </summary>
public static class Display
{
    /// <summary>Window sizes on offer; ones bigger than the screen are left out.</summary>
    public static readonly Vector2I[] Sizes =
    {
        new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440), new(3200, 1800), new(3840, 2160),
    };

    /// <summary>Interface sizes on offer; 0 is Auto (follows the window's height, at least 100%).</summary>
    public static readonly float[] Scales = { 0f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f };

    public static WindowStyle Style { get; private set; } = WindowStyle.Windowed;
    public static Vector2I WindowSize { get; private set; } = new(1600, 900);
    /// <summary>The chosen interface size; 0 is Auto.</summary>
    public static float ScaleChoice { get; private set; }
    /// <summary>The full screen style F11 switches to (the last one used).</summary>
    public static WindowStyle LastFullscreen { get; private set; } = WindowStyle.Borderless;

    /// <summary>The interface size in use now (Auto worked out).</summary>
    public static float Scale { get; private set; } = 1f;

    public static event Action? Changed;

    private static Window? _window;
    private static string? _file;

    /// <summary>
    /// Read the saved settings and apply them. With <paramref name="applyWindow"/> false (CI smoke tests and
    /// screenshots) the window is left as the command line made it; only the scaling is set up.
    /// </summary>
    public static void Load(Node root, string dataDir, bool applyWindow = true)
    {
        _file = System.IO.Path.Combine(dataDir, "display.cfg");
        var cfg = new ConfigFile();
        if (cfg.Load(_file) == Error.Ok)
        {
            if (Enum.TryParse<WindowStyle>((string)cfg.GetValue("display", "style", "Windowed"), out var s)) Style = s;
            if (Enum.TryParse<WindowStyle>((string)cfg.GetValue("display", "last_fullscreen", "Borderless"), out var f) && f != WindowStyle.Windowed)
                LastFullscreen = f;
            var w = (int)cfg.GetValue("display", "width", 1600);
            var h = (int)cfg.GetValue("display", "height", 900);
            if (w >= 640 && h >= 360) WindowSize = new Vector2I(w, h);
            var scale = (float)cfg.GetValue("display", "ui_scale", 0f);
            ScaleChoice = scale == 0f || Scales.Contains(scale) ? scale : 0f;
        }
        _window = root.GetWindow();
        // Keep the screen and window pixels one to one, then scale by the interface size alone: the logical
        // screen is the window size divided by it (see Rescale).
        _window.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
        _window.ContentScaleAspect = Window.ContentScaleAspectEnum.Expand;
        _window.SizeChanged += Rescale;
        if (applyWindow) ApplyWindow();
        Rescale();
    }

    public static string Name(WindowStyle s) => s switch
    {
        WindowStyle.Borderless => "Borderless full screen",
        WindowStyle.Fullscreen => "Full screen",
        _ => "Window",
    };

    public static string ScaleName(float s) => s == 0f ? $"Auto ({AutoScale() * 100:0}%)" : $"{s * 100:0}%";

    public static void SetStyle(WindowStyle style)
    {
        if (style == Style) return;
        Style = style;
        if (style != WindowStyle.Windowed) LastFullscreen = style;
        ApplyWindow();
        Save("Window style changed", new { style = style.ToString() });
    }

    public static void SetWindowSize(Vector2I size)
    {
        WindowSize = size;
        if (Style != WindowStyle.Windowed) Style = WindowStyle.Windowed;
        ApplyWindow();
        Save("Window size changed", new { width = size.X, height = size.Y });
    }

    public static void SetScale(float scale)
    {
        if (scale == ScaleChoice) return;
        ScaleChoice = scale;
        Rescale();
        Save("Interface size changed", new { scale });
    }

    /// <summary>F11 / Alt+Enter: the window, or back to the full screen style used last.</summary>
    public static void ToggleFullscreen() => SetStyle(Style == WindowStyle.Windowed ? LastFullscreen : WindowStyle.Windowed);

    /// <summary>The window sizes that fit on the screen the window is on (always at least the smallest).</summary>
    public static List<Vector2I> FittingSizes()
    {
        var screen = DisplayServer.ScreenGetSize(_window?.CurrentScreen ?? DisplayServer.WindowGetCurrentScreen());
        var list = Sizes.Where(s => s.X <= screen.X && s.Y <= screen.Y).ToList();
        if (list.Count == 0) list.Add(Sizes[0]);
        if (!list.Contains(WindowSize)) list.Add(WindowSize);
        return list.OrderBy(s => s.X).ToList();
    }

    private static void ApplyWindow()
    {
        if (_window == null) return;
        switch (Style)
        {
            case WindowStyle.Fullscreen:
                _window.Mode = Window.ModeEnum.ExclusiveFullscreen;
                break;
            case WindowStyle.Borderless:
                _window.Mode = Window.ModeEnum.Fullscreen;
                break;
            default:
            {
                _window.Mode = Window.ModeEnum.Windowed;
                _window.Borderless = false;
                // Never bigger than the screen's free area, so the title bar stays reachable.
                var usable = DisplayServer.ScreenGetUsableRect(_window.CurrentScreen).Size;
                var size = new Vector2I(Math.Min(WindowSize.X, Math.Max(640, usable.X)), Math.Min(WindowSize.Y, Math.Max(360, usable.Y - 40)));
                _window.Size = size;
                _window.MoveToCenter();
                break;
            }
        }
        Rescale();
    }

    /// <summary>Auto: 100% up to a 1080-line window, then in quarter steps with the height (1440 → 125%, 2160 → 200%).</summary>
    public static float AutoScale()
    {
        var h = _window?.Size.Y ?? 900;
        return Math.Max(1f, MathF.Round(h / 1080f * 4f) / 4f);
    }

    private static void Rescale()
    {
        if (_window == null) return;
        var size = _window.Size;
        if (size.X <= 0 || size.Y <= 0) return; // minimised
        var scale = ScaleChoice == 0f ? AutoScale() : ScaleChoice;
        // Never so big that the menus run off a small window: keep at least 960 x 540 of room.
        scale = Math.Min(scale, Math.Max(0.5f, Math.Min(size.X / 960f, size.Y / 540f)));
        // Setting these can raise SizeChanged again; only touch them when they differ.
        if (_window.ContentScaleSize != size) _window.ContentScaleSize = size;
        if (!Mathf.IsEqualApprox(_window.ContentScaleFactor, scale)) _window.ContentScaleFactor = scale;
        if (scale != Scale)
        {
            Scale = scale;
            Changed?.Invoke();
        }
    }

    private static void Save(string message, object data)
    {
        if (_file != null)
        {
            var cfg = new ConfigFile();
            cfg.SetValue("display", "style", Style.ToString());
            cfg.SetValue("display", "last_fullscreen", LastFullscreen.ToString());
            cfg.SetValue("display", "width", WindowSize.X);
            cfg.SetValue("display", "height", WindowSize.Y);
            cfg.SetValue("display", "ui_scale", ScaleChoice);
            cfg.Save(_file);
        }
        Main.Instance.Log.Info("ui", message, data);
        StyleChanged?.Invoke();
    }

    /// <summary>Window style, window size and interface size, for the title screen and the game menu.</summary>
    public static Control Picker()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(Ui.Label("Display:", 15, Ui.Dim));

        var style = new OptionButton
        {
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Window: a normal window you can resize. Borderless full screen: fills the screen, quick to switch away from. Full screen: exclusive, can be a little smoother. F11 or Alt+Enter switches",
        };
        foreach (var s in Enum.GetValues<WindowStyle>()) style.AddItem(Name(s), (int)s);
        style.Selected = (int)Style;
        style.ItemSelected += i => Main.Instance.RunGuarded("display style", () => SetStyle((WindowStyle)(int)i));
        row.AddChild(style);

        var size = new OptionButton
        {
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Window size (in a window only: full screen always uses the screen's own resolution)",
        };
        var sizes = FittingSizes();
        foreach (var s in sizes) size.AddItem($"{s.X} × {s.Y}");
        size.Selected = sizes.IndexOf(WindowSize);
        size.Disabled = Style != WindowStyle.Windowed;
        size.ItemSelected += i => Main.Instance.RunGuarded("window size", () => SetWindowSize(sizes[(int)i]));
        row.AddChild(size);

        row.AddChild(Ui.Label("Interface:", 15, Ui.Dim));
        var scale = new OptionButton
        {
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "How big text, menus and the map draw. Auto grows with the window on big screens (1440 lines: 125%, 4K: 200%)",
        };
        void FillScales()
        {
            scale.Clear();
            foreach (var s in Scales) scale.AddItem(ScaleName(s));
            scale.Selected = Array.IndexOf(Scales, ScaleChoice);
        }
        FillScales();
        scale.ItemSelected += i => Main.Instance.RunGuarded("interface size", () => SetScale(Scales[(int)i]));
        row.AddChild(scale);

        // Keep the picker in step with F11 and window changes made elsewhere.
        // Deferred, as this can be raised from inside the picker's own selection.
        void Sync() => Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(row)) return;
            style.Selected = (int)Style;
            size.Disabled = Style != WindowStyle.Windowed;
            FillScales();
        }).CallDeferred();
        Changed += Sync;
        StyleChanged += Sync;
        row.TreeExiting += () => { Changed -= Sync; StyleChanged -= Sync; };
        return row;
    }

    /// <summary>The window style or size changed (from a picker or F11).</summary>
    private static event Action? StyleChanged;
}
