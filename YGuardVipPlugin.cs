using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace YGuardVIP;

public class YGuardVipPlugin : BasePlugin, IPluginConfig<YGuardVipConfig>
{
    public override string ModuleName => "YGuard VIP";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "YGuard";
    public override string ModuleDescription => "VIP settings, free guns, colored smoke, healthshot";

    public YGuardVipConfig Config { get; set; } = new();

    private PlayerStore _store = null!;
    private readonly HashSet<int> _usedGunsThisRound = [];
    private readonly Dictionary<int, string> _originalClan = [];

    public void OnConfigParsed(YGuardVipConfig config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        _store = new PlayerStore(Path.Combine(ModuleDirectory, "player_settings.json"));

        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);

        // NOTE: say/say_team Pre hooks removed in 1.0.1 — they caused server crashes.
        // VIP is shown via clan tag; chat color can return later with a safer method.

        Logger.LogInformation("YGuard VIP {Version} loaded", ModuleVersion);
    }

    public override void Unload(bool hotReload)
    {
        try { _store.Save(); } catch { /* ignore */ }
    }

    private bool IsVip(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return false;

        try
        {
            return AdminManager.PlayerHasPermissions(player, Config.VipPermission)
                   || AdminManager.PlayerHasPermissions(player, "@css/vip");
        }
        catch
        {
            return false;
        }
    }

    private PlayerVipSettings SettingsOf(CCSPlayerController player)
        => _store.Get(player.SteamID, Config);

    private void Msg(CCSPlayerController? player, string message)
    {
        try { player?.PrintToChat($"{Config.ChatPrefix} {message}"); } catch { /* ignore */ }
    }

    #region Commands

    [ConsoleCommand("css_vip", "Open VIP settings")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdVip(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null || !player.IsValid)
            return;

        if (!IsVip(player))
        {
            Msg(player, $"{ChatColors.Red}You are not VIP.");
            return;
        }

        OpenVipMenu(player);
    }

    [ConsoleCommand("css_g", "Open free VIP guns menu")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdGuns(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null || !player.IsValid)
            return;

        if (!IsVip(player))
        {
            Msg(player, $"{ChatColors.Red}You are not VIP.");
            return;
        }

        OpenGunsMenu(player);
    }

    [ConsoleCommand("css_addvipflag", "Grant @yguard/vip to an online SteamID64")]
    [RequiresPermissions("@css/root")]
    [CommandHelper(minArgs: 1, usage: "<steamid64>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdAddVipFlag(CCSPlayerController? player, CommandInfo info)
    {
        if (!ulong.TryParse(info.GetArg(1), out var steamId))
        {
            info.ReplyToCommand("Usage: css_addvipflag <steamid64>");
            return;
        }

        var target = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.SteamID == steamId);
        if (target != null)
        {
            AdminManager.AddPlayerPermissions(target, Config.VipPermission);
            ApplyTag(target);
            Msg(target, $"{ChatColors.Lime}VIP enabled.");
            info.ReplyToCommand($"Granted {Config.VipPermission} to online player {steamId}");
        }
        else
        {
            info.ReplyToCommand($"Player {steamId} offline. Add in admins.json / SimpleAdmin: flags [\"{Config.VipPermission}\"]");
        }
    }

    #endregion

    #region Menus

    private void OpenVipMenu(CCSPlayerController player)
    {
        var settings = SettingsOf(player);
        var menu = new CenterHtmlMenu("YGuard VIP Settings", this);

        menu.AddMenuOption($"VIP Tag: {(settings.TagEnabled ? "ON" : "OFF")}", (p, _) =>
        {
            settings.TagEnabled = !settings.TagEnabled;
            _store.Save();
            ApplyTag(p);
            Msg(p, $"VIP Tag: {(settings.TagEnabled ? $"{ChatColors.Lime}ON" : $"{ChatColors.Red}OFF")}");
            OpenVipMenu(p);
        });

        menu.AddMenuOption($"Smoke Color: {settings.SmokeColor}", (p, _) => OpenSmokeMenu(p));
        menu.AddMenuOption("Free Guns (/g)", (p, _) => OpenGunsMenu(p));
        menu.Open(player);
    }

    private void OpenSmokeMenu(CCSPlayerController player)
    {
        var settings = SettingsOf(player);
        var menu = new CenterHtmlMenu("Smoke Color", this);

        foreach (var key in Config.SmokeColors.Keys)
        {
            var colorKey = key;
            var mark = settings.SmokeColor.Equals(colorKey, StringComparison.OrdinalIgnoreCase) ? " *" : "";
            menu.AddMenuOption($"{colorKey}{mark}", (p, _) =>
            {
                SettingsOf(p).SmokeColor = colorKey;
                _store.Save();
                Msg(p, $"Smoke color: {ChatColors.Lime}{colorKey}");
                OpenVipMenu(p);
            });
        }

        menu.AddMenuOption("<< Back", (p, _) => OpenVipMenu(p));
        menu.Open(player);
    }

    private void OpenGunsMenu(CCSPlayerController player)
    {
        if (!player.PawnIsAlive)
        {
            Msg(player, $"{ChatColors.Red}You must be alive.");
            return;
        }

        if (Config.GunsOncePerRound && _usedGunsThisRound.Contains(player.Slot))
        {
            Msg(player, $"{ChatColors.Red}Already used free gun this round.");
            return;
        }

        var menu = new CenterHtmlMenu("Free Guns", this);
        foreach (var gun in Config.Guns)
        {
            var g = gun;
            menu.AddMenuOption(g.Name, (p, _) => GiveGun(p, g.Weapon, g.Name));
        }

        menu.Open(player);
    }

    private void GiveGun(CCSPlayerController player, string weapon, string displayName)
    {
        try
        {
            if (!IsVip(player) || !player.PawnIsAlive)
                return;

            if (Config.GunsOncePerRound && _usedGunsThisRound.Contains(player.Slot))
            {
                Msg(player, $"{ChatColors.Red}Already used free gun this round.");
                return;
            }

            player.GiveNamedItem(weapon);
            _usedGunsThisRound.Add(player.Slot);
            Msg(player, $"{ChatColors.Lime}Received {displayName}");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "GiveGun failed");
        }
    }

    #endregion

    #region Tag / Healthshot / Round

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _usedGunsThisRound.Clear();
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !IsVip(player))
            return HookResult.Continue;

        var p = player;
        AddTimer(0.15f, () =>
        {
            if (!p.IsValid || !p.PawnIsAlive)
                return;

            ApplyTag(p);
            GiveExactOneHealthshot(p);
        });

        return HookResult.Continue;
    }

    private void ApplyTag(CCSPlayerController player)
    {
        try
        {
            if (!player.IsValid)
                return;

            var settings = SettingsOf(player);
            if (!_originalClan.ContainsKey(player.Slot))
                _originalClan[player.Slot] = player.Clan ?? "";

            player.Clan = settings.TagEnabled ? Config.VipTagText : _originalClan.GetValueOrDefault(player.Slot, "");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ApplyTag failed");
        }
    }

    private void GiveExactOneHealthshot(CCSPlayerController player)
    {
        try
        {
            if (!player.IsValid || !player.PawnIsAlive)
                return;

            // Safe remove (no AcceptInput Kill — that can crash)
            player.RemoveItemByDesignerName("weapon_healthshot");

            var target = player;
            AddTimer(0.05f, () =>
            {
                if (!target.IsValid || !target.PawnIsAlive)
                    return;

                if (CountWeapons(target, "weapon_healthshot") > 0)
                    return;

                target.GiveNamedItem("weapon_healthshot");
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Healthshot failed");
        }
    }

    private static int CountWeapons(CCSPlayerController player, string designerName)
    {
        var count = 0;
        var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
        if (weapons == null)
            return 0;

        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon != null && weapon.IsValid &&
                weapon.DesignerName.Equals(designerName, StringComparison.OrdinalIgnoreCase))
                count++;
        }

        return count;
    }

    private void OnClientDisconnect(int playerSlot)
    {
        _usedGunsThisRound.Remove(playerSlot);
        _originalClan.Remove(playerSlot);
    }

    #endregion

    #region Smoke

    private void OnEntitySpawned(CEntityInstance entity)
    {
        if (!Config.EnableSmokeColor)
            return;

        if (entity.DesignerName != "smokegrenade_projectile")
            return;

        var handle = entity.Handle;
        Server.NextFrame(() =>
        {
            try
            {
                var projectile = new CSmokeGrenadeProjectile(handle);
                if (!projectile.IsValid)
                    return;

                var throwerPawn = projectile.Thrower.Value;
                if (throwerPawn == null || !throwerPawn.IsValid)
                    return;

                var player = throwerPawn.OriginalController.Value;
                if (player == null || !player.IsValid || !IsVip(player))
                    return;

                var settings = SettingsOf(player);
                if (settings.SmokeColor.Equals("off", StringComparison.OrdinalIgnoreCase))
                    return;

                if (!Config.SmokeColors.TryGetValue(settings.SmokeColor.ToLowerInvariant(), out var rgb))
                    return;

                projectile.SmokeColor.X = rgb[0];
                projectile.SmokeColor.Y = rgb[1];
                projectile.SmokeColor.Z = rgb[2];
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Smoke color failed");
            }
        });
    }

    #endregion
}
