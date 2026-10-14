using System.Net;

namespace KitchenCore.Droid;

/// <summary>
/// The two screens the app shows itself, before or instead of the server: the
/// first-run question and "the server cannot be reached". Self-contained HTML in
/// the app's colours, as on the desktop (KitchenCore.Desktop/SetupPage.cs), so
/// the step before the app looks like the app.
///
/// They answer by navigating to <c>kitchencore://...</c>, which MainActivity
/// intercepts. French when the phone is, English otherwise -- the server's own
/// language setting is not known yet.
/// </summary>
internal static class LocalPages
{
    private static readonly bool French = Java.Util.Locale.Default.Language == "fr";

    private static string T(string fr, string en) => French ? fr : en;

    public static string Setup(string? server, string? device, string? error) => Page($$"""
        <form class="card" id="form">
          <h1>KitchenCore</h1>
          <p class="lead">{{T(
              "L'adresse du serveur KitchenCore de la famille. Ce téléphone demandera ensuite l'accès, à approuver par un administrateur.",
              "The address of the family's KitchenCore server. This phone then asks for access, which an admin approves.")}}</p>

          {{Error(error)}}

          <label class="field">
            <span class="label">{{T("Adresse du serveur", "Server address")}}</span>
            <!-- text, not url: type=url refuses an address without its scheme. -->
            <input type="text" id="server" inputmode="url" autocapitalize="off" autocorrect="off" spellcheck="false"
                   placeholder="192.168.1.20:8080" value="{{Encode(server)}}" required>
            <small class="hint">{{T("http:// est ajouté s'il manque.", "http:// is assumed if you leave the scheme off.")}}</small>
          </label>

          <label class="field">
            <span class="label">{{T("Nom de cet appareil", "A name for this device")}}</span>
            <input type="text" id="device" value="{{Encode(device)}}" required>
            <small class="hint">{{T("Ce que verra l'administrateur dans la demande d'accès.", "What the admin sees on the access request.")}}</small>
          </label>

          <button type="submit" id="go">{{T("Se connecter", "Connect")}}</button>
        </form>

        <script>
          document.getElementById('form').addEventListener('submit', event => {
            event.preventDefault();
            document.getElementById('go').disabled = true;
            document.getElementById('go').textContent = '{{T("Connexion…", "Connecting…")}}';

            const query = new URLSearchParams({
              server: document.getElementById('server').value.trim(),
              device: document.getElementById('device').value.trim(),
            });

            location.href = 'kitchencore://setup?' + query;
          });
        </script>
        """);

    public static string Offline(string server, string? reason) => Page($$"""
        <div class="card">
          <h1>{{T("Serveur injoignable", "Cannot reach the server")}}</h1>
          <p class="lead">{{T(
              "Vérifiez que ce téléphone est sur le bon réseau et que le serveur tourne.",
              "Check this phone is on the right network and the server is running.")}}</p>
          <p class="where">{{Encode(server)}}</p>
          {{Error(reason)}}
          <a class="button" href="kitchencore://retry">{{T("Réessayer", "Try again")}}</a>
          <a class="button button--quiet" href="kitchencore://change">{{T("Changer de serveur", "Change server")}}</a>
        </div>
        """);

    private static string Error(string? message) =>
        message is null ? string.Empty : $"<p class=\"error\">{Encode(message)}</p>";

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private static string Page(string body) => $$"""
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>
          :root {
            color-scheme: light dark;
            --surface: #f8faf6; --card: #ffffff; --text: #191d18; --muted: #5a6157;
            --line: #d5dbd1; --accent: #3c6b4f; --on-accent: #ffffff; --error: #b3261e;
          }
          @media (prefers-color-scheme: dark) {
            :root {
              --surface: #111411; --card: #1d211c; --text: #e1e4dd; --muted: #c0c8bc;
              --line: #404942; --accent: #a3d5b3; --on-accent: #0b3b21; --error: #f2b8b5;
            }
          }
          * { box-sizing: border-box; }
          body {
            margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 1rem;
            background: var(--surface); color: var(--text); font-family: system-ui, sans-serif;
          }
          .card {
            width: min(28rem, 100%); padding: 1.5rem; background: var(--card);
            border: 1px solid var(--line); border-radius: 16px;
          }
          h1 { margin: 0 0 .25rem; font-size: 1.25rem; }
          p.lead { margin: 0 0 1.25rem; color: var(--muted); font-size: .9rem; line-height: 1.45; }
          label.field { display: block; margin-bottom: 1rem; }
          span.label { display: block; margin-bottom: .25rem; font-size: .8rem; font-weight: 600; color: var(--muted); }
          input {
            width: 100%; min-height: 3rem; padding: .5rem .75rem; font: inherit; font-size: 1rem; color: var(--text);
            background: var(--surface); border: 1px solid var(--line); border-radius: 8px;
          }
          input:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
          small.hint { display: block; margin-top: .25rem; color: var(--muted); font-size: .75rem; line-height: 1.4; }
          button, .button {
            display: grid; place-items: center; width: 100%; min-height: 3rem; margin-top: .5rem;
            font: inherit; font-weight: 600; text-decoration: none;
            color: var(--on-accent); background: var(--accent); border: 0; border-radius: 999px;
          }
          .button--quiet { color: var(--accent); background: transparent; border: 1px solid var(--line); }
          button:disabled { opacity: .6; }
          .error {
            margin: 0 0 1rem; padding: .5rem .75rem; border-radius: 8px;
            background: color-mix(in srgb, var(--error) 15%, transparent);
            color: var(--error); font-size: .85rem; word-break: break-word;
          }
          .where { margin: 0 0 1rem; font-size: .85rem; color: var(--muted); word-break: break-all; }
        </style>
        </head>
        <body>
        {{body}}
        </body>
        </html>
        """;
}
