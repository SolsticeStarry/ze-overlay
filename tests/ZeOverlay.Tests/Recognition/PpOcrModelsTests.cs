using ZeOverlay.Stage.Recognition;

namespace ZeOverlay.Tests.Recognition;

/// <summary>
/// <see cref="PpOcrModels"/> 的路径/标签解析：自动挑选顺序、按版本选字典、显式配置优先。
/// </summary>
public sealed class PpOcrModelsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zeoverlay-models-" + Guid.NewGuid().ToString("N"));

    public PpOcrModelsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir, name), "x");

    [Theory]
    [InlineData("ch_PP-OCRv6_rec_infer.onnx", "PP-OCRv6-rec")]
    [InlineData("ch_PP-OCRv4_rec_infer.onnx", "PP-OCRv4-rec")]
    [InlineData("ch_PP-OCRv3_rec_infer.onnx", "PP-OCRv3-rec")]
    [InlineData("custom.onnx", "PP-OCR-rec")]
    public void LabelFor_ReadsVersionFromFileName(string file, string expected)
    {
        Assert.Equal(expected, PpOcrModels.LabelFor(file));
    }

    [Theory]
    [InlineData("ch_PP-OCRv6_rec_infer.onnx", "ppocrv6_dict.txt")]
    [InlineData("ch_PP-OCRv5_rec_infer.onnx", "ppocrv5_dict.txt")]
    [InlineData("ch_PP-OCRv4_rec_infer.onnx", "ppocr_keys_v1.txt")]
    [InlineData("ch_PP-OCRv3_rec_infer.onnx", "ppocr_keys_v1.txt")]
    public void DictFileName_MatchesVersion(string file, string expected)
    {
        Assert.Equal(expected, PpOcrModels.DictFileNameFor(file));
    }

    [Fact]
    public void Resolve_AutoPrefersNewestAvailableModel()
    {
        Touch("ch_PP-OCRv3_rec_infer.onnx");
        Touch("ch_PP-OCRv6_rec_infer.onnx");
        Touch("ppocrv6_dict.txt");
        Touch("ppocr_keys_v1.txt");

        PpOcrModels.Resolved resolved = PpOcrModels.Resolve(_dir);

        Assert.Equal("ch_PP-OCRv6_rec_infer.onnx", Path.GetFileName(resolved.ModelPath));
        Assert.Equal("ppocrv6_dict.txt", Path.GetFileName(resolved.KeysPath));
        Assert.Equal("PP-OCRv6-rec", resolved.Label);
    }

    [Fact]
    public void Resolve_FallsBackToLegacyDict_WhenVersionDictMissing()
    {
        Touch("ch_PP-OCRv6_rec_infer.onnx");
        Touch("ppocr_keys_v1.txt");

        PpOcrModels.Resolved resolved = PpOcrModels.Resolve(_dir);

        Assert.Equal("ch_PP-OCRv6_rec_infer.onnx", Path.GetFileName(resolved.ModelPath));
        Assert.Equal("ppocr_keys_v1.txt", Path.GetFileName(resolved.KeysPath));
    }

    [Fact]
    public void Resolve_ExplicitModelWins()
    {
        Touch("ch_PP-OCRv6_rec_infer.onnx");
        Touch("ch_PP-OCRv3_rec_infer.onnx");
        Touch("ppocrv6_dict.txt");
        Touch("ppocr_keys_v1.txt");

        PpOcrModels.Resolved resolved = PpOcrModels.Resolve(_dir, modelFile: "ch_PP-OCRv3_rec_infer.onnx");

        Assert.Equal("ch_PP-OCRv3_rec_infer.onnx", Path.GetFileName(resolved.ModelPath));
        Assert.Equal("ppocr_keys_v1.txt", Path.GetFileName(resolved.KeysPath));
        Assert.Equal("PP-OCRv3-rec", resolved.Label);
    }

    [Fact]
    public void Resolve_ExplicitKeysWins()
    {
        Touch("ch_PP-OCRv6_rec_infer.onnx");
        Touch("ppocrv6_dict.txt");
        Touch("my_dict.txt");

        PpOcrModels.Resolved resolved = PpOcrModels.Resolve(_dir, keysFile: "my_dict.txt");

        Assert.Equal("my_dict.txt", Path.GetFileName(resolved.KeysPath));
    }
}
