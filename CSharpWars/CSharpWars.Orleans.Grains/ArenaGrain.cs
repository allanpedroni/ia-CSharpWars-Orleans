﻿using CSharpWars.Orleans.Contracts.Arena;
using CSharpWars.Orleans.Contracts.Bot;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Runtime;

namespace CSharpWars.Orleans.Grains;

public class ArenaState
{
    public bool Exists { get; set; }
    public string Name { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public IList<Guid>? BotIds { get; set; }
}

public interface IArenaGrain : IGrainWithStringKey
{
    Task<List<BotDto>> GetAllActiveBots();

    Task<ArenaDto> GetArenaDetails();

    Task<BotDto> CreateBot(string playerName, BotToCreateDto bot);
    Task DeleteArena();
}

public class ArenaGrain : Grain, IArenaGrain
{
    private readonly IPersistentState<ArenaState> _state;
    private readonly IConfiguration _configuration;

    public ArenaGrain(
        [PersistentState("arena", "arenaStore")] IPersistentState<ArenaState> state,
        IConfiguration configuration)
    {
        _state = state;
        _configuration = configuration;
    }

    public async Task<List<BotDto>> GetAllActiveBots()
    {
        if (!_state.State.Exists)
        {
            _ = await GetArenaDetails();
        }

        var activeBots = new List<BotDto>();

        if (_state.State.BotIds != null)
        {
            foreach (var botId in _state.State.BotIds)
            {
                var botGrain = GrainFactory.GetGrain<IBotGrain>(botId);
                activeBots.Add(await botGrain.GetState());
            }
        }

        await PingProcessor();

        return activeBots;
    }

    public async Task<ArenaDto> GetArenaDetails()
    {
        if (!_state.State.Exists)
        {
            _state.State.Name = this.GetPrimaryKeyString();
            _state.State.Width = _configuration.GetValue<int>("ARENA_WIDTH");
            _state.State.Height = _configuration.GetValue<int>("ARENA_HEIGHT");
            _state.State.BotIds = new List<Guid>();
            _state.State.Exists = true;
            await _state.WriteStateAsync();
        }

        await PingProcessor();

        return new ArenaDto(_state.State.Name, _state.State.Width, _state.State.Height);
    }

    public async Task<BotDto> CreateBot(string playerName, BotToCreateDto bot)
    {
        if (!_state.State.Exists || _state.State.BotIds == null)
        {
            throw new ArgumentNullException();
        }

        var botId = Guid.NewGuid();

        var playerGrain = GrainFactory.GetGrain<IPlayerGrain>(playerName);
        await playerGrain.ValidateBotDeploymentLimit();
        await playerGrain.BotCeated(botId);

        var botGrain = GrainFactory.GetGrain<IBotGrain>(botId);
        var createdBot = await botGrain.CreateBot(bot);

        _state.State.BotIds.Add(botId);
        await _state.WriteStateAsync();

        return createdBot;
    }

    public async Task DeleteArena()
    {
        if (_state.State.Exists && _state.State.BotIds != null)
        {
            var processingGrain = GrainFactory.GetGrain<IProcessingGrain>(_state.State.Name);
            await processingGrain.Stop();

            await Task.Delay(2000);

            foreach (var botId in _state.State.BotIds)
            {
                var botGrain = GrainFactory.GetGrain<IBotGrain>(botId);
                await botGrain.DeleteBot();
            }

            await _state.ClearStateAsync();
        }

        DeactivateOnIdle();
    }

    private async Task PingProcessor()
    {
        var processingGrain = GrainFactory.GetGrain<IProcessingGrain>(_state.State.Name);
        await processingGrain.Ping();
    }
}