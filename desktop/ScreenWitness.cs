using System;
using System.Drawing;

internal enum ScreenArea { Workshop, Result, Dialog }

internal static class ScreenWitness
{
    private static readonly Rectangle[] WorkshopAreas =
    {
        new Rectangle(1068, 100, 412, 50), // Hunt and workshop mode
        new Rectangle(1198, 552, 281, 249), // Quantity, balance, price and Craft
        new Rectangle(538, 194, 114, 114), new Rectangle(362, 307, 105, 108),
        new Rectangle(721, 305, 105, 108), new Rectangle(363, 505, 105, 110),
        new Rectangle(720, 505, 105, 110), new Rectangle(538, 615, 114, 108)
    };

    private static readonly Rectangle[] ResultAreas =
    {
        new Rectangle(137, 229, 1219, 403), // Gear, positions and selection markers
        new Rectangle(660, 646, 170, 51),
        new Rectangle(72, 743, 1000, 65)
    };

    private static readonly Rectangle[] DialogAreas =
    {
        new Rectangle(420, 178, 660, 75), // Dialog title
        new Rectangle(455, 314, 584, 237), // Equipment, costs or proceeds
        new Rectangle(505, 584, 540, 64)
    };

    internal static bool Matches(Bitmap expected, Bitmap current, ScreenArea area)
    {
        if (expected.Size != current.Size)
            return false;
        var regions = area == ScreenArea.Workshop ? WorkshopAreas :
            area == ScreenArea.Dialog ? DialogAreas : ResultAreas;
        foreach (var region in regions)
        {
            if (!new Rectangle(Point.Empty, current.Size).Contains(region))
                return false;
            for (int row = region.Top; row < region.Bottom; row += 24)
            for (int col = region.Left; col < region.Right; col += 24)
            {
                int changed = 0;
                for (int y = row; y < Math.Min(row + 24, region.Bottom); y += 4)
                for (int x = col; x < Math.Min(col + 24, region.Right); x += 4)
                {
                    var a = expected.GetPixel(x, y);
                    var b = current.GetPixel(x, y);
                    if (Math.Abs(a.R - b.R) > 38 || Math.Abs(a.G - b.G) > 38 || Math.Abs(a.B - b.B) > 38)
                        changed++;
                }
                if (changed >= 9)
                    return false;
            }
        }
        return true;
    }
}
