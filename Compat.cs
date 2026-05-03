using System.Runtime.InteropServices;
using System.Text;
#if NETFRAMEWORK
using System.Runtime.Serialization.Json;
#else
using System.Text.Json;
#endif

namespace CanScanmatik;

internal static class Compat
{
    public static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    public static bool ContainsIgnoreCase(string text, string value)
    {
        return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static string RemoveHexPrefix(string text)
    {
        return ReplaceIgnoreCase(text, "0x", string.Empty);
    }

    private static string ReplaceIgnoreCase(string text, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(oldValue))
        {
            return text;
        }

        StringBuilder builder = new();
        int startIndex = 0;
        while (true)
        {
            int matchIndex = text.IndexOf(oldValue, startIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                builder.Append(text, startIndex, text.Length - startIndex);
                return builder.ToString();
            }

            builder.Append(text, startIndex, matchIndex - startIndex);
            builder.Append(newValue);
            startIndex = matchIndex + oldValue.Length;
        }
    }
}

internal static class WinFormsCompat
{
    public static void SetPlaceholderText(TextBox textBox, string placeholderText)
    {
#if NETFRAMEWORK
        _ = SendMessage(textBox.Handle, EmSetCueBanner, IntPtr.Zero, placeholderText);
#else
        textBox.PlaceholderText = placeholderText;
#endif
    }

#if NETFRAMEWORK
    private const int EmSetCueBanner = 0x1501;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
#endif
}

internal static class JsonCompat
{
    public static string Serialize<T>(T value)
    {
#if NETFRAMEWORK
        using MemoryStream stream = new();
        DataContractJsonSerializer serializer = new(typeof(T));
        serializer.WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
#else
        JsonSerializerOptions options = new()
        {
            WriteIndented = true
        };

        return JsonSerializer.Serialize(value, options);
#endif
    }

    public static T? Deserialize<T>(string json)
    {
#if NETFRAMEWORK
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        DataContractJsonSerializer serializer = new(typeof(T));
        return (T?)serializer.ReadObject(stream);
#else
        return JsonSerializer.Deserialize<T>(json);
#endif
    }
}
