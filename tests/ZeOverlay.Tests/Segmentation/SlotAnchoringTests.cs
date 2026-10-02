using ZeOverlay.Shared;
using ZeOverlay.Stage.GlyphSegmentation;

namespace ZeOverlay.Tests.Segmentation;

/// <summary>
/// S2 按行带顺序给出槽位（只用于展示/排序）。社区服的**身份**改用「名称+标号」，
/// 因此这里不再要求槽位随绝对 Y 稳定。
/// </summary>
public sealed class SlotAnchoringTests
{
    [Fact]
    public void Slots_AreSequentialAcrossBands()
    {
        var stage = new GlyphSegmentationStage();
        ImageFrame frame = Frame();

        IReadOnlyList<RowCrop> crops = stage.Process(new RowInput(
            frame,
            Report(frame, Band(95, 105), Band(125, 135), Band(155, 165))));

        Assert.Equal(new[] { 0, 1, 2 }, crops.Select(c => c.Slot).ToArray());
    }

    private static ImageFrame Frame(int width = 100, int height = 400) => new()
    {
        Width = width,
        Height = height,
        Bgra = new byte[width * height * 4],
        CapturedAtUnixMs = 0,
    };

    private static RowBand Band(int top, int bottom, int left = 0, int right = 50)
        => new(0, top, bottom, left, right, 100, 100);

    private static RowReport Report(ImageFrame frame, params RowBand[] bands) => new()
    {
        Width = frame.Width,
        Height = frame.Height,
        MeanLuminance = 0,
        Threshold = 0,
        Bands = bands,
        Regions = [new InkRegion(0, 50)],
        Grid = new RowGrid(bands[0].CenterY, 30, bands.Length, bands.Length, bands.Length, [], []),
        GridBands = bands,
        OutlierBands = [],
    };
}
