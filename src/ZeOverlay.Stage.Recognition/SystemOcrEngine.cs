using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Recognition;

/// <summary>
/// 系统 OCR（`Windows.Media.Ocr`）后端。
///
/// 选它的理由：**零下载、零模型管理**，中英混排都能读，是 `PLAN.md` 第 3.2 节列的兜底层。
/// 自建字形模板（更快更省）留给 M6 —— 当前优先把「能识别」这条链路打通。
/// 代价是要中文语言包；`zh-Hans` 建不出来时回退到用户配置语言。
/// </summary>
public sealed class SystemOcrEngine : IOcrEngine
{
    private readonly OcrEngine? _engine;

    public SystemOcrEngine()
    {
        OcrEngine? engine = TryCreate("zh-Hans") ?? TryCreate("zh-Hant") ?? OcrEngine.TryCreateFromUserProfileLanguages();
        _engine = engine;

        Name = engine?.RecognizerLanguage?.LanguageTag is { Length: > 0 } tag
            ? $"系统OCR({tag})"
            : "系统OCR(不可用)";
    }

    public string Name { get; }

    public bool IsAvailable => _engine is not null;

    public bool TryRecognize(ImageFrame frame, out IReadOnlyList<OcrTextLine> lines, out string? error)
    {
        lines = [];
        error = null;

        if (_engine is null)
        {
            error = "系统 OCR 不可用（可能未安装中文语言包）。";
            return false;
        }

        if (frame.Width > OcrEngine.MaxImageDimension || frame.Height > OcrEngine.MaxImageDimension)
        {
            error = $"图像超过系统 OCR 上限（{OcrEngine.MaxImageDimension}px）。";
            return false;
        }

        try
        {
            using var bitmap = ToSoftwareBitmap(frame);
            OcrResult result = _engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();

            var parsed = new List<OcrTextLine>(result.Lines.Count);

            foreach (global::Windows.Media.Ocr.OcrLine source in result.Lines)
            {
                if (string.IsNullOrWhiteSpace(source.Text))
                {
                    continue;
                }

                var words = new List<OcrTextWord>();
                int left = int.MaxValue;
                int top = int.MaxValue;
                int right = 0;
                int bottom = 0;

                foreach (global::Windows.Media.Ocr.OcrWord word in source.Words)
                {
                    var rect = word.BoundingRect;
                    int x = (int)Math.Round(rect.X);
                    int y = (int)Math.Round(rect.Y);
                    int w = (int)Math.Round(rect.Width);
                    int h = (int)Math.Round(rect.Height);

                    words.Add(new OcrTextWord(word.Text, x, y, w, h));

                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + w);
                    bottom = Math.Max(bottom, y + h);
                }

                if (words.Count == 0)
                {
                    continue;
                }

                parsed.Add(new OcrTextLine(source.Text, left, top, right - left, bottom - top, words));
            }

            lines = parsed;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public void Dispose()
    {
    }

    private static SoftwareBitmap ToSoftwareBitmap(ImageFrame frame)
    {
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Premultiplied);

        // 用 DataWriter 拿 IBuffer：避免依赖已废弃的 System.Runtime.WindowsRuntime 扩展方法。
        using var writer = new DataWriter();
        writer.WriteBytes(frame.Bgra);
        bitmap.CopyFromBuffer(writer.DetachBuffer());

        return bitmap;
    }

    private static OcrEngine? TryCreate(string languageTag)
    {
        try
        {
            return OcrEngine.TryCreateFromLanguage(new Language(languageTag));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
