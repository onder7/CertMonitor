using CertMonitor;
using Microsoft.Extensions.Options;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ScanOptions>(builder.Configuration.GetSection("Scan"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<ScanStore>();
builder.Services.AddSingleton<ScanService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScanService>());

var app = builder.Build();
app.Use(async (ctx, next) =>
{
    var publicPath = ctx.Request.Path.StartsWithSegments("/login.html") || ctx.Request.Path.StartsWithSegments("/api/auth");
    if (!publicPath && !ctx.Request.Cookies.ContainsKey("certmonitor.session"))
    {
        if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = 401; return; }
        ctx.Response.Redirect("/login.html"); return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/auth/login", (LoginRequest request, AuthService auth, IOptions<AuthOptions> options, HttpResponse response) =>
{
    if (!auth.Validate(request.UserName, request.Password)) return Results.Unauthorized();
    response.Cookies.Append("certmonitor.session", request.UserName.Trim(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = false, MaxAge = TimeSpan.FromHours(Math.Max(1, options.Value.SessionHours)) });
    return Results.Ok(new { userName = request.UserName.Trim() });
});
app.MapPost("/api/auth/logout", (HttpResponse response) => { response.Cookies.Delete("certmonitor.session"); return Results.Ok(); });
app.MapGet("/api/auth/me", (HttpRequest request) => Results.Ok(new { userName = request.Cookies["certmonitor.session"] }));

app.MapGet("/api/results", (ScanStore s, ScanService svc) => Results.Ok(new { scanning = svc.IsScanning, results = s.All() }));
app.MapPost("/api/scan", (ScanService svc) => { _ = Task.Run(() => svc.ScanAllAsync(CancellationToken.None)); return Results.Accepted(); });
app.MapGet("/api/targets", (ScanService svc) => Results.Ok(svc.LoadTargets()));
app.MapGet("/api/settings/email", (EmailService emailService) => Results.Ok(new
{
    emailService.GetConfig().Enabled,
    emailService.GetConfig().SmtpHost,
    emailService.GetConfig().SmtpPort,
    emailService.GetConfig().UseSsl,
    emailService.GetConfig().Username,
    Password = string.IsNullOrEmpty(emailService.GetConfig().Password) ? "" : "********",
    emailService.GetConfig().FromAddress,
    emailService.GetConfig().FromName,
    emailService.GetConfig().ToAddresses,
    emailService.GetConfig().NotifyOnWarning,
    emailService.GetConfig().NotifyOnCritical,
    emailService.GetConfig().NotifyOnExpired,
    emailService.GetConfig().NotifyDaysBefore
}));
app.MapPost("/api/settings/email", (EmailOptions request, EmailService emailService) =>
{
    try
    {
        emailService.SaveConfig(request);
        return Results.Ok(new { message = "Ayarlar kaydedildi." });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Problem("App_Data klasörüne yazma izni yok. IIS Application Pool hesabına App_Data için Modify izni verin.", statusCode: 500);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Ayarlar kaydedilemedi: {ex.Message}", statusCode: 500);
    }
});
app.MapPost("/api/settings/email/test", async (TestEmailRequest request, EmailService emailService) =>
{
    if (string.IsNullOrWhiteSpace(request.TestAddress))
        return Results.BadRequest(new { error = "Test e-posta adresi belirtilmelidir." });
    
    var (success, message) = await emailService.SendTestEmailAsync(request.TestAddress);
    return success ? Results.Ok(new { message }) : Results.Problem(message, statusCode: 500);
});
app.MapGet("/api/export.csv", (ScanStore s) =>
{
    var sb = new StringBuilder("Host,IP,Port,Ortam,Sahip,Durum,KalanGun,Bitis,Issuer,TLS,Uyarilar\n");
    foreach (var r in s.All()) sb.AppendLine(string.Join(',', new[] { r.Host, r.IpAddress ?? "", r.Port.ToString(), r.Environment, r.Owner, r.Status, r.DaysLeft?.ToString() ?? "", r.NotAfter?.ToString("yyyy-MM-dd") ?? "", r.Issuer ?? "", r.TlsVersion ?? "", string.Join(" | ", r.Warnings) }.Select(x => $"\"{x.Replace("\"", "\"\"")}\"")));
    return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", "ssl-durum.csv");
});

app.Run();
