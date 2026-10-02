using ZeOverlay.Shared;

namespace ZeOverlay.Stage.GlyphSegmentation;

/// <summary>
/// S2 实现：裁剪每一行送给 OCR。
///
/// 两种裁剪方式：
/// - **紧缩到字形边界**（默认，本服括号语法用）：按估计边界留空白时 PP-OCR 会在行末幻觉出字符，
///   所以裁到字形实际边界。代价是低对比度的列可能被漏掉。
/// - **按行带全宽**（<paramref name="useBandWidth"/> = true，社区服 Plain 语法用）：该 HUD 的
///   「简称列」又小又暗，紧缩裁剪会把它整个漏掉（真机视频样本上实测：只剩玩家名+状态，
///   名称丢失 → 解析失败），因此直接用行带检测到的左右边界。
/// </summary>
public sealed class GlyphSegmentationStage : IGlyphSegmentationStage
{
    private readonly bool _useBandWidth;

    public GlyphSegmentationStage(bool useBandWidth = false)
    {
        _useBandWidth = useBandWidth;
    }

    public string Name => "字形切分";

    public IReadOnlyList<RowCrop> Process(RowInput input)
    {
        IReadOnlyList<RowBand> bands = input.Report.GridBands.Count > 0 ? input.Report.GridBands : input.Report.Bands;
        var crops = new List<RowCrop>(bands.Count);
        int slot = 0;

        foreach (RowBand band in bands)
        {
            int top = Math.Max(0, band.Top - 3);
            int bottom = Math.Min(input.Frame.Height - 1, band.Bottom + 3);
            if (bottom <= top)
            {
                continue;
            }

            int left;
            int right;

            if (_useBandWidth)
            {
                left = band.Left;
                right = band.Right;
            }
            else
            {
                GlyphLine glyphs = Segmenter.SegmentRow(input.Frame, band.Top, band.Bottom);
                left = glyphs.Glyphs.Count > 0 ? glyphs.Glyphs[0].Left : band.Left;
                right = glyphs.Glyphs.Count > 0 ? glyphs.Glyphs[^1].Right : band.Right;
            }

            left = Math.Max(0, left - 2);
            right = Math.Min(input.Frame.Width - 1, right + 2);

            int width = right - left + 1;
            if (width < 4)
            {
                continue;
            }

            crops.Add(new RowCrop(slot++, band, input.Frame.Crop(left, top, width, bottom - top + 1)));
        }

        return crops;
    }
}
