using System.Globalization;
using System.Text;

namespace Yohuke.LiveRecorder.Infrastructure.Analytics;

internal static class AnalyticsSpreadsheetText
{
    internal const int MAX_CELL_LENGTH = 32767;

    internal static IReadOnlyList<string> Split(string value)
    {
        var parts = new List<string>();
        var part = new StringBuilder(Math.Min(value.Length, MAX_CELL_LENGTH));
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            string token;
            if (character == '_' && IsEscape(value, index))
            {
                token = "_x005F_";
            }
            else if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                token = value.Substring(index++, 2);
            }
            else if (character is '\r' or '\uFFFE' or '\uFFFF' || character < ' ' && character is not ('\t' or '\n') || char.IsSurrogate(character))
            {
                token = "_x" + ((int)character).ToString("X4", CultureInfo.InvariantCulture) + "_";
            }
            else
            {
                token = character.ToString();
            }
            if (part.Length + token.Length > MAX_CELL_LENGTH)
            {
                parts.Add(part.ToString());
                part.Clear();
            }
            part.Append(token);
        }
        parts.Add(part.ToString());
        return parts;
    }

    private static bool IsEscape(string value, int index)
    {
        if (index + 6 >= value.Length || value[index + 1] != 'x' || value[index + 6] != '_')
        {
            return false;
        }
        return value.AsSpan(index + 2, 4).IndexOfAnyExcept("0123456789ABCDEFabcdef") < 0;
    }
}
