using Discord.Commands;
using Fergun.Interactive;
using Fergun.Interactive.Pagination;
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Database.Common;
using Mewdeko.Modules.Gambling.Common;
using Mewdeko.Modules.Gambling.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Threading.Tasks;

namespace Mewdeko.Modules.Gambling;

public partial class Gambling
{
    [Group]
    public class FlowerShopCommands : GamblingSubmodule<IShopService>
    {
        public enum List
        {
            List
        }

        public enum Role
        {
            Role
        }

        private readonly ICurrencyService _cs;
        private readonly DbService _db;
        private readonly InteractiveService _interactivity;

        public FlowerShopCommands(DbService db, ICurrencyService cs, GamblingConfigService gamblingConf,
            InteractiveService serv)
            : base(gamblingConf)
        {
            _interactivity = serv;
            _db = db;
            _cs = cs;
        }

        private async Task ShopInternalAsync()
        {
            await using var uow = _db.GetDbContext();
            var entries = (await uow.ForGuildId(ctx.Guild.Id,
                    set => set.Include(x => x.ShopEntries)
                        .ThenInclude(x => x.Items))).ShopEntries
                .ToIndexed();
            var paginator = new LazyPaginatorBuilder()
                .AddUser(ctx.User)
                .WithPageFactory(PageFactory)
                .WithFooter(PaginatorFooter.PageNumber | PaginatorFooter.Users)
                .WithMaxPageIndex(entries.Count / 9)
                .WithDefaultEmotes()
            .WithActionOnCancellation(ActionOnStop.DeleteMessage)
                .Build();

            await _interactivity.SendPaginatorAsync(paginator, Context.Channel, TimeSpan.FromMinutes(60)).ConfigureAwait(false);

            async Task<PageBuilder> PageFactory(int page)
            {
                await Task.CompletedTask;
                var theseEntries = entries.Skip(page * 9).Take(9).ToArray();

                if (theseEntries.Length == 0)
                {
                    return new PageBuilder().WithErrorColor()
                            .WithDescription(GetText("shop_none"));
                }

                var embed = new PageBuilder().WithOkColor()
                        .WithTitle(GetText("shop", CurrencySign));

                for (var i = 0; i < theseEntries.Length; i++)
                {
                    var entry = theseEntries[i];
                    embed.AddField(
                        $"#{(page * 9) + i + 1} - {entry.Price}{CurrencySign}",
                        EntryToString(entry),
                        true);
                }

                return embed;
            }
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild)]
        public Task Shop()
            => ShopInternalAsync();

