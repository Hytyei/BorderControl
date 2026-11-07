using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BorderControl;

/// <summary>
/// Cubic-bezier easing (same maths as CSS cubic-bezier), so WPF animations can use strong custom curves.
/// Use with EasingMode="EaseIn" (the default here) so the curve is applied exactly as written.
/// </summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; } = 1;
    public double Y2 { get; set; } = 1;

    public CubicBezierEase() => EasingMode = EasingMode.EaseIn;

    public CubicBezierEase(double x1, double y1, double x2, double y2) : this()
    {
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
    }

    protected override Freezable CreateInstanceCore() => new CubicBezierEase(X1, Y1, X2, Y2);

    protected override double EaseInCore(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;

        // Solve x(s) = t for s with Newton–Raphson, falling back to bisection.
        double s = t;
        for (int i = 0; i < 8; i++)
        {
            double x = Bez(s, X1, X2) - t;
            double d = Deriv(s, X1, X2);
            if (Math.Abs(x) < 1e-6) return Bez(s, Y1, Y2);
            if (Math.Abs(d) < 1e-6) break;
            s -= x / d;
        }

        double lo = 0, hi = 1;
        s = t;
        while (hi - lo > 1e-6)
        {
            double x = Bez(s, X1, X2);
            if (x < t) lo = s; else hi = s;
            s = (lo + hi) / 2;
        }
        return Bez(s, Y1, Y2);
    }

    private static double Bez(double s, double p1, double p2) =>
        3 * (1 - s) * (1 - s) * s * p1 + 3 * (1 - s) * s * s * p2 + s * s * s;

    private static double Deriv(double s, double p1, double p2) =>
        3 * (1 - s) * (1 - s) * p1 + 6 * (1 - s) * s * (p2 - p1) + 3 * s * s * (1 - p2);
}

/// <summary>Shared motion tokens and helpers.</summary>
public static class Motion
{
    /// <summary>Strong ease-out for entrances and UI responses.</summary>
    public static readonly IEasingFunction EaseOut = Freeze(new CubicBezierEase(0.23, 1, 0.32, 1));

    /// <summary>Strong ease-in-out for things moving on screen.</summary>
    public static readonly IEasingFunction EaseInOut = Freeze(new CubicBezierEase(0.77, 0, 0.175, 1));

    /// <summary>iOS-like sheet curve.</summary>
    public static readonly IEasingFunction Drawer = Freeze(new CubicBezierEase(0.32, 0.72, 0, 1));

    /// <summary>CSS "ease" – for colour changes.</summary>
    public static readonly IEasingFunction Ease = Freeze(new CubicBezierEase(0.25, 0.1, 0.25, 1));

    /// <summary>Gentle sine for ambient, looping light.</summary>
    public static readonly IEasingFunction Sine = Freeze(new SineEase { EasingMode = EasingMode.EaseInOut });

    /// <summary>
    /// True when Windows "Animation effects" is turned off (Settings → Accessibility → Visual effects).
    /// Motion then becomes fades only; nothing moves or loops.
    /// </summary>
    public static bool Reduced => !SystemParameters.ClientAreaAnimation;

    public static void To(DependencyObject target, DependencyProperty prop, double to, int ms,
        IEasingFunction? ease = null, double? from = null, int delayMs = 0, Action? done = null)
    {
        var a = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(Math.Max(1, ms)),
            EasingFunction = ease ?? EaseOut,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        };
        if (from.HasValue) a.From = from;
        if (done != null) a.Completed += (_, _) => done();
        Begin(target, prop, a);
    }

    public static void Color(SolidColorBrush brush, Color to, int ms = 420) =>
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(to, TimeSpan.FromMilliseconds(Reduced ? 1 : ms)) { EasingFunction = Ease });

    public static void Color(GradientStop stop, Color to, int ms = 600) =>
        stop.BeginAnimation(GradientStop.ColorProperty,
            new ColorAnimation(to, TimeSpan.FromMilliseconds(Reduced ? 1 : ms)) { EasingFunction = Ease });

    /// <summary>Endless back-and-forth loop for ambient light. Skipped under reduced motion.</summary>
    public static void Drift(DependencyObject target, DependencyProperty prop, double from, double to, double seconds)
    {
        if (Reduced) return;
        Begin(target, prop, new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Sine,
        });
    }

    public static void Stop(DependencyObject target, DependencyProperty prop)
    {
        if (target is IAnimatable a) a.BeginAnimation(prop, null);
    }

    private static void Begin(DependencyObject target, DependencyProperty prop, AnimationTimeline anim)
    {
        if (target is IAnimatable a) a.BeginAnimation(prop, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private static IEasingFunction Freeze(EasingFunctionBase e)
    {
        e.Freeze();
        return e;
    }
}
