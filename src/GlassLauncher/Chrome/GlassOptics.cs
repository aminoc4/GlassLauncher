using System;
using System.Threading.Tasks;

namespace GlassLauncher.Chrome;

/// <summary>光学玻璃的外观参数。数值单位一律是 DIP（与 XAML 里看到的一致）。</summary>
internal sealed record GlassOpticsOptions
{
    /// <summary>圆角半径（超椭圆，不是圆弧）。</summary>
    public double Radius { get; init; } = 20;

    /// <summary>边缘透镜作用带宽度：只有离边缘这么近的像素会被折射。</summary>
    public double Band { get; init; } = 26;

    /// <summary>透镜最大位移（向外取景，所以能“看见窗外”的多一点背景）。</summary>
    public double Amplitude { get; init; } = 17;

    /// <summary>背景模糊的高斯 sigma。</summary>
    /// <remarks>
    /// macOS 的窗口不是"轻微磨砂"，是把背景糊成一片色雾（相当于 20px 级的模糊）。
    /// 太小会把壁纸的大色块原样透出来，看起来像一层玻璃贴纸而不是玻璃。
    /// </remarks>
    public double BlurSigma { get; init; } = 11;

    /// <summary>背景饱和度倍数（macOS 的玻璃会把背景“提鲜”）。</summary>
    public double Saturation { get; init; } = 0.9;

    /// <summary>染色强度。</summary>
    public double TintOpacity { get; init; } = 0.5;

    public byte TintR { get; init; } = 0xF6;
    public byte TintG { get; init; } = 0xF8;
    public byte TintB { get; init; } = 0xFC;

    /// <summary>顶部受光（白色叠加量）。</summary>
    public double SheenTop { get; init; } = 0.10;

    /// <summary>底部压暗（黑色叠加量）。</summary>
    public double SheenBottom { get; init; } = 0.05;

    /// <summary>整体强度；0 = 直接透出原始背景，1 = 完整效果。</summary>
    public double Strength { get; init; } = 1;

    public static GlassOpticsOptions Light { get; } = new();

    public static GlassOpticsOptions Dark { get; } = new()
    {
        TintR = 0x1C,
        TintG = 0x1D,
        TintB = 0x21,
        TintOpacity = 0.66,
        SheenTop = 0.07,
        SheenBottom = 0.12,
        Saturation = 1.2,
    };
}

/// <summary>
/// 把抓到的背景像素做成一整块“光学玻璃”。
///
/// 与 DWM 的区别：这里是自己算的，不依赖系统模糊，所以窗口失焦也不会变灰板。
/// 每一步都能对应到真实玻璃的物理行为：
///   · 模糊 + 提饱和  → 磨砂面把背景糊开，颜色反而更纯
///   · 染色           → 玻璃本身的体色
///   · 边缘位移       → 厚玻璃的侧面像透镜，把窗外的背景“折”进来
///   · 边缘高光/暗边  → 受光的棱，左上亮、右下暗
///   · 色散           → 蓝光比红光弯得多，边缘会有一丝彩边
/// </summary>
internal static class GlassOptics
{
    private const double Exponent = 2.6; // 超椭圆指数，苹果圆角的常用值

