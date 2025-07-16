using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using IksAdminApi;

namespace IksAdmins;

public partial class IksAdmins : AdminModule
{
    internal static DataBaseService? _dataBaseService;

    public override string ModuleName => "[IKS] Admins";
    public override string ModuleDescription => "";
    public override string ModuleAuthor => "E!N";
    public override string ModuleVersion => "v1.0.0";


    public override void Ready()
    {
        _dataBaseService = new DataBaseService(
            Localizer
        );
        _dataBaseService.TestAndCheckDataBaseTableAsync().GetAwaiter().GetResult();
        Api.OnFullConnect += OnFullConnect;
        _ = (_dataBaseService?.ReloadCacheAsync());
    }

    private async void OnFullConnect(string steamId, string ip)
    {
        if (ulong.TryParse(steamId, out ulong steamId64))
        {
            if (Api.ServerAdmins.TryGetValue(steamId64, out _))
            {
                await _dataBaseService!.AddAdminInListAsync(steamId);
            }
        }
    }

    public override void Unload(bool hotReload)
    {
        base.Unload(hotReload);
        Api.OnFullConnect -= OnFullConnect;
        Api.NextPlayerMessage.Clear();
    }

    [ConsoleCommand("css_admins", "AdminsList")]
    public void OnCommandAdmins(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null)
        {
            IDynamicMenu menu = Api.CreateMenu("", Localizer["menu.admins.title"]);

            foreach (CCSPlayerController? user in PlayersUtils.GetOnlinePlayers(includeBots: false))
            {
                if (user == null || !user.IsValid || user.IsHLTV)
                {
                    continue;
                }

                Admin? admin = AdminUtils.Admin(user);

                if (admin == null || Api.HidenAdmins.Any(a => a.SteamId == admin.SteamId))
                {
                    continue;
                }

                menu.AddMenuOption(
                    MenuUtils.GenerateOptionId($"admin_select_{admin.SteamId}"),
                    admin.CurrentName,
                    (p, opt) => OnSelectAdmin(p, admin, menu),
                    disabled: !_dataBaseService!._adminDataCache.ContainsKey(admin.SteamId)
                );
            }

            menu.Open(player);
        }
    }

    public void OnSelectAdmin(CCSPlayerController player, Admin admin, IDynamicMenu? main_menu)
    {
        (int Likes, int Dislikes)? rep = _dataBaseService!.GetPlayerRep(admin.SteamId);
        string? contact = _dataBaseService!.GetAdminContact(admin.SteamId);

        CreateAndOpenAdminMenu(player, admin, main_menu, rep, contact);
    }

    private void CreateAndOpenAdminMenu(CCSPlayerController player, Admin admin, IDynamicMenu? main_menu, (int Likes, int Dislikes)? rep, string? contact)
    {
        IDynamicMenu menu = Api.CreateMenu(MenuUtils.GenerateMenuId($"admin_info_{admin.SteamId}"), Localizer["menu.select.admin.title", admin.CurrentName], backMenu: main_menu);

        // Группа
        if (admin.Group != null)
        {
            menu.AddMenuOption(
                MenuUtils.GenerateOptionId($"admin_info_group_{admin.SteamId}"),
                Localizer["menu.select.admin.group", admin.Group],
                (p, opt) => { },
                disabled: true
            );
        }

        // Флаги
        menu.AddMenuOption(
            MenuUtils.GenerateOptionId($"admin_info_flags_{admin.SteamId}"),
            Localizer["menu.select.admin.flags", admin.CurrentFlags],
            (p, opt) => { },
            disabled: true
        );

        // Иммунитет
        menu.AddMenuOption(
            MenuUtils.GenerateOptionId($"admin_info_immunity_{admin.SteamId}"),
            Localizer["menu.select.admin.immunity", admin.CurrentImmunity],
            (p, opt) => { },
            disabled: true
        );

        // Дата окончания
        menu.AddMenuOption(
            MenuUtils.GenerateOptionId($"admin_info_end_{admin.SteamId}"),
            Localizer["menu.select.admin.end", admin.EndAt.HasValue && admin.EndAt.Value > 0 ? Utils.GetDateString(admin.EndAt.Value) : Api.Localizer["Other.Never"]],
            (p, opt) => { },
            disabled: true
        );

        // Репутация
        if (rep.HasValue)
        {
            menu.AddMenuOption(
                MenuUtils.GenerateOptionId($"admin_info_rep_{admin.SteamId}"),
                Localizer["menu.select.admin.rep", rep.Value.Likes, rep.Value.Dislikes],
                (p, opt) => { },
                disabled: true
            );
        }

        // Указать\Изменить контакт
        if (player == admin.Controller)
        {
            menu.AddMenuOption(
                MenuUtils.GenerateOptionId($"admin_set_contact_{admin.SteamId}"),
                Localizer["menu.select.admin.contact.set"],
                (p, opt) => SetContact(player, admin, main_menu)
            );
        }

        // Текущий контакт
        menu.AddMenuOption(
            MenuUtils.GenerateOptionId($"admin_info_contact_{admin.SteamId}"),
            contact == null
                ? Localizer["menu.select.admin.contact.null"]
                : Localizer["menu.select.admin.contact.established", contact],
            (p, opt) => { },
            disabled: true
        );

        if (player != admin.Controller)
        {
            // Поставить лайк
            menu.AddMenuOption(
                MenuUtils.GenerateOptionId($"admin_like_{admin.SteamId}"),
                Localizer["menu.select.admin.like"],
                async (p, opt) =>
                {
                    await _dataBaseService!.SetReputationAsync(player, admin, true);
                    Server.NextFrame(() => OnSelectAdmin(player, admin, main_menu));
                }
            );

            // Поставить дизлайк
            menu.AddMenuOption(
                MenuUtils.GenerateOptionId($"admin_dislike_{admin.SteamId}"),
                Localizer["menu.select.admin.dislike"],
                async (p, opt) =>
                {
                    await _dataBaseService!.SetReputationAsync(player, admin, false);
                    Server.NextFrame(() => OnSelectAdmin(player, admin, main_menu));
                }
            );

            // Отправить сообщение
            menu.AddMenuOption(
                MenuUtils.GenerateOptionId($"admin_send_message_{admin.SteamId}"),
                Localizer["menu.select.admin.send.message"],
                (p, opt) =>
                {
                    SendMessage(player, admin);
                }
            );
        }
        menu.Open(player);
    }

    public void SendMessage(CCSPlayerController player, Admin admin)
    {
        if (player.AuthorizedSteamID == null)
        {
            return;
        }

        Api.HookNextPlayerMessage(player, input =>
        {
            Api.RemoveNextPlayerMessageHook(player);
            if (input.ToLower() is "cancel" or "отмена")
            {
                AdminUtils.Print(player, Localizer["message.canceled"]);
                Api.RemoveNextPlayerMessageHook(player);
                return;
            }

            if (!player.IsValid || admin.Controller == null || !admin.Controller.IsValid)
            {
                Api.RemoveNextPlayerMessageHook(player);
                return;
            }

            AdminUtils.Print(player, Localizer["message.sent"]);
            if (admin.Controller != null)
            {
                PlayersUtils.HtmlMessage(admin.Controller, Localizer["message.admin.notify", player.PlayerName], 3.5f);
                AdminUtils.Print(admin.Controller, Localizer["message.text", player.PlayerName, input.Replace("{", "{{").Replace("}", "}}")]);
            }
        });

        AdminUtils.Print(player, Localizer["message.input"]);
    }

    public void SetContact(CCSPlayerController player, Admin admin, IDynamicMenu? main_menu)
    {
        if (player.AuthorizedSteamID == null)
        {
            return;
        }

        ulong steamId = player.AuthorizedSteamID.SteamId64;

        Api.HookNextPlayerMessage(player, async input =>
        {
            if (input.ToLower() is "cancel" or "отмена")
            {
                Server.NextFrame(() =>
                {
                    AdminUtils.Print(player, Localizer["contact.canceled"]);
                    Api.RemoveNextPlayerMessageHook(player);
                });
                return;
            }

            if (!player.IsValid)
            {
                Server.NextFrame(() => Api.RemoveNextPlayerMessageHook(player));
                return;
            }

            AdminUtils.Print(player, Localizer["contact.set"]);
            await _dataBaseService!.SetContactAsync(steamId, input);
            Server.NextFrame(() =>
            {
                Api.RemoveNextPlayerMessageHook(player);
                if (player.IsValid)
                {
                    OnSelectAdmin(player, admin, main_menu);
                }
            });
        });
        AdminUtils.Print(player, Localizer["contact.input"]);
    }
}