using Discord.Commands;
using Fergun.Interactive;
using Fergun.Interactive.Pagination;
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.Help.Services;
using Mewdeko.Modules.Permissions.Services;
using Mewdeko.Services.strings;
using Swan;
using System.Threading.Tasks;
namespace Mewdeko.Modules.Help;

public class Help : MewdekoModuleBase<HelpService>
{
    private readonly CommandService _cmds;
    private readonly InteractiveService _interactive;
    private readonly GlobalPermissionService _perms;
    private readonly IServiceProvider _services;
    private readonly IBotStrings _strings;
    private readonly GuildSettingsService _guildSettings;
    

    public Help(GlobalPermissionService perms, CommandService cmds,
        IServiceProvider services, IBotStrings strings,
        InteractiveService serv,
        GuildSettingsService guildSettings)
    {
        _interactive = serv;
        _guildSettings = guildSettings;
        _cmds = cmds;
        _perms = perms;
        _services = services;
        _strings = strings;
    }

    

    [Cmd, Aliases]
    public async Task SearchCommand(string commandname)
    {
        var cmds = _cmds.Commands.Distinct().Where(c => c.Name.Contains(commandname, StringComparison.InvariantCulture));
        if (!cmds.Any())
        {
            await ctx.Channel.SendErrorAsync(
                "That command wasn't found! Please retry your search with a different term.").ConfigureAwait(false);
        }
        else
        {
            string? cmdnames = null;
            string? cmdremarks = null;
            foreach (var i in cmds)
            {
                cmdnames += $"\n{i.Name}";
                cmdremarks += $"\n{i.RealSummary(_strings, ctx.Guild.Id, await _guildSettings.GetPrefix(ctx.Guild)).Truncate(50)}";
            }
            var eb = new EmbedBuilder()
                     .WithOkColor()
                     .AddField("Command", cmdnames, true)
                     .AddField("Description", cmdremarks, true);
            await ctx.Channel.SendMessageAsync(embed: eb.Build()).ConfigureAwait(false);
        }
    }

