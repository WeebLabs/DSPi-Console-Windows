using System.Text;

namespace DSPiConsole.Core.Models;

/// <summary>
/// The firmware stores a channel name in 32 bytes: up to 31 bytes of UTF-8 plus
/// a terminator. Names are fitted here before they reach the model, so the app
/// never holds a name the device has silently cut short.
/// </summary>
public static class ChannelNameLimit
{
    public const int MaxBytes = 31;

    /// <summary>Longest prefix of <paramref name="name"/> whose UTF-8 encoding
    /// fits in <see cref="MaxBytes"/>, never splitting a character.</summary>
    public static string Fit(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) <= MaxBytes) return name;
        var sb = new StringBuilder();
        int bytes = 0;
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(name);
        while (e.MoveNext())
        {
            string element = e.GetTextElement();
            int n = Encoding.UTF8.GetByteCount(element);
            if (bytes + n > MaxBytes) break;
            sb.Append(element);
            bytes += n;
        }
        return sb.ToString();
    }
}
