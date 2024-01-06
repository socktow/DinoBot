using Discord.Commands;
using Fergun.Interactive;
using Fergun.Interactive.Pagination;
using Humanizer;
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Common.TypeReaders.Models;
using Mewdeko.Modules.Utility.Common;
using Mewdeko.Modules.Utility.Services;
using Mewdeko.Services.Impl;
using Mewdeko.Services.Settings;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using StringExtensions = Mewdeko.Extensions.StringExtensions;

namespace Mewdeko.Modules.Utility;

public partial class Utility : MewdekoModuleBase<UtilityService>
{
    private static readonly SemaphoreSlim _sem = new(1, 1);
    private readonly DiscordSocketClient _client;
    private readonly IBotCredentials _creds;
    private readonly IStatsService _stats;
    private readonly DownloadTracker _tracker;
    private readonly InteractiveService _interactivity;
    private readonly ICoordinator _coordinator;
    private readonly GuildSettingsService _guildSettings;
    private readonly HttpClient _httpClient;
    private readonly BotConfigService _config;

    public Utility(
        DiscordSocketClient client,
        IStatsService stats, IBotCredentials creds, DownloadTracker tracker, InteractiveService serv, ICoordinator coordinator,
        GuildSettingsService guildSettings,
        HttpClient httpClient,
        BotConfigService config)
    {
        _coordinator = coordinator;
        _guildSettings = guildSettings;
        _httpClient = httpClient;
        _config = config;
        _interactivity = serv;
        _client = client;
        _stats = stats;
        _creds = creds;
        _tracker = tracker;
    }
    [Cmd, Aliases, RequireContext(ContextType.Guild), UserPerm(GuildPermission.ManageMessages)]
    public async Task SaveChat(StoopidTime time, ITextChannel? channel = null)
    {
        var curTime = DateTime.UtcNow.Subtract(time.Time);
        if (!Directory.Exists(_creds.ChatSavePath))
        {
            await ctx.Channel.SendErrorAsync("Chat save directory does not exist. Please create it.").ConfigureAwait(false);
            return;
        }
        var secureString = StringExtensions.GenerateSecureString(16);
        try
        {
            Directory.CreateDirectory($"{_creds.ChatSavePath}/{ctx.Guild.Id}/{secureString}");
        }
        catch (Exception ex)
        {
            await ctx.Channel.SendErrorAsync($"Failed to create directory. {ex.Message}").ConfigureAwait(false);
            return;
        }
        if (time.Time.Days > 3)
        {
            await ctx.Channel.SendErrorAsync("Max time to grab messages is 3 days. This will be increased in the near future.").ConfigureAwait(false);
            return;
        }
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            Arguments = $"../ChatExporter/DiscordChatExporter.Cli.dll export -t {_creds.Token} -c {channel?.Id ?? ctx.Channel.Id} --after {curTime:yyyy-MM-ddTHH:mm:ssZ} --output \"{_creds.ChatSavePath}/{ctx.Guild.Id}/{secureString}/{ctx.Guild.Name.Replace(" ", "-")}-{(channel?.Name ?? ctx.Channel.Name).Replace(" ", "-")}-{curTime:yyyy-MM-ddTHH-mm-ssZ}.html\" --media true",
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using (ctx.Channel.EnterTypingState())
        {
            process.Start();
            await ctx.Channel.SendConfirmAsync("<a:loadingstate:1138172643867111595> Saving chat log, this may take some time...");
        }

        await process.WaitForExitAsync().ConfigureAwait(false);
        if (_creds.ChatSavePath.Contains("/usr/share/nginx/cdn"))
            await ctx.User.SendConfirmAsync(
                $"Your chat log is here: https://cdn.mewdeko.tech/chatlogs/{ctx.Guild.Id}/{secureString}/{ctx.Guild.Name.Replace(" ", "-")}-{(channel?.Name ?? ctx.Channel.Name).Replace(" ", "-")}-{curTime:yyyy-MM-ddTHH-mm-ssZ}.html").ConfigureAwait(false);
        else
            await ctx.Channel.SendConfirmAsync($"Your chat log is here: {_creds.ChatSavePath}/{ctx.Guild.Id}/{secureString}").ConfigureAwait(false);
    }
    [Cmd, Aliases]
    public async Task EmoteList([Remainder] string? emotetype = null)
    {
        var emotes = emotetype switch
        {
            "animated" => ctx.Guild.Emotes.Where(x => x.Animated).ToArray(),
            "nonanimated" => ctx.Guild.Emotes.Where(x => !x.Animated).ToArray(),
            _ => ctx.Guild.Emotes.ToArray()
        };

        if (emotes.Length == 0)
        {
            await ctx.Channel.SendErrorAsync("No emotes found!").ConfigureAwait(false);
            return;
        }

        var paginator = new LazyPaginatorBuilder()
            .AddUser(ctx.User)
            .WithPageFactory(PageFactory)
            .WithFooter(PaginatorFooter.PageNumber | PaginatorFooter.Users)
            .WithMaxPageIndex(emotes.Length / 10)
            .WithDefaultEmotes()
            .WithActionOnCancellation(ActionOnStop.DeleteMessage)
            .Build();

        await _interactivity.SendPaginatorAsync(paginator, Context.Channel,
            TimeSpan.FromMinutes(60)).ConfigureAwait(false);

        async Task<PageBuilder> PageFactory(int page)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            var titleText = emotetype switch
            {
                "animated" => $"{emotes.Length} Animated Emotes",
                "nonanimated" => $"{emotes.Length} Non Animated Emotes",
                _ =>
                    $"{emotes.Count(x => x.Animated)} Animated Emotes | {emotes.Count(x => !x.Animated)} Non Animated Emotes"
            };

            return new PageBuilder()
                                   .WithTitle(titleText)
                                   .WithDescription(string.Join("\n",
                                       emotes.OrderBy(x => x.Name).Skip(10 * page).Take(10)
                                             .Select(x => $"{x} `{x.Name}` [Link]({x.Url})")))
                                   .WithOkColor();
        }
    }

    [Cmd, Aliases]
    public async Task Invite()
    {
        var eb = new EmbedBuilder()
            .AddField("Invite Link (IOS shows an error so use the browser)",
                $"[Click Here](https://discord.com/oauth2/authorize?client_id={ctx.Client.CurrentUser.Id}&scope=bot&permissions=66186303&scope=bot%20applications.commands)")
            .AddField("Website/Docs", "https://chuchudayne.com")
            .AddField("Support Server", "https://discord.gg/dinogaming")
            .WithOkColor();
        await ctx.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task TestSite(string url)
    {
        var response = await _httpClient.GetAsync(url).ConfigureAwait(false);

        await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var statusCode = response.StatusCode;
        if (statusCode.ToString() == "Forbidden")
            await ctx.Channel.SendErrorAsync("Sites down m8").ConfigureAwait(false);
        else
            await ctx.Channel.SendConfirmAsync("Sites ok m8").ConfigureAwait(false);
    }

    [Cmd, Aliases, UserPerm(GuildPermission.ManageChannels)]
    public async Task ReactChannel(ITextChannel? chan = null)
    {
        var e = await Service.GetReactChans(ctx.Guild.Id);
        if (chan == null)
        {
            if (e == 0) return;
            await Service.SetReactChan(ctx.Guild, 0).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync("React Channel Disabled!").ConfigureAwait(false);
        }
        else
        {
            if (e == 0)
            {
                await Service.SetReactChan(ctx.Guild, chan.Id).ConfigureAwait(false);
                await ctx.Channel.SendConfirmAsync($"Your React Channel has been set to {chan.Mention}!").ConfigureAwait(false);
            }
            else
            {
                var chan2 = await ctx.Guild.GetTextChannelAsync(e).ConfigureAwait(false);
                if (e == chan.Id)
                {
                    await ctx.Channel.SendErrorAsync("This is already your React Channel!").ConfigureAwait(false);
                }
                else
                {
                    await Service.SetReactChan(ctx.Guild, chan.Id).ConfigureAwait(false);
                    await ctx.Channel.SendConfirmAsync(
                        $"Your React Channel has been switched from {chan2.Mention} to {chan.Mention}!").ConfigureAwait(false);
                }
            }
        }
    }

    [Cmd, Aliases, UserPerm(GuildPermission.Administrator),
     RequireContext(ContextType.Guild)]
    public async Task SnipeSet(string yesnt)
    {
        await Service.SnipeSet(ctx.Guild, yesnt).ConfigureAwait(false);
        var t = await Service.GetSnipeSet(ctx.Guild.Id);
        await ReplyConfirmLocalizedAsync("snipe_set", t ? "Enabled" : "Disabled").ConfigureAwait(false);
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public async Task Snipe()
    {
        if (!await Service.GetSnipeSet(ctx.Guild.Id))
        {
            await ReplyErrorLocalizedAsync("snipe_not_enabled", await _guildSettings.GetPrefix(ctx.Guild)).ConfigureAwait(false);
            return;
        }

        await Service.GetSnipes(ctx.Guild.Id).ConfigureAwait(false);
        var msg = (await Service.GetSnipes(ctx.Guild.Id).ConfigureAwait(false)).LastOrDefault(x => x.ChannelId == ctx.Channel.Id);
        if (msg is null)
        {
            await ctx.Channel.SendErrorAsync("There is nothing to snipe here!").ConfigureAwait(false);
            return;
        }
        var user = await ctx.Channel.GetUserAsync(msg.UserId).ConfigureAwait(false) ?? await _client.Rest.GetUserAsync(msg.UserId).ConfigureAwait(false);

        var em = new EmbedBuilder
        {
            Author = new EmbedAuthorBuilder
            {
                IconUrl = user.GetAvatarUrl(),
                Name = $"{user} said:"
            },
            Description = msg.Message,
            Footer = new EmbedFooterBuilder
            {
                IconUrl = ctx.User.GetAvatarUrl(),
                Text =
                    GetText("snipe_request", ctx.User.ToString(), (DateTime.UtcNow - msg.DateAdded).Humanize())
            },
            Color = Mewdeko.OkColor
        };
        await ctx.Channel.SendMessageAsync(embed: em.Build(), 
            components: _config.Data.ShowInviteButton ? new ComponentBuilder()
                                                        .WithButton(style: ButtonStyle.Link, 
                                                            url: "https://discord.com/oauth2/authorize?client_id=701019662795800606&permissions=8&response_type=code&redirect_uri=https%3A%2F%2Fmewdeko.tech&scope=bot%20applications.commands", 
                                                            label: "Invite Me!", 
                                                            emote: "<a:HaneMeow:968564817784877066>".ToIEmote()).Build() : null).ConfigureAwait(false);
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public async Task SnipeList(int amount = 5)
    {
        if (!await Service.GetSnipeSet(ctx.Guild.Id))
        {
            await ReplyErrorLocalizedAsync("snipe_not_enabled", await _guildSettings.GetPrefix(ctx.Guild)).ConfigureAwait(false);
            return;
        }

        var msgs = (await Service.GetSnipes(ctx.Guild.Id).ConfigureAwait(false)).Where(x => x.ChannelId == ctx.Channel.Id && x.Edited == 0);
        {
            var snipeStores = msgs as SnipeStore[] ?? msgs.ToArray();
            if (snipeStores.Length == 0)
            {
                await ReplyErrorLocalizedAsync("no_snipes").ConfigureAwait(false);
                return;
            }

            var msg = snipeStores.OrderByDescending(d => d.DateAdded).Where(x => x.Edited == 0).Take(amount);
            var paginator = new LazyPaginatorBuilder()
                .AddUser(ctx.User)
                .WithPageFactory(PageFactory)
                .WithFooter(PaginatorFooter.PageNumber | PaginatorFooter.Users)
                .WithMaxPageIndex(msg.Count() - 1)
                .WithDefaultEmotes()
            .WithActionOnCancellation(ActionOnStop.DeleteMessage)
                .Build();

            await _interactivity.SendPaginatorAsync(paginator, Context.Channel,
                TimeSpan.FromMinutes(60)).ConfigureAwait(false);

            async Task<PageBuilder> PageFactory(int page)
            {
                var msg1 = msg.Skip(page).FirstOrDefault();
                var user = await ctx.Channel.GetUserAsync(msg1.UserId).ConfigureAwait(false) ??
                           await _client.Rest.GetUserAsync(msg1.UserId).ConfigureAwait(false);

                return new PageBuilder()
                    .WithOkColor()
                    .WithAuthor(
                        new EmbedAuthorBuilder()
                            .WithIconUrl(user.RealAvatarUrl().AbsoluteUri)
                            .WithName($"{user} said:"))
                    .WithDescription($"{msg1.Message}")
                    .WithFooter($"\n\nMessage deleted {(DateTime.UtcNow - msg1.DateAdded).Humanize()} ago");
            }
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public async Task EditSnipeList(int amount = 5)
    {
        if (!await Service.GetSnipeSet(ctx.Guild.Id))
        {
            await ReplyErrorLocalizedAsync("snipe_not_enabled", await _guildSettings.GetPrefix(ctx.Guild)).ConfigureAwait(false);
            return;
        }

        var msgs = (await Service.GetSnipes(ctx.Guild.Id).ConfigureAwait(false)).Where(x => x.ChannelId == ctx.Channel.Id && x.Edited == 1);
        {
            var snipeStores = msgs as SnipeStore[] ?? msgs.ToArray();
            if (snipeStores.Length == 0)
            {
                await ReplyErrorLocalizedAsync("no_snipes").ConfigureAwait(false);
                return;
            }

            var msg = snipeStores.OrderByDescending(d => d.DateAdded).Where(x => x.Edited == 1).Take(amount);
            var paginator = new LazyPaginatorBuilder()
                .AddUser(ctx.User)
                .WithPageFactory(PageFactory)
                .WithFooter(PaginatorFooter.PageNumber | PaginatorFooter.Users)
                .WithMaxPageIndex(msg.Count() - 1)
                .WithDefaultEmotes()
            .WithActionOnCancellation(ActionOnStop.DeleteMessage)
                .Build();

            await _interactivity.SendPaginatorAsync(paginator, Context.Channel,
                TimeSpan.FromMinutes(60)).ConfigureAwait(false);

            async Task<PageBuilder> PageFactory(int page)
            {
                var msg1 = msg.Skip(page).FirstOrDefault();
                var user = await ctx.Channel.GetUserAsync(msg1.UserId).ConfigureAwait(false) ??
                           await _client.Rest.GetUserAsync(msg1.UserId).ConfigureAwait(false);

                return new PageBuilder()
                    .WithOkColor()
                    .WithAuthor(
                        new EmbedAuthorBuilder()
                            .WithIconUrl(user.RealAvatarUrl().AbsoluteUri)
                            .WithName($"{user} originally said:"))
                    .WithDescription($"{msg1.Message}")
                    .WithFooter($"\n\nMessage edited {(DateTime.UtcNow - msg1.DateAdded).Humanize()} ago");
            }
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild), Priority(1)]
    public async Task Snipe(IUser user1)
    {
        if (!await Service.GetSnipeSet(ctx.Guild.Id))
        {
            await ReplyErrorLocalizedAsync("snipe_not_enabled", await _guildSettings.GetPrefix(ctx.Guild)).ConfigureAwait(false);
            return;
        }

        var msg = (await Service.GetSnipes(ctx.Guild.Id).ConfigureAwait(false))
                         .Find(x => x.ChannelId == ctx.Channel.Id && x.UserId == user1.Id && x.Edited == 0);
        if (msg is null)
        {
            await ctx.Channel.SendErrorAsync("There is nothing to snipe for this user!").ConfigureAwait(false);
            return;
        }
        var user = await ctx.Channel.GetUserAsync(msg.UserId).ConfigureAwait(false) ?? await _client.Rest.GetUserAsync(msg.UserId).ConfigureAwait(false);
        var em = new EmbedBuilder
        {
            Author = new EmbedAuthorBuilder { IconUrl = user.GetAvatarUrl(), Name = $"{user} said:" },

            Description = msg.Message,
            Footer = new EmbedFooterBuilder
            {
                IconUrl = ctx.User.GetAvatarUrl(),
                Text =
                    GetText("snipe_request", ctx.User.ToString(), (DateTime.UtcNow - msg.DateAdded).Humanize())
            },
            Color = Mewdeko.OkColor
        };
        await ctx.Channel.SendMessageAsync(embed: em.Build(), 
            components: _config.Data.ShowInviteButton ? new ComponentBuilder()
                                                        .WithButton(style: ButtonStyle.Link, 
                                                            url: "https://discord.com/oauth2/authorize?client_id=701019662795800606&permissions=8&response_type=code&redirect_uri=https%3A%2F%2Fmewdeko.tech&scope=bot%20applications.commands", 
                                                            label: "Invite Me!", 
                                                            emote: "<a:HaneMeow:968564817784877066>".ToIEmote()).Build() : null).ConfigureAwait(false);
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild), Priority(2)]
    public async Task VCheck([Remainder] string? url = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            await ctx.Channel.SendErrorAsync("You didn't specify a url").ConfigureAwait(false);
        }
        else
        {
            var result = await UtilityService.UrlChecker(url).ConfigureAwait(false);
            var eb = new EmbedBuilder();
            eb.WithOkColor();
            eb.WithDescription(result.Permalink);
            eb.AddField("Virus Positives", result.Positives, true);
            eb.AddField("Number of scans", result.Total, true);
            await ctx.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild), Priority(2)]
    public async Task Snipe(ITextChannel chan)
    {
        if (!await Service.GetSnipeSet(ctx.Guild.Id))
        {
            await ReplyErrorLocalizedAsync("snipe_not_enabled", await _guildSettings.GetPrefix(ctx.Guild)).ConfigureAwait(false);
            return;
        }

        var msg = (await Service.GetSnipes(ctx.Guild.Id).ConfigureAwait(false)).Where(x => x.Edited == 0)
                                                         .LastOrDefault(x => x.ChannelId == chan.Id);
        if (msg == null)
        {
            await ReplyErrorLocalizedAsync("no_snipes").ConfigureAwait(false);
            return;
        }

        var user = await ctx.Channel.GetUserAsync(msg.UserId).ConfigureAwait(false) ?? await _client.Rest.GetUserAsync(msg.UserId).ConfigureAwait(false);

        var em = new EmbedBuilder
        {
            Author = new EmbedAuthorBuilder { IconUrl = user.GetAvatarUrl(), Name = $"{user} said:" },
            Description = msg.Message,
            Footer = new EmbedFooterBuilder
            {
                IconUrl = ctx.User.GetAvatarUrl(),
                Text =
                    GetText("snipe_request", ctx.User.ToString(), (DateTime.UtcNow - msg.DateAdded).Humanize())
            },
            Color = Mewdeko.OkColor
        };
        await ctx.Channel.SendMessageAsync(embed: em.Build(), 
            components: _config.Data.ShowInviteButton ? new ComponentBuilder()
                                                        .WithButton(style: ButtonStyle.Link, 
                                                            url: "https://discord.com/oauth2/authorize?client_id=701019662795800606&permissions=8&response_type=code&redirect_uri=https%3A%2F%2Fmewdeko.tech&scope=bot%20applications.commands", 
                                                            label: "Invite Me!", 
                                                            emote: "<a:HaneMeow:968564817784877066>".ToIEmote()).Build() : null).ConfigureAwait(false);
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild), Priority(2)]
    public async Task Snipe(ITextChannel chan, IUser user1)
    {
        if (!await Service.GetSnipeSet(ctx.Guild.Id))
        {
            await ReplyErrorLocalizedAsync("snipe_not_enabled", await _guildSettings.GetPrefix(ctx.Guild)).ConfigureAwait(false);
            return;
        }

        var msg = (await Service.GetSnipes(ctx.Guild.Id).ConfigureAwait(false)).Where(x => x.Edited == 0)
                                                         .LastOrDefault(x => x.ChannelId == chan.Id && x.UserId == user1.Id);
        {
            if (msg == null)
            {
                await ctx.Channel.SendErrorAsync("There's nothing to snipe for that channel and user!").ConfigureAwait(false);
                return;
            }

            var user = await ctx.Channel.GetUserAsync(msg.UserId).ConfigureAwait(false) ?? await _client.Rest.GetUserAsync(msg.UserId).ConfigureAwait(false);

            var em = new EmbedBuilder
            {
                Author = new EmbedAuthorBuilder { IconUrl = user.GetAvatarUrl(), Name = $"{user} said:" },
                Description = msg.Message,
                Footer = new EmbedFooterBuilder
                {
                    IconUrl = ctx.User.GetAvatarUrl(),
                    Text =
                        GetText("snipe_request", ctx.User.ToString(), (DateTime.UtcNow - msg.DateAdded).Humanize())
                },
                Color = Mewdeko.OkColor
            };
            await ctx.Channel.SendMessageAsync(embed: em.Build(), 
                components: _config.Data.ShowInviteButton ? new ComponentBuilder()
                                                            .WithButton(style: ButtonStyle.Link, 
                                                                url: "https://discord.com/oauth2/authorize?client_id=701019662795800606&permissions=8&response_type=code&redirect_uri=https%3A%2F%2Fmewdeko.tech&scope=bot%20applications.commands", 
                                                                label: "Invite Me!", 
                                                                emote: "<a:HaneMeow:968564817784877066>".ToIEmote()).Build() : null).ConfigureAwait(false);
        }
    }

    [Cmd, Aliases, UserPerm(GuildPermission.Administrator),
     RequireContext(ContextType.Guild)]
    public async Task PreviewLinks(string yesnt)
    {
        await Service.PreviewLinks(ctx.Guild, yesnt[..1].ToLower()).ConfigureAwait(false);
        switch (await Service.GetPLinks(ctx.Guild.Id))
        {
            case 1:
                await ctx.Channel.SendConfirmAsync("Link previews are now enabled!").ConfigureAwait(false);
                break;
            case 0:
                await ctx.Channel.SendConfirmAsync("Link Previews are now disabled!").ConfigureAwait(false);
                break;
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public async Task EditSnipe()
    {
        if (!await Service.GetSnipeSet(ctx.Guild.Id))
        {
            await ReplyErrorLocalizedAsync("snipe_not_enabled", await _guildSettings.GetPrefix(ctx.Guild)).ConfigureAwait(false);
            return;
        }

        {
            var msg = (await Service.GetSnipes(ctx.Guild.Id).ConfigureAwait(false))
                      .Where(x => x.Edited == 1)
                      .LastOrDefault(x => x.ChannelId == ctx.Channel.Id);
            if (msg == null)
            {
                await ctx.Channel.SendErrorAsync("There's nothing to snipe!").ConfigureAwait(false);
                return;
            }

            var user = await ctx.Channel.GetUserAsync(msg.UserId).ConfigureAwait(false) ?? await _client.Rest.GetUserAsync(msg.UserId).ConfigureAwait(false);

            var em = new EmbedBuilder
            {
                Author = new EmbedAuthorBuilder
                {
                    IconUrl = user.GetAvatarUrl(),
                    Name = $"{user} originally said:"
                },
                Description = msg.Message,
                Footer = new EmbedFooterBuilder
                {
                    IconUrl = ctx.User.GetAvatarUrl(),
                    Text =
                        GetText("snipe_request", ctx.User.ToString(), (DateTime.UtcNow - msg.DateAdded).Humanize())
                },
                Color = Mewdeko.OkColor
            };
            await ctx.Channel.SendMessageAsync(embed: em.Build(), 
                components: _config.Data.ShowInviteButton ? new ComponentBuilder()
                                                            .WithButton(style: ButtonStyle.Link, 
                                                                url: "https://discord.com/oauth2/authorize?client_id=701019662795800606&permissions=8&response_type=code&redirect_uri=https%3A%2F%2Fmewdeko.tech&scope=bot%20applications.commands", 
                                                                label: "Invite Me!", 
                                                                emote: "<a:HaneMeow:968564817784877066>".ToIEmote()).Build() : null).ConfigureAwait(false);
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild), Priority(1)]
    public async Task EditSnipe(IUser user1)
    {
        if (!await Service.GetSnipeSet(ctx.Guild.Id))
        {
            await ReplyErrorLocalizedAsync("snipe_not_enabled", await _guildSettings.GetPrefix(ctx.Guild)).ConfigureAwait(false);
            return;
        }

        {
            var msg = (await Service.GetSnipes(ctx.Guild.Id).ConfigureAwait(false))
                      .Where(x => x.Edited == 1)
                      .LastOrDefault(x => x.ChannelId == ctx.Channel.Id && x.UserId == user1.Id);
            if (msg == null)
            {
                await ReplyErrorLocalizedAsync("no_snipes").ConfigureAwait(false);
                return;
            }

            var user = await ctx.Channel.GetUserAsync(msg.UserId).ConfigureAwait(false) ?? await _client.Rest.GetUserAsync(msg.UserId).ConfigureAwait(false);

            var em = new EmbedBuilder
            {
                Author = new EmbedAuthorBuilder
                {
                    IconUrl = user.GetAvatarUrl(),
                    Name = $"{user} originally said:"
                },
                Description = msg.Message,
                Footer = new EmbedFooterBuilder
                {
                    IconUrl = ctx.User.GetAvatarUrl(),
                    Text =
                        GetText("snipe_request", ctx.User.ToString(), (DateTime.UtcNow - msg.DateAdded).Humanize())
                },
                Color = Mewdeko.OkColor
            };
            await ctx.Channel.SendMessageAsync(embed: em.Build(), 
                components: _config.Data.ShowInviteButton ? new ComponentBuilder()
                                                            .WithButton(style: ButtonStyle.Link, 
                                                                url: "https://discord.com/oauth2/authorize?client_id=701019662795800606&permissions=8&response_type=code&redirect_uri=https%3A%2F%2Fmewdeko.tech&scope=bot%20applications.commands", 
                                                                label: "Invite Me!", 
                                                                emote: "<a:HaneMeow:968564817784877066>".ToIEmote()).Build() : null).ConfigureAwait(false);
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild), Priority(1)]
    public async Task EditSnipe(ITextChannel chan)
    {
        if (!await Service.GetSnipeSet(ctx.Guild.Id))
        {
            await ReplyErrorLocalizedAsync("snipe_not_enabled", await _guildSettings.GetPrefix(ctx.Guild)).ConfigureAwait(false);
            return;
        }

        {
            var msg = (await Service.GetSnipes(ctx.Guild.Id).ConfigureAwait(false))
                      .Where(x => x.Edited == 1)
                      .LastOrDefault(x => x.ChannelId == chan.Id);
            if (msg == null)
            {
                await ReplyErrorLocalizedAsync("no_snipes").ConfigureAwait(false);
                return;
            }

            var user = await ctx.Channel.GetUserAsync(msg.UserId).ConfigureAwait(false) ?? await _client.Rest.GetUserAsync(msg.UserId).ConfigureAwait(false);

            var em = new EmbedBuilder
            {
                Author = new EmbedAuthorBuilder
                {
                    IconUrl = user.GetAvatarUrl(),
                    Name = $"{user} originally said:"
                },
                Description = msg.Message,
                Footer = new EmbedFooterBuilder
                {
                    IconUrl = ctx.User.GetAvatarUrl(),
                    Text =
                        GetText("snipe_request", ctx.User.ToString(), (DateTime.UtcNow - msg.DateAdded).Humanize())
                },
                Color = Mewdeko.OkColor
            };
            await ctx.Channel.SendMessageAsync(embed: em.Build(), 
                components: _config.Data.ShowInviteButton ? new ComponentBuilder()
                                                            .WithButton(style: ButtonStyle.Link, 
                                                                url: "https://discord.com/oauth2/authorize?client_id=701019662795800606&permissions=8&response_type=code&redirect_uri=https%3A%2F%2Fmewdeko.tech&scope=bot%20applications.commands", 
                                                                label: "Invite Me!", 
                                                                emote: "<a:HaneMeow:968564817784877066>".ToIEmote()).Build() : null).ConfigureAwait(false);
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild), Priority(1)]
    public async Task EditSnipe(ITextChannel chan, IUser user1)
    {
        if (!await Service.GetSnipeSet(ctx.Guild.Id))
        {
            await ReplyErrorLocalizedAsync("snipe_not_enabled", await _guildSettings.GetPrefix(ctx.Guild)).ConfigureAwait(false);
            return;
        }

        {
            var msg = (await Service.GetSnipes(ctx.Guild.Id).ConfigureAwait(false))
                      .Where(x => x.Edited == 1)
                      .LastOrDefault(x => x.ChannelId == chan.Id && x.UserId == user1.Id);
            if (msg == null)
            {
                await ReplyErrorLocalizedAsync("no_snipes").ConfigureAwait(false);
                return;
            }

            var user = await ctx.Channel.GetUserAsync(msg.UserId).ConfigureAwait(false) ?? await _client.Rest.GetUserAsync(msg.UserId).ConfigureAwait(false);

            var em = new EmbedBuilder
            {
                Author = new EmbedAuthorBuilder
                {
                    IconUrl = user.GetAvatarUrl(),
                    Name = $"{user} originally said:"
                },
                Description = msg.Message,
                Footer = new EmbedFooterBuilder
                {
                    IconUrl = ctx.User.GetAvatarUrl(),
                    Text =
                        GetText("snipe_request", ctx.User.ToString(), (DateTime.UtcNow - msg.DateAdded).Humanize())
                },
                Color = Mewdeko.OkColor
            };
            await ctx.Channel.SendMessageAsync(embed: em.Build(), 
                components: _config.Data.ShowInviteButton ? new ComponentBuilder()
                                                            .WithButton(style: ButtonStyle.Link, 
                                                                url: "https://discord.com/oauth2/authorize?client_id=701019662795800606&permissions=8&response_type=code&redirect_uri=https%3A%2F%2Fmewdeko.tech&scope=bot%20applications.commands", 
                                                                label: "Invite Me!", 
                                                                emote: "<a:HaneMeow:968564817784877066>".ToIEmote()).Build() : null).ConfigureAwait(false);
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public async Task WhosPlaying([Remainder] string? game)
    {
        game = game?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(game))
            return;

        if (ctx.Guild is not SocketGuild socketGuild)
        {
            Log.Warning("Can't cast guild to socket guild.");
            return;
        }

        var rng = new MewdekoRandom();
        var arr = await Task.Run(() => socketGuild.Users
                                                  .Where(x => x.Activities.Any())
                                                  .Where(u =>  u.Activities.FirstOrDefault().Name.ToUpperInvariant().Contains(game))
                                                  .OrderBy(_ => rng.Next())
                                                  .ToArray()).ConfigureAwait(false);

        var i = 0;
        if (arr.Length == 0)
        {
            await ReplyErrorLocalizedAsync("nobody_playing_game").ConfigureAwait(false);
        }
        else
        {
            
            var paginator = new LazyPaginatorBuilder()
                            .AddUser(ctx.User)
                            .WithPageFactory(PageFactory)
                            .WithFooter(PaginatorFooter.PageNumber | PaginatorFooter.Users)
                            .WithMaxPageIndex(arr.Length / 20)
                            .WithDefaultEmotes()
                            .WithActionOnCancellation(ActionOnStop.DeleteMessage)
                            .Build();
            
            await _interactivity.SendPaginatorAsync(paginator, Context.Channel,
                TimeSpan.FromMinutes(60)).ConfigureAwait(false);
            
            async Task<PageBuilder> PageFactory(int page)
            {
                await Task.CompletedTask.ConfigureAwait(false);
                var pagebuilder = new PageBuilder().WithOkColor()
                                            .WithDescription(string.Join("\n", arr.Skip(page * 20).Take(20).Select(x => $"{(i++)+1}. {x.Username}#{x.Discriminator} `{x.Id}`: `{(x.Activities.FirstOrDefault() is CustomStatusGame cs ? cs.State : x.Activities.FirstOrDefault().Name)}`")));
                return pagebuilder;
            }
                
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public async Task Vote() =>
        await ctx.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                                                       .WithDescription(
                                                           "Vote here for Dino!\n[Vote Link](https://top.gg/bot/701019662795800606)\nMake sure to join the support server! \n[Link](https://discord.gg/dinogaming)")).ConfigureAwait(false);

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public async Task InRole([Remainder] IRole role)
    {
        await Context.Channel.TriggerTypingAsync().ConfigureAwait(false);
        await _tracker.EnsureUsersDownloadedAsync(ctx.Guild).ConfigureAwait(false);

        var users = await ctx.Guild.GetUsersAsync().ConfigureAwait(false);
        var roleUsers = users
            .Where(u => u.RoleIds.Contains(role.Id))
            .ToArray();

        var paginator = new LazyPaginatorBuilder()
            .AddUser(ctx.User)
            .WithPageFactory(PageFactory)
            .WithFooter(PaginatorFooter.PageNumber | PaginatorFooter.Users)
            .WithMaxPageIndex(roleUsers.Length / 20)
            .WithDefaultEmotes()
            .WithActionOnCancellation(ActionOnStop.DeleteMessage)
            .Build();

        await _interactivity.SendPaginatorAsync(paginator, Context.Channel,
            TimeSpan.FromMinutes(60)).ConfigureAwait(false);

        async Task<PageBuilder> PageFactory(int page)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return new PageBuilder().WithOkColor()
                                                    .WithTitle(
                                                        $"{Format.Bold(GetText("inrole_list", Format.Bold(role.Name)))} - {roleUsers.Length}")
                                                    .WithDescription(string.Join("\n",
                                                        roleUsers.Skip(page * 20).Take(20)
                                                                 .Select(x => $"{x} `{x.Id}`"))).AddField("User Stats",
                                                        $"<:online:914548119730024448> {roleUsers.Count(x => x.Status == UserStatus.Online)}\n<:dnd:914548634178187294> {roleUsers.Count(x => x.Status == UserStatus.DoNotDisturb)}\n<:idle:914548262424412172> {roleUsers.Count(x => x.Status == UserStatus.Idle)}\n<:offline:914548368037003355> {roleUsers.Count(x => x.Status == UserStatus.Offline)}");
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public async Task InRoles(IRole role, IRole role2)
    {
        await Context.Channel.TriggerTypingAsync().ConfigureAwait(false);
        await _tracker.EnsureUsersDownloadedAsync(ctx.Guild).ConfigureAwait(false);
        var users = await ctx.Guild.GetUsersAsync().ConfigureAwait(false);
        var roleUsers = users
            .Where(u => u.RoleIds.Contains(role.Id) && u.RoleIds.Contains(role2.Id))
            .Select(u => u.ToString())
            .ToArray();

        var paginator = new LazyPaginatorBuilder()
            .AddUser(ctx.User)
            .WithPageFactory(PageFactory)
            .WithFooter(PaginatorFooter.PageNumber | PaginatorFooter.Users)
            .WithMaxPageIndex(roleUsers.Length / 20)
            .WithDefaultEmotes()
            .WithActionOnCancellation(ActionOnStop.DeleteMessage)
            .Build();

        await _interactivity.SendPaginatorAsync(paginator, Context.Channel,
            TimeSpan.FromMinutes(60)).ConfigureAwait(false);

        async Task<PageBuilder> PageFactory(int page)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return new PageBuilder().WithOkColor()
                                                    .WithTitle(Format.Bold(
                                                        $"Users in the roles: {role.Name} | {role2.Name} - {roleUsers.Length}"))
                                                    .WithDescription(string.Join("\n",
                                                        roleUsers.Skip(page * 20).Take(20)));
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public async Task UserId([Remainder] IGuildUser? target = null)
    {
        var usr = target ?? ctx.User;
        await ReplyConfirmLocalizedAsync("userid", "🆔", Format.Bold(usr.ToString()),
            Format.Code(usr.Id.ToString())).ConfigureAwait(false);
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public async Task RoleId([Remainder] IRole role) =>
        await ReplyConfirmLocalizedAsync("roleid", "🆔", Format.Bold(role.ToString()),
            Format.Code(role.Id.ToString())).ConfigureAwait(false);

    [Cmd, Aliases]
    public async Task ChannelId() =>
        await ReplyConfirmLocalizedAsync("channelid", "🆔", Format.Code(ctx.Channel.Id.ToString()))
            .ConfigureAwait(false);

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public async Task ServerId() =>
        await ReplyConfirmLocalizedAsync("serverid", "🆔", Format.Code(ctx.Guild.Id.ToString()))
            .ConfigureAwait(false);

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public async Task Roles(IGuildUser? target, int page = 1)
    {
        var channel = (ITextChannel)ctx.Channel;
        var guild = channel.Guild;

        const int rolesPerPage = 20;

        if (page is < 1 or > 100)
            return;

        if (target != null)
        {
            var roles = target.GetRoles().Except(new[] { guild.EveryoneRole }).OrderBy(r => -r.Position)
                .Skip((page - 1) * rolesPerPage).Take(rolesPerPage).ToArray();
            if (roles.Length == 0)
            {
                await ReplyErrorLocalizedAsync("no_roles_on_page").ConfigureAwait(false);
            }
            else
            {
                await channel.SendConfirmAsync(GetText("roles_page", page, Format.Bold(target.ToString())),
                                $"\n• {string.Join("\n• ", (IEnumerable<IRole>)roles)}").ConfigureAwait(false);
            }
        }
        else
        {
            var roles = guild.Roles.Except(new[] { guild.EveryoneRole }).OrderBy(r => -r.Position)
                .Skip((page - 1) * rolesPerPage).Take(rolesPerPage).ToArray();
            if (roles.Length == 0)
            {
                await ReplyErrorLocalizedAsync("no_roles_on_page").ConfigureAwait(false);
            }
            else
            {
                await channel.SendConfirmAsync(GetText("roles_all_page", page),
                                             $"\n• {string.Join("\n• ", (IEnumerable<IRole>)roles).SanitizeMentions()}")
                                .ConfigureAwait(false);
            }
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public Task Roles(int page = 1) => Roles(null, page);

    [Cmd, Aliases, RequireContext(ContextType.Guild)]
    public async Task ChannelTopic([Remainder] ITextChannel? channel = null)
    {
        channel ??= (ITextChannel)ctx.Channel;

        var topic = channel.Topic;
        if (string.IsNullOrWhiteSpace(topic))
            await ReplyErrorLocalizedAsync("no_topic_set").ConfigureAwait(false);
        else
            await ctx.Channel.SendConfirmAsync(GetText("channel_topic"), topic).ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task Stats()
    {
        var user = await _client.Rest.GetUserAsync(327410729466724352).ConfigureAwait(false);
        await ctx.Channel.EmbedAsync(
                     new EmbedBuilder().WithOkColor()
                                       .WithAuthor(eab => eab.WithName($"{_client.CurrentUser.Username} - 3.7.2 - APIv9")
                                                             .WithUrl("https://discord.gg/dinogaming")
                                                             .WithIconUrl(_client.CurrentUser.GetAvatarUrl()))
                                       .AddField(efb =>
                                           efb.WithName(GetText("author")).WithValue($"{user.Username}#{user.Discriminator}")
                                              .WithIsInline(false))
                                       .AddField(efb => efb.WithName("Library").WithValue(_stats.Library).WithIsInline(false))
                                       .AddField(efb =>
                                           efb.WithName(GetText("shard")).WithValue($"#{_client.ShardId} / {_creds.TotalShards}")
                                              .WithIsInline(false))
                                       .AddField(efb => efb.WithName(GetText("memory")).WithValue($"{_stats.Heap} MB").WithIsInline(false))
                                       .AddField(efb =>
                                           efb.WithName(GetText("uptime")).WithValue(_stats.GetUptimeString("\n")).WithIsInline(false))
                                       .AddField(efb => efb.WithName("Servers").WithValue($"{_coordinator.GetGuildCount()} Servers").WithIsInline(false)))
                 .ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task Showemojis([Remainder] string _)
    {
        var tags = ctx.Message.Tags.Where(t => t.Type == TagType.Emoji).Select(t => (Emote)t.Value);

        var result = string.Join("\n", tags.Select(m => GetText("showemojis", m, m.Url)));

        if (string.IsNullOrWhiteSpace(result))
            await ReplyErrorLocalizedAsync("showemojis_none").ConfigureAwait(false);
        else
            await ctx.Channel.SendMessageAsync(result.TrimTo(2000)).ConfigureAwait(false);
    }

    [Cmd, Ratelimit(30)]
    public async Task Ping()
    {
        await _sem.WaitAsync(5000).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var msg = await ctx.Channel.SendMessageAsync("🏓").ConfigureAwait(false);
            sw.Stop();
            msg.DeleteAfter(0);

            await ctx.Channel
                .SendConfirmAsync(
                    $"Bot Ping {(int)sw.Elapsed.TotalMilliseconds}ms\nBot Latency {((DiscordSocketClient)ctx.Client).Latency}ms")
                .ConfigureAwait(false);
        }
        finally
        {
            _sem.Release();
        }
    }

    [Cmd, Aliases]
    public async Task Roll([Remainder] string roll)
    {
        RollResult result;
        try
        {
            result = RollCommandService.ParseRoll(roll);
        }
        catch (ArgumentException ex)
        {
            await ReplyErrorLocalizedAsync("roll_fail_new_dm", GetText(ex.Message)).ConfigureAwait(false);
            return;
        }

        async Task<PageBuilder> PageFactory(int page)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return new PageBuilder()
                    .WithOkColor()
                    .WithFields(result.Results.Skip(page * 10)
                                        .Take(10)
                                        .Select(x => new EmbedFieldBuilder()
                                            .WithName(x.Key.ToString())
                                            .WithValue(string.Join(',', x.Value))).ToArray())
                    .WithDescription(result.InacurateTotal
                                        // hide possible int rollover errors
                                        ? GetText("roll_fail_too_large")
                                        : result.ToString());
        }

        var paginator = new LazyPaginatorBuilder()
            .AddUser(ctx.User)
            .WithPageFactory(PageFactory)
            .WithFooter(PaginatorFooter.PageNumber | PaginatorFooter.Users)
            .WithMaxPageIndex(result.Results.Count / 10)
            .WithDefaultCanceledPage()
            .WithDefaultEmotes()
            .WithActionOnCancellation(ActionOnStop.DeleteMessage)
            .Build();

        await _interactivity.SendPaginatorAsync(paginator, ctx.Channel, TimeSpan.FromMinutes(60)).ConfigureAwait(false);
    }
    [Cmd, Aliases]
    public async Task OwoIfy([Remainder] string input)
        => await ctx.Channel.SendMessageAsync(OwoServices.OwoIfy(input).SanitizeMentions(true)).ConfigureAwait(false);
}
