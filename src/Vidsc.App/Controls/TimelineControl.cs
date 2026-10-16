using System.Globalization;
using Vidsc.Core.Time;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace Vidsc.App.Controls;

/// <summary>A bounded drawing of a clip band: never creates per-frame elements.</summary>
public sealed class TimelineControl : FrameworkElement
{
    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(nameof(Duration), typeof(double), typeof(TimelineControl), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender, OnDurationChanged));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(double), typeof(TimelineControl), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(nameof(Markers), typeof(IReadOnlyList<long>), typeof(TimelineControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InPositionProperty = DependencyProperty.Register(nameof(InPosition), typeof(double), typeof(TimelineControl), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OutPositionProperty = DependencyProperty.Register(nameof(OutPosition), typeof(double), typeof(TimelineControl), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double InPosition { get => (double)GetValue(InPositionProperty); set => SetValue(InPositionProperty, value); }
    public double OutPosition { get => (double)GetValue(OutPositionProperty); set => SetValue(OutPositionProperty, value); }
    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public IReadOnlyList<long>? Markers { get => (IReadOnlyList<long>?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public event Action? ScrubStarted;
    public event Action<double>? ScrubMoved;
    public event Action<double>? ScrubEnded;
    public event Action<double, double>? ViewportChanged;
    public event Action<bool, long>? BoundaryRequested;
    public double ViewStart => _start;
    public double ViewSpan => _span;
    private double _start, _span = 1, _panX, _panStart;
    private bool _dragging, _panning;
    private bool _overview;
    private int _handle;
    public TimelineControl() { Focusable = true; Cursor = Cursors.Hand; Height = 84; }
    protected override AutomationPeer OnCreateAutomationPeer() => new TimelineAutomationPeer(this);

    private sealed class TimelineAutomationPeer(TimelineControl owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(TimelineControl);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetNameCore() => "Video navigation timeline";
        protected override bool IsKeyboardFocusableCore() => true;
    }

    private static void OnDurationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((TimelineControl)sender).Fit();
    public void Fit() { _start = 0; _span = Math.Max(1, Duration); Changed(); }
    public void Center() { _start = Position - _span / 2; Clamp(); Changed(); }
    public void Zoom(double factor) { double anchor = Math.Clamp((Position - _start) / _span, 0, 1); _span = Math.Clamp(_span * factor, Math.Min(Math.Max(1, Duration), TimeSpan.TicksPerSecond), Math.Max(1, Duration)); _start = Position - anchor * _span; Clamp(); Changed(); }
    public void SetViewport(double start, double span) { _start = start; _span = Math.Clamp(span, 1, Math.Max(1, Duration)); Clamp(); Changed(); }
    private void Changed() { InvalidateVisual(); ViewportChanged?.Invoke(_start, _span); }
    private void Clamp() => _start = Math.Clamp(_start, 0, Math.Max(0, Duration - _span));
    private double Ticks(double x) => Math.Clamp(_start + x / Math.Max(1, ActualWidth) * _span, 0, Duration);
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(26, 37, 48)), new Pen(Brushes.SlateGray, 1), new Rect(0, 0, ActualWidth, ActualHeight));
        drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(42, 76, 94)), null, new Rect(0, 24, ActualWidth, Math.Max(1, ActualHeight - 38)));
        int divisions = Math.Clamp((int)(ActualWidth / 150), 4, 8);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i <= divisions; i++)
        {
            double tickX = ActualWidth * i / divisions;
            var label = new FormattedText(TimeText.Format((long)(_start + _span * i / divisions)),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 11, Brushes.LightSlateGray, dpi);
            drawing.DrawText(label, new Point(Math.Clamp(tickX - label.Width / 2, 0, Math.Max(0, ActualWidth - label.Width)), 3));
            drawing.DrawLine(new Pen(Brushes.SlateGray, 1), new Point(tickX, 20), new Point(tickX, 27));
        }
        // One marker per screen column bounds rendering even for a large keyframe index.
        int previousPixel = -1;
        if (Markers is { } markers)
        {
            int low = 0, high = markers.Count;
            while (low < high) { int mid = low + (high - low) / 2; if (markers[mid] < _start) low = mid + 1; else high = mid; }
            for (int i = low; i < markers.Count && markers[i] <= _start + _span; i++)
            {
                int pixel = (int)((markers[i] - _start) / _span * ActualWidth);
                if (pixel == previousPixel) continue;
                previousPixel = pixel;
                drawing.DrawLine(new Pen(Brushes.Goldenrod, 1), new Point(pixel, 28), new Point(pixel, ActualHeight - 14));
            }
        }
        double x = (Position - _start) / _span * ActualWidth;
        if (x >= 0 && x <= ActualWidth)
        {
            drawing.DrawLine(new Pen(Brushes.White, 2), new Point(x, 20), new Point(x, ActualHeight));
            var triangle = new StreamGeometry();
            using (StreamGeometryContext context = triangle.Open())
            {
                context.BeginFigure(new Point(x - 5, 20), true, true);
                context.LineTo(new Point(x + 5, 20), true, false);
                context.LineTo(new Point(x, 28), true, false);
            }
            drawing.DrawGeometry(Brushes.White, null, triangle);
        }
        foreach (var handle in new[] { (InPosition, Brushes.LightGreen, "I"), (OutPosition, Brushes.Salmon, "O") })
        {
            double hx = (handle.Item1 - _start) / _span * ActualWidth;
            if (hx < 0 || hx > ActualWidth) continue;
            drawing.DrawRectangle(handle.Item2, null, new Rect(hx - 5, 26, 10, Math.Max(1, ActualHeight - 43)));
            drawing.DrawText(new FormattedText(handle.Item3, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, Brushes.Black, dpi), new Point(hx - 3, 28));
        }
        double duration = Math.Max(1, Duration), overviewY = ActualHeight - 12;
        drawing.DrawRectangle(Brushes.DarkSlateGray, null, new Rect(0, overviewY, ActualWidth, 12));
        drawing.DrawRectangle(Brushes.SteelBlue, new Pen(Brushes.LightSteelBlue, 1),
            new Rect(_start / duration * ActualWidth, overviewY, Math.Max(1, _span / duration * ActualWidth), 11));
        drawing.DrawLine(new Pen(Brushes.White, 2), new Point(Position / duration * ActualWidth, overviewY), new Point(Position / duration * ActualWidth, ActualHeight));
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus(); CaptureMouse(); _dragging = true;
        _panning = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _panX = e.GetPosition(this).X; _panStart = _start;
        _overview = e.GetPosition(this).Y >= ActualHeight - 12;
        _handle = !_overview && !_panning && e.GetPosition(this).Y >= 26
            ? Math.Abs(_panX - (InPosition - _start) / _span * ActualWidth) <= 8 ? 1
                : Math.Abs(_panX - (OutPosition - _start) / _span * ActualWidth) <= 8 ? 2 : 0 : 0;
        if (_overview) { _start = _panX / Math.Max(1, ActualWidth) * Duration - _span / 2; Clamp(); Changed(); }
        else if (!_panning && _handle == 0) { ScrubStarted?.Invoke(); ScrubMoved?.Invoke(Ticks(_panX)); }
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        if (_overview) { _start = e.GetPosition(this).X / Math.Max(1, ActualWidth) * Duration - _span / 2; Clamp(); Changed(); }
        else if (_panning) { _start = _panStart - (e.GetPosition(this).X - _panX) / Math.Max(1, ActualWidth) * _span; Clamp(); Changed(); }
        else if (_handle != 0) SetCurrentValue(_handle == 1 ? InPositionProperty : OutPositionProperty, Ticks(e.GetPosition(this).X));
        else ScrubMoved?.Invoke(Ticks(e.GetPosition(this).X));
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        if (_handle != 0) BoundaryRequested?.Invoke(_handle == 2, (long)Ticks(e.GetPosition(this).X));
        else if (!_panning && !_overview) ScrubEnded?.Invoke(Ticks(e.GetPosition(this).X));
        ReleaseMouseCapture(); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging && !_panning && !_overview && _handle == 0) ScrubEnded?.Invoke(Position);
        _dragging = false;
    }
}
