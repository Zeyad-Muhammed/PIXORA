// Generates the PIXORA mark (SVG + PNG sizes + multi-size ICO) into assets/brand.
// Usage: dotnet run --project tools/IconGen -- <repo-root>
using SkiaSharp;

var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var outDir = Path.Combine(root, "assets", "brand");
Directory.CreateDirectory(outDir);

var coral = new SKColor(0xC7, 0x5C, 0x64);
var coralLight = new SKColor(0xE0, 0x8A, 0x90);
var tile = new SKColor(0x14, 0x14, 0x17);
var tileBorder = new SKColor(0x30, 0x30, 0x35);

// Mark geometry in a 100x100 box: an aperture ring with six curved blades sweeping around a lens core.
void DrawMark(SKCanvas c, float size, bool withTile)
{
    c.Save();
    c.Scale(size / 100f);
    if (withTile)
    {
        using var bg = new SKPaint { Color = tile, IsAntialias = true };
        c.DrawRoundRect(new SKRect(2, 2, 98, 98), 22, 22, bg);
        using var bd = new SKPaint { Color = tileBorder, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        c.DrawRoundRect(new SKRect(2, 2, 98, 98), 22, 22, bd);
    }
    const float cx = 50, cy = 50;
    using var ring = new SKPaint { Color = coral, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6.5f, StrokeCap = SKStrokeCap.Round };
    // Outer ring broken into a motion arc.
    c.DrawArc(new SKRect(cx - 34, cy - 34, cx + 34, cy + 34), -60, 300, false, ring);
    using var blade = new SKPaint { Color = coral, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4.2f, StrokeCap = SKStrokeCap.Round };
    for (int i = 0; i < 6; i++)
    {
        float a = i * 60f * MathF.PI / 180f;
        using var p = new SKPath();
        var start = new SKPoint(cx + 24 * MathF.Cos(a), cy + 24 * MathF.Sin(a));
        var ctrl = new SKPoint(cx + 20 * MathF.Cos(a + 0.9f), cy + 20 * MathF.Sin(a + 0.9f));
        var end = new SKPoint(cx + 9 * MathF.Cos(a + 1.6f), cy + 9 * MathF.Sin(a + 1.6f));
        p.MoveTo(start);
        p.QuadTo(ctrl, end);
        c.DrawPath(p, blade);
    }
    using var core = new SKPaint { Color = coralLight, IsAntialias = true };
    c.DrawCircle(cx, cy, 5.5f, core);
    // Motion accent: a short trailing arc outside the ring gap.
    using var trail = new SKPaint { Color = coral.WithAlpha(150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, StrokeCap = SKStrokeCap.Round };
    c.DrawArc(new SKRect(cx - 42, cy - 42, cx + 42, cy + 42), -52, 34, false, trail);
    c.Restore();
}

byte[] Png(int size, bool tileBg)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    surface.Canvas.Clear(SKColors.Transparent);
    DrawMark(surface.Canvas, size, tileBg);
    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

foreach (var s in new[] { 16, 24, 32, 48, 64, 128, 256, 512 })
    File.WriteAllBytes(Path.Combine(outDir, $"pixora-{s}.png"), Png(s, true));
File.WriteAllBytes(Path.Combine(outDir, "pixora-mark-256.png"), Png(256, false));

// ICO with embedded PNG frames (supported since Windows Vista).
var icoSizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var frames = icoSizes.Select(s => Png(s, true)).ToArray();
using (var fs = File.Create(Path.Combine(outDir, "pixora.ico")))
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

// SVG equivalent of the mark for web/launcher use.
var sb = new System.Text.StringBuilder();
sb.AppendLine("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="512" height="512">""");
sb.AppendLine("""  <rect x="2" y="2" width="96" height="96" rx="22" fill="#141417" stroke="#303035" stroke-width="1.5"/>""");
static string Arc(float cx, float cy, float r, float startDeg, float sweepDeg)
{
    float a0 = startDeg * MathF.PI / 180, a1 = (startDeg + sweepDeg) * MathF.PI / 180;
    var p0 = (cx + r * MathF.Cos(a0), cy + r * MathF.Sin(a0));
    var p1 = (cx + r * MathF.Cos(a1), cy + r * MathF.Sin(a1));
    int large = Math.Abs(sweepDeg) > 180 ? 1 : 0;
    return FormattableString.Invariant($"M{p0.Item1:0.##} {p0.Item2:0.##} A{r} {r} 0 {large} 1 {p1.Item1:0.##} {p1.Item2:0.##}");
}
sb.AppendLine($"""  <path d="{Arc(50, 50, 34, -60, 300)}" fill="none" stroke="#C75C64" stroke-width="6.5" stroke-linecap="round"/>""");
for (int i = 0; i < 6; i++)
{
    float a = i * 60f * MathF.PI / 180f;
    sb.AppendLine(FormattableString.Invariant(
        $"""  <path d="M{50 + 24 * MathF.Cos(a):0.##} {50 + 24 * MathF.Sin(a):0.##} Q{50 + 20 * MathF.Cos(a + 0.9f):0.##} {50 + 20 * MathF.Sin(a + 0.9f):0.##} {50 + 9 * MathF.Cos(a + 1.6f):0.##} {50 + 9 * MathF.Sin(a + 1.6f):0.##}" fill="none" stroke="#C75C64" stroke-width="4.2" stroke-linecap="round"/>"""));
}
sb.AppendLine("""  <circle cx="50" cy="50" r="5.5" fill="#E08A90"/>""");
sb.AppendLine($"""  <path d="{Arc(50, 50, 42, -52, 34)}" fill="none" stroke="#C75C64" stroke-opacity="0.59" stroke-width="3.5" stroke-linecap="round"/>""");
sb.AppendLine("</svg>");
File.WriteAllText(Path.Combine(outDir, "pixora.svg"), sb.ToString());
Console.WriteLine($"Wrote icons to {outDir}");
