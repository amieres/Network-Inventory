# NetDaemonFSApps — Claude Code Notes

## Working Style

Before doing any significant work, ask clarifying questions to confirm assumptions. Do not start implementing until the approach is agreed upon.

## Publish & Deploy

From `NetDaemonFS/` directory, run:

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
```

This script:
1. Stops the HA add-on (slug configured in `publish.ps1`)
2. Runs `dotnet publish -c Release -o \\<ha-host>\config\<addon-slug>`
3. Starts the add-on again

The stop/start calls use the HA PowerShell module which sometimes fails DNS resolution — the publish step still succeeds and copies files to the SMB share. If stop/start fails, restart the add-on manually via:

```bash
curl -s -X POST "http://<ha-host>:8123/api/services/hassio/addon_restart" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"addon": "<addon-slug>"}'
```

Token is in `NetDaemonFS/appsettings.json` → `HomeAssistant.Token`.

**Reliable one-liner** (publish + restart, from repo root):

```bash
cd NetDaemonFS && powershell -ExecutionPolicy Bypass -File publish.ps1; \
TOKEN=$(cat appsettings.json | python -c "import sys,json; print(json.load(sys.stdin)['HomeAssistant']['Token'])"); \
curl -s -X POST "http://<ha-host>:8123/api/services/hassio/addon_restart" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"addon": "<addon-slug>"}'
```

## Static-only changes (app.js / index.html)

No restart needed — copy files directly to the share:

```bash
cp NetDaemonFS/wwwroot/app.js    //<ha-host>/config/<addon-slug>/wwwroot/app.js
cp NetDaemonFS/wwwroot/index.html //<ha-host>/config/<addon-slug>/wwwroot/index.html
```

## Bumping app.js version

Whenever `app.js` is modified, **always** also update two things in `index.html`:
1. The `?v=N` query string on the `<script src="app.js?v=N">` tag
2. The `html vN` version label in the `<h1>` (increment separately — tracks html-only changes)

Both numbers must match `JS_VERSION` in `app.js` or the browser will serve a stale cached file.

## HA Add-on Info

- Add-on slug: configured in `publish.ps1`
- Deploy target: `\\<ha-host>\config\<addon-slug>\`
- HA host: configured in `appsettings.json` → `HomeAssistant.Host`
- DB path (on HA): `/config/devices.db`
