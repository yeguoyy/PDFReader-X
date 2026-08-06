using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PDFReaderX.App.Helpers;

internal static class BitmapExtensions
{
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>把 System.Drawing.Image 转成可冻结的 BitmapSource（允许跨线程使用）。</summary>
    public static BitmapSource ToBitmapSource(this Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var bitmap = new Bitmap(image);
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }
}
