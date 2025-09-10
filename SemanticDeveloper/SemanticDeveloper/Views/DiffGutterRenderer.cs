using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;

namespace SemanticDeveloper.Views;

// Draws +/- indicators and a subtle background tint per changed line
// inside the text view area (at the far-left of each visual line).
public class DiffGutterRenderer : IBackgroundRenderer
{
    public HashSet<int> AddedLines { get; } = new();
    public HashSet<int> RemovedLines { get; } = new();

    public bool ShowAdded { get; set; }
    public bool ShowRemoved { get; set; }

    public IBrush AddedBrush { get; set; } = new SolidColorBrush(Color.Parse("#2E7D32"));
    public IBrush RemovedBrush { get; set; } = new SolidColorBrush(Color.Parse("#C62828"));
    public IBrush GlyphBrush { get; set; } = Brushes.White;
    public double GutterWidth { get; set; } = 14.0;

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.VisualLinesValid == false) return;
        var left = 0.0; // draw at the very left inside the text view
        var typeface = new Typeface("Consolas");

        foreach (var vline in textView.VisualLines)
        {
            var firstDocLine = vline.FirstDocumentLine.LineNumber;
            bool isAdd = ShowAdded && AddedLines.Contains(firstDocLine);
            bool isDel = ShowRemoved && RemovedLines.Contains(firstDocLine);
            if (!isAdd && !isDel) continue;

            var brush = isAdd ? AddedBrush : RemovedBrush;
            var glyph = isAdd ? "+" : "−"; // U+2212 for minus

            var y = vline.VisualTop - textView.VerticalOffset;
            var rect = new Rect(left, y, GutterWidth, vline.Height);

            try
            {
                using var _ = drawingContext.PushOpacity(0.22);
                drawingContext.FillRectangle(brush, rect);
            }
            catch { }

            try
            {
                var fontSize = 12.0;
                var tl = new TextLayout(glyph, typeface, fontSize, GlyphBrush);
                var gx = rect.X + (rect.Width - 8) / 2; // approx center
                var gy = rect.Y + (rect.Height - fontSize) / 2;
                tl.Draw(drawingContext, new Point(gx, gy));
            }
            catch { }
        }
    }

    public void Update(HashSet<int>? added, HashSet<int>? removed)
    {
        AddedLines.Clear();
        RemovedLines.Clear();
        if (added != null) foreach (var l in added) AddedLines.Add(l);
        if (removed != null) foreach (var l in removed) RemovedLines.Add(l);
    }
}
