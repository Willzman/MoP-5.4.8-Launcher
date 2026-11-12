using System.Windows;
using System.Windows.Media;

namespace MoPLauncher;

// Blossom petals that come off the tree in the background art and blow
// across the window. Sits directly over the artwork and under the rest of
// the UI, and follows the artwork's UniformToFill scaling so petals always
// start at the tree's canopy.
public sealed class PetalLayer : FrameworkElement
{
    // Size of background.jpg, and where the tree's canopy is in it.
    private const double ArtWidth = 1920;
    private const double ArtHeight = 1080;
    private const double CanopyX = 235, CanopyY = 525, CanopyRadiusX = 210, CanopyRadiusY = 140;

    private const int Count = 70;

    private struct Petal
    {
        public double X, Y;            // position, in artwork pixels
        public double SpeedX, SpeedY;  // drift, artwork pixels per second
        public double Sway, SwayRate, Phase;
        public double Angle, Spin;     // radians, radians per second
        public double Flip, FlipRate;  // tumble, shown as the petal thinning
        public double Size;
        public double Opacity;
        public double Age;
        public int Color;
    }

    private static readonly Brush[] Colors = CreateBrushes(
        Color.FromRgb(250, 196, 204), Color.FromRgb(255, 214, 218),
        Color.FromRgb(244, 170, 180), Color.FromRgb(236, 140, 160));

    private readonly Petal[] _petals = new Petal[Count];
    private readonly Random _random = new(548);
    private TimeSpan _lastFrame;
    private double _time;

    public PetalLayer()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;

        // Start with petals already in the air rather than an empty sky.
        for (var i = 0; i < _petals.Length; i++)
        {
            Spawn(ref _petals[i]);
            var head = _random.NextDouble() * (ArtWidth / _petals[i].SpeedX);
            _petals[i].X += _petals[i].SpeedX * head;
            _petals[i].Y += _petals[i].SpeedY * head;
            _petals[i].Age = head;
            if (IsGone(_petals[i]))
                Spawn(ref _petals[i]);
        }

        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    private static Brush[] CreateBrushes(params Color[] colors)
    {
        var brushes = new Brush[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            brushes[i] = new SolidColorBrush(colors[i]);
            brushes[i].Freeze();
        }

        return brushes;
    }

    private double Range(double min, double max) => min + _random.NextDouble() * (max - min);

    private void Spawn(ref Petal petal)
    {
        // A random point inside the canopy.
        var angle = Range(0, Math.PI * 2);
        var distance = Math.Sqrt(_random.NextDouble());
        petal.X = CanopyX + Math.Cos(angle) * distance * CanopyRadiusX;
        petal.Y = CanopyY + Math.Sin(angle) * distance * CanopyRadiusY;

        petal.SpeedX = Range(55, 150);
        petal.SpeedY = Range(10, 42);
        petal.Sway = Range(10, 32);
        petal.SwayRate = Range(0.6, 1.6);
        petal.Phase = Range(0, Math.PI * 2);
        petal.Angle = Range(0, Math.PI * 2);
        petal.Spin = Range(-1.8, 1.8);
        petal.Flip = Range(0, Math.PI * 2);
        petal.FlipRate = Range(1.2, 3.6);
        petal.Size = Range(5, 12);
        petal.Opacity = Range(0.5, 0.9);
        petal.Age = 0;
        petal.Color = _random.Next(Colors.Length);
    }

    private static bool IsGone(in Petal petal) => petal.X > ArtWidth + 40 || petal.Y > ArtHeight + 40;

    private void OnFrame(object? sender, EventArgs e)
    {
        // Rendering can fire more than once per frame; only move on new ones.
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame)
            return;

        var elapsed = _lastFrame == TimeSpan.Zero ? 0 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        // Stand still while hidden, and for people who turned animations off.
        if (!IsVisible || !SystemParameters.ClientAreaAnimation)
            return;

        // After a pause (minimized, dragged) carry on rather than jump.
        var dt = Math.Min(elapsed, 0.05);
        _time += dt;

        // The wind comes in gusts: two slow waves on top of a steady breeze.
        var gust = 1 + 0.35 * Math.Sin(_time * 0.37) + 0.2 * Math.Sin(_time * 1.13 + 1.7);

        for (var i = 0; i < _petals.Length; i++)
        {
            ref var petal = ref _petals[i];
            petal.Age += dt;
            petal.X += petal.SpeedX * gust * dt;
            // Flutter up and down, and get lifted a little by stronger gusts.
            petal.Y += (petal.SpeedY + petal.Sway * Math.Cos(petal.Age * petal.SwayRate + petal.Phase)
                        - 22 * (gust - 1)) * dt;
            petal.Angle += petal.Spin * gust * dt;
            petal.Flip += petal.FlipRate * dt;

            if (IsGone(petal))
                Spawn(ref petal);
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        // Same mapping as the artwork: UniformToFill, centered.
        var scale = Math.Max(ActualWidth / ArtWidth, ActualHeight / ArtHeight);
        var offsetX = (ActualWidth - ArtWidth * scale) / 2;
        var offsetY = (ActualHeight - ArtHeight * scale) / 2;

        foreach (ref readonly var petal in _petals.AsSpan())
        {
            // Fade in as it leaves the branch.
            var opacity = petal.Opacity * Math.Min(1, petal.Age / 0.8);
            var radiusX = petal.Size * scale;
            var radiusY = petal.Size * 0.45 * scale * Math.Max(0.25, Math.Abs(Math.Cos(petal.Flip)));

            drawingContext.PushOpacity(opacity);
            drawingContext.PushTransform(new TranslateTransform(offsetX + petal.X * scale, offsetY + petal.Y * scale));
            drawingContext.PushTransform(new RotateTransform(petal.Angle * 180 / Math.PI));
            drawingContext.DrawEllipse(Colors[petal.Color], null, new Point(0, 0), radiusX, radiusY);
            drawingContext.Pop();
            drawingContext.Pop();
            drawingContext.Pop();
        }
    }
}
