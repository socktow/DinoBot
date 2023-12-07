﻿using Discord.Net;
using Mewdeko.Common.Collections;
using Mewdeko.Common.ModuleBehaviors;
using Mewdeko.Common.PubSub;
using Mewdeko.Modules.Administration.Services;
using Mewdeko.Modules.Moderation.Services;
using Mewdeko.Services.strings;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Globalization;
using System.Threading.Tasks;

namespace Mewdeko.Modules.Permissions.Services;

public class FilterService : IEarlyBehavior, INService
{
    private readonly CultureInfo? _cultureInfo = new("en-US");
    private readonly DbService _db;
    private readonly IPubSub _pubSub;

    private readonly TypedKey<AutoBanEntry[]> _blPubKey = new("autobanword.reload");
    private readonly DiscordSocketClient _client;
    public IReadOnlyList<AutoBanEntry> Blacklist;
    public readonly AdministrationService Ass;
    public readonly UserPunishService Upun;
    private readonly GuildSettingsService _gss;

    public FilterService(DiscordSocketClient client, DbService db, IPubSub pubSub,
        UserPunishService upun2, IBotStrings strng, AdministrationService ass,
        GuildSettingsService gss, EventHandler eventHandler)
    {
        _db = db;
        _client = client;
        _pubSub = pubSub;
        Upun = upun2;
        Strings = strng;
        Reload(false);
        _pubSub.Sub(_blPubKey, OnReload);
        Ass = ass;
        _gss = gss;
        using (var uow = db.GetDbContext())
        {
            var ids = client.GetGuildIds();
            var configs = uow.GuildConfigs
                .AsQueryable()
                .Include(x => x.FilteredWords)
                .Include(x => x.FilterLinksChannelIds)
                .Include(x => x.FilterWordsChannelIds)
                .Include(x => x.FilterInvitesChannelIds)
                .Where(gc => ids.Contains(gc.GuildId))
                .ToList();

            InviteFilteringServers =
                new ConcurrentHashSet<ulong>(configs.Where(gc => gc.FilterInvites).Select(gc => gc.GuildId));
            InviteFilteringChannels =
                new ConcurrentHashSet<ulong>(configs.SelectMany(gc =>
                    gc.FilterInvitesChannelIds.Select(fci => fci.ChannelId)));

            LinkFilteringServers =
                new ConcurrentHashSet<ulong>(configs.Where(gc => gc.FilterLinks).Select(gc => gc.GuildId));
            LinkFilteringChannels =
                new ConcurrentHashSet<ulong>(configs.SelectMany(gc =>
                    gc.FilterLinksChannelIds.Select(fci => fci.ChannelId)));

            var dict = configs.ToDictionary(gc => gc.GuildId,
                gc => new ConcurrentHashSet<string>(gc.FilteredWords.Select(fw => fw.Word)));

            ServerFilteredWords = new ConcurrentDictionary<ulong, ConcurrentHashSet<string>>(dict);

            var serverFiltering = configs.Where(gc => gc.FilterWords);
            WordFilteringServers = new ConcurrentHashSet<ulong>(serverFiltering.Select(gc => gc.GuildId));
            WordFilteringChannels =
                new ConcurrentHashSet<ulong>(configs.SelectMany(gc =>
                    gc.FilterWordsChannelIds.Select(fwci => fwci.ChannelId)));
        }

        eventHandler.MessageUpdated += (oldData, newMsg, channel) =>
        {
            _ = Task.Run(() =>
            {
                var guild = (channel as ITextChannel)?.Guild;

                if (guild == null || newMsg is not IUserMessage usrMsg)
                    return Task.CompletedTask;

                return RunBehavior(null, guild, usrMsg);
            });
            return Task.CompletedTask;
        };
    }

    public IBotStrings Strings { get; set; }
    public ConcurrentHashSet<ulong> InviteFilteringChannels { get; }
    public ConcurrentHashSet<ulong> InviteFilteringServers { get; }

    //serverid, filteredwords
    public ConcurrentDictionary<ulong, ConcurrentHashSet<string>> ServerFilteredWords { get; }

    public ConcurrentHashSet<ulong> WordFilteringChannels { get; }
    public ConcurrentHashSet<ulong> WordFilteringServers { get; }

    public ConcurrentHashSet<ulong> LinkFilteringChannels { get; }
    public ConcurrentHashSet<ulong> LinkFilteringServers { get; }

    public int Priority => -50;
    public ModuleBehaviorType BehaviorType => ModuleBehaviorType.Blocker;

    public async Task<bool> RunBehavior(DiscordSocketClient _, IGuild guild, IUserMessage msg) =>
        msg.Author is IGuildUser gu && !gu.RoleIds.Contains(await Ass.GetStaffRole(guild.Id)) &&
        !gu.GuildPermissions.Administrator && (await FilterInvites(guild, msg).ConfigureAwait(false)
                                               || await FilterWords(guild, msg).ConfigureAwait(false)
                                               || await FilterLinks(guild, msg).ConfigureAwait(false)
                                               || await FilterBannedWords(guild, msg).ConfigureAwait(false));

