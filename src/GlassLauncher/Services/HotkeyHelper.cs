using System;
using System.Windows.Input;

namespace GlassLauncher.Services;

/// <summary>
/// 快捷键文本 ↔ Win32 RegisterHotKey 参数的换算。
/// 文本格式固定为 "Ctrl+Alt+G" 这种 修饰键+主键 的形式（修饰键在前、按 Ctrl/Alt/Shift/Win 排序），
/// 存进 settings.json 的 Hotkey 字段。
/// </summary>
public static class HotkeyHelper
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    /// <summary>解析设置里的快捷键文本。修饰键全空或主键缺失都算无效。</summary>
    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Key key = Key.None;

        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out key))
                    {
                        return false;
                    }

                    break;
            }
        }

        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return false;
        }

        try
        {
            virtualKey = unchecked((uint)KeyInterop.VirtualKeyFromKey(key));
        }
        catch
        {
            return false;
        }

        return virtualKey != 0;
    }

    /// <summary>
    /// 把设置页里按下的组合键转成文本。返回 null 表示无效组合：
    /// 按的还是修饰键本身，或者裸按键（没有修饰键且不是 F1~F12）——
    /// 裸全局热键会把那个键从所有程序里抢走，必须拦住。
    /// </summary>
    public static string? TryFromKeyboard(Key key, ModifierKeys pressedModifiers)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Tab
            or Key.Escape or Key.Delete or Key.Back)
        {
            return null;
        }

        uint modifiers = 0;
        if (pressedModifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= ModControl;
        }

        if (pressedModifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= ModAlt;
        }

        if (pressedModifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= ModShift;
        }

        if (pressedModifiers.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= ModWin;
        }

        bool functionKey = key is >= Key.F1 and <= Key.F12;
        if (modifiers == 0 && !functionKey)
        {
            return null;
        }

        return Format(modifiers, key);
    }

    public static string Format(uint modifiers, Key key)
    {
        var parts = new System.Collections.Generic.List<string>(5);

        if ((modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(DisplayName(key));
        return string.Join('+', parts);
    }

    private static string DisplayName(Key key)
    {
        // D1~D9/D0 显示成数字，其余用枚举名（Space、F5、G 等，够读）
        return key switch
        {
            Key.D0 => "0",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.D3 => "3",
            Key.D4 => "4",
            Key.D5 => "5",
            Key.D6 => "6",
            Key.D7 => "7",
            Key.D8 => "8",
            Key.D9 => "9",
            Key.Space => "Space",
            _ => key.ToString(),
        };
    }
}
