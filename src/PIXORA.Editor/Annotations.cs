using SkiaSharp;

namespace PIXORA.Editor;

public enum AnnotationKind { Arrow, Line, Rectangle, Ellipse, Text, Step, Highlight, Blur, Pixelate }

/// <summary>
/// An editable object living in image-pixel coordinates. Objects stay editable until the
/// document is flattened for export, so editing is non-destructive.
/// </summary>
public abstract class Annotation
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
    public SKColor Color { get; set; } = new(0xC7, 0x5C, 0x64);
    public float Opacity { get; set; } = 1f;
    public float StrokeWidth { get; set; } = 4f;

    public abstract AnnotationKind Kind { get; }
    public abstract SKRect Bounds { get; }
    public abstract IReadOnlyList<SKPoint> Handles { get; }

    public abstract bool HitTest(SKPoint p, float tolerance);
    public abstract void Translate(float dx, float dy);
    public abstract void MoveHandle(int index, SKPoint p);
    public abstract Annotation Clone();

    /// <summary>Draws vector annotations. Pixel operations (blur/pixelate) are applied by the renderer instead.</summary>
    public virtual void Draw(SKCanvas canvas) { }

    protected SKColor WithOpacity() => Color.WithAlpha((byte)Math.Clamp(Opacity * 255f, 0f, 255f));

    protected T CopyBase<T>(T copy) where T : Annotation
    {
        copy.Id = Id;
        copy.Color = Color;
        copy.Opacity = Opacity;
        copy.StrokeWidth = StrokeWidth;
        return copy;
    }

    protected SKPaint StrokePaint() => new()
    {
        Color = WithOpacity(),
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = StrokeWidth,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };
}

/// <summary>Annotation defined by two raw corner points (may be un-normalised while dragging).</summary>
public abstract class BoxAnnotation : Annotation
{
    public float Left { get; set; }
    public float Top { get; set; }
    public float Right { get; set; }
    public float Bottom { get; set; }

    public SKRect Rect => new(Math.Min(Left, Right), Math.Min(Top, Bottom), Math.Max(Left, Right), Math.Max(Top, Bottom));
    public override SKRect Bounds => Rect;

    public override IReadOnlyList<SKPoint> Handles => new[]
    {
        new SKPoint(Left, Top), new SKPoint(Right, Top), new SKPoint(Right, Bottom), new SKPoint(Left, Bottom),
    };

    public override void Translate(float dx, float dy) { Left += dx; Right += dx; Top += dy; Bottom += dy; }

    public override void MoveHandle(int index, SKPoint p)
    {
        switch (index)
        {
            case 0: Left = p.X; Top = p.Y; break;
            case 1: Right = p.X; Top = p.Y; break;
            case 2: Right = p.X; Bottom = p.Y; break;
            case 3: Left = p.X; Bottom = p.Y; break;
        }
    }

    public override bool HitTest(SKPoint p, float tolerance)
    {
        var r = Rect;
        r.Inflate(tolerance, tolerance);
        return r.Contains(p);
    }

    public BoxAnnotation At(float l, float t, float r, float b) { Left = l; Top = t; Right = r; Bottom = b; return this; }

    protected T CopyBox<T>(T copy) where T : BoxAnnotation
    {
        CopyBase(copy);
        copy.Left = Left; copy.Top = Top; copy.Right = Right; copy.Bottom = Bottom;
        return copy;
    }
}

public sealed class RectangleAnnotation : BoxAnnotation
{
    public override AnnotationKind Kind => AnnotationKind.Rectangle;
    public float CornerRadius { get; set; } = 4f;

    public override bool HitTest(SKPoint p, float tolerance)
    {
        // Outline hit only, so the interior stays clickable for objects underneath.
        var r = Rect;
        var outer = r; outer.Inflate(tolerance + StrokeWidth / 2, tolerance + StrokeWidth / 2);
        var inner = r; inner.Inflate(-(tolerance + StrokeWidth / 2), -(tolerance + StrokeWidth / 2));
        return outer.Contains(p) && !(inner.Width > 0 && inner.Height > 0 && inner.Contains(p));
    }

    public override void Draw(SKCanvas canvas)
    {
        using var paint = StrokePaint();
        canvas.DrawRoundRect(Rect, CornerRadius, CornerRadius, paint);
    }

    public override Annotation Clone() => CopyBox(new RectangleAnnotation { CornerRadius = CornerRadius });
}

public sealed class EllipseAnnotation : BoxAnnotation
{
    public override AnnotationKind Kind => AnnotationKind.Ellipse;

