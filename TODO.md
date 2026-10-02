# TODO

## Server

- [x] Run ntfy on the Docker host (`docs/ntfy/`, port 8090, deny-all access)
- [x] Create the `administrator` account and the `pf-plugin` account (write-only on `pf-alerts`) with its access token
- [x] Deploy `docs/ntfy.conf` on the reverse proxy and run Certbot
- [ ] Check `https://ntfy.shifusenproductions.com/v1/health` from outside the LAN (phone on mobile data)
- [ ] Add the DNS A record for `plugins.shifusenproductions.com`, then deploy `docs/plugins.conf` and run Certbot
- [ ] First release: `pwsh tools/publish.ps1`
- [ ] Optional hardening: let only the reverse proxy reach ntfy's port 8090 on .113. Docker bypasses ufw, so this needs a `DOCKER-USER` rule. Today any LAN client can call ntfy directly over HTTP; access control still applies, but such a client can spoof `X-Forwarded-For` to dodge rate limits.

## Phone

- [ ] Install the ntfy app, subscribe to `pf-alerts` on the self-hosted server, and log in as `administrator`
- [ ] Send a test with `/pfnotify test` and confirm it arrives with the screen off and the app closed

## Dalamud plugin

Everything here must follow the passive-only rule in `docs/dalamud-api.md`: observe client state, never send anything to the game server.

- [x] Rename `SamplePlugin` to `PfNotification` and fill in the manifest properties
- [x] Replace the template's `/pmycommand`, sample windows and goat image with `/pfnotify` and one status/settings window
- [x] Detect "party full" from the party size (`IPartyList`, or `InfoProxyCrossRealm` for Party Finder groups)
- [x] Settings window with a "Send test notification" button
- [x] Send off the game thread with a timeout and retries; report failures instead of throwing
- [x] Notify once per fill; re-arm after a slot stays open for 10 s
- [x] Unit tests for the detector, the ntfy client and the role summary
- [ ] In-game verification with diagnostic logging on:
  - cross-world listing fills
  - duty entry and exit
  - Duty Finder
  - joining as the last member
  - slot refill
  - `target 4`
- [ ] From the diagnostic log, confirm the cross-realm count includes you and the `ClassJob.Role` values (1 tank, 2/3 DPS, 4 healer)
- [ ] Record the "recruitment complete" `LogMessageId`, then add it as a second trigger, de-duplicated with the same latch

## Mobile app (Swift + Skip, on the Mac)

Plan: [docs/mobile-app.md](docs/mobile-app.md). One SwiftUI codebase for iOS and Android. Android keeps its own ntfy connection; iOS gets APNs through our own relay. No ntfy.sh upstream.

- [ ] Mac setup: Xcode, Android Studio, `brew install skip`, `skip checkup`
- [ ] Enroll in the Apple Developer Program (needed for push notifications and TestFlight)
- [ ] Scaffold `mobile/` with `skip init` (app ID `com.shifusen.pfnotification`): setup screen, alerts list, ntfy client
- [ ] Android delivery: foreground service on the ntfy JSON stream, boot restart, catch-up with `since=`
- [ ] iOS delivery: the `pf-relay` service (Vapor + APNSwift) on .113, the `pf-relay` ntfy account, `push.shifusenproductions.com` (DNS, `docs/push.conf`, Certbot), APNs registration in the app
- [ ] Distribution: TestFlight for iOS, a signed APK for Android
- [ ] Delete the Kotlin template in `android/` once `mobile/` builds for Android

## Cleanup

- [ ] Retire pf-vetter, the old web-app version: remove its nginx site and fail2ban jail on the reverse proxy
