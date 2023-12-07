﻿using Mewdeko.Modules.Gambling.Common.AnimalRacing;
using System.Threading.Tasks;

namespace Mewdeko.Modules.Gambling.Services;

public class AnimalRaceService : INService, IUnloadableService
{
    public ConcurrentDictionary<ulong, AnimalRace> AnimalRaces { get; } = new();

    public Task Unload()
    {
        foreach (var kvp in AnimalRaces)
        {
            try
            {
                kvp.Value.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        return Task.CompletedTask;
    }
}