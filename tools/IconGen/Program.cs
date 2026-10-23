// Generates the PIXORA icon set (PNG sizes + multi-size ICO) from the approved logo master
// assets/brand/pixora-logo.png (transparent background).
// Usage: dotnet run --project tools/IconGen -- <repo-root>
using SkiaSharp;

var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var brand = Path.Combine(root, "assets", "brand");
var masterPath = Path.Combine(brand, "pixora-logo.png");
using var master = SKBitmap.Decode(masterPath) ?? throw new FileNotFoundException("Logo master not found", masterPath);
if (master.Width != master.Height) throw new InvalidDataException("The logo master must be square.");

byte[] Png(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    surface.Canvas.Clear(SKColors.Transparent);
    using var img = SKImage.FromBitmap(master);
    surface.Canvas.DrawImage(img, new SKRect(0, 0, size, size), new SKSamplingOptions(SKCubicResampler.Mitchell));
    using var snap = surface.Snapshot();
    using var data = snap.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

foreach (var s in new[] { 16, 24, 32, 48, 64, 128, 256, 512 })
    File.WriteAllBytes(Path.Combine(brand, $"pixora-{s}.png"), Png(s));
File.WriteAllBytes(Path.Combine(brand, "pixora-mark-256.png"), Png(256));

// ICO with embedded PNG frames (supported since Windows Vista).
var icoSizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var frames = icoSizes.Select(Png).ToArray();
using (var fs = File.Create(Path.Combine(brand, "pixora.ico")))
using (var w = new BinaryWriter(fs))
{
    w.Write((short)0); w.Write((short)1); w.Write((short)frames.Length);
    int offset = 6 + 16 * frames.Length;
    for (int i = 0; i < frames.Length; i++)
    {
        int s = icoSizes[i];
        w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
        w.Write(frames[i].Length); w.Write(offset);
        offset += frames[i].Length;
    }
    foreach (var f in frames) w.Write(f);
}
Console.WriteLine($"Wrote icon set to {brand}");
