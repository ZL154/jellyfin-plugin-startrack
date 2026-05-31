using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.InternalRating;

/// <summary>
/// Lightweight projection of Jellyfin's <c>User</c> entity that the plugin
/// can rely on across Jellyfin ABI versions.
/// </summary>
internal sealed class UserAbi
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public bool HasProfileImage { get; init; }
}

/// <summary>
/// v1.5.3: ABI compatibility shim for Jellyfin user enumeration that survives
/// breaking changes between 10.11.6 → 10.11.10.
///
/// Confirmed at runtime on Jellyfin 10.11.10:
/// - <c>IUserManager.Users</c> property is GONE from the interface
///   (reflection lookup returns null).
/// - <c>IUserManager.GetUsers()</c> method is present and returns
///   <c>Jellyfin.Database.Implementations.Entities.User[]</c>.
///
/// Plugins compiled against 10.11.6-10.11.8 referenced <c>Users</c> directly,
/// which throws <see cref="System.MissingMethodException"/> at JIT time on
/// 10.11.9+ because the property no longer exists on the interface.
///
/// We try <c>GetUsers()</c> first (newer API), then fall back to <c>Users</c>
/// for older Jellyfin hosts. Both via reflection so the JIT can't bind to a
/// specific signature at IL emit time.
///
/// We extract <see cref="UserAbi.Id"/> and <see cref="UserAbi.Username"/>
/// via reflection on each item rather than casting to a compile-time
/// <c>User</c> token. Jellyfin loads plugins in an isolated
/// <c>AssemblyLoadContext</c>, so a typed <c>is User u</c> cast can fail
/// when the runtime User assembly version differs from the plugin's
/// compile-time reference. Callers only need Id and Username, so we never
/// touch the runtime User type directly.
///
/// Empty enumeration on any failure so callers degrade gracefully instead
/// of 500-ing user-facing endpoints.
/// </summary>
internal static class IUserManagerAbiExtensions
{
    public static IEnumerable<UserAbi> EnumerateAll(this IUserManager userManager)
    {
        if (userManager is null) yield break;

        IEnumerable? raw = ResolveUserCollection(userManager);
        if (raw is null) yield break;

        PropertyInfo? idProp = null;
        PropertyInfo? usernameProp = null;
        PropertyInfo? profileImageProp = null;

        foreach (var item in raw)
        {
            if (item is null) continue;
            var t = item.GetType();
            if (idProp is null || idProp.DeclaringType != t)
            {
                idProp = t.GetProperty("Id");
            }
            if (usernameProp is null || usernameProp.DeclaringType != t)
            {
                usernameProp = t.GetProperty("Username");
            }
            if (profileImageProp is null || profileImageProp.DeclaringType != t)
            {
                profileImageProp = t.GetProperty("ProfileImage");
            }

            Guid id = Guid.Empty;
            string username = string.Empty;
            bool hasImage = false;
            try
            {
                if (idProp?.GetValue(item) is Guid g) id = g;
                username = usernameProp?.GetValue(item) as string ?? string.Empty;
                var imgObj = profileImageProp?.GetValue(item);
                if (imgObj is not null)
                {
                    var pathProp = imgObj.GetType().GetProperty("Path");
                    hasImage = pathProp?.GetValue(imgObj) is string p && !string.IsNullOrEmpty(p);
                }
            }
            catch
            {
                continue;
            }

            if (id == Guid.Empty) continue;
            yield return new UserAbi { Id = id, Username = username, HasProfileImage = hasImage };
        }
    }

    private static IEnumerable? ResolveUserCollection(IUserManager userManager)
    {
        try
        {
            var getUsers = userManager.GetType().GetMethod(
                "GetUsers",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (getUsers is not null)
            {
                return getUsers.Invoke(userManager, null) as IEnumerable;
            }
        }
        catch
        {
        }

        try
        {
            var usersProp = typeof(IUserManager).GetProperty("Users");
            if (usersProp is not null)
            {
                return usersProp.GetValue(userManager) as IEnumerable;
            }
        }
        catch
        {
        }

        try
        {
            var runtimeUsersProp = userManager.GetType().GetProperty("Users");
            if (runtimeUsersProp is not null)
            {
                return runtimeUsersProp.GetValue(userManager) as IEnumerable;
            }
        }
        catch
        {
        }

        return null;
    }
}
