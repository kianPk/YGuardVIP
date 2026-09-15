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
    public string ChatPrefix { get; set; } = " {green}[YGuard VIP]{default}";

    [JsonPropertyName("VipChatColor")]
    public string VipChatColor { get; set; } = "gold";

    [JsonPropertyName("VipTagText")]
    public string VipTagText { get; set; } = "★VIP";

    [JsonPropertyName("DefaultTagEnabled")]
    public bool DefaultTagEnabled { get; set; } = true;

    [JsonPropertyName("DefaultSmokeColor")]
    public string DefaultSmokeColor { get; set; } = "red";

    [JsonPropertyName("GunsOncePerRound")]
    public bool GunsOncePerRound { get; set; } = true;

    [JsonPropertyName("MinPlayersAliveForGuns")]
    public int MinPlayersAliveForGuns { get; set; } = 1;

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
        ["red"] = [255, 40, 40],
        ["green"] = [40, 255, 80],
        ["blue"] = [40, 120, 255],
        ["purple"] = [180, 40, 255],
        ["orange"] = [255, 140, 40],
        ["pink"] = [255, 80, 180],
        ["cyan"] = [40, 220, 255]
    };
}

public class GunOption
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Weapon")]
    public string Weapon { get; set; } = "";
}
