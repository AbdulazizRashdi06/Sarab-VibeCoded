using Microsoft.AspNetCore.SignalR;
using Sarab.Api.Hubs;

namespace Sarab.Api.Services;

public sealed class RoomClockService(
    RoomManager rooms,
    IHubContext<GameHub> hub,
    ILogger<RoomClockService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var changedRooms = await rooms.AdvanceExpiredRoomsAsync(stoppingToken);
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
                logger.LogWarning(ex, "Sarab room clock failed to advance a room.");
            }
        }
    }

    private async Task BroadcastRoom(string roomCode, CancellationToken cancellationToken)
    {
        foreach (var (connectionId, snapshot) in rooms.GetSnapshotsForRoom(roomCode))
        {
            await hub.Clients.Client(connectionId).SendAsync("roomUpdated", snapshot, cancellationToken);
        }
    }
}
