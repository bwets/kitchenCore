using KitchenCore.Core.Config;
using KitchenCore.Shared;

namespace KitchenCore.Server.Auth;

/// <summary>
/// Who the current request is. Resolved from the X-Device-Token header, which is
/// what every device stores after it is granted access.
/// </summary>
public sealed class DeviceContext(IHttpContextAccessor accessor, DeviceStore devices)
{
    public const string HeaderName = "X-Device-Token";

    public Identity Current =>
        devices.Identify(accessor.HttpContext?.Request.Headers[HeaderName].FirstOrDefault());
}
