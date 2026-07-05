using Sarab.Api.Domain;

namespace Sarab.Api.Services;

public sealed class RoomManager(IServiceScopeFactory scopeFactory)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RoomState> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string RoomCode, Guid PlayerId)> _connections = [];
    private readonly Random _random = new();

    public async Task<RoomSnapshotDto> CreateRoomAsync(string connectionId, CreateRoomRequest request, CancellationToken cancellationToken = default)
    {
        var categories = await GetCatalogAsync(cancellationToken);
        lock (_gate)
        {
            var code = GenerateCode();
            var player = new PlayerState(Guid.NewGuid(), CleanName(request.PlayerName), true)
            {
                Avatar = NormalizeAvatar(request.Avatar),
                Connections = { connectionId }
            };

            var room = new RoomState(code, player.Id)
            {
                Players = { player },
                Categories = categories
            };

            _rooms[code] = room;
            _connections[connectionId] = (code, player.Id);
            return Snapshot(room, player.Id);
        }
    }

    public async Task<RoomSnapshotDto> JoinRoomAsync(string connectionId, JoinRoomRequest request, CancellationToken cancellationToken = default)
    {
        var categories = await GetCatalogAsync(cancellationToken);
        lock (_gate)
        {
            if (!_rooms.TryGetValue(request.RoomCode.Trim().ToUpperInvariant(), out var room))
            {
                throw new InvalidOperationException("Room not found.");
            }

            room.Categories = categories;
            var waiting = room.Phase != RoomPhase.Lobby;
            var player = new PlayerState(Guid.NewGuid(), CleanName(request.PlayerName), false)
            {
                Avatar = NormalizeAvatar(request.Avatar),
                WaitingForNextRound = waiting,
                Connections = { connectionId }
            };

            room.Players.Add(player);
            _connections[connectionId] = (room.Code, player.Id);
            return Snapshot(room, player.Id);
        }
    }

    public void UpdateAvatar(string connectionId, PlayerAvatarDto avatar)
    {
        lock (_gate)
        {
            var room = GetRoomByConnection(connectionId);
            var player = GetPlayer(room, connectionId);
            player.Avatar = NormalizeAvatar(avatar);
        }
    }

    public void UpdateReady(string connectionId, bool ready)
    {
        lock (_gate)
        {
            var room = GetRoomByConnection(connectionId);
            var player = GetPlayer(room, connectionId);
            if (room.Phase != RoomPhase.Lobby)
            {
                throw new InvalidOperationException("Ready is only available in the lobby.");
            }

            player.Ready = ready;
        }
    }

    public RoomSnapshotDto AddDevBots(string connectionId, int count)
    {
        lock (_gate)
        {
            var room = GetRoomByConnection(connectionId);
            EnsureHost(room, connectionId);
            EnsurePhase(room, RoomPhase.Lobby);

            var slots = Math.Max(0, 20 - room.Players.Count);
            var viewer = GetPlayer(room, connectionId);
            if (slots == 0)
            {
                return Snapshot(room, viewer.Id);
            }

            var toAdd = Math.Clamp(count, 1, Math.Min(16, slots));
            var existingBots = room.Players.Count(player => player.IsBot);
            for (var index = 0; index < toAdd; index++)
            {
                var botNumber = existingBots + index + 1;
                room.Players.Add(new PlayerState(Guid.NewGuid(), $"Sarab Bot {botNumber}", false)
                {
                    IsBot = true,
                    Connected = true,
                    Ready = true
                });
            }

            return Snapshot(room, viewer.Id);
        }
    }

    public Task<IReadOnlyList<string>> DisconnectAsync(string connectionId)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(connectionId, out var mapped))
            {
                return Task.FromResult<IReadOnlyList<string>>([]);
            }

            _connections.Remove(connectionId);
            if (!_rooms.TryGetValue(mapped.RoomCode, out var room))
            {
                return Task.FromResult<IReadOnlyList<string>>([]);
            }

            var player = room.Players.FirstOrDefault(x => x.Id == mapped.PlayerId);
            if (player is not null)
            {
                player.Connections.Remove(connectionId);
                player.Connected = player.IsBot || player.Connections.Count > 0;
                player.DisconnectedAt = DateTimeOffset.UtcNow;
            }

            return Task.FromResult<IReadOnlyList<string>>(room.ConnectionIds());
        }
    }

    public IReadOnlyList<string> GetRoomConnectionIds(string roomCode)
    {
        lock (_gate)
        {
            return _rooms.TryGetValue(roomCode, out var room) ? room.ConnectionIds() : [];
        }
    }

    public Guid? GetPlayerForConnection(string connectionId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(connectionId, out var mapped) ? mapped.PlayerId : null;
        }
    }

    public string? GetRoomForConnection(string connectionId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(connectionId, out var mapped) ? mapped.RoomCode : null;
        }
    }

    public IReadOnlyList<(string ConnectionId, RoomSnapshotDto Snapshot)> GetSnapshotsForRoom(string roomCode)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomCode, out var room))
            {
                return [];
            }

            return room.Players
                .SelectMany(player => player.Connections.Select(connectionId => (connectionId, Snapshot(room, player.Id))))
                .ToList();
        }
    }

    public IReadOnlyList<DevBotTurn> GetPendingBotTurns()
    {
        lock (_gate)
        {
            var turns = new List<DevBotTurn>();
            foreach (var room in _rooms.Values)
            {
                var activePlayers = room.ActivePlayers().ToList();
                foreach (var bot in activePlayers.Where(player => player.IsBot && !player.BotTurnInProgress))
                {
                    DevBotTurn? turn = room.Phase switch
                    {
                        RoomPhase.Answer when !room.Answers.ContainsKey(bot.Id) => BuildBotTurn(room, bot, activePlayers),
                        RoomPhase.SelfReport when !room.SelfReportDone.Contains(bot.Id) => BuildBotTurn(room, bot, activePlayers),
                        RoomPhase.Vote when room.Answers.ContainsKey(bot.Id) && !room.Votes.ContainsKey(bot.Id) => BuildBotTurn(room, bot, activePlayers),
                        _ => null
                    };

                    if (turn is not null)
                    {
                        bot.BotTurnInProgress = true;
                        turns.Add(turn);
                    }
                }
            }

            return turns;
        }
    }

    public async Task<IReadOnlyList<string>> ApplyBotDecisionAsync(DevBotTurn turn, DevBotDecision decision, CancellationToken cancellationToken = default)
    {
        var changed = false;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(turn.RoomCode, out var room))
            {
                return [];
            }

            var bot = room.Players.FirstOrDefault(player => player.Id == turn.BotPlayerId && player.IsBot);
            if (bot is null)
            {
                return [];
            }

            try
            {
                if (room.Phase != turn.Phase || room.CurrentRound != turn.RoundNumber)
                {
                    return [];
                }

                switch (room.Phase)
                {
                    case RoomPhase.Answer when !room.Answers.ContainsKey(bot.Id):
                        var answer = CleanBotAnswer(decision.Answer);
                        room.Answers[bot.Id] = new AnswerState(Guid.NewGuid(), bot.Id, answer, bot.PromptIndex);
                        changed = true;
                        break;
                    case RoomPhase.SelfReport when !room.SelfReportDone.Contains(bot.Id):
                        ApplyBotTellChoice(room, bot, decision.TellChoice);
                        changed = true;
                        break;
                    case RoomPhase.Vote when room.Answers.ContainsKey(bot.Id) && !room.Votes.ContainsKey(bot.Id):
                        var ownAnswer = room.Answers[bot.Id].AnswerId;
                        var answerId = decision.VoteAnswerId is { } candidate
                            && candidate != ownAnswer
                            && room.Answers.Values.Any(answer => answer.AnswerId == candidate)
                                ? candidate
                                : room.Answers.Values.First(answer => answer.AnswerId != ownAnswer).AnswerId;
                        room.Votes[bot.Id] = new PlayerVote(bot.Id, answerId, decision.Confidence, DateTimeOffset.UtcNow);
                        changed = true;
                        break;
                }
            }
            finally
            {
                bot.BotTurnInProgress = false;
            }
        }

        if (!changed)
        {
            return [];
        }

        var advanced = await TryAutoAdvanceRoomAsync(turn.RoomCode, cancellationToken);
        return advanced ? [turn.RoomCode] : [turn.RoomCode];
    }

    public void ReleaseBotTurn(DevBotTurn turn)
    {
        lock (_gate)
        {
            if (_rooms.TryGetValue(turn.RoomCode, out var room))
            {
                var bot = room.Players.FirstOrDefault(player => player.Id == turn.BotPlayerId && player.IsBot);
                if (bot is not null)
                {
                    bot.BotTurnInProgress = false;
                }
            }
        }
    }

    public async Task StartGameAsync(string connectionId, StartGameRequest request, CancellationToken cancellationToken = default)
    {
        PromptCategory category;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var contentStore = scope.ServiceProvider.GetRequiredService<IContentStore>();
            category = await contentStore.GetCategoryAsync(request.CategoryId, cancellationToken)
                ?? throw new InvalidOperationException("Category not found.");
        }

        lock (_gate)
        {
            var room = GetRoomByConnection(connectionId);
            EnsureHost(room, connectionId);
            var activePlayers = room.Players.Where(x => !x.WaitingForNextRound).ToList();
            if (activePlayers.Count < 4)
            {
                throw new InvalidOperationException("Sarab needs at least 4 players to start.");
            }

            room.Category = category;
            room.TotalRounds = Math.Clamp(request.TotalRounds, 1, 50);
            room.AnswerSeconds = Math.Clamp(request.AnswerSeconds, 10, 180);
            room.SelfReportSeconds = Math.Clamp(request.SelfReportSeconds, 5, 90);
            room.VoteSeconds = Math.Clamp(request.VoteSeconds, 10, 180);
            room.Rollover = 0;
            room.CurrentRound = 0;
            room.RoundResults.Clear();
            foreach (var player in activePlayers)
            {
                player.Score = ScoringEngine.StartingScore;
                player.Ready = false;
            }

            StartNextRound(room);
        }
    }

    public void SubmitAnswer(string connectionId, SubmitAnswerRequest request)
    {
        lock (_gate)
        {
            var room = GetRoomByConnection(connectionId);
            var player = GetPlayer(room, connectionId);
            EnsurePhase(room, RoomPhase.Answer);
            if (player.WaitingForNextRound)
            {
                throw new InvalidOperationException("You will join next round.");
            }

            var answer = request.Answer.Trim();
            if (answer.Length == 0 || answer.Any(char.IsWhiteSpace))
            {
                throw new InvalidOperationException("Answer must be exactly one word.");
            }

            if (answer.Length > 32)
            {
                throw new InvalidOperationException("Answer is too long.");
            }

            room.Answers[player.Id] = new AnswerState(Guid.NewGuid(), player.Id, answer, player.PromptIndex);
        }
    }

    public async Task<bool> TryAutoAdvanceRoomAsync(string roomCode, CancellationToken cancellationToken = default)
    {
        RoomPhase phase;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomCode, out var room))
            {
                return false;
            }

            phase = room.Phase;
            if (phase == RoomPhase.Answer)
            {
                if (!AllActiveAnswered(room))
                {
                    return false;
                }

                MoveToSelfReport(room, DateTimeOffset.UtcNow);
                return true;
            }

            if (phase == RoomPhase.SelfReport)
            {
                if (!AllActiveSelfReportDone(room))
                {
                    return false;
                }

                MoveToVote(room, DateTimeOffset.UtcNow);
                return true;
            }

            if (phase != RoomPhase.Vote || !AllActiveVoted(room))
            {
                return false;
            }
        }

        return await ScoreRoundByCodeAsync(roomCode, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> AdvanceExpiredRoomsAsync(CancellationToken cancellationToken = default)
    {
        List<(string Code, RoomPhase Phase)> dueRooms;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            dueRooms = _rooms.Values
                .Where(room => room.PhaseEndsAt <= now && room.Phase is RoomPhase.Answer or RoomPhase.SelfReport or RoomPhase.Vote)
                .Select(room => (room.Code, room.Phase))
                .ToList();
        }

        var changed = new List<string>();
        foreach (var (code, phase) in dueRooms)
        {
            if (phase == RoomPhase.Vote)
            {
                if (await ScoreRoundByCodeAsync(code, cancellationToken))
                {
                    changed.Add(code);
                }

                continue;
            }

            lock (_gate)
            {
                if (!_rooms.TryGetValue(code, out var room) || room.Phase != phase || room.PhaseEndsAt > DateTimeOffset.UtcNow)
                {
                    continue;
                }

                if (room.Phase == RoomPhase.Answer)
                {
                    MoveToSelfReport(room, DateTimeOffset.UtcNow);
                    changed.Add(code);
                }
                else if (room.Phase == RoomPhase.SelfReport)
                {
                    MoveToVote(room, DateTimeOffset.UtcNow);
                    changed.Add(code);
                }
            }
        }

        return changed;
    }

    public async Task<bool> AdvanceExpiredRoomForConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        string roomCode;
        RoomPhase phase;
        lock (_gate)
        {
            var room = GetRoomByConnection(connectionId);
            if (room.PhaseEndsAt is null || room.PhaseEndsAt > DateTimeOffset.UtcNow)
            {
                return false;
            }

            roomCode = room.Code;
            phase = room.Phase;
            if (phase == RoomPhase.Answer)
            {
                MoveToSelfReport(room, DateTimeOffset.UtcNow);
                return true;
            }

            if (phase == RoomPhase.SelfReport)
            {
                MoveToVote(room, DateTimeOffset.UtcNow);
                return true;
            }

            if (phase != RoomPhase.Vote)
            {
                return false;
            }
        }

        return await ScoreRoundByCodeAsync(roomCode, cancellationToken);
    }

    public void SelfReport(string connectionId)
    {
        lock (_gate)
        {
            var room = GetRoomByConnection(connectionId);
            var player = GetPlayer(room, connectionId);
            EnsurePhase(room, RoomPhase.SelfReport);
            EnsureTellChoiceOpen(room, player);
            if (!room.SelfReports.Any(x => x.PlayerId == player.Id))
            {
                room.SelfReports.Add(new SelfReportClaim(player.Id, DateTimeOffset.UtcNow));
            }

            room.SelfReportDone.Add(player.Id);
        }
    }

    public void BetSafe(string connectionId)
    {
        lock (_gate)
        {
            var room = GetRoomByConnection(connectionId);
            var player = GetPlayer(room, connectionId);
            EnsurePhase(room, RoomPhase.SelfReport);
            EnsureTellChoiceOpen(room, player);
            room.SafeBets.Add(new SafeBetClaim(player.Id, DateTimeOffset.UtcNow));
            room.SelfReportDone.Add(player.Id);
        }
    }

    public void FinishSelfReport(string connectionId)
    {
        lock (_gate)
        {
            var room = GetRoomByConnection(connectionId);
            var player = GetPlayer(room, connectionId);
            EnsurePhase(room, RoomPhase.SelfReport);
            EnsureTellChoiceOpen(room, player);
            room.SelfReportDone.Add(player.Id);
        }
    }

    public void SubmitVote(string connectionId, SubmitVoteRequest request)
    {
        lock (_gate)
        {
            var room = GetRoomByConnection(connectionId);
            var player = GetPlayer(room, connectionId);
            EnsurePhase(room, RoomPhase.Vote);
            if (!room.Answers.TryGetValue(player.Id, out var ownAnswer))
            {
                throw new InvalidOperationException("Submit an answer before voting.");
            }

            if (ownAnswer.AnswerId == request.AnswerId)
            {
                throw new InvalidOperationException("You cannot vote for your own answer.");
            }

            if (!room.Answers.Values.Any(x => x.AnswerId == request.AnswerId))
            {
                throw new InvalidOperationException("Answer not found.");
            }

            room.Votes[player.Id] = new PlayerVote(player.Id, request.AnswerId, request.Confidence, DateTimeOffset.UtcNow);
        }
    }

    public async Task AdvancePhaseAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        RoomState room;
        lock (_gate)
        {
            room = GetRoomByConnection(connectionId);
            EnsureHost(room, connectionId);
        }

        if (room.Phase == RoomPhase.Vote)
        {
            await ScoreRoundByCodeAsync(room.Code, cancellationToken);
            return;
        }

        lock (_gate)
        {
            switch (room.Phase)
            {
                case RoomPhase.Answer:
                    MoveToSelfReport(room, DateTimeOffset.UtcNow);
                    break;
                case RoomPhase.SelfReport:
                    MoveToVote(room, DateTimeOffset.UtcNow);
                    break;
                case RoomPhase.Results:
                    if (room.CurrentRound >= room.TotalRounds)
                    {
                        room.Phase = RoomPhase.GameOver;
                        room.PhaseEndsAt = null;
                    }
                    else
                    {
                        foreach (var player in room.Players.Where(x => x.WaitingForNextRound))
                        {
                            player.WaitingForNextRound = false;
                player.Score = ScoringEngine.StartingScore;
                player.Ready = false;
            }

                        StartNextRound(room);
                    }
                    break;
            }
        }
    }

    public void SkipRound(string connectionId)
    {
        lock (_gate)
        {
            var room = GetRoomByConnection(connectionId);
            EnsureHost(room, connectionId);
            StartNextRound(room);
        }
    }

    public void RemovePlayer(string connectionId, Guid playerId)
    {
        lock (_gate)
        {
            var room = GetRoomByConnection(connectionId);
            EnsureHost(room, connectionId);
            var player = room.Players.FirstOrDefault(x => x.Id == playerId);
            if (player is null || player.Id == room.HostPlayerId)
            {
                return;
            }

            foreach (var connection in player.Connections)
            {
                _connections.Remove(connection);
            }

            room.Players.Remove(player);
        }
    }

    public async Task EndGameAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        GameSummaryDto? summary = null;
        lock (_gate)
        {
            var room = GetRoomByConnection(connectionId);
            EnsureHost(room, connectionId);
            room.Phase = RoomPhase.GameOver;
            room.PhaseEndsAt = null;
            summary = BuildSummary(room);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var contentStore = scope.ServiceProvider.GetRequiredService<IContentStore>();
        await contentStore.SaveGameSummaryAsync(summary, cancellationToken);
    }

    private async Task ScoreRoundAsync(RoomState room, CancellationToken cancellationToken)
    {
        ScoringInput input;
        lock (_gate)
        {
            if (room.CurrentRoundState is null || room.Round is null)
            {
                throw new InvalidOperationException("No active round.");
            }

            var activePlayers = room.ActivePlayers().ToList();
            if (room.Answers.Count < activePlayers.Count)
            {
                foreach (var player in activePlayers.Where(player => !room.Answers.ContainsKey(player.Id)))
                {
                    room.Answers[player.Id] = new AnswerState(Guid.NewGuid(), player.Id, "blank", player.PromptIndex);
                }
            }

            input = new ScoringInput(
                room.CurrentRound,
                room.Rollover,
                room.SelfReportSeconds,
                room.SelfReportOpenedAt,
                room.Round,
                room.MajorityPrompt,
                room.AlternatePrompt,
                room.ImposterPlayerId!.Value,
                activePlayers.Select(x => new ScoringPlayer(x.Id, x.Name, x.Score)).ToList(),
                room.Answers.Values.Select(x => new SubmittedAnswer(x.AnswerId, x.PlayerId, x.Text, x.PromptIndex)).ToList(),
                room.Votes.Values.ToList(),
                room.SelfReports.ToList(),
                room.SafeBets.ToList());
        }

        ScoringOutcome outcome;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var scoringEngine = scope.ServiceProvider.GetRequiredService<ScoringEngine>();
            outcome = await scoringEngine.ScoreAsync(input, cancellationToken);
        }

        lock (_gate)
        {
            foreach (var player in room.Players)
            {
                if (outcome.Scores.TryGetValue(player.Id, out var score))
                {
                    player.Score = score;
                }
            }

            room.Rollover = outcome.Result.Rollover;
            room.LastResult = outcome.Result;
            room.RoundResults.Add(outcome.Result);
            room.Phase = RoomPhase.Results;
            room.PhaseEndsAt = null;
        }
    }

    private async Task<bool> ScoreRoundByCodeAsync(string roomCode, CancellationToken cancellationToken)
    {
        RoomState room;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomCode, out room!) || room.Phase != RoomPhase.Vote || room.ScoringInProgress)
            {
                return false;
            }

            room.ScoringInProgress = true;
        }

        try
        {
            await ScoreRoundAsync(room, cancellationToken);
            return true;
        }
        finally
        {
            lock (_gate)
            {
                if (_rooms.TryGetValue(roomCode, out var current))
                {
                    current.ScoringInProgress = false;
                }
            }
        }
    }

    private static bool AllActiveAnswered(RoomState room)
    {
        var activePlayers = room.ActivePlayers().ToList();
        return activePlayers.Count > 0 && activePlayers.All(player => room.Answers.ContainsKey(player.Id));
    }

    private static bool AllActiveVoted(RoomState room)
    {
        var activePlayers = room.ActivePlayers().ToList();
        return activePlayers.Count > 0 && activePlayers.All(player => room.Votes.ContainsKey(player.Id));
    }

    private static bool AllActiveSelfReportDone(RoomState room)
    {
        var activePlayers = room.ActivePlayers().ToList();
        return activePlayers.Count > 0 && activePlayers.All(player => room.SelfReportDone.Contains(player.Id));
    }

    private static void FillMissingAnswers(RoomState room)
    {
        foreach (var player in room.ActivePlayers().Where(player => !room.Answers.ContainsKey(player.Id)))
        {
            room.Answers[player.Id] = new AnswerState(Guid.NewGuid(), player.Id, "blank", player.PromptIndex);
        }
    }

    private static void MoveToSelfReport(RoomState room, DateTimeOffset now)
    {
        FillMissingAnswers(room);
        room.Phase = RoomPhase.SelfReport;
        room.SelfReportOpenedAt = now;
        room.PhaseEndsAt = now.AddSeconds(room.SelfReportSeconds);
    }

    private static void MoveToVote(RoomState room, DateTimeOffset now)
    {
        room.Phase = RoomPhase.Vote;
        room.PhaseEndsAt = now.AddSeconds(room.VoteSeconds);
    }

    private void StartNextRound(RoomState room)
    {
        if (room.Category is null)
        {
            throw new InvalidOperationException("Choose a category before starting.");
        }

        room.CurrentRound++;
        room.Phase = RoomPhase.Answer;
        room.PhaseEndsAt = DateTimeOffset.UtcNow.AddSeconds(room.AnswerSeconds);
        room.Answers.Clear();
        room.Votes.Clear();
        room.SelfReports.Clear();
        room.SafeBets.Clear();
        room.SelfReportDone.Clear();
        room.LastResult = null;
        room.SelfReportOpenedAt = DateTimeOffset.UtcNow;

        var available = room.Category.Rounds
            .Where(round => !room.RecentRoundIds.Contains(round.Id))
            .ToList();

        if (available.Count == 0)
        {
            room.RecentRoundIds.Clear();
            available = room.Category.Rounds.ToList();
        }

        room.Round = available[_random.Next(available.Count)];
        room.RecentRoundIds.Enqueue(room.Round.Id);
        while (room.RecentRoundIds.Count > Math.Min(12, room.Category.Rounds.Count))
        {
            room.RecentRoundIds.Dequeue();
        }

        var flip = _random.Next(2) == 0;
        room.MajorityPrompt = flip ? room.Round.PromptA : room.Round.PromptB;
        room.AlternatePrompt = flip ? room.Round.PromptB : room.Round.PromptA;
        room.MajorityPromptIndex = flip ? 0 : 1;
        room.AlternatePromptIndex = flip ? 1 : 0;

        var activePlayers = room.ActivePlayers().ToList();
        var imposter = activePlayers[_random.Next(activePlayers.Count)];
        room.ImposterPlayerId = imposter.Id;
        foreach (var player in activePlayers)
        {
            player.Prompt = player.Id == imposter.Id ? room.AlternatePrompt : room.MajorityPrompt;
            player.PromptIndex = player.Id == imposter.Id ? room.AlternatePromptIndex : room.MajorityPromptIndex;
        }

        room.CurrentRoundState = Guid.NewGuid();
    }

    private RoomState GetRoomByConnection(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var mapped) || !_rooms.TryGetValue(mapped.RoomCode, out var room))
        {
            throw new InvalidOperationException("Join a room first.");
        }

        return room;
    }

    private PlayerState GetPlayer(RoomState room, string connectionId)
    {
        var playerId = _connections[connectionId].PlayerId;
        return room.Players.First(x => x.Id == playerId);
    }

    private void EnsureHost(RoomState room, string connectionId)
    {
        var player = GetPlayer(room, connectionId);
        if (player.Id != room.HostPlayerId)
        {
            throw new InvalidOperationException("Only the host can do that.");
        }
    }

    private static void EnsurePhase(RoomState room, RoomPhase phase)
    {
        if (room.Phase != phase)
        {
            throw new InvalidOperationException($"This action is only available during {phase}.");
        }
    }

    private static void EnsureTellChoiceOpen(RoomState room, PlayerState player)
    {
        if (room.SelfReportDone.Contains(player.Id))
        {
            throw new InvalidOperationException("Your tell choice is already locked.");
        }
    }

    private static DevBotTurn BuildBotTurn(RoomState room, PlayerState bot, IReadOnlyList<PlayerState> activePlayers)
    {
        var answerOptions = room.Answers.Values
            .OrderBy(answer => answer.Text)
            .Select(answer => new DevBotAnswerOption(
                answer.AnswerId,
                answer.Text,
                answer.PlayerId == bot.Id,
                room.SelfReports.Any(claim => claim.PlayerId == answer.PlayerId),
                room.SafeBets.Any(bet => bet.PlayerId == answer.PlayerId)))
            .ToList();

        return new DevBotTurn(
            room.Code,
            bot.Id,
            bot.Name,
            room.Phase,
            room.CurrentRound,
            bot.Prompt,
            room.MajorityPrompt,
            room.AlternatePrompt,
            room.Rollover,
            activePlayers.Count,
            answerOptions);
    }

    private static string CleanBotAnswer(string? answer)
    {
        var cleaned = (answer ?? "").Trim();
        if (cleaned.Length == 0)
        {
            return "sand";
        }

        cleaned = cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        var letters = new string(cleaned.Where(char.IsLetterOrDigit).Take(32).ToArray());
        return letters.Length == 0 ? "sand" : letters;
    }

    private static void ApplyBotTellChoice(RoomState room, PlayerState bot, string? tellChoice)
    {
        var choice = tellChoice?.Trim().ToLowerInvariant();
        if (choice is "claim" or "mirage" or "self-report")
        {
            room.SelfReports.Add(new SelfReportClaim(bot.Id, DateTimeOffset.UtcNow));
        }
        else if (choice is "safe" or "bet-safe" or "bet_safe")
        {
            room.SafeBets.Add(new SafeBetClaim(bot.Id, DateTimeOffset.UtcNow));
        }

        room.SelfReportDone.Add(bot.Id);
    }

    private RoomSnapshotDto Snapshot(RoomState room, Guid? viewerId)
    {
        var revealAuthors = room.Phase is RoomPhase.Results or RoomPhase.GameOver;
        var revealAnswers = room.Phase is RoomPhase.SelfReport or RoomPhase.Vote or RoomPhase.Results or RoomPhase.GameOver;
        var warning = room.Players.Count is < 4 or > 20
            ? "Sarab is tuned for 4-20 players, but the host can still run the room."
            : null;

        return new RoomSnapshotDto(
            room.Code,
            room.Phase,
            viewerId,
            room.Phase == RoomPhase.Answer ? room.Players.FirstOrDefault(x => x.Id == viewerId)?.Prompt : null,
            room.Players.FirstOrDefault(x => x.Id == room.HostPlayerId)?.Name ?? "Host",
            room.CurrentRound,
            room.TotalRounds,
            room.Rollover,
            room.PhaseEndsAt,
            room.Players.Select(player => new PlayerDto(
                player.Id,
                player.Name,
                player.Score,
                player.Avatar,
                player.Connected,
                player.Id == room.HostPlayerId,
                player.IsBot,
                player.Ready,
                player.WaitingForNextRound,
                room.Answers.ContainsKey(player.Id),
                room.SelfReports.Any(x => x.PlayerId == player.Id),
                room.SafeBets.Any(x => x.PlayerId == player.Id),
                room.SelfReportDone.Contains(player.Id),
                room.Votes.ContainsKey(player.Id))).ToList(),
            revealAnswers
                ? room.Answers.Values
                    .OrderBy(x => x.Text)
                    .Select(answer => new AnswerDto(
                        answer.AnswerId,
                        answer.Text,
                        revealAuthors ? answer.PlayerId : null,
                        revealAuthors ? room.Players.First(x => x.Id == answer.PlayerId).Name : null,
                        answer.PlayerId == viewerId,
                        revealAuthors && room.LastResult?.ImposterAnswerId == answer.AnswerId))
                    .ToList()
                : [],
            room.LastResult,
            room.Categories,
            warning);
    }

    private string GenerateCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        while (true)
        {
            var code = new string(Enumerable.Range(0, 5).Select(_ => alphabet[_random.Next(alphabet.Length)]).ToArray());
            if (!_rooms.ContainsKey(code))
            {
                return code;
            }
        }
    }

    private static string CleanName(string name)
    {
        var cleaned = name.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Player" : cleaned[..Math.Min(24, cleaned.Length)];
    }

    private static PlayerAvatarDto NormalizeAvatar(PlayerAvatarDto? avatar)
    {
        if (avatar is null)
        {
            return DefaultAvatar();
        }

        var skinColor = IsValidSkinColor(avatar.SkinColor) ? avatar.SkinColor.ToUpperInvariant() : DefaultAvatar().SkinColor;
        return avatar with { SkinColor = skinColor };
    }

    private static bool IsValidSkinColor(string value)
    {
        return value.Length == 7
            && value[0] == '#'
            && value.Skip(1).All(Uri.IsHexDigit);
    }

    private static PlayerAvatarDto DefaultAvatar() => new(AvatarGender.Male, "#B97855", null, null, null);

    private static GameSummaryDto BuildSummary(RoomState room)
    {
        return new GameSummaryDto(
            Guid.NewGuid(),
            room.Code,
            DateTimeOffset.UtcNow,
            room.Category?.Name ?? "Unknown",
            room.Players.Select(x => new PlayerSummaryDto(x.Id, x.Name, x.Score)).OrderByDescending(x => x.Score).ToList(),
            room.RoundResults.ToList());
    }

    private async Task<IReadOnlyList<CatalogCategoryDto>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var contentStore = scope.ServiceProvider.GetRequiredService<IContentStore>();
        return await contentStore.GetCatalogAsync(cancellationToken);
    }
}

