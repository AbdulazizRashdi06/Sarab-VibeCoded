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
        var code = rooms.GetRoomForConnection(Context.ConnectionId);
        await rooms.AdvancePhaseAsync(Context.ConnectionId, Context.ConnectionAborted);
        if (code is not null)
        {
            await BroadcastRoom(code);
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

    private async Task BroadcastRoom(string roomCode)
    {
        foreach (var (connectionId, snapshot) in rooms.GetSnapshotsForRoom(roomCode))
        {
            await Clients.Client(connectionId).SendAsync("roomUpdated", snapshot, Context.ConnectionAborted);
        }
    }

    private bool DevBotsAllowed()
    {
        var configured = configuration.GetValue<bool?>("Sarab:DevBots:Enabled");
        return configured ?? environment.IsDevelopment();
    }
}
