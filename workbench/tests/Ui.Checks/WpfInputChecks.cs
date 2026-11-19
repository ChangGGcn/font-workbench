using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace FontBook.Workbench.Ui.Checks;

internal static partial class Program
{
    // Offscreen windows must not take Win32 focus. This is WPF's focus notification step,
    // straight after the Win32 focus negotiation that an offscreen window can't perform.
    // It sets IsKeyboardFocusWithin and raises the real WPF keyboard focus events.
    private static void FocusInWpf(UIElement? element)
    {
        if (ReferenceEquals(Keyboard.FocusedElement, element))
            return;
        var changeFocus = typeof(KeyboardDevice).GetMethod("ChangeFocus", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, [typeof(DependencyObject), typeof(int)], modifiers: null)
            ?? throw new InvalidOperationException("本机 WPF 不支持离屏键盘焦点测试。");
        changeFocus.Invoke(Keyboard.PrimaryDevice, [element, Environment.TickCount]);
        Require(ReferenceEquals(Keyboard.FocusedElement, element), "WPF 没有把离屏焦点交给目标控件。");
    }

    private static void WpfMouseClick(UIElement target, ModifierKeys modifiers = ModifierKeys.None)
    {
        WithThreadModifiers(modifiers, () =>
        {
            try
            {
                RaiseMouse(Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent);
            }
            finally { RaiseMouse(Mouse.PreviewMouseUpEvent, Mouse.MouseUpEvent); }
        });

        void RaiseMouse(RoutedEvent preview, RoutedEvent bubble)
        {
            var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = preview
            };
            target.RaiseEvent(e);
            if (!e.Handled)
            {
                e.RoutedEvent = bubble;
                target.RaiseEvent(e);
            }
        }
    }

    private static void WpfKey(UIElement target, Key key, ModifierKeys modifiers)
    {
        var source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("目标控件尚未呈现，不能发送 WPF 键盘事件。");
        WithThreadModifiers(modifiers, () =>
        {
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            target.RaiseEvent(e);
            if (!e.Handled)
            {
                e.RoutedEvent = Keyboard.KeyDownEvent;
                target.RaiseEvent(e);
            }
        });
    }

    private static void WithThreadModifiers(ModifierKeys modifiers, Action action)
    {
        // GetKeyState (used by WPF Extended) reads the dispatcher's message-queue state.
        // No SendInput, cursor moves, or foreground-window changes. Never await with synthetic state.
        var original = new byte[256];
        Require(GetKeyboardState(original), "无法读取 UI 检查线程的键盘状态。");
        var state = (byte[])original.Clone();
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
            state[key] &= 0x7F;
        if ((modifiers & ModifierKeys.Shift) != 0)
            state[0x10] = state[0xA0] = 0x80;
        if ((modifiers & ModifierKeys.Control) != 0)
            state[0x11] = state[0xA2] = 0x80;
        Require(SetKeyboardState(state), "无法设置 UI 检查线程的修饰键状态。");
        try
        {
            Require(Keyboard.Modifiers == modifiers, "WPF 没有读取到测试线程设置的修饰键。");
            action();
        }
        finally
        {
            Require(SetKeyboardState(original), "无法恢复 UI 检查线程的键盘状态。");
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] state);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState([In] byte[] state);
}
