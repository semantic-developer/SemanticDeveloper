using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace SemanticDeveloper.Views;

public class LogPrefixColorizer : DocumentColorizingTransformer
{
    private static readonly SolidColorBrush UserBrush = new SolidColorBrush(Color.Parse("#4FC1FF"));
    private static readonly SolidColorBrush AssistantBrush = new SolidColorBrush(Color.Parse("#A5D6A7"));
    private static readonly SolidColorBrush SystemBrush = new SolidColorBrush(Color.Parse("#FFC66D"));

    protected override void ColorizeLine(DocumentLine line)
    {
        var doc = CurrentContext?.Document;
        if (doc is null) return;
        var text = doc.GetText(line);
        if (string.IsNullOrEmpty(text)) return;

        if (text.StartsWith("You:"))
        {
            ChangeLinePart(line.Offset, line.Offset + 4, el =>
            {
                el.TextRunProperties.SetForegroundBrush(UserBrush);
                el.TextRunProperties.SetTypeface(new Typeface(el.TextRunProperties.Typeface.FontFamily, FontStyle.Normal, FontWeight.SemiBold));
            });
        }
        else if (text.StartsWith("Assistant:"))
        {
            ChangeLinePart(line.Offset, line.Offset + 10, el =>
            {
                el.TextRunProperties.SetForegroundBrush(AssistantBrush);
                el.TextRunProperties.SetTypeface(new Typeface(el.TextRunProperties.Typeface.FontFamily, FontStyle.Normal, FontWeight.SemiBold));
            });
        }
        else if (text.StartsWith("System:"))
        {
            ChangeLinePart(line.Offset, line.Offset + 7, el =>
            {
                el.TextRunProperties.SetForegroundBrush(SystemBrush);
                el.TextRunProperties.SetTypeface(new Typeface(el.TextRunProperties.Typeface.FontFamily, FontStyle.Normal, FontWeight.SemiBold));
            });
        }
    }
}
