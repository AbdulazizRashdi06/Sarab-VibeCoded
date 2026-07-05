using Microsoft.AspNetCore.SignalR;
using Sarab.Api.Hubs;

namespace Sarab.Api.Services;

public sealed class DevBotRunnerService(
    RoomManager rooms,
    IDevBotBrain brain,
    IHubContext<GameHub> hub,
    IWebHostEnvironment environment,
    IConfiguration configuration,
    ILogger<DevBotRunnerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(900));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!DevBotsAllowed())
            {
                continue;
            }

            try
            {
                var changedRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var turn in rooms.GetPendingBotTurns())
                {
                    try
                    {
                        var decision = await brain.DecideAsync(turn, stoppingToken);
                        foreach (var roomCode in await rooms.ApplyBotDecisionAsync(turn, decision, stoppingToken))
                        {
                            changedRooms.Add(roomCode);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        rooms.ReleaseBotTurn(turn);
                        logger.LogWarning(ex, "Sarab dev bot could not get an LLM decision for room {RoomCode}.", turn.RoomCode);
                    }
                }

                foreach (var roomCode in changedRooms)
                {
                    await BroadcastRoom(roomCode, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sarab dev bot runner failed.");
            }
        }
    }

    private bool DevBotsAllowed()
    {
        var configured = configuration.GetValue<bool?>("Sarab:DevBots:Enabled");
        return configured ?? environment.IsDevelopment();
    }

    private async Task BroadcastRoom(string roomCode, CancellationToken cancellationToken)
    {
        foreach (var (connectionId, snapshot) in rooms.GetSnapshotsForRoom(roomCode))
        {
            await hub.Clients.Client(connectionId).SendAsync("roomUpdated", snapshot, cancellationToken);
        }
    }
}
