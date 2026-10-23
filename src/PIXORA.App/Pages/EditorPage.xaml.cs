using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using PIXORA.App.Services;
using PIXORA.Capture;
using PIXORA.Core;
using PIXORA.Editor;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace PIXORA.App.Pages;

public enum EditorTool { Select, Crop, Arrow, Line, Rectangle, Ellipse, Text, Step, Highlight, Blur, Pixelate }

public sealed partial class EditorPage : Page
{
    private sealed record LayerItem(Annotation Annotation, string Label)
    {
        public override string ToString() => Label;
    }

    private enum DragKind { None, Create, Move, Resize, Crop }

    private static readonly SKColor[] Palette =
    {
        new(0xC7, 0x5C, 0x64), new(0xE5, 0xEE, 0xF0), new(0x3B, 0xA7, 0xF0), new(0x4C, 0xC3, 0x8A), new(0xF2, 0xC2, 0x30), new(0x11, 0x11, 0x14),
    };

    private readonly EditorSession _session = App.Get<EditorSession>();
    private readonly SettingsStore _settings = App.Get<SettingsStore>();
    private readonly DispatcherTimer _propCoalesce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private EditorDocument? _doc;
    private EditorTool _tool = EditorTool.Select;
    private Annotation? _selected;
    private double _zoom = 1;
    private WriteableBitmap? _wb;
    private bool _renderPending;
    private bool _loadingProps = true, _syncingLayers, _propCheckpointOpen; // true until InitializeComponent finishes (sliders raise ValueChanged while loading)

    private DragKind _drag;
    private SKPoint _dragStart, _lastPoint;
    private int _handle;
    private bool _moved;
    private Annotation? _creating;
    private SKRect? _cropDraft;

    // Defaults for new annotations (also edited through the inspector when nothing is selected).
    private SKColor _color = Palette[0];
    private float _stroke = 4, _opacity = 1, _fontSize = 28, _blurStrength = 14, _stepRadius = 16;
    private int _pixelBlock = 14;

    public EditorPage()
    {
        InitializeComponent();
        _loadingProps = false;
        _propCoalesce.Tick += (_, _) => { _propCoalesce.Stop(); _propCheckpointOpen = false; };
        BuildSwatches();
        AddAccelerators();
        KeyDown += Page_KeyDown;
    }

