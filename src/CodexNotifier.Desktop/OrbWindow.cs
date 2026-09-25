using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CodexNotifier.Desktop;

public sealed class OrbWindow : Window
{
    readonly OrbDrawing drawing = new();
    readonly App app;
    Point? down;
    bool dragging;
    public OrbWindow(App app)
    {
        this.app = app;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        Width = Height = app.Settings.OrbSize + 28;
        Content = drawing; Cursor = Cursors.Hand;
        ToolTip = "Codex Notifier：拖动移动 · 双击暂停通知 · 右键设置";
        SourceInitialized += (_, _) => Native.NoActivate(this, false);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { down = null; app.Settings.Enabled = !app.Settings.Enabled; app.ApplySettings(); e.Handled = true; return; }
            down = e.GetPosition(this);
        };
        MouseMove += (_, e) =>
        {
            if (down is not Point p || e.LeftButton != MouseButtonState.Pressed || dragging) return;
            var n = e.GetPosition(this);
            if (Math.Abs(n.X - p.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(n.Y - p.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            down = null; dragging = true;
            try { DragMove(); } catch (InvalidOperationException) { }
            finally { dragging = false; app.Settings.OrbLeft = Left; app.Settings.OrbTop = Top; app.SaveSettings(); }
        };
        MouseLeftButtonUp += (_, _) => down = null;
        MouseRightButtonUp += (_, _) => app.OpenPanel();
        IsVisibleChanged += (_, _) => drawing.SetVisible(IsVisible);
        Closed += (_, _) => drawing.Dispose();
    }
    public void Apply()
    {
        Width = Height = app.Settings.OrbSize + 28;
        Left = app.Settings.OrbLeft; Top = app.Settings.OrbTop;
        if (app.Settings.OrbVisible) { if (!IsVisible) Show(); Native.ClampToMonitor(this); }
        else Hide();
        drawing.SetState(app.Settings.Enabled, app.Tracker.Active > 0);
    }
    public void Complete() => drawing.Complete();
    public void DemoWorking() => drawing.SetState(true, true);
    public void Capture(string file) => Native.Capture(drawing, file);
}

sealed class OrbDrawing : FrameworkElement, IDisposable
{
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly Stopwatch clock = Stopwatch.StartNew();
    bool enabled = true, working, visible;
    double mix, target, finished = -100, lastText;
    string glyph = "0x";
    readonly Random random = new();
    public OrbDrawing()
    {
        timer.Tick += (_, _) =>
        {
            mix += (target - mix) * .14;
            if (clock.Elapsed.TotalSeconds - lastText > .55)
            { const string chars = "01{}[]<>/?:;#@"; glyph = new string(Enumerable.Range(0, 3).Select(_ => chars[random.Next(chars.Length)]).ToArray()); lastText = clock.Elapsed.TotalSeconds; }
            InvalidateVisual();
            if ((!working || !enabled) && Math.Abs(mix - target) < .005 && clock.Elapsed.TotalSeconds - finished > 1.5) timer.Stop();
        };
    }
    public void SetState(bool enabled, bool working)
    { this.enabled = enabled; this.working = working; target = enabled && working ? 1 : 0; if (!enabled) finished = -100; Start(); InvalidateVisual(); }
    public void SetVisible(bool v) { visible = v; if (v) Start(); else timer.Stop(); }
    void Start() { if (visible) timer.Start(); }
    public void Complete() { if (!enabled) return; finished = clock.Elapsed.TotalSeconds; mix = 1; Start(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double t = clock.Elapsed.TotalSeconds, age = t - finished;
        bool check = enabled && age < 1.3;
        double blend = check ? Math.Max(0, 1 - age / .6) : mix;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = Math.Max(8, Math.Min(ActualWidth, ActualHeight) / 2 - 14);
        Color green = Color.FromRgb(59, 192, 117), white = Color.FromRgb(238, 245, 240);
        Color body = !enabled ? Color.FromRgb(107, 118, 114) : Mix(green, white, blend);
        for (int i = enabled ? 12 : 0; i >= 2; i -= 2)
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(enabled ? 7 : 2), 76, 231, 139)), null, center, r + i, r + i);
        var brush = new RadialGradientBrush { Center = new Point(.35, .28), GradientOrigin = new Point(.3, .2), RadiusX = .85, RadiusY = .85 };
        brush.GradientStops.Add(new GradientStop(Mix(body, Colors.White, .45), 0));
        brush.GradientStops.Add(new GradientStop(body, .58));
        brush.GradientStops.Add(new GradientStop(Mix(body, Colors.Black, .28), 1));
        dc.DrawEllipse(brush, new Pen(new SolidColorBrush(enabled ? Color.FromArgb(110, 217, 255, 227) : Color.FromArgb(90, 180, 187, 183)), 1), center, r, r);
        if (enabled && working && !check)
        {
            for (int j = 0; j < 38; j++)
            {
                double a = t * 2.7 - j * .041;
                var p = new Point(center.X + Math.Cos(a) * (r + 4), center.Y + Math.Sin(a) * (r + 4));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(220 * (1 - j / 38.0)), 124, 255, 184)), null, p, 1.7, 1.7);
            }
            double opacity = .12 + .7 * Math.Pow(Math.Sin((t - lastText) / .55 * Math.PI), 2);
            dc.PushOpacity(opacity);
            var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), r * .50, new SolidColorBrush(Color.FromRgb(23, 43, 33)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2)); dc.Pop();
        }
        else if (check)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(18, 83, 45)), 3.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var g = new StreamGeometry(); using (var ctx = g.Open()) { ctx.BeginFigure(new Point(center.X - r * .35, center.Y), false, false); ctx.LineTo(new Point(center.X - r * .07, center.Y + r * .25), true, false); ctx.LineTo(new Point(center.X + r * .4, center.Y - r * .27), true, false); }
            dc.PushOpacity(Math.Min(1, age * 7)); dc.DrawGeometry(null, pen, g); dc.Pop();
        }
        else
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 240, 255, 246)), null, new Point(center.X - r * .24, center.Y - r * .34), r * .19, r * .095);
            if (!enabled)
            {
                var b = new SolidColorBrush(Color.FromArgb(180, 33, 49, 40));
                dc.DrawRoundedRectangle(b, null, new Rect(center.X - 7, center.Y - 8, 4, 16), 1, 1);
                dc.DrawRoundedRectangle(b, null, new Rect(center.X + 3, center.Y - 8, 4, 16), 1, 1);
            }
        }
    }
    static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    public void Dispose() => timer.Stop();
}

