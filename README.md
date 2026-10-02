# PF Notification

Get a notification on your phone when your FINAL FANTASY XIV Party Finder party fills, so you can step away from the PC while recruiting.

> **Status:**
> - **Plugin:** written and unit-tested, not yet verified in-game.
> - **ntfy server:** live.
> - **Plugin repository for FC mates:** goes live once its DNS record exists.
> - **Mobile app:** planned.
>
> See [TODO.md](TODO.md).

## How it works

```
FFXIV + Dalamud plugin ──HTTPS POST──▶ nginx (TLS) ──▶ ntfy (self-hosted) ──▶ phone
```

1. The Dalamud plugin watches your party. When it fills, it sends one HTTPS POST to your topic on a self-hosted [ntfy](https://ntfy.sh) server, e.g. "Party full: 8/8: 2 tanks, 2 healers, 4 DPS".
2. ntfy forwards the message to every phone subscribed to that topic.
3. The phone receives it through the ntfy Android app. A Swift companion app for iOS and Android is planned in [docs/mobile-app.md](docs/mobile-app.md).

No Firebase or other Google push service is involved. With a self-hosted server, the ntfy Android app keeps its own connection open instead.

The plugin is **read-only**. It observes your own client's party data and never sends anything to the game servers: no packets, no game commands, no Party Finder refreshes. See the passive-only rule in [docs/dalamud-api.md](docs/dalamud-api.md).

## Repository layout

| Path | Contents |
|---|---|
| `PfNotification/` | Dalamud plugin (C#, .NET 10, Dalamud API 15). |
| `tests/PfNotification.Tests/` | xUnit tests for the detector, the ntfy client and the role summary. |
| `tools/publish.ps1` | Builds a release and publishes it to the private plugin repository. |
| `android/` | Kotlin template, to be replaced by a Swift/Skip app in `mobile/` (see [docs/mobile-app.md](docs/mobile-app.md)). |
| `docs/ntfy/` | ntfy server: `docker-compose.yml` and `server.yml`. |
| `docs/ntfy.conf`, `docs/plugins.conf` | nginx sites for `ntfy.` and `plugins.shifusenproductions.com`. |
| `docs/dalamud-api.md` | The Dalamud API 15 subset the plugin uses, and the passive-only rule. |
| `docs/mobile-app.md` | Plan for the Swift/Skip companion app and the iOS push relay. |
| `.github/workflows/pr-build.yml` | CI: builds the solution and runs the tests on pull requests to `master`. |

## Dalamud plugin

### What it does

* **Detection.** Every 250 ms it reads the party size: `IPartyList` for same-world parties, or the cross-realm party data for Party Finder groups. When the size reaches the target (8, or 4 for light parties) and holds for 2 seconds, it sends one alert.
* **No false alarms.**
  * Entering or leaving a duty never alerts, including a Duty Finder party forming.
  * Joining someone else's full party as the last member doesn't alert either; you're clearly at the PC.
  * A party that was already full when you logged in or loaded the plugin doesn't alert.
* **Re-arming.** If a slot stays open for 10 seconds and then fills again, you get another alert.
* **Limits.** Field operations (Eureka, Bozja, Occult Crescent) count as being in a duty, so recruiting there doesn't alert. Alliance (24-player) listings aren't supported.

### Commands

| Command | Effect |
|---|---|
| `/pfnotify` | Open the status and settings window. |
| `/pfnotify test` | Send a test notification. |
| `/pfnotify on` / `off` | Turn alerts on or off. Detection keeps running either way. |
| `/pfnotify status` | Print the party size and detector state to your chat log. |
| `/pfnotify target <2-8>` | Set the size that counts as full. |

Settings are the ntfy server URL, your topic and access token, the priority, the target size, the chat confirmation and diagnostic logging. The token is stored unencrypted in the plugin's config file.

### Prerequisites

* XIVLauncher, FINAL FANTASY XIV and Dalamud installed, and the game run with Dalamud at least once.
* XIVLauncher in its default location. Otherwise set `DALAMUD_HOME` to Dalamud's dev directory.
* .NET 10 SDK.

### Building and testing

```powershell
dotnet build PfNotification.slnx -c Release
dotnet test tests/PfNotification.Tests -c Release
```

The plugin is written to `PfNotification/bin/x64/Release/PfNotification.dll` (or `Debug`). Its manifest is generated from the properties in `PfNotification/PfNotification.csproj`. Release builds also produce `PfNotification/bin/x64/Release/PfNotification/latest.zip`.

### Loading it as a dev plugin

1. Run `/xlsettings`, open **Experimental**, and add the full path to `PfNotification.dll` under **Dev Plugin Locations**.
2. Run `/xlplugins`, open **Dev Tools > Installed Dev Plugins**, and enable **PF Notification**.
3. Run `/pfnotify`, enter your topic and token, and press **Send test notification**.

### Installing it as an FC mate

1. In `/xlsettings` > **Experimental** > **Custom Plugin Repositories**, add `https://plugins.shifusenproductions.com/da52407735eff741/repo.json`, tick it, and save. This URL isn't live yet; see the server section of [TODO.md](TODO.md).
2. Install **PF Notification** from `/xlplugins`. Updates arrive automatically.
3. Ask for an ntfy account (see [Adding an FC mate](#adding-an-fc-mate)). Then set the topic and token in `/pfnotify`.

The source for each release is at `source.zip` next to `repo.json`.

### Publishing a release

1. Bump `<Version>` in `PfNotification/PfNotification.csproj`; Dalamud only updates when the version increases.
2. Commit.
3. Run `pwsh tools/publish.ps1`. It refuses to run with uncommitted plugin changes. It builds, runs the tests, writes `repo.json` and `source.zip`, and uploads them with `latest.zip`. Use `-DryRun` to build without uploading.

### AI usage

Before submitting to the official Dalamud repository, review the [AI Usage Policy](https://dalamud.dev/plugin-publishing/ai-policy) and disclose your level of AI use. Entirely AI-generated submissions are rejected, and undisclosed AI use may result in a ban. This plugin is distributed privately and isn't meant for that repository. See also the [Code of Conduct](https://dalamud.dev/code-of-conduct) and the [Dalamud developer docs](https://dalamud.dev).

## Mobile app

The companion app will be written in Swift with [Skip](https://skip.dev): one SwiftUI codebase that builds a native iOS app and transpiles to Kotlin and Jetpack Compose for Android. Skip needs macOS 15+, so this work happens on the Mac. The plan, including how iOS gets alerts through an APNs relay, is in [docs/mobile-app.md](docs/mobile-app.md).

The Kotlin template in `android/` (Gradle 9.8, JDK 25) still builds with `.\gradlew.bat assembleDebug`. It will be deleted once the Skip app builds for Android.

## ntfy server

ntfy runs on the home Docker host behind the nginx reverse proxy at `https://ntfy.shifusenproductions.com`. The deploy commands are in the header of each file:

* `docs/ntfy/` goes to `~/ntfy` on the Docker host and is started with `docker compose up -d`. It listens on port 8090.
* `docs/ntfy.conf` goes in nginx's `sites-available` on the reverse proxy, with HTTPS from Certbot.

Nothing on the server is readable or writable without an account or token. Each person gets their own topic:

| Account | Access | Used by |
|---|---|---|
| `administrator` | Admin: read and write on all topics | Your phone and the web app |
| `pf-plugin` | Write-only on `pf-alerts`, via access token | Your plugin |
| `<mate>` | Read and write on `pf-<mate>` | That mate's phone, and their plugin via a token |

Keep tokens out of this repo; the plugin stores its token in its Dalamud configuration.

### Adding an FC mate

On the Docker host:

```bash
docker exec -it ntfy ntfy user add <mate>                          # choose a password; the mate logs into the app with it
docker exec ntfy ntfy access <mate> pf-<mate> rw
docker exec ntfy ntfy token add --label PfNotification <mate>      # prints tk_..., for their plugin
```

Send them the password and token privately. They install the ntfy app from Google Play or F-Droid, add the server `https://ntfy.shifusenproductions.com` (not the default ntfy.sh), log in, and subscribe to `pf-<mate>`. In the plugin, they set the topic to `pf-<mate>` and paste the token.

iPhone users can't get timely alerts yet. The server deliberately doesn't forward to ntfy.sh, and without that the official ntfy iOS app only receives messages after a delay of up to hours. iOS support arrives with the Swift app and its APNs relay ([docs/mobile-app.md](docs/mobile-app.md)).

To test from a terminal:

```bash
curl -H "Authorization: Bearer <token>" -H "Content-Type: application/json" \
  -d '{"topic":"pf-alerts","title":"PF Notification","message":"Party full (8/8)"}' \
  https://ntfy.shifusenproductions.com/
```

## License

[AGPL-3.0](LICENSE.md). Based on the [goatcorp/SamplePlugin](https://github.com/goatcorp/SamplePlugin) template. If you give the plugin to someone, you must also offer them its source; `tools/publish.ps1` publishes `source.zip` for that.