    private double Scale => XamlRoot?.RasterizationScale ?? 1.0;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _session.DocumentReplaced += OnDocumentReplaced;
        _session.Saved += OnSaved;
        AttachDocument(_session.Document, fit: _doc != _session.Document || _zoom <= 0);
        SetTool(_tool);
        UpdateOutputFormat();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _session.DocumentReplaced -= OnDocumentReplaced;
        _session.Saved -= OnSaved;
        if (_doc != null) _doc.Changed -= OnDocChanged;
    }

    private void OnDocumentReplaced() => AttachDocument(_session.Document, fit: true);
    private void OnSaved(string path) => RefreshChrome();

    private void AttachDocument(EditorDocument? doc, bool fit)
    {
        if (_doc != null) _doc.Changed -= OnDocChanged;
        _doc = doc;
        _selected = null;
        _wb = null;
        if (_doc != null) _doc.Changed += OnDocChanged;
        bool has = _doc != null;
        EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        Scroller.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        ToolDock.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in new Control[] { SaveButton, ExportButton, CopyButton }) b.IsEnabled = has;
        if (has && fit)
        {
            if (Scroller.ActualWidth > 0) FitToWindow();
            else _zoom = -1; // fit once the scroller has a size
        }
        RefreshAll();
    }

    private void OnDocChanged()
    {
        if (_selected != null && _doc != null)
            _selected = _doc.Annotations.FirstOrDefault(a => a.Id == _selected.Id); // undo/redo swaps instances
        RefreshAll();
    }

    // ---- rendering -----------------------------------------------------------------------------------------

    private void RequestRender()
    {
        if (_renderPending) return;
        _renderPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { _renderPending = false; Render(); });
    }

    private void Render()
    {
        if (_doc == null) { CanvasImage.Source = null; return; }
        using var comp = Renderer.Composite(_doc.Base, _doc.Annotations, _selected);
        using (var c = new SKCanvas(comp))
        {
            var crop = _cropDraft ?? (_doc.Crop is { } cr ? new SKRect(cr.Left, cr.Top, cr.Right, cr.Bottom) : (SKRect?)null);
            if (crop is { } r)
            {
                using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 150) };
                using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                path.AddRect(new SKRect(0, 0, comp.Width, comp.Height));
                path.AddRect(r);
                c.DrawPath(path, dim);
                if (_tool == EditorTool.Crop || _cropDraft != null)
                {
                    float px = (float)(_doc.Base.Width / Math.Max(1, CanvasImage.ActualWidth));
                    using var frame = new SKPaint { Color = new SKColor(0xC7, 0x5C, 0x64), Style = SKPaintStyle.Stroke, StrokeWidth = 2 * px, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 8 * px, 5 * px }, 0) };
                    c.DrawRect(r, frame);
                }
            }
        }
        _wb = UiHelpers.ToWriteable(comp, _wb);
        CanvasImage.Source = _wb;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_doc == null || _zoom <= 0) return; // zoom < 0 means "fit once the scroller is laid out"
        CanvasImage.Width = _doc.Base.Width * _zoom / Scale;
        CanvasImage.Height = _doc.Base.Height * _zoom / Scale;
        ZoomText.Content = $"{_zoom * 100:0}%";
    }

    private void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.05, 8);
        ApplyZoom();
    }

    private void FitToWindow()
    {
        if (_doc == null) return;
        double w = Math.Max(100, Scroller.ActualWidth - 80), h = Math.Max(100, Scroller.ActualHeight - 150);
        SetZoom(Math.Min(1.0, Math.Min(w / (_doc.Base.Width / Scale), h / (_doc.Base.Height / Scale))));
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_zoom < 0 && _doc != null) { FitToWindow(); Render(); }
    }

    private void Scroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var mods = e.KeyModifiers;
        if (!mods.HasFlag(VirtualKeyModifiers.Control) || _doc == null) return;
        int delta = e.GetCurrentPoint(Scroller).Properties.MouseWheelDelta;
        SetZoom(_zoom * (delta > 0 ? 1.15 : 1 / 1.15));
        e.Handled = true;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25);
    private void Zoom100_Click(object sender, RoutedEventArgs e) => SetZoom(1);
    private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow();

    // ---- chrome/inspector ----------------------------------------------------------------------------------

    private void RefreshAll()
    {
        RefreshChrome();
        RefreshLayers();
        RefreshInspector();
        Render();
    }

    private void RefreshChrome()
    {
        if (_doc == null)
        {
            DocTitle.Text = "Editor";
            DocInfo.Text = "";
            DirtyDot.Visibility = Visibility.Collapsed;
            UndoButton.IsEnabled = RedoButton.IsEnabled = false;
            return;
        }
        DocTitle.Text = _doc.FilePath != null ? Path.GetFileName(_doc.FilePath) : "Untitled capture";
        DocInfo.Text = _doc.Crop is { } c ? $"{_doc.Base.Width}×{_doc.Base.Height} · cropped to {c.Width}×{c.Height}" : $"{_doc.Base.Width}×{_doc.Base.Height} px";
        DirtyDot.Visibility = _doc.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = _doc.CanUndo;
        RedoButton.IsEnabled = _doc.CanRedo;
        CropInfo.Text = _doc.Crop is { } cc ? $"Export area: {cc.Width}×{cc.Height} at ({cc.Left}, {cc.Top}). Drag again to change it." : "Drag on the image to choose the area to keep. The original stays intact until you save.";
        ResetCropButton.IsEnabled = _doc.Crop != null;
    }

    private void RefreshLayers()
    {
        _syncingLayers = true;
        var items = new List<LayerItem>();
        if (_doc != null)
        {
            var counts = new Dictionary<AnnotationKind, int>();
            foreach (var a in _doc.Annotations)
            {
                counts[a.Kind] = counts.GetValueOrDefault(a.Kind) + 1;
                var label = a switch
                {
                    TextAnnotation t => $"Text “{(t.Text.Length > 18 ? t.Text[..18] + "…" : t.Text)}”",
                    StepAnnotation s => $"Step {s.Number}",
                    BlurAnnotation => $"Blur area {counts[a.Kind]}",
                    PixelateAnnotation => $"Pixelated area {counts[a.Kind]}",
                    _ => $"{a.Kind} {counts[a.Kind]}",
                };
                items.Add(new LayerItem(a, label));
            }
        }
        items.Reverse(); // top-most first, like a layer panel
        LayerList.ItemsSource = items;
        LayerList.SelectedItem = items.FirstOrDefault(i => i.Annotation.Id == _selected?.Id);
        StackTitle.Text = $"MARKUP STACK ({items.Count})";
        LayerEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _syncingLayers = false;
    }

    private void LayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLayers) return;
        _selected = (LayerList.SelectedItem as LayerItem)?.Annotation;
        if (_selected != null && _tool != EditorTool.Select) SetTool(EditorTool.Select);
        RefreshInspector();
        Render();
    }

    private AnnotationKind? ContextKind => _selected?.Kind ?? _tool switch
    {
        EditorTool.Arrow => AnnotationKind.Arrow, EditorTool.Line => AnnotationKind.Line, EditorTool.Rectangle => AnnotationKind.Rectangle,
        EditorTool.Ellipse => AnnotationKind.Ellipse, EditorTool.Text => AnnotationKind.Text, EditorTool.Step => AnnotationKind.Step,
        EditorTool.Highlight => AnnotationKind.Highlight, EditorTool.Blur => AnnotationKind.Blur, EditorTool.Pixelate => AnnotationKind.Pixelate,
        _ => null,
    };

    private void RefreshInspector()
    {
        _loadingProps = true;
        var kind = ContextKind;
        CropPanel.Visibility = _tool == EditorTool.Crop && _selected == null ? Visibility.Visible : Visibility.Collapsed;
        PropsPanel.Visibility = kind != null ? Visibility.Visible : Visibility.Collapsed;
        if (kind is { } k)
        {
            PropsTarget.Text = _selected != null ? "Selected object" : "New objects";
            bool redact = k is AnnotationKind.Blur or AnnotationKind.Pixelate;
            ColorRow.Visibility = redact ? Visibility.Collapsed : Visibility.Visible;
            OpacityRow.Visibility = redact ? Visibility.Collapsed : Visibility.Visible;
            StrokeRow.Visibility = k is AnnotationKind.Arrow or AnnotationKind.Line or AnnotationKind.Rectangle or AnnotationKind.Ellipse ? Visibility.Visible : Visibility.Collapsed;
            SizeRow.Visibility = k is AnnotationKind.Text or AnnotationKind.Step or AnnotationKind.Blur or AnnotationKind.Pixelate ? Visibility.Visible : Visibility.Collapsed;
            TextRow.Visibility = k == AnnotationKind.Text && _selected != null ? Visibility.Visible : Visibility.Collapsed;
            RedactNote.Visibility = redact ? Visibility.Visible : Visibility.Collapsed;

            var color = _selected?.Color ?? (k == AnnotationKind.Highlight ? new SKColor(0xF2, 0xC2, 0x30) : _color);
            ShowColor(color);
            StrokeSlider.Value = _selected?.StrokeWidth ?? _stroke;
            OpacitySlider.Value = (_selected?.Opacity ?? (k == AnnotationKind.Highlight ? 0.4f : _opacity)) * 100;
            switch (k)
            {
                case AnnotationKind.Text:
                    SizeLabel.Text = "Font size"; SizeSlider.Minimum = 8; SizeSlider.Maximum = 120;
                    SizeSlider.Value = (_selected as TextAnnotation)?.FontSize ?? _fontSize;
                    break;
                case AnnotationKind.Step:
                    SizeLabel.Text = "Marker size"; SizeSlider.Minimum = 8; SizeSlider.Maximum = 60;
                    SizeSlider.Value = (_selected as StepAnnotation)?.Radius ?? _stepRadius;
                    break;
                case AnnotationKind.Blur:
                    SizeLabel.Text = "Blur strength"; SizeSlider.Minimum = BlurAnnotation.MinStrength; SizeSlider.Maximum = 40;
                    SizeSlider.Value = (_selected as BlurAnnotation)?.Strength ?? _blurStrength;
                    break;
                case AnnotationKind.Pixelate:
                    SizeLabel.Text = "Block size"; SizeSlider.Minimum = PixelateAnnotation.MinBlock; SizeSlider.Maximum = 60;
                    SizeSlider.Value = (_selected as PixelateAnnotation)?.BlockSize ?? _pixelBlock;
                    break;
            }
            if (_selected is TextAnnotation ta)
            {
                if (TextInput.Text != ta.Text) TextInput.Text = ta.Text;
                BoldBox.IsChecked = ta.Bold;
            }
            UpdatePropLabels();
        }
        _loadingProps = false;
    }

    private void UpdatePropLabels()
    {
        StrokeText.Text = $"{StrokeSlider.Value:0} px";
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        SizeText.Text = ContextKind == AnnotationKind.Blur ? $"{SizeSlider.Value:0}" : $"{SizeSlider.Value:0} px";
    }

    private void BuildSwatches()
    {
        foreach (var c in Palette)
        {
            var b = new Button { Style = (Style)Resources["Swatch"], Background = new SolidColorBrush(ToUi(c)), Tag = c };
            ToolTipService.SetToolTip(b, $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}");
            b.Click += (_, _) => ApplyColor((SKColor)b.Tag);
            Swatches.Children.Add(b);
        }
        var custom = new Button { Style = (Style)Resources["Swatch"], Content = new FontIcon { Glyph = "", FontSize = 13 }, Background = (Brush)Application.Current.Resources["PxElevated"] };
        ToolTipService.SetToolTip(custom, "Custom colour");
        custom.Click += CustomColor_Click;
        Swatches.Children.Add(custom);
        ShowColor(_color);
    }

    private static Windows.UI.Color ToUi(SKColor c) => Windows.UI.Color.FromArgb(255, c.Red, c.Green, c.Blue);

    private void ShowColor(SKColor c)
    {
        var hex = $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
        ColorHex.Text = hex;
        DockColorText.Text = hex;
        DockColorDot.Fill = new SolidColorBrush(ToUi(c));
        foreach (var child in Swatches.Children.OfType<Button>())
            child.BorderBrush = child.Tag is SKColor sc && sc.Red == c.Red && sc.Green == c.Green && sc.Blue == c.Blue
                ? (Brush)Application.Current.Resources["PxText"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void ApplyColor(SKColor c)
    {
        if (_selected != null && _doc != null)
        {
            if (_selected is BlurAnnotation or PixelateAnnotation) return;
            _doc.Checkpoint();
            _selected.Color = c;
            _doc.NotifyChanged();
        }
        else _color = c;
        ShowColor(c);
    }

    private void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        var current = _selected?.Color ?? _color;
        var picker = new ColorPicker { Color = ToUi(current), IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorSliderVisible = true, IsHexInputVisible = true };
        var apply = new Button { Content = "Apply", Style = (Style)Application.Current.Resources["PxPrimaryButton"], HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { picker, apply } } };
        apply.Click += (_, _) => { var c = picker.Color; ApplyColor(new SKColor(c.R, c.G, c.B)); flyout.Hide(); };
        flyout.ShowAt((FrameworkElement)sender);
    }

    private void OpenPropCheckpoint()
    {
        if (_doc == null) return;
        if (!_propCheckpointOpen) { _doc.Checkpoint(); _propCheckpointOpen = true; }
        _propCoalesce.Stop();
        _propCoalesce.Start();
    }

    private void Prop_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingProps) return;
        UpdatePropLabels();
        var k = ContextKind;
        float stroke = (float)StrokeSlider.Value, opacity = (float)(OpacitySlider.Value / 100), size = (float)SizeSlider.Value;
        if (_selected == null)
        {
            if (ReferenceEquals(sender, StrokeSlider)) _stroke = stroke;
            else if (ReferenceEquals(sender, OpacitySlider) && k != AnnotationKind.Highlight) _opacity = opacity;
            else if (ReferenceEquals(sender, SizeSlider))
            {
                switch (k)
                {
                    case AnnotationKind.Text: _fontSize = size; break;
                    case AnnotationKind.Step: _stepRadius = size; break;
                    case AnnotationKind.Blur: _blurStrength = size; break;
                    case AnnotationKind.Pixelate: _pixelBlock = (int)size; break;
                }
            }
            return;
        }
        OpenPropCheckpoint();
        if (ReferenceEquals(sender, StrokeSlider)) _selected.StrokeWidth = stroke;
        else if (ReferenceEquals(sender, OpacitySlider)) _selected.Opacity = opacity;
        else if (ReferenceEquals(sender, SizeSlider))
        {
            switch (_selected)
            {
                case TextAnnotation t: t.FontSize = size; break;
                case StepAnnotation s: s.Radius = size; break;
                case BlurAnnotation b: b.Strength = size; break;
                case PixelateAnnotation p: p.BlockSize = (int)size; break;
            }
        }
        RefreshChrome();
        RequestRender();
    }

    private void TextInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingProps || _selected is not TextAnnotation t || t.Text == TextInput.Text) return;
        OpenPropCheckpoint();
        t.Text = TextInput.Text;
        RefreshChrome();
        RefreshLayersLabelsOnly();
        RequestRender();
    }

    private void RefreshLayersLabelsOnly()
    {
        var keep = _selected;
        RefreshLayers();
        _selected = keep;
    }

    private void Bold_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not TextAnnotation t || _doc == null) return;
        _doc.Checkpoint();
        t.Bold = BoldBox.IsChecked == true;
        _doc.NotifyChanged();
    }

    // ---- tools ---------------------------------------------------------------------------------------------

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<EditorTool>(tag, out var t)) SetTool(t);
    }

    private void SetTool(EditorTool tool)
    {
        _tool = tool;
        if (tool != EditorTool.Select) _selected = null;
        foreach (var tb in new ToggleButton[] { ToolSelect, ToolArrow, ToolRect, ToolEllipse, ToolLine, ToolText, ToolStep, ToolHighlight, ToolBlur, ToolPixelate, ToolCrop,
                     DockSelect, DockRect, DockEllipse, DockArrow, DockText, DockBlur, DockStep, DockCrop })
            tb.IsChecked = (string)tb.Tag == tool.ToString();
        ToolName.Text = tool switch { EditorTool.Step => "Numbered steps", EditorTool.Pixelate => "Pixelate", _ => tool + " tool" };
        ToolKey.Text = "Shortcut " + tool switch
        {
            EditorTool.Select => "V", EditorTool.Crop => "C", EditorTool.Arrow => "A", EditorTool.Line => "L", EditorTool.Rectangle => "R",
            EditorTool.Ellipse => "E", EditorTool.Text => "T", EditorTool.Step => "N", EditorTool.Highlight => "H", EditorTool.Blur => "B", _ => "P",
        };
        if (CanvasHost.IsLoaded)
            CanvasHost.SetCursor(tool == EditorTool.Select ? InputSystemCursorShape.Arrow : tool == EditorTool.Text ? InputSystemCursorShape.IBeam : InputSystemCursorShape.Cross);
        RefreshLayers();
        RefreshInspector();
        RequestRender();
    }

    // ---- canvas input --------------------------------------------------------------------------------------

    private SKPoint ToImage(Windows.Foundation.Point pos)
    {
        if (_doc == null || CanvasImage.ActualWidth <= 0) return default;
        return new SKPoint((float)(pos.X * _doc.Base.Width / CanvasImage.ActualWidth), (float)(pos.Y * _doc.Base.Height / CanvasImage.ActualHeight));
    }

    private float Tolerance => _doc == null || CanvasImage.ActualWidth <= 0 ? 6 : (float)(6 * _doc.Base.Width / CanvasImage.ActualWidth);

    private SKPoint Clamp(SKPoint p) => _doc == null ? p : new SKPoint(Math.Clamp(p.X, 0, _doc.Base.Width), Math.Clamp(p.Y, 0, _doc.Base.Height));

    private Annotation? CreateFor(EditorTool tool, SKPoint p)
    {
        Annotation? a = tool switch
        {
            EditorTool.Arrow => new ArrowAnnotation { Start = p, End = p },
            EditorTool.Line => new LineAnnotation { Start = p, End = p },
            EditorTool.Rectangle => new RectangleAnnotation().At(p.X, p.Y, p.X, p.Y),
            EditorTool.Ellipse => new EllipseAnnotation().At(p.X, p.Y, p.X, p.Y),
            EditorTool.Highlight => new HighlightAnnotation { Opacity = (float)(OpacitySlider.Value / 100) }.At(p.X, p.Y, p.X, p.Y),
            EditorTool.Blur => new BlurAnnotation { Strength = _blurStrength }.At(p.X, p.Y, p.X, p.Y),
            EditorTool.Pixelate => new PixelateAnnotation { BlockSize = _pixelBlock }.At(p.X, p.Y, p.X, p.Y),
            _ => null,
        };
        if (a == null) return null;
        if (a is not (HighlightAnnotation or BlurAnnotation or PixelateAnnotation)) { a.Color = _color; a.Opacity = _opacity; }
        if (a is HighlightAnnotation) a.Color = _selected?.Color ?? new SKColor(0xF2, 0xC2, 0x30);
        a.StrokeWidth = _stroke;
        return a;
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_doc == null) return;
        var pt = e.GetCurrentPoint(CanvasImage);
        if (!pt.Properties.IsLeftButtonPressed) return;
        Focus(FocusState.Pointer);
        var p = ToImage(pt.Position);
        _dragStart = _lastPoint = p;
        _moved = false;

        switch (_tool)
        {
            case EditorTool.Select:
                if (_selected != null)
                {
                    var hs = _selected.Handles;
                    for (int i = 0; i < hs.Count; i++)
                        if (SKPoint.Distance(hs[i], p) <= Tolerance * 1.6f)
                        {
                            _drag = DragKind.Resize; _handle = i; _doc.Checkpoint();
                            CanvasHost.CapturePointer(e.Pointer);
                            return;
                        }
                }
                _selected = _doc.HitTest(p, Tolerance);
                _drag = _selected != null ? DragKind.Move : DragKind.None;
                RefreshLayers();
                RefreshInspector();
                break;
            case EditorTool.Crop:
                _drag = DragKind.Crop;
                _cropDraft = new SKRect(p.X, p.Y, p.X, p.Y);
                break;
            case EditorTool.Text:
                var t = new TextAnnotation { Position = p, Text = "Text", FontSize = _fontSize, Color = _color, Opacity = _opacity };
                _doc.Add(t);
                SetTool(EditorTool.Select);
                _selected = t;
                RefreshAll();
                TextInput.Focus(FocusState.Programmatic);
                TextInput.SelectAll();
                return;
            case EditorTool.Step:
                var s = new StepAnnotation { Center = p, Number = _doc.NextStepNumber(), Radius = _stepRadius, Color = _color, Opacity = _opacity };
                _doc.Add(s);
                _selected = s;
                RefreshAll();
                return;
            default:
                var a = CreateFor(_tool, p);
                if (a == null) return;
                _doc.Checkpoint();
                _doc.Annotations.Add(a);
                _creating = a;
                _selected = a;
                _drag = DragKind.Create;
                break;
        }
        CanvasHost.CapturePointer(e.Pointer);
        RequestRender();
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_doc == null || _drag == DragKind.None) return;
        var p = ToImage(e.GetCurrentPoint(CanvasImage).Position);
        bool shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        switch (_drag)
        {
            case DragKind.Create when _creating is LineBase line:
                line.End = shift ? Snap45(line.Start, p) : p;
                ShowSizeBadge($"{SKPoint.Distance(line.Start, line.End):0} px");
                break;
            case DragKind.Create when _creating is BoxAnnotation box:
                var q = shift ? Square(_dragStart, p) : p;
                if (box is BlurAnnotation or PixelateAnnotation) q = Clamp(q);
                box.Right = q.X; box.Bottom = q.Y;
                ShowSizeBadge($"{box.Rect.Width:0} × {box.Rect.Height:0}");
                break;
            case DragKind.Move when _selected != null:
                if (!_moved) { _doc.Checkpoint(); _moved = true; }
                _selected.Translate(p.X - _lastPoint.X, p.Y - _lastPoint.Y);
                break;
            case DragKind.Resize when _selected != null:
                _moved = true;
                _selected.MoveHandle(_handle, p);
                if (_selected is BoxAnnotation rb) ShowSizeBadge($"{rb.Rect.Width:0} × {rb.Rect.Height:0}");
                break;
            case DragKind.Crop:
                var c = Clamp(p);
                _cropDraft = new SKRect(Math.Min(_dragStart.X, c.X), Math.Min(_dragStart.Y, c.Y), Math.Max(_dragStart.X, c.X), Math.Max(_dragStart.Y, c.Y));
                ShowSizeBadge($"{_cropDraft.Value.Width:0} × {_cropDraft.Value.Height:0}");
                break;
        }
        _lastPoint = p;
        RequestRender();
    }

    private static SKPoint Snap45(SKPoint a, SKPoint b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        float len = MathF.Sqrt(dx * dx + dy * dy);
        return new SKPoint(a.X + len * (float)Math.Cos(ang), a.Y + len * (float)Math.Sin(ang));
    }

    private static SKPoint Square(SKPoint a, SKPoint b)
    {
        float d = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        return new SKPoint(a.X + Math.Sign(b.X - a.X) * d, a.Y + Math.Sign(b.Y - a.Y) * d);
    }

    private void ShowSizeBadge(string text)
    {
        SizeBadgeText.Text = text;
        SizeBadge.Visibility = Visibility.Visible;
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CanvasHost.ReleasePointerCapture(e.Pointer);
        SizeBadge.Visibility = Visibility.Collapsed;
        if (_doc == null) { _drag = DragKind.None; return; }
        switch (_drag)
        {
            case DragKind.Create when _creating != null:
                var b = _creating.Bounds;
                bool tiny = _creating is LineBase l ? SKPoint.Distance(l.Start, l.End) < 3 : b.Width < 3 || b.Height < 3;
                if (tiny)
                {
                    _doc.Annotations.Remove(_creating);
                    _doc.DiscardCheckpoint();
                    _selected = null;
                }
                _creating = null;
                _doc.NotifyChanged();
                break;
            case DragKind.Move:
                if (_moved) _doc.NotifyChanged();
                break;
            case DragKind.Resize:
                if (!_moved) _doc.DiscardCheckpoint();
                _doc.NotifyChanged();
                break;
            case DragKind.Crop when _cropDraft is { } r:
                _cropDraft = null;
                if (r.Width >= 4 && r.Height >= 4)
                    _doc.SetCrop(new SKRectI((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom)));
                else RequestRender();
                break;
        }
        _drag = DragKind.None;
    }

    private void Canvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_selected is TextAnnotation)
        {
            TextInput.Focus(FocusState.Programmatic);
            TextInput.SelectAll();
        }
    }

    // ---- commands ------------------------------------------------------------------------------------------

    private void Undo_Click(object sender, RoutedEventArgs e) { _propCheckpointOpen = false; _doc?.Undo(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { _propCheckpointOpen = false; _doc?.Redo(); }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_doc == null || _selected == null) return;
        var s = _selected;
        _selected = null;
        _doc.Remove(s);
    }

    private void LayerUp_Click(object sender, RoutedEventArgs e) => MoveLayer(+1);
    private void LayerDown_Click(object sender, RoutedEventArgs e) => MoveLayer(-1);

    private void MoveLayer(int dir)
    {
        if (_doc == null || _selected == null) return;
        int i = _doc.Annotations.FindIndex(a => a.Id == _selected.Id);
        int j = i + dir;
        if (i < 0 || j < 0 || j >= _doc.Annotations.Count) return;
        _doc.Checkpoint();
        (_doc.Annotations[i], _doc.Annotations[j]) = (_doc.Annotations[j], _doc.Annotations[i]);
        _doc.NotifyChanged();
    }

    private void ResetCrop_Click(object sender, RoutedEventArgs e) => _doc?.SetCrop(null);

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(false);
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsync(true);

    private async Task SaveAsync(bool saveAs)
    {
        if (_doc == null) return;
        try
        {
            var path = await _session.SaveAsync(saveAs);
            if (path != null) App.MainWindow.ShowBanner("Saved", path, InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            App.MainWindow.ShowBanner("Save failed", ex.Message, InfoBarSeverity.Error);
        }
        RefreshChrome();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyFlattened();

    private void CopyFlattened()
    {
        if (_doc == null) return;
        try
        {
            using var flat = Renderer.Flatten(_doc);
            ClipboardService.SetImage(flat);
            App.MainWindow.ShowBanner("Copied", $"Edited image ({flat.Width}×{flat.Height}) is on the clipboard.", InfoBarSeverity.Success);
        }
        catch (CaptureException ex) { App.MainWindow.ShowBanner("Copy failed", ex.Message, InfoBarSeverity.Error); }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!await App.MainWindow.ConfirmDiscardEditorChangesAsync()) return;
        var path = await UiHelpers.PickImageAsync();
        if (path != null) OpenPath(path);
    }

    private void OpenPath(string path)
    {
        try { _session.Open(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            App.MainWindow.ShowBanner("Cannot open image", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void Paste_Click(object sender, RoutedEventArgs e) => await PasteAsync();

    private async Task PasteAsync()
    {
        try
        {
            var bmp = ClipboardService.GetImage();
            if (bmp == null) { App.MainWindow.ShowBanner("No image on the clipboard", "Copy an image first.", InfoBarSeverity.Warning); return; }
            if (!await App.MainWindow.ConfirmDiscardEditorChangesAsync()) { bmp.Dispose(); return; }
            _session.OpenBitmap(bmp, null);
        }
        catch (CaptureException ex) { App.MainWindow.ShowBanner("Clipboard unavailable", ex.Message, InfoBarSeverity.Error); }
    }

    private async void NewCapture_Click(object sender, RoutedEventArgs e) => await App.Get<CaptureController>().CaptureAsync(CaptureMode.Region);

    private void Canvas_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.Copy; e.DragUIOverride.Caption = "Open in editor"; }
    }

    private async void Canvas_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<StorageFile>().FirstOrDefault(f => NamingTemplate.TryFormatFromExtension(f.Path, out _) || f.FileType.Equals(".bmp", StringComparison.OrdinalIgnoreCase));
        if (file == null) { App.MainWindow.ShowBanner("Unsupported file", "Drop a PNG, JPEG, WebP or BMP image.", InfoBarSeverity.Warning); return; }
        if (!await App.MainWindow.ConfirmDiscardEditorChangesAsync()) return;
        OpenPath(file.Path);
    }

    private void UpdateOutputFormat()
    {
        var s = _settings.Current;
        OutputFormat.Text = s.ScreenshotFormat switch
        {
            ImageFormat.Png => "PNG · Lossless",
            ImageFormat.Jpeg => $"JPEG · {s.ImageQuality}%",
            _ => $"WebP · {s.ImageQuality}%",
        };
    }

    // ---- keyboard ------------------------------------------------------------------------------------------

    private void AddAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers mods, Action action)
        {
            var a = new KeyboardAccelerator { Key = key, Modifiers = mods };
            a.Invoked += (_, e) =>
            {
                if (FocusManager.GetFocusedElement(XamlRoot) is TextBox && key is VirtualKey.Z or VirtualKey.Y or VirtualKey.C or VirtualKey.V) return;
                action();
                e.Handled = true;
            };
            KeyboardAccelerators.Add(a);
        }
        Add(VirtualKey.Z, VirtualKeyModifiers.Control, () => Undo_Click(this, new RoutedEventArgs()));
        Add(VirtualKey.Y, VirtualKeyModifiers.Control, () => Redo_Click(this, new RoutedEventArgs()));
        Add(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => Redo_Click(this, new RoutedEventArgs()));
        Add(VirtualKey.S, VirtualKeyModifiers.Control, () => _ = SaveAsync(false));
        Add(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _ = SaveAsync(true));
        Add(VirtualKey.C, VirtualKeyModifiers.Control, CopyFlattened);
        Add(VirtualKey.V, VirtualKeyModifiers.Control, () => _ = PasteAsync());
        Add(VirtualKey.O, VirtualKeyModifiers.Control, () => Open_Click(this, new RoutedEventArgs()));
        Add(VirtualKey.Number0, VirtualKeyModifiers.Control, FitToWindow);
        Add(VirtualKey.Number1, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => SetZoom(1));
        Add((VirtualKey)0xBB, VirtualKeyModifiers.Control, () => SetZoom(_zoom * 1.25)); // Ctrl+=
        Add((VirtualKey)0xBD, VirtualKeyModifiers.Control, () => SetZoom(_zoom / 1.25)); // Ctrl+-
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or ComboBox) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl || _doc == null) return;
        EditorTool? tool = e.Key switch
        {
            VirtualKey.V => EditorTool.Select, VirtualKey.C => EditorTool.Crop, VirtualKey.A => EditorTool.Arrow, VirtualKey.L => EditorTool.Line,
            VirtualKey.R => EditorTool.Rectangle, VirtualKey.E => EditorTool.Ellipse, VirtualKey.T => EditorTool.Text, VirtualKey.N => EditorTool.Step,
            VirtualKey.H => EditorTool.Highlight, VirtualKey.B => EditorTool.Blur, VirtualKey.P => EditorTool.Pixelate, _ => null,
        };
        if (tool is { } t) { SetTool(t); e.Handled = true; return; }
        switch (e.Key)
        {
            case VirtualKey.Delete or VirtualKey.Back: DeleteSelected(); e.Handled = true; break;
            case VirtualKey.Escape: _selected = null; RefreshLayers(); RefreshInspector(); RequestRender(); e.Handled = true; break;
            case VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down when _selected != null:
                bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
                float step = shift ? 10 : 1;
                OpenPropCheckpoint();
                _selected.Translate(e.Key == VirtualKey.Left ? -step : e.Key == VirtualKey.Right ? step : 0, e.Key == VirtualKey.Up ? -step : e.Key == VirtualKey.Down ? step : 0);
                RefreshChrome();
                RequestRender();
                e.Handled = true;
                break;
        }
    }
}