    [Cmd, Aliases]
    public async Task Modules()
    {
        var embed = await Service.GetHelpEmbed(false, ctx.Guild, ctx.Channel, ctx.User);
        await HelpService.AddUser(ctx.Message, DateTime.UtcNow).ConfigureAwait(false);
        await ctx.Channel.SendMessageAsync(embed: embed.Build(), components: Service.GetHelpComponents(ctx.Guild, ctx.User).Build()).ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task Donate() =>
        await ctx.Channel.SendConfirmAsync(
            "If you would like to support the project, here's how:\nKo-Fi: https://playerduo.net/dcschuchu\nI appreciate any donations as they will help improve Dino for the better!").ConfigureAwait(false);

    [Cmd, Aliases]
    public async Task Commands([Remainder] string? module = null)
    {
        module = module?.Trim().ToUpperInvariant().Replace(" ", "");
        if (string.IsNullOrWhiteSpace(module))
        {
            await Modules().ConfigureAwait(false);
            return;
        }

        var prefix = await _guildSettings.GetPrefix(ctx.Guild);
        // Find commands for that module
        // don't show commands which are blocked
        // order by name
        var cmds = _cmds.Commands.Where(c =>
                c.Module.GetTopLevelModule().Name.ToUpperInvariant()
                    .StartsWith(module, StringComparison.InvariantCulture))
            .Where(c => !_perms.BlockedCommands.Contains(c.Aliases[0].ToLowerInvariant()))
            .OrderBy(c => c.Aliases[0])
            .Distinct(new CommandTextEqualityComparer());

        // check preconditions for all commands, but only if it's not 'all'
        // because all will show all commands anyway, no need to check
        var succ = new HashSet<CommandInfo>((await Task.WhenAll(cmds.Select(async x =>
            {
                var pre = await x.CheckPreconditionsAsync(Context, _services).ConfigureAwait(false);
                return (Cmd: x, Succ: pre.IsSuccess);
            })).ConfigureAwait(false))
            .Where(x => x.Succ)
            .Select(x => x.Cmd));

        var cmdsWithGroup = cmds
            .GroupBy(c => c.Module.Name.Replace("Commands", "", StringComparison.InvariantCulture))
            .OrderBy(x => x.Key == x.First().Module.Name ? int.MaxValue : x.Count());

        if (!cmds.Any())
        {
            await ReplyErrorLocalizedAsync("module_not_found_or_cant_exec").ConfigureAwait(false);
            return;
        }

        var i = 0;
        var groups = cmdsWithGroup.GroupBy(_ => i++ / 48).ToArray();
        var paginator = new LazyPaginatorBuilder()
            .AddUser(ctx.User)
            .WithPageFactory(PageFactory)
            .WithFooter(PaginatorFooter.PageNumber | PaginatorFooter.Users)
            .WithMaxPageIndex(groups.Select(x => x.Count()).FirstOrDefault() - 1)
            .WithDefaultEmotes()
            .WithActionOnCancellation(ActionOnStop.DeleteMessage)
            .Build();

        await _interactive.SendPaginatorAsync(paginator, Context.Channel,
            TimeSpan.FromMinutes(60)).ConfigureAwait(false);

        async Task<PageBuilder> PageFactory(int page)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            var transformed = groups.Select(x => x.ElementAt(page).Where(commandInfo => !commandInfo.Attributes.Any(attribute => attribute is HelpDisabled)).Select(commandInfo =>
                    $"{(succ.Contains(commandInfo) ? "✅" : "❌")}{prefix + commandInfo.Aliases[0],-15} {$"[{commandInfo.Aliases.Skip(1).FirstOrDefault()}]",-8}"))
                .FirstOrDefault();
            var last = groups.Select(x => x.Count()).FirstOrDefault();
            for (i = 0; i < last; i++)
            {
                if (i != last - 1 || (i + 1) % 1 == 0) continue;
                var grp = 0;
                var count = transformed.Count();
                transformed = transformed
                              .GroupBy(_ => grp++ % count / 2)
                              .Select(x => x.Count() == 1 ? $"{x.First()}" : string.Concat(x));
            }

            return new PageBuilder()
                .AddField(groups.Select(x => x.ElementAt(page).Key).FirstOrDefault(),
                    $"```css\n{string.Join("\n", transformed)}\n```")
                .WithDescription(
                    $"<a:pickyes:1183894766648295476>: Bạn có thể dùng lệnh này .\n<a:pickno:1183894770834223164>: Bạn không thể dùng lệnh này .\n<a:loading:1182337910998052946>: Nếu bạn cần giúp đỡ bất cứ điều gì [The Support Server](https://discord.gg/C3yyk7ebEz)\nNhập `{prefix}h commandname` để xem thông tin lệnh")
                .WithOkColor();
        }
    }

    [Cmd, Aliases, Priority(0)]
    public async Task H([Remainder] string fail)
    {
        var prefixless =
            _cmds.Commands.FirstOrDefault(x => x.Aliases.Any(cmdName => cmdName.ToLowerInvariant() == fail));
        if (prefixless != null)
        {
            await H(prefixless).ConfigureAwait(false);
            return;
        }

        await ReplyErrorLocalizedAsync("command_not_found").ConfigureAwait(false);
    }