    public override bool HitTest(SKPoint p, float tolerance)
    {
        var r = Rect;
        float rx = r.Width / 2, ry = r.Height / 2;
        if (rx < 1 || ry < 1) return r.Contains(p);
        float nx = (p.X - r.MidX) / rx, ny = (p.Y - r.MidY) / ry;
        float d = MathF.Sqrt(nx * nx + ny * ny);
        float tol = (tolerance + StrokeWidth / 2) / Math.Min(rx, ry);
        return Math.Abs(d - 1f) <= tol;
    }

    public override void Draw(SKCanvas canvas)
    {
        using var paint = StrokePaint();
        canvas.DrawOval(Rect, paint);
    }

    public override Annotation Clone() => CopyBox(new EllipseAnnotation());
}

public sealed class HighlightAnnotation : BoxAnnotation
{
    public HighlightAnnotation()
    {
        Color = new SKColor(0xFF, 0xD5, 0x4A);
        Opacity = 0.4f;
    }

    public override AnnotationKind Kind => AnnotationKind.Highlight;

    public override void Draw(SKCanvas canvas)
    {
        using var paint = new SKPaint { Color = WithOpacity(), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRect(Rect, paint);
    }

    public override Annotation Clone() => CopyBox(new HighlightAnnotation());
}

/// <summary>Region whose pixels are irreversibly blurred when rendered.</summary>
public sealed class BlurAnnotation : BoxAnnotation
{
    public const float MinStrength = 6f;
    public override AnnotationKind Kind => AnnotationKind.Blur;
    public float Strength { get; set; } = 14f;
    public override Annotation Clone() => CopyBox(new BlurAnnotation { Strength = Strength });
}

/// <summary>Region whose pixels are replaced by block averages when rendered.</summary>
public sealed class PixelateAnnotation : BoxAnnotation
{
    public const int MinBlock = 6;
    public override AnnotationKind Kind => AnnotationKind.Pixelate;
    public int BlockSize { get; set; } = 14;
    public override Annotation Clone() => CopyBox(new PixelateAnnotation { BlockSize = BlockSize });
}

/// <summary>Two-point annotation (line or arrow).</summary>
public abstract class LineBase : Annotation
{
    public SKPoint Start { get; set; }
    public SKPoint End { get; set; }

    public override SKRect Bounds => new(
        Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y), Math.Max(Start.X, End.X), Math.Max(Start.Y, End.Y));

    public override IReadOnlyList<SKPoint> Handles => new[] { Start, End };

    public override void Translate(float dx, float dy)
    {
        Start = new SKPoint(Start.X + dx, Start.Y + dy);
        End = new SKPoint(End.X + dx, End.Y + dy);
    }

    public override void MoveHandle(int index, SKPoint p)
    {
        if (index == 0) Start = p; else End = p;
    }

    public override bool HitTest(SKPoint p, float tolerance) =>
        DistanceToSegment(p, Start, End) <= tolerance + StrokeWidth / 2;

    internal static float DistanceToSegment(SKPoint p, SKPoint a, SKPoint b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len2 = dx * dx + dy * dy;
        if (len2 < 1e-6f) return SKPoint.Distance(p, a);
        float t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0f, 1f);
        return SKPoint.Distance(p, new SKPoint(a.X + t * dx, a.Y + t * dy));
    }

    protected T CopyLine<T>(T copy) where T : LineBase
    {
        CopyBase(copy);
        copy.Start = Start; copy.End = End;
        return copy;
    }
}

public sealed class LineAnnotation : LineBase
{
    public override AnnotationKind Kind => AnnotationKind.Line;

    public override void Draw(SKCanvas canvas)
    {
        using var paint = StrokePaint();
        canvas.DrawLine(Start, End, paint);
    }

    public override Annotation Clone() => CopyLine(new LineAnnotation());
}

public sealed class ArrowAnnotation : LineBase
{
    public override AnnotationKind Kind => AnnotationKind.Arrow;

