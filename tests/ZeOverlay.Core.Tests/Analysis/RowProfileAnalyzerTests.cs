using ZeOverlay.Core.Analysis;
using ZeOverlay.Core.Imaging;

namespace ZeOverlay.Core.Tests.Analysis;

public class RowProfileAnalyzerTests
{
    private const int Background = 100;

    [Fact]
    public void Analyze_FindsEachSyntheticTextRow()
    {
        // 5 行「文字」：每行 10 px 高、间隔 10 px，行内黑白交替制造强水平梯度。
        ImageFrame frame = BuildStripedRows(
            width: 200,
            height: 120,
            rowTops: [10, 30, 50, 70, 90],
            rowHeight: 10);

        RowProfileReport report = RowProfileAnalyzer.Analyze(frame);

        Assert.Equal(5, report.Bands.Count);

        for (int i = 0; i < 5; i++)
        {
            RowBand band = report.Bands[i];
            Assert.Equal(10 + i * 20, band.Top);
            Assert.Equal(19 + i * 20, band.Bottom);
            Assert.Equal(10, band.Height);
        }
    }

    [Fact]
    public void Analyze_ComputesMedianPitch()
    {
        ImageFrame frame = BuildStripedRows(200, 120, [10, 30, 50, 70, 90], 10);

        RowProfileReport report = RowProfileAnalyzer.Analyze(frame);

        Assert.Equal(20, report.MedianPitch);
    }

    [Fact]
    public void Analyze_ExtentCoversFullSyntheticWidth()
    {
        // 合成文字横跨整幅宽度，因此每行横向范围应为整幅。
        ImageFrame frame = BuildStripedRows(200, 120, [10, 40], 10);

        RowProfileReport report = RowProfileAnalyzer.Analyze(frame);

        Assert.All(report.Bands, band =>
        {
            Assert.Equal(0, band.Left);
            Assert.Equal(199, band.Right);
        });
    }

    [Fact]
    public void Analyze_UniformFrame_ReturnsNoBands()
    {
        ImageFrame frame = BuildSolid(120, 80, Background);

        RowProfileReport report = RowProfileAnalyzer.Analyze(frame);

        Assert.Empty(report.Bands);
        Assert.Null(report.MedianPitch);
    }

    [Fact]
    public void Analyze_IgnoresBandsShorterThanMinimumHeight()
    {
        // 1 px 高的「线」应被最小行高过滤掉。
        ImageFrame frame = BuildStripedRows(200, 60, [20], rowHeight: 1, minGapRows: 0);

        RowProfileReport report = RowProfileAnalyzer.Analyze(frame, minBandHeight: 5);

        Assert.Empty(report.Bands);
    }

    [Fact]
    public void Analyze_ReportsImageSizeAndMeanLuminance()
    {
        ImageFrame frame = BuildSolid(120, 80, Background);

        RowProfileReport report = RowProfileAnalyzer.Analyze(frame);

        Assert.Equal(120, report.Width);
        Assert.Equal(80, report.Height);
        Assert.Equal(Background, report.MeanLuminance, 1);
    }

    [Fact]
    public void Analyze_SingleCluster_ReportsOneRegionWithoutWarning()
    {
        ImageFrame frame = BuildStripedRows(400, 120, [10, 40], 10);

        RowProfileReport report = RowProfileAnalyzer.Analyze(frame);

        Assert.Equal(1, report.RegionCount);
        Assert.Null(report.RegionWarning);
    }

    [Fact]
    public void Analyze_TwoDisjointClusters_ReportsTwoRegionsAndWarns()
    {
        // 模拟「ROI 框宽了」：左半是列表、右半是聊天栏，两者横向完全不相连。
        ImageFrame frame = BuildClusters(600, 120, [(20, 180), (400, 560)], [10, 40]);

        RowProfileReport report = RowProfileAnalyzer.Analyze(frame);

        Assert.Equal(2, report.RegionCount);
        Assert.NotNull(report.RegionWarning);
        Assert.Contains("横向区域", report.RegionWarning);

        // 两个区域都应在列方向上被检出，且互不重叠。
        Assert.True(report.Regions[0].Right < report.Regions[1].Left);
    }

    [Fact]
    public void Analyze_NearbyCluster_MergesIntoOneRegion()
    {
        // 模拟「场景里的亮物件（如白桶）混在列表旁边」：两者间隔 60 px，
        // 远小于「多框了聊天栏」那种间隔，应合并成一个区域，不误报。
        ImageFrame frame = BuildClusters(600, 120, [(20, 180), (240, 560)], [10, 40]);

        RowProfileReport report = RowProfileAnalyzer.Analyze(frame);

        Assert.Equal(1, report.RegionCount);
        Assert.Null(report.RegionWarning);
    }

    [Fact]
    public void Analyze_RejectsOutlierBand_AndReportsRobustRowCount()
    {
        // 4 行规律列表（行距 30）+ 1 行远处的噪声（模拟场景亮物件）
        ImageFrame frame = BuildStripedRows(200, 260, [10, 40, 70, 100, 200], 10);

        RowProfileReport report = RowProfileAnalyzer.Analyze(frame);

        Assert.Equal(5, report.Bands.Count);          // 检出 5 条
        Assert.Equal(4, report.RowCount);             // 稳健行数 4（剔除离群）
        Assert.Single(report.OutlierBands);
        Assert.Equal(4, report.GridBands.Count);
        Assert.NotNull(report.Grid);
        Assert.Equal(30, report.Grid.Pitch, 3);
    }

    /// <summary>在指定横向区间内画黑白交替的「文字」行，其余为纯背景。</summary>
    private static ImageFrame BuildClusters(int width, int height, (int Left, int Right)[] clusters, int[] rowTops)
    {
        byte[] pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            bool isTextRow = rowTops.Any(top => y >= top && y < top + 10);

            for (int x = 0; x < width; x++)
            {
                bool inCluster = clusters.Any(c => x >= c.Left && x < c.Right);
                int value = isTextRow && inCluster && x % 2 == 0
                    ? 255
                    : isTextRow && inCluster
                        ? 0
                        : Background;

                int i = (y * width + x) * 4;
                pixels[i] = (byte)value;
                pixels[i + 1] = (byte)value;
                pixels[i + 2] = (byte)value;
                pixels[i + 3] = 255;
            }
        }

        return ImageFrame.FromBgra(width, height, pixels);
    }

    private static ImageFrame BuildStripedRows(int width, int height, int[] rowTops, int rowHeight, int minGapRows = 2)
    {
        _ = minGapRows;
        byte[] pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            bool isText = rowTops.Any(top => y >= top && y < top + rowHeight);

            for (int x = 0; x < width; x++)
            {
                int value = isText && x % 2 == 0 ? 255 : isText ? 0 : Background;

                int i = (y * width + x) * 4;
                pixels[i] = (byte)value;
                pixels[i + 1] = (byte)value;
                pixels[i + 2] = (byte)value;
                pixels[i + 3] = 255;
            }
        }

        return ImageFrame.FromBgra(width, height, pixels);
    }

    private static ImageFrame BuildSolid(int width, int height, int value)
    {
        byte[] pixels = new byte[width * height * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)value;
            pixels[i + 1] = (byte)value;
            pixels[i + 2] = (byte)value;
            pixels[i + 3] = 255;
        }

        return ImageFrame.FromBgra(width, height, pixels);
    }
}