    [Cmd, Aliases, Priority(1)]
    public async Task H([Remainder] CommandInfo? com = null)
    {
        var channel = ctx.Channel;

        if (com == null)
        {
            await Modules().ConfigureAwait(false);
            return;
        }

        var comp = new ComponentBuilder().WithButton(GetText("help_run_cmd"), $"runcmd.{com.Aliases[0]}", ButtonStyle.Success);
        var embed = await Service.GetCommandHelp(com, ctx.Guild);
        await channel.SendMessageAsync(embed: embed.Build(), components: comp.Build()).ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task Guide() => await ctx.Channel.SendConfirmAsync("You can find the website at https://chuchudayne.com").ConfigureAwait(false);
    [Cmd, Aliases]
    public async Task Source() => await ctx.Channel.SendConfirmAsync("https://chuchudayne.com/source").ConfigureAwait(false);

///bầu cua ei
    [Cmd, Aliases]
    public async Task Baucua()
    {
        var emojiMap = new Dictionary<string, string>
        {
            { "cá", "<:ca:878997284669505556>" },
            { "bầu", "<:bau:878997284476567703>" },
            { "cua", "<:cua:878997284686270465>" },
            { "nai", "<:nai:878997284312977509>" },
            { "tôm", "<:tom:878997284480770098>" },
            { "gà", "<:ga:878997284661108737>" }
        };
        var random = new Random();
        var randomEmojis = emojiMap.Keys.OrderBy(x => random.Next()).Take(3).ToList();
        var resultEmojis = randomEmojis.Select(emoji => new Emoji(emojiMap[emoji])).ToList();
        var resultValues = randomEmojis.Select(emoji => emoji).ToList();
        var randomMessage = await ctx.Channel.SendMessageAsync("<a:z1:879671558749167626> <a:z1:879671558749167626> <a:z1:879671558749167626>").ConfigureAwait(false);
        await Task.Delay(5000); // Chờ 5 giây
        await randomMessage.ModifyAsync(x => x.Content = $"**{Context.User.Username}** lắc ra : **{string.Join(" ", resultEmojis)}**").ConfigureAwait(false);
        await Task.Delay(1000);
        var secondMessage = await ctx.Channel.SendMessageAsync($"Kết Quả : **{string.Join("<a:TT24:1184913707101339688>", resultValues)}**").ConfigureAwait(false);
    }

    [Cmd, Aliases]
    public async Task Random([Remainder] string args = null)
    {
        if (string.IsNullOrWhiteSpace(args) || !int.TryParse(args, out int parsedLimit) || parsedLimit <= 0)
        {
            await ctx.Channel.SendMessageAsync("Phải nhập một số tự nhiên lớn hơn 0.");
            return;
        }
        int limit = parsedLimit;
        int randomNumber = new Random().Next(limit + 1);
        await ctx.Channel.SendMessageAsync($"Số ngẫu nhiên: **{randomNumber}**");
    }
    [Cmd, Aliases]
    public async Task Taixiu([Remainder] string args = null)
    {
        int numberOfValuesToChoose = 3;
        int[] diceValues = { 1, 2, 3, 4, 5, 6 };

        Dictionary<int, string> emojiMap = new Dictionary<int, string>
        {
            { 1, "<:1:879665246556545086>" },
            { 2, "<:2:879665246808191016>" },
            { 3, "<:3:879665246506205186>" },
            { 4, "<:4:879665246569115649>" },
            { 5, "<:5:879665246686568469>" },
            { 6, "<:6:879665246376185887>" }
        };

        List<int> chosenValues = diceValues.OrderBy(x => new Random().Next()).Take(numberOfValuesToChoose).ToList();

        var firstMessage = await ctx.Channel.SendMessageAsync($"{string.Join("", Enumerable.Repeat("<a:lac:738858292343996526>", numberOfValuesToChoose))}");

        await Task.Delay(2000);

        List<string> currentEmojis = Enumerable.Repeat("<a:lac:738858292343996526>", numberOfValuesToChoose).ToList();

        for (int i = 0; i < numberOfValuesToChoose; i++)
        {
            currentEmojis[i] = emojiMap[chosenValues[i]];
            await firstMessage.ModifyAsync(x => x.Content = $"{string.Join("", currentEmojis)}");
            await Task.Delay(2000);
        }

        int total = chosenValues.Sum();
        string result = total > 10 ? "Tài" : "Xỉu";
        await ctx.Channel.SendMessageAsync($"Kết Quả : **{string.Join("<a:TT24:1184913707101339688>", chosenValues)} = {total}, {result}**");
    }
}
public class CommandTextEqualityComparer : IEqualityComparer<CommandInfo>
{
    public bool Equals(CommandInfo? x, CommandInfo? y) => x.Aliases[0] == y.Aliases[0];

    public int GetHashCode(CommandInfo obj) => obj.Aliases[0].GetHashCode(StringComparison.InvariantCulture);
}
