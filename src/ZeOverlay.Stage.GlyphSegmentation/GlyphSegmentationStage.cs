using ZeOverlay.Shared;

namespace ZeOverlay.Stage.GlyphSegmentation;

/// <summary>S2 实现：按字形实际边界裁剪每一行（避免行末幻觉）。</summary>
public sealed class GlyphSegmentationStage : IGlyphSegmentationStage
{
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

            GlyphLine glyphs = Segmenter.SegmentRow(input.Frame, band.Top, band.Bottom);

            int left = glyphs.Glyphs.Count > 0 ? glyphs.Glyphs[0].Left : band.Left;
            int right = glyphs.Glyphs.Count > 0 ? glyphs.Glyphs[^1].Right : band.Right;
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
