using Discord.Commands;
using Discord.Net;
using Discord.Rest;
using Fergun.Interactive;
using Fergun.Interactive.Pagination;
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Common.DiscordImplementations;
using Mewdeko.Modules.OwnerOnly.Services;
using Mewdeko.Services.Settings;
using Mewdeko.Services.strings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;

namespace Mewdeko.Modules.OwnerOnly;
[OwnerOnly]
public class OwnerOnly : MewdekoModuleBase<OwnerOnlyService>
{
    public enum SettableUserStatus
    {
        Online,
        Invisible,
        Idle,
        Dnd
    }

    private readonly Mewdeko _bot;
    private readonly DiscordSocketClient _client;
    private readonly DbService _db;
    private readonly ICoordinator _coord;
    private readonly IEnumerable<IConfigService> _settingServices;
    private readonly IBotStrings _strings;
    private readonly InteractiveService _interactivity;
    private readonly IDataCache _cache;
    private readonly CommandService _commandService;
    private readonly IServiceProvider _services;
    private readonly IBotCredentials _credentials;
    private readonly GuildSettingsService _guildSettings;
    private readonly CommandHandler _commandHandler;

    public OwnerOnly(
        DiscordSocketClient client,
        Mewdeko bot,
        IBotStrings strings,
        InteractiveService serv,
        ICoordinator coord,
        IEnumerable<IConfigService> settingServices,
        DbService db,
        IDataCache cache,
        CommandService commandService,
        IServiceProvider services,
        IBotCredentials credentials,
        GuildSettingsService guildSettings,
        CommandHandler commandHandler)
    {
        _interactivity = serv;
        _client = client;
        _bot = bot;
        _strings = strings;
        _coord = coord;
        _settingServices = settingServices;
        _db = db;
        _cache = cache;
        _commandService = commandService;
        _services = services;
        _credentials = credentials;
        _guildSettings = guildSettings;
        _commandHandler = commandHandler;
    }

    [Cmd, Aliases]
    public async Task Sudo(IGuildUser user, [Remainder] string args)
    {
        var msg = new MewdekoUserMessage
        {
            Content = $"{await _guildSettings.GetPrefix(ctx.Guild)}{args}",
            Author = user,
            Channel = ctx.Channel
        };
        _commandHandler.AddCommandToParseQueue(msg);
        _ = Task.Run(async () => await _commandHandler.ExecuteCommandsInChannelAsync(ctx.Channel.Id)).ConfigureAwait(false);
    }
    [Cmd, Aliases]
    public async Task RedisExec([Remainder] string command)
    {
        var result = await _cache.ExecuteRedisCommand(command).ConfigureAwait(false);
        var eb = new EmbedBuilder().WithOkColor().WithTitle(result.Type.ToString()).WithDescription(result.ToString());
        await ctx.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
    }
    [Cmd, Aliases]
    public async Task SqlExec([Remainder] string sql)
    {
        if (!await PromptUserConfirmAsync("Are you sure you want to execute this??", ctx.User.Id).ConfigureAwait(false))
            return;
        await using var uow = _db.GetDbContext();
        var affected = await uow.Database.ExecuteSqlRawAsync(sql).ConfigureAwait(false);
        await ctx.Channel.SendErrorAsync($"Affected {affected} rows.").ConfigureAwait(false);
    }
    [Cmd, Aliases]
    public async Task ListServers(int page = 1)
    {
        page--;

        if (page < 0)
            return;

        var guilds = await Task.Run(() => _client.Guilds.OrderBy(g => g.Name).Skip(page * 15).Take(15))
            .ConfigureAwait(false);

        if (!guilds.Any())
        {
            await ReplyErrorLocalizedAsync("listservers_none").ConfigureAwait(false);
            return;
        }

        await ctx.Channel.EmbedAsync(guilds.Aggregate(new EmbedBuilder().WithOkColor(),
                (embed, g) => embed.AddField(efb => efb.WithName(g.Name)
                    .WithValue(
                        GetText("listservers", g.Id, g.MemberCount,
                            g.OwnerId))
                    .WithIsInline(false))))
            .ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task Config(string? name = null, string? prop = null, [Remainder] string? value = null)
    {
        var configNames = _settingServices.Select(x => x.Name);

        // if name is not provided, print available configs
        name = name?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name))
        {
            var embed = new EmbedBuilder()
                .WithOkColor()
                .WithTitle(GetText("config_list"))
                .WithDescription(string.Join("\n", configNames));

            await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
            return;
        }