    public override void Draw(SKCanvas canvas)
    {
        float dx = End.X - Start.X, dy = End.Y - Start.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return;
        float ux = dx / len, uy = dy / len;
        float head = Math.Min(len * 0.6f, Math.Max(14f, StrokeWidth * 4f));
        const float spread = 0.5f; // radians (~28 degrees)
        SKPoint Rot(float a) => new(
            End.X - head * (ux * MathF.Cos(a) - uy * MathF.Sin(a)),
            End.Y - head * (ux * MathF.Sin(a) + uy * MathF.Cos(a)));
        var left = Rot(spread);
        var right = Rot(-spread);
        // Shaft stops inside the head so the tip stays sharp with thick strokes.
        var shaftEnd = new SKPoint(End.X - ux * head * 0.6f, End.Y - uy * head * 0.6f);

        using var paint = StrokePaint();
        canvas.DrawLine(Start, shaftEnd, paint);
        using var fill = new SKPaint { Color = WithOpacity(), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var path = new SKPath();
        path.MoveTo(End);
        path.LineTo(left);
        path.LineTo(right);
        path.Close();
        canvas.DrawPath(path, fill);
    }

    public override Annotation Clone() => CopyLine(new ArrowAnnotation());
}

public sealed class TextAnnotation : Annotation
{
    public override AnnotationKind Kind => AnnotationKind.Text;
    public string Text { get; set; } = "Text";
    public SKPoint Position { get; set; }
    public float FontSize { get; set; } = 28f;
    public bool Bold { get; set; } = true;

    public static string FontFamily { get; set; } = "Segoe UI";

    private SKFont CreateFont() => new(
        SKTypeface.FromFamilyName(FontFamily, Bold ? SKFontStyle.Bold : SKFontStyle.Normal), FontSize);

    private IReadOnlyList<string> Lines => Text.Replace("\r", "").Split('\n');

    public override SKRect Bounds
    {
        get
        {
            using var font = CreateFont();
            var m = font.Metrics;
            float lineH = m.Descent - m.Ascent + m.Leading;
            float w = Lines.Max(l => font.MeasureText(l));
            return new SKRect(Position.X, Position.Y, Position.X + Math.Max(w, 4f), Position.Y + lineH * Lines.Count);
        }
    }

    public override IReadOnlyList<SKPoint> Handles => Array.Empty<SKPoint>();
    public override void MoveHandle(int index, SKPoint p) { }
    public override void Translate(float dx, float dy) => Position = new SKPoint(Position.X + dx, Position.Y + dy);

    public override bool HitTest(SKPoint p, float tolerance)
    {
        var r = Bounds;
        r.Inflate(tolerance, tolerance);
        return r.Contains(p);
    }

    public override void Draw(SKCanvas canvas)
    {
        if (string.IsNullOrEmpty(Text)) return;
        using var font = CreateFont();
        using var paint = new SKPaint { Color = WithOpacity(), IsAntialias = true };
        var m = font.Metrics;
        float lineH = m.Descent - m.Ascent + m.Leading;
        float y = Position.Y - m.Ascent;
        foreach (var line in Lines)
        {
            canvas.DrawText(line, Position.X, y, SKTextAlign.Left, font, paint);
            y += lineH;
        }
    }

    public override Annotation Clone() =>
        CopyBase(new TextAnnotation { Text = Text, Position = Position, FontSize = FontSize, Bold = Bold });
}

/// <summary>Numbered step marker (a filled circle with a number).</summary>
public sealed class StepAnnotation : Annotation
{
    public override AnnotationKind Kind => AnnotationKind.Step;
    public int Number { get; set; } = 1;
    public SKPoint Center { get; set; }
    public float Radius { get; set; } = 16f;

    public override SKRect Bounds => new(Center.X - Radius, Center.Y - Radius, Center.X + Radius, Center.Y + Radius);
    public override IReadOnlyList<SKPoint> Handles => Array.Empty<SKPoint>();
    public override void MoveHandle(int index, SKPoint p) { }
    public override void Translate(float dx, float dy) => Center = new SKPoint(Center.X + dx, Center.Y + dy);
    public override bool HitTest(SKPoint p, float tolerance) => SKPoint.Distance(p, Center) <= Radius + tolerance;

    public override void Draw(SKCanvas canvas)
    {
        using var fill = new SKPaint { Color = WithOpacity(), IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawCircle(Center, Radius, fill);
        using var ring = new SKPaint { Color = SKColors.White.WithAlpha(fill.Color.Alpha), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, Radius / 10f) };
        canvas.DrawCircle(Center, Radius - ring.StrokeWidth / 2, ring);
        using var font = new SKFont(SKTypeface.FromFamilyName(TextAnnotation.FontFamily, SKFontStyle.Bold), Radius * 1.05f);
        using var txt = new SKPaint { Color = SKColors.White.WithAlpha(fill.Color.Alpha), IsAntialias = true };
        var m = font.Metrics;
        canvas.DrawText(Number.ToString(), Center.X, Center.Y - (m.Ascent + m.Descent) / 2f, SKTextAlign.Center, font, txt);
    }

    public override Annotation Clone() => CopyBase(new StepAnnotation { Number = Number, Center = Center, Radius = Radius });
}
