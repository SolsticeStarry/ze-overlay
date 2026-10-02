
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

public class CtcDecoderTests
{
    private static readonly string[] Characters = ["blank", "a", "b", " "];

    [Fact]
    public void Decode_SkipsBlankAndRepeatedClasses()
    {
        // timeSteps=5, classes=4：blank, a, a, blank, b -> "ab"
        float[] logits =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 1, 0, 0,
            1, 0, 0, 0,
            0, 0, 1, 0,
        ];

        var (text, confidence) = Decoder.Decode(logits, 5, 4, Characters);

        Assert.Equal("ab", text);
        Assert.Equal(1.0, confidence, 3);
    }

    [Fact]
    public void Decode_AllBlank_ReturnsEmptyWithZeroConfidence()
    {
        float[] logits = new float[3 * 4]; // 全 0 ⇒ argmax 恒为 blank(0)

        var (text, confidence) = Decoder.Decode(logits, 3, 4, Characters);

        Assert.Equal(string.Empty, text);
        Assert.Equal(0.0, confidence, 3);
    }

    [Fact]
    public void Decode_UsesOnlyTheGivenSampleBuffer()
    {
        // 已切片好的样本：空格 然后 b
        float[] sample = [0, 0, 0, 1, 0, 0, 1, 0];

        var (text, _) = Decoder.Decode(sample, 2, 4, Characters);

        Assert.Equal(" b", text);
    }
}