        var setting = _settingServices.FirstOrDefault(x =>
            x.Name.StartsWith(name, StringComparison.InvariantCultureIgnoreCase));

        // if config name is not found, print error and the list of configs
        if (setting is null)
        {
            var embed = new EmbedBuilder()
                .WithErrorColor()
                .WithDescription(GetText("config_not_found", Format.Code(name)))
                .AddField(GetText("config_list"), string.Join("\n", configNames));

            await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
            return;
        }

        name = setting.Name;

        // if prop is not sent, then print the list of all props and values in that config
        prop = prop?.ToLowerInvariant();
        var propNames = setting.GetSettableProps();
        if (string.IsNullOrWhiteSpace(prop))
        {
            var propStrings = GetPropsAndValuesString(setting, propNames);
            var embed = new EmbedBuilder()
                .WithOkColor()
                .WithTitle($"⚙️ {setting.Name}")
                .WithDescription(propStrings);

            await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
            return;
        }
        // if the prop is invalid -> print error and list of 

        var exists = propNames.Any(x => x == prop);

        if (!exists)
        {
            var propStrings = GetPropsAndValuesString(setting, propNames);
            var propErrorEmbed = new EmbedBuilder()
                .WithErrorColor()
                .WithDescription(GetText("config_prop_not_found", Format.Code(prop), Format.Code(name)))
                .AddField($"⚙️ {setting.Name}", propStrings);

            await ctx.Channel.EmbedAsync(propErrorEmbed).ConfigureAwait(false);
            return;
        }

        // if prop is sent, but value is not, then we have to check
        // if prop is valid -> 
        if (string.IsNullOrWhiteSpace(value))
        {
            value = setting.GetSetting(prop);
            if (prop != "currency.sign") Format.Code(Format.Sanitize(value.TrimTo(1000)), "json");

            if (string.IsNullOrWhiteSpace(value))
                value = "-";

            var embed = new EmbedBuilder()
                .WithOkColor()
                .AddField("Config", Format.Code(setting.Name), true)
                .AddField("Prop", Format.Code(prop), true)
                .AddField("Value", value);

            var comment = setting.GetComment(prop);
            if (!string.IsNullOrWhiteSpace(comment))
                embed.AddField("Comment", comment);

            await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
            return;
        }

        var success = setting.SetSetting(prop, value);

        if (!success)
        {
            await ReplyErrorLocalizedAsync("config_edit_fail", Format.Code(prop), Format.Code(value)).ConfigureAwait(false);
            return;
        }

