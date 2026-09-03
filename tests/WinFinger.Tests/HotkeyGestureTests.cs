using System.Windows.Input;
using WinFinger.Controls;
using WinFinger.Services;
using Xunit;

namespace WinFinger.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, Key.V, "Ctrl+Shift+V")]
    [InlineData(ModifierKeys.Control, Key.D1, "Ctrl+1")]
    [InlineData(ModifierKeys.Alt, Key.Space, "Alt+Space")]
    [InlineData(ModifierKeys.Windows | ModifierKeys.Shift, Key.S, "Shift+Win+S")]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.OemComma, "Ctrl+Alt+,")]
    [InlineData(ModifierKeys.Control, Key.NumPad7, "Ctrl+7")]
    public void Build_returns_canonical_gesture(ModifierKeys mods, Key key, string expected)
    {
        Assert.Equal(expected, HotkeyGesture.Build(mods, key));
    }

    [Fact]
    public void Build_orders_modifiers_consistently()
    {
        Assert.Equal("Ctrl+Shift+Alt+Win+K",
            HotkeyGesture.Build(ModifierKeys.Windows | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Control, Key.K));
    }

    [Fact]
    public void Function_keys_need_no_modifier()
    {
        Assert.Equal("F5", HotkeyGesture.Build(ModifierKeys.None, Key.F5));
        Assert.Equal("Ctrl+F12", HotkeyGesture.Build(ModifierKeys.Control, Key.F12));
        Assert.Equal("Shift+F2", HotkeyGesture.Build(ModifierKeys.Shift, Key.F2)); // F 键上 Shift 单独可用
    }

    [Theory]
    [InlineData(ModifierKeys.None, Key.A)]      // 单键会抢走全局输入
    [InlineData(ModifierKeys.None, Key.Space)]
    [InlineData(ModifierKeys.Shift, Key.A)]     // 只按 Shift 会抢走普通打字
    [InlineData(ModifierKeys.Shift, Key.D1)]
    [InlineData(ModifierKeys.Control, Key.LeftShift)] // 修饰键不能当主键
    [InlineData(ModifierKeys.Control, Key.None)]
    [InlineData(ModifierKeys.Control, Key.CapsLock)]  // 不支持的主键
    public void Build_rejects_invalid_combinations(ModifierKeys mods, Key key)
    {
        Assert.Null(HotkeyGesture.Build(mods, key));
    }

    [Fact]
    public void Built_gestures_round_trip_through_HotkeyService()
    {
        foreach (var key in new[] { Key.V, Key.D9, Key.OemPeriod, Key.PageDown, Key.F3 })
        {
            var gesture = HotkeyGesture.Build(ModifierKeys.Control | ModifierKeys.Shift, key);
            Assert.NotNull(gesture);
            Assert.True(HotkeyService.TryParse(gesture!, out _, out uint vk));
            Assert.NotEqual(0u, vk);
        }
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RWin)]
    [InlineData(Key.System)]
    public void IsModifierKey_detects_standalone_modifiers(Key key) =>
        Assert.True(HotkeyGesture.IsModifierKey(key));

    [Fact]
    public void IsModifierKey_is_false_for_normal_keys() =>
        Assert.False(HotkeyGesture.IsModifierKey(Key.V));
}
