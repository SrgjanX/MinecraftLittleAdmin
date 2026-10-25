using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace MinecraftLittleAdmin;

/// <summary>
/// Finds the Minecraft for Windows window and types a chat command into it,
/// so nobody has to type "/connect" by hand.
/// </summary>
public static class MinecraftWindow
{
    private static readonly string[] WindowTitles = ["Minecraft", "Minecraft Preview"];

    public static IntPtr Find()
    {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            var title = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            if (WindowTitles.Contains(title.ToString().Trim()))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Brings Minecraft to the front, opens chat with T, pastes the command and presses Enter.
    /// The command is pasted rather than typed so it works with any keyboard layout.
    /// </summary>
    public static async Task SendChatCommandAsync(IntPtr hwnd, string command, bool closeMenuFirst)
    {
        Clipboard.SetText(command);

        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
        await Task.Delay(500);

        if (closeMenuFirst)
        {
            PressKey(VK_ESCAPE);
            await Task.Delay(400);
        }

        PressKey(VK_T);
        await Task.Delay(500);

        KeyDown(VK_CONTROL);
        PressKey(VK_V);
        KeyUp(VK_CONTROL);
        await Task.Delay(300);

        PressKey(VK_RETURN);
    }

    private static void PressKey(ushort vk)
    {
        KeyDown(vk);
        Thread.Sleep(30);
        KeyUp(vk);
    }

    private static void KeyDown(ushort vk) => SendKey(vk, 0);

    private static void KeyUp(ushort vk) => SendKey(vk, KEYEVENTF_KEYUP);

    private static void SendKey(ushort vk, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC),
                    dwFlags = flags,
                },
            },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private const int SW_RESTORE = 9;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MAPVK_VK_TO_VSC = 0;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_T = 0x54;
    private const ushort VK_V = 0x56;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // The union must include MOUSEINPUT (its largest member) so INPUT has the size Windows expects.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
