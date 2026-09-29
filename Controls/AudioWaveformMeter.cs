using System.Windows;
using System.Windows.Media;

namespace RoomTalk.Controls;

/// <summary>
/// Đồng hồ mức microphone dạng sóng đơn giản, nhẹ và không cần thư viện đồ họa ngoài.
/// Level nhận giá trị từ 0 đến 100.
/// </summary>
public sealed class AudioWaveformMeter : FrameworkElement
{
    private static readonly double[] BarShape =
    {
        0.28, 0.42, 0.58, 0.76, 0.52, 0.88, 0.64, 1.00, 0.72, 0.92,
        0.62, 0.82, 0.48, 0.70, 0.40, 0.56, 0.32
    };

    private static readonly Brush MeterBackground = CreateFrozenBrush(241, 245, 249);
    private static readonly Brush InactiveBrush = CreateFrozenBrush(203, 213, 225);
    private static readonly Brush NormalBrush = CreateFrozenBrush(13, 148, 136);
    private static readonly Brush WarningBrush = CreateFrozenBrush(245, 158, 11);
    private static readonly Brush PeakBrush = CreateFrozenBrush(220, 38, 38);
    private static readonly Pen BorderPen = CreateFrozenPen(203, 213, 225);

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(double),
        typeof(AudioWaveformMeter),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender,
            null,
            CoerceLevel));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(AudioWaveformMeter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }


    private static object CoerceLevel(DependencyObject dependencyObject, object baseValue)
    {
        double value = baseValue is double number && double.IsFinite(number) ? number : 0d;
        return Math.Clamp(value, 0d, 100d);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 260 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 48 : availableSize.Height;
        return new Size(Math.Max(80, width), Math.Max(24, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        drawingContext.DrawRoundedRectangle(
            MeterBackground,
            BorderPen,
            new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1)),
            7,
            7);

        double normalizedLevel = IsActive ? Math.Clamp(Level / 100d, 0d, 1d) : 0d;
        double horizontalPadding = 10;
        double verticalPadding = 7;
        double usableWidth = Math.Max(1, width - horizontalPadding * 2);
        double usableHeight = Math.Max(1, height - verticalPadding * 2);
        double gap = 3;
        int barCount = BarShape.Length;
        double barWidth = Math.Max(2, (usableWidth - gap * (barCount - 1)) / barCount);

        Brush activeBrush = normalizedLevel >= 0.88
            ? PeakBrush
            : normalizedLevel >= 0.68
                ? WarningBrush
                : NormalBrush;

        for (int index = 0; index < barCount; index++)
        {
            double shape = BarShape[index];
            double minimumHeight = 4;
            double activeHeight = minimumHeight + usableHeight * shape * normalizedLevel;
            double displayedHeight = IsActive ? activeHeight : minimumHeight;
            double x = horizontalPadding + index * (barWidth + gap);
            double y = (height - displayedHeight) / 2;

            drawingContext.DrawRoundedRectangle(
                IsActive ? activeBrush : InactiveBrush,
                null,
                new Rect(x, y, barWidth, displayedHeight),
                barWidth / 2,
                barWidth / 2);
        }
    }

    private static Brush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(byte red, byte green, byte blue)
    {
        var pen = new Pen(CreateFrozenBrush(red, green, blue), 1);
        pen.Freeze();
        return pen;
    }

}
