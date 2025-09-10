using System.Text.RegularExpressions;

namespace SemanticDeveloper.Services;

public static class TextFilter
{
    private static readonly Regex AnsiRegex = new("\\u001B\\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    public static string StripAnsi(string input) => string.IsNullOrEmpty(input) ? input : AnsiRegex.Replace(input, string.Empty);
}
