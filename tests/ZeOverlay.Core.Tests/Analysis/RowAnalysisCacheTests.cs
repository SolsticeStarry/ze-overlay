using ZeOverlay.Core.Analysis;
using ZeOverlay.Core.Imaging;

namespace ZeOverlay.Core.Tests.Analysis;

public class RowAnalysisCacheTests
{
    [Fact]
    public void IdenticalPixelsWithNewTimestamp_ReuseReport()
    {
        var cache = new RowAnalysisCache();
        var first = cache.Analyze(ImageFrame.FromBgra(16, 16, new byte[1024], 1), f => RowProfileAnalyzer.Analyze(f));
        var second = cache.Analyze(ImageFrame.FromBgra(16, 16, new byte[1024], 2), _ => throw new Exception("Should be cached"));
        Assert.Same(first, second);
    }

    [Fact]
    public void SinglePixelChange_AndDimensionChange_InvalidateCache()
    {
        var cache = new RowAnalysisCache();
        int calls = 0;
        RowProfileReport Analyze(ImageFrame f) { calls++; return RowProfileAnalyzer.Analyze(f); }
        cache.Analyze(ImageFrame.FromBgra(16, 16, new byte[1024]), Analyze);
        byte[] changed = new byte[1024];
        changed[1020] = 255;
        cache.Analyze(ImageFrame.FromBgra(16, 16, changed), Analyze);
        cache.Analyze(ImageFrame.FromBgra(32, 8, changed), Analyze);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void FailedAnalysis_RetriesSameFrame()
    {
        var cache = new RowAnalysisCache();
        var frame = ImageFrame.FromBgra(16, 16, new byte[1024]);
        Assert.Throws<InvalidOperationException>(() => cache.Analyze(frame, _ => throw new InvalidOperationException()));
        Assert.NotNull(cache.Analyze(frame, f => RowProfileAnalyzer.Analyze(f)));
    }
}
