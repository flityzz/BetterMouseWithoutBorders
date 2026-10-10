# Better Mouse Without Borders

Use one mouse and keyboard across two Windows PCs, like Mouse Without Borders, but:

- **No admin rights needed on the other PC.** It's one `.exe`. No installer, no service, no driver.
  It runs on the .NET Framework 4.8 that already ships with Windows 10 and 11.
- **Local network only.** It talks straight to the other PC's IP address on your LAN. It never
  uses the internet, a cloud service or DNS, so it keeps working when your internet goes down.
- **Reconnects by itself, forever.** A heartbeat every 0.5 s detects a dead link within 4 s.
  It then redials within a few seconds and keeps retrying (at most 3 s apart) until the other PC
  is back. It also reconnects straight away after sleep, a Wi-Fi change or a new IP address.

## Setup (5 minutes)

1. **Turn off PowerToys → Mouse Without Borders on both PCs.** Two tools fighting over the
   screen edge will misbehave.
2. Copy `dist\BetterMouseWithoutBorders.exe` to both PCs, into any folder you like (for example `Documents\BetterMouseWithoutBorders`).
   If Windows shows "Windows protected your PC", click **More info → Run anyway**. That needs no admin rights.
3. **On your PC (the one where you're admin): run it and choose _Host_.**
   - Click **Generate** to make a security key, and write it down.
   - Note the first **IP address** shown at the top of Settings (for example `192.168.1.20 (Wi-Fi)`).
   - When it asks about the Windows Firewall, click **Yes**, then approve the UAC prompt (one time only).
     This adds one rule that works on *every* network type. Windows' own pop-up only allows "Private"
     networks, and a network that loses internet can turn "Public" or "Unidentified". That's a common
     reason these tools stop working when the internet drops.
4. **On the other PC: run it and choose _Client_.**
   - Type the host's IP address and the **same** security key, then click OK.
5. Tell the app where the other PC sits: in **Settings → Where is the other PC?**, click the box
   on the side where the other PC's screen actually is on your desk. You can also use the tray menu
   → **"<other PC> is on my" → Left / Right / Top / Bottom**. You only need to do this on **one** PC:
   the other PC mirrors it automatically. If you say "the laptop is on my left" here, the laptop
   learns "the desktop is on my right".
6. Push the mouse off that screen edge. 🎉

When you switch from PC A to PC B, the cursor always comes back through the edge it went in, at the
same height.

### Upgrading from BetterMouse (1.3 or earlier)

The app was renamed in 1.4 and the exe is now `BetterMouseWithoutBorders.exe`.
1. On both PCs: tray → **Exit** the old version, then start the new exe. Your settings (role, key,
   side…) and "start with Windows" carry over automatically. You can delete the old `BetterMouse.exe`.
2. **On the host only:** the old firewall rule belonged to the old exe. Right-click the tray icon →
   **Allow through Windows Firewall…** once (one admin prompt). Until you do, the tray icon is red
   and says so.

## Everyday use

| | |
|---|---|
| Switch PCs | Push the mouse past the configured screen edge (not while a mouse button is held down) |
| `Ctrl+Alt+F2` | Jump to the other PC |
| `Ctrl+Alt+F1` | Come back to this PC (works on either PC, always) |
| Clipboard | Copy on one PC, paste on the other: text, images and files/folders (up to 100 MB, configurable) |
| Tray icon | grey = searching · green = connected · blue = you're controlling the other PC · purple = being controlled · red = problem (hover or right-click for details) |

Right-click the tray icon for Settings, which side the other PC is on, **Reconnect now**,
**Network check**, the log, and turning edge switching on or off.

**Keep this PC active while I use the other PC** (on by default, per PC). While you're really using
one PC, the other PC's Windows idle timer is reset every 30 s with an invisible zero-pixel mouse
nudge. Teams stays *Available* and the screen doesn't sleep or lock while you work at the desk. It
only mirrors real activity: step away and both PCs go idle and *Away* as usual. Turn it off on a PC
(Settings or tray menu) to let that PC idle on its own schedule.
Settings asks whether to **start with Windows** (on by default). This uses your user's Run key, so it needs no admin rights.

## Why it survives network trouble

- **Host** listens on every network adapter (Ethernet, Wi-Fi, direct cable; IPv4 and IPv6). When the
  client dials in again, the host drops the stale connection and takes the new one.
- **Client** dials the address you typed **and** the last address that worked, both at the same time.
  It also runs a LAN broadcast search, so it finds the host even if the host's IP changes. That
  search is authenticated with your key, so only *your* host answers. The client only makes
  outgoing connections, so it never needs a firewall rule (and never needs admin).
- If the link stalls while you're controlling the other PC, your own cursor returns within about 2 s.
  `Ctrl+Alt+F1` works instantly at any time.
- Session lock, sleep and resume are handled. Held keys and mouse buttons are always released on
  the other PC, so nothing gets stuck.

**If the router itself dies**, every LAN tool loses the connection. That includes this one, because
there's no network left between the PCs. A cheap Ethernet cable straight between the two PCs (or a
small switch) keeps working without a router. Better Mouse Without Borders uses it automatically, and the LAN search
finds the other PC on the cable's `169.254.x.x` addresses.

## VPNs (FortiClient and others)

When a VPN is connected on one of the PCs, two different things can stop the PCs seeing each other.
Tray menu → **Network check** (on the PC with the VPN) tells you which one you have:

1. **The VPN's networks overlap your home network.** For example, the company also uses `192.168.x.x`,
   so Windows sends traffic for your other PC into the VPN tunnel. The app handles this
   automatically: when the other PC is on the same subnet as your Wi-Fi/Ethernet adapter, it also
   connects through that adapter directly. The Network check then says *"Works … connects that way
   automatically"*.
2. **The VPN is set to block the local network.** FortiClient calls this "exclusive routing", or
   "local LAN access" turned off; other VPNs call it a "kill switch". It's a security setting,
   usually controlled by your company's IT, and enforced below the network routes. The app doesn't
   try to get around it. Your options:
   - ask IT whether your VPN profile can allow **local LAN access / split tunnelling**;
   - if you created the VPN connection yourself in FortiClient, check that connection's settings for
     local LAN access;
   - otherwise, the app reconnects by itself a few seconds after the VPN disconnects.

Third-party security suites with their own firewall (Kaspersky, Norton, …) may also need
Better Mouse Without Borders allowed on the host.

## Limits (Windows rules for non-admin apps)

In normal (no-admin) mode, on the PC being controlled Windows does not let the app type into:
- the lock screen / login screen, `Ctrl+Alt+Del`, or UAC prompts
- windows of programs running "as administrator"

Use that PC's own keyboard for those, or turn on **login-screen control** below. Keys like `Win+L`
always act on the PC you physically press them on. Better Mouse Without Borders supports two PCs.

## Login-screen control (optional, needs admin once)

By default the app runs as a normal user with no admin rights. To also reach a PC's **login, lock
and UAC screens** (so you can type your password there from the other PC), that PC needs a small
Windows service — the same approach Mouse Without Borders uses, and the same reason it needs admin:
only a SYSTEM service is allowed onto Windows' protected "secure desktop". **It does not bypass your
password — you still type it;** it only lets your shared keyboard and mouse reach that screen.

Turn it on **on the PC you want to unlock remotely**:
- Tray icon → **Login-screen control → Enable (needs admin once)…**, approve the UAC prompt. Or from
  an administrator terminal: `BetterMouseWithoutBorders.exe --install-service`.
- Turn it off with the tray menu, or `BetterMouseWithoutBorders.exe --uninstall-service` (admin).

How it works: while that PC is unlocked, the normal no-admin app runs as usual. When it locks or sits
at the login screen, a background service (LocalSystem) takes over the connection and injects your
mouse and keyboard into the secure desktop; on unlock, control hands back. Each hand-off is a
one-to-two-second reconnect. The service stores a machine-wide copy of your settings in
`%ProgramData%\BetterMouseWithoutBorders\` (the key encrypted for this machine). If you later change
the role, key or host address, re-run **Enable** (or `--install-service`) to update it.

Notes:
- Do this on the PC being unlocked. On a work laptop where you are not an administrator, only your
  IT can install it.
- It can't *unlock* an already-locked PC it was never set up on, and it can't run where the whole
  machine is managed to forbid new services.

## Security

You type the same security key on both PCs. It's stretched with PBKDF2-SHA256, and every connection
derives fresh session keys from random nonces. All traffic (keystrokes, clipboard) is encrypted
with AES-256-CTR and authenticated with HMAC-SHA256, with sequence numbers to stop replays. A PC
with the wrong key gets nothing and is shown as "security key does not match". The key is stored
encrypted for your Windows user (DPAPI).

## Files

- Settings and log: `%APPDATA%\BetterMouseWithoutBorders\` (`BetterMouseWithoutBorders.ini`, `BetterMouseWithoutBorders.log`).
  For a portable install, put a `BetterMouseWithoutBorders.ini` next to the exe and everything stays in that folder.
- Files received through the clipboard: `%TEMP%\BetterMouseWithoutBorders\Clipboard\` (only the latest copy is kept).
- Default port: TCP and UDP `15155`. You can change it, but it must be the same on both PCs.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Client stays grey, "Host not reachable" | Check the IP (Settings on the host lists it). Check that the app runs on the host. On the host, right-click the tray icon → **Allow through Windows Firewall…** |
| Red, "Security key does not match" | Type exactly the same key on both PCs |
| Red, "Both PCs are set to Client/Host" | One PC must be Host and the other Client |
| You must go right to reach a PC that's on your left | Tray menu → "<other PC> is on my" → Left (the other PC mirrors it) |
| Cursor jumps to the wrong PC / doesn't switch | Check the side as above. Turn off Mouse Without Borders on both PCs |
| Stops working when the VPN connects | Tray menu → **Network check** on the PC with the VPN, then see *VPNs* above |
| Can't type into an admin window on the other PC | A Windows limit for non-admin apps. See *Limits* |

The log (tray → **Open log**) records every connect and disconnect with its reason.

## Building from source

Needs the .NET SDK (any recent version) on the build machine only:

```powershell
.\build.ps1          # builds, runs the tests, writes dist\BetterMouseWithoutBorders.exe
```

The project is in `src/BetterMouse` and the tests are in `tests/BetterMouse.Tests`. The tests run
real network checks over loopback: handshake, wrong key, heartbeat timeout, reconnect after the host
restarts, discovery, and input jumping ahead of large clipboard transfers. The tests that drive the
mouse are opt-in: `$env:BM_INTERACTIVE=1`.
