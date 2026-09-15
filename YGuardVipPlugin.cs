using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace YGuardVIP;

public class YGuardVipPlugin : BasePlugin, IPluginConfig<YGuardVipConfig>
{
    public override string ModuleName => "YGuard VIP";
    public override string ModuleVersion => "1.0.5";
    public override string ModuleAuthor => "YGuard";
    public override string ModuleDescription => "VIP settings, free guns, smoke, healthshot, votekick";

    public YGuardVipConfig Config { get; set; } = new();

    private PlayerStore _store = null!;
    private readonly HashSet<int> _usedGunsThisRound = [];
    private readonly Dictionary<int, string> _originalClan = [];
    private int _roundNumber;
    private bool _resetRoundOnNextStart;

    // Vote kick
    private bool _voteActive;
    private ulong _voteTargetSteam;
    private string _voteTargetName = "";
    private readonly HashSet<ulong> _voteYes = [];
    private readonly HashSet<ulong> _voteNo = [];
    private DateTime _lastVoteKickUtc = DateTime.MinValue;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _voteTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _voteHudTimer;

    public void OnConfigParsed(YGuardVipConfig config) => Config = config;

    public override void Load(bool hotReload)
    {
        _store = new PlayerStore(Path.Combine(ModuleDirectory, "player_settings.json"));

        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _roundNumber = 0;
            _resetRoundOnNextStart = false;
            _usedGunsThisRound.Clear();
            CancelVoteKick(silent: true);
        });

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundAnnounceWarmup>((_, _) =>
        {
            _roundNumber = 0;
            _resetRoundOnNextStart = false;
            return HookResult.Continue;
        });

        // Side swap / half-time → next live round counts as round 1 (no free guns)
        RegisterEventHandler<EventAnnouncePhaseEnd>((_, _) =>
        {
            _resetRoundOnNextStart = true;
            Logger.LogInformation("Half/side change queued (AnnouncePhaseEnd)");
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundAnnounceLastRoundHalf>((_, _) =>
        {
            _resetRoundOnNextStart = true;
            Logger.LogInformation("Half/side change queued (LastRoundHalf)");
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundAnnounceMatchStart>((_, _) =>
        {
            _roundNumber = 0;
            _resetRoundOnNextStart = false;
            _usedGunsThisRound.Clear();
            return HookResult.Continue;
        });

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

    #region Vote Kick (menu for every player)

    private void OpenVoteKickMenu(CCSPlayerController starter)
    {
        if (!Config.VoteKickEnabled)
        {
            Msg(starter, $"{ChatColors.Red}Vote kick disabled.");
            return;
        }

        if (_voteActive)
        {
            Msg(starter, $"{ChatColors.Red}A vote kick is already running.");
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
        _voteYes.Add(starter.SteamID); // starter counts as YES
        _lastVoteKickUtc = DateTime.UtcNow;

        Broadcast($"{ChatColors.Orange}VOTE KICK: {ChatColors.Red}{targetName}");
        Broadcast($"{ChatColors.Grey}Started by {starter.PlayerName} — vote in the menu or type {ChatColors.Lime}!yes {ChatColors.Grey}/ {ChatColors.Red}!no");
        Broadcast($"{ChatColors.Grey}Need {VotesNeeded()} YES · {Config.VoteKickDurationSeconds}s");

        // Open a Yes/No ballot for EVERY player (except the target)
        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid || p.IsBot || p.IsHLTV || p.SteamID == 0)
                continue;
            if (p.SteamID == targetSteam)
                continue;

            OpenVoteBallot(p, targetName);
        }

        PushVoteHud();

        _voteTimer?.Kill();
        _voteTimer = AddTimer(Config.VoteKickDurationSeconds, () => FinishVoteKick());

        _voteHudTimer?.Kill();
        _voteHudTimer = AddTimer(1.0f, () =>
        {
            if (!_voteActive)
            {
                _voteHudTimer?.Kill();
                _voteHudTimer = null;
                return;
            }

            PushVoteHud();
        }, TimerFlags.REPEAT);

        // Early finish if already enough yes (only starter online etc.)
        if (_voteYes.Count >= VotesNeeded())
            FinishVoteKick();
    }

    private void OpenVoteBallot(CCSPlayerController voter, string targetName)
    {
        try
        {
            var menu = new CenterHtmlMenu($"Kick {targetName}?");
            menu.AddMenuOption("YES — Kick", (p, _) => CastVote(p, yes: true));
            menu.AddMenuOption("NO — Keep", (p, _) => CastVote(p, yes: false));
            menu.PostSelectAction = PostSelectAction.Close;
            MenuManager.OpenCenterHtmlMenu(this, voter, menu);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "OpenVoteBallot failed for {Name}", voter.PlayerName);
            // Fallback chat hint
            Msg(voter, $"{ChatColors.Orange}Vote kick {_voteTargetName}: type {ChatColors.Lime}!yes {ChatColors.Grey}or {ChatColors.Red}!no");
        }
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
        Broadcast($"{ChatColors.Grey}{_voteTargetName}: {ChatColors.Lime}YES {_voteYes.Count}{ChatColors.Grey}/{VotesNeeded()} {ChatColors.Red}NO {_voteNo.Count}");
        PushVoteHud();

        if (_voteYes.Count >= VotesNeeded())
            FinishVoteKick();
    }

    private int VotesNeeded()
    {
        var voters = Utilities.GetPlayers().Count(p =>
            p.IsValid && !p.IsBot && !p.IsHLTV && p.SteamID != 0 && p.SteamID != _voteTargetSteam);
        if (voters < 1) voters = 1;
        return Math.Max(1, (int)Math.Ceiling(voters * Config.VoteKickRatio));
    }

    private void PushVoteHud()
    {
        if (!_voteActive) return;

        var needed = VotesNeeded();
        var html =
            $"<font color='#ffaa00'><b>VOTE KICK</b></font><br>" +
            $"<font color='#ff4444'>{_voteTargetName}</font><br>" +
            $"<font color='#88ff88'>YES {_voteYes.Count}</font> / {needed}  " +
            $"<font color='#ff8888'>NO {_voteNo.Count}</font><br>" +
            $"<font color='#cccccc'>Menu or !yes / !no</font>";

        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid || p.IsBot || p.IsHLTV) continue;
            try { p.PrintToCenterHtml(html); } catch { /* ignore */ }
        }
    }

    private void FinishVoteKick()
    {
        if (!_voteActive)
            return;

        _voteTimer?.Kill();
        _voteTimer = null;
        _voteHudTimer?.Kill();
        _voteHudTimer = null;

        var needed = VotesNeeded();
        var yes = _voteYes.Count;
        var passed = yes >= needed;

        if (passed)
        {
            Broadcast($"{ChatColors.Lime}Vote kick PASSED ({yes}/{needed}) — kicking {_voteTargetName}");
            var target = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.SteamID == _voteTargetSteam);
            if (target != null)
                Server.ExecuteCommand($"kickid {target.UserId} Vote kicked");
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
        _voteHudTimer?.Kill();
        _voteHudTimer = null;
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
            _resetRoundOnNextStart = false;
            return HookResult.Continue;
        }

        // After side swap / half: restart counting from 1
        if (_resetRoundOnNextStart)
        {
            _roundNumber = 0;
            _resetRoundOnNextStart = false;
            Logger.LogInformation("Round counter reset for new half");
        }

        _roundNumber++;

        foreach (var p in Utilities.GetPlayers())
        {
            if (!IsVip(p)) continue;
            ApplyTag(p);
            if (BenefitsAllowed() && p.PawnIsAlive)
                EnforceOneHealthshot(p, giveIfMissing: true);
            else
                EnforceOneHealthshot(p, giveIfMissing: false);
        }

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
                EnforceOneHealthshot(p, giveIfMissing: true);
            else
                EnforceOneHealthshot(p, giveIfMissing: false);
        });

        AddTimer(0.6f, () =>
        {
            if (!p.IsValid || !p.PawnIsAlive)
                return;

            if (BenefitsAllowed())
                EnforceOneHealthshot(p, giveIfMissing: true);
            else
                EnforceOneHealthshot(p, giveIfMissing: false);
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

    private void EnforceOneHealthshot(CCSPlayerController player, bool giveIfMissing)
    {
        try
        {
            if (!player.IsValid || !player.PawnIsAlive)
                return;

            CapHealthshots(player, giveIfMissing);

            var target = player;
            AddTimer(0.2f, () =>
            {
                if (!target.IsValid || !target.PawnIsAlive)
                    return;
                CapHealthshots(target, giveIfMissing);
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "EnforceOneHealthshot failed");
        }
    }

    private static void CapHealthshots(CCSPlayerController player, bool giveIfMissing)
    {
        var count = CountWeapons(player, "weapon_healthshot");

        while (count > 1)
        {
            player.RemoveItemByDesignerName("weapon_healthshot");
            count = CountWeapons(player, "weapon_healthshot");
        }

        if (count == 0 && giveIfMissing)
            player.GiveNamedItem("weapon_healthshot");
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
                var smoke = new CSmokeGrenadeProjectile(handle);
                if (!smoke.IsValid) return;

                var throwerEnt = smoke.Thrower.Value?.Controller.Value;
                if (throwerEnt is not CCSPlayerController thrower || !thrower.IsValid || !IsVip(thrower))
                    return;

                var settings = SettingsOf(thrower);
                if (settings.SmokeColor.Equals("off", StringComparison.OrdinalIgnoreCase))
                    return;

                if (!Config.SmokeColors.TryGetValue(settings.SmokeColor, out var rgb) || rgb.Length < 3)
                    return;

                smoke.SmokeColor.X = rgb[0];
                smoke.SmokeColor.Y = rgb[1];
                smoke.SmokeColor.Z = rgb[2];
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Smoke color failed");
            }
        });
    }

    #endregion
}