internal sealed class RoomState(string code, Guid hostPlayerId)
{
    public string Code { get; } = code;
    public Guid HostPlayerId { get; } = hostPlayerId;
    public RoomPhase Phase { get; set; } = RoomPhase.Lobby;
    public int CurrentRound { get; set; }
    public int TotalRounds { get; set; }
    public int Rollover { get; set; }
    public int AnswerSeconds { get; set; } = 30;
    public int SelfReportSeconds { get; set; } = 15;
    public int VoteSeconds { get; set; } = 30;
    public DateTimeOffset? PhaseEndsAt { get; set; }
    public DateTimeOffset SelfReportOpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public PromptCategory? Category { get; set; }
    public PromptRound? Round { get; set; }
    public string MajorityPrompt { get; set; } = "";
    public string AlternatePrompt { get; set; } = "";
    public int MajorityPromptIndex { get; set; }
    public int AlternatePromptIndex { get; set; } = 1;
    public Guid? ImposterPlayerId { get; set; }
    public Guid? CurrentRoundState { get; set; }
    public List<PlayerState> Players { get; } = [];
    public Dictionary<Guid, AnswerState> Answers { get; } = [];
    public Dictionary<Guid, PlayerVote> Votes { get; } = [];
    public List<SelfReportClaim> SelfReports { get; } = [];
    public List<SafeBetClaim> SafeBets { get; } = [];
    public HashSet<Guid> SelfReportDone { get; } = [];
    public List<RoundResultDto> RoundResults { get; } = [];
    public RoundResultDto? LastResult { get; set; }
    public Queue<Guid> RecentRoundIds { get; } = [];
    public IReadOnlyList<CatalogCategoryDto> Categories { get; set; } = [];
    public bool ScoringInProgress { get; set; }

