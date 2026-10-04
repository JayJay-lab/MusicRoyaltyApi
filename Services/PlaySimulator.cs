using Microsoft.EntityFrameworkCore;
using MusicRoyaltyApi.Data;

namespace MusicRoyaltyApi.Services;

public class PlaySimulator : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Random _random = new();

    public PlaySimulator(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait 10 seconds after the API starts
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var tracks = await db.Tracks.ToListAsync(stoppingToken);

                foreach (var track in tracks)
                {
                    // Simulate new plays
                    int newPlays = _random.Next(1, 11);

                    track.Plays += newPlays;
                }

                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Play simulator error: {ex.Message}");
            }

            // Update every 30 seconds
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}