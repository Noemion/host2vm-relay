using Microsoft.Win32;

namespace Host2VMRelay;

/// <summary>The current user's Run entry is authoritative; do not recreate it at startup.</summary>
internal sealed class StartupRegistration(string keyPath = StartupRegistration.RunKey)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Host2VMRelay";
    public static string Command(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"')) throw new ArgumentException("启动程序路径无效。");
        string command = "\"" + executable + "\" --startup";
        if (command.Length > 260) throw new IOException("程序路径过长，无法注册 Windows 登录启动项。请将程序放到较短的路径。");
        return command;
    }
    public bool IsEnabled(string executable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return string.Equals(key?.GetValue(ValueName) as string, Command(executable), StringComparison.OrdinalIgnoreCase);
    }
    public void SetEnabled(bool enabled, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        if (enabled) key.SetValue(ValueName, Command(executable), RegistryValueKind.String);
        else if (string.Equals(key.GetValue(ValueName) as string, Command(executable), StringComparison.OrdinalIgnoreCase))
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        // Another portable copy may own the entry. Disabling this copy must not
        // delete another installation's registration.
        if (IsEnabled(executable) != enabled) throw new IOException("Windows 启动项未能更新，请检查注册表访问权限。");
    }
}
