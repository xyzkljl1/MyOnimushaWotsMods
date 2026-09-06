using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using REFrameworkNET.Attributes;

public static class HideConsole
{
    [ModuleInitializer]
    [PluginEntryPoint]
    public static void Main()
    {
        var window = GetConsoleWindow();
        if (window != IntPtr.Zero)
        {
            ShowWindow(window, 0);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}
