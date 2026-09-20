using System.Net;
using KitchenCore.Core.Config;
using KitchenCore.Shared;

namespace KitchenCore.Server.Auth;

/// <summary>
/// Who the current request is. Resolved from the X-Device-Token header, which is
/// what every device stores after it is granted access.
///
/// One exception: single-user mode. When the desktop app hosts the server
/// in-process there is exactly one person -- the one at the keyboard -- and
/// asking them to register a device with themselves and then approve it would be
/// theatre. So that mode reports a standing admin instead.
///
/// It is gated on two things together, not one: the flag must be set, AND the
/// request must come from the loopback interface. The flag alone would turn a
/// mis-copied environment variable on a real server into open admin access; the
/// loopback check means that even then, only a process already on that machine
/// could use it.
/// </summary>
public sealed class DeviceContext(IHttpContextAccessor accessor, DeviceStore devices)
{
    public const string HeaderName = "X-Device-Token";

    /// <summary>Set by the desktop app when it hosts the server for one person.</summary>
    public const string SingleUserVariable = "KITCHENCORE_SINGLE_USER";

    /// <summary>
    /// Who that person is. It matters even with nobody to authenticate against:
    /// a standalone data folder is often a git clone shared with the family
    /// server, and the commits it produces need a name on them.
    /// </summary>
    public const string SingleUserNameVariable = "KITCHENCORE_SINGLE_USER_NAME";

    private static readonly bool SingleUserConfigured =
        Environment.GetEnvironmentVariable(SingleUserVariable) is "1" or "true";

    public Identity Current
    {
        get
        {
            var http = accessor.HttpContext;

            if (SingleUserConfigured && IsLoopback(http))
            {
                return SingleUser();
            }

            return devices.Identify(http?.Request.Headers[HeaderName].FirstOrDefault());
        }
    }

    private static bool IsLoopback(HttpContext? http)
    {
        var address = http?.Connection.RemoteIpAddress;

        // A null remote address means an in-process request, which is as local
        // as it gets.
        return address is null || IPAddress.IsLoopback(address);
    }

    private static Identity SingleUser() => new()
    {
        // The desktop app asked for this on first run and passes it through, so
        // commits are attributed to a person rather than to "someone".
        Name = Environment.GetEnvironmentVariable(SingleUserNameVariable) is { Length: > 0 } name
            ? name
            : Environment.UserName,
        Known = true,
        Approved = true,
        Admin = true,
        Sections = new Dictionary<string, SectionRole>
        {
            ["menu"] = SectionRole.Editor,
            ["shopping"] = SectionRole.Editor,
        },
    };
}
