using System.Windows;
using System.Windows.Media;
using Vidsc.Media.Inspection;

namespace Vidsc.App.Controls;

/// <summary>Advisory peak display. Partial/absent coverage is visually distinct from silence.</summary>
public sealed class WaveformControl : FrameworkElement
{
    public static readonly DependencyProperty WaveformProperty = DependencyProperty.Register(nameof(Waveform), typeof(WaveformResult),
        typeof(WaveformControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public WaveformResult? Waveform { get => (WaveformResult?)GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Waveform is not { } waveform || ActualWidth <= 0 || ActualHeight <= 0) return;
        var level = waveform.Levels[0];
        foreach (var candidate in waveform.Levels.Skip(1))
        {
            if (level.Bins.Count <= ActualWidth * 2) break;
            level = candidate;
        }
        double duration = waveform.Metadata.GetProperty("analyzedDurationSeconds").GetDouble();
        foreach (var bin in level.Bins)
        {
            double x = bin.Index * (double)level.SamplesPerBin / waveform.SampleRate / duration * ActualWidth;
            double width = Math.Max(1, level.SamplesPerBin / (double)waveform.SampleRate / duration * ActualWidth);
            if (x >= ActualWidth) continue;
            bool partial = bin.CoverageSamples < level.SamplesPerBin;
            if (partial) drawing.DrawRectangle(Brushes.DimGray, null, new Rect(x, 0, Math.Min(width, ActualWidth - x), ActualHeight));
            else drawing.DrawLine(new Pen(Brushes.DarkSlateGray, 2), new Point(x, ActualHeight / 2),
                new Point(Math.Min(ActualWidth, x + width), ActualHeight / 2));
            double minimum = Math.Clamp(bin.Peaks.Min(p => p[0]), -1, 1), maximum = Math.Clamp(bin.Peaks.Max(p => p[1]), -1, 1);
            drawing.DrawLine(new Pen(partial ? Brushes.LightGray : Brushes.MediumTurquoise, Math.Min(width, 2)),
                new Point(x, (1 - maximum) * ActualHeight / 2), new Point(x, (1 - minimum) * ActualHeight / 2));
        }
    }
}
