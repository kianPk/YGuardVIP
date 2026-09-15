using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace YGuardVIP;

public class YGuardVipPlugin : BasePlugin, IPluginConfig<YGuardVipConfig>
{
    public override string ModuleName => "YGuard VIP";
    public override string ModuleVersion => "1.1.5";
    public override string ModuleAuthor => "YGuard";
    public override string ModuleDescription => "Timed VIP DB, panel menu, guns, smoke, healthshot, votekick";

    public YGuardVipConfig Config { get; set; } = new();

    private PlayerStore _store = null!;
    private VipDatabase _vipDb = null!;
    private readonly HashSet<int> _usedGunsThisRound = [];
    private readonly Dictionary<int, string> _originalClan = [];
    private readonly Dictionary<int, string> _originalName = [];
    /// <summary>Active VIP menus per player slot — used to silence !1/!3 chat picks.</summary>
    private readonly Dictionary<int, BaseMenu> _openMenus = [];
    /// <summary>Keep silencing number picks for a while after opening a panel.</summary>
    private readonly Dictionary<int, DateTime> _menuSilentUntil = [];
    private readonly HashSet<ulong> _silentChatHandled = [];
    private int _roundNumber;
    private bool _resetRoundOnNextStart;

    private bool _voteActive;
    private ulong _voteTargetSteam;
    private string _voteTargetName = "";
    private readonly HashSet<ulong> _voteYes = [];
    private readonly HashSet<ulong> _voteNo = [];
    private DateTime _lastVoteKickUtc = DateTime.MinValue;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _voteTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _voteHudTimer;

    public void OnConfigParsed(YGuardVipConfig config) => Config = config;

