
using ZeOverlay.Shared;
using ZeOverlay.Infrastructure;
using ZeOverlay.Win32;
using ZeOverlay.Stage.ScreenCapture;
using ZeOverlay.Stage.RowAnalysis;
using ZeOverlay.Stage.GlyphSegmentation;
using ZeOverlay.Stage.Recognition;
using ZeOverlay.Stage.Parsing;
using ZeOverlay.Stage.Tracking;
using ZeOverlay.Stage.Matching;

namespace ZeOverlay.Tests;

public class PpOcrInputTests
{
    [Theory]
    [InlineData(384, 36, 512)]   // 48 * 384/36
    [InlineData(10, 100, 8)]     // 4.8 -> 下限 8
    [InlineData(4000, 20, 1200)] // 9600 -> 上限 1200
    public void NaturalWidth_Clamps_ToRange(int cropWidth, int cropHeight, int expected)
        => Assert.Equal(expected, PpOcrInput.NaturalWidth(cropWidth, cropHeight));

    [Fact]
    public void Fill_NormalizesChannelsAndPadsRight()
    {
        const int cropWidth = 10;
        const int cropHeight = 10;
        int targetWidth = 64;

        // 纯蓝（BGRA：B=255, G=0, R=0, A=255）
        var bgra = new byte[cropWidth * cropHeight * 4];
        for (int i = 0; i < cropWidth * cropHeight; i++)
        {
            bgra[i * 4] = 255;
            bgra[i * 4 + 3] = 255;
        }

        var crop = ImageFrame.FromBgra(cropWidth, cropHeight, bgra);
        var dst = new float[3 * PpOcrInput.TargetHeight * targetWidth];

        PpOcrInput.Fill(crop, dst, 0, targetWidth);

        int plane = PpOcrInput.TargetHeight * targetWidth;
        int width = PpOcrInput.NaturalWidth(cropWidth, cropHeight); // 48

        Assert.Equal(48, width);

        // B 通道归一化到 +1；G/R 到 -1
        Assert.Equal(1.0, dst[0 * plane], 3);
        Assert.Equal(-1.0, dst[1 * plane], 3);
        Assert.Equal(-1.0, dst[2 * plane], 3);

        // 右侧 padding（x >= width）补 0
        Assert.Equal(0.0, dst[0 * plane + width], 3);
        Assert.Equal(0.0, dst[0 * plane + (targetWidth - 1)], 3);

        // 其它通道的 padding 同样为 0
        Assert.Equal(0.0, dst[1 * plane + width], 3);
        Assert.Equal(0.0, dst[2 * plane + width], 3);
    }

    [Fact]
    public void Fill_WritesToTheRequestedBatchIndex()
    {
        var crop = ImageFrame.FromBgra(4, 4, new byte[4 * 4 * 4]);
        int targetWidth = 16;
        int plane = PpOcrInput.TargetHeight * targetWidth;

        var dst = new float[2 * 3 * plane];
        PpOcrInput.Fill(crop, dst, 1, targetWidth);

        // 第二个样本被写入（全 0 像素 ⇒ -1），第一个样本保持 0
        Assert.Equal(0.0, dst[0], 3);
        Assert.Equal(-1.0, dst[3 * plane], 3);
    }
}
