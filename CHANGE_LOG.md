# Changelog

Notable changes to PF Notification. Versions match the plugin version shown in `/xlplugins`.

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