sealed class GlowWindow : Window
{
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly GlowDrawing drawing;
    readonly Stopwatch clock = Stopwatch.StartNew();
    public GlowWindow(OrbWindow orb, bool flash)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; IsHitTestVisible = false;
        if (flash)
        {
            var area = Native.MonitorBounds(orb);
            Left = area.Left; Top = area.Top; Width = area.Width; Height = area.Height;
        }
        else { Width = Height = 460; Left = orb.Left + orb.Width / 2 - 230; Top = orb.Top + orb.Height / 2 - 230; }
        drawing = new GlowDrawing(flash, new Point(orb.Left + orb.Width / 2 - Left, orb.Top + orb.Height / 2 - Top)); Content = drawing;
        SourceInitialized += (_, _) => Native.NoActivate(this, true);
        timer.Tick += (_, _) => { drawing.Progress = clock.Elapsed.TotalSeconds / .95; drawing.InvalidateVisual(); if (drawing.Progress >= 1) Close(); };
        Closed += (_, _) => timer.Stop(); Loaded += (_, _) => { clock.Restart(); timer.Start(); };
    }
    sealed class GlowDrawing(bool flash, Point center) : FrameworkElement
    {
        public double Progress;
        protected override void OnRender(DrawingContext dc)
        {
            double p = Math.Clamp(Progress, 0, 1), alpha = Math.Sin(p * Math.PI) * (1 - p);
            if (flash) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(alpha * 20), 88, 235, 145)), null, new Rect(0, 0, ActualWidth, ActualHeight));
            double r = 34 + p * 175;
            for (int i = 0; i < 5; i++) dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(alpha * (65 - i * 11)), 100, 251, 162)), 3 + i * 3), center, r, r);
        }
    }
}

static class Native
{
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int n, int value);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr h, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public RectI Monitor, Work; public uint Flags; }
    public static void NoActivate(Window window, bool clickThrough)
    {
        var h = new WindowInteropHelper(window).Handle;
        SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x08000000 | 0x80 | (clickThrough ? 0x20 : 0));
    }
    public static Rect MonitorBounds(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(h, 2), ref info)) return SystemParameters.WorkArea;
        var transform = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(transform.Transform(new Point(info.Work.Left, info.Work.Top)), transform.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }
    public static void ClampToMonitor(Window w)
    {
        var r = MonitorBounds(w);
        w.Left = Math.Clamp(w.Left, r.Left, Math.Max(r.Left, r.Right - w.Width));
        w.Top = Math.Clamp(w.Top, r.Top, Math.Max(r.Top, r.Bottom - w.Height));
    }
    public static void Capture(FrameworkElement element, string file)
    {
        element.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var f = File.Create(file); encoder.Save(f);
    }
}
