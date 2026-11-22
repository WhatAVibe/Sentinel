using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PCMonitor.Agent.Services;

namespace PCMonitor.Agent.Services;

public sealed class TrayService : IDisposable
{
    private readonly AgentHost _host;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _awayItem;
    private readonly ToolStripMenuItem _disarmItem;
    private readonly ToolStripMenuItem _codeItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly Icon _generatedIcon;
    private readonly Action _show;
    private readonly Action _settings;
    private readonly Action _disarm;
    private readonly Action _exit;

    public TrayService(AgentHost host, Action show, Action settings, Action disarm, Action exit)
    {
        _host = host;
        _show = show;
        _settings = settings;
        _disarm = disarm;
        _exit = exit;
        _generatedIcon = BuildIcon();

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _awayItem = new ToolStripMenuItem("Away mode", null, (_, _) => _ = ToggleAwayAsync())
        {
            CheckOnClick = false
        };
        _disarmItem = new ToolStripMenuItem("Unlock / disable away…", null, (_, _) => _disarm())
        {
            Visible = false
        };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = StartupManager.IsEnabled()
        };
        _codeItem = new ToolStripMenuItem("Copy 2FA code", null, (_, _) => CopyCode());

        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()),
            ShowImageMargin = false,
            BackColor = Color.FromArgb(18, 22, 28),
            ForeColor = Color.FromArgb(230, 233, 239),
            Font = new Font("Segoe UI", 9f)
        };
        menu.Items.Add(new ToolStripMenuItem("Open Sentinel", null, (_, _) => _show()) { Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Lock PC", null, (_, _) => PowerActions.Lock()));
        menu.Items.Add(new ToolStripMenuItem("Secure lock (2FA)", null, (_, _) => _host.EnterSecureLock("Locked from the tray menu.")));
        menu.Items.Add(_codeItem);
        menu.Items.Add(new ToolStripMenuItem("Take screenshot", null, (_, _) => _ = _host.CaptureAndUploadAsync("tray")));
        menu.Items.Add(_awayItem);
        menu.Items.Add(_disarmItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => _settings()));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _exit()));
        foreach (ToolStripItem item in menu.Items) item.ForeColor = Color.FromArgb(230, 233, 239);
        menu.Opening += (_, _) => UpdateCodeItem();

        _icon = new NotifyIcon
        {
            Icon = _generatedIcon,
            Text = "Sentinel",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => _show();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _show();
        };
        _icon.BalloonTipClicked += (_, _) => _show();

        host.Runtime.StatusChanged += _ => UiState.Post(Update);
        host.AwayChanged += () => UiState.Post(Update);
        Update();
    }

    public void Update()
    {
        var status = _host.Runtime.Status;
        var paired = _host.Config.Paired;
        _statusItem.Text = paired ? $"{status} · {_host.Config.DeviceName}" : "Not paired";
        _awayItem.Checked = _host.Away;
        _awayItem.Text = _host.Away ? "Away mode (active)" : "Away mode";
        _disarmItem.Visible = _host.Away;
        _startupItem.Checked = StartupManager.IsEnabled();
        var text = paired ? $"Sentinel · {status}" : "Sentinel · not paired";
        _icon.Text = text.Length > 62 ? text[..62] : text;
    }

    public void ShowBalloon(string title, string body)
    {
        if (!_host.Config.BalloonNotifications) return;
        try
        {
            _icon.BalloonTipTitle = title.Length > 60 ? title[..60] : title;
            _icon.BalloonTipText = body.Length > 240 ? body[..240] : body;
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(4000);
        }
        catch
        {
            // ignored
        }
    }

    private async Task ToggleAwayAsync()
    {
        if (!_host.Away)
            await _host.EnableAwayAsync("tray");
        else
            _disarm();
    }

    private void CopyCode()
    {
        try
        {
            var code = Totp.Now(_host.Config.TotpSecret);
            if (string.IsNullOrEmpty(code)) return;
            Clipboard.SetText(code);
            ShowBalloon("Sentinel", $"2FA code {code} copied to the clipboard.");
        }
        catch
        {
            // ignored
        }
    }

    private void UpdateCodeItem()
    {
        var code = Totp.Now(_host.Config.TotpSecret);
        _codeItem.Text = string.IsNullOrEmpty(code)
            ? "Copy 2FA code"
            : $"Copy 2FA code  ·  {code}  ({Totp.SecondsRemaining()}s)";
    }

    private void ToggleStartup()
    {
        var enable = !StartupManager.IsEnabled();
        StartupManager.Set(enable);
        _host.Config.StartWithWindows = enable;
        _host.SaveConfig();
        Update();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _generatedIcon.Dispose();
    }

    private static Icon BuildIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted != null) return (Icon)extracted.Clone();
            }
        }
        catch
        {
            // fall back to the drawn icon
        }

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var accent = new SolidBrush(Color.FromArgb(108, 140, 255));
            g.FillEllipse(accent, 1, 1, 30, 30);
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, 10, 16, 12, 8);
            using var pen = new Pen(Color.White, 2.4f);
            g.DrawArc(pen, 11, 9, 10, 11, 180, 180);
        }
        var handle = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        NativeMethods.DestroyIcon(handle);
        return icon;
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private static Color Bg => Color.FromArgb(18, 22, 28);
        private static Color Line => Color.FromArgb(35, 42, 53);
        private static Color Hover => Color.FromArgb(27, 34, 51);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Color.FromArgb(42, 53, 96);
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
    }
}
