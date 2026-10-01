using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeepSeekBalanceWidget.Tray;

/// <summary>
/// 动态托盘图标渲染（D-01，探针 A §4 实证的唯一干净路径）：
/// WPF DrawingVisual 画圆 → RenderTargetBitmap.CopyPixels → 手写最小 ICO
/// （ICONDIR + ICONDIRENTRY + 32bpp BGRA DIB + AND 掩码）落盘 → BitmapImage 文件 URI。
/// H.NotifyIcon 2.3.2 约束：IconSource→Icon 转换链只接受 ICO 容器流
/// （RenderTargetBitmap/裸 PNG 流均崩溃）；DIB 写入 R/B 通道顺序不可反（探针 A §4-3 蓝橙互换教训）。
/// 落盘位置 = 应用专属目录（D-02，随用随清），不落 %TEMP%、不落 exe 旁。
/// </summary>
public static class DynamicIconRenderer
{
    /// <summary>渲染 32×32 圆形图标（外圈深色环 + 内圈状态色）并写成 ICO 文件。</summary>
    public static void WriteCircleIco(string path, Color fill, Color ring)
    {
        const int size = 32;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(ring), null, new Point(16, 16), 15, 15);
            dc.DrawEllipse(new SolidColorBrush(fill), null, new Point(16, 16), 12, 12);
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var px = new byte[size * size * 4];
        bmp.CopyPixels(px, size * 4, 0);

        int andStride = size / 8;                 // 1bpp 掩码，每行 4 字节
        int andSize = andStride * size;
        int dibSize = 40 + px.Length + andSize;

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0);                      // reserved
        bw.Write((ushort)1);                      // type: icon
        bw.Write((ushort)1);                      // count
        bw.Write((byte)size); bw.Write((byte)size);
        bw.Write((byte)0); bw.Write((byte)0);     // colors, reserved
        bw.Write((ushort)1);                      // planes
        bw.Write((ushort)32);                     // bpp
        bw.Write(dibSize);
        bw.Write(6 + 16);                         // image offset

        bw.Write(40);                             // BITMAPINFOHEADER.biSize
        bw.Write(size);                           // width
        bw.Write(size * 2);                       // height = XOR + AND
        bw.Write((ushort)1); bw.Write((ushort)32);
        bw.Write(0u);                             // compression: BI_RGB
        bw.Write((uint)(px.Length + andSize));    // sizeImage
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

        // DIB 自下而上；CopyPixels 输出即 BGRA——通道顺序直写（R/B 不可互换，探针 A §4-3 教训）
        for (int y = size - 1; y >= 0; y--)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                bw.Write(px[i]); bw.Write(px[i + 1]); bw.Write(px[i + 2]); bw.Write(px[i + 3]);
            }
        }
        for (int y = 0; y < andSize; y++) bw.Write((byte)0);   // AND 掩码全 0，透明交给 alpha
    }

    /// <summary>ICO 文件 → 冻结的 BitmapImage（文件 URI + OnLoad，不持有文件句柄，D-02 随用随清前提）。</summary>
    public static BitmapImage LoadIco(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri(path, UriKind.Absolute);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>解码 ICO 中心像素（验证实际落盘文件的颜色，防 R/B 反色回归；冒烟取证用）。</summary>
    public static (byte R, byte G, byte B) DecodeIcoCenterPixel(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // 布局（本写入器固定）：6B ICONDIR + 16B entry + 40B BITMAPINFOHEADER + XOR(BGRA 自下而上) + AND
        const int xorOffset = 6 + 16 + 40;
        const int size = 32, stride = size * 4;
        int dibRow = size - 1 - 16;              // DIB 自下而上：px 行 16 → DIB 行 15
        int i = xorOffset + dibRow * stride + 16 * 4;
        return (bytes[i + 2], bytes[i + 1], bytes[i]);   // BGRA → RGB
    }
}
