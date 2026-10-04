using Microsoft.EntityFrameworkCore;
using MusicRoyaltyApi.Data;
using MusicRoyaltyApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure PostgreSQL with Entity Framework Core
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")
    ));
builder.Services.AddHostedService<PlaySimulator>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(
                "https://royalty-web.onrender.com",
                "http://localhost:5173",
                "http://localhost:3000"
              )
              .SetIsOriginAllowed(origin =>
                  origin.EndsWith(".onrender.com") ||
                  origin.EndsWith(".vercel.app"))
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});
var app = builder.Build();

// 1. Routing must come first
app.UseRouting();

// 2. CORS must come right after Routing, but BEFORE Auth and Endpoints
app.UseCors("AllowFrontend");

// 3. Authentication & Authorization
app.UseAuthentication();
app.UseAuthorization();

// 4. Controller endpoints
app.MapControllers();

// If you have static fallback files:
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();