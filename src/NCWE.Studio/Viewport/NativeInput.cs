// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;

namespace NCWE.Studio.Viewport;

internal static partial class NativeInput
{
    // Physical key positions (set 1 scan codes): the same keys on QWERTY (WASD)
    // and AZERTY (ZQSD), exactly like the game's default bindings.
    public const uint ScanForward = 0x11, ScanLeft = 0x1E, ScanBack = 0x1F, ScanRight = 0x20, ScanDown = 0x10, ScanUp = 0x12;
    public const uint ScanSpace = 0x39, ScanC = 0x2E;
    public const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;

    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int key);
    [LibraryImport("user32.dll")] private static partial uint MapVirtualKeyW(uint code, uint type);
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out Point point);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetCursorPos(int x, int y);
    [LibraryImport("user32.dll")] private static partial uint GetDpiForWindow(nint window);

    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }

    public static bool IsForeground(nint window) => GetForegroundWindow() == window;
    public static bool IsVirtualKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    // MAPVK_VSC_TO_VK_EX resolves the current keyboard layout.
    public static bool IsScanDown(uint scan) => IsVirtualKeyDown((int)MapVirtualKeyW(scan, 3));
    // The character a key types on the current layout ('\0' for keys without one): MAPVK_VK_TO_CHAR.
    public static char VirtualKeyChar(int key) => char.ToUpperInvariant((char)(MapVirtualKeyW((uint)key, 2) & 0x7FFF));
    // The same for a physical position (the movement keys: W A S D on QWERTY, Z Q S D on AZERTY).
    public static char ScanChar(uint scan) => VirtualKeyChar((int)MapVirtualKeyW(scan, 3));
    public static uint ScanOf(int key) => MapVirtualKeyW((uint)key, 0);
    public static Point Cursor() { GetCursorPos(out var p); return p; }
    public static void MoveCursor(Point p) => SetCursorPos(p.X, p.Y);
    public static double Dpi(nint window) => GetDpiForWindow(window) / 96.0;
    // ShowCursor keeps a per-thread display counter; normalize it to hidden/visible.
    public static void SetCursorVisible(bool visible)
    {
        if (visible) { for (int i = 0; i < 16 && ShowCursor(true) < 0; i++) { } }
        else { for (int i = 0; i < 16 && ShowCursor(false) >= 0; i++) { } }
    }
}
