# Mobile app plan (Swift + Skip)

Status: planned 2026-10-01, nothing built yet. Replaces the Kotlin template in `android/`.

## Goal

One Swift/SwiftUI codebase that ships a native iOS app and an Android app. Each receives the party-full alerts for its user's topic (`pf-alerts`, `pf-<mate>`) from the self-hosted ntfy server and shows recent alerts. The Dalamud plugin doesn't change: it keeps publishing to ntfy.

**Decisions:**
- **Language: Swift**, so an iOS release stays possible.
- **Tooling: [Skip](https://skip.dev)**, free and open source since January 2026. It builds the iOS app as plain SwiftUI and transpiles the same code to Kotlin and Jetpack Compose for Android. Skip requires macOS 15+, Xcode and Android Studio, so all app work happens on the Mac.
- **No ntfy.sh upstream.** The server stays self-contained, so iOS delivery uses our own APNs relay, below.

## Delivery: the platform split

The two platforms need different delivery paths. The Android app can keep a connection open itself; on iOS only Apple's push service can wake an app.

```
Dalamud plugin ──POST──▶ ntfy (.113) ──JSON stream──▶ Android app (foreground service)
                              │
                              └──JSON stream──▶ pf-relay (.113) ──APNs──▶ iOS app
```

### Android: the app's own connection

- **Connection:** a foreground service holds `GET https://ntfy.shifusenproductions.com/<topic>/json` open with the user's token. ntfy sends a keepalive every 45 s. On each message, the service posts a local notification.
- **Lifecycle:**
  - restart on boot and after the app updates
  - reconnect with backoff
  - catch up on missed messages with `?since=<last id>`
- **Permissions:** prompt for the battery-optimization exemption and the notification permission (Android 13+).
- **Written in Kotlin:** this part is Android-specific (service, manifest, notification channel), so it goes in Skip's Android-only code (`#if SKIP` blocks or Kotlin sources in the Android module).
- **To verify:**
  - which foreground service type Android 14/15 accept for a long-lived push connection (`specialUse` is the likely fit; `dataSync` has a daily time limit on Android 15)
  - whether Skip's URLSession bridge can stream, or the stream needs OkHttp directly

### iOS: APNs via our own relay

ntfy can't send APNs pushes to a third-party app; only the official ntfy app gets iOS pushes, through ntfy.sh. So a small relay service runs next to ntfy:

- **`pf-relay`:** server-side Swift (Vapor plus `APNSwift`, from the swift-server project), in a Docker container on .113 next to ntfy.
- **Registration:** the iOS app sends `POST /register {topic, deviceToken, environment}` with the user's ntfy token. The relay checks that the token can read the topic (a 1-message poll of `GET /<topic>/json?poll=1` returning 200), then stores topic → device tokens in SQLite. Tokens Apple reports as invalid are removed.
- **Fan-out:** the relay subscribes to the registered topics with its own ntfy account (`pf-relay`, read-only on `pf-*`) and turns each message into an APNs alert. The title and body come from the ntfy message. ntfy priority 4–5 maps to `interruption-level: time-sensitive`.
- **Exposure:** through nginx as `push.shifusenproductions.com` (new DNS record, new `docs/push.conf`, Certbot), the same pattern as `ntfy.conf`.
- **Privacy:** APNs payloads pass through Apple, so the alert text ("Party full", count and roles; no names) is visible to Apple's push service. This is unavoidable for background alerts on iOS.
- **Apple requirements:** an APNs auth key (`.p8`) from the Apple Developer account, mounted into the relay container as a secret. It never goes in this repo.

## App features (v1)

1. **Setup:** server URL (defaulting to `https://ntfy.shifusenproductions.com`), topic and access token, the same three values the plugin uses. A QR code that the plugin window shows could replace the typing later.
2. **Alerts list:** recent messages from `GET /<topic>/json?poll=1&since=12h`, newest first, with pull to refresh.
3. **Delivery status:** connected or registered, the last message received, and a "Send test" button that publishes to the topic. This works for mates, whose token is read-write; your `pf-plugin` token is write-only, so you'd use your own account's token.
4. **Settings:** notification sound, and the time-sensitive option on iOS.

Shared code (models, ntfy API client, settings storage, SwiftUI screens) lives once in Swift. Only delivery is platform-specific.

## Repo layout

| Path | Contents |
|---|---|
| `mobile/` | The Skip project (`Package.swift`, `Sources/PFNotification/`, `Darwin/` for the Xcode project, `Android/` for the Gradle project). App ID `com.shifusen.pfnotification`. |
| `relay/` | The `pf-relay` Swift package and its Dockerfile. |
| `docs/relay/` | `docker-compose.yml` for the relay on .113. |
| `docs/push.conf` | nginx site for `push.shifusenproductions.com`. |

`android/`, the Kotlin template, gets deleted once `mobile/` builds for Android.

## Phases

1. **Mac setup:**
   - Install Xcode, Android Studio and Homebrew, then `brew install skip` and `skip checkup`.
   - Enroll in the Apple Developer Program ($99/year). It's needed for push notifications and TestFlight, and approval can take a day or two.
2. **Scaffold** `mobile/` with `skip init` (app ID `com.shifusen.pfnotification`). Build the shared screens and the ntfy API client. Confirm the iOS simulator and an Android emulator both run.
3. **Android delivery:** the foreground service, notifications, boot restart and catch-up. Test with the screen off and the app swiped away.
4. **iOS delivery:**
   - Build `pf-relay`, its compose file, the `pf-relay` ntfy account, `docs/push.conf`, the DNS record and Certbot.
   - Add APNs registration in the app.
   - Test on a real iPhone; the simulator can't receive real APNs pushes.
5. **Distribution:**
   - **iOS:** TestFlight. Invite FC mates; external testers need a one-time Beta App Review.
   - **Android:** a signed release APK published next to the plugin on `plugins.shifusenproductions.com`, or Google Play later.
6. **Cleanup:** delete `android/`, and update README and TODO.

## Open questions

- Should mates log in with username and password, with the app creating its own token (`POST /v1/account/token`), instead of pasting a token?
- The relay holds one subscription per topic. That's fine for an FC, but revisit if the user count grows.
- An App Store release (versus TestFlight only) needs a privacy policy and App Review. Decide when it comes up.
