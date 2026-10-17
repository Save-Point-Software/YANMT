using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Yanmt.Infrastructure;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Security;
using Yanmt.Web.Components;
using Yanmt.Web.Endpoints;
using Yanmt.Web.Hosting;

ILogger? startupLogger = null;
string? logsDirectory = null;
try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,

        // As a service the working directory is System32; config and wwwroot live next to the exe.
        ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
    });
    builder.Services.AddWindowsService(o => o.ServiceName = "YANMT");

    var yanmtOptions = builder.Configuration.GetSection(YanmtOptions.SectionName).Get<YanmtOptions>() ?? new();
    var paths = YanmtPaths.Resolve(yanmtOptions.DataPath, builder.Environment.ContentRootPath);
    paths.EnsureCreated();
    logsDirectory = paths.Logs;
    LoggingSetup.Configure(builder, paths);

    // A failing background service is logged and must never stop the web app.
    builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

    // HTTPS only, certificate from the Windows store. Loaded now so a bad thumbprint or missing
    // key ACL fails startup with a clear message instead of on the first connection.
    var https = builder.Configuration.GetSection(HttpsOptions.SectionName).Get<HttpsOptions>() ?? new();
    var certificate = CertificateLoader.Load(https, builder.Environment);
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.AddServerHeader = false;
        void Configure(ListenOptions listen)
        {
            // Windows auth (Negotiate) on Kestrel requires HTTP/1.1. Blazor's WebSocket works over it.
            listen.Protocols = HttpProtocols.Http1;
            if (certificate is null)
            {
                listen.UseHttps(); // Development only: the ASP.NET Core dev certificate
            }
            else
            {
                listen.UseHttps(certificate);
            }
        }

        if (string.IsNullOrWhiteSpace(https.ListenAddress))
        {
            kestrel.ListenAnyIP(https.Port, Configure); // dual-stack, IPv4 fallback
        }
        else
        {
            kestrel.Listen(IPAddress.Parse(https.ListenAddress), https.Port, Configure);
        }
    });

    // Windows Integrated Authentication; roles come from AD or local group membership.
    var roleOptions = builder.Configuration.GetSection(RoleGroupOptions.SectionName).Get<RoleGroupOptions>() ?? new();
    builder.Services.AddSingleton(sp => RoleGroupMap.Resolve(roleOptions, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Yanmt.Roles")));
    builder.Services.AddSingleton<IClaimsTransformation, GroupRoleClaimsTransformation>();
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
    var viewerPolicy = new AuthorizationPolicyBuilder().RequireRole(YanmtRoles.Admin, YanmtRoles.Viewer).Build();
    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(YanmtPolicies.Admin, p => p.RequireRole(YanmtRoles.Admin))
        .AddPolicy(YanmtPolicies.Viewer, viewerPolicy)
        .SetFallbackPolicy(viewerPolicy);
    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddHttpContextAccessor();

    builder.Services.AddScoped<ICurrentUser, WebCurrentUser>();
    builder.Services.AddYanmtInfrastructure(paths);

    builder.Services.AddRazorComponents().AddInteractiveServerComponents();

    var app = builder.Build();
    startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Yanmt.Startup");
    startupLogger.LogInformation("YANMT starting. Data folder: {DataPath}. Listening on https port {Port}.", paths.Root, https.Port);

    // Resolve role groups now so misconfiguration is logged at startup, not on the first request.
    _ = app.Services.GetRequiredService<RoleGroupMap>();

    using (var scope = app.Services.CreateScope())
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<YanmtDbContext>>();
        await DatabaseInitializer.InitializeAsync(factory, startupLogger);
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseStatusCodePages(WriteStatusPageAsync);
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapNodeExport();
    app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    LoggingSetup.ReportStartupFailure(ex, startupLogger, logsDirectory);
    return 1;
}

// Plain pages for errors raised outside Blazor (mainly 403: signed in but not in a YANMT group).
static async Task WriteStatusPageAsync(StatusCodeContext context)
{
    var http = context.HttpContext;
    var user = HtmlEncoder.Default.Encode(http.User.Identity?.Name ?? "unknown");
    var (title, body) = http.Response.StatusCode switch
    {
        StatusCodes.Status403Forbidden when http.User.IsInRole(YanmtRoles.Viewer) => ("Admin access required",
            $"Your account (<b>{user}</b>) has viewer access. This page changes configuration, so it is limited to members of a YANMT admin group. <a href=\"/nodes\">Back to nodes</a>"),
        StatusCodes.Status403Forbidden => ("Access denied",
            $"Your account (<b>{user}</b>) is not in a YANMT admin or viewer group. Ask an administrator to add you, then sign out of Windows and back in so your group membership refreshes."),
        StatusCodes.Status404NotFound => ("Not found", "There is nothing at this address."),
        _ => ($"Error {http.Response.StatusCode}", "The request could not be completed."),
    };

    http.Response.ContentType = "text/html; charset=utf-8";
    await http.Response.WriteAsync(
        $"<!doctype html><title>YANMT - {title}</title><body style=\"font:15px system-ui;margin:3rem;max-width:40rem\"><h1>{title}</h1><p>{body}</p></body>");
}
