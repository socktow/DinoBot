﻿using Discord.Commands;
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.Games.Common;
using Mewdeko.Modules.Games.Services;
using System.Threading;
using System.Threading.Tasks;

namespace Mewdeko.Modules.Games;

public partial class Games
{
    [Group]
    public class TicTacToeCommands : MewdekoSubmodule<GamesService>
    {
        private readonly DiscordSocketClient _client;
        private readonly SemaphoreSlim _sem = new(1, 1);

        public TicTacToeCommands(DiscordSocketClient client) => _client = client;

        [Cmd, Aliases, RequireContext(ContextType.Guild),
         MewdekoOptions(typeof(TicTacToe.Options))]
        public async Task TicTacToe(params string[] args)
        {
            var (options, _) = OptionsParser.ParseFrom(new TicTacToe.Options(), args);
            var channel = (ITextChannel)ctx.Channel;

            await _sem.WaitAsync(1000).ConfigureAwait(false);
            try
            {
                if (Service.TicTacToeGames.TryGetValue(channel.Id, out var game))
                {
                    _ = Task.Run(async () => await game.Start((IGuildUser)ctx.User).ConfigureAwait(false));
                    return;
                }

                game = new TicTacToe(Strings, _client, channel, (IGuildUser)ctx.User, options);
                Service.TicTacToeGames.Add(channel.Id, game);
                await ReplyConfirmLocalizedAsync("ttt_created").ConfigureAwait(false);

                game.OnEnded += _ =>
                {
                    Service.TicTacToeGames.Remove(channel.Id);
                    _sem.Dispose();
                };
            }
            finally
            {
                _sem.Release();
            }
        }
    }
}