
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

public class ImageFrameTests
{
    [Fact]
    public void FromBgra_RejectsUndersizedBuffer()
    {
        Assert.Throws<ArgumentException>(() => ImageFrame.FromBgra(4, 4, new byte[10]));
    }

    [Fact]
    public void FromBgra_RejectsNonPositiveSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageFrame.FromBgra(0, 4, new byte[64]));
    }

    [Fact]
    public void LuminanceAt_UsesBt601Weights()
    {
        // 纯白
        var white = ImageFrame.FromBgra(1, 1, [255, 255, 255, 255]);
        Assert.Equal(255, white.LuminanceAt(0, 0));

        // 纯黑
        var black = ImageFrame.FromBgra(1, 1, [0, 0, 0, 255]);
        Assert.Equal(0, black.LuminanceAt(0, 0));

        // 纯绿：0.587 * 255 ≈ 150
        var green = ImageFrame.FromBgra(1, 1, [0, 255, 0, 255]);
        Assert.Equal(149, green.LuminanceAt(0, 0));
    }

    [Fact]
    public void Crop_OutOfBounds_PadsWithTransparentBlack()
    {
        // 2x2 全红
        byte[] pixels =
        [
            0, 0, 255, 255, 0, 0, 255, 255,
            0, 0, 255, 255, 0, 0, 255, 255,
        ];
        var frame = ImageFrame.FromBgra(2, 2, pixels);

        // 从 (1,1) 取 3x3：只有左上角落在原图内。
        ImageFrame cropped = frame.Crop(1, 1, 3, 3);

        Assert.Equal(3, cropped.Width);
        Assert.Equal(3, cropped.Height);
        Assert.Equal(255, cropped.Bgra[2]);      // (0,0) 的 R 通道保留
        Assert.Equal(0, cropped.Bgra[3 * 4 + 2]); // 越界像素为 0
    }

    [Fact]
    public void Crop_PreservesTimestamp()
    {
        var frame = ImageFrame.FromBgra(2, 2, new byte[16], capturedAtUnixMs: 12345);

        Assert.Equal(12345, frame.Crop(0, 0, 1, 1).CapturedAtUnixMs);
    }

    [Fact]
    public void Stride_IsWidthTimesFour()
    {
        Assert.Equal(8, ImageFrame.FromBgra(2, 2, new byte[16]).Stride);
    }
}
