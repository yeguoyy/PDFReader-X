using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PDFReaderX.App.Services;

/// <summary>注册 .pdfrx 文件关联（HKCU，无需管理员权限），使“打开方式”可选 PDFReader X。</summary>
public static class FileAssociationHelper
{
    private const string Extension = ".pdfrx";
    private const string ProgId = "PDFReaderX.Pdfrx";

    /// <summary>把当前 exe 注册为 .pdfrx 的打开方式（启动时调用，exe 路径变化会自动更新）。</summary>
    public static void RegisterPdfrxAssociation()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }
            using (var progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                progIdKey.SetValue(null, "PDFReader X 批注文档");
                using (var iconKey = progIdKey.CreateSubKey("DefaultIcon"))
                {
                    iconKey.SetValue(null, $"\"{exePath}\",0");
                }
                using (var commandKey = progIdKey.CreateSubKey(@"shell\open\command"))
                {
                    commandKey.SetValue(null, $"\"{exePath}\" \"%1\"");
                }
            }
            using (var openWithKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Extension}\OpenWithProgids"))
            {
                openWithKey.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
            using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Extension}");
            extKey.SetValue(null, ProgId);
        }
        catch
        {
            // 注册失败不影响主流程
        }
        // 通知资源管理器关联已变更，立即刷新文件图标与“打开方式”列表
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;
}