    private ValueTask OnReload(AutoBanEntry[] blacklist)
    {
        Blacklist = blacklist;
        return default;
    }

    public void Reload(bool publish = true)
    {
        using var uow = _db.GetDbContext();
        var toPublish = uow.AutoBanWords.AsNoTracking().ToArray();
        Blacklist = toPublish;
        if (publish) _pubSub.Pub(_blPubKey, toPublish);
    }

    public void WordBlacklist(string id, ulong id2)
    {
        using var uow = _db.GetDbContext();
        var item = new AutoBanEntry { Word = id, GuildId = id2 };
        uow.AutoBanWords.Add(item);
        uow.SaveChanges();

        Reload();
    }

    public void UnBlacklist(string id, ulong id2)
    {
        using var uow = _db.GetDbContext();
        var toRemove = uow.AutoBanWords
            .FirstOrDefault(bi => bi.Word == id && bi.GuildId == id2);

        if (toRemove is not null)
            uow.AutoBanWords.Remove(toRemove);

        uow.SaveChanges();

        Reload();
    }

    public ConcurrentHashSet<string?>? FilteredWordsForChannel(ulong channelId, ulong guildId)
    {
        var words = new ConcurrentHashSet<string>();
        if (WordFilteringChannels.Contains(channelId))
            ServerFilteredWords.TryGetValue(guildId, out words);
        return words;
    }

    public async Task<int> GetInvWarn(ulong id) => (await _gss.GetGuildConfig(id)).invwarn;

    public async Task InvWarn(IGuild guild, string yesnt)
    {
        var yesno = -1;
        await using (_db.GetDbContext().ConfigureAwait(false))
        {
            yesno = yesnt switch
            {
                "y" => 1,
                "n" => 0,
                _ => yesno
            };
        }

        var uow = _db.GetDbContext();
        await using (uow.ConfigureAwait(false))
        {
            var gc = await uow.ForGuildId(guild.Id, set => set);
            gc.invwarn = yesno;
            await uow.SaveChangesAsync().ConfigureAwait(false);
            _gss.UpdateGuildConfig(guild.Id, gc);
        }
    }

    public async Task<int> GetFw(ulong id) => (await _gss.GetGuildConfig(id)).fwarn;

    public async Task SetFwarn(IGuild guild, string yesnt)
    {
        var yesno = -1;
        await using (_db.GetDbContext().ConfigureAwait(false))
        {
            yesno = yesnt switch
            {
                "y" => 1,
                "n" => 0,
                _ => yesno
            };
        }

        var uow = _db.GetDbContext();
        await using (uow.ConfigureAwait(false))
        {
            var gc = await uow.ForGuildId(guild.Id, set => set);
            gc.fwarn = yesno;
            await uow.SaveChangesAsync().ConfigureAwait(false);
            _gss.UpdateGuildConfig(guild.Id, gc);
        }
    }

    public async Task ClearFilteredWords(ulong guildId)
    {
        await using var uow = _db.GetDbContext();
        var gc = await uow.ForGuildId(guildId,
            set => set.Include(x => x.FilteredWords)
                .Include(x => x.FilterWordsChannelIds));

        WordFilteringServers.TryRemove(guildId);
        ServerFilteredWords.TryRemove(guildId, out _);

        foreach (var c in gc.FilterWordsChannelIds) WordFilteringChannels.TryRemove(c.ChannelId);

        gc.FilterWords = false;
        gc.FilteredWords.Clear();
        gc.FilterWordsChannelIds.Clear();

        await uow.SaveChangesAsync().ConfigureAwait(false);
    }

    public ConcurrentHashSet<string>? FilteredWordsForServer(ulong guildId)
    {
        var words = new ConcurrentHashSet<string>();
        if (WordFilteringServers.Contains(guildId))
            ServerFilteredWords.TryGetValue(guildId, out words);
        return words;
    }

    protected string? GetText(string? key, params object?[] args) => Strings.GetText(key, _cultureInfo, args);

    public async Task<bool> FilterBannedWords(IGuild? guild, IUserMessage? msg)
    {
        if (guild is null)
            return false;
        if (msg is null)
            return false;
        var bannedwords = Blacklist.Where(x => x.GuildId == guild.Id);
        foreach (var i in bannedwords.Select(x => x.Word))
        {
            if (msg.Content.ToLower().Contains(i.ToLower()))
                try
                {
                    await msg.DeleteAsync().ConfigureAwait(false);
                    var defaultMessage = GetText("bandm", Format.Bold(guild.Name),
                        $"Banned for saying autoban word {i}");
                    var embed = await Upun.GetBanUserDmEmbed(_client, guild as SocketGuild,
                        await guild.GetUserAsync(_client.CurrentUser.Id).ConfigureAwait(false), msg.Author as IGuildUser, defaultMessage,
                        $"Banned for saying autoban word {i}", null).ConfigureAwait(false);
                    await (await msg.Author.CreateDMChannelAsync().ConfigureAwait(false)).SendMessageAsync(embed.Item2, embeds: embed.Item1, components: embed.Item3.Build()).ConfigureAwait(false);
                    await guild.AddBanAsync(msg.Author, 0, "Auto Ban Word Detected").ConfigureAwait(false);
                    return true;
                }
                catch
                {
                    try
                    {
                        await guild.AddBanAsync(msg.Author, 0, "Auto Ban Word Detected").ConfigureAwait(false);
                        return true;
                    }
                    catch
                    {
                        Log.Error("Im unable to autoban in {ChannelName}", msg.Channel.Name);
                        return false;
                    }
                }
        }

        return false;
    }

