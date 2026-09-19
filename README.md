# YGuard VIP

CounterStrikeSharp VIP for YGuard **Public / Custom dedicated** only (`SERVER_TYPE`; idle on Ranked/Practice).

## Commands
- `!vip` — settings panel (tag, smoke, guns, votekick)
- `!g` — free gun
- Admin: `css_addvip` / `css_removevip` / `css_listvip`

## Panel install (Plugin Directory)
Release zip root is the plugin folder (`YGuardVIP.dll` + `lang/`).

- Layout: **Archive root is the plugin folder**
- Install path: `addons/counterstrikesharp/plugins/YGuardVIP`
- Load: turn **Ranked Matches** ON (covers public dedicated; plugin idles when `SERVER_TYPE=Ranked`)

## Data
Persists under `configs/plugins/YGuardVIP/` (`vip_database.json`, `player_settings.json`).
