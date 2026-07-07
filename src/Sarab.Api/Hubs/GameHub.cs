using Microsoft.AspNetCore.SignalR;
using Sarab.Api.Domain;
using Sarab.Api.Services;

namespace Sarab.Api.Hubs;

public sealed class GameHub(
    RoomManager rooms,
    IWebHostEnvironment environment,
    IConfiguration configuration,
    ILogger<GameHub> logger) : Hub
{
    public async Task<RoomSnapshotDto> CreateRoom(CreateRoomRequest request)
    {
        var snapshot = await rooms.CreateRoomAsync(Context.ConnectionId, request, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, snapshot.Code, Context.ConnectionAborted);
        await BroadcastRoom(snapshot.Code);
        return snapshot;
    }

    public async Task<RoomSnapshotDto> JoinRoom(JoinRoomRequest request)
    {
        var snapshot = await rooms.JoinRoomAsync(Context.ConnectionId, request, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, snapshot.Code, Context.ConnectionAborted);
        await BroadcastRoom(snapshot.Code);
        return snapshot;
    }

    public async Task<RoomSnapshotDto> ReconnectRoom(ReconnectRoomRequest request)
    {
        var snapshot = await rooms.ReconnectRoomAsync(Context.ConnectionId, request, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, snapshot.Code, Context.ConnectionAborted);
        await BroadcastRoom(snapshot.Code);
        return snapshot;
    }

    public async Task<RoomSnapshotDto> AddDevBots(int count)
    {
        if (!DevBotsAllowed())
        {
            throw new HubException("Development bots are not enabled.");
        }

        var snapshot = rooms.AddDevBots(Context.ConnectionId, count);
        await BroadcastRoom(snapshot.Code);
        return snapshot;
    }

    public async Task StartGame(StartGameRequest request)
    {
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        await rooms.StartGameAsync(Context.ConnectionId, request, Context.ConnectionAborted);
        if (code is not null)
        {
            await BroadcastRoom(code);
        }
    }

    public async Task SubmitAnswer(SubmitAnswerRequest request)
    {
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        rooms.SubmitAnswer(Context.ConnectionId, request);
        if (code is not null)
        {
            await rooms.TryAutoAdvanceRoomAsync(code, Context.ConnectionAborted);
            await BroadcastRoom(code);
        }
    }

    public async Task SelfReport()
    {
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        rooms.SelfReport(Context.ConnectionId);
        if (code is not null)
        {
            await rooms.TryAutoAdvanceRoomAsync(code, Context.ConnectionAborted);
            await BroadcastRoom(code);
        }
    }

    public async Task FinishSelfReport()
    {
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        rooms.FinishSelfReport(Context.ConnectionId);
        if (code is not null)
        {
            await rooms.TryAutoAdvanceRoomAsync(code, Context.ConnectionAborted);
            await BroadcastRoom(code);
        }
    }

    public async Task BetSafe()
    {
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        rooms.BetSafe(Context.ConnectionId);
        if (code is not null)
        {
            await rooms.TryAutoAdvanceRoomAsync(code, Context.ConnectionAborted);
            await BroadcastRoom(code);
        }
    }

    public async Task UpdateAvatar(PlayerAvatarDto avatar)
    {
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        rooms.UpdateAvatar(Context.ConnectionId, avatar);
        if (code is not null)
        {
            await BroadcastRoom(code);
        }
    }

    public async Task UpdateReady(bool ready)
    {
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        rooms.UpdateReady(Context.ConnectionId, ready);
        if (code is not null)
        {
            await BroadcastRoom(code);
        }
    }

    public async Task SubmitVote(SubmitVoteRequest request)
    {
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        rooms.SubmitVote(Context.ConnectionId, request);
        if (code is not null)
        {
            await rooms.TryAutoAdvanceRoomAsync(code, Context.ConnectionAborted);
            await BroadcastRoom(code);
        }
    }

    public async Task AdvancePhase()
    {
        string? code = null;
        try
        {
            code = rooms.GetRoomForConnection(Context.ConnectionId);
            await rooms.AdvancePhaseAsync(Context.ConnectionId, Context.ConnectionAborted);
            if (code is not null)
            {
                await BroadcastRoom(code);
            }
        }
        catch (HubException)
        {
            throw;
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            logger.LogWarning("AdvancePhase was canceled for connection {ConnectionId}.", Context.ConnectionId);
            throw new HubException("The request was interrupted. Try again.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AdvancePhase failed for connection {ConnectionId} in room {RoomCode}.", Context.ConnectionId, code ?? "unknown");
            if (code is not null)
            {
                await BroadcastRoom(code, CancellationToken.None);
            }

            throw new HubException($"Advance failed: {ex.Message}");
        }
    }

    public async Task ExpirePhase()
    {
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        if (code is not null && await rooms.AdvanceExpiredRoomForConnectionAsync(Context.ConnectionId, Context.ConnectionAborted))
        {
            await BroadcastRoom(code);
        }
    }

    public async Task SkipRound()
    {
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        rooms.SkipRound(Context.ConnectionId);
        if (code is not null)
        {
            await BroadcastRoom(code);
        }
    }

    public async Task RemovePlayer(Guid playerId)
    {
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        rooms.RemovePlayer(Context.ConnectionId, playerId);
        if (code is not null)
        {
            await BroadcastRoom(code);
        }
    }

    public async Task EndGame()
    {
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        await rooms.EndGameAsync(Context.ConnectionId, Context.ConnectionAborted);
        if (code is not null)
        {
            await BroadcastRoom(code);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        await rooms.DisconnectAsync(Context.ConnectionId);
        if (code is not null)
        {
            await BroadcastRoom(code);
        }

        if (exception is not null)
        {
            logger.LogDebug(exception, "Client disconnected from Sarab room.");
        }

        await base.OnDisconnectedAsync(exception);
    }

    private Task BroadcastRoom(string roomCode) => BroadcastRoom(roomCode, CancellationToken.None);

    private async Task BroadcastRoom(string roomCode, CancellationToken cancellationToken)
    {
        foreach (var (connectionId, snapshot) in rooms.GetSnapshotsForRoom(roomCode))
        {
            try
            {
                await Clients.Client(connectionId).SendAsync("roomUpdated", snapshot, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not broadcast Sarab room {RoomCode} to connection {ConnectionId}.", roomCode, connectionId);
            }
        }
    }

    private bool DevBotsAllowed()
    {
        var configured = configuration.GetValue<bool?>("Sarab:DevBots:Enabled");
        return configured ?? environment.IsDevelopment();
    }
}