    public async Task<bool> FilterWords(IGuild? guild, IUserMessage? usrMsg)
    {
        if (guild is null)
            return false;
        if (usrMsg is null)
            return false;

        var filteredChannelWords =
            FilteredWordsForChannel(usrMsg.Channel.Id, guild.Id) ?? new ConcurrentHashSet<string>();
        var filteredServerWords = FilteredWordsForServer(guild.Id) ?? new ConcurrentHashSet<string>();
        usrMsg.Content.ToLowerInvariant().Split(' ');
        if (filteredChannelWords.Count != 0 || filteredServerWords.Count != 0)
        {
            foreach (var word in filteredChannelWords)
                if (usrMsg.Content.ToLower().Contains(word))
                {
                    try
                    {
                        await usrMsg.DeleteAsync().ConfigureAwait(false);
                        if (await GetFw(guild.Id) != 0)
                        {
                            await Upun.Warn(guild, usrMsg.Author.Id, _client.CurrentUser,
                                "Warned for Filtered Word").ConfigureAwait(false);
                            var user = await usrMsg.Author.CreateDMChannelAsync().ConfigureAwait(false);
                            await user.SendErrorAsync($"You have been warned for using the word {Format.Code(word)}").ConfigureAwait(false);
                        }
                    }
                    catch (HttpException ex)
                    {
                        Log.Warning(
                            "I do not have permission to filter words in channel with id " + usrMsg.Channel.Id, ex);
                    }

                    return true;
                }
        }

        foreach (var word in filteredServerWords)
        {
            if (usrMsg.Content.Contains(word))
            {
                try
                {
                    await usrMsg.DeleteAsync().ConfigureAwait(false);
                    if (await GetFw(guild.Id) != 0)
                    {
                        await Upun.Warn(guild, usrMsg.Author.Id, _client.CurrentUser,
                            "Warned for Filtered Word").ConfigureAwait(false);
                        var user = await usrMsg.Author.CreateDMChannelAsync().ConfigureAwait(false);
                        await user.SendErrorAsync($"You have been warned for using the word {Format.Code(word)}").ConfigureAwait(false);
                    }
                }
                catch (HttpException ex)
                {
                    Log.Warning(
                        "I do not have permission to filter words in channel with id " + usrMsg.Channel.Id, ex);
                }

                return true;
            }
        }

        return false;
    }

    public async Task<bool> FilterInvites(IGuild? guild, IUserMessage? usrMsg)
    {
        if (guild is null)
            return false;
        if (usrMsg is null)
            return false;

        if ((InviteFilteringChannels.Contains(usrMsg.Channel.Id)
             || InviteFilteringServers.Contains(guild.Id))
            && usrMsg.Content.IsDiscordInvite())
        {
            try
            {
                await usrMsg.DeleteAsync().ConfigureAwait(false);
                if (await GetInvWarn(guild.Id) != 0)
                {
                    await Upun.Warn(guild, usrMsg.Author.Id, _client.CurrentUser, "Warned for Posting Invite").ConfigureAwait(false);
                    var user = await usrMsg.Author.CreateDMChannelAsync().ConfigureAwait(false);
                    await user.SendErrorAsync("You have been warned for sending an invite, this is not allowed!").ConfigureAwait(false);
                }

                return true;
            }
            catch (HttpException ex)
            {
                Log.Warning("I do not have permission to filter invites in channel with id " + usrMsg.Channel.Id,
                    ex);
                return true;
            }
        }

        return false;
    }

    public async Task<bool> FilterLinks(IGuild? guild, IUserMessage? usrMsg)
    {
        if (guild is null)
            return false;
        if (usrMsg is null)
            return false;

        if ((LinkFilteringChannels.Contains(usrMsg.Channel.Id)
             || LinkFilteringServers.Contains(guild.Id))
            && usrMsg.Content.TryGetUrlPath(out _))
        {
            try
            {
                await usrMsg.DeleteAsync().ConfigureAwait(false);
                return true;
            }
            catch
            {
                Log.Warning("I do not have permission to filter links in channel with id " + usrMsg.Channel.Id);
                return true;
            }
        }

        return false;
    }
}