    public IEnumerable<PlayerState> ActivePlayers() => Players.Where(x => !x.WaitingForNextRound);

    public IReadOnlyList<string> ConnectionIds() => Players.SelectMany(x => x.Connections).ToList();
}

internal sealed record PlayerState(Guid Id, string Name, bool IsHost)
{
    public int Score { get; set; } = ScoringEngine.StartingScore;
    public PlayerAvatarDto Avatar { get; set; } = new(AvatarGender.Male, "#B97855", null, null, null);
    public bool Connected { get; set; } = true;
    public bool IsBot { get; set; }
    public bool Ready { get; set; }
    public bool BotTurnInProgress { get; set; }
    public bool WaitingForNextRound { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }
    public HashSet<string> Connections { get; } = [];
    public string Prompt { get; set; } = "";
    public int PromptIndex { get; set; }
}

internal sealed record AnswerState(Guid AnswerId, Guid PlayerId, string Text, int PromptIndex);

public sealed record DevBotTurn(
    string RoomCode,
    Guid BotPlayerId,
    string BotName,
    RoomPhase Phase,
    int RoundNumber,
    string Prompt,
    string MajorityPrompt,
    string AlternatePrompt,
    int Rollover,
    int ActivePlayerCount,
    IReadOnlyList<DevBotAnswerOption> Answers);

public sealed record DevBotAnswerOption(Guid Id, string Text, bool IsMine, bool AuthorClaimedMirage, bool AuthorBetSafe);

public sealed record DevBotDecision(string? Answer, string? TellChoice, Guid? VoteAnswerId, ConfidenceLevel Confidence);
