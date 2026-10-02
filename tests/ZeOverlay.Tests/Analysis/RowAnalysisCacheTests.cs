
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

public class RowAnalysisCacheTests
{
    [Fact]
    public void IdenticalPixelsWithNewTimestamp_ReuseReport()
    {
        var cache = new Cache();
        var first = cache.Analyze(ImageFrame.FromBgra(16, 16, new byte[1024], 1), f => Analyzer.Analyze(f));
        var second = cache.Analyze(ImageFrame.FromBgra(16, 16, new byte[1024], 2), _ => throw new Exception("Should be cached"));
        Assert.Same(first, second);
    }

    [Fact]
    public void SinglePixelChange_AndDimensionChange_InvalidateCache()
    {
        var cache = new Cache();
        int calls = 0;
        RowReport Analyze(ImageFrame f) { calls++; return Analyzer.Analyze(f); }
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
        var cache = new Cache();
        var frame = ImageFrame.FromBgra(16, 16, new byte[1024]);
        Assert.Throws<InvalidOperationException>(() => cache.Analyze(frame, _ => throw new InvalidOperationException()));
        Assert.NotNull(cache.Analyze(frame, f => Analyzer.Analyze(f)));
    }
}
