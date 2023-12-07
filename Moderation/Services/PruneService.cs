﻿using Mewdeko.Common.Collections;
using System.Threading.Tasks;

namespace Mewdeko.Modules.Moderation.Services;

public class PurgeService : INService
{
    //channelids where Purges are currently occuring
    private readonly ConcurrentHashSet<ulong> _pruningGuilds = new();

    private readonly TimeSpan _twoWeeks = TimeSpan.FromDays(14);

    public async Task PurgeWhere(ITextChannel channel, int amount, Func<IMessage, bool> predicate)
    {
        channel.ThrowIfNull(nameof(channel));
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount));

        if (!_pruningGuilds.Add(channel.GuildId))
            return;

        try
        {
            var msgs = (await channel.GetMessagesAsync(50).FlattenAsync().ConfigureAwait(false)).Where(predicate)
                                                                                                .Take(amount).ToArray();
            while (amount > 0 && msgs.Length > 0)
            {
                var lastMessage = msgs[^1];

                var bulkDeletable = new List<IMessage>();
                var singleDeletable = new List<IMessage>();
                foreach (var x in msgs)
                {
                    if (DateTime.UtcNow - x.CreatedAt < _twoWeeks)
                        bulkDeletable.Add(x);
                    else
                        singleDeletable.Add(x);
                }

                if (bulkDeletable.Count > 0)
                {
                    await Task.WhenAll(Task.Delay(1000), channel.DeleteMessagesAsync(bulkDeletable))
                        .ConfigureAwait(false);
                }

                var i = 0;
                foreach (var group in singleDeletable.GroupBy(_ => ++i / (singleDeletable.Count / 5)))
                {
                    await Task.WhenAll(Task.Delay(1000), Task.WhenAll(group.Select(x => x.DeleteAsync())))
                        .ConfigureAwait(false);
                }

                //this isn't good, because this still work as if i want to remove only specific user's messages from the last
                //100 messages, Maybe this needs to be reduced by msgs.Length instead of 100
                amount -= 50;
                if (amount > 0)
                {
                    msgs = (await channel.GetMessagesAsync(lastMessage, Direction.Before, 50).FlattenAsync()
                        .ConfigureAwait(false)).Where(predicate).Take(amount).ToArray();
                }
            }
        }
        catch
        {
            //ignore
        }
        finally
        {
            _pruningGuilds.TryRemove(channel.GuildId);
        }
    }
}