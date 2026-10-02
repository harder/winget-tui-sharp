$ErrorActionPreference = 'Stop'

$appId = 'harder.winget-tui-sharp'
$exe = $env:WTS_NOTIFICATION_EXE
if (-not $exe -or -not [IO.File]::Exists($exe)) {
    throw 'The notification executable is missing.'
}

$shortcutPath = $env:WTS_NOTIFICATION_SHORTCUT
if (-not $shortcutPath) {
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Windows\Start Menu\Programs\WinGet TUI.lnk'
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct NotificationPropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}

[StructLayout(LayoutKind.Explicit)]
public struct NotificationPropertyValue
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public IntPtr Value;
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INotificationPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out NotificationPropertyKey key);
    [PreserveSig] int GetValue(ref NotificationPropertyKey key, out NotificationPropertyValue value);
    [PreserveSig] int SetValue(ref NotificationPropertyKey key, ref NotificationPropertyValue value);
    [PreserveSig] int Commit();
}

public static class NotificationShortcutProperty
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetPropertyStoreFromParsingName(
        string path, IntPtr bindContext, uint flags, ref Guid interfaceId,
        out IntPtr store);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref NotificationPropertyValue propertyValue);

    public static void SetAppId(string path, string appId)
    {
        Guid iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        IntPtr pointer;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2, ref iid, out pointer));
        INotificationPropertyStore store;
        try { store = (INotificationPropertyStore)Marshal.GetTypedObjectForIUnknown(pointer, typeof(INotificationPropertyStore)); }
        finally { Marshal.Release(pointer); }
        try
        {
            var key = new NotificationPropertyKey {
                FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5
            };
            var value = new NotificationPropertyValue { Type = 31, Value = Marshal.StringToCoTaskMemUni(appId) };
            try
            {
                Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
                Marshal.ThrowExceptionForHR(store.Commit());
                NotificationPropertyValue stored;
                Marshal.ThrowExceptionForHR(store.GetValue(ref key, out stored));
                try
                {
                    if (stored.Type != 31 || Marshal.PtrToStringUni(stored.Value) != appId)
                        throw new InvalidOperationException("The shortcut app ID was not saved.");
                }
                finally { PropVariantClear(ref stored); }
            }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }
}
'@

$folder = Split-Path -Parent $shortcutPath
[IO.Directory]::CreateDirectory($folder) | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = Split-Path -Parent $exe
$shortcut.Save()
[NotificationShortcutProperty]::SetAppId($shortcutPath, $appId)

$message = $env:WTS_NOTIFICATION_MESSAGE
if (-not $message) { return }

[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null
$message = [Security.SecurityElement]::Escape($message)
$xml = [Windows.Data.Xml.Dom.XmlDocument]::new()
$xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>WinGet TUI</text><text>$message</text></binding></visual></toast>")
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
