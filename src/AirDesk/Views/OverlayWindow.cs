using System;
using System.Runtime.InteropServices;

namespace AirDesk;

internal static class OverlayWindow
{
    const int ExtendedStyle = -20;
    const int Passive = 0x20 | 0x80 | 0x08000000; // Transparent mouse input, tool window, no activation.

    public static void SetPassive(IntPtr window) => SetWindowLong(window, ExtendedStyle, GetWindowLong(window, ExtendedStyle) | Passive);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    static extern int SetWindowLong(IntPtr window, int index, int value);
}
