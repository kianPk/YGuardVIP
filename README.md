# YGuard VIP

CounterStrikeSharp plugin for YGuard / 5Stack **Public / Custom dedicated** only.

On Ranked / Practice pods the plugin loads but **idles** (reads `SERVER_TYPE`), same pattern as YGuardNoFF.

## Features

- `!vip` — **panel** (CenterHtml window, not chat menu)
  - Toggle VIP tag `ⱽᴵᴾ` (scoreboard clan tag)
  - Smoke color
  - Free guns / vote kick
- `!g` — free gun panel (once per round, blocked on round 1 of each half)
- One healthshot per round (never stacks)
- Timed VIP stored in **vip_database.json** (survives restarts)

## Config

`configs/plugins/YGuardVIP/YGuardVIP.json`:

```json
"PublicOnly": true
```

Leave `true` so Ranked matchmaking never gets VIP guns/tag/votekick.

## Admin commands

```
css_addvip <steamid64> <duration>
css_removevip <steamid64>
css_listvip
```

Duration examples: `30m` `12h` `7d` `30d` `2w` `1mo` `perm`

```
css_addvip 76561199388315261 30d
css_addvip 76561199388315261 perm
```

## Data (persistent)

Saved under:

`addons/counterstrikesharp/configs/plugins/YGuardVIP/`

- `vip_database.json` — VIP grants + expiry
- `player_settings.json` — tag/smoke prefs

Updating the plugin DLL does **not** wipe this folder.

## VIP permission

Runtime flag: `@yguard/vip` (also accepts `@css/vip`). Timed grants apply this flag automatically from the DB.

## Install on 5Stack node

```bash
PUBLIC=05cb789f-0e5e-433d-bbfc-6114e465323b
cd /tmp
rm -rf yguardvip YGuardVIP-1.2.2.zip
wget -O YGuardVIP-1.2.2.zip "https://github.com/kianPk/YGuardVIP/releases/download/v1.2.2/YGuardVIP-1.2.2.zip"
unzip -o YGuardVIP-1.2.2.zip -d yguardvip

for ROOT in \
  /opt/5stack/custom-plugins/addons/counterstrikesharp \
  /opt/5stack/servers/$PUBLIC/addons/counterstrikesharp
do
  rm -rf "$ROOT/plugins/YGuardVIP"
  cp -a yguardvip/YGuardVIP "$ROOT/plugins/"
done
```

Restart Public (and any Ranked pods if they already loaded the old DLL). Config: `configs/plugins/YGuardVIP/YGuardVIP.json`

Set tag text if needed:
```json
"VipTagText": "ⱽᴵᴾ"
```
