using AmaanParkingSystem.Controllers;
using AmaanParkingSystem.Services;
using DocumentFormat.OpenXml.Drawing.Charts;
using DocumentFormat.OpenXml.Office2016.Drawing.ChartDrawing;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(typeof(UtilitySessionAuthorizeAttribute));
})
.AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    options.JsonSerializerOptions.WriteIndented = true;
});

builder.Services.AddSingleton<IConnectionStringService, ConnectionStringService>();
builder.Services.AddSingleton<IDatabaseSettingsService, DatabaseSettingsService>();

builder.Services.AddScoped<ANPRService>();
builder.Services.AddScoped<CollectionService>();
builder.Services.AddScoped<CollectionController>();
builder.Services.AddScoped<EmailReportService>();
builder.Services.AddScoped<DatabaseBackupService>();
builder.Services.AddHostedService<ScheduledBackupService>();
builder.Services.AddHostedService<ScheduledReportService>();
builder.Services.AddSingleton<RealTimeReportScheduledService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RealTimeReportScheduledService>());
builder.Services.AddScoped<SiteService>();
builder.Services.AddHostedService<BalanceMonitorBackgroundService>();
builder.Services.AddHostedService<MonthlyCompanyReportBackgroundService>();
builder.Services.AddScoped<FocReportService>();
builder.Services.AddSingleton<B2EntryNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<B2EntryNotificationService>());

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthorization();

app.Use(async (context, next) =>
{
    var connStrService = context.RequestServices.GetRequiredService<IConnectionStringService>();
    var dbSettingsService = context.RequestServices.GetRequiredService<IDatabaseSettingsService>();
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

    bool isStaticFile = context.Request.Path.StartsWithSegments("/css")
                     || context.Request.Path.StartsWithSegments("/js")
                     || context.Request.Path.StartsWithSegments("/images")
                     || context.Request.Path.StartsWithSegments("/lib")
                     || context.Request.Path.StartsWithSegments("/favicon.ico");

    if (!isStaticFile && !connStrService.IsConfigured)
    {
        var saved = dbSettingsService.Load();
        if (saved != null)
        {
            try
            {
                string decryptedPassword =
                    DatabaseSettingsService.DecryptPassword(saved.EncryptedPassword);

                var builder2 = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
                {
                    DataSource = saved.ServerName,
                    InitialCatalog = saved.DatabaseName,
                    UserID = saved.Username,
                    Password = decryptedPassword,
                    TrustServerCertificate = true,
                    MultipleActiveResultSets = true,
                    ConnectTimeout = 15
                };

                using var conn = new Microsoft.Data.SqlClient.SqlConnection(builder2.ConnectionString);
                conn.Open();
                conn.Close();

                connStrService.SetConnectionString(builder2.ConnectionString);

                context.Session.SetString(DatabaseLoginController.DbSessionKey, "true");

                logger.LogInformation(
                    "[Startup] Auto-connect succeeded. Server={Server}, Database={Database}",
                    saved.ServerName, saved.DatabaseName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Startup] Auto-connect failed — DB login page will be shown.");
            }
        }
    }

    await next();
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=DatabaseLogin}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "parkercompany",
    pattern: "{controller=ParkerCompany}/{action=ParkerCompanyRegistration}/{id?}");

app.Run();