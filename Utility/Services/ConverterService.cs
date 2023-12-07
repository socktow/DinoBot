﻿using Mewdeko.Modules.Utility.Common;
using Newtonsoft.Json;
using StackExchange.Redis;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Mewdeko.Modules.Utility.Services;

public class ConverterService : INService, IUnloadableService
{
    private readonly IDataCache _cache;

    private readonly Timer _currencyUpdater;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TimeSpan _updateInterval = new(12, 0, 0);

    public ConverterService(DiscordSocketClient client,
        IDataCache cache, IHttpClientFactory factory)
    {
        _cache = cache;
        _httpFactory = factory;

        if (client.ShardId == 0)
        {
            _currencyUpdater = new Timer(
                async shouldLoad => await UpdateCurrency((bool)shouldLoad).ConfigureAwait(false),
                client.ShardId == 0,
                TimeSpan.Zero,
                _updateInterval);
        }
    }

    public ConvertUnit[] Units =>
        _cache.Redis.GetDatabase()
            .StringGet("converter_units")
            .ToString()
            .MapJson<ConvertUnit[]>();

    public Task Unload()
    {
        _currencyUpdater.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    private async Task<Rates> GetCurrencyRates()
    {
        using var http = _httpFactory.CreateClient();
        var res = await http.GetStringAsync("https://convertapi.nadeko.bot/latest").ConfigureAwait(false);
        return JsonConvert.DeserializeObject<Rates>(res);
    }

    private async Task UpdateCurrency(bool shouldLoad)
    {
        try
        {
            const string unitTypeString = "currency";
            if (shouldLoad)
            {
                var currencyRates = await GetCurrencyRates().ConfigureAwait(false);
                var baseType = new ConvertUnit
                {
                    Triggers = new[] { currencyRates.Base },
                    Modifier = decimal.One,
                    UnitType = unitTypeString
                };
                var range = currencyRates.ConversionRates.Select(u => new ConvertUnit
                {
                    Triggers = new[] { u.Key },
                    Modifier = u.Value,
                    UnitType = unitTypeString
                }).ToArray();

                var fileData = (JsonConvert.DeserializeObject<ConvertUnit[]>(
                        await File.ReadAllTextAsync("data/units.json").ConfigureAwait(false)) ?? Array.Empty<ConvertUnit>())
                    .Where(x => x.UnitType != "currency");

                var data = JsonConvert.SerializeObject(range.Append(baseType).Concat(fileData).ToList());
                _cache.Redis.GetDatabase()
                    .StringSet("converter_units", data,  flags: CommandFlags.FireAndForget);
            }
        }
        catch
        {
            // ignored
        }
    }
}

public class Rates
{
    public string Base { get; set; }
    public DateTime Date { get; set; }

    [JsonProperty("rates")] public Dictionary<string, decimal> ConversionRates { get; set; }
}