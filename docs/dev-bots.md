# Sarab Dev Bots

Development bots are for local playtesting only. They let one human host fill a room and run the full Sarab loop without four real players.

## How They Work

- When dev bots are enabled on the backend, the lobby host sees `Add 3 dev bots`.
- Bots join as ready players and act automatically during answer, tell, and vote phases.
- During answer phase, the LLM returns five one-word answer candidates and the server randomly submits one of them.
- Bots require `OPENAI_API_KEY` or `OpenAI:ApiKey`; there is no deterministic fallback.
- If the LLM call fails, the bot turn is retried on the next runner tick.

## Configuration

Default local behavior:

```powershell
$env:OPENAI_API_KEY="your-key"
$env:Sarab__DevBots__Model="gpt-5.4-mini"
dotnet run --project src/Sarab.Api/Sarab.Api.csproj --urls http://localhost:5000
```

Optional flags:

```powershell
$env:Sarab__DevBots__Enabled="true"
$env:Sarab__DevBots__UseLlm="true"
```

Leave `Sarab__DevBots__UseLlm=true`; setting it to `false` disables bot decisions because fallback logic has been removed.

## Bot Prompt

The server explains Sarab to the agent every turn:

- Almost everyone gets one secret prompt.
- One hidden mirage player may have a different prompt.
- Answer phase asks for five possible one-word answers; Sarab randomly picks the submitted word.
- Tell phase has `claim`, `safe`, or `neutral`.
- Vote phase requires choosing another player's anonymous answer.
- Confidence controls risk and reward.

The LLM must return a small JSON decision only. The server still validates and sanitizes the action.
