using ChristmasListTracker.Client.Pages;
using ChristmasListTracker.Components;
using ChristmasListTracker.Services;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

if (args.Contains("--migrate", StringComparer.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("Set ConnectionStrings:DefaultConnection before running migrations.");
    }

    await using var dataSource = NpgsqlDataSource.Create(connectionString);
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
    var migrationLogger = loggerFactory.CreateLogger<MigrationRunner>();
    var migrationRunner = new MigrationRunner(
        dataSource,
        Path.Combine(AppContext.BaseDirectory, "database", "migrations"),
        migrationLogger);
    await migrationRunner.ApplyMigrationsAsync();
    return;
}

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Set ConnectionStrings:DefaultConnection to connect to the Supabase PostgreSQL database.");
}

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<ListRepository>();
builder.Services.AddSingleton<ImageCompressionService>();
builder.Services.AddSingleton<R2StorageService>();
builder.Services.AddScoped<ChristmasListService>();
builder.Services.AddScoped<CurrentUserState>();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 32 * 1024);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.MapStaticAssets();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(ChristmasListTracker.Client._Imports).Assembly);

app.Run();
