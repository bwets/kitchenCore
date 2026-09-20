using System.Net;

namespace KitchenCore.Desktop;

/// <summary>
/// The first-run screen, as a self-contained HTML document.
///
/// Photino wraps the OS webview and deliberately offers no native dialogs, so
/// the setup prompt is a page rendered in the same window rather than a form
/// toolkit. That is a feature here: it looks like the app it is about to open,
/// and there is no second UI stack to carry for two fields.
///
/// It talks back through Photino's message channel -- window.external.sendMessage
/// -- which is the one piece of glue between the page and the host.
/// </summary>
public static class SetupPage
{
    public static string Html(DesktopConfig config, string? error = null) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>KitchenCore</title>
        <style>
          :root {
            color-scheme: light dark;
            --surface: #f8faf6;
            --card: #ffffff;
            --text: #191d18;
            --muted: #5a6157;
            --line: #d5dbd1;
            --accent: #3c6b4f;
            --on-accent: #ffffff;
            --error: #b3261e;
          }
          @media (prefers-color-scheme: dark) {
            :root {
              --surface: #111411; --card: #1d211c; --text: #e1e4dd;
              --muted: #c0c8bc; --line: #404942; --accent: #a3d5b3; --on-accent: #0b3b21;
              --error: #f2b8b5;
            }
          }
          * { box-sizing: border-box; }
          body {
            margin: 0; min-height: 100vh; display: grid; place-items: center;
            padding: 2rem; background: var(--surface); color: var(--text);
            font-family: "Segoe UI Variable", "Segoe UI", system-ui, -apple-system, sans-serif;
          }
          .card {
            width: min(26rem, 100%); padding: 2rem; background: var(--card);
            border: 1px solid var(--line); border-radius: 16px;
          }
          h1 { margin: 0 0 .25rem; font-size: 1.2rem; }
          p.lead { margin: 0 0 1.5rem; color: var(--muted); font-size: .9rem; line-height: 1.45; }
          label { display: block; margin-bottom: 1rem; }
          span.label { display: block; margin-bottom: .25rem; font-size: .8rem; font-weight: 600; color: var(--muted); }
          input {
            width: 100%; min-height: 2.75rem; padding: .5rem .75rem; font: inherit;
            color: var(--text); background: var(--surface);
            border: 1px solid var(--line); border-radius: 8px;
          }
          input:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
          small { display: block; margin-top: .25rem; color: var(--muted); font-size: .75rem; }
          button {
            width: 100%; min-height: 2.75rem; margin-top: .5rem; font: inherit; font-weight: 600;
            color: var(--on-accent); background: var(--accent);
            border: 0; border-radius: 999px; cursor: pointer;
          }
          button:disabled { opacity: .5; cursor: default; }
          .error {
            margin: 0 0 1rem; padding: .5rem .75rem; border-radius: 8px;
            background: color-mix(in srgb, var(--error) 15%, transparent);
            color: var(--error); font-size: .85rem;
          }
          .where { margin-top: 1.25rem; font-size: .7rem; color: var(--muted); word-break: break-all; }
        </style>
        </head>
        <body>
          <form class="card" id="form">
            <h1>KitchenCore</h1>
            <p class="lead">Point this computer at the family's KitchenCore server. You can change it later by editing the file below.</p>

            {{(error is null ? string.Empty : $"<p class=\"error\">{WebUtility.HtmlEncode(error)}</p>")}}

            <label>
              <span class="label">Server address</span>
              <input name="serverUrl" id="serverUrl" placeholder="kitchen.local:8080"
                     value="{{WebUtility.HtmlEncode(config.ServerUrl ?? string.Empty)}}" autofocus>
              <small>http:// is assumed if you leave the scheme off.</small>
            </label>

            <label>
              <span class="label">Name for this device</span>
              <input name="deviceName" id="deviceName" placeholder="Kitchen laptop"
                     value="{{WebUtility.HtmlEncode(config.DeviceName ?? Environment.MachineName)}}">
              <small>Shown to whoever approves this device.</small>
            </label>

            <button type="submit" id="go">Connect</button>

            <p class="where">{{WebUtility.HtmlEncode(DesktopConfigStore.Path_)}}</p>
          </form>

        <script>
          const form = document.getElementById('form');

          form.addEventListener('submit', event => {
            event.preventDefault();

            const payload = {
              serverUrl: document.getElementById('serverUrl').value.trim(),
              deviceName: document.getElementById('deviceName').value.trim(),
            };

            if (!payload.serverUrl || !payload.deviceName) {
              return;
            }

            document.getElementById('go').disabled = true;

            // The one piece of glue between this page and the host process.
            window.external.sendMessage(JSON.stringify(payload));
          });
        </script>
        </body>
        </html>
        """;
}
