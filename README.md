# YGuard VIP

CounterStrikeSharp plugin for YGuard / 5Stack public Comp.

## Features

- `!vip` / `css_vip` — settings menu (VIP only)
  - Toggle VIP scoreboard/chat tag
  - Change smoke grenade color
- `!g` / `/g` / `css_g` — free gun menu (once per round)
- One `weapon_healthshot` every round (never stacks)
- Colored VIP chat name when tag is enabled
- VIP tag `★VIP★` on scoreboard (toggle in `!vip`)
- Native CS2 vote kick (`!votekick` / menu) — other players vote F1/F2; pass = kick

## VIP permission

Default flag: `@yguard/vip` (also accepts `@css/vip` and `@css/root`).

### SimpleAdmin / admins.json example

```json
{
  "76561199388315261": {
    "identity": "76561199388315261",
    "flags": ["@yguard/vip"]
  }
}
```

Online grant (temporary until reconnect unless saved in admins):

```
css_addvipflag 76561199388315261
```

## Build

```bash
dotnet build -c Release
```

Output: `bin/Release/net8.0/YGuardVIP.dll`

## Install on 5Stack node

Do **not** add this to `.5stack-plugins/index` (managed plugins get skipped unless in `ENABLED_PLUGINS`). Keep it hand-placed:

```bash
PUBLIC=05cb789f-0e5e-433d-bbfc-6114e465323b

for ROOT in \
  /opt/5stack/custom-plugins/addons/counterstrikesharp \
  /opt/5stack/servers/$PUBLIC/addons/counterstrikesharp
do
  mkdir -p "$ROOT/plugins/YGuardVIP"
  cp -a YGuardVIP.dll lang "$ROOT/plugins/YGuardVIP/" 2>/dev/null || true
  cp YGuardVIP.dll "$ROOT/plugins/YGuardVIP/"
  cp -a lang "$ROOT/plugins/YGuardVIP/"
done
```

Restart Public. Config is auto-generated at:

`configs/plugins/YGuardVIP/YGuardVIP.json`

Player settings (tag/smoke): next to the plugin DLL as `player_settings.json`.

## Chat trigger

Ensure `core.json` has `"!"` in `PublicChatTrigger` so `!vip` / `!g` work.
