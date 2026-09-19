using KitchenCore.Shared;

namespace KitchenCore.Server.Auth;

/// <summary>
/// Endpoint filters that enforce a role for a section.
///
/// Authorization sits on the endpoints rather than inside the store, so the
/// store stays a plain file API that tests can drive without pretending to be a
/// device.
/// </summary>
public static class RoleFilter
{
    public static TBuilder RequireRole<TBuilder>(this TBuilder builder, Section section, SectionRole role)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var devices = context.HttpContext.RequestServices.GetRequiredService<DeviceContext>();
            var identity = devices.Current;

            if (!identity.Known)
            {
                return Results.Problem(
                    title: "This device is not registered.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!identity.Approved)
            {
                // Distinct from 401 on purpose: the device did everything right
                // and is simply waiting for a person to approve it.
                return Results.Problem(
                    title: "This device is waiting for approval.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (identity.RoleFor(section) < role)
            {
                return Results.Problem(
                    title: $"This device may not {role.ToString().ToLowerInvariant()} in {section}.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return await next(context);
        });

        return builder;
    }

    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var devices = context.HttpContext.RequestServices.GetRequiredService<DeviceContext>();

            if (!devices.Current.Admin)
            {
                return Results.Problem(
                    title: "Administrator access is required.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return await next(context);
        });

        return builder;
    }
}
