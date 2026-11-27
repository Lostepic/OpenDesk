using System.Diagnostics;
using System.Net;
using Microsoft.Win32;

namespace OpenDesk;

internal sealed class HostWindow : Form
{
    private static readonly Color Ink = Color.FromArgb(222, 235, 235);
    private static readonly Color Muted = Color.FromArgb(162, 185, 186);
    private static readonly Color Surface = Color.FromArgb(30, 47, 52);
    private static readonly Color Mint = Color.FromArgb(138, 239, 207);

    private readonly ComboBox displays = Picker("Display");
    private readonly ComboBox networks = Picker("Network adapter");
    private readonly ComboBox profiles = Picker("Stream quality");
    private readonly Button share = ActionButton("Start sharing", primary: true);
    private readonly Button detect = ActionButton("Refresh");
    private readonly Button copy = ActionButton("Copy link");
    private readonly Button disconnect = ActionButton("Disconnect");
    private readonly CheckBox touchControl = new()
    {
        Text = "Allow touch control from approved viewers", Checked = true, AutoSize = true,
        ForeColor = Ink, Margin = new Padding(0, 4, 0, 6)
    };
    private readonly ListBox deviceList = new()
    {
        AccessibleName = "Connected devices", Dock = DockStyle.Fill, IntegralHeight = false,
        BackColor = Surface, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 4, 10, 0)
    };
    private readonly TextBox invite = new()
    {
        AccessibleName = "Viewer link", Dock = DockStyle.Fill, ReadOnly = true,
        BackColor = Surface, ForeColor = Ink, Margin = new Padding(0, 4, 10, 0)
    };
    private readonly Label status = TextLabel("Ready to share", Mint, bold: true);
    private readonly Label deviceStatus = TextLabel("Starting LAN discovery…", Muted, bottom: 12);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 400 };
    private ShareSession? session;
    private ViewerPreferences? preferences;
    private DeviceConnections? devices;
    private string? manualDisplayReservation;
    private bool switchingNetwork;
    private bool disconnecting;
    private volatile bool touchAllowed = true;
    private volatile StreamProfile selectedProfile = StreamProfile.All[1];
    private bool refreshing;
    private bool changing;
    private bool closeRequested;
    private bool locked;
    private long copiedAt = -2500;

    internal HostWindow()
    {
        Text = "OpenDesk";
        BackColor = Color.FromArgb(16, 30, 34);
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 654);
        MinimumSize = new Size(560, 625);
        StartPosition = FormStartPosition.CenterScreen;

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1,
            Padding = new Padding(30, 24, 30, 22)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(body);
        var title = TextLabel("OpenDesk", Ink);
        title.Font = new Font("Segoe UI", 29F, FontStyle.Bold);
        body.Controls.Add(title);
        body.Controls.Add(deviceStatus);
        body.Controls.Add(WithButton(deviceList, disconnect, "Connected devices", fieldHeight: 66));
        body.Controls.Add(touchControl);

        var settings = ActionButton("Display settings");
        settings.Click += (_, _) => OpenDisplaySettings();
        body.Controls.Add(WithButton(displays, detect, "Display"));
        body.Controls.Add(WithButton(networks, null, "Network adapter"));
        body.Controls.Add(WithButton(profiles, null, "Stream quality"));

        var actions = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 3, 0, 14)
        };
        actions.Controls.Add(share);
        actions.Controls.Add(settings);
        body.Controls.Add(actions);

        body.Controls.Add(new Panel { Height = 1, Dock = DockStyle.Top, BackColor = Surface });
        status.Margin = new Padding(0, 11, 0, 0);
        body.Controls.Add(status);
        body.Controls.Add(WithButton(invite, copy, "Viewer link"));
        body.Controls.Add(TextLabel("Open the link in a browser on the same trusted network.", Muted));

        profiles.Items.AddRange(StreamProfile.All);
        profiles.SelectedIndex = 1;
        profiles.SelectedIndexChanged += (_, _) =>
        {
            if (profiles.SelectedItem is StreamProfile profile) selectedProfile = profile;
        };
        detect.Click += (_, _) => RefreshChoices();
        networks.SelectedIndexChanged += async (_, _) =>
        {
            if (!refreshing && IsHandleCreated)
                await StartDeviceDiscoveryAsync();
        };
        Shown += async (_, _) => await StartDeviceDiscoveryAsync();
        share.Click += async (_, _) => await ToggleAsync();
        disconnect.Click += async (_, _) => await DisconnectDeviceAsync();
        touchControl.CheckedChanged += (_, _) => touchAllowed = touchControl.Checked;
        deviceList.SelectedIndexChanged += (_, _) => UpdateDisconnectButton();
        copy.Click += (_, _) =>
        {
            if (session is null)
                return;
            try
            {
                Clipboard.SetText(session.Invite);
                status.Text = "Viewer link copied";
                copiedAt = Environment.TickCount64;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                status.Text = "Clipboard busy. Select and copy the viewer link.";
            }
        };

        timer.Tick += async (_, _) =>
        {
            UpdateDeviceList();
            if (session is null || changing)
                return;
            if (session.CaptureError is { } error)
            {
                await StopAsync(error);
                RefreshChoices();
            }
            else if (Environment.TickCount64 - copiedAt > 2500)
            {
                int viewers = session.Viewers;
                status.Text = viewers switch
                {
                    0 => "Sharing · waiting for a viewer",
                    1 => "Sharing · 1 viewer",
                    _ => $"Sharing · {viewers} viewers"
                };
            }
        };
        SystemEvents.SessionSwitch += SessionSwitched;
        timer.Start();
        RefreshChoices();
        UpdateControls();
    }

    private void UpdateDeviceList()
    {
        var connected = switchingNetwork || locked ? [] : devices?.GetDevices() ?? [];
        if (devices is not null && networks.SelectedItem is NetworkChoice network && !locked && !switchingNetwork)
        {
            int live = connected.Count(device => device.Live);
            deviceStatus.Text = live > 0
                ? $"LAN viewers · {live} connected · {network.Address}"
                : devices.TopologyError ?? $"Ready for LAN viewers · {network.Address}";
        }
        if (deviceList.Items.Cast<ConnectedDevice>().SequenceEqual(connected))
            return;

        Guid? selected = (deviceList.SelectedItem as ConnectedDevice)?.Id;
        deviceList.BeginUpdate();
        deviceList.Items.Clear();
        deviceList.Items.AddRange(connected);
        if (selected is not null)
            deviceList.SelectedIndex = Array.FindIndex(connected, device => device.Id == selected);
        else if (connected.Length == 1)
            deviceList.SelectedIndex = 0;
        deviceList.EndUpdate();
        UpdateDisconnectButton();
    }

    private async Task DisconnectDeviceAsync()
    {
        if (!disconnect.Enabled || devices is not { } current || deviceList.SelectedItem is not ConnectedDevice device)
            return;
        disconnecting = true;
        UpdateDisconnectButton();
        try
        {
            await current.DisconnectAsync(device.Id);
        }
        finally
        {
            disconnecting = false;
            UpdateDeviceList();
            UpdateDisconnectButton();
        }
    }

    private void UpdateDisconnectButton() =>
        disconnect.Enabled = !switchingNetwork && !disconnecting && !locked && deviceList.SelectedIndex >= 0;

    private void RefreshChoices()
    {
        refreshing = true;
        string? selectedDisplay = (displays.SelectedItem as DisplayChoice)?.DeviceName;
        var selectedNetwork = (networks.SelectedItem as NetworkChoice)?.Address;
        var availableDisplays = DisplayChoice.GetActive();
        var availableNetworks = NetworkChoice.GetAvailable();
        displays.Items.Clear();
        displays.Items.AddRange(availableDisplays);
        int displayIndex = Array.FindIndex(availableDisplays, item => item.DeviceName == selectedDisplay);
        if (availableDisplays.Length > 0)
            displays.SelectedIndex = Math.Max(0, displayIndex);
        networks.Items.Clear();
        networks.Items.AddRange(availableNetworks);
        networks.SelectedIndex = Math.Max(0, Array.FindIndex(availableNetworks, item =>
            item.Address.Equals(selectedNetwork)));
        refreshing = false;
        UpdateControls();
    }

    private async Task StartDeviceDiscoveryAsync()
    {
        if (switchingNetwork || closeRequested)
            return;
        switchingNetwork = true;
        UpdateControls();
        try
        {
            if (devices is not null)
                await devices.DisposeAsync();
            devices = null;
            if (locked || networks.SelectedItem is not NetworkChoice network || IPAddress.IsLoopback(network.Address))
            {
                deviceStatus.Text = "LAN discovery paused";
                return;
            }

            try
            {
                preferences ??= new ViewerPreferences();
                var next = new DeviceConnections(network, ApproveViewerAsync,
                    () => Volatile.Read(ref manualDisplayReservation), () => touchAllowed, () => selectedProfile, preferences);
                try
                {
                    await next.StartAsync();
                    devices = next;
                }
                catch
                {
                    await next.DisposeAsync();
                    throw;
                }
            }
            catch (Exception error)
            {
                deviceStatus.Text = $"Could not start LAN discovery. {error.Message}";
            }
        }
        finally
        {
            switchingNetwork = false;
            UpdateControls();
            if (closeRequested && !IsDisposed)
                BeginInvoke(Close);
        }
    }

    private Task<bool> ApproveViewerAsync(IPAddress peer)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || !IsHandleCreated)
            return Task.FromResult(false);
        BeginInvoke(() => result.TrySetResult(!locked && !closeRequested && !switchingNetwork &&
            MessageBox.Show(this, $"Allow the device at {peer} to connect as a virtual display?",
                "OpenDesk connection", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes));
        return result.Task;
    }

    private async Task ToggleAsync()
    {
        if (changing)
            return;
        if (session is not null)
        {
            await StopAsync("Sharing stopped");
            return;
        }

        if (locked || networks.SelectedItem is not NetworkChoice network ||
            displays.SelectedItem is not DisplayChoice display ||
            profiles.SelectedItem is not StreamProfile profile)
            return;
        Volatile.Write(ref manualDisplayReservation, display.DeviceName);
        if (devices?.IsUsing(display.DeviceName) == true)
        {
            Volatile.Write(ref manualDisplayReservation, null);
            status.Text = "That display is connected to a LAN viewer.";
            return;
        }

        changing = true;
        UpdateControls();
        var next = new ShareSession(network, display, profile);
        try
        {
            await next.StartAsync();
            session = next;
            invite.Text = next.Invite;
            status.Text = "Sharing · waiting for a viewer";
        }
        catch (Exception error)
        {
            Volatile.Write(ref manualDisplayReservation, null);
            await next.DisposeAsync();
            status.Text = "Could not start sharing";
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            changing = false;
            UpdateControls();
        }

        if (closeRequested)
        {
            if (session is null)
                BeginInvoke(Close);
            else
                await StopAsync("Sharing stopped");
        }
        else if (locked && session is not null)
            await StopAsync("Sharing stopped when Windows locked");
    }

    private async Task StopAsync(string message)
    {
        if (session is null || changing)
            return;

        changing = true;
        var current = session;
        session = null;
        invite.Clear();
        UpdateControls();
        try
        {
            await current.DisposeAsync();
            status.Text = message;
        }
        finally
        {
            Volatile.Write(ref manualDisplayReservation, null);
            changing = false;
            UpdateControls();
            if (closeRequested && !IsDisposed)
                BeginInvoke(Close);
        }
    }

    private void SessionSwitched(object sender, SessionSwitchEventArgs args)
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(async () =>
            {
                if (args.Reason == SessionSwitchReason.SessionLock)
                {
                    locked = true;
                    await StopAsync("Sharing stopped when Windows locked");
                    await StartDeviceDiscoveryAsync();
                }
                else if (args.Reason == SessionSwitchReason.SessionUnlock)
                {
                    locked = false;
                    await StartDeviceDiscoveryAsync();
                }
                UpdateControls();
            });
    }

    private void UpdateControls()
    {
        bool sharing = session is not null;
        share.Text = sharing ? "Stop sharing" : "Start sharing";
        share.Enabled = !changing && !locked && displays.Items.Count > 0;
        copy.Enabled = sharing && !changing;
        displays.Enabled = profiles.Enabled = detect.Enabled = !sharing && !changing;
        networks.Enabled = !sharing && !changing && !switchingNetwork;
        UpdateDisconnectButton();
    }

    private void OpenDisplaySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (changing || switchingNetwork || session is not null || devices is not null)
        {
            e.Cancel = true;
            closeRequested = true;
            if (!changing && !switchingNetwork)
                BeginInvoke(async () =>
                {
                    switchingNetwork = true;
                    if (devices is not null)
                        await devices.DisposeAsync();
                    devices = null;
                    switchingNetwork = false;
                    await StopAsync("Sharing stopped");
                    if (!IsDisposed)
                        BeginInvoke(Close);
                });
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionSwitch -= SessionSwitched;
            timer.Dispose();
        }
        base.Dispose(disposing);
    }

    private static ComboBox Picker(string name) => new()
    {
        AccessibleName = name, Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
        BackColor = Surface, ForeColor = Ink, FlatStyle = FlatStyle.Flat,
        Margin = new Padding(0, 4, 10, 0)
    };

    private static Label TextLabel(string text, Color color, int bottom = 0, bool bold = false) => new()
    {
        AutoSize = true, MaximumSize = new Size(530, 0), Text = text, ForeColor = color,
        Margin = new Padding(0, 0, 0, bottom),
        Font = bold ? new Font("Segoe UI", 10F, FontStyle.Bold) : null
    };

    private static Button ActionButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text, Anchor = AnchorStyles.Left, AutoSize = true, Height = 34,
            MinimumSize = new Size(96, 34), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Mint : Surface, ForeColor = primary ? Color.FromArgb(13, 38, 33) : Ink,
            Margin = new Padding(0, 0, 10, 0), Padding = new Padding(8, 0, 8, 0)
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private static Control WithButton(Control field, Button? button, string label, int fieldHeight = 35)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2,
            Margin = new Padding(0, 5, 0, 7)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, fieldHeight));
        var heading = TextLabel(label, Ink);
        row.Controls.Add(heading, 0, 0);
        row.SetColumnSpan(heading, 2);
        row.Controls.Add(field, 0, 1);
        if (button is not null)
        {
            button.Margin = Padding.Empty;
            row.Controls.Add(button, 1, 1);
        }
        else
        {
            row.SetColumnSpan(field, 2);
            field.Margin = new Padding(0, 4, 0, 0);
        }
        return row;
    }
}
