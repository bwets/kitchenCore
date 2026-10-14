using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using AndroidX.Core.Content;
using Uri = Android.Net.Uri;

namespace KitchenCore.Droid;

/// <summary>
/// KitchenCore on a phone: one WebView onto the family's server.
///
/// The first run asks for the server's address (and a name for the device), then
/// opens the server's own access page, which runs the usual device approval --
/// the token lands in the WebView's localStorage like in any browser, so there is
/// nothing to carry on this side. After that it opens straight onto the app.
///
/// The parts a bare WebView does not do by itself, and which this class adds:
/// the file picker and the camera behind the shopping list's photo buttons, the
/// back button walking the page's history, links to other sites opening in the
/// browser, and a way out when the server cannot be reached.
/// </summary>
[Activity(
    Label = "KitchenCore",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = SoftInput.AdjustResize,
    // Turning the phone keeps the page: a reload would boot the Blazor app again.
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize |
                           ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : Activity
{
    private const int CameraRequest = 1;
    private const int PickRequest = 2;

    private WebView _web = null!;
    private ServerSettings _settings = null!;

    // True while one of LocalPages is showing rather than the server.
    private bool _local;

    private IValueCallback? _chooser;
    private Java.IO.File? _snap;
    private BackCallback? _back;
    private bool _backRegistered;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _settings = new ServerSettings(this);
        ForgetOldSnaps();

        _web = new WebView(this);
        _web.SetBackgroundColor(Color.Transparent);

        var web = _web.Settings;
        web.JavaScriptEnabled = true;
        web.DomStorageEnabled = true;
        web.AllowFileAccess = false;
        web.UserAgentString += " KitchenCoreAndroid";

        _web.SetWebViewClient(new Client(this));
        _web.SetWebChromeClient(new Chrome(this));

#if DEBUG
        WebView.SetWebContentsDebuggingEnabled(true);
#endif

        var root = new FrameLayout(this);
        root.AddView(_web, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Android 15 draws every app edge to edge, under the status and
        // navigation bars, and stops resizing the window for the keyboard. Pad
        // the page clear of both, or the header sits under the clock and the add
        // box under the keyboard.
        if (OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            root.SetOnApplyWindowInsetsListener(new EdgePadding());
        }

        SetContentView(root);

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            _back = new BackCallback(this);
        }

        var restored = savedInstanceState is not null &&
                       !savedInstanceState.GetBoolean("local") &&
                       _web.RestoreState(savedInstanceState) is not null;

        if (restored)
        {
            return;
        }

        if (_settings.Server is { } server)
        {
            Open($"{server}/");
        }
        else
        {
            ShowSetup(error: null);
        }
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutBoolean("local", _local);

        if (!_local)
        {
            _web.SaveState(outState);
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        _web.OnResume();
    }

    protected override void OnPause()
    {
        _web.OnPause();
        CookieManager.Instance?.Flush();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        _chooser?.OnReceiveValue(null);
        _chooser = null;
        _web.Destroy();
        base.OnDestroy();
    }

    // Before Android 13. From 13 the dispatcher callback below does this, and
    // from 16 this is no longer called at all.
#pragma warning disable CA1422 // Obsolete from 33, which is exactly when it stops being the path.
    public override void OnBackPressed()
    {
        if (!_local && _web.CanGoBack())
        {
            _web.GoBack();
        }
        else
        {
            base.OnBackPressed();
        }
    }
#pragma warning restore CA1422

    /// <summary>
    /// Back goes back through the page while there is somewhere to go, and leaves
    /// the app otherwise. Registered only while there is, so leaving keeps the
    /// system's predictive animation.
    /// </summary>
    private void UpdateBack()
    {
        if (_back is null || !OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return;
        }

        var wanted = !_local && _web.CanGoBack();

        if (wanted == _backRegistered)
        {
            return;
        }

        if (wanted)
        {
            OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0 /* PRIORITY_DEFAULT */, _back);
        }
        else
        {
            OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(_back);
        }

        _backRegistered = wanted;
    }

    private void Open(string url)
    {
        _local = false;
        _web.LoadUrl(url);
    }

    private void ShowSetup(string? error)
    {
        ShowLocal(LocalPages.Setup(_settings.Server, _settings.Device ?? DefaultDeviceName(), error));
    }

    private void ShowOffline(string? reason)
    {
        ShowLocal(LocalPages.Offline(_settings.Server ?? string.Empty, reason));
    }

    private void ShowLocal(string html)
    {
        _local = true;
        _web.LoadDataWithBaseURL(null, html, "text/html", "utf-8", null);
        UpdateBack();
    }

    /// <summary>What the phone calls itself in Settings, else its model.</summary>
    private string DefaultDeviceName() =>
        Settings.Global.GetString(ContentResolver, "device_name") is { Length: > 0 } name ? name : Build.Model ?? "Android";

    /// <summary>The answers from LocalPages. Only honoured while one of them is showing.</summary>
    private async void OnLocalAction(Uri uri)
    {
        switch (uri.Host)
        {
            case "setup":
                var server = ServerSettings.NormalizeUrl(uri.GetQueryParameter("server"));
                var device = uri.GetQueryParameter("device")?.Trim();

                if (server is null)
                {
                    ShowSetup(LocalText("Ce n'est pas une adresse web.", "That does not look like a web address."));
                    return;
                }

                // Checked before saving, so a typo is caught here rather than
                // turning into an offline screen on every start.
                if (await ServerSettings.ProbeAsync(server) is { } problem)
                {
                    _settings.Device = device;
                    ShowSetup(LocalText($"{server} ne répond pas : {problem}", $"{server} did not answer: {problem}"));
                    return;
                }

                _settings.Server = server;
                _settings.Device = device;

                // The device name rides along so the access page can prefill it,
                // as the desktop app does.
                Open($"{server}/access?device={System.Uri.EscapeDataString(device ?? string.Empty)}");
                break;

            case "retry" when _settings.Server is { } current:
                Open($"{current}/");
                break;

            default:
                ShowSetup(error: null);
                break;
        }
    }

    private static string LocalText(string fr, string en) =>
        Java.Util.Locale.Default.Language == "fr" ? fr : en;

    /// <summary>Same host and port as the server: stays in the app. Anything else is someone else's site.</summary>
    private bool IsServer(Uri uri) =>
        _settings.Server is { } server &&
        Uri.Parse(server) is { } home &&
        string.Equals(uri.Host, home.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == home.Port;

    // --- Pictures -----------------------------------------------------------

    /// <summary>
    /// The page's file inputs. The shopping list's camera button asks for a
    /// capture, which goes straight to the camera app -- one tap, as in a mobile
    /// browser; the other button opens the picker.
    /// </summary>
    private bool ShowFileChooser(IValueCallback callback, WebChromeClient.FileChooserParams? options)
    {
        // A chooser left open (the activity was recreated) would otherwise block
        // every later one.
        _chooser?.OnReceiveValue(null);
        _chooser = callback;

        if (options?.IsCaptureEnabled == true && StartCamera())
        {
            return true;
        }

        var intent = options?.CreateIntent() ?? new Intent(Intent.ActionGetContent).SetType("*/*").AddCategory(Intent.CategoryOpenable);

        if (options?.Mode == ChromeFileChooserMode.OpenMultiple)
        {
            intent.PutExtra(Intent.ExtraAllowMultiple, true);
        }

        try
        {
            StartActivityForResult(intent, PickRequest);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            // Returning false tells the WebView the callback will not be called.
            _chooser = null;
            return false;
        }
    }

    private bool StartCamera()
    {
        var folder = new Java.IO.File(CacheDir, "photos");
        folder.Mkdirs();

        _snap = new Java.IO.File(folder, $"snap-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");

        var intent = new Intent(MediaStore.ActionImageCapture);
        intent.PutExtra(MediaStore.ExtraOutput, FileProvider.GetUriForFile(this, PackageName + ".photos", _snap));
        intent.AddFlags(ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission);

        try
        {
            StartActivityForResult(intent, CameraRequest);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            // No camera app: fall back to the picker.
            _snap = null;
            return false;
        }
    }

    protected override void OnActivityResult(int requestCode, [GeneratedEnum] Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (_chooser is not { } chooser)
        {
            return;
        }

        _chooser = null;
        Uri[]? picked = null;

        if (requestCode == CameraRequest)
        {
            if (resultCode == Result.Ok && _snap is { } snap && snap.Exists() && snap.Length() > 0)
            {
                picked = [FileProvider.GetUriForFile(this, PackageName + ".photos", snap)!];
            }

            _snap = null;
        }
        else if (requestCode == PickRequest && resultCode == Result.Ok && data is not null)
        {
            picked = PickedUris(data);
        }

        // Always answered, null on cancel: an unanswered callback leaves the
        // input dead until the page reloads.
        chooser.OnReceiveValue(picked is { Length: > 0 }
            ? new Java.Lang.Object(JNIEnv.NewArray(picked), JniHandleOwnership.TransferLocalRef)
            : null);
    }

    private static Uri[] PickedUris(Intent data)
    {
        if (data.ClipData is { ItemCount: > 0 } clip)
        {
            return Enumerable.Range(0, clip.ItemCount)
                .Select(i => clip.GetItemAt(i)?.Uri)
                .OfType<Uri>()
                .ToArray();
        }

        return data.Data is { } single ? [single] : [];
    }

    /// <summary>A snap is uploaded within seconds; anything a day old was abandoned.</summary>
    private void ForgetOldSnaps()
    {
        var folder = new Java.IO.File(CacheDir, "photos");
        var cutoff = Java.Lang.JavaSystem.CurrentTimeMillis() - (long)TimeSpan.FromDays(1).TotalMilliseconds;

        foreach (var file in folder.ListFiles() ?? [])
        {
            if (file.LastModified() < cutoff)
            {
                file.Delete();
            }
        }
    }

    // --- WebView plumbing ---------------------------------------------------

    private sealed class Client(MainActivity activity) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            if (request?.Url is not { } uri)
            {
                return false;
            }

            if (uri.Scheme == "kitchencore")
            {
                if (activity._local)
                {
                    activity.OnLocalAction(uri);
                }

                return true;
            }

            if (uri.Scheme is "http" or "https" && activity.IsServer(uri))
            {
                return false;
            }

            // A recipe link in a menu entry, a mailto:, a tel: -- the phone's own apps.
            try
            {
                activity.StartActivity(new Intent(Intent.ActionView, uri));
            }
            catch (ActivityNotFoundException)
            {
            }

            return true;
        }

        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            // Only the page itself. A failed image or API call is the app's to
            // show, and it does.
            if (request?.IsForMainFrame == true && !activity._local)
            {
                activity.ShowOffline(error?.Description);
            }
        }

        public override void DoUpdateVisitedHistory(WebView? view, string? url, bool isReload)
        {
            base.DoUpdateVisitedHistory(view, url, isReload);
            activity.UpdateBack();
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);

            // A local page has no history worth going back to: back from the
            // offline screen should leave, not return to the failed load.
            if (activity._local)
            {
                view?.ClearHistory();
            }

            activity.UpdateBack();
        }
    }

    private sealed class Chrome(MainActivity activity) : WebChromeClient
    {
        public override bool OnShowFileChooser(WebView? webView, IValueCallback? filePathCallback, FileChooserParams? fileChooserParams) =>
            filePathCallback is not null && activity.ShowFileChooser(filePathCallback, fileChooserParams);
    }

    private sealed class BackCallback(MainActivity activity) : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
    {
        public void OnBackInvoked()
        {
            if (activity._web.CanGoBack())
            {
                activity._web.GoBack();
            }
        }
    }

    private sealed class EdgePadding : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                return insets;
            }

            var edges = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout() | WindowInsets.Type.Ime());
            v.SetPadding(edges.Left, edges.Top, edges.Right, edges.Bottom);
            return WindowInsets.Consumed;
        }
    }
}
