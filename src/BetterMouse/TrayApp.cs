using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BetterMouse
{
    /// <summary>Tray icon + wiring between input engine, injector, clipboard and network.</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        readonly Control ui;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem statusItem, switchItem, edgeItem, firewallItem, sideMenu, activityItem;
        readonly ToolStripMenuItem loginMenu, loginEnableItem, loginDisableItem;
        bool serviceActive;
        readonly Icon iconOffline, iconOnline, iconRemote, iconControlled, iconProblem;
        readonly KvmEngine kvm;
        readonly Injector injector;
        readonly ClipboardSync clipboard;
        volatile NetworkManager net;
        volatile Settings settings;
        string cachedKeySource;
        byte[] cachedMasterKey;
        LinkState linkState = LinkState.Idle;
        string linkDetail = "Starting…";
        string peerName;
        bool controllingOther, controlledByOther, greeted;
        SettingsForm settingsForm;

        public TrayApp()
        {
            ui = new Control();
            ui.CreateControl();
            _ = ui.Handle;

            settings = Settings.Load();

            iconOffline = Icons.Create(Icons.Offline);
            iconOnline = Icons.Create(Icons.Online);
            iconRemote = Icons.Create(Icons.Remote);
            iconControlled = Icons.Create(Icons.Controlled);
            iconProblem = Icons.Create(Icons.Problem);

            statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
            switchItem = new ToolStripMenuItem("Switch to the other PC\tCtrl+Alt+F2", null, (s, e) => kvm.SwitchToOther());
            edgeItem = new ToolStripMenuItem("Switch at screen edge", null, (s, e) => ToggleEdgeSwitching());
            activityItem = new ToolStripMenuItem("Keep this PC active while I use the other PC", null, (s, e) =>
            {
                settings.MirrorActivity = !settings.MirrorActivity;
                SaveSettings();
            });
            firewallItem = new ToolStripMenuItem("Allow through Windows Firewall…", null, (s, e) => ConfigureFirewall());
            loginEnableItem = new ToolStripMenuItem("Enable (needs admin once)…", null, (s, e) => EnableLoginScreen());
            loginDisableItem = new ToolStripMenuItem("Turn off…", null, (s, e) => DisableLoginScreen());
            loginMenu = new ToolStripMenuItem("Login-screen control");
            loginMenu.DropDownItems.Add(loginEnableItem);
            loginMenu.DropDownItems.Add(loginDisableItem);
            sideMenu = new ToolStripMenuItem("Other PC is on my");
            foreach (var side in new[] { Edge.Left, Edge.Right, Edge.Top, Edge.Bottom })
            {
                var e1 = side;
                var item = new ToolStripMenuItem(side == Edge.Top ? "Top (above)" : side == Edge.Bottom ? "Bottom (below)" : side.Describe().Substring(0, 1).ToUpper() + side.Describe().Substring(1),
                    null, (s, e) => ChooseSide(e1)) { Tag = side };
                sideMenu.DropDownItems.Add(item);
            }
            var menu = new ContextMenuStrip();
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(switchItem);
            menu.Items.Add(edgeItem);
            menu.Items.Add(sideMenu);
            menu.Items.Add(activityItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings…", null, (s, e) => ShowSettings(false));
            menu.Items.Add("Reconnect now", null, (s, e) => net?.Kick());
            menu.Items.Add("Network check…", null, (s, e) => NetworkCheck.ShowDialog(() => settings, () => net));
            menu.Items.Add(firewallItem);
            menu.Items.Add(loginMenu);
            menu.Items.Add("Open log", null, (s, e) => OpenLog());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitThread());
            menu.Opening += (s, e) => RefreshMenu();

            tray = new NotifyIcon { Icon = iconOffline, Text = AppInfo.Name, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (s, e) => ShowSettings(false);

            // "Connected" for switching purposes = heard from the other PC within the last 2.5 s
            // (heartbeats arrive every 0.5 s), so a stalled link never captures this PC's mouse.
            kvm = new KvmEngine(SendWarm, () => net?.Current is Connection c && c.SilenceMs < 2500);
            injector = new Injector(SendWarm);
            kvm.LocalMouseMoved += () =>
            {
                injector.MarkLocalMouseMoved();
                net?.Current?.KeepWarm(); // heading for the edge? have the link awake before we get there
            };
            kvm.TakeBackRequested += injector.ReturnControl;
            kvm.LocalActivity += () => net?.Current?.Send(Msg.Activity);
            kvm.RemoteModeChanged += on => Ui(() => { controllingOther = on; UpdateTray(); });
            injector.ControlledChanged += on => Ui(() => { controlledByOther = on; UpdateTray(); });
            kvm.Start();
            kvm.Configure(settings.EdgeSwitching, settings.PeerSide);

            clipboard = new ClipboardSync(ui, () => settings, s => { var n = net; if (n != null) n.SendBulk(s); else s.Dispose(); }, () => net?.IsConnected == true);
            clipboard.Notice += text => Ui(() => Balloon(text, ToolTipIcon.Info));

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            Log.Info($"{AppInfo.Name} {AppInfo.Version} started on {Environment.MachineName}; screens {ScreenLayout.Current}");
            if (!settings.IsComplete)
            {
                Ui(() => ShowSettings(true));
            }
            else
            {
                StartNetwork();
                if (settings.Role == Role.Host) CheckFirewallInBackground();
            }
            UpdateTray();
        }

        /// <summary>Host: warn (once) if the firewall rule is missing, e.g. after the exe was moved.</summary>
        void CheckFirewallInBackground()
        {
            System.Threading.Tasks.Task.Run(() => Firewall.RuleLooksPresent()).ContinueWith(t =>
            {
                if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && !t.Result)
                    Ui(() =>
                    {
                        // No pop-up: shown in the tray tooltip/menu while nothing is connected.
                        firewallMissing = true;
                        Log.Warn("No firewall rule for this exe (new install, renamed or moved). " +
                                 "Tray menu > Allow through Windows Firewall.");
                        UpdateTray();
                    });
            });
        }

        bool firewallMissing;

        // ------------------------------------------------------------ network

        void StartNetwork()
        {
            StopNetwork();
            var s = settings;
            if (!s.IsComplete)
            {
                SetLink(LinkState.Idle, "Not set up yet – open Settings");
                return;
            }
            if (cachedKeySource != s.SecurityKey)
            {
                cachedMasterKey = Crypto.DeriveMasterKey(s.SecurityKey);
                cachedKeySource = s.SecurityKey;
            }

            var n = new NetworkManager(s, cachedMasterKey, Environment.MachineName, OnMessage);
            n.StatusChanged += (state, detail) => Ui(() => { if (net == n) SetLink(state, detail); });
            n.Connected += c =>
            {
                injector.EndControl();
                var current = settings;
                c.Send(Msg.Layout(current.PeerSide, current.LayoutChangedUtc)); // the PCs agree on left/right
                clipboard.OnConnected();
                Ui(() => { peerName = c.PeerName; Greet(); });
            };
            n.Disconnected += c =>
            {
                kvm.ForceLocal("connection lost");
                injector.EndControl();
                clipboard.OnDisconnected();
            };
            n.LastGoodAddressChanged += address => Ui(() =>
            {
                settings.LastGoodAddress = address;
                SaveSettings();
            });
            net = n;
            Log.Info($"Starting as {s.Role}" + (s.Role == Role.Client ? $" (host: '{s.PeerAddress}', last good: '{s.LastGoodAddress}', discovery: {s.Discovery})" : $" on port {s.Port}"));
            n.Start();
        }

        void StopNetwork()
        {
            var n = net;
            net = null;
            n?.Stop();
            kvm.ForceLocal("network restarted");
            injector.EndControl();
        }

        /// <summary>Sends input/control and keeps the link's fast heartbeat going while the mouse is in use.</summary>
        void SendWarm(byte[] message)
        {
            var c = net?.Current;
            if (c == null) return;
            c.KeepWarm();
            c.Send(message);
        }

        /// <summary>Network reader thread. Input is replayed right here for minimum latency.</summary>
        void OnMessage(Connection conn, byte[] m)
        {
            var r = new PacketReader(m);
            var type = (MsgType)m[0];
            if (type == MsgType.Enter || (type >= MsgType.MouseMove && type <= MsgType.Key))
                conn.KeepWarm(); // being driven: keep the Wi-Fi awake
            switch (type)
            {
                case MsgType.MouseMove:
                    injector.Move(r.Int32(), r.Int32());
                    break;
                case MsgType.MouseButton:
                    injector.Button((MouseButton)r.Byte(), r.Bool());
                    break;
                case MsgType.Wheel:
                    injector.Wheel(r.Int16(), r.Bool());
                    break;
                case MsgType.Key:
                {
                    var vk = r.UInt16();
                    var scan = r.UInt16();
                    var flags = r.Byte();
                    injector.Key(vk, scan, (flags & 1) != 0, (flags & 2) != 0);
                    break;
                }
                case MsgType.Enter:
                {
                    var edge = (Edge)r.Byte();
                    var ratio = r.Double();
                    kvm.OtherTookControl();
                    injector.Enter(edge, ratio);
                    break;
                }
                case MsgType.Leave:
                    kvm.CursorReturned(r.Double());
                    break;
                case MsgType.EndControl:
                    injector.EndControl();
                    break;
                case MsgType.Activity:
                    if (settings.MirrorActivity) injector.Nudge(conn.PeerName);
                    break;
                case MsgType.Layout:
                {
                    var theirs = (Edge)r.Byte();
                    var stamp = r.Int64();
                    Ui(() => OnPeerLayout(theirs, stamp));
                    break;
                }
                case MsgType.ClipBegin:
                case MsgType.ClipEntry:
                case MsgType.ClipData:
                case MsgType.ClipEnd:
                case MsgType.ClipCancel:
                    clipboard.Receive(m);
                    break;
            }
        }

        // ------------------------------------------------------------ system events

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.ConsoleDisconnect:
                case SessionSwitchReason.RemoteConnect:
                    kvm.ForceLocal("session " + e.Reason);
                    injector.ReturnControl(); // input can't reach a locked screen: hand the cursor back
                    // If login-screen control is on, release the connection so the SYSTEM service
                    // agent (which CAN reach the lock screen) takes over while we're locked.
                    if (ServiceControl.IsRunning())
                    {
                        serviceActive = true;
                        Log.Info("Locked: handing the connection to the login-screen service");
                        StopNetwork();
                    }
                    break;
                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.ConsoleConnect:
                    kvm.ResetKeyState();
                    if (serviceActive || net == null)
                    {
                        serviceActive = false;
                        Log.Info("Unlocked: taking the connection back from the login-screen service");
                        StartNetwork();
                    }
                    break;
            }
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                Log.Info("Going to sleep");
                kvm.ForceLocal("sleep");
                net?.Current?.CloseGracefully("This PC is going to sleep");
            }
            else if (e.Mode == PowerModes.Resume)
            {
                Log.Info("Resumed from sleep: reconnecting");
                kvm.ResetKeyState();
                net?.Kick();
            }
        }

        // ------------------------------------------------------------ UI

        void SetLink(LinkState state, string detail)
        {
            var previous = linkState;
            linkState = state;
            linkDetail = detail;
            if (state != LinkState.Connected) controlledByOther = false;
            if (state == LinkState.AuthFailed && previous != LinkState.AuthFailed)
                Balloon(detail + ". Use the same security key on both PCs (Settings).", ToolTipIcon.Warning);
            UpdateTray();
        }

        void Greet()
        {
            if (greeted) return;
            greeted = true;
            var s = settings;
            var side = s.PeerSide.ToString().ToLowerInvariant();
            Balloon($"Connected to {peerName}. " +
                    (s.EdgeSwitching ? $"Move the mouse off the {side} edge to use it. " : "") +
                    "Ctrl+Alt+F1 / F2 switch PCs.", ToolTipIcon.Info);
        }

        void UpdateTray()
        {
            Icon icon;
            string text;
            switch (linkState)
            {
                case LinkState.Connected:
                    icon = controllingOther ? iconRemote : controlledByOther ? iconControlled : iconOnline;
                    text = controllingOther ? $"Controlling {peerName}" : controlledByOther ? $"Controlled by {peerName}" : linkDetail;
                    break;
                case LinkState.AuthFailed:
                case LinkState.Error:
                    icon = iconProblem;
                    text = linkDetail;
                    break;
                default:
                    icon = iconOffline;
                    text = linkDetail;
                    break;
            }
            if (firewallMissing && linkState != LinkState.Connected && settings.Role == Role.Host)
            {
                icon = iconProblem;
                text = "Firewall rule missing: right-click > Allow through Windows Firewall";
            }
            tray.Icon = icon;
            SetTrayTip(AppInfo.Name + "\n" + text);
            statusItem.Text = text;
        }

        /// <summary>
        /// Windows allows 127 characters in a tray tooltip, but WinForms on .NET Framework rejects
        /// more than 63, too short for the name plus a status. Set the field directly when possible.
        /// </summary>
        void SetTrayTip(string tip)
        {
            if (tip.Length > 127) tip = tip.Substring(0, 126) + "…";
            try
            {
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var type = typeof(NotifyIcon);
                type.GetField("text", flags).SetValue(tray, tip);
                if ((bool)type.GetField("added", flags).GetValue(tray))
                    type.GetMethod("UpdateIcon", flags).Invoke(tray, new object[] { true });
            }
            catch
            {
                tray.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
            }
        }

        void RefreshMenu()
        {
            var s = settings;
            statusItem.Text = linkDetail;
            switchItem.Enabled = net?.IsConnected == true;
            edgeItem.Checked = s.EdgeSwitching;
            activityItem.Checked = s.MirrorActivity;
            firewallItem.Visible = s.Role == Role.Host;
            sideMenu.Text = $"{(net?.Current?.PeerName ?? "Other PC")} is on my";
            foreach (ToolStripMenuItem item in sideMenu.DropDownItems)
                item.Checked = (Edge)item.Tag == s.PeerSide;

            bool installed = ServiceControl.IsInstalled();
            serviceActive = installed && ServiceControl.IsRunning();
            loginMenu.Text = "Login-screen control: " + (installed ? (serviceActive ? "on" : "installed") : "off");
            loginEnableItem.Text = installed ? "Re-apply current settings (admin)…" : "Enable (needs admin once)…";
            loginDisableItem.Visible = installed;
        }

        /// <summary>The user picked a side here (tray menu or Settings): apply and tell the other PC.</summary>
        void ChooseSide(Edge side)
        {
            ApplySide(side, DateTime.UtcNow.Ticks);
            net?.Send(Msg.Layout(side, settings.LayoutChangedUtc));
        }

        void ApplySide(Edge side, long stamp)
        {
            settings.PeerSide = side;
            settings.LayoutChangedUtc = stamp;
            kvm.Configure(settings.EdgeSwitching, side);
            SaveSettings();
            Log.Info($"Other PC is now on the {side} of this PC");
        }

        void OnPeerLayout(Edge theirs, long stamp)
        {
            var s = settings;
            var adopt = EdgeExtensions.SyncLayout(s.PeerSide, s.LayoutChangedUtc, s.Role, theirs, stamp);
            if (adopt == null) return;
            ApplySide(adopt.Value, stamp);
            var who = peerName ?? "The other PC";
            Balloon($"Screen layout synced: {who} is {(adopt.Value.IsHorizontal() ? "on the " : "")}{adopt.Value.Describe()} " +
                    $"{(adopt.Value.IsHorizontal() ? "of " : "")}this PC.", ToolTipIcon.Info);
        }

        void ToggleEdgeSwitching()
        {
            settings.EdgeSwitching = !settings.EdgeSwitching;
            kvm.Configure(settings.EdgeSwitching, settings.PeerSide);
            SaveSettings();
        }

        void ShowSettings(bool firstRun)
        {
            if (settingsForm != null)
            {
                settingsForm.Activate();
                return;
            }
            var sideAtOpen = settings.PeerSide;
            using (settingsForm = new SettingsForm(settings, firstRun, net?.Current?.PeerName))
            {
                var result = settingsForm.ShowDialog();
                var updated = settingsForm.Result;
                var autostart = settingsForm.StartWithWindows;
                settingsForm = null;
                if (result != DialogResult.OK || updated == null)
                {
                    if (firstRun && !settings.IsComplete) SetLink(LinkState.Idle, "Not set up yet – open Settings");
                    return;
                }

                var old = settings;
                updated.LastGoodAddress = updated.PeerAddress == old.PeerAddress && updated.Port == old.Port ? old.LastGoodAddress : "";
                bool sideChanged = updated.PeerSide != sideAtOpen;
                if (sideChanged)
                {
                    updated.LayoutChangedUtc = DateTime.UtcNow.Ticks;
                }
                else
                {
                    // Untouched in the dialog: keep the current side (the other PC may have synced it meanwhile).
                    updated.PeerSide = old.PeerSide;
                    updated.LayoutChangedUtc = old.LayoutChangedUtc;
                }
                settings = updated;
                SaveSettings();
                try { Autostart.Set(autostart); }
                catch (Exception ex) { Log.Error("Could not change autostart", ex); }
                kvm.Configure(updated.EdgeSwitching, updated.PeerSide);
                if (sideChanged) net?.Send(Msg.Layout(updated.PeerSide, updated.LayoutChangedUtc));

                // Ask before listening, otherwise Windows shows its own pop-up first (which only
                // allows "Private" networks).
                if (updated.Role == Role.Host && (firstRun || old.Role != Role.Host) && !Firewall.RuleLooksPresent())
                {
                    var answer = MessageBox.Show(
                        "This PC is the Host. Add a Windows Firewall rule so the other PC can always connect " +
                        "(also when the internet is down or the network is marked Public)?\n\nWindows will ask for admin permission once.",
                        AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer == DialogResult.Yes) ConfigureFirewall();
                }

                bool networkChanged = firstRun || net == null || updated.Role != old.Role || updated.Port != old.Port ||
                                      updated.SecurityKey != old.SecurityKey || updated.PeerAddress != old.PeerAddress ||
                                      updated.Discovery != old.Discovery;
                if (networkChanged)
                {
                    greeted = false;
                    StartNetwork();
                }
            }
        }

        void ConfigureFirewall()
        {
            bool ok = Firewall.ConfigureWithElevation(out var message);
            if (ok)
            {
                firewallMissing = false;
                UpdateTray();
            }
            MessageBox.Show(message, AppInfo.Name, MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        void EnableLoginScreen()
        {
            if (!settings.IsComplete)
            {
                MessageBox.Show("Set up the connection first (role, security key, and the host address on a client), then try again.",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ShowSettings(false);
                return;
            }
            var answer = MessageBox.Show(
                "Turn on login-screen control for THIS PC?\n\n" +
                "It installs a small Windows service so your shared mouse and keyboard can reach this PC's " +
                "login, lock and UAC screens. You still type your own password — it is not bypassed.\n\n" +
                "Windows will ask for administrator permission once. Do this on the PC you want to unlock remotely.",
                AppInfo.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;
            bool ok = ServiceControl.Enable(out var message);
            UpdateTray();
            MessageBox.Show(message, AppInfo.Name, MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        void DisableLoginScreen()
        {
            bool ok = ServiceControl.Disable(out var message);
            serviceActive = false;
            UpdateTray();
            MessageBox.Show(message, AppInfo.Name, MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        void SaveSettings()
        {
            try { settings.Save(); }
            catch (Exception ex)
            {
                Log.Error("Could not save settings", ex);
                Balloon("Could not save settings: " + ex.Message, ToolTipIcon.Error);
            }
        }

        void OpenLog()
        {
            try
            {
                var path = Log.FilePath;
                if (path != null && File.Exists(path)) Process.Start("notepad.exe", "\"" + path + "\"");
                else Process.Start("explorer.exe", "\"" + Settings.DataDirectory + "\"");
            }
            catch (Exception ex)
            {
                Log.Error("Could not open log", ex);
            }
        }

        /// <summary>
        /// No pop-up notifications, by request: events only go to the log. The tray icon's colour,
        /// tooltip and menu still show the current state.
        /// </summary>
        void Balloon(string text, ToolTipIcon icon)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (icon == ToolTipIcon.Info) Log.Info(text);
            else Log.Warn(text);
        }

        void Ui(Action action)
        {
            if (ui.IsDisposed) return;
            try { ui.BeginInvoke(action); }
            catch (InvalidOperationException) { /* shutting down */ }
        }

        protected override void ExitThreadCore()
        {
            Log.Info("Exiting");
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            tray.Visible = false;
            try { StopNetwork(); } catch (Exception ex) { Log.Error("Stop failed", ex); }
            kvm.Dispose();
            clipboard.Dispose();
            tray.Dispose();
            Log.Flush();
            base.ExitThreadCore();
        }
    }
}