    /// <param name="source">带边距的背景像素（BGRA32，物理像素）。</param>
    /// <param name="captureWidth">带边距的宽。</param>
    /// <param name="captureHeight">带边距的高。</param>
    /// <param name="width">要输出的窗口宽（物理像素）。</param>
    /// <param name="height">要输出的窗口高。</param>
    /// <param name="padX">边距（源图里窗口左上角之前的像素数）。</param>
    /// <param name="padY">边距。</param>
    /// <param name="scale">每 DIP 多少物理像素。</param>
    public static byte[] Render(
        byte[] source,
        int captureWidth,
        int captureHeight,
        int width,
        int height,
        int padX,
        int padY,
        double scale,
        GlassOpticsOptions options)
    {
        // 背景是重模糊的，高频细节本来就不存在 —— 先降到 1/2 分辨率再模糊，
        // 采样、模糊的像素量都只剩四分之一，肉眼差别为零。边缘高光是解析算的，不受影响。
        const int downsample = 2;
        int smallWidth = Math.Max(1, captureWidth / downsample);
        int smallHeight = Math.Max(1, captureHeight / downsample);

        var small = new byte[smallWidth * smallHeight * 4];
        Downsample(small, smallWidth, smallHeight, source, captureWidth, captureHeight, downsample, options.Saturation);

        int blurRadius = Math.Max(1, (int)Math.Round(options.BlurSigma * scale / downsample));
        Blur(small, smallWidth, smallHeight, blurRadius);

        var output = new byte[width * height * 4];

        double dipWidth = width / scale;
        double dipHeight = height / scale;
        double dipPerPixel = 1.0 / scale;

        double radius = Math.Clamp(options.Radius, 0, Math.Min(dipWidth, dipHeight) / 2);
        double band = Math.Clamp(options.Band, 0.001, Math.Min(dipWidth, dipHeight) / 2);

        double tintWeight = options.TintOpacity * options.Strength;
        double tintR = options.TintR * tintWeight;
        double tintG = options.TintG * tintWeight;
        double tintB = options.TintB * tintWeight;

        Parallel.For(0, height, y =>
        {
            double dipY = (y + 0.5) * dipPerPixel;
            double sheenTop = options.SheenTop * (1 - (y + 0.5) / height) * options.Strength;
            double sheenBottom = options.SheenBottom * Math.Pow((y + 0.5) / height, 2) * options.Strength;

            for (int x = 0; x < width; x++)
            {
                double dipX = (x + 0.5) * dipPerPixel;

                double edgeX = Math.Min(dipX, dipWidth - dipX);
                double edgeY = Math.Min(dipY, dipHeight - dipY);

                double signedDistance;
                double normalX;
                double normalY;

                if (radius <= 0 || edgeX >= radius || edgeY >= radius)
                {
                    // 直边区域：距离可以直接算，没有三角函数
                    signedDistance = -Math.Min(edgeX, edgeY);
                    if (edgeX < edgeY)
                    {
                        normalX = dipX < dipWidth / 2 ? -1 : 1;
                        normalY = 0;
                    }
                    else
                    {
                        normalX = 0;
                        normalY = dipY < dipHeight / 2 ? -1 : 1;
                    }
                }
                else
                {
                    // 角部：超椭圆 |1-x/r|^n + |1-y/r|^n = 1 的近似欧氏距离 + 精确法线
                    double u = 1 - (edgeX / radius);
                    double v = 1 - (edgeY / radius);
                    double fu = Math.Pow(u, Exponent);
                    double fv = Math.Pow(v, Exponent);
                    double gu = Exponent * Math.Pow(u, Exponent - 1) / radius;
                    double gv = Exponent * Math.Pow(v, Exponent - 1) / radius;
                    double gradient = Math.Sqrt((gu * gu) + (gv * gv));

                    signedDistance = gradient > 1e-9 ? (fu + fv - 1) / gradient : -Math.Min(edgeX, edgeY);

                    double signX = dipX < dipWidth / 2 ? -1 : 1;
                    double signY = dipY < dipHeight / 2 ? -1 : 1;
                    normalX = signX * gu;
                    normalY = signY * gv;

                    double length = Math.Sqrt((normalX * normalX) + (normalY * normalY));
                    if (length > 1e-9)
                    {
                        normalX /= length;
                        normalY /= length;
                    }
                    else
                    {
                        normalX = 0;
                        normalY = 0;
                    }
                }

                // 1 像素抗锯齿：形状边界从 alpha 0 平滑过渡到 255
                double coverage = Math.Clamp(0.5 - (signedDistance / dipPerPixel), 0, 1);
                if (coverage <= 0)
                {
                    continue;
                }

                double depth = Math.Max(0, -signedDistance);
                double sampleX = x + padX;
                double sampleY = y + padY;
                double highlight = 0;
                double shade = 0;
                double split = 0;

                if (depth < band)
                {
                    // 透镜位移：越靠边越弯，t=1 时达到最大位移
                    double t = 1 - (depth / band);
                    double offset = (1 - Math.Sqrt(Math.Max(0, 1 - (t * t)))) * options.Amplitude * options.Strength;

                    sampleX += normalX * offset * scale;
                    sampleY += normalY * offset * scale;

                    // 0.75 DIP 的方向性白色描边
                    double stroke = Math.Clamp((0.75 + (dipPerPixel / 2) - depth) / dipPerPixel, 0, 1);
                    highlight = Math.Abs((normalX + normalY) / Math.Sqrt(2)) * 0.72 * stroke;

                    // 斜面受光：光来自左上，右下压暗
                    double bevel = Math.Pow(Math.Clamp(1 - (depth / 10), 0, 1), 2);
                    double light = (-0.6 * normalX) - (0.8 * normalY);
                    highlight += Math.Pow(Math.Max(0, light), 2) * 0.24 * bevel;
                    shade = Math.Pow(Math.Max(0, -light), 2) * 0.20 * bevel;

                    // 色散：只在有明显折射的地方出现
                    double refract = Math.Clamp(offset / 8, 0, 1);
                    double curvature = Math.Clamp(2 * Math.Abs(normalX * normalY), 0, 1);
                    split = Math.Min(0.75, offset * 0.035) * refract * refract * (0.65 + (0.35 * curvature)) * scale;
                }

                int index = ((y * width) + x) * 4;
                double red;
                double green;
                double blue;

                if (split > 0.2)
                {
                    // 蓝光比红光弯得多：B 往 -法线 偏，R 往 +法线 偏
                    red = Sample(small, smallWidth, smallHeight, (sampleX + (normalX * split)) / downsample, (sampleY + (normalY * split)) / downsample, 2);
                    green = Sample(small, smallWidth, smallHeight, sampleX / downsample, sampleY / downsample, 1);
                    blue = Sample(small, smallWidth, smallHeight, (sampleX - (normalX * split)) / downsample, (sampleY - (normalY * split)) / downsample, 0);
                }
                else
                {
                    red = Sample(small, smallWidth, smallHeight, sampleX / downsample, sampleY / downsample, 2);
                    green = Sample(small, smallWidth, smallHeight, sampleX / downsample, sampleY / downsample, 1);
                    blue = Sample(small, smallWidth, smallHeight, sampleX / downsample, sampleY / downsample, 0);
                }

                // 染色
                red = (red * (1 - tintWeight)) + tintR;
                green = (green * (1 - tintWeight)) + tintG;
                blue = (blue * (1 - tintWeight)) + tintB;

                // 棱的暗面与高光
                double darken = 1 - (shade * options.Strength);
                red *= darken;
                green *= darken;
                blue *= darken;

                double glow = highlight * options.Strength * 255;
                red += glow;
                green += glow;
                blue += glow;

                // 整体受光：上亮下暗，给玻璃一个厚度
                if (sheenTop > 0)
                {
                    red = (red * (1 - sheenTop)) + (255 * sheenTop);
                    green = (green * (1 - sheenTop)) + (255 * sheenTop);
                    blue = (blue * (1 - sheenTop)) + (255 * sheenTop);
                }

                if (sheenBottom > 0)
                {
                    red *= 1 - sheenBottom;
                    green *= 1 - sheenBottom;
                    blue *= 1 - sheenBottom;
                }

                output[index] = ToByte(red);
                output[index + 1] = ToByte(green);
                output[index + 2] = ToByte(blue);
                output[index + 3] = ToByte(coverage * 255);
            }
        });

        return output;
    }

