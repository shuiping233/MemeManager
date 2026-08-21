namespace MemeManager.Infrastructure;

public static class HotKeyUtils
{
    public static void ReRegisterHotKey(IntPtr _hWnd, int HOTKEY_ID, uint HotKeyModifiers, ushort HotKeyVk)
    {
        NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_ID);
        NativeMethods.RegisterHotKey(_hWnd, HOTKEY_ID, HotKeyModifiers, HotKeyVk);
    }

    public static void UnregisterHotKey(IntPtr _hWnd, int HOTKEY_ID)
    {
        NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_ID);
    }

    /// <summary>
    /// 当前配置的快捷键文本，如 "Ctrl+Alt+." / "Ctrl+B" / "Ctrl+F8"
    /// </summary>
    public static string ToText(uint modifiers, ushort vk)
    {
        List<string> parts = [];
        if ((modifiers & 0x8) != 0) parts.Add("Win");
        if ((modifiers & 0x1) != 0) parts.Add("Alt");
        if ((modifiers & 0x2) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x4) != 0) parts.Add("Shift");

        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private static string KeyName(ushort vk)
    {
        // 常见按键手动映射（GetKeyNameText 依赖扫描码，部分组合会返回空）
        switch (vk)
        {
            case 0x41: return "A";
            case 0x42: return "B";
            case 0x43: return "C";
            case 0x44: return "D";
            case 0x45: return "E";
            case 0x46: return "F";
            case 0x47: return "G";
            case 0x48: return "H";
            case 0x49: return "I";
            case 0x4A: return "J";
            case 0x4B: return "K";
            case 0x4C: return "L";
            case 0x4D: return "M";
            case 0x4E: return "N";
            case 0x4F: return "O";
            case 0x50: return "P";
            case 0x51: return "Q";
            case 0x52: return "R";
            case 0x53: return "S";
            case 0x54: return "T";
            case 0x55: return "U";
            case 0x56: return "V";
            case 0x57: return "W";
            case 0x58: return "X";
            case 0x59: return "Y";
            case 0x5A: return "Z";
            case 0x30: return "0";
            case 0x31: return "1";
            case 0x32: return "2";
            case 0x33: return "3";
            case 0x34: return "4";
            case 0x35: return "5";
            case 0x36: return "6";
            case 0x37: return "7";
            case 0x38: return "8";
            case 0x39: return "9";
            case >= 0x70 and <= 0x87: return "F" + (vk - 0x6F); // F1..F24
        }

        // 其余按键用 GetKeyNameText（正确构造 lParam：扫描码 + 扩展键位）
        uint scan = NativeMethods.MapVirtualKey(vk, 0); // MAPVK_VK_TO_VSC
        // 扩展键（方向键、小键盘、右 Ctrl/Alt 等）需要第 24 位
        bool extended = vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 // 翻页/方向
            or 0x2D or 0x2E or 0x2F or 0x6A or 0x6B or 0x6C or 0x6D or 0xA3 or 0xA4 or 0xA5; // Ins/Del/Home/End + 右修饰键
        int lParam = ((int)scan << 16) | (extended ? 0x01000000 : 0);

        var sb = new System.Text.StringBuilder(64);
        if (NativeMethods.GetKeyNameTextW(lParam, sb, sb.Capacity) > 0 && sb.Length > 0)
        {
            // 去掉可能存在的 “(数字键盘)” 等冗余描述，保留简洁名
            return sb.ToString().Replace(" (数字键盘)", "").Replace(" (小键盘)", "").Trim();
        }

        // OEM / 标点等：用 OEM 映射表自行推断
        return vk switch
        {
            0xBE => ".",
            0xBC => ",",
            0xBB => "=",
            0xBD => "-",
            0xBA => ";",
            0xDE => "'",
            0xC0 => "`",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xE2 => "\\",
            _ => "0x" + vk.ToString("X2")
        };
    }
}

