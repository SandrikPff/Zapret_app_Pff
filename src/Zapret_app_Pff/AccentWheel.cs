using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZapretAppPff;

public sealed class AccentWheel : FrameworkElement
{
    private const int ImageSize = 256;
    private static readonly Lazy<BitmapSource> WheelImage = new(CreateWheelImage);
    private double _hue;
    private double _saturation;
    private double _brightness = 1;

    public event EventHandler? ColorChanged;

    public Color SelectedColor => FromHsv(_hue, _saturation, _brightness);

    public double Brightness
    {
        get => _brightness;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (_brightness == value)
                return;

            _brightness = value;
            InvalidateVisual();
            ColorChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetColor(Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        var min = Math.Min(color.R, Math.Min(color.G, color.B));
        var spread = max - min;
        _brightness = max / 255d;
        _saturation = max == 0 ? 0 : (double)spread / max;
        _hue = spread == 0 ? 0 : max == color.R ? (double)(color.G - color.B) / spread
            : max == color.G ? 2 + (double)(color.B - color.R) / spread
            : 4 + (double)(color.R - color.G) / spread;
        _hue = (_hue + 6) % 6;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var radius = Radius;
        if (radius <= 0)
            return;

        var center = Center;
        drawingContext.DrawImage(WheelImage.Value,
            new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));

        if (_brightness < 1)
        {
            drawingContext.PushOpacity(1 - _brightness);
            drawingContext.DrawEllipse(Brushes.Black, null, center, radius, radius);
            drawingContext.Pop();
        }

        var angle = _hue * Math.PI / 3;
        var cursor = center + new Vector(Math.Cos(angle), Math.Sin(angle)) * (_saturation * radius);
        drawingContext.DrawEllipse(null, new Pen(Brushes.Black, 3), cursor, 5.5, 5.5);
        drawingContext.DrawEllipse(new SolidColorBrush(SelectedColor), new Pen(Brushes.White, 1.5), cursor, 5.5, 5.5);
    }

    private Point Center => new(ActualWidth / 2, ActualHeight / 2);
    private double Radius => Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 8);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var offset = e.GetPosition(this) - Center;
        if (Radius <= 0 || offset.Length > Radius + 6)
            return;

        Focus();
        CaptureMouse();
        SelectOffset(offset / Radius);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed && Radius > 0)
            SelectOffset((e.GetPosition(this) - Center) / Radius);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
            return;

        if (Radius > 0)
            SelectOffset((e.GetPosition(this) - Center) / Radius);

        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var direction = e.Key switch
        {
            Key.Left => new Vector(-1, 0),
            Key.Right => new Vector(1, 0),
            Key.Up => new Vector(0, -1),
            Key.Down => new Vector(0, 1),
            _ => new Vector()
        };
        if (direction.LengthSquared == 0)
            return;

        var angle = _hue * Math.PI / 3;
        var point = new Vector(Math.Cos(angle), Math.Sin(angle)) * _saturation;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .12 : .025;
        SelectOffset(point + direction * step);
        e.Handled = true;
    }

    private void SelectOffset(Vector offset)
    {
        var saturation = Math.Min(offset.Length, 1);
        if (saturation > 0)
            _hue = (Math.Atan2(offset.Y, offset.X) * 3 / Math.PI + 6) % 6;

        _saturation = saturation;
        InvalidateVisual();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private static BitmapSource CreateWheelImage()
    {
        var pixels = new byte[ImageSize * ImageSize * 4];
        var radius = ImageSize / 2d;
        for (var y = 0; y < ImageSize; y++)
        for (var x = 0; x < ImageSize; x++)
        {
            var dx = x + .5 - radius;
            var dy = y + .5 - radius;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var coverage = Math.Clamp(radius + .5 - distance, 0, 1);
            if (coverage == 0)
                continue;

            var hue = (Math.Atan2(dy, dx) * 3 / Math.PI + 6) % 6;
            var color = FromHsv(hue, Math.Min(distance / radius, 1), 1);
            var offset = (y * ImageSize + x) * 4;
            pixels[offset] = color.B;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R;
            pixels[offset + 3] = (byte)Math.Round(coverage * 255);
        }

        var bitmap = BitmapSource.Create(ImageSize, ImageSize, 96, 96, PixelFormats.Bgra32, null, pixels, ImageSize * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color FromHsv(double hue, double saturation, double brightness)
    {
        var chroma = brightness * saturation;
        var neighboring = chroma * (1 - Math.Abs(hue % 2 - 1));
        var neutral = brightness - chroma;
        var (red, green, blue) = hue switch
        {
            < 1 => (chroma, neighboring, 0d),
            < 2 => (neighboring, chroma, 0d),
            < 3 => (0d, chroma, neighboring),
            < 4 => (0d, neighboring, chroma),
            < 5 => (neighboring, 0d, chroma),
            _ => (chroma, 0d, neighboring)
        };

        return Color.FromRgb(ToByte(red + neutral), ToByte(green + neutral), ToByte(blue + neutral));
    }

    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
