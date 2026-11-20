namespace Seraph.Core.Framework;

[Flags]
public enum EcologyCell { Open = 0, Forest = 1, Vegetation = 2, Ground = 4, Unavailable = 8 }

/// <summary>Resumable horizontal coverage survey. Never treats unavailable world data as cleared forest.</summary>
public sealed class SurfaceEcologySurvey
{
    private int sample, y = -1, bottom;
    private bool forest, vegetation;
    public int ForestColumns { get; private set; }
    public int VegetationColumns { get; private set; }
    public int ObservedColumns { get; private set; }
    public bool Complete => sample >= 64;

    public int Advance(ReadOnlySpan<ushort> rainHeights, int top, int budget, Func<int, int, int, EcologyCell> read)
    {
        if (rainHeights.Length < 1024) { sample = 64; return 0; }
        var used = 0;
        while (!Complete && used < budget)
        {
            var x = sample % 8 * 4 + 2; var z = sample / 8 * 4 + 2;
            if (y < 0)
            {
                var surface = rainHeights[z * 32 + x];
                y = Math.Min(top, surface + 48);
                bottom = Math.Max(0, surface - 96);
            }
            var cell = read(x, y, z); used++;
            forest |= cell.HasFlag(EcologyCell.Forest);
            vegetation |= cell.HasFlag(EcologyCell.Vegetation);
            if (cell.HasFlag(EcologyCell.Unavailable)) Next(false);
            else if (cell.HasFlag(EcologyCell.Ground) || y <= bottom) Next(true);
            else y--;
        }
        return used;
    }

    private void Next(bool observed)
    {
        if (observed)
        {
            ObservedColumns++;
            if (forest) ForestColumns++;
            if (vegetation) VegetationColumns++;
        }
        sample++; y = -1; forest = vegetation = false;
    }
}
