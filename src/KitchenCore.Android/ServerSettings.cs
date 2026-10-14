using Android.Content;

namespace KitchenCore.Droid;

/// <summary>
/// The server's address and this device's name, in the app's preferences. The
/// device token is not here: the page keeps it in localStorage, as in any
/// browser, and the WebView persists that by itself.
/// </summary>
internal sealed class ServerSettings(Context context)
{
    private readonly ISharedPreferences _preferences = context.GetSharedPreferences("kitchencore", FileCreationMode.Private)!;

    public string? Server
    {
        get => _preferences.GetString("server", null);
        set => Save("server", value);
    }

    public string? Device
    {
        get => _preferences.GetString("device", null);
        set => Save("device", value);
    }

    private void Save(string key, string? value)
    {
        using var editor = _preferences.Edit()!;
        editor.PutString(key, value);
        editor.Apply();
    }

    /// <summary>
    /// http:// unless a scheme is given, no trailing slash. Null when it is not a
    /// web address at all. The same rules as the desktop app's.
    /// </summary>
    public static string? NormalizeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var text = input.Trim();

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        return System.Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == System.Uri.UriSchemeHttp || uri.Scheme == System.Uri.UriSchemeHttps)
            ? text.TrimEnd('/')
            : null;
    }

    /// <summary>Asks the server's health check. Null when it answered, else why not.</summary>
    public static async Task<string?> ProbeAsync(string server)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        try
        {
            using var response = await http.GetAsync($"{server}/healthz");
            return response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
        }
        catch (TaskCanceledException)
        {
            return "timeout";
        }
        catch (Exception ex)
        {
            return ex.GetBaseException().Message;
        }
    }
}