    /// <summary>按块平均降到 1/n，同时把饱和度提上去（一步走完，少一次全图遍历）。</summary>
    private static void Downsample(
        byte[] target,
        int targetWidth,
        int targetHeight,
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int factor,
        double saturation)
    {
        int area = factor * factor;

        for (int y = 0; y < targetHeight; y++)
        {
            for (int x = 0; x < targetWidth; x++)
            {
                int sumB = 0;
                int sumG = 0;
                int sumR = 0;

                for (int dy = 0; dy < factor; dy++)
                {
                    int sy = Math.Min((y * factor) + dy, sourceHeight - 1);
                    int row = sy * sourceWidth * 4;

                    for (int dx = 0; dx < factor; dx++)
                    {
                        int sx = Math.Min((x * factor) + dx, sourceWidth - 1);
                        int index = row + (sx * 4);
                        sumB += source[index];
                        sumG += source[index + 1];
                        sumR += source[index + 2];
                    }
                }

                double blue = (double)sumB / area;
                double green = (double)sumG / area;
                double red = (double)sumR / area;

                if (saturation > 1.001)
                {
                    double luminance = (0.072 * blue) + (0.715 * green) + (0.213 * red);
                    blue = luminance + ((blue - luminance) * saturation);
                    green = luminance + ((green - luminance) * saturation);
                    red = luminance + ((red - luminance) * saturation);
                }

                int target_index = ((y * targetWidth) + x) * 4;
                target[target_index] = ToByte(blue);
                target[target_index + 1] = ToByte(green);
                target[target_index + 2] = ToByte(red);
                target[target_index + 3] = 255;
            }
        }
    }