        [Cmd, Aliases, RequireContext(ContextType.Guild)]
        public async Task Buy(int index)
        {
            index--;
            if (index < 0)
                return;
            ShopEntry entry;
            await using (var uow = _db.GetDbContext())
            {
                var config = await uow.ForGuildId(ctx.Guild.Id, set => set
                    .Include(x => x.ShopEntries)
                    .ThenInclude(x => x.Items));
                var entries = new IndexedCollection<ShopEntry>(config.ShopEntries);
                entry = entries.ElementAtOrDefault(index);
                await uow.SaveChangesAsync().ConfigureAwait(false);
            }

            if (entry == null)
            {
                await ReplyErrorLocalizedAsync("shop_item_not_found").ConfigureAwait(false);
                return;
            }

            switch (entry.Type)
            {
                case ShopEntryType.Role:
                    {
                        var guser = (IGuildUser)ctx.User;
                        var role = ctx.Guild.GetRole(entry.RoleId);

                        if (role == null)
                        {
                            await ReplyErrorLocalizedAsync("shop_role_not_found").ConfigureAwait(false);
                            return;
                        }

                        if (guser.RoleIds.Any(id => id == role.Id))
                        {
                            await ReplyErrorLocalizedAsync("shop_role_already_bought").ConfigureAwait(false);
                            return;
                        }

                        if (await _cs.RemoveAsync(ctx.User.Id, $"Shop purchase - {entry.Type}", entry.Price)
                                     .ConfigureAwait(false))
                        {
                            try
                            {
                                await guser.AddRoleAsync(role).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Error adding shop role");
                                await _cs.AddAsync(ctx.User.Id, "Shop error refund", entry.Price).ConfigureAwait(false);
                                await ReplyErrorLocalizedAsync("shop_role_purchase_error").ConfigureAwait(false);
                                return;
                            }

                            var profit = GetProfitAmount(entry.Price);
                            await _cs.AddAsync(ctx.Client.CurrentUser.Id, $"Shop sell item - {entry.Type}", profit)
                                     .ConfigureAwait(false);
                            await _cs.AddAsync(ctx.Client.CurrentUser.Id, "Shop sell item - cut", entry.Price - profit)
                                     .ConfigureAwait(false);
                            await ReplyConfirmLocalizedAsync("shop_role_purchase", Format.Bold(role.Name))
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await ReplyErrorLocalizedAsync("not_enough", CurrencySign).ConfigureAwait(false);
                        }

                        break;
                    }
                case ShopEntryType.List when entry.Items.Count == 0:
                    await ReplyErrorLocalizedAsync("out_of_stock").ConfigureAwait(false);
                    return;
                case ShopEntryType.List:
                    {
                        var item = entry.Items.ToArray()[new MewdekoRandom().Next(0, entry.Items.Count)];

                        if (await _cs.RemoveAsync(ctx.User.Id, $"Shop purchase - {entry.Type}", entry.Price)
                                     .ConfigureAwait(false))
                        {
                            await using (var uow = _db.GetDbContext())
                            {
                                uow.Set<ShopEntryItem>().Remove(item);
                                await uow.SaveChangesAsync().ConfigureAwait(false);
                            }

                            try
                            {
                                await (await ctx.User.CreateDMChannelAsync().ConfigureAwait(false))
                                      .EmbedAsync(new EmbedBuilder().WithOkColor()
                                                                    .WithTitle(GetText("shop_purchase", ctx.Guild.Name))
                                                                    .AddField(efb =>
                                                                        efb.WithName(GetText("item")).WithValue(item.Text).WithIsInline(false))
                                                                    .AddField(efb =>
                                                                        efb.WithName(GetText("price")).WithValue(entry.Price.ToString())
                                                                           .WithIsInline(true))
                                                                    .AddField(efb =>
                                                                        efb.WithName(GetText("name")).WithValue(entry.Name).WithIsInline(true)))
                                      .ConfigureAwait(false);

                                await _cs.AddAsync(ctx.Client.CurrentUser.Id,
                                    $"Shop sell item - {entry.Name}",
                                    GetProfitAmount(entry.Price)).ConfigureAwait(false);
                            }
                            catch
                            {
                                await _cs.AddAsync(ctx.User.Id,
                                    $"Shop error refund - {entry.Name}",
                                    entry.Price).ConfigureAwait(false);
                                await using (var uow = _db.GetDbContext())
                                {
                                    var entries = new IndexedCollection<ShopEntry>((await uow.ForGuildId(ctx.Guild.Id,
                                            set => set.Include(x => x.ShopEntries)
                                                      .ThenInclude(x => x.Items)))
                                        .ShopEntries);
                                    entry = entries.ElementAtOrDefault(index);
                                    if (entry != null)
                                    {
                                        if (entry.Items.Add(item))
                                            await uow.SaveChangesAsync().ConfigureAwait(false);
                                    }
                                }

                                await ReplyErrorLocalizedAsync("shop_buy_error").ConfigureAwait(false);
                                return;
                            }

                            await ReplyConfirmLocalizedAsync("shop_item_purchase").ConfigureAwait(false);
                        }
                        else
                        {
                            await ReplyErrorLocalizedAsync("not_enough", CurrencySign).ConfigureAwait(false);
                        }

                        break;
                    }
            }
        }

        private static long GetProfitAmount(int price) => (int)Math.Ceiling(0.90 * price);

