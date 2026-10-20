# Changelog

Notable changes to PF Notification. Versions match the plugin version shown in `/xlplugins`.

## [0.1.0] - 2026-10-20

### Added

- **Duty-ready alert:** a notification when a Duty Finder queue or roulette pops, e.g. "Duty ready: Sastasha: accept within 45 s" (roulettes show as "Duty Roulette"). Priority 5 by default, so it gets through Do Not Disturb. A re-pop after someone declines alerts again; a second pop within 10 seconds doesn't. Toggle: `/pfnotify duty on|off`.
- **Remote party chat (opt-in, off by default):** messages you publish to your chat topic (the alerts topic plus `-chat`) from your phone are posted to party chat as `/p <text>` by your client.
  - **Guards:** one line, no commands, 500 bytes, one message every 2 seconds and 10 a minute (extra ones wait), only while logged in and in a party, nothing older than 60 seconds or from before the feature was turned on.
  - **Feedback:** "Sent to party" or "Not sent to party" with the reason, on your alerts topic; rejections are also printed in chat.
  - **Stream:** reconnects with backoff, catches up after a short blip, stops on a rejected token. The window shows its state.
  - **Toggle:** `/pfnotify chat on|off`. The plugin token needs read access to the chat topic on the server (see README).

### Changed

- The plugin is now described as **passive by default**: remote party chat is the one feature that makes the client send something to the game server, and only when you turn it on. The window footer, README and `docs/dalamud-api.md` say so.
- `/pfnotify status` also reports duty-ready alerts and the remote chat stream.

### Known limits

- **Not yet tested in-game**, including whether `/p` from the plugin reaches a cross-world (pre-duty) Party Finder party.
- The official ntfy Android app can't compose messages: send from the ntfy web app, or wait for the companion app.
- Text the game's own filter would change (some emoji and symbols) is refused rather than sent altered.
- No extra shared secret: anyone who can publish to the chat topic can speak as you. ntfy's access control and HTTPS are the protection.

## [0.0.1] - 2026-10-09

First test build (alpha).

### Added

- **Party-full alert:** a notification on your phone when your party reaches full size and stays there for 2 seconds, e.g. "Party full: 8/8: 2 tanks, 2 healers, 4 DPS".
  - **Party types:** Party Finder (cross-world) and same-world parties. Full means 8, or 4 for light parties with `/pfnotify target 4`.
  - **When it stays quiet:**
    - entering or leaving a duty, including a Duty Finder party forming
    - joining someone else's full party as its last member
    - a party that was already full when you logged in
  - **Re-arming:** it fires again once a slot has stayed open for 10 seconds and then fills.
- **Join and leave alerts:** "WHM joined" or "SAM left", with the new count and roles.
  - **Jobs only:** never names.
  - **Low priority by default:** they arrive without sound or vibration.
  - **Not sent** in duties, or when you join or leave a party yourself.
  - **No double alert:** the member who completes the party is announced by the party-full alert.
  - **Toggle:** `/pfnotify joins on|off`.
- **Delivery through ntfy:**
  - your own topic and access token on an ntfy server (https only)
  - retries on network and server errors
  - a separate priority for each alert type
- **Window** (`/pfnotify`): party status, settings, and a **Send test notification** button. In-game, join and leave alerts only report failures.
- **Commands:** `/pfnotify test`, `on`, `off`, `status`, `target <2-8>` and `joins on|off`.
- **Diagnostic logging** to `/xllog` for tuning detection.
- **Read-only:** the plugin only observes your own client. It never sends anything to the game servers.

### Known limits

- **Not yet tested in-game.**
- **Field operations** (Eureka, Bozja, Occult Crescent) count as duties, so recruiting there doesn't alert.
- **Alliance listings** (24 players) aren't supported.
- **iPhones** don't get timely alerts from a self-hosted ntfy server.
