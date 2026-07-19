using System.Text;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MPR.Application.Interfaces;
using MPR.Application.Services;
using MPR.Infrastructure.Persistence;
using MPR.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// Render provides a DATABASE_URL env var in postgres:// URI form; local dev can still
// use a normal keyword connection string via ConnectionStrings:Default.
var connStr = Environment.GetEnvironmentVariable("DATABASE_URL") is { } databaseUrl
    ? ConvertPostgresUrlToConnectionString(databaseUrl)
    : builder.Configuration.GetConnectionString("Default");

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(connStr));

builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<IMprCalculationService, MprCalculationService>();
builder.Services.AddScoped<IExcelExportService, ExcelExportService>();
builder.Services.AddSingleton<MPR.Infrastructure.Services.Ocr.ITemplateLibrary>(sp =>
    new MPR.Infrastructure.Services.Ocr.FileSystemTemplateLibrary(
        Path.Combine(builder.Environment.ContentRootPath, "App_Data", "MarkTemplates")));
builder.Services.AddScoped<IPdfExtractionService>(sp =>
    new PdfExtractionService(
        builder.Configuration["Ocr:TessDataPath"]!,
        sp.GetRequiredService<MPR.Infrastructure.Services.Ocr.ITemplateLibrary>()));
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddHttpClient();
builder.Services.AddScoped(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
    var chatConfig = builder.Configuration.GetSection("ChatProvider");
    return new MPR.Web.Services.Chat.OllamaChatClient(
        http,
        chatConfig["BaseUrl"] ?? "http://localhost:11434",
        chatConfig["Model"] ?? "llama3.1",
        chatConfig["ApiKey"]); // null for local Ollama; set for Groq/any OpenAI-compatible hosted API
});

builder.Services.AddHangfire(cfg => cfg.UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connStr)));
builder.Services.AddHangfireServer();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        var jwt = builder.Configuration.GetSection("Jwt");
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!))
        };
    });

builder.Services.AddAuthorization(opt =>
{
    opt.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
    opt.AddPolicy("CanExportOrEmail", p => p.RequireClaim("CanExportOrEmail", "true"));
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Render injects a PORT env var and expects the app to bind to 0.0.0.0:$PORT.
var renderPort = Environment.GetEnvironmentVariable("PORT");
if (renderPort is not null)
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseHangfireDashboard("/jobs"); // restrict this path to Admin in production via middleware/policy

app.MapControllers();

// Runs on every startup; DbSeeder itself checks for existing rows before inserting,
// so this is safe to leave on for a Render free-tier deploy where you don't have
// shell access to run migrations/seeding manually ahead of time.
await MPR.Infrastructure.Persistence.DbSeeder.SeedAsync(app.Services);

app.Run();

static string ConvertPostgresUrlToConnectionString(string url)
{
    // postgres://user:pass@host:port/dbname -> Npgsql keyword connection string
    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':');
    return $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.TrimStart('/')};" +
           $"Username={userInfo[0]};Password={userInfo[1]};SSL Mode=Require;Trust Server Certificate=true";
}