        [Cmd, Aliases, RequireContext(ContextType.Guild),
         UserPerm(GuildPermission.Administrator), BotPerm(GuildPermission.ManageRoles)]
        public async Task ShopAdd(Role _, int price, [Remainder] IRole role)
        {
            var entry = new ShopEntry
            {
                Name = "-",
                Price = price,
                Type = ShopEntryType.Role,
                AuthorId = ctx.User.Id,
                RoleId = role.Id,
                RoleName = role.Name
            };
            await using (var uow = _db.GetDbContext())
            {
                (await uow.ForGuildId(ctx.Guild.Id, set => set)).ShopEntries = new IndexedCollection<ShopEntry>((await uow.ForGuildId(ctx.Guild.Id,
                    set => set.Include(x => x.ShopEntries)
                        .ThenInclude(x => x.Items))).ShopEntries)
                {
                    entry
                };
                await uow.SaveChangesAsync().ConfigureAwait(false);
            }

            await ctx.Channel.EmbedAsync(EntryToEmbed(entry)
                .WithTitle(GetText("shop_item_add"))).ConfigureAwait(false);
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild),
         UserPerm(GuildPermission.Administrator)]
        public async Task ShopAdd(List _, int price, [Remainder] string name)
        {
            var entry = new ShopEntry
            {
                Name = name.TrimTo(100),
                Price = price,
                Type = ShopEntryType.List,
                AuthorId = ctx.User.Id,
                Items = new HashSet<ShopEntryItem>()
            };
            await using (var uow = _db.GetDbContext())
            {
                (await uow.ForGuildId(ctx.Guild.Id, set => set)).ShopEntries = new IndexedCollection<ShopEntry>((await uow.ForGuildId(ctx.Guild.Id,
                    set => set.Include(x => x.ShopEntries)
                        .ThenInclude(x => x.Items))).ShopEntries)
                {
                    entry
                };
                await uow.SaveChangesAsync().ConfigureAwait(false);
            }

            await ctx.Channel.EmbedAsync(EntryToEmbed(entry)
                .WithTitle(GetText("shop_item_add"))).ConfigureAwait(false);
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild),
         UserPerm(GuildPermission.Administrator)]
        public async Task ShopListAdd(int index, [Remainder] string itemText)
        {
            index--;
            if (index < 0)
                return;
            var item = new ShopEntryItem
            {
                Text = itemText
            };
            ShopEntry entry;
            var rightType = false;
            var added = false;
            await using (var uow = _db.GetDbContext())
            {
                var entries = new IndexedCollection<ShopEntry>((await uow.ForGuildId(ctx.Guild.Id,
                    set => set.Include(x => x.ShopEntries)
                        .ThenInclude(x => x.Items))).ShopEntries);
                entry = entries.ElementAtOrDefault(index);
                if (entry != null && (rightType = entry.Type == ShopEntryType.List))
                {
                    // ReSharper disable once AssignmentInConditionalExpression
                    if (added = entry.Items.Add(item))
                        await uow.SaveChangesAsync().ConfigureAwait(false);
                }
            }

            if (entry == null)
                await ReplyErrorLocalizedAsync("shop_item_not_found").ConfigureAwait(false);
            else if (!rightType)
                await ReplyErrorLocalizedAsync("shop_item_wrong_type").ConfigureAwait(false);
            else if (!added)
                await ReplyErrorLocalizedAsync("shop_list_item_not_unique").ConfigureAwait(false);
            else
                await ReplyConfirmLocalizedAsync("shop_list_item_added").ConfigureAwait(false);
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild),
         UserPerm(GuildPermission.Administrator)]
        public async Task ShopRemove(int index)
        {
            index--;
            if (index < 0)
                return;
            ShopEntry removed;
            await using (var uow = _db.GetDbContext())
            {
                var config = await uow.ForGuildId(ctx.Guild.Id, set => set
                    .Include(x => x.ShopEntries)
                    .ThenInclude(x => x.Items));

                var entries = new IndexedCollection<ShopEntry>(config.ShopEntries);
                removed = entries.ElementAtOrDefault(index);
                if (removed != null)
                {
                    uow.RemoveRange(removed.Items);
                    uow.Remove(removed);
                    await uow.SaveChangesAsync().ConfigureAwait(false);
                }
            }

            if (removed == null)
            {
                await ReplyErrorLocalizedAsync("shop_item_not_found").ConfigureAwait(false);
            }
            else
            {
                await ctx.Channel.EmbedAsync(EntryToEmbed(removed)
                                .WithTitle(GetText("shop_item_rm"))).ConfigureAwait(false);
            }
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild),
         UserPerm(GuildPermission.Administrator)]
        public async Task ShopChangePrice(int index, int price)
        {
            if (--index < 0 || price <= 0)
                return;

            var succ = await Service.ChangeEntryPriceAsync(Context.Guild.Id, index, price);
            if (succ)
            {
                await ShopInternalAsync();
                await ctx.OkAsync();
            }
            else
            {
                await ctx.ErrorAsync();
            }
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild),
         UserPerm(GuildPermission.Administrator)]
        public async Task ShopChangeName(int index, [Remainder] string newName)
        {
            if (--index < 0 || string.IsNullOrWhiteSpace(newName))
                return;

            var succ = await Service.ChangeEntryNameAsync(Context.Guild.Id, index, newName);
            if (succ)
            {
                await ShopInternalAsync();
                await ctx.OkAsync();
            }
            else
            {
                await ctx.ErrorAsync();
            }
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild),
         UserPerm(GuildPermission.Administrator)]
        public async Task ShopSwap(int index1, int index2)
        {
            if (--index1 < 0 || --index2 < 0 || index1 == index2)
                return;

            var succ = await Service.SwapEntriesAsync(Context.Guild.Id, index1, index2);
            if (succ)
            {
                await ShopInternalAsync();
                await ctx.OkAsync();
            }
            else
            {
                await ctx.ErrorAsync();
            }
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild),
         UserPerm(GuildPermission.Administrator)]
        public async Task ShopMove(int fromIndex, int toIndex)
        {
            if (--fromIndex < 0 || --toIndex < 0 || fromIndex == toIndex)
                return;

            var succ = await Service.MoveEntryAsync(Context.Guild.Id, fromIndex, toIndex);
            if (succ)
            {
                await ShopInternalAsync();
                await ctx.OkAsync();
            }
            else
            {
                await ctx.ErrorAsync();
            }
        }

        public EmbedBuilder EntryToEmbed(ShopEntry entry)
        {
            var embed = new EmbedBuilder().WithOkColor();

            switch (entry.Type)
            {
                case ShopEntryType.Role:
                    return embed.AddField(efb =>
                                    efb.WithName(GetText("name")).WithValue(GetText("shop_role",
                                           Format.Bold(ctx.Guild.GetRole(entry.RoleId)?.Name ?? "MISSING_ROLE")))
                                       .WithIsInline(true))
                                .AddField(efb =>
                                    efb.WithName(GetText("price")).WithValue(entry.Price.ToString()).WithIsInline(true))
                                .AddField(efb =>
                                    efb.WithName(GetText("type")).WithValue(entry.Type.ToString()).WithIsInline(true));
                case ShopEntryType.List:
                    return embed.AddField(efb => efb.WithName(GetText("name")).WithValue(entry.Name).WithIsInline(true))
                                .AddField(efb =>
                                    efb.WithName(GetText("price")).WithValue(entry.Price.ToString()).WithIsInline(true))
                                .AddField(efb =>
                                    efb.WithName(GetText("type")).WithValue(GetText("random_unique_item")).WithIsInline(true));
                default:
                    //else if (entry.Type == ShopEntryType.Infinite_List)
                    //    return embed.AddField(efb => efb.WithName(GetText("name")).WithValue(GetText("shop_role", Format.Bold(entry.RoleName))).WithIsInline(true))
                    //            .AddField(efb => efb.WithName(GetText("price")).WithValue(entry.Price.ToString()).WithIsInline(true))
                    //            .AddField(efb => efb.WithName(GetText("type")).WithValue(entry.Type.ToString()).WithIsInline(true));
                    return null;
            }
        }

        public string? EntryToString(ShopEntry entry)
        {
            switch (entry.Type)
            {
                case ShopEntryType.Role:
                    return GetText("shop_role", Format.Bold(ctx.Guild.GetRole(entry.RoleId)?.Name ?? "MISSING_ROLE"));
                case ShopEntryType.List:
                    return $"{GetText("unique_items_left", entry.Items.Count)}\n{entry.Name}";
                default:
                    //else if (entry.Type == ShopEntryType.Infinite_List)
                    //{

                    //}
                    return "";
            }
        }
    }
}