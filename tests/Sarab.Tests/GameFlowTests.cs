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

    [Fact]
    public async Task Host_can_advance_from_results_to_the_next_round()
    {
        using var provider = BuildProvider();
        var rooms = provider.GetRequiredService<RoomManager>();
        var lobby = await rooms.CreateRoomAsync("host", new CreateRoomRequest("Host"));
        await rooms.JoinRoomAsync("second", new JoinRoomRequest(lobby.Code, "P2"));
        await rooms.JoinRoomAsync("third", new JoinRoomRequest(lobby.Code, "P3"));
        await rooms.JoinRoomAsync("fourth", new JoinRoomRequest(lobby.Code, "P4"));

        await rooms.StartGameAsync("host", new StartGameRequest(lobby.Categories[0].CategoryId, 2));
        rooms.SubmitAnswer("host", new SubmitAnswerRequest("palm"));
        rooms.SubmitAnswer("second", new SubmitAnswerRequest("shade"));
        rooms.SubmitAnswer("third", new SubmitAnswerRequest("water"));
        rooms.SubmitAnswer("fourth", new SubmitAnswerRequest("dune"));

        Assert.True(await rooms.TryAutoAdvanceRoomAsync(lobby.Code));
        rooms.FinishSelfReport("host");
        rooms.FinishSelfReport("second");
        rooms.FinishSelfReport("third");
        rooms.FinishSelfReport("fourth");

        Assert.True(await rooms.TryAutoAdvanceRoomAsync(lobby.Code));
        foreach (var (connectionId, snapshot) in rooms.GetSnapshotsForRoom(lobby.Code))
        {
            var voteTarget = snapshot.Answers.First(answer => !answer.IsMine);
            rooms.SubmitVote(connectionId, new SubmitVoteRequest(voteTarget.Id, ConfidenceLevel.Medium));
        }

        Assert.True(await rooms.TryAutoAdvanceRoomAsync(lobby.Code));
        Assert.Equal(RoomPhase.Results, HostSnapshot(rooms, lobby.Code).Phase);

        await rooms.AdvancePhaseAsync("host");

        var nextRound = HostSnapshot(rooms, lobby.Code);
        Assert.Equal(RoomPhase.Answer, nextRound.Phase);
        Assert.Equal(2, nextRound.CurrentRound);
    }

    [Fact]
    public async Task Host_can_advance_bot_room_from_results_to_the_next_round()
    {
        using var provider = BuildProvider();
        var rooms = provider.GetRequiredService<RoomManager>();
        var lobby = await rooms.CreateRoomAsync("host", new CreateRoomRequest("Host"));
        rooms.AddDevBots("host", 3);

        await rooms.StartGameAsync("host", new StartGameRequest(lobby.Categories[0].CategoryId, 2));
        rooms.SubmitAnswer("host", new SubmitAnswerRequest("palm"));
        foreach (var turn in rooms.GetPendingBotTurns())
        {
            await rooms.ApplyBotDecisionAsync(turn, new DevBotDecision(["shade", "water", "dune", "sun", "oasis"], null, null, ConfidenceLevel.Medium));
        }

        var snapshot = HostSnapshot(rooms, lobby.Code);
        Assert.Equal(RoomPhase.SelfReport, snapshot.Phase);
        var allowedBotAnswers = new[] { "shade", "water", "dune", "sun", "oasis" };
        Assert.All(
            snapshot.Answers.Where(answer => answer.AuthorName?.StartsWith("Sarab Bot", StringComparison.Ordinal) == true),
            answer => Assert.Contains(answer.Text, allowedBotAnswers));

        rooms.FinishSelfReport("host");
        foreach (var turn in rooms.GetPendingBotTurns())
        {
            await rooms.ApplyBotDecisionAsync(turn, new DevBotDecision([], "safe", null, ConfidenceLevel.Medium));
        }

        snapshot = HostSnapshot(rooms, lobby.Code);
        Assert.Equal(RoomPhase.Vote, snapshot.Phase);

        var hostVoteTarget = snapshot.Answers.First(answer => !answer.IsMine);
        rooms.SubmitVote("host", new SubmitVoteRequest(hostVoteTarget.Id, ConfidenceLevel.Medium));
        foreach (var turn in rooms.GetPendingBotTurns())
        {
            var voteTarget = turn.Answers.First(answer => !answer.IsMine).Id;
            await rooms.ApplyBotDecisionAsync(turn, new DevBotDecision([], null, voteTarget, ConfidenceLevel.Medium));
        }

        snapshot = HostSnapshot(rooms, lobby.Code);
        Assert.Equal(RoomPhase.Results, snapshot.Phase);

        await rooms.AdvancePhaseAsync("host");

        var nextRound = HostSnapshot(rooms, lobby.Code);
        Assert.Equal(RoomPhase.Answer, nextRound.Phase);
        Assert.Equal(2, nextRound.CurrentRound);
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
