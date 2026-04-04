using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.InternalRating
{
    /// <summary>
    /// Inserts <see cref="ScriptInjectionMiddleware"/> at the very front of
    /// Jellyfin's ASP.NET Core pipeline so it can intercept index.html responses.
    /// Registered as IStartupFilter so it runs before any Jellyfin middleware.
    /// </summary>
    public class ScriptInjectionStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.UseMiddleware<ScriptInjectionMiddleware>();
                next(app);
            };
        }
    }
}
