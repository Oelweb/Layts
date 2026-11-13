using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Layts.Core;

/// <summary>
/// Kleine Animations-Helfer für XAML:
///   core:Anim.FadeIn="True"  – blendet ein und gleitet leicht nach oben (beim Laden und beim Sichtbarwerden)
///   core:Anim.Delay="80"     – Verzögerung in ms (für gestaffelte Karten)
///   core:Anim.Pop="True"     – springt beim Sichtbarwerden kurz auf (z. B. Häkchen)
/// </summary>
public static class Anim
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction Spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 };

    // ---------- FadeIn ----------

    public static readonly DependencyProperty FadeInProperty = DependencyProperty.RegisterAttached(
        "FadeIn", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnFadeInChanged));

    public static bool GetFadeIn(DependencyObject o) => (bool)o.GetValue(FadeInProperty);
    public static void SetFadeIn(DependencyObject o, bool v) => o.SetValue(FadeInProperty, v);

    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached(
        "Delay", typeof(int), typeof(Anim), new PropertyMetadata(0));

    public static int GetDelay(DependencyObject o) => (int)o.GetValue(DelayProperty);
    public static void SetDelay(DependencyObject o, int v) => o.SetValue(DelayProperty, v);

    public static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(Anim), new PropertyMetadata(14.0));

    public static double GetOffset(DependencyObject o) => (double)o.GetValue(OffsetProperty);
    public static void SetOffset(DependencyObject o, double v) => o.SetValue(OffsetProperty, v);

    private static void OnFadeInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el || e.NewValue is not true) return;
        el.Loaded += (_, _) => PlayFadeIn(el);
        el.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true && el.IsLoaded) PlayFadeIn(el);
        };
    }

    private static void PlayFadeIn(FrameworkElement el)
    {
        if (!el.IsVisible) return;

        // Ohne feste Verzögerung: automatisch nach Position im Panel staffeln (Karten erscheinen nacheinander).
        int ms = GetDelay(el);
        if (ms == 0 && VisualTreeHelper.GetParent(el) is System.Windows.Controls.Panel panel)
            ms = Math.Min(panel.Children.IndexOf(el), 6) * 60;
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        var duration = TimeSpan.FromMilliseconds(320);

        var move = EnsureTransform<TranslateTransform>(el);
        el.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = EaseOut };
        fade.Completed += (_, _) =>
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
        };
        el.BeginAnimation(UIElement.OpacityProperty, fade);
        move.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(GetOffset(el), 0, duration) { BeginTime = delay, EasingFunction = EaseOut });
    }

    // ---------- Pop ----------

    public static readonly DependencyProperty PopProperty = DependencyProperty.RegisterAttached(
        "Pop", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnPopChanged));

    public static bool GetPop(DependencyObject o) => (bool)o.GetValue(PopProperty);
    public static void SetPop(DependencyObject o, bool v) => o.SetValue(PopProperty, v);

    private static void OnPopChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el || e.NewValue is not true) return;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true) PlayPop(el);
        };
        el.Loaded += (_, _) =>
        {
            if (el.IsVisible) PlayPop(el);
        };
    }

    private static void PlayPop(FrameworkElement el)
    {
        var scale = EnsureTransform<ScaleTransform>(el);
        var duration = TimeSpan.FromMilliseconds(420);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.4, 1, duration) { EasingFunction = Spring });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, 1, duration) { EasingFunction = Spring });
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    // ---------- Hilfen ----------

    /// <summary>Holt (oder ergänzt) eine Transformation, ohne vorhandene zu verlieren.</summary>
    private static T EnsureTransform<T>(UIElement el) where T : Transform, new()
    {
        switch (el.RenderTransform)
        {
            case T existing when !existing.IsFrozen:
                return existing;
            case TransformGroup group when !group.IsFrozen:
            {
                var found = group.Children.OfType<T>().FirstOrDefault(t => !t.IsFrozen);
                if (found != null) return found;
                var added = new T();
                group.Children.Add(added);
                return added;
            }
        }

        var created = new T();
        if (el.RenderTransform is { } current && current != Transform.Identity)
        {
            var group = new TransformGroup();
            group.Children.Add(current.IsFrozen ? current.Clone() : current);
            group.Children.Add(created);
            el.RenderTransform = group;
        }
        else
        {
            el.RenderTransform = created;
        }
        return created;
    }
}