        await ctx.OkAsync().ConfigureAwait(false);
    }

    private static string GetPropsAndValuesString(IConfigService config, IEnumerable<string> names)
    {
        var propValues = names.Select(pr =>
        {
            var val = config.GetSetting(pr);
            if (pr != "currency.sign")
                val = val.TrimTo(28);
            return val?.Replace("\n", "") ?? "-";
        });

        var strings = names.Zip(propValues, (name, value) =>
            $"{name,-25} = {value}\n");

        return Format.Code(string.Concat(strings), "hs");
    }

    [Cmd, Aliases]
    public async Task RotatePlaying()
    {
        if (Service.ToggleRotatePlaying())
            await ReplyConfirmLocalizedAsync("ropl_enabled").ConfigureAwait(false);
        else
            await ReplyConfirmLocalizedAsync("ropl_disabled").ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task AddPlaying(ActivityType t, [Remainder] string status)
    {
        await Service.AddPlaying(t, status).ConfigureAwait(false);

        await ReplyConfirmLocalizedAsync("ropl_added").ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task ListPlaying()
    {
        var statuses = Service.GetRotatingStatuses();

        if (statuses.Count == 0)
        {
            await ReplyErrorLocalizedAsync("ropl_not_set").ConfigureAwait(false);
        }
        else
        {
            var i = 1;
            await ReplyConfirmLocalizedAsync("ropl_list",
                    string.Join("\n\t", statuses.Select(rs => $"`{i++}.` *{rs.Type}* {rs.Status}")))
                .ConfigureAwait(false);
        }
    }

    [Cmd, Aliases]
    public async Task DefPrefix([Remainder] string? prefix = null)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            await ReplyConfirmLocalizedAsync("defprefix_current", await _guildSettings.GetPrefix()).ConfigureAwait(false);
            return;
        }

        var oldPrefix = await _guildSettings.GetPrefix();
        var newPrefix = Service.SetDefaultPrefix(prefix);

        await ReplyConfirmLocalizedAsync("defprefix_new", Format.Code(oldPrefix), Format.Code(newPrefix))
            .ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task RemovePlaying(int index)
    {
        index--;

        var msg = await Service.RemovePlayingAsync(index).ConfigureAwait(false);

        if (msg == null)
            return;

        await ReplyConfirmLocalizedAsync("reprm", msg).ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task LanguageSetDefault(string name)
    {
        try
        {
            CultureInfo? ci;
            if (string.Equals(name.Trim(), "default", StringComparison.InvariantCultureIgnoreCase))
            {
                Localization.ResetDefaultCulture();
                ci = Localization.DefaultCultureInfo;
            }
            else
            {
                ci = new CultureInfo(name);
                Localization.SetDefaultCulture(ci);
            }

            await ReplyConfirmLocalizedAsync("lang_set_bot", Format.Bold(ci.ToString()),
                Format.Bold(ci.NativeName)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await ReplyErrorLocalizedAsync("lang_set_fail").ConfigureAwait(false);
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild),
     UserPerm(GuildPermission.Administrator), OwnerOnly]
    public async Task StartupCommandAdd([Remainder] string cmdText)
    {
        if (cmdText.StartsWith($"{await _guildSettings.GetPrefix(ctx.Guild)}die", StringComparison.InvariantCulture) || cmdText.StartsWith($"{await _guildSettings.GetPrefix(ctx.Guild)}restart", StringComparison.InvariantCulture))
            return;

        var guser = (IGuildUser)ctx.User;
        var cmd = new AutoCommand
        {
            CommandText = cmdText,
            ChannelId = ctx.Channel.Id,
            ChannelName = ctx.Channel.Name,
            GuildId = ctx.Guild?.Id,
            GuildName = ctx.Guild?.Name,
            VoiceChannelId = guser.VoiceChannel?.Id,
            VoiceChannelName = guser.VoiceChannel?.Name,
            Interval = 0
        };
        Service.AddNewAutoCommand(cmd);

        await ctx.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
            .WithTitle(GetText("scadd"))
            .AddField(efb => efb.WithName(GetText("server"))
                .WithValue(cmd.GuildId == null ? "-" : $"{cmd.GuildName}/{cmd.GuildId}").WithIsInline(true))
            .AddField(efb => efb.WithName(GetText("channel"))
                .WithValue($"{cmd.ChannelName}/{cmd.ChannelId}").WithIsInline(true))
            .AddField(efb => efb.WithName(GetText("command_text"))
                .WithValue(cmdText).WithIsInline(false))).ConfigureAwait(false);
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild),
     UserPerm(GuildPermission.Administrator), OwnerOnly]
    public async Task AutoCommandAdd(int interval, [Remainder] string cmdText)
    {
        if (cmdText.StartsWith($"{await _guildSettings.GetPrefix(ctx.Guild)}die", StringComparison.InvariantCulture))
            return;
        var command = _commandService.Search(cmdText.Replace(await _guildSettings.GetPrefix(ctx.Guild), "").Split(" ")[0]);
        if (!command.IsSuccess)
            return;
        foreach (var i in command.Commands)
        {
            if (!(await i.CheckPreconditionsAsync(ctx, _services).ConfigureAwait(false)).IsSuccess)
                return;
        }
        var count = Service.GetAutoCommands().Where(x => x.GuildId == ctx.Guild.Id);

        if (count.Count() == 15)
            return;
        if (interval < 5)
            return;

        var guser = (IGuildUser)ctx.User;
        var cmd = new AutoCommand
        {
            CommandText = cmdText,
            ChannelId = ctx.Channel.Id,
            ChannelName = ctx.Channel.Name,
            GuildId = ctx.Guild?.Id,
            GuildName = ctx.Guild?.Name,
            VoiceChannelId = guser.VoiceChannel?.Id,
            VoiceChannelName = guser.VoiceChannel?.Name,
            Interval = interval
        };
        Service.AddNewAutoCommand(cmd);

        await ReplyConfirmLocalizedAsync("autocmd_add", Format.Code(Format.Sanitize(cmdText)), cmd.Interval)
            .ConfigureAwait(false);
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild), OwnerOnly]
    public async Task StartupCommandsList(int page = 1)
    {
        if (page-- < 1)
            return;

        var scmds = Service.GetStartupCommands()
            .Skip(page * 5)
            .Take(5)
            .ToList();

        if (scmds.Count == 0)
        {
            await ReplyErrorLocalizedAsync("startcmdlist_none").ConfigureAwait(false);
        }
        else
        {
            var i = 0;
            await ctx.Channel.SendConfirmAsync(
                    text: string.Join("\n", scmds
                        .Select(x => $@"```css
#{++i}
[{GetText("server")}]: {(x.GuildId.HasValue ? $"{x.GuildName} #{x.GuildId}" : "-")}
[{GetText("channel")}]: {x.ChannelName} #{x.ChannelId}
[{GetText("command_text")}]: {x.CommandText}```")),
                    title: string.Empty,
                    footer: GetText("page", page + 1))
                .ConfigureAwait(false);
        }
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild), OwnerOnly]
    public async Task AutoCommandsList(int page = 1)
    {
        if (page-- < 1)
            return;

        var scmds = Service.GetAutoCommands()
            .Skip(page * 5)
            .Take(5)
            .ToList();
        if (scmds.Count == 0)
        {
            await ReplyErrorLocalizedAsync("autocmdlist_none").ConfigureAwait(false);
        }
        else
        {
            var i = 0;
            await ctx.Channel.SendConfirmAsync(
                    text: string.Join("\n", scmds
                        .Select(x => $@"```css
#{++i}
[{GetText("server")}]: {(x.GuildId.HasValue ? $"{x.GuildName} #{x.GuildId}" : "-")}
[{GetText("channel")}]: {x.ChannelName} #{x.ChannelId}
{GetIntervalText(x.Interval)}
[{GetText("command_text")}]: {x.CommandText}```")),
                    title: string.Empty,
                    footer: GetText("page", page + 1))
                .ConfigureAwait(false);
        }
    }

    private string GetIntervalText(int interval) => $"[{GetText("interval")}]: {interval}";

    [Cmd, Aliases]
    public async Task Wait(int miliseconds)
    {
        if (miliseconds <= 0)
            return;
        ctx.Message.DeleteAfter(0);
        try
        {
            var msg = await ctx.Channel.SendConfirmAsync($"⏲ {miliseconds}ms")
                .ConfigureAwait(false);
            msg.DeleteAfter(miliseconds / 1000);
        }
        catch
        {
            // ignored
        }

        await Task.Delay(miliseconds).ConfigureAwait(false);
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild),
     UserPerm(GuildPermission.Administrator), OwnerOnly]
    public async Task AutoCommandRemove([Remainder] int index)
    {
        if (!Service.RemoveAutoCommand(--index, out _))
        {
            await ReplyErrorLocalizedAsync("acrm_fail").ConfigureAwait(false);
            return;
        }

        await ctx.OkAsync().ConfigureAwait(false);
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild), OwnerOnly]
    public async Task StartupCommandRemove([Remainder] int index)
    {
        if (!Service.RemoveStartupCommand(--index, out _))
            await ReplyErrorLocalizedAsync("scrm_fail").ConfigureAwait(false);
        else
            await ReplyConfirmLocalizedAsync("scrm").ConfigureAwait(false);
    }

    [Cmd, Aliases, RequireContext(ContextType.Guild),
     UserPerm(GuildPermission.Administrator), OwnerOnly]
    public async Task StartupCommandsClear()
    {
        Service.ClearStartupCommands();

        await ReplyConfirmLocalizedAsync("startcmds_cleared").ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task ForwardMessages()
    {
        var enabled = Service.ForwardMessages();

        if (enabled)
            await ReplyConfirmLocalizedAsync("fwdm_start").ConfigureAwait(false);
        else
            await ReplyConfirmLocalizedAsync("fwdm_stop").ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task ForwardToAll()
    {
        var enabled = Service.ForwardToAll();

        if (enabled)
            await ReplyConfirmLocalizedAsync("fwall_start").ConfigureAwait(false);
        else
            await ReplyConfirmLocalizedAsync("fwall_stop").ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task ShardStats()
    {
        var statuses = _coord.GetAllShardStatuses();

        var status = string.Join(" : ", statuses
            .Select(x => (ConnectionStateToEmoji(x), x))
            .GroupBy(x => x.Item1)
            .Select(x => $"`{x.Count()} {x.Key}`")
            .ToArray());

        var allShardStrings = statuses
            .Select(st =>
            {
                var stateStr = ConnectionStateToEmoji(st);
                var timeDiff = DateTime.UtcNow - st.LastUpdate;
                var maxGuildCountLength = statuses.Max(x => x.GuildCount).ToString().Length;
                return
                    $"`{stateStr} | #{st.ShardId.ToString().PadBoth(3)} | {timeDiff:mm\\:ss} | {st.GuildCount.ToString().PadBoth(maxGuildCountLength)} | {st.UserCount}`";
            })
            .ToArray();

        var paginator = new LazyPaginatorBuilder()
            .AddUser(ctx.User)
            .WithPageFactory(PageFactory)
            .WithFooter(PaginatorFooter.PageNumber | PaginatorFooter.Users)
            .WithMaxPageIndex(allShardStrings.Length / 25)
            .WithDefaultEmotes()
            .WithActionOnCancellation(ActionOnStop.DeleteMessage)
            .Build();

        await _interactivity.SendPaginatorAsync(paginator, Context.Channel, TimeSpan.FromMinutes(60)).ConfigureAwait(false);

        async Task<PageBuilder> PageFactory(int page)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            var str = string.Join("\n", allShardStrings.Skip(25 * page).Take(25));

            if (string.IsNullOrWhiteSpace(str))
                str = GetText("no_shards_on_page");

            return new PageBuilder()
                .WithAuthor(a => a.WithName(GetText("shard_stats")))
                .WithTitle(status)
                .WithColor(Mewdeko.OkColor)
                .WithDescription(str);
        }
    }

    private static string ConnectionStateToEmoji(ShardStatus status)
    {
        var timeDiff = DateTime.UtcNow - status.LastUpdate;
        return status.ConnectionState switch
        {
            ConnectionState.Connected => "✅",
            ConnectionState.Disconnected => "🔻",
            _ when timeDiff > TimeSpan.FromSeconds(30) => " ❗ ",
            _ => " ⏳"
        };
    }

    [Cmd, Aliases]
    public async Task RestartShard(int shardId)
    {
        var success = _coord.RestartShard(shardId);
        if (success)
            await ReplyConfirmLocalizedAsync("shard_reconnecting", Format.Bold($"#{shardId}")).ConfigureAwait(false);
        else
            await ReplyErrorLocalizedAsync("no_shard_id").ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public Task LeaveServer([Remainder] string guildStr) => Service.LeaveGuild(guildStr);

    [Cmd, Aliases]
    public async Task Die()
    {
        try
        {
            await ReplyConfirmLocalizedAsync("shutting_down").ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        await Task.Delay(2000).ConfigureAwait(false);
        Environment.SetEnvironmentVariable("SNIPE_CACHED", "0");
        Environment.SetEnvironmentVariable("AFK_CACHED", "0");
        _coord.Die();
    }

    [Cmd, Aliases]
    public async Task Restart()
    {
        var success = _coord.RestartBot();
        if (!success)
        {
            await ReplyErrorLocalizedAsync("restart_fail").ConfigureAwait(false);
            return;
        }

        try
        {
            await ReplyConfirmLocalizedAsync("restarting").ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }
    }

    [Cmd, Aliases]
    public async Task SetName([Remainder] string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return;

        try
        {
            await _client.CurrentUser.ModifyAsync(u => u.Username = newName).ConfigureAwait(false);
        }
        catch (RateLimitedException)
        {
            Log.Warning("You've been ratelimited. Wait 2 hours to change your name");
        }

        await ReplyConfirmLocalizedAsync("bot_name", Format.Bold(newName)).ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task SetStatus([Remainder] SettableUserStatus status)
    {
        await _client.SetStatusAsync(SettableUserStatusToUserStatus(status)).ConfigureAwait(false);

        await ReplyConfirmLocalizedAsync("bot_status", Format.Bold(status.ToString())).ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task SetAvatar([Remainder] string? img = null)
    {
        var success = await Service.SetAvatar(img).ConfigureAwait(false);

        if (success) await ReplyConfirmLocalizedAsync("set_avatar").ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task SetGame(ActivityType type, [Remainder] string? game = null)
    {
        var rep = new ReplacementBuilder()
            .WithDefault(Context)
            .Build();

        await _bot.SetGameAsync(game == null ? game : rep.Replace(game), type).ConfigureAwait(false);

        await ReplyConfirmLocalizedAsync("set_game").ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task SetStream(string url, [Remainder] string? name = null)
    {
        name ??= "";

        await _client.SetGameAsync(name, url, ActivityType.Streaming).ConfigureAwait(false);

        await ReplyConfirmLocalizedAsync("set_stream").ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task Send(ulong whereOrTo, [Remainder] string msg)
        => await Send(whereOrTo, 0, msg).ConfigureAwait(false);

    [Cmd, Aliases]
    public async Task Send(ulong whereOrTo, ulong to = 0, [Remainder] string? msg = null)
    {
        var rep = new ReplacementBuilder().WithDefault(Context).Build();
        RestGuild potentialServer;
        try
        {
            potentialServer = await _client.Rest.GetGuildAsync(whereOrTo).ConfigureAwait(false);
        }
        catch
        {
            var potentialUser = _client.GetUser(whereOrTo);
            if (potentialUser is null)
            {
                await ctx.Channel.SendErrorAsync("Unable to find that user or guild! Please double check the Id!").ConfigureAwait(false);
                return;
            }
            if (SmartEmbed.TryParse(rep.Replace(msg), ctx.Guild?.Id, out var embed, out var plainText, out var components))
            {
                await potentialUser.SendMessageAsync(plainText, embeds: embed, components:components.Build()).ConfigureAwait(false);
                await ctx.Channel.SendConfirmAsync($"Message sent to {potentialUser.Mention}!").ConfigureAwait(false);
                return;
            }

            await potentialUser.SendMessageAsync(rep.Replace(msg)).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync($"Message sent to {potentialUser.Mention}!").ConfigureAwait(false);
            return;
        }

        if (to == 0)
        {
            await ctx.Channel.SendErrorAsync("You need to specify a Channel or User ID after the Server ID!").ConfigureAwait(false);
            return;
        }
        var channel = await potentialServer.GetTextChannelAsync(to).ConfigureAwait(false);
        if (channel is not null)
        {
            if (SmartEmbed.TryParse(rep.Replace(msg), ctx.Guild.Id, out var embed, out var plainText, out var components))
            {
                await channel.SendMessageAsync(plainText, embeds: embed, components:components?.Build()).ConfigureAwait(false);
                await ctx.Channel.SendConfirmAsync($"Message sent to {potentialServer} in {channel.Mention}").ConfigureAwait(false);
                return;
            }

            await channel.SendMessageAsync(rep.Replace(msg)).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync($"Message sent to {potentialServer} in {channel.Mention}").ConfigureAwait(false);
            return;
        }

        var user = await potentialServer.GetUserAsync(to).ConfigureAwait(false);
        if (user is null)
        {
            await ctx.Channel.SendErrorAsync("Unable to find that channel or user! Please check the ID and try again.").ConfigureAwait(false);
            return;
        }
        if (SmartEmbed.TryParse(rep.Replace(msg), ctx.Guild?.Id, out var embed1, out var plainText1, out var components1 ))
        {
            await channel.SendMessageAsync(plainText1, embeds: embed1, components:components1?.Build()).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync($"Message sent to {potentialServer} to {user.Mention}").ConfigureAwait(false);
            return;
        }

        await channel.SendMessageAsync(rep.Replace(msg)).ConfigureAwait(false);
        await ctx.Channel.SendConfirmAsync($"Message sent to {potentialServer} in {user.Mention}").ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task ImagesReload()
    {
        Service.ReloadImages();
        await ReplyConfirmLocalizedAsync("images_loading", 0).ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task StringsReload()
    {
        _strings.Reload();
        await ReplyConfirmLocalizedAsync("bot_strings_reloaded").ConfigureAwait(false);
    }

    private static UserStatus SettableUserStatusToUserStatus(SettableUserStatus sus) =>
        sus switch
        {
            SettableUserStatus.Online => UserStatus.Online,
            SettableUserStatus.Invisible => UserStatus.Invisible,
            SettableUserStatus.Idle => UserStatus.AFK,
            SettableUserStatus.Dnd => UserStatus.DoNotDisturb,
            _ => UserStatus.Online
        };

    [Cmd, Aliases]
    public async Task Bash([Remainder] string message)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"{message} 2>&1\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (ctx.Channel.EnterTypingState())
        {
            process.Start();

            // Synchronously read the standard output of the spawned process.
            var reader = process.StandardOutput;

            var output = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (output.Length > 2000)
            {
                var chunkSize = 1988;
                var stringLength = output.Length;
                for (var i = 0; i < stringLength; i += chunkSize)
                {
                    if (i + chunkSize > stringLength) chunkSize = stringLength - i;
                    await ctx.Channel.SendMessageAsync($"```bash\n{output.Substring(i, chunkSize)}```").ConfigureAwait(false);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            else if (string.IsNullOrEmpty(output))
            {
                await ctx.Channel.SendMessageAsync("```The output was blank```").ConfigureAwait(false);
            }
            else
            {
                await ctx.Channel.SendMessageAsync($"```bash\n{output}```").ConfigureAwait(false);
            }
        }

        await process.WaitForExitAsync().ConfigureAwait(false);
    }

    [Cmd, Aliases, OwnerOnly]
    public async Task Evaluate([Remainder] string code)
    {
        var cs1 = code.IndexOf("```", StringComparison.Ordinal) + 3;
        cs1 = code.IndexOf('\n', cs1) + 1;
        var cs2 = code.LastIndexOf("```", StringComparison.Ordinal);

        if (cs1 == -1 || cs2 == -1)
            throw new ArgumentException("You need to wrap the code into a code block.", nameof(code));

        code = code[cs1..cs2];

        var embed = new EmbedBuilder
        {
            Title = "Evaluating...",
            Color = new Color(0xD091B2)
        };
        var msg = await ctx.Channel.SendMessageAsync(embed: embed.Build()).ConfigureAwait(false);

        var globals = new EvaluationEnvironment((CommandContext)Context);
        var sopts = ScriptOptions.Default
            .WithImports("System", "System.Collections.Generic", "System.Diagnostics", "System.Linq",
                "System.Net.Http", "System.Net.Http.Headers", "System.Reflection", "System.Text",
                "System.Threading.Tasks", "Discord.Net", "Discord", "Discord.WebSocket", "Mewdeko.Modules",
                "Mewdeko.Services", "Mewdeko.Extensions", "Mewdeko.Modules.Administration",
                "Mewdeko.Modules.Chat_Triggers", "Mewdeko.Modules.Gambling", "Mewdeko.Modules.Games",
                "Mewdeko.Modules.Help", "Mewdeko.Modules.Music", "Mewdeko.Modules.Nsfw",
                "Mewdeko.Modules.Permissions", "Mewdeko.Modules.Searches", "Mewdeko.Modules.Server_Management")
            .WithReferences(AppDomain.CurrentDomain.GetAssemblies()
                .Where(xa => !xa.IsDynamic && !string.IsNullOrWhiteSpace(xa.Location)));

        var sw1 = Stopwatch.StartNew();
        var cs = CSharpScript.Create(code, sopts, typeof(EvaluationEnvironment));
        var csc = cs.Compile();
        sw1.Stop();

        if (csc.Any(xd => xd.Severity == DiagnosticSeverity.Error))
        {
            embed = new EmbedBuilder
            {
                Title = "Compilation failed",
                Description =
                    $"Compilation failed after {sw1.ElapsedMilliseconds:#,##0}ms with {csc.Length:#,##0} errors.",
                Color = new Color(0xD091B2)
            };
            foreach (var xd in csc.Take(3))
            {
                var ls = xd.Location.GetLineSpan();
                embed.AddField($"Error at {ls.StartLinePosition.Line:#,##0}, {ls.StartLinePosition.Character:#,##0}", Format.Code(xd.GetMessage()));
            }

            if (csc.Length > 3)
                embed.AddField("Some errors omitted", $"{csc.Length - 3:#,##0} more errors not displayed");
            await msg.ModifyAsync(x => x.Embed = embed.Build()).ConfigureAwait(false);
            return;
        }

        Exception rex;
        ScriptState<object> css = default;
        var sw2 = Stopwatch.StartNew();
        try
        {
            css = await cs.RunAsync(globals).ConfigureAwait(false);
            rex = css.Exception;
        }
        catch (Exception ex)
        {
            rex = ex;
        }

        sw2.Stop();

        if (rex != null)
        {
            embed = new EmbedBuilder
            {
                Title = "Execution failed",
                Description =
                    $"Execution failed after {sw2.ElapsedMilliseconds:#,##0}ms with `{rex.GetType()}: {rex.Message}`.",
                Color = new Color(0xD091B2)
            };
            await msg.ModifyAsync(x => x.Embed = embed.Build()).ConfigureAwait(false);
            return;
        }

        // execution succeeded
        embed = new EmbedBuilder
        {
            Title = "Evaluation successful",
            Color = new Color(0xD091B2)
        };

        embed.AddField("Result", css.ReturnValue != null ? css.ReturnValue.ToString() : "No value returned")
            .AddField("Compilation time", $"{sw1.ElapsedMilliseconds:#,##0}ms", true)
            .AddField("Execution time", $"{sw2.ElapsedMilliseconds:#,##0}ms", true);

        if (css.ReturnValue != null)
            embed.AddField("Return type", css.ReturnValue.GetType().ToString(), true);

        await msg.ModifyAsync(x => x.Embed = embed.Build()).ConfigureAwait(false);
    }
}


public sealed class EvaluationEnvironment
{
    public EvaluationEnvironment(CommandContext ctx) => Ctx = ctx;

    public CommandContext Ctx { get; }

    public IUserMessage Message => Ctx.Message;
    public IMessageChannel Channel => Ctx.Channel;
    public IGuild Guild => Ctx.Guild;
    public IUser User => Ctx.User;
    public IGuildUser Member => (IGuildUser)Ctx.User;
    public DiscordSocketClient Client => Ctx.Client as DiscordSocketClient;
}