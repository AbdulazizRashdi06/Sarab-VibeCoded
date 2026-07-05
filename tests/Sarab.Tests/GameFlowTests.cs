using Microsoft.Extensions.DependencyInjection;
using Sarab.Api.Domain;
using Sarab.Api.Services;

namespace Sarab.Tests;

public sealed class GameFlowTests
{
    [Fact]
    public async Task Self_report_phase_auto_advances_when_every_active_player_is_done()
    {
        using var provider = BuildProvider();
        var rooms = provider.GetRequiredService<RoomManager>();
        var host = "host";
        var second = "second";
        var third = "third";
        var fourth = "fourth";

        var lobby = await rooms.CreateRoomAsync(host, new CreateRoomRequest("Host"));
        await rooms.JoinRoomAsync(second, new JoinRoomRequest(lobby.Code, "P2"));
        await rooms.JoinRoomAsync(third, new JoinRoomRequest(lobby.Code, "P3"));
        await rooms.JoinRoomAsync(fourth, new JoinRoomRequest(lobby.Code, "P4"));

        await rooms.StartGameAsync(host, new StartGameRequest(lobby.Categories[0].CategoryId, 1));
        rooms.SubmitAnswer(host, new SubmitAnswerRequest("palm"));
        rooms.SubmitAnswer(second, new SubmitAnswerRequest("shade"));
        rooms.SubmitAnswer(third, new SubmitAnswerRequest("water"));
        rooms.SubmitAnswer(fourth, new SubmitAnswerRequest("dune"));

        Assert.True(await rooms.TryAutoAdvanceRoomAsync(lobby.Code));
        Assert.Equal(RoomPhase.SelfReport, HostSnapshot(rooms, lobby.Code).Phase);

        rooms.FinishSelfReport(host);
        rooms.BetSafe(second);
        rooms.SelfReport(third);
        rooms.FinishSelfReport(fourth);

        Assert.True(await rooms.TryAutoAdvanceRoomAsync(lobby.Code));
        var snapshot = HostSnapshot(rooms, lobby.Code);
        Assert.Equal(RoomPhase.Vote, snapshot.Phase);
        Assert.All(snapshot.Players, player => Assert.True(player.SelfReportDone));
        Assert.Contains(snapshot.Players, player => player.Name == "P2" && player.SafeBet);
    }

    [Fact]
    public async Task Expired_phase_nudge_only_moves_when_the_timer_is_due()
    {
        using var provider = BuildProvider();
        var rooms = provider.GetRequiredService<RoomManager>();
        var lobby = await rooms.CreateRoomAsync("host", new CreateRoomRequest("Host"));
        await rooms.JoinRoomAsync("second", new JoinRoomRequest(lobby.Code, "P2"));
        await rooms.JoinRoomAsync("third", new JoinRoomRequest(lobby.Code, "P3"));
        await rooms.JoinRoomAsync("fourth", new JoinRoomRequest(lobby.Code, "P4"));

        await rooms.StartGameAsync("host", new StartGameRequest(lobby.Categories[0].CategoryId, 1));

        Assert.False(await rooms.AdvanceExpiredRoomForConnectionAsync("host"));
        Assert.Equal(RoomPhase.Answer, HostSnapshot(rooms, lobby.Code).Phase);
    }

    [Fact]
    public async Task Host_can_add_ready_dev_bots_in_lobby()
    {
        using var provider = BuildProvider();
        var rooms = provider.GetRequiredService<RoomManager>();
        var lobby = await rooms.CreateRoomAsync("host", new CreateRoomRequest("Host"));

        var snapshot = rooms.AddDevBots("host", 3);

        Assert.Equal(lobby.Code, snapshot.Code);
        Assert.Equal(4, snapshot.Players.Count);
        Assert.Equal(3, snapshot.Players.Count(player => player.IsBot));
        Assert.All(snapshot.Players.Where(player => player.IsBot), bot => Assert.True(bot.Ready));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IContentStore, InMemoryContentStore>();
        services.AddScoped<ScoringEngine>();
        services.AddSingleton<RoomManager>();
        return services.BuildServiceProvider();
    }

    private static RoomSnapshotDto HostSnapshot(RoomManager rooms, string code)
    {
        return rooms.GetSnapshotsForRoom(code).First().Snapshot;
    }
}