    /// <summary>双线性取样（一条通道），坐标越界时钳到边缘。</summary>
    private static double Sample(byte[] data, int width, int height, double x, double y, int channel)
    {
        if (x < 0)
        {
            x = 0;
        }
        else if (x > width - 1)
        {
            x = width - 1;
        }

        if (y < 0)
        {
            y = 0;
        }
        else if (y > height - 1)
        {
            y = height - 1;
        }

        int x0 = (int)x;
        int y0 = (int)y;
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);

        double fx = x - x0;
        double fy = y - y0;

        int row0 = ((y0 * width) + x0) * 4;
        int row1 = ((y1 * width) + x0) * 4;

        double topLeft = data[row0 + channel];
        double topRight = data[row0 + ((x1 - x0) * 4) + channel];
        double bottomLeft = data[row1 + channel];
        double bottomRight = data[row1 + ((x1 - x0) * 4) + channel];

        double top = (topLeft * (1 - fx)) + (topRight * fx);
        double bottom = (bottomLeft * (1 - fx)) + (bottomRight * fx);

        return (top * (1 - fy)) + (bottom * fy);
    }

    /// <summary>三次盒式模糊逼近高斯（O(n)，与半径无关）。</summary>
    private static void Blur(byte[] data, int width, int height, int radius)
    {
        if (radius < 1)
        {
            return;
        }

        var scratch = new byte[data.Length];

        for (int pass = 0; pass < 3; pass++)
        {
            BoxHorizontal(data, scratch, width, height, radius);
            BoxVertical(scratch, data, width, height, radius);
        }
    }

    private static void BoxHorizontal(byte[] source, byte[] target, int width, int height, int radius)
    {
        int window = (radius * 2) + 1;

        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            int sumB = 0;
            int sumG = 0;
            int sumR = 0;
            int sumA = 0;

            for (int k = -radius; k <= radius; k++)
            {
                int px = row + (Math.Clamp(k, 0, width - 1) * 4);
                sumB += source[px];
                sumG += source[px + 1];
                sumR += source[px + 2];
                sumA += source[px + 3];
            }

            for (int x = 0; x < width; x++)
            {
                int px = row + (x * 4);
                target[px] = (byte)(sumB / window);
                target[px + 1] = (byte)(sumG / window);
                target[px + 2] = (byte)(sumR / window);
                target[px + 3] = (byte)(sumA / window);

                int add = row + (Math.Clamp(x + radius + 1, 0, width - 1) * 4);
                int remove = row + (Math.Clamp(x - radius, 0, width - 1) * 4);
                sumB += source[add] - source[remove];
                sumG += source[add + 1] - source[remove + 1];
                sumR += source[add + 2] - source[remove + 2];
                sumA += source[add + 3] - source[remove + 3];
            }
        }
    }

    private static void BoxVertical(byte[] source, byte[] target, int width, int height, int radius)
    {
        int window = (radius * 2) + 1;
        int stride = width * 4;

        for (int x = 0; x < width; x++)
        {
            int column = x * 4;
            int sumB = 0;
            int sumG = 0;
            int sumR = 0;
            int sumA = 0;

            for (int k = -radius; k <= radius; k++)
            {
                int py = column + (Math.Clamp(k, 0, height - 1) * stride);
                sumB += source[py];
                sumG += source[py + 1];
                sumR += source[py + 2];
                sumA += source[py + 3];
            }

            for (int y = 0; y < height; y++)
            {
                int py = column + (y * stride);
                target[py] = (byte)(sumB / window);
                target[py + 1] = (byte)(sumG / window);
                target[py + 2] = (byte)(sumR / window);
                target[py + 3] = (byte)(sumA / window);

                int add = column + (Math.Clamp(y + radius + 1, 0, height - 1) * stride);
                int remove = column + (Math.Clamp(y - radius, 0, height - 1) * stride);
                sumB += source[add] - source[remove];
                sumG += source[add + 1] - source[remove + 1];
                sumR += source[add + 2] - source[remove + 2];
                sumA += source[add + 3] - source[remove + 3];
            }
        }
    }

    private static byte ToByte(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value >= 255 ? (byte)255 : (byte)(value + 0.5);
    }
}
