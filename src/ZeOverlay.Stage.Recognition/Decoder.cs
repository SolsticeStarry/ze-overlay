using System.Text;

using ZeOverlay.Shared;

namespace ZeOverlay.Stage.Recognition;

/// <summary>
/// PP-OCR 的 CTC 贪心解码（纯逻辑，便于单测）。
///
/// 跳过 blank(0) 与连续重复；置信度取被采纳时间步的最大 softmax 值均值。
/// </summary>
internal static class Decoder
{
    public static (string Text, double Confidence) Decode(
        ReadOnlySpan<float> sample,
        int timeSteps,
        int classes,
        IReadOnlyList<string> characters)
    {
        var builder = new StringBuilder();
        int previous = -1;
        double confidenceSum = 0;
        int confidenceCount = 0;

        for (int t = 0; t < timeSteps; t++)
        {
            ReadOnlySpan<float> step = sample.Slice(t * classes, classes);
            int best = 0;
            float bestValue = step[0];

            for (int c = 1; c < classes; c++)
            {
                float value = step[c];
                if (value > bestValue)
                {
                    bestValue = value;
                    best = c;
                }
            }

            if (best != 0 && best != previous)
            {
                builder.Append(CharacterAt(characters, best));
                confidenceSum += bestValue;
                confidenceCount++;
            }

            previous = best;
        }

        double confidence = confidenceCount == 0 ? 0 : confidenceSum / confidenceCount;
        return (builder.ToString(), confidence);
    }

    private static string CharacterAt(IReadOnlyList<string> characters, int index)
        => index >= 0 && index < characters.Count ? characters[index] : string.Empty;
}
