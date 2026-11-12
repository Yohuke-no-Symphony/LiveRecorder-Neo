using System.Text;

namespace Yohuke.LiveRecorder.Media;

internal static class GapSlateRenderer
{
    private static readonly IReadOnlyDictionary<char, string[]> GLYPHS = new Dictionary<char, string[]>
    {
        ['N'] = ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
        ['O'] = ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
        ['V'] = ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
        ['I'] = ["11111", "00100", "00100", "00100", "00100", "00100", "11111"],
        ['D'] = ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
        ['E'] = ["11111", "10000", "10000", "11110", "10000", "10000", "11111"]
    };

    internal static async Task WriteAsync(string path, int width, int height, CancellationToken cancellationToken)
    {
        var pixels = new byte[checked(width * height * 3)];
        Array.Fill(pixels, (byte)24);
        const string label = "NO VIDEO";
        var scale = Math.Max(1, Math.Min(width / 60, height / 20));
        var left = (width - label.Length * 6 * scale) / 2;
        var top = (height - 7 * scale) / 2;
        for (var character = 0; character < label.Length; character++)
        {
            if (!GLYPHS.TryGetValue(label[character], out var glyph))
            {
                continue;
            }
            for (var row = 0; row < 7; row++)
            {
                for (var column = 0; column < 5; column++)
                {
                    if (glyph[row][column] != '1')
                    {
                        continue;
                    }
                    for (var y = 0; y < scale; y++)
                    {
                        for (var x = 0; x < scale; x++)
                        {
                            var offset = ((top + row * scale + y) * width + left + (character * 6 + column) * scale + x) * 3;
                            pixels[offset] = 210;
                            pixels[offset + 1] = 210;
                            pixels[offset + 2] = 210;
                        }
                    }
                }
            }
        }
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        await output.WriteAsync(Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"), cancellationToken);
        await output.WriteAsync(pixels, cancellationToken);
    }
}
