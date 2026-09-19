using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace YGuardVIP;

public class YGuardVipConfig : BasePluginConfig
{
    [JsonPropertyName("VipPermission")]
    public string VipPermission { get; set; } = "@yguard/vip";

    [JsonPropertyName("AdminPermission")]
    public string AdminPermission { get; set; } = "@css/root";

    [JsonPropertyName("ChatPrefix")]
    public string ChatPrefix { get; set; } = "YGuard";

    /// <summary>
    /// When true (default), plugin idles on Ranked / Practice pods
    /// (reads SERVER_TYPE). VIP features only run on Public/Custom dedicated.
    /// </summary>
    [JsonPropertyName("PublicOnly")]
    public bool PublicOnly { get; set; } = true;

    /// <summary>Shown as the scoreboard clan tag (small-caps style).</summary>
    [JsonPropertyName("VipTagText")]
    public string VipTagText { get; set; } = "ⱽᴵᴾ";

    [JsonPropertyName("DefaultTagEnabled")]
    public bool DefaultTagEnabled { get; set; } = true;

    [JsonPropertyName("DefaultSmokeColor")]
    public string DefaultSmokeColor { get; set; } = "red";

    [JsonPropertyName("EnableSmokeColor")]
    public bool EnableSmokeColor { get; set; } = true;

    [JsonPropertyName("GunsOncePerRound")]
    public bool GunsOncePerRound { get; set; } = true;

    /// <summary>Round number from which free guns &amp; healthshot are allowed (1 = blocked on round 1).</summary>
    [JsonPropertyName("MinRoundForGunsAndHealthshot")]
    public int MinRoundForGunsAndHealthshot { get; set; } = 2;

    [JsonPropertyName("VoteKickEnabled")]
    public bool VoteKickEnabled { get; set; } = true;

    [JsonPropertyName("VoteKickRatio")]
    public float VoteKickRatio { get; set; } = 0.6f;

    [JsonPropertyName("VoteKickDurationSeconds")]
    public int VoteKickDurationSeconds { get; set; } = 30;

    [JsonPropertyName("VoteKickCooldownSeconds")]
    public int VoteKickCooldownSeconds { get; set; } = 120;

    [JsonPropertyName("Guns")]
    public List<GunOption> Guns { get; set; } =
    [
        new() { Name = "AK-47", Weapon = "weapon_ak47" },
        new() { Name = "M4A4", Weapon = "weapon_m4a1" },
        new() { Name = "M4A1-S", Weapon = "weapon_m4a1_silencer" },
        new() { Name = "AWP", Weapon = "weapon_awp" },
        new() { Name = "Desert Eagle", Weapon = "weapon_deagle" },
        new() { Name = "USP-S", Weapon = "weapon_usp_silencer" },
        new() { Name = "Glock-18", Weapon = "weapon_glock" }
    ];

    [JsonPropertyName("SmokeColors")]
    public Dictionary<string, int[]> SmokeColors { get; set; } = new()
    {
        ["off"] = [255, 255, 255],
        ["red"] = [255, 20, 20],
        ["green"] = [20, 255, 40],
        ["blue"] = [10, 60, 255],
        ["purple"] = [200, 20, 255],
        ["orange"] = [255, 90, 0],
        ["pink"] = [255, 40, 160],
        ["cyan"] = [0, 255, 255]
    };
}

public class GunOption
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Weapon")]
    public string Weapon { get; set; } = "";
}
