using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace YGuardVIP;

public class YGuardVipPlugin : BasePlugin, IPluginConfig<YGuardVipConfig>
{
    public override string ModuleName => "YGuard VIP";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "YGuard";
    public override string ModuleDescription => "VIP settings, free guns, colored smoke, healthshot, chat color";

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

        AddCommandListener("say", OnSay, HookMode.Pre);
        AddCommandListener("say_team", OnSayTeam, HookMode.Pre);

        Logger.LogInformation("YGuard VIP loaded");
    }

    public override void Unload(bool hotReload)
    {
        _store.Save();
    }

    private bool IsVip(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return false;

        return AdminManager.PlayerHasPermissions(player, Config.VipPermission)
               || AdminManager.PlayerHasPermissions(player, "@css/vip")
               || AdminManager.PlayerHasPermissions(player, "@css/root");
    }

    private PlayerVipSettings SettingsOf(CCSPlayerController player)
        => _store.Get(player.SteamID, Config);

    private void Msg(CCSPlayerController? player, string message)
    {
        player?.PrintToChat($"{Config.ChatPrefix} {message}");
    }

    private void Broadcast(string message)
    {
        Server.PrintToChatAll($"{Config.ChatPrefix} {message}");
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

    [ConsoleCommand("css_addvipflag", "Grant @yguard/vip to a SteamID64 (temporary until admins.json save)")]
    [RequiresPermissions("@css/root")]
    [CommandHelper(minArgs: 1, usage: "<steamid64>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdAddVipFlag(CCSPlayerController? player, CommandInfo info)
    {
        if (!ulong.TryParse(info.GetArg(1), out var steamId))
        {
            info.ReplyToCommand("Usage: css_addvipflag <steamid64>");
            return;
        }

        // Runtime grant for currently connected player; also document admins.json
        var target = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.SteamID == steamId);
        if (target != null)
        {
            AdminManager.AddPlayerPermissions(target, Config.VipPermission);
            ApplyTag(target);
            GiveExactOneHealthshot(target);
            Msg(target, $"{ChatColors.Lime}VIP enabled.");
            info.ReplyToCommand($"Granted {Config.VipPermission} to online player {steamId}");
        }
        else
        {
            info.ReplyToCommand($"Player {steamId} is offline. Add permission in admins.json / SimpleAdmin:");
            info.ReplyToCommand($"  \"{steamId}\": {{ \"identity\": \"{steamId}\", \"flags\": [\"{Config.VipPermission}\"] }}");
        }
    }

    #endregion

    #region Menus

    private void OpenVipMenu(CCSPlayerController player)
    {
        var settings = SettingsOf(player);
        var menu = new CenterHtmlMenu($"{Config.ChatPrefix} Settings", this);

        menu.AddMenuOption($"VIP Tag: {(settings.TagEnabled ? "ON" : "OFF")}", (p, _) =>
        {
            settings.TagEnabled = !settings.TagEnabled;
            _store.Save();
            ApplyTag(p);
            Msg(p, $"VIP Tag: {(settings.TagEnabled ? $"{ChatColors.Lime}ON" : $"{ChatColors.Red}OFF")}");
            OpenVipMenu(p);
        });

        menu.AddMenuOption($"Smoke Color: {settings.SmokeColor}", (p, _) =>
        {
            OpenSmokeMenu(p);
        });

        menu.AddMenuOption("Free Guns (/g)", (p, _) =>
        {
            OpenGunsMenu(p);
        });

        menu.Open(player);
    }

    private void OpenSmokeMenu(CCSPlayerController player)
    {
        var settings = SettingsOf(player);
        var menu = new CenterHtmlMenu("Smoke Color", this);

        foreach (var key in Config.SmokeColors.Keys)
        {
            var colorKey = key;
            var mark = settings.SmokeColor.Equals(colorKey, StringComparison.OrdinalIgnoreCase) ? " ✓" : "";
            menu.AddMenuOption($"{colorKey}{mark}", (p, _) =>
            {
                SettingsOf(p).SmokeColor = colorKey;
                _store.Save();
                Msg(p, $"Smoke color set to {ChatColors.Lime}{colorKey}");
                OpenVipMenu(p);
            });
        }

        menu.AddMenuOption("← Back", (p, _) => OpenVipMenu(p));
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
            Msg(player, $"{ChatColors.Red}You already took a free gun this round.");
            return;
        }

        var menu = new CenterHtmlMenu("Free Guns", this);
        foreach (var gun in Config.Guns)
        {
            var g = gun;
            menu.AddMenuOption(g.Name, (p, _) =>
            {
                GiveGun(p, g.Weapon, g.Name);
            });
        }

        menu.Open(player);
    }

    private void GiveGun(CCSPlayerController player, string weapon, string displayName)
    {
        if (!IsVip(player) || !player.PawnIsAlive)
            return;

        if (Config.GunsOncePerRound && _usedGunsThisRound.Contains(player.Slot))
        {
            Msg(player, $"{ChatColors.Red}You already took a free gun this round.");
            return;
        }

        // Drop current weapon in same slot category for rifles/pistols roughly by giving
        player.GiveNamedItem(weapon);
        _usedGunsThisRound.Add(player.Slot);
        Msg(player, $"{ChatColors.Lime}Received {displayName}");
    }

    #endregion

    #region Tag / Healthshot / Round

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _usedGunsThisRound.Clear();

        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsVip(player))
                continue;

            ApplyTag(player);
            // Healthshot on spawn event is more reliable; also do here as backup next frame
            var p = player;
            Server.NextFrame(() =>
            {
                if (p.IsValid && p.PawnIsAlive)
                    GiveExactOneHealthshot(p);
            });
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !IsVip(player))
            return HookResult.Continue;

        Server.NextFrame(() =>
        {
            if (!player.IsValid || !player.PawnIsAlive)
                return;

            ApplyTag(player);
            GiveExactOneHealthshot(player);
        });

        return HookResult.Continue;
    }

    private void ApplyTag(CCSPlayerController player)
    {
        if (!player.IsValid)
            return;

        var settings = SettingsOf(player);
        if (!_originalClan.ContainsKey(player.Slot))
            _originalClan[player.Slot] = player.Clan ?? "";

        if (settings.TagEnabled)
            player.Clan = Config.VipTagText;
        else
            player.Clan = _originalClan.GetValueOrDefault(player.Slot, "");

        // Force scoreboard refresh
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
    }

    private void GiveExactOneHealthshot(CCSPlayerController player)
    {
        if (!player.IsValid || player.PlayerPawn.Value is not { } pawn)
            return;

        RemoveWeapon(player, "weapon_healthshot");

        var target = player;
        Server.NextFrame(() =>
        {
            if (!target.IsValid || !target.PawnIsAlive)
                return;

            // If somehow still present, strip again then give exactly one
            RemoveWeapon(target, "weapon_healthshot");
            target.GiveNamedItem("weapon_healthshot");
        });
    }

    private static void RemoveWeapon(CCSPlayerController player, string designerName)
    {
        if (player.PlayerPawn.Value?.WeaponServices?.MyWeapons == null)
            return;

        foreach (var handle in player.PlayerPawn.Value.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;

            if (!weapon.DesignerName.Equals(designerName, StringComparison.OrdinalIgnoreCase))
                continue;

            weapon.AcceptInput("Kill");
        }
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
        if (entity.DesignerName != "smokegrenade_projectile")
            return;

        var projectile = entity.As<CSmokeGrenadeProjectile>();
        Server.NextFrame(() =>
        {
            try
            {
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
                Logger.LogWarning(ex, "Failed to color smoke");
            }
        });
    }

    #endregion

    #region Chat color

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
        => HandleChat(player, info, teamOnly: false);

    private HookResult OnSayTeam(CCSPlayerController? player, CommandInfo info)
        => HandleChat(player, info, teamOnly: true);

    private HookResult HandleChat(CCSPlayerController? player, CommandInfo info, bool teamOnly)
    {
        if (player == null || !player.IsValid || !IsVip(player))
            return HookResult.Continue;

        var settings = SettingsOf(player);
        if (!settings.TagEnabled)
            return HookResult.Continue;

        var message = info.ArgString?.Trim() ?? "";
        if (message.Length == 0)
            return HookResult.Continue;

        // Strip surrounding quotes if present
        if (message.StartsWith('"') && message.EndsWith('"') && message.Length >= 2)
            message = message[1..^1];

        // Don't intercept commands
        if (message.StartsWith('!') || message.StartsWith('/') || message.StartsWith('.'))
            return HookResult.Continue;

        var color = ResolveChatColor(Config.VipChatColor);
        var teamTag = teamOnly ? $"{ChatColors.Grey}(TEAM) " : "";
        var vipTag = settings.TagEnabled ? $"{ChatColors.Green}[{Config.VipTagText}] " : "";

        var line = $"{teamTag}{vipTag}{color}{player.PlayerName}{ChatColors.Default}: {message}";

        if (teamOnly)
        {
            var team = player.Team;
            foreach (var p in Utilities.GetPlayers())
            {
                if (p.IsValid && p.Team == team)
                    p.PrintToChat(line);
            }
        }
        else
        {
            Server.PrintToChatAll(line);
        }

        return HookResult.Handled;
    }

    private static char ResolveChatColor(string name) => name.ToLowerInvariant() switch
    {
        "red" => ChatColors.Red,
        "green" => ChatColors.Green,
        "blue" => ChatColors.Blue,
        "yellow" => ChatColors.Yellow,
        "orange" => ChatColors.Orange,
        "purple" => ChatColors.Purple,
        "lime" => ChatColors.Lime,
        "lightblue" => ChatColors.LightBlue,
        "gold" => ChatColors.Gold,
        "olive" => ChatColors.Olive,
        "pink" => ChatColors.LightRed,
        _ => ChatColors.Gold
    };

    #endregion
}
