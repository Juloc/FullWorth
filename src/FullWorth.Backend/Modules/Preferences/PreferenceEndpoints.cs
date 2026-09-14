using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Preferences;

public static class PreferenceEndpoints
{
    public static IEndpointRouteBuilder MapPreferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/preferences").WithTags("Preferences");

        group.MapGet("/{key}", async (string key, Guid fullWorthSpaceId, CurrentUserContext currentUser, PreferenceStore store, CancellationToken ct) =>
        {
            if (!PreferenceStore.AllowedKeys.Contains(key)) return Results.NotFound();
            var view = await store.GetAsync(currentUser.RequireUserId(), fullWorthSpaceId, key, ct);
            return view is null ? Results.NotFound() : Results.Ok(view);
        });

        group.MapPut("/{key}", async (string key, Guid fullWorthSpaceId, System.Text.Json.JsonElement value, CurrentUserContext currentUser, PreferenceStore store, HttpContext http, CancellationToken ct) =>
        {
            if (!PreferenceStore.AllowedKeys.Contains(key)) return Results.NotFound();
            var json = value.GetRawText();
            if (json.Length > PreferenceStore.MaxValueBytes) return Results.BadRequest(new { error = "Preference value too large." });
            return await store.SetAsync(currentUser.RequireUserId(), fullWorthSpaceId, key, json, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        return app;
    }
}
