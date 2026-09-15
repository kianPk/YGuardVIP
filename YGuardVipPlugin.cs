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
    public override string ModuleVersion => "1.0.3";
    public override string ModuleAuthor => "YGuard";
    public override string ModuleDescription => "VIP settings, free guns, smoke, healthshot, votekick";

    public YGuardVipConfig Config { get; set; } = new();

    private PlayerStore _store = null!;
    private readonly HashSet<int> _usedGunsThisRound = [];
    private readonly Dictionary<int, string> _originalClan = [];
    private int _roundNumber;

    // Vote kick state
    private bool _voteActive;
    private ulong _voteTargetSteam;
    private string _voteTargetName = "";
    private readonly HashSet<ulong> _voteYes = [];
    private readonly HashSet<ulong> _voteNo = [];
    private DateTime _lastVoteKickUtc = DateTime.MinValue;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _voteTimer;

    public void OnConfigParsed(YGuardVipConfig config) => Config = config;

    public override void Load(bool hotReload)
    {
        _store = new PlayerStore(Path.Combine(ModuleDirectory, "player_settings.json"));

        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _roundNumber = 0;
            _usedGunsThisRound.Clear();
            CancelVoteKick(silent: true);
        });

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundAnnounceWarmup>((_, _) =>
        {
            _roundNumber = 0;
            return HookResult.Continue;
        });

        // Only hide VIP command text from public chat — do not rewrite normal chat
        AddCommandListener("say", HideVipCommandChat, HookMode.Pre);
        AddCommandListener("say_team", HideVipCommandChat, HookMode.Pre);

        Logger.LogInformation("YGuard VIP {Version} loaded", ModuleVersion);
    }

    public override void Unload(bool hotReload)
    {
        try { _store.Save(); } catch { /* ignore */ }
        CancelVoteKick(silent: true);
    }

    private bool IsWarmup()
    {
        try
        {
            var gamerules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
            return gamerules?.GameRules?.WarmupPeriod == true;
        }
        catch
        {
            return false;
        }
    }

    private bool BenefitsAllowed()
        => !IsWarmup() && _roundNumber >= Config.MinRoundForGunsAndHealthshot;

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

    private void Broadcast(string message)
    {
        try { Server.PrintToChatAll($"{Config.ChatPrefix} {message}"); } catch { /* ignore */ }
    }

    #region Silent commands in chat

    private HookResult HideVipCommandChat(CCSPlayerController? player, CommandInfo info)
    {
        // IMPORTANT: returning Handled on say Pre blocks CSS from running css_* commands.
        // So we run the VIP action ourselves, then hide the chat line.
        try
        {
            if (player == null || !player.IsValid)
                return HookResult.Continue;

            var raw = info.ArgString?.Trim() ?? "";
            if (raw.Length == 0)
                return HookResult.Continue;

            if (raw.StartsWith('"') && raw.EndsWith('"') && raw.Length >= 2)
                raw = raw[1..^1].Trim();

            if (!(raw.StartsWith('!') || raw.StartsWith('/') || raw.StartsWith('.')))
                return HookResult.Continue;

            var parts = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var key = parts[0].ToLowerInvariant().TrimStart('!', '/', '.');

            switch (key)
            {
                case "vip":
                case "css_vip":
                    RunVip(player);
                    return HookResult.Handled;
                case "g":
                case "guns":
                case "css_g":
                    RunGuns(player);
                    return HookResult.Handled;
                case "votekick":
                case "vk":
                case "css_votekick":
                case "css_vk":
                    RunVoteKick(player);
                    return HookResult.Handled;
                case "yes":
                case "css_yes":
                    CastVote(player, yes: true);
                    return HookResult.Handled;
                case "no":
                case "css_no":
                    CastVote(player, yes: false);
                    return HookResult.Handled;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "HideVipCommandChat failed");
        }

        return HookResult.Continue;
    }

    private void RunVip(CCSPlayerController player)
    {
        if (!IsVip(player))
        {
            Msg(player, $"{ChatColors.Red}You are not VIP.");
            return;
        }

        OpenVipMenu(player);
    }

    private void RunGuns(CCSPlayerController player)
    {
        if (!IsVip(player))
        {
            Msg(player, $"{ChatColors.Red}You are not VIP.");
            return;
        }

        OpenGunsMenu(player);
    }

    private void RunVoteKick(CCSPlayerController player)
    {
        if (!IsVip(player))
        {
            Msg(player, $"{ChatColors.Red}You are not VIP.");
            return;
        }

        OpenVoteKickMenu(player);
    }

    #endregion

    #region Commands

    [ConsoleCommand("css_vip", "Open VIP settings")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdVip(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null || !player.IsValid) return;
        RunVip(player);
    }

    [ConsoleCommand("css_g", "Open free VIP guns menu")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdGuns(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null || !player.IsValid) return;
        RunGuns(player);
    }

    [ConsoleCommand("css_votekick", "VIP vote kick a player")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdVoteKick(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null || !player.IsValid) return;
        RunVoteKick(player);
    }

    [ConsoleCommand("css_vk", "Alias for votekick")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdVk(CCSPlayerController? player, CommandInfo info) => CmdVoteKick(player, info);

    [ConsoleCommand("css_yes", "Vote yes on kick")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdYes(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null || !player.IsValid) return;
        CastVote(player, yes: true);
    }

    [ConsoleCommand("css_no", "Vote no on kick")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdNo(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null || !player.IsValid) return;
        CastVote(player, yes: false);
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
            info.ReplyToCommand($"Granted {Config.VipPermission} to {steamId}");
        }
        else
        {
            info.ReplyToCommand($"Offline. Add flags [\"{Config.VipPermission}\"] in admins.json / SimpleAdmin.");
        }
    }

    #endregion

    #region Menus

    private void OpenVipMenu(CCSPlayerController player)
    {
        var settings = SettingsOf(player);
        var menu = new ChatMenu($"{Config.ChatPrefix} Settings");

        menu.AddMenuOption($"VIP Tag: {(settings.TagEnabled ? "ON" : "OFF")}", (p, _) =>
        {
            settings.TagEnabled = !settings.TagEnabled;
            _store.Save();
            ApplyTag(p);
            Msg(p, $"VIP Tag: {(settings.TagEnabled ? $"{ChatColors.Lime}ON" : $"{ChatColors.Red}OFF")}");
            OpenVipMenu(p);
        });

        menu.AddMenuOption($"Smoke Color: {settings.SmokeColor}", (p, _) => OpenSmokeMenu(p));
        menu.AddMenuOption("Free Guns (!g)", (p, _) => OpenGunsMenu(p));

        if (Config.VoteKickEnabled)
            menu.AddMenuOption("Vote Kick player", (p, _) => OpenVoteKickMenu(p));

        menu.PostSelectAction = PostSelectAction.Close;
        MenuManager.OpenChatMenu(player, menu);
    }

    private void OpenSmokeMenu(CCSPlayerController player)
    {
        var settings = SettingsOf(player);
        var menu = new ChatMenu("Smoke Color");

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
        MenuManager.OpenChatMenu(player, menu);
    }

    private void OpenGunsMenu(CCSPlayerController player)
    {
        if (!player.PawnIsAlive)
        {
            Msg(player, $"{ChatColors.Red}You must be alive.");
            return;
        }

        if (!BenefitsAllowed())
        {
            Msg(player, $"{ChatColors.Red}Free guns from round {Config.MinRoundForGunsAndHealthshot}+ (now round {_roundNumber}).");
            return;
        }

        if (Config.GunsOncePerRound && _usedGunsThisRound.Contains(player.Slot))
        {
            Msg(player, $"{ChatColors.Red}Already used free gun this round.");
            return;
        }

        var menu = new ChatMenu("Free Guns");
        foreach (var gun in Config.Guns)
        {
            var g = gun;
            menu.AddMenuOption(g.Name, (p, _) => GiveGun(p, g.Weapon, g.Name));
        }

        MenuManager.OpenChatMenu(player, menu);
    }

    private void GiveGun(CCSPlayerController player, string weapon, string displayName)
    {
        try
        {
            if (!IsVip(player) || !player.PawnIsAlive)
                return;

            if (!BenefitsAllowed())
            {
                Msg(player, $"{ChatColors.Red}Free guns from round {Config.MinRoundForGunsAndHealthshot}+.");
                return;
            }

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

    #region Vote Kick

    private void OpenVoteKickMenu(CCSPlayerController starter)
    {
        if (!Config.VoteKickEnabled)
        {
            Msg(starter, $"{ChatColors.Red}Vote kick disabled.");
            return;
        }

        if (_voteActive)
        {
            Msg(starter, $"{ChatColors.Red}A vote kick is already running. Use !yes / !no");
            return;
        }

        var since = (DateTime.UtcNow - _lastVoteKickUtc).TotalSeconds;
        if (since < Config.VoteKickCooldownSeconds)
        {
            Msg(starter, $"{ChatColors.Red}Cooldown: {(int)(Config.VoteKickCooldownSeconds - since)}s");
            return;
        }

        var menu = new ChatMenu("Vote Kick — select player");
        var any = false;

        foreach (var p in Utilities.GetPlayers().OrderBy(x => x.PlayerName))
        {
            if (!p.IsValid || p.IsBot || p.IsHLTV || p.SteamID == 0)
                continue;
            if (p.SteamID == starter.SteamID)
                continue;
            // Don't allow kicking root admins
            if (AdminManager.PlayerHasPermissions(p, Config.AdminPermission))
                continue;

            any = true;
            var target = p;
            var name = target.PlayerName;
            var sid = target.SteamID;
            menu.AddMenuOption(name, (voter, _) => StartVoteKick(voter, sid, name));
        }

        if (!any)
        {
            Msg(starter, $"{ChatColors.Red}No kickable players online.");
            return;
        }

        MenuManager.OpenChatMenu(starter, menu);
    }

    private void StartVoteKick(CCSPlayerController starter, ulong targetSteam, string targetName)
    {
        if (!IsVip(starter) || _voteActive)
            return;

        _voteActive = true;
        _voteTargetSteam = targetSteam;
        _voteTargetName = targetName;
        _voteYes.Clear();
        _voteNo.Clear();
        _voteYes.Add(starter.SteamID); // starter counts as yes
        _lastVoteKickUtc = DateTime.UtcNow;

        Broadcast($"{ChatColors.Orange}Vote kick started by {starter.PlayerName} → {ChatColors.Red}{targetName}");
        Broadcast($"{ChatColors.Grey}Type {ChatColors.Lime}!yes {ChatColors.Grey}or {ChatColors.Red}!no {ChatColors.Grey}({Config.VoteKickDurationSeconds}s)");

        AnnounceVoteStatus();

        _voteTimer?.Kill();
        _voteTimer = AddTimer(Config.VoteKickDurationSeconds, () => FinishVoteKick());
    }

    private void CastVote(CCSPlayerController player, bool yes)
    {
        if (!_voteActive)
        {
            Msg(player, $"{ChatColors.Red}No active vote kick.");
            return;
        }

        if (player.SteamID == _voteTargetSteam)
        {
            Msg(player, $"{ChatColors.Red}You cannot vote on your own kick.");
            return;
        }

        _voteYes.Remove(player.SteamID);
        _voteNo.Remove(player.SteamID);
        if (yes) _voteYes.Add(player.SteamID);
        else _voteNo.Add(player.SteamID);

        Msg(player, yes ? $"{ChatColors.Lime}Voted YES" : $"{ChatColors.Red}Voted NO");
        AnnounceVoteStatus();

        // Early success
        var needed = VotesNeeded();
        if (_voteYes.Count >= needed)
            FinishVoteKick();
    }

    private int VotesNeeded()
    {
        var voters = Utilities.GetPlayers().Count(p =>
            p.IsValid && !p.IsBot && !p.IsHLTV && p.SteamID != 0 && p.SteamID != _voteTargetSteam);
        if (voters < 1) voters = 1;
        return Math.Max(1, (int)Math.Ceiling(voters * Config.VoteKickRatio));
    }

    private void AnnounceVoteStatus()
    {
        Broadcast($"{ChatColors.Grey}Vote kick {_voteTargetName}: {ChatColors.Lime}YES {_voteYes.Count}{ChatColors.Grey}/{VotesNeeded()} {ChatColors.Red}NO {_voteNo.Count}");
    }

    private void FinishVoteKick()
    {
        if (!_voteActive)
            return;

        _voteTimer?.Kill();
        _voteTimer = null;

        var needed = VotesNeeded();
        var yes = _voteYes.Count;
        var passed = yes >= needed;

        if (passed)
        {
            Broadcast($"{ChatColors.Lime}Vote kick PASSED ({yes}/{needed}) — kicking {_voteTargetName}");
            var target = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.SteamID == _voteTargetSteam);
            if (target != null)
            {
                Server.ExecuteCommand($"kickid {target.UserId} Vote kicked by VIP vote");
            }
        }
        else
        {
            Broadcast($"{ChatColors.Red}Vote kick FAILED ({yes}/{needed}) — {_voteTargetName} stays");
        }

        _voteActive = false;
        _voteYes.Clear();
        _voteNo.Clear();
        _voteTargetSteam = 0;
        _voteTargetName = "";
    }

    private void CancelVoteKick(bool silent)
    {
        _voteTimer?.Kill();
        _voteTimer = null;
        if (_voteActive && !silent)
            Broadcast($"{ChatColors.Grey}Vote kick cancelled.");
        _voteActive = false;
        _voteYes.Clear();
        _voteNo.Clear();
        _voteTargetSteam = 0;
        _voteTargetName = "";
    }

    #endregion

    #region Round / Tag / Healthshot

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _usedGunsThisRound.Clear();

        if (IsWarmup())
        {
            _roundNumber = 0;
            return HookResult.Continue;
        }

        _roundNumber++;
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

            if (BenefitsAllowed())
                GiveExactOneHealthshot(p);
        });

        return HookResult.Continue;
    }

    private void ApplyTag(CCSPlayerController player)
    {
        try
        {
            if (!player.IsValid) return;
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
        if (weapons == null) return 0;

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
                if (!projectile.IsValid) return;

                var throwerPawn = projectile.Thrower.Value;
                if (throwerPawn == null || !throwerPawn.IsValid) return;

                var player = throwerPawn.OriginalController.Value;
                if (player == null || !player.IsValid || !IsVip(player)) return;

                var settings = SettingsOf(player);
                if (settings.SmokeColor.Equals("off", StringComparison.OrdinalIgnoreCase)) return;
                if (!Config.SmokeColors.TryGetValue(settings.SmokeColor.ToLowerInvariant(), out var rgb)) return;

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