    private string DataDirectory
    {
        get
        {
            // Prefer configs path (survives plugin replace); fall back next to DLL
            foreach (var dir in new[]
                     {
                         SafeJoin(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "configs", "plugins", "YGuardVIP"),
                         Path.Combine(ModuleDirectory, "data")
                     })
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                try
                {
                    Directory.CreateDirectory(dir);
                    return dir;
                }
                catch
                {
                    // try next
                }
            }

            return ModuleDirectory;
        }
    }

    private static string? SafeJoin(string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;
        try { return Path.Combine(new[] { root }.Concat(parts).ToArray()); }
        catch { return null; }
    }

    public override void Load(bool hotReload)
    {
        _store = new PlayerStore(Path.Combine(DataDirectory, "player_settings.json"));
        _vipDb = new VipDatabase(Path.Combine(DataDirectory, "vip_database.json"));

        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _roundNumber = 0;
            _resetRoundOnNextStart = false;
            _usedGunsThisRound.Clear();
            CancelVoteKick(silent: true);
        });

        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundAnnounceWarmup>((_, _) =>
        {
            _roundNumber = 0;
            _resetRoundOnNextStart = false;
            return HookResult.Continue;
        });
        RegisterEventHandler<EventAnnouncePhaseEnd>((_, _) =>
        {
            _resetRoundOnNextStart = true;
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundAnnounceLastRoundHalf>((_, _) =>
        {
            _resetRoundOnNextStart = true;
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

        // say Handled alone does NOT hide chat in CS2 — must DontBroadcast player_chat
        RegisterEventHandler<EventPlayerChat>(OnPlayerChatHide, HookMode.Pre);

        // Expire VIPs + refresh clan tags
        AddTimer(30.0f, () =>
        {
            ProcessExpirations();
            foreach (var p in Utilities.GetPlayers())
            {
                if (!p.IsValid || p.IsBot) continue;
                SyncVipPermission(p);
                if (IsVip(p)) ApplyTag(p);
            }
        }, TimerFlags.REPEAT);

        Logger.LogInformation("YGuard VIP {Version} loaded — DB: {Path}", ModuleVersion,
            Path.Combine(DataDirectory, "vip_database.json"));
    }

    public override void Unload(bool hotReload)
    {
        try { _store.Save(); } catch { /* ignore */ }
        try { _vipDb.Save(); } catch { /* ignore */ }
        CancelVoteKick(silent: true);
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        var p = player;
        AddTimer(1.0f, () =>
        {
            if (!p.IsValid) return;
            SyncVipPermission(p);
            if (IsVip(p))
            {
                ApplyTag(p);
                var left = _vipDb.FormatRemaining(p.SteamID);
                Msg(p, $"{ChatColors.Lime}VIP active{ChatColors.Default} · time left: {ChatColors.Orange}{left}");
            }
        });

        return HookResult.Continue;
    }

    private void ProcessExpirations()
    {
        var expired = _vipDb.PurgeExpired();
        foreach (var g in expired)
        {
            var online = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.SteamID == g.SteamId);
            if (online != null)
            {
                try { AdminManager.RemovePlayerPermissions(online, Config.VipPermission); } catch { /* ignore */ }
                ClearTag(online);
                Msg(online, $"{ChatColors.Red}Your VIP has expired.");
            }

            Logger.LogInformation("VIP expired for {SteamId}", g.SteamId);
        }
    }

    private void SyncVipPermission(CCSPlayerController player)
    {
        try
        {
            // Only ADD from DB. Removals happen on expiry / css_removevip
            // so permanent admins.json / @css/vip flags are not wiped each spawn.
            if (_vipDb.IsActive(player.SteamID))
                AdminManager.AddPlayerPermissions(player, Config.VipPermission);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "SyncVipPermission failed");
        }
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
            if (_vipDb.IsActive(player.SteamID))
                return true;

            return AdminManager.PlayerHasPermissions(player, Config.VipPermission)
                   || AdminManager.PlayerHasPermissions(player, "@css/vip")
                   || AdminManager.PlayerHasPermissions(player, "@css/root");
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
        try
        {
            // Real ChatColors — not literal {green}... text
            player?.PrintToChat($" {ChatColors.Green}YGuard{ChatColors.Default} {message}");
        }
        catch { /* ignore */ }
    }

    private void Broadcast(string message)
    {
        try
        {
            Server.PrintToChatAll($" {ChatColors.Green}YGuard{ChatColors.Default} {message}");
        }
        catch { /* ignore */ }
    }

    /// <summary>Panel feedback — only center screen for that player, never chat (others won't see).</summary>
    private void PanelHint(CCSPlayerController? player, string message)
    {
        try
        {
            player?.PrintToCenter(message);
            player?.PrintToCenterHtml($"<font color='#a0ff90'>{System.Net.WebUtility.HtmlEncode(message)}</font>");
        }
        catch { /* ignore */ }
    }

    private void OpenPanel(CCSPlayerController player, CenterHtmlMenu menu)
    {
        menu.PostSelectAction = PostSelectAction.Close;
        TrackMenu(player, menu);
        MenuManager.OpenCenterHtmlMenu(this, player, menu);
    }

    private void OpenChatPanel(CCSPlayerController player, ChatMenu menu)
    {
        menu.PostSelectAction = PostSelectAction.Close;
        TrackMenu(player, menu);
        MenuManager.OpenChatMenu(player, menu);
    }

    private void TrackMenu(CCSPlayerController player, BaseMenu menu)
    {
        _openMenus[player.Slot] = menu;
        _menuSilentUntil[player.Slot] = DateTime.UtcNow.AddSeconds(90);
    }

    private void ClearTrackedMenu(CCSPlayerController player)
    {
        _openMenus.Remove(player.Slot);
        // keep silence window a bit so trailing number presses stay hidden
        _menuSilentUntil[player.Slot] = DateTime.UtcNow.AddSeconds(8);
    }

    private bool IsMenuSilenceActive(CCSPlayerController player)
        => _openMenus.ContainsKey(player.Slot)
           || (_menuSilentUntil.TryGetValue(player.Slot, out var until) && until > DateTime.UtcNow);

    /// <summary>
    /// Handle menu number picks ourselves and hide them from public chat
    /// (!3, /3, 3, etc.).
    /// </summary>
    private bool TrySelectTrackedMenu(CCSPlayerController player, int oneBasedIndex)
    {
        try
        {
            if (!_openMenus.TryGetValue(player.Slot, out var menu) || menu == null)
                return false;

            if (oneBasedIndex < 1 || oneBasedIndex > menu.MenuOptions.Count)
                return false;

            var option = menu.MenuOptions[oneBasedIndex - 1];
            if (option.Disabled)
                return false;

            if (menu.PostSelectAction == PostSelectAction.Close)
                ClearTrackedMenu(player);

            option.OnSelect?.Invoke(player, option);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "TrySelectTrackedMenu failed");
            return false;
        }
    }

    private static string NormalizeChatRaw(string? raw)
    {
        raw = raw?.Trim() ?? "";
        if (raw.Length == 0) return "";
        if (raw.StartsWith('"') && raw.EndsWith('"') && raw.Length >= 2)
            raw = raw[1..^1].Trim();
        return raw;
    }

    /// <summary>True if this chat line is a VIP cmd or menu pick that must stay private.</summary>
    private bool IsSilentVipText(CCSPlayerController player, string raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;

        // Menu picks while panel open / recently open: 3, !3, /3, .9
        var maybeNum = raw.TrimStart('!', '/', '.').Trim();
        if (int.TryParse(maybeNum, out var choice) && choice >= 1 && choice <= 9
            && raw.Length <= 4
            && (char.IsDigit(raw[0]) || raw[0] is '!' or '/' or '.')
            && IsMenuSilenceActive(player))
            return true;

        if (!(raw.StartsWith('!') || raw.StartsWith('/') || raw.StartsWith('.')))
            return false;

        var key = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0]
            .ToLowerInvariant().TrimStart('!', '/', '.');

        return key is "vip" or "css_vip" or "g" or "guns" or "css_g"
            or "votekick" or "vk" or "css_votekick" or "css_vk"
            or "yes" or "css_yes" or "no" or "css_no"
            or "1" or "2" or "3" or "4" or "5" or "6" or "7" or "8" or "9";
    }

    /// <summary>Run VIP/menu action for silent chat. Returns true if consumed.</summary>
    private bool TryHandleSilentVipText(CCSPlayerController player, string raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;

        var maybeNum = raw.TrimStart('!', '/', '.').Trim();
        if (int.TryParse(maybeNum, out var choice) && choice >= 1 && choice <= 9
            && raw.Length <= 4
            && (char.IsDigit(raw[0]) || raw[0] is '!' or '/' or '.'))
        {
            if (TrySelectTrackedMenu(player, choice))
                return true;
            // Even if select failed, still silence while panel silence window is active
            if (IsMenuSilenceActive(player))
                return true;
        }

        if (!(raw.StartsWith('!') || raw.StartsWith('/') || raw.StartsWith('.')))
            return false;

        var key = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0]
            .ToLowerInvariant().TrimStart('!', '/', '.');

        switch (key)
        {
            case "vip":
            case "css_vip":
                RunVip(player);
                return true;
            case "g":
            case "guns":
            case "css_g":
                RunGuns(player);
                return true;
            case "votekick":
            case "vk":
            case "css_votekick":
            case "css_vk":
                RunVoteKick(player);
                return true;
            case "yes":
            case "css_yes":
                CastVote(player, yes: true);
                return true;
            case "no":
            case "css_no":
                CastVote(player, yes: false);
                return true;
        }

        return false;
    }

    /// <summary>Open menu next tick — opening during say Pre often fails silently.</summary>
    private void DeferOpen(CCSPlayerController player, Action<CCSPlayerController> open)
    {
        var p = player;
        Server.NextFrame(() =>
        {
            if (!p.IsValid) return;
            try { open(p); }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "DeferOpen failed, trying ChatMenu fallback path");
            }
        });
    }

    private void DeferMenu(CCSPlayerController player, Action<CCSPlayerController> open)
    {
        var p = player;
        AddTimer(0.1f, () =>
        {
            if (p.IsValid)
            {
                try { open(p); }
                catch (Exception ex) { Logger.LogWarning(ex, "DeferMenu failed"); }
            }
        });
    }

    #region Silent commands in chat

    private HookResult HideVipCommandChat(CCSPlayerController? player, CommandInfo info)
    {
        try
        {
            if (player == null || !player.IsValid)
                return HookResult.Continue;

            var raw = NormalizeChatRaw(info.ArgString);
            if (!TryHandleSilentVipText(player, raw))
                return HookResult.Continue;

            // Mark so EventPlayerChat doesn't double-run the action
            _silentChatHandled.Add(player.SteamID);
            AddTimer(0.05f, () => _silentChatHandled.Remove(player.SteamID));
            return HookResult.Handled;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "HideVipCommandChat failed");
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// CS2 still broadcasts chat even when say returns Handled.
    /// DontBroadcast on player_chat is what actually hides it from others.
    /// </summary>
    private HookResult OnPlayerChatHide(EventPlayerChat @event, GameEventInfo info)
    {
        try
        {
            var raw = NormalizeChatRaw(@event.Text);
            if (string.IsNullOrEmpty(raw))
                return HookResult.Continue;

            var player = Utilities.GetPlayers()
                .FirstOrDefault(p => p.IsValid && p.UserId == @event.Userid);

            if (player == null || !player.IsValid)
                return HookResult.Continue;

            if (!IsSilentVipText(player, raw))
                return HookResult.Continue;

            info.DontBroadcast = true;

            // If say listener didn't run/handle (some paths only fire player_chat), do action here
            if (!_silentChatHandled.Contains(player.SteamID))
                TryHandleSilentVipText(player, raw);

            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "OnPlayerChatHide failed");
            return HookResult.Continue;
        }
    }

    private void RunVip(CCSPlayerController player)
    {
        try
        {
            SyncVipPermission(player);
            if (!IsVip(player))
            {
                Msg(player, $"{ChatColors.Red}You are not VIP.");
                return;
            }

            // Must not open menu inside say Pre — defer to next frame
            DeferOpen(player, OpenVipMenu);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "RunVip failed");
            Msg(player, $"{ChatColors.Red}VIP menu error — check server log.");
        }
    }

    private void RunGuns(CCSPlayerController player)
    {
        SyncVipPermission(player);
        if (!IsVip(player))
        {
            Msg(player, $"{ChatColors.Red}You are not VIP.");
            return;
        }

        DeferOpen(player, OpenGunsMenu);
    }

    private void RunVoteKick(CCSPlayerController player)
    {
        SyncVipPermission(player);
        if (!IsVip(player))
        {
            Msg(player, $"{ChatColors.Red}You are not VIP.");
            return;
        }

        DeferOpen(player, OpenVoteKickMenu);
    }

    #endregion

    #region Commands

    [ConsoleCommand("css_vip", "Open VIP settings panel")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdVip(CCSPlayerController? player, CommandInfo _)
    {
        if (player == null || !player.IsValid) return;
        RunVip(player);
    }

    [ConsoleCommand("css_g", "Open free VIP guns panel")]
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

    [ConsoleCommand("css_addvip", "Grant timed VIP (saved to DB). Usage: css_addvip <steamid64> <duration>")]
    [RequiresPermissions("@css/root")]
    [CommandHelper(minArgs: 2, usage: "<steamid64> <duration: 7d|12h|30d|perm>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdAddVip(CCSPlayerController? player, CommandInfo info)
    {
        if (!ulong.TryParse(info.GetArg(1), out var steamId))
        {
            info.ReplyToCommand("Usage: css_addvip <steamid64> <duration>   e.g. css_addvip 7656... 7d");
            return;
        }

        if (!VipDatabase.TryParseDuration(info.GetArg(2), out var duration, out var err))
        {
            info.ReplyToCommand(err);
            return;
        }

        var by = player?.PlayerName ?? "CONSOLE";
        var grant = _vipDb.Grant(steamId, duration, by);
        var left = grant.ExpiresAtUtc == null ? "permanent" : _vipDb.FormatRemaining(steamId);

        var target = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.SteamID == steamId);
        if (target != null)
        {
            SyncVipPermission(target);
            ApplyTag(target);
            Msg(target, $"{ChatColors.Lime}VIP activated{ChatColors.Default} · {ChatColors.Orange}{left}");
        }

        info.ReplyToCommand($"VIP granted to {steamId} · {left} (saved to vip_database.json)");
        Logger.LogInformation("VIP granted {SteamId} by {By} for {Left}", steamId, by, left);
    }

    [ConsoleCommand("css_addvipflag", "Alias of css_addvip (requires duration)")]
    [RequiresPermissions("@css/root")]
    [CommandHelper(minArgs: 2, usage: "<steamid64> <duration>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdAddVipFlag(CCSPlayerController? player, CommandInfo info) => CmdAddVip(player, info);

    [ConsoleCommand("css_removevip", "Remove VIP from DB")]
    [RequiresPermissions("@css/root")]
    [CommandHelper(minArgs: 1, usage: "<steamid64>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdRemoveVip(CCSPlayerController? player, CommandInfo info)
    {
        if (!ulong.TryParse(info.GetArg(1), out var steamId))
        {
            info.ReplyToCommand("Usage: css_removevip <steamid64>");
            return;
        }

        var ok = _vipDb.Revoke(steamId);
        var target = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.SteamID == steamId);
        if (target != null)
        {
            try { AdminManager.RemovePlayerPermissions(target, Config.VipPermission); } catch { /* ignore */ }
            ClearTag(target);
            Msg(target, $"{ChatColors.Red}VIP removed.");
        }

        info.ReplyToCommand(ok ? $"VIP removed for {steamId}" : $"No VIP grant found for {steamId}");
    }

    [ConsoleCommand("css_listvip", "List VIP grants in database")]
    [RequiresPermissions("@css/root")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdListVip(CCSPlayerController? player, CommandInfo info)
    {
        var list = _vipDb.ListActive();
        if (list.Count == 0)
        {
            info.ReplyToCommand("No active VIP grants.");
            return;
        }

        info.ReplyToCommand($"Active VIPs ({list.Count}):");
        foreach (var g in list)
        {
            var left = g.ExpiresAtUtc == null ? "permanent" : _vipDb.FormatRemaining(g.SteamId);
            info.ReplyToCommand($"  {g.SteamId} · {left} · by {g.GrantedBy}");
        }
    }

    #endregion

    #region Menus (CenterHtml panel — not chat)

    private void OpenVipMenu(CCSPlayerController player)
    {
        try
        {
            var settings = SettingsOf(player);
            var left = _vipDb.IsActive(player.SteamID) ? _vipDb.FormatRemaining(player.SteamID) : "VIP";
            var menu = new CenterHtmlMenu($"YGuard VIP | {left}", this);

            menu.AddMenuOption($"Tag: {(settings.TagEnabled ? "ON" : "OFF")}", (p, _) =>
            {
                settings.TagEnabled = !settings.TagEnabled;
                _store.Save();
                ApplyTag(p);
                PanelHint(p, $"VIP Tag: {(settings.TagEnabled ? "ON" : "OFF")}");
                DeferMenu(p, OpenVipMenu);
            });

            menu.AddMenuOption($"Smoke: {settings.SmokeColor}", (p, _) => DeferMenu(p, OpenSmokeMenu));
            menu.AddMenuOption("Free Guns", (p, _) => DeferMenu(p, OpenGunsMenu));

            if (Config.VoteKickEnabled)
                menu.AddMenuOption("Vote Kick", (p, _) => DeferMenu(p, OpenVoteKickMenu));

            OpenPanel(player, menu);
            PanelHint(player, "VIP menu opened");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "CenterHtml VIP menu failed — using ChatMenu");
            OpenVipMenuChatFallback(player);
        }
    }

    private void OpenVipMenuChatFallback(CCSPlayerController player)
    {
        var settings = SettingsOf(player);
        var menu = new ChatMenu("YGuard VIP");

        menu.AddMenuOption($"Tag: {(settings.TagEnabled ? "ON" : "OFF")}", (p, _) =>
        {
            settings.TagEnabled = !settings.TagEnabled;
            _store.Save();
            ApplyTag(p);
            PanelHint(p, $"VIP Tag: {(settings.TagEnabled ? "ON" : "OFF")}");
            AddTimer(0.1f, () => { if (p.IsValid) OpenVipMenuChatFallback(p); });
        });

        menu.AddMenuOption($"Smoke: {settings.SmokeColor}", (p, _) =>
        {
            AddTimer(0.1f, () => { if (p.IsValid) OpenSmokeMenuChatFallback(p); });
        });
        menu.AddMenuOption("Free Guns", (p, _) =>
        {
            AddTimer(0.1f, () => { if (p.IsValid) OpenGunsMenu(p); });
        });

        if (Config.VoteKickEnabled)
        {
            menu.AddMenuOption("Vote Kick", (p, _) =>
            {
                AddTimer(0.1f, () => { if (p.IsValid) OpenVoteKickMenu(p); });
            });
        }

        menu.PostSelectAction = PostSelectAction.Close;
        OpenChatPanel(player, menu);
        Msg(player, $"{ChatColors.Lime}VIP menu — type the number (hidden from chat)");
    }

    private void OpenSmokeMenuChatFallback(CCSPlayerController player)
    {
        var settings = SettingsOf(player);
        var menu = new ChatMenu("Smoke Color");
        foreach (var key in Config.SmokeColors.Keys)
        {
            var colorKey = key;
            menu.AddMenuOption(colorKey, (p, _) =>
            {
                SettingsOf(p).SmokeColor = colorKey;
                _store.Save();
                PanelHint(p, $"Smoke: {colorKey}");
            });
        }

        OpenChatPanel(player, menu);
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
                PanelHint(p, $"Smoke: {colorKey}");
                DeferMenu(p, OpenVipMenu);
            });
        }

        menu.AddMenuOption("« Back", (p, _) => DeferMenu(p, OpenVipMenu));
        OpenPanel(player, menu);
    }

    private void OpenGunsMenu(CCSPlayerController player)
    {
        if (!player.PawnIsAlive)
        {
            PanelHint(player, "You must be alive.");
            return;
        }

        if (!BenefitsAllowed())
        {
            PanelHint(player, $"Free guns from round {Config.MinRoundForGunsAndHealthshot}+ (now {_roundNumber})");
            return;
        }

        if (Config.GunsOncePerRound && _usedGunsThisRound.Contains(player.Slot))
        {
            PanelHint(player, "Already used free gun this round.");
            return;
        }

        var menu = new CenterHtmlMenu("Free Guns", this);
        foreach (var gun in Config.Guns)
        {
            var g = gun;
            menu.AddMenuOption(g.Name, (p, _) => GiveGun(p, g.Weapon, g.Name));
        }

        menu.AddMenuOption("« Back", (p, _) => DeferMenu(p, OpenVipMenu));
        OpenPanel(player, menu);
    }

    private void GiveGun(CCSPlayerController player, string weapon, string displayName)
    {
        try
        {
            if (!IsVip(player) || !player.PawnIsAlive)
                return;

            if (!BenefitsAllowed())
            {
                PanelHint(player, $"Free guns from round {Config.MinRoundForGunsAndHealthshot}+");
                return;
            }

            if (Config.GunsOncePerRound && _usedGunsThisRound.Contains(player.Slot))
            {
                PanelHint(player, "Already used free gun this round.");
                return;
            }

            player.GiveNamedItem(weapon);
            _usedGunsThisRound.Add(player.Slot);
            PanelHint(player, $"Received {displayName}");
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
            Msg(starter, $"{ChatColors.Red}A vote kick is already running. Type !yes / !no");
            return;
        }

        var since = (DateTime.UtcNow - _lastVoteKickUtc).TotalSeconds;
        if (since < Config.VoteKickCooldownSeconds)
        {
            Msg(starter, $"{ChatColors.Red}Cooldown: {(int)(Config.VoteKickCooldownSeconds - since)}s");
            return;
        }

        var menu = new CenterHtmlMenu("Vote Kick — select player", this);
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
            var name = p.PlayerName;
            var sid = p.SteamID;
            menu.AddMenuOption(name, (voter, _) =>
            {
                var v = voter;
                AddTimer(0.05f, () =>
                {
                    if (v.IsValid)
                        StartVoteKick(v, sid, name);
                });
            });
        }

        if (!any)
        {
            Msg(starter, $"{ChatColors.Red}No kickable players online.");
            return;
        }

        OpenPanel(starter, menu);
    }

    private void StartVoteKick(CCSPlayerController starter, ulong targetSteam, string targetName)
    {
        try
        {
            if (!starter.IsValid || !IsVip(starter) || _voteActive)
                return;

            var since = (DateTime.UtcNow - _lastVoteKickUtc).TotalSeconds;
            if (since < Config.VoteKickCooldownSeconds)
            {
                Msg(starter, $"{ChatColors.Red}Cooldown: {(int)(Config.VoteKickCooldownSeconds - since)}s");
                return;
            }

            var target = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.SteamID == targetSteam);
            if (target == null)
            {
                Msg(starter, $"{ChatColors.Red}Player left.");
                return;
            }

            targetName = target.PlayerName;
            _voteActive = true;
            _voteTargetSteam = targetSteam;
            _voteTargetName = targetName;
            _voteYes.Clear();
            _voteNo.Clear();
            _voteYes.Add(starter.SteamID);
            _lastVoteKickUtc = DateTime.UtcNow;

            var needed = VotesNeeded();
            Server.PrintToChatAll("====================================");
            Broadcast($"{ChatColors.Orange}VOTE KICK → {ChatColors.Red}{targetName}");
            Broadcast($"{ChatColors.Grey}By {starter.PlayerName} · need {ChatColors.Lime}{needed} YES {ChatColors.Grey}· {Config.VoteKickDurationSeconds}s");
            Broadcast($"{ChatColors.Lime}!yes {ChatColors.Grey}/ {ChatColors.Red}!no {ChatColors.Grey}or use the panel");
            Server.PrintToChatAll("====================================");

            AddTimer(0.1f, () =>
            {
                if (!_voteActive) return;
                foreach (var p in Utilities.GetPlayers())
                {
                    if (!p.IsValid || p.IsBot || p.IsHLTV || p.SteamID == 0)
                        continue;
                    if (p.SteamID == targetSteam)
                        continue;
                    OpenVoteBallot(p);
                }
            });

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

            AddTimer(1.5f, () =>
            {
                if (_voteActive && _voteYes.Count >= VotesNeeded())
                    FinishVoteKick();
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "StartVoteKick failed");
            _voteActive = false;
            Msg(starter, $"{ChatColors.Red}Vote kick failed to start.");
        }
    }

    private void OpenVoteBallot(CCSPlayerController voter)
    {
        try
        {
            if (!voter.IsValid || !_voteActive) return;

            var menu = new CenterHtmlMenu($"Kick {_voteTargetName}?", this);
            menu.AddMenuOption("YES — Kick", (p, _) => CastVote(p, yes: true));
            menu.AddMenuOption("NO — Keep", (p, _) => CastVote(p, yes: false));
            OpenPanel(voter, menu);
            PanelHint(voter, $"Vote kick {_voteTargetName}: !yes / !no");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "OpenVoteBallot failed");
            PanelHint(voter, $"Vote kick {_voteTargetName}: !yes / !no");
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
        var needed = (int)Math.Ceiling(voters * Config.VoteKickRatio);
        if (voters >= 2)
            needed = Math.Max(2, needed);
        return Math.Max(1, needed);
    }

    private void PushVoteHud()
    {
        if (!_voteActive) return;
        var needed = VotesNeeded();
        var line = $"VOTE KICK {_voteTargetName}  YES {_voteYes.Count}/{needed}  NO {_voteNo.Count}";
        var html =
            $"<font color='#ffaa00'><b>VOTE KICK</b></font><br>" +
            $"<font color='#ff4444'>{_voteTargetName}</font><br>" +
            $"<font color='#88ff88'>YES {_voteYes.Count}</font>/{needed} " +
            $"<font color='#ff8888'>NO {_voteNo.Count}</font><br>" +
            $"<font color='#cccccc'>!yes / !no</font>";

        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid || p.IsBot || p.IsHLTV) continue;
            try
            {
                p.PrintToCenter(line);
                p.PrintToCenterHtml(html);
            }
            catch { /* ignore */ }
        }
    }

    private void FinishVoteKick()
    {
        if (!_voteActive) return;

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

        if (_resetRoundOnNextStart)
        {
            _roundNumber = 0;
            _resetRoundOnNextStart = false;
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
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        SyncVipPermission(player);
        if (!IsVip(player))
            return HookResult.Continue;

        var p = player;
        AddTimer(0.15f, () =>
        {
            if (!p.IsValid || !p.PawnIsAlive) return;
            ApplyTag(p);
            if (BenefitsAllowed())
                EnforceOneHealthshot(p, giveIfMissing: true);
            else
                EnforceOneHealthshot(p, giveIfMissing: false);
        });

        AddTimer(0.8f, () =>
        {
            if (!p.IsValid || !p.PawnIsAlive) return;
            ApplyTag(p);
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
            var wantTag = IsVip(player) && settings.TagEnabled;
            var tag = Config.VipTagText;

            RememberBaseName(player, tag);

            if (!_originalName.TryGetValue(player.Slot, out var baseName) || IsPlaceholderName(baseName))
                return; // wait until real Steam name is known — avoids "ⱽᴵᴾ✶ Player"

            if (!_originalClan.ContainsKey(player.Slot))
                _originalClan[player.Slot] = StripVipPrefixes(player.Clan ?? "", tag);

            // Name prefix only (no clan VIP — avoids double tag)
            player.Clan = _originalClan.GetValueOrDefault(player.Slot, "");

            var newName = wantTag ? $"{tag} {baseName}" : baseName;
            if (!string.Equals(player.PlayerName, newName, StringComparison.Ordinal))
            {
                player.PlayerName = newName;
                try { Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName"); }
                catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ApplyTag failed");
        }
    }

    private void RememberBaseName(CCSPlayerController player, string tag)
    {
        var cleaned = StripVipPrefixes(player.PlayerName ?? "", tag);
        if (IsPlaceholderName(cleaned))
            return;

        if (!_originalName.TryGetValue(player.Slot, out var existing) || IsPlaceholderName(existing))
            _originalName[player.Slot] = cleaned;
    }

    private static bool IsPlaceholderName(string? name)
        => string.IsNullOrWhiteSpace(name)
           || name.Equals("Player", StringComparison.OrdinalIgnoreCase)
           || name.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    private static string StripVipPrefixes(string name, string tag)
    {
        if (string.IsNullOrEmpty(name))
            return "";

        var n = name.Trim();
        for (var i = 0; i < 8; i++)
        {
            if (!string.IsNullOrEmpty(tag) && n.StartsWith(tag + " ", StringComparison.Ordinal))
            {
                n = n[(tag.Length + 1)..].TrimStart();
                continue;
            }

            if (n.StartsWith("VIP ", StringComparison.OrdinalIgnoreCase))
            {
                n = n[4..].TrimStart();
                continue;
            }

            break;
        }

        return n.Trim();
    }

    private void ClearTag(CCSPlayerController player)
    {
        try
        {
            if (!player.IsValid) return;
            var tag = Config.VipTagText;
            RememberBaseName(player, tag);
            var baseName = _originalName.TryGetValue(player.Slot, out var o) && !IsPlaceholderName(o)
                ? o
                : StripVipPrefixes(player.PlayerName ?? "", tag);
            if (IsPlaceholderName(baseName))
                return;

            _originalName[player.Slot] = baseName;
            player.PlayerName = baseName;
            player.Clan = _originalClan.GetValueOrDefault(player.Slot, "");
            try { Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName"); }
            catch { /* ignore */ }
        }
        catch { /* ignore */ }
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
                if (!target.IsValid || !target.PawnIsAlive) return;
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
        _originalName.Remove(playerSlot);
        _openMenus.Remove(playerSlot);
        _menuSilentUntil.Remove(playerSlot);
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
