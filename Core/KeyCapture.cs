using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Layts.Core;

/// <summary>
/// Turns a read-only TextBox into a "press a key" field: <c>core:KeyCapture.Key="{Binding Key}"</c>.
/// Click it, press any key, done. Backspace/Delete clears it.
/// </summary>
public static class KeyCapture
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(int), typeof(KeyCapture),
        // Default -1 so that a bound value of 0 ("no key yet") still triggers the hook-up.
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnKeyChanged));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, object> Hooked = new();

    public static int GetKey(DependencyObject d) => (int)d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, int value) => d.SetValue(KeyProperty, value);

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        if (!Hooked.TryGetValue(box, out _))
        {
            Hooked.Add(box, new object());
            box.IsReadOnly = true;
            box.IsReadOnlyCaretVisible = false;
            box.Cursor = Cursors.Hand;
            box.PreviewKeyDown += OnPreviewKeyDown;
            box.GotKeyboardFocus += (_, _) => box.Text = Loc.Get("Wh_PressKey");
            box.LostKeyboardFocus += (_, _) => box.Text = WheelMapper.KeyName(GetKey(box));
        }
        box.Text = WheelMapper.KeyName((int)e.NewValue);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) return; // keep keyboard navigation
        e.Handled = true;
        if (key is Key.Back or Key.Delete)
        {
            SetKey(box, 0);
        }
        else
        {
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0) return;
            SetKey(box, vk);
            box.Text = WheelMapper.KeyName(vk);
        }
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(box), null);
    }
}
