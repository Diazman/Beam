using Avalonia;
using Avalonia.Media;
using QRCoder;

namespace Beam.App.Services;

/// <summary>Draws QR codes as vector geometry (one square per dark module), so they stay sharp at any size.</summary>
public static class QrCode
{
    /// <summary>A geometry of size <c>Modules × Modules</c> units, including the quiet zone.</summary>
    public static (Geometry Geometry, int Modules) Create(string text)
    {
        using var generator = new QRCodeGenerator();
        // Long codes (a link with pairing details) use less error correction so the modules stay big enough to scan.
        using var data = generator.CreateQrCode(text, text.Length > 150 ? QRCodeGenerator.ECCLevel.L : QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix; // includes a 4-module quiet zone
        var size = matrix.Count;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (!matrix[y][x]) continue;
                    var run = 1;
                    while (x + run < size && matrix[y][x + run]) run++;
                    context.BeginFigure(new Point(x, y), true);
                    context.LineTo(new Point(x + run, y));
                    context.LineTo(new Point(x + run, y + 1));
                    context.LineTo(new Point(x, y + 1));
                    context.EndFigure(true);
                    x += run - 1;
                }
            }
        }

        return (geometry, size);
    }
}
