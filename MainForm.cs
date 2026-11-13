using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Media;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BetterScanner.Coordination;
using BetterScanner.Escl;
using BetterScanner.Scanning;

namespace BetterScanner;

public sealed class MainForm : Form
{
    private sealed record Choice(string Value, string Text)
    {
        public override string ToString() => Text;
    }

    private static readonly Color Accent = Color.FromArgb(0x0B, 0x5C, 0xD6);
    private static readonly Color Danger = Color.FromArgb(0xC6, 0x28, 0x28);
    private static readonly Color Armed = Color.FromArgb(0x1B, 0x8A, 0x3A);
    private static readonly Color Muted = Color.FromArgb(0x5F, 0x66, 0x6D);

    private readonly AppSettings settings = AppSettings.Load();
    private readonly HandsFreeEngine engine = new();
    private readonly PrinterLocks locks = new();
    private volatile ScanOptions options = new();
    private bool loading = true, exiting, trayHintShown, glassBusy, starting;
    private int sessionStacks, sessionPages;
    private PrinterClaim? shownClaim;      // colleague holding the selected printer (shown in the UI)
    private string? notifyFreeFor;         // printer id to announce when it becomes free
    private string? stopNote;              // reason to show when we stop the engine ourselves
    private static readonly Color Amber = Color.FromArgb(0x9A, 0x5B, 0x00);

    private readonly ComboBox cboScanner = Combo();
    private readonly Button btnRefresh = Btn("Find printers");
    private readonly Button btnAddIp = Btn("Add by IP…");
    private readonly Label lblPrinterStatus = new() { AutoSize = true, ForeColor = Muted, Margin = new Padding(3, 2, 3, 6) };
    private readonly TextBox txtName = new() { Width = 170, Anchor = AnchorStyles.Left };
    private readonly NumericUpDown numIdle = new() { Minimum = 1, Maximum = 240, Width = 60, Anchor = AnchorStyles.Left };
    private readonly FlowLayoutPanel pnlLinks = new() { Dock = DockStyle.Bottom, AutoSize = true, Visible = false, Margin = new Padding(0), Padding = new Padding(0, 2, 0, 2) };
    private readonly LinkLabel lnkOther = Link(), lnkNotify = Link(), lnkAsk = Link();

    private readonly TextBox txtFolder = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly Button btnBrowse = Btn("Change…");
    private readonly Button btnOpenFolder = Btn("Open");
    private readonly TextBox txtPrefix = new() { Width = 150, Anchor = AnchorStyles.Left };
    private readonly CheckBox chkDaily = Check("New folder for each day");

    private readonly ComboBox cboColor = Combo(), cboRes = Combo(), cboPage = Combo(), cboSplit = Combo();
    private readonly NumericUpDown numPages = new() { Minimum = 1, Maximum = 999, Width = 70, Anchor = AnchorStyles.Left };
    private readonly Label lblPagesPerFile = Lbl("Pages per file");
    private readonly CheckBox chkDuplex = Check("Both sides (duplex)");
    private readonly CheckBox chkSkipBlank = Check("Skip blank pages");

    private readonly Panel pnlStatus = new() { Dock = DockStyle.Fill, Height = 150, Padding = new Padding(16, 12, 16, 10), Margin = new Padding(0, 12, 0, 10) };
    private readonly Label lblTitle = new() { Dock = DockStyle.Top, Height = 36, Font = new Font("Segoe UI Semibold", 15f), AutoEllipsis = true };
    private readonly Label lblDetail = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label lblSession = new() { Dock = DockStyle.Bottom, Height = 22, AutoEllipsis = true };

    private readonly Button btnStart = new()
    {
        Dock = DockStyle.Fill, Height = 54, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Accent,
        Font = new Font("Segoe UI Semibold", 12f), Margin = new Padding(0, 0, 8, 0), Cursor = Cursors.Hand,
    };
    private readonly Button btnGlass = new()
    {
        Dock = DockStyle.Fill, Height = 54, Width = 150, Text = "Scan glass now", Margin = new Padding(0), Cursor = Cursors.Hand,
    };

    private readonly ListView lvRecent = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, LabelEdit = true, HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, Margin = new Padding(0, 4, 0, 6),
    };

    private readonly CheckBox chkAutoStart = Check("Start waiting for paper when the app opens");
    private readonly CheckBox chkTray = Check("Keep running in the tray when closed");

    private readonly NotifyIcon tray = new();
    private readonly ToolStripMenuItem trayToggle = new("Start hands-free scanning");
    private readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 4000 };
    private readonly Icon iconIdle = LoadIcon("app.ico"), iconArmed = LoadIcon("app-armed.ico");
    private readonly Icon trayIdle = LoadIcon("app.ico", small: true), trayArmed = LoadIcon("app-armed.ico", small: true);

    public MainForm()
    {
        BuildLayout();
        Native.SetPlaceholder(txtName, Environment.UserName);
        LoadSettingsIntoControls();
        WireEvents();
        loading = false;
        locks.UserName = MyName;
        locks.Start();
        RebuildOptions();
        SyncVisibility();
        SetRunningUi(false);
        ShowIdle();
    }

    // ── Layout ──────────────────────────────────────────────────────────────

    private static ComboBox Combo() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private static Button Btn(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(6, 1, 6, 1), Anchor = AnchorStyles.Left };
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 5, 12, 3) };
    private static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 8, 3) };
    private static LinkLabel Link() => new()
    {
        AutoSize = true, Margin = new Padding(0, 2, 18, 0), LinkColor = Accent, ActiveLinkColor = Accent,
        LinkBehavior = LinkBehavior.HoverUnderline, Font = new Font("Segoe UI Semibold", 9.75f),
    };

    private static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Margin = new Padding(0, 8, 0, 4),
    };

    private static TableLayoutPanel Row(Control fill, params Control[] rest)
    {
        var t = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1 + rest.Length, RowCount = 1, Margin = new Padding(0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.Controls.Add(fill, 0, 0);
        for (int i = 0; i < rest.Length; i++)
        {
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.Controls.Add(rest[i], i + 1, 0);
        }
        return t;
    }

    private void BuildLayout()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Better Scanner";
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(660, 820);
        MinimumSize = new Size(580, 700);
        Icon = iconIdle;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(18, 10, 18, 10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control c, SizeType size = SizeType.AutoSize, float height = 0)
        {
            root.RowStyles.Add(new RowStyle(size, height));
            root.Controls.Add(c, 0, root.RowCount++);
        }

        Add(Heading("Printer"));
        Add(Row(cboScanner, btnRefresh, btnAddIp));
        Add(lblPrinterStatus);
        var whoRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        var whoHint = Lbl("shown to coworkers while you're using a printer");
        whoHint.ForeColor = Muted;
        whoRow.Controls.AddRange([Lbl("Your name"), txtName, whoHint]);
        Add(whoRow);

        Add(Heading("Save scans to"));
        Add(Row(txtFolder, btnBrowse, btnOpenFolder));
        var nameRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
        nameRow.Controls.AddRange([Lbl("File names start with"), txtPrefix, chkDaily]);
        Add(nameRow);

        Add(Heading("Scan settings"));
        var grid = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 4, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.Controls.Add(Lbl("Color"), 0, 0);
        grid.Controls.Add(cboColor, 1, 0);
        grid.Controls.Add(Lbl("Resolution"), 2, 0);
        grid.Controls.Add(cboRes, 3, 0);
        grid.Controls.Add(Lbl("Page size"), 0, 1);
        grid.Controls.Add(cboPage, 1, 1);
        grid.Controls.Add(Lbl("Save as"), 2, 1);
        grid.Controls.Add(cboSplit, 3, 1);
        var idleRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), Anchor = AnchorStyles.Left, WrapContents = false };
        idleRow.Controls.AddRange([numIdle, Lbl("min with no paper")]);
        grid.Controls.Add(chkDuplex, 1, 2);
        grid.Controls.Add(Lbl("Auto-stop after"), 2, 2);
        grid.Controls.Add(idleRow, 3, 2);
        grid.Controls.Add(chkSkipBlank, 1, 3);
        grid.Controls.Add(lblPagesPerFile, 2, 3);
        grid.Controls.Add(numPages, 3, 3);
        Add(grid);

        pnlLinks.Controls.AddRange([lnkOther, lnkNotify, lnkAsk]);
        pnlStatus.Controls.Add(lblDetail);  // Fill is docked last, so add it first
        pnlStatus.Controls.Add(pnlLinks);
        pnlStatus.Controls.Add(lblSession);
        pnlStatus.Controls.Add(lblTitle);
        Add(pnlStatus);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 56, Margin = new Padding(0, 0, 0, 6) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        buttons.Controls.Add(btnStart, 0, 0);
        buttons.Controls.Add(btnGlass, 1, 0);
        btnStart.FlatAppearance.BorderSize = 0;
        Add(buttons, SizeType.Absolute, 62);

        Add(Heading("Recent scans  (double-click to open · F2 to rename)"));
        lvRecent.Columns.Add("File", 330);
        lvRecent.Columns.Add("Pages", 60, HorizontalAlignment.Right);
        lvRecent.Columns.Add("Saved", 150);
        Add(lvRecent, SizeType.Percent, 100);

        var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        bottom.Controls.AddRange([chkAutoStart, chkTray]);
        Add(bottom);

        // Footer credit: name → GitHub repo, email → new email.
        const string name = "Emil Paladin (Lime1-hue)", email = "paladinemil947@gmail.com";
        var prefix = $"Better Scanner v{Application.ProductVersion.Split('+')[0]}  ·  Made by ";
        var credit = new LinkLabel
        {
            AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 6, 0, 0),
            ForeColor = Muted, LinkColor = Accent, ActiveLinkColor = Accent, LinkBehavior = LinkBehavior.HoverUnderline,
            Font = new Font("Segoe UI", 8.75f), Text = $"{prefix}{name}  ·  {email}",
        };
        credit.Links.Clear();
        credit.Links.Add(prefix.Length, name.Length, "https://github.com/Lime1-hue/better-scanner-app");
        credit.Links.Add(prefix.Length + name.Length + 5, email.Length, "mailto:" + email + "?subject=Better%20Scanner");
        credit.LinkClicked += (_, e) => { if (e.Link?.LinkData is string url) OpenUrl(url); };
        Add(credit);

        Controls.Add(root);

        // Recent list context menu
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => OpenSelected());
        menu.Items.Add("Show in folder", null, (_, _) => ShowSelectedInFolder());
        menu.Items.Add("Rename", null, (_, _) => lvRecent.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.BeginEdit());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete (to Recycle Bin)", null, (_, _) => DeleteSelected());
        lvRecent.ContextMenuStrip = menu;

        // Tray
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open Better Scanner", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add(trayToggle);
        trayMenu.Items.Add("Open scans folder", null, (_, _) => OpenFolder());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => ExitApp());
        trayToggle.Click += (_, _) => ToggleHandsFree();
        tray.ContextMenuStrip = trayMenu;
        tray.Icon = trayIdle;
        tray.Text = "Better Scanner";
        tray.Visible = true;
        tray.DoubleClick += (_, _) => ShowFromTray();

        ResumeLayout(true);
    }

    /// <summary>Loads an embedded .ico; <paramref name="small"/> picks the tray-sized frame.</summary>
    private static Icon LoadIcon(string name, bool small = false)
    {
        using var s = typeof(MainForm).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing icon resource " + name);
        return small ? new Icon(s, SystemInformation.SmallIconSize) : new Icon(s);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ── Settings ↔ controls ─────────────────────────────────────────────────

    private static void Fill(ComboBox c, params Choice[] items) => c.Items.AddRange(items);

    private static void Select(ComboBox c, string value)
    {
        foreach (Choice ch in c.Items)
        {
            if (ch.Value == value) { c.SelectedItem = ch; return; }
        }
        if (c.Items.Count > 0) c.SelectedIndex = 0;
    }

    private static string Val(ComboBox c) => (c.SelectedItem as Choice)?.Value ?? "";

    private void LoadSettingsIntoControls()
    {
        Fill(cboColor, new("color", "Color"), new("gray", "Grayscale"), new("bw", "Black & white (smallest files)"));
        Fill(cboRes, new("100", "100 dpi (draft)"), new("200", "200 dpi"), new("300", "300 dpi (recommended)"), new("600", "600 dpi (fine detail)"));
        Fill(cboPage,
            new("auto", "Auto — crop to the paper"),
            new("a4", "A4"),
            new("letter", "Short (8.5 × 11 in)"),
            new("folio", "Long (8.5 × 13 in)"),
            new("legal", "Legal (8.5 × 14 in)"),
            new("full", "No cropping (whole scan area)"));
        Fill(cboSplit, new("stack", "One PDF per stack"), new("page", "One PDF per page"), new("fixed", "One PDF every N pages"));

        Select(cboColor, settings.Color);
        Select(cboRes, settings.Resolution.ToString());
        Select(cboPage, settings.PageSize);
        Select(cboSplit, settings.Split);
        numPages.Value = Num.Clamp(settings.PagesPerFile, 1, 999);
        chkDuplex.Checked = settings.Duplex;
        chkSkipBlank.Checked = settings.SkipBlank;
        txtFolder.Text = settings.SaveFolder;
        txtPrefix.Text = settings.FilePrefix;
        chkDaily.Checked = settings.DailySubfolder;
        chkAutoStart.Checked = settings.AutoStartHandsFree;
        chkTray.Checked = settings.CloseToTray;
        txtName.Text = settings.DisplayName;
        numIdle.Value = Num.Clamp(settings.IdleStopMinutes, 1, 240);
        cboScanner.FormattingEnabled = true;
        cboScanner.Format += (_, e) =>
        {
            if (e.ListItem is ScannerInfo s && locks.ClaimFor(s) is { } c) e.Value = $"{s}   — in use by {c.User}";
        };
        PopulateScanners();
    }

    private string MyName => string.IsNullOrWhiteSpace(settings.DisplayName) ? Environment.UserName : settings.DisplayName.Trim();

    private void OnOptionsChanged()
    {
        if (loading) return;
        settings.Color = Val(cboColor);
        settings.Resolution = int.TryParse(Val(cboRes), out var r) ? r : 300;
        settings.PageSize = Val(cboPage);
        settings.Split = Val(cboSplit);
        settings.PagesPerFile = (int)numPages.Value;
        settings.Duplex = chkDuplex.Checked;
        settings.SkipBlank = chkSkipBlank.Checked;
        settings.SaveFolder = txtFolder.Text.Trim();
        settings.FilePrefix = txtPrefix.Text;
        settings.DailySubfolder = chkDaily.Checked;
        settings.AutoStartHandsFree = chkAutoStart.Checked;
        settings.CloseToTray = chkTray.Checked;
        settings.DisplayName = txtName.Text.Trim();
        settings.IdleStopMinutes = (int)numIdle.Value;
        locks.UserName = MyName;
        settings.Save();
        RebuildOptions();
        SyncVisibility();
    }

    private void RebuildOptions() => options = new ScanOptions
    {
        Color = settings.Color,
        Resolution = settings.Resolution,
        Duplex = settings.Duplex,
        PageSize = settings.PageSize,
        SkipBlank = settings.SkipBlank,
        Split = settings.Split,
        PagesPerFile = settings.PagesPerFile,
        Folder = settings.SaveFolder,
        DailySubfolder = settings.DailySubfolder,
        Prefix = settings.FilePrefix,
        SettleSeconds = settings.SettleSeconds,
        IdleStopMinutes = settings.IdleStopMinutes,
    };

    private void SyncVisibility()
    {
        bool fixedSplit = Val(cboSplit) == "fixed";
        lblPagesPerFile.Visible = fixedSplit;
        numPages.Visible = fixedSplit;
    }

    private void WireEvents()
    {
        foreach (var c in new[] { cboColor, cboRes, cboPage, cboSplit }) c.SelectedIndexChanged += (_, _) => OnOptionsChanged();
        foreach (var c in new[] { chkDuplex, chkSkipBlank, chkDaily, chkAutoStart, chkTray }) c.CheckedChanged += (_, _) => OnOptionsChanged();
        numPages.ValueChanged += (_, _) => OnOptionsChanged();
        numIdle.ValueChanged += (_, _) => OnOptionsChanged();
        txtName.TextChanged += (_, _) => OnOptionsChanged();
        txtPrefix.TextChanged += (_, _) => OnOptionsChanged();
        txtFolder.TextChanged += (_, _) => OnOptionsChanged();
        txtFolder.Leave += (_, _) => LoadRecent();

        cboScanner.SelectedIndexChanged += (_, _) =>
        {
            if (loading) return;
            settings.ScannerId = SelectedScanner?.Id;
            settings.Save();
            UpdateLockUi();
            _ = RefreshPrinterStatusAsync();
        };
        lnkOther.LinkClicked += (_, _) =>
        {
            if (lnkOther.Tag is ScannerInfo free)
            {
                int i = cboScanner.Items.IndexOf(free);
                if (i >= 0) cboScanner.SelectedIndex = i;
            }
        };
        lnkNotify.LinkClicked += (_, _) =>
        {
            if (SelectedScanner is not { } s) return;
            notifyFreeFor = s.Id;
            lnkNotify.Text = "✓ You'll get a notification when it's free";
        };
        lnkAsk.LinkClicked += (_, _) =>
        {
            if (shownClaim is not { } c) return;
            locks.AskRelease(c);
            lnkAsk.Text = $"✓ Asked {c.User} — a message popped up on their PC";
        };
        locks.ClaimsChanged += () => Ui(UpdateLockUi);
        locks.ReleaseRequested += who => Ui(() =>
        {
            if (!engine.IsRunning) return;
            var printer = SelectedScanner?.Name ?? "the printer";
            lblSession.ForeColor = Amber;
            lblSession.Text = $"{who} is asking to use {printer}. Click Stop when you're done.";
            Notify($"{who} wants to use {printer}", "Click \"Stop hands-free scanning\" when you're done so they can use it.", ToolTipIcon.Info);
            Sound(SystemSounds.Exclamation);
        });
        btnRefresh.Click += async (_, _) => await DiscoverAsync(userInitiated: true);
        btnAddIp.Click += async (_, _) => await AddByIpAsync();
        btnBrowse.Click += (_, _) => BrowseFolder();
        btnOpenFolder.Click += (_, _) => OpenFolder();
        btnStart.Click += (_, _) => ToggleHandsFree();
        btnGlass.Click += async (_, _) => await ScanGlassAsync();

        lvRecent.DoubleClick += (_, _) => OpenSelected();
        lvRecent.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F2) lvRecent.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.BeginEdit();
            else if (e.KeyCode == Keys.Enter) OpenSelected();
            else if (e.KeyCode == Keys.Delete) DeleteSelected();
        };
        lvRecent.AfterLabelEdit += OnRenamed;

        statusTimer.Tick += (_, _) =>
        {
            if (!engine.IsRunning && !glassBusy && Visible) _ = RefreshPrinterStatusAsync();
        };

        engine.StatusChanged += (p, t, d) => Ui(() => ShowPhase(p, t, d));
        engine.ScanSaved += o => Ui(() => OnScanSaved(o));
        engine.ScanFailed += m => Ui(() => OnScanFailed(m));
        engine.ScannerMoved += s => Ui(() => { locks.UpdatePrinter(s); MergeScanners([s]); });
        engine.Stopped += (reason, isError) => Ui(() => OnEngineStopped(reason, isError));
    }

    private void Ui(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LoadRecent();
        statusTimer.Start();
        if (!locks.Available)
        {
            lblSession.ForeColor = Amber;
            lblSession.Text = $"Can't check whether coworkers are using a printer (network port {PrinterLocks.Port} is blocked).";
        }
        await RefreshPrinterStatusAsync();
        await DiscoverAsync(userInitiated: false);
        if (settings.AutoStartHandsFree && !engine.IsRunning && SelectedScanner is not null) StartHandsFree();
    }

    // ── Printers ────────────────────────────────────────────────────────────

    private ScannerInfo? SelectedScanner => cboScanner.SelectedItem as ScannerInfo;

    private void PopulateScanners()
    {
        if (cboScanner.DroppedDown) return; // don't close the list under the user's mouse
        bool was = loading;
        loading = true;
        cboScanner.Items.Clear();
        foreach (var s in settings.KnownScanners) cboScanner.Items.Add(s);
        var pick = settings.KnownScanners.FindIndex(s => s.Id == settings.ScannerId);
        if (pick < 0 && cboScanner.Items.Count > 0) pick = 0;
        if (pick >= 0) cboScanner.SelectedIndex = pick;
        settings.ScannerId = SelectedScanner?.Id;
        loading = was;
    }

    private void MergeScanners(IEnumerable<ScannerInfo> found)
    {
        var list = settings.KnownScanners;
        foreach (var f in found)
        {
            int i = list.FindIndex(k => k.Id == f.Id);
            if (i < 0) i = list.FindIndex(k => k.Ip == f.Ip);
            if (i >= 0)
            {
                if (list[i].Id == settings.ScannerId) settings.ScannerId = f.Id;
                list[i] = f; // keeps the fresh IP
            }
            else
            {
                list.Add(f);
            }
        }
        settings.Save();
        PopulateScanners();
        _ = RefreshPrinterStatusAsync();
    }

    private async Task DiscoverAsync(bool userInitiated)
    {
        btnRefresh.Enabled = false;
        btnRefresh.Text = "Searching…";
        try
        {
            var found = await Discovery.DiscoverAsync(TimeSpan.FromSeconds(1.5));
            if (found.Count > 0) MergeScanners(found);
            if (userInitiated)
            {
                MessageBox.Show(this,
                    found.Count == 0
                        ? "No printers answered on the network.\n\nMake sure the printer is on and connected to the same network, or use \"Add by IP…\"."
                        : $"Found {found.Count} printer{(found.Count == 1 ? "" : "s")}:\n\n" + string.Join("\n", found.Select(f => $"• {f.Name}  ({f.Ip})")),
                    "Find printers", MessageBoxButtons.OK, found.Count == 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            if (userInitiated) MessageBox.Show(this, "Searching the network failed: " + ex.Message, "Find printers", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            btnRefresh.Text = "Find printers";
            btnRefresh.Enabled = !engine.IsRunning;
        }
    }

    private async Task AddByIpAsync()
    {
        var input = Prompt("Add printer by IP", "Printer IP address (shown on the printer's network settings):");
        if (string.IsNullOrWhiteSpace(input)) return;
        if (!IPAddress.TryParse(input!.Trim(), out var ip))
        {
            MessageBox.Show(this, $"\"{input}\" is not a valid IP address.", "Add printer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        UseWaitCursor = true;
        try
        {
            var addr = ip.ToString();
            var status = await EsclClient.GetStatusAsync(addr, TimeSpan.FromSeconds(6));
            if (status is null)
            {
                MessageBox.Show(this, $"No scanner answered at {addr}.\n\nCheck the address, and that the printer supports AirScan / eSCL scanning.",
                    "Add printer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var model = await EsclClient.GetModelAsync(addr) ?? "Printer";
            model = Regex.Replace(Regex.Replace(model, @"^EPSON\s+", "", RegexOptions.IgnoreCase), @"\s+Series$", "", RegexOptions.IgnoreCase);
            var info = new ScannerInfo("ip-" + addr.Replace('.', '-'), model, addr);
            MergeScanners([info]);
            settings.ScannerId = settings.KnownScanners.First(s => s.Ip == addr).Id;
            PopulateScanners();
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async Task RefreshPrinterStatusAsync()
    {
        var s = SelectedScanner;
        if (s is null)
        {
            lblPrinterStatus.Text = "No printer yet — click \"Find printers\".";
            lblPrinterStatus.ForeColor = Muted;
            return;
        }
        if (engine.IsRunning)
        {
            lblPrinterStatus.Text = $"{s.Ip} · in use by you (hands-free scanning)";
            lblPrinterStatus.ForeColor = Muted;
            return;
        }
        if (ShowClaimStatus(s)) return;
        var st = await EsclClient.GetStatusAsync(s.Ip, TimeSpan.FromSeconds(4));
        if (SelectedScanner?.Id != s.Id || engine.IsRunning || ShowClaimStatus(s)) return;
        lblPrinterStatus.Text = st is null ? $"{s.Ip} · not reachable — is it switched on?" : $"{s.Ip} · {st.Describe()}";
        lblPrinterStatus.ForeColor = st is null ? Danger : Muted;
    }

    // ── Hands-free ──────────────────────────────────────────────────────────

    private void ToggleHandsFree()
    {
        if (engine.IsRunning) engine.Stop();
        else StartHandsFree();
    }

    private bool EnsureFolder()
    {
        try
        {
            Directory.CreateDirectory(ScanPipeline.ResolveFolder(options));
            return true;
        }
        catch (Exception ex)
        {
            ShowFromTray();
            MessageBox.Show(this, "Can't use the save folder:\n" + ex.Message, "Save folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private async void StartHandsFree()
    {
        if (starting || engine.IsRunning) return;
        var s = SelectedScanner;
        if (s is null)
        {
            MessageBox.Show(this, "Pick a printer first (click \"Find printers\").", "Better Scanner", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!EnsureFolder()) return;

        // Coworkers see this name, and office PCs often share one Windows login.
        if (string.IsNullOrWhiteSpace(settings.DisplayName) && Visible)
        {
            var name = Prompt("Your name", "Your name (coworkers see it while you're using a printer):", "OK", "e.g. Maria");
            if (!string.IsNullOrWhiteSpace(name)) txtName.Text = name!.Trim();
        }

        starting = true;
        btnStart.Enabled = cboScanner.Enabled = btnRefresh.Enabled = btnAddIp.Enabled = false;
        btnStart.Text = "Checking that nobody else is using it…";
        PrinterClaim? rival;
        try
        {
            rival = await locks.TryClaimAsync(s);
        }
        finally
        {
            starting = false;
        }
        if (rival is not null)
        {
            SetRunningUi(false);
            UpdateLockUi();
            ShowFromTray();
            Sound(SystemSounds.Exclamation);
            return;
        }

        sessionStacks = sessionPages = 0;
        lblSession.Text = "Nothing scanned yet this session";
        engine.Start(s, () => options);
        Native.KeepAwake(true);
        SetRunningUi(true);
    }

    private void SetRunningUi(bool running)
    {
        btnStart.Text = running ? "Stop hands-free scanning" : "Start hands-free scanning";
        btnStart.BackColor = running ? Danger : Accent;
        btnStart.Enabled = true;
        if (running)
        {
            shownClaim = null;
            pnlLinks.Visible = false;
        }
        trayToggle.Text = running ? "Stop hands-free scanning" : "Start hands-free scanning";
        cboScanner.Enabled = btnRefresh.Enabled = btnAddIp.Enabled = !running;
        tray.Icon = running ? trayArmed : trayIdle;
        Icon = running ? iconArmed : iconIdle;
        if (running)
        {
            lblPrinterStatus.Text = $"{SelectedScanner?.Ip} · in use by hands-free scanning";
            lblPrinterStatus.ForeColor = Muted;
        }
    }

    private void OnEngineStopped(string? reason, bool isError)
    {
        locks.Release();
        Native.KeepAwake(false);
        SetRunningUi(false);
        if (stopNote is not null)
        {
            reason = stopNote;
            isError = true;
            stopNote = null;
        }
        if (reason is not null)
        {
            ShowPhase(isError ? Phase.Offline : Phase.Stopped, isError ? "Hands-free scanning stopped" : "Stopped", reason);
            Notify("Hands-free scanning stopped", reason, isError ? ToolTipIcon.Error : ToolTipIcon.Info);
            if (isError) Sound(SystemSounds.Hand);
        }
        else
        {
            ShowIdle();
        }
        lblSession.ForeColor = Muted;
        lblSession.Text = SessionText();
        UpdateLockUi();
        _ = RefreshPrinterStatusAsync();
    }

    // ── Printer lock (who is using which printer) ───────────────────────────

    /// <summary>Re-evaluates the lock state after anyone takes or releases a printer.</summary>
    private void UpdateLockUi()
    {
        PopulateScanners(); // refresh "(in use by …)" in the printer list

        if (notifyFreeFor is { } wantedId
            && settings.KnownScanners.FirstOrDefault(k => k.Id == wantedId) is { } wanted
            && locks.ClaimFor(wanted) is null)
        {
            notifyFreeFor = null;
            Notify($"{wanted.Name} is free now", "You can start hands-free scanning.", ToolTipIcon.Info);
            Sound(SystemSounds.Asterisk);
        }

        if (engine.IsRunning)
        {
            // Two people pressed Start at the same moment: the earlier one keeps it.
            if (locks.LostTo() is { } rival)
            {
                stopNote = $"{rival.Who} started hands-free on {rival.PrinterName} at the same moment and got it first. Pick another printer, or wait until they're done.";
                engine.Stop();
            }
            return;
        }
        if (starting) return;

        var s = SelectedScanner;
        var claim = s is null ? null : locks.ClaimFor(s);
        if (s is not null && claim is not null)
        {
            ShowBlocked(s, claim);
        }
        else if (shownClaim is not null)
        {
            shownClaim = null;
            pnlLinks.Visible = false;
            SetRunningUi(false);
            ShowIdle();
            _ = RefreshPrinterStatusAsync();
        }
    }

    private void ShowBlocked(ScannerInfo s, PrinterClaim c)
    {
        bool same = shownClaim?.InstanceId == c.InstanceId;
        shownClaim = c;
        var since = c.SinceUtc.ToLocalTime().ToString("h:mm tt");

        ShowPhase(Phase.Busy, $"In use by {c.User}",
            $"{c.Who} is scanning hands-free on {s.Name} (since {since}). Paper put in its feeder goes to their PC, so hands-free is off for you until they stop. \"Scan glass now\" still works.");
        btnStart.Enabled = false;
        btnStart.Text = $"{s.Name} is in use by {c.User}";
        btnStart.BackColor = Color.FromArgb(0x9A, 0xA0, 0xA6);

        var free = settings.KnownScanners.FirstOrDefault(k => k.Id != s.Id && k.Ip != s.Ip && locks.ClaimFor(k) is null);
        lnkOther.Tag = free;
        lnkOther.Visible = free is not null;
        if (free is not null) lnkOther.Text = $"Use {free.Name} instead (free)";
        if (!same || notifyFreeFor != s.Id) lnkNotify.Text = notifyFreeFor == s.Id ? "✓ You'll get a notification when it's free" : "Tell me when it's free";
        if (!same) lnkAsk.Text = $"Ask {c.User} to release it";
        pnlLinks.Visible = true;
        ShowClaimStatus(s);
    }

    /// <summary>Shows "in use by …" under the printer list. Returns true when the printer is taken.</summary>
    private bool ShowClaimStatus(ScannerInfo s)
    {
        if (locks.ClaimFor(s) is not { } c) return false;
        lblPrinterStatus.Text = $"⚠ In use by {c.Who} — hands-free since {c.SinceUtc.ToLocalTime():h:mm tt}";
        lblPrinterStatus.ForeColor = Amber;
        return true;
    }

    private string SessionText() => sessionStacks == 0
        ? ""
        : $"Last session: {sessionStacks} stack{(sessionStacks == 1 ? "" : "s")}, {sessionPages} page{(sessionPages == 1 ? "" : "s")}";

    private void OnScanSaved(ScanOutcome o)
    {
        sessionStacks++;
        sessionPages += o.Pages;
        foreach (var f in o.Files.Reverse()) AddRecent(f, o.Files.Count == 1 ? o.Pages : null, DateTime.Now);

        var first = Path.GetFileName(o.Files[0]);
        var what = o.Files.Count == 1 ? first : $"{o.Files.Count} files ({first} …)";
        var skipped = o.SkippedBlank > 0 ? $", {o.SkippedBlank} blank skipped" : "";
        lblSession.ForeColor = Muted;
        lblSession.Text = $"This session: {sessionStacks} stack{(sessionStacks == 1 ? "" : "s")}, {sessionPages} page{(sessionPages == 1 ? "" : "s")} · last: {what}";

        if (o.Warning is not null)
        {
            Notify("Scan partly saved", o.Warning, ToolTipIcon.Warning);
            Sound(SystemSounds.Exclamation);
        }
        else
        {
            Notify($"Saved {o.Pages} page{(o.Pages == 1 ? "" : "s")}{skipped}", what, ToolTipIcon.Info);
            Sound(SystemSounds.Asterisk);
        }
    }

    private void OnScanFailed(string message)
    {
        lblSession.ForeColor = Danger;
        lblSession.Text = "Last scan failed: " + message;
        Notify("Scan failed", message, ToolTipIcon.Error);
        Sound(SystemSounds.Hand);
    }

    private void ShowIdle() => ShowPhase(Phase.Stopped, "Ready",
        "Click Start, walk to the printer, and put your pages face-up in the feeder. Every stack is scanned and saved as a PDF by itself — no need to come back to the PC.");

    private void ShowPhase(Phase p, string title, string detail)
    {
        (Color bg, Color fg) = p switch
        {
            Phase.Waiting => (Color.FromArgb(0xE8, 0xF1, 0xFD), Color.FromArgb(0x0B, 0x4A, 0x9B)),
            Phase.Settling => (Color.FromArgb(0xFF, 0xF4, 0xD6), Color.FromArgb(0x7A, 0x52, 0x00)),
            Phase.Scanning => (Color.FromArgb(0xE3, 0xF5, 0xE7), Color.FromArgb(0x1B, 0x6B, 0x2D)),
            Phase.Busy => (Color.FromArgb(0xFF, 0xF4, 0xD6), Color.FromArgb(0x7A, 0x52, 0x00)),
            Phase.ClearFeeder or Phase.Offline => (Color.FromArgb(0xFD, 0xEA, 0xEA), Color.FromArgb(0x9B, 0x1C, 0x1C)),
            _ => (Color.FromArgb(0xF2, 0xF4, 0xF6), Color.FromArgb(0x26, 0x2B, 0x31)),
        };
        pnlStatus.BackColor = bg;
        lblTitle.ForeColor = fg;
        lblTitle.Text = title;
        lblDetail.Text = detail;
        lblDetail.ForeColor = Color.FromArgb(0x34, 0x3A, 0x40);
        var tip = "Better Scanner — " + title;
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    // ── Glass scan ──────────────────────────────────────────────────────────

    private async Task ScanGlassAsync()
    {
        var s = SelectedScanner;
        if (s is null)
        {
            MessageBox.Show(this, "Pick a printer first (click \"Find printers\").", "Better Scanner", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!EnsureFolder()) return;

        glassBusy = true;
        btnGlass.Enabled = false;
        btnGlass.Text = "Scanning…";
        bool ownStatus = !engine.IsRunning;
        if (ownStatus) ShowPhase(Phase.Scanning, "Scanning the glass…", s.Name);
        try
        {
            var progress = new Progress<string>(m => { if (ownStatus) ShowPhase(Phase.Scanning, "Scanning the glass…", m); });
            var outcome = await ScanPipeline.RunAsync(s, feeder: false, options, progress, CancellationToken.None);
            OnScanSaved(outcome);
            if (ownStatus) ShowPhase(Phase.Stopped, "Saved", string.Join(", ", outcome.Files.Select(Path.GetFileName)));
        }
        catch (Exception ex)
        {
            OnScanFailed(ex.Message);
            if (ownStatus) ShowPhase(Phase.Offline, "Scan failed", ex.Message);
        }
        finally
        {
            glassBusy = false;
            btnGlass.Enabled = true;
            btnGlass.Text = "Scan glass now";
        }
    }

    // ── Folder + recent files ───────────────────────────────────────────────

    private string RootFolder => string.IsNullOrWhiteSpace(settings.SaveFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Scans")
        : settings.SaveFolder;

    private void BrowseFolder()
    {
        var start = Directory.Exists(RootFolder) ? RootFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        using var dlg = new FolderBrowserDialog
        {
            Description = "Where should scans be saved?",
            ShowNewFolderButton = true,
#if NETFRAMEWORK
            SelectedPath = start,
#else
            UseDescriptionForTitle = true,
            InitialDirectory = start,
#endif
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            txtFolder.Text = dlg.SelectedPath;
            LoadRecent();
        }
    }

    private void OpenFolder()
    {
        try
        {
            var folder = ScanPipeline.ResolveFolder(options);
            if (!Directory.Exists(folder)) folder = RootFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void AddRecent(string path, int? pages, DateTime when)
    {
        var item = new ListViewItem(Path.GetFileName(path)) { Tag = path, ToolTipText = path };
        item.SubItems.Add(pages?.ToString() ?? "");
        item.SubItems.Add(when.ToString("MMM d, h:mm:ss tt"));
        lvRecent.Items.Insert(0, item);
        while (lvRecent.Items.Count > 60) lvRecent.Items.RemoveAt(lvRecent.Items.Count - 1);
    }

    /// <summary>PDFs in the folder and one level of subfolders (the per-day folders), skipping ones we can't read.</summary>
    private static IEnumerable<FileInfo> PdfsIn(DirectoryInfo root)
    {
        var dirs = new List<DirectoryInfo> { root };
        try { dirs.AddRange(root.GetDirectories()); } catch { }
        foreach (var d in dirs)
        {
            FileInfo[] files;
            try { files = d.GetFiles("*.pdf"); } catch { continue; }
            foreach (var f in files) yield return f;
        }
    }

    private async void LoadRecent()
    {
        lvRecent.Items.Clear();
        var root = RootFolder;
        List<FileInfo> files;
        try
        {
            if (!Directory.Exists(root)) return;
            files = await Task.Run(() => PdfsIn(new DirectoryInfo(root))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(40)
                .ToList());
        }
        catch
        {
            return;
        }

        foreach (var f in files.AsEnumerable().Reverse()) AddRecent(f.FullName, null, f.LastWriteTime);

        // Fill in page counts in the background.
        var counts = await Task.Run(() => files.ToDictionary(f => f.FullName, f => CountPdfPages(f.FullName)));
        foreach (ListViewItem item in lvRecent.Items)
        {
            if (item.Tag is string p && counts.TryGetValue(p, out var n) && n > 0 && item.SubItems[1].Text.Length == 0)
                item.SubItems[1].Text = n.ToString();
        }
    }

    private static int CountPdfPages(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length > 80_000_000) return 0;
            var text = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(path));
            return Regex.Matches(text, @"/Type\s*/Page(?![a-zA-Z])").Count;
        }
        catch
        {
            return 0;
        }
    }

    private string? SelectedPath => lvRecent.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.Tag as string;

    private void OpenSelected()
    {
        if (SelectedPath is not { } p) return;
        try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void ShowSelectedInFolder()
    {
        if (SelectedPath is not { } p) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
    }

    private void DeleteSelected()
    {
        var item = lvRecent.SelectedItems.Cast<ListViewItem>().FirstOrDefault();
        if (item?.Tag is not string p) return;
        if (MessageBox.Show(this, $"Move \"{Path.GetFileName(p)}\" to the Recycle Bin?", "Delete scan",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            lvRecent.Items.Remove(item);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete scan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnRenamed(object? sender, LabelEditEventArgs e)
    {
        var item = lvRecent.Items[e.Item];
        if (e.Label is null || item.Tag is not string oldPath) return; // cancelled
        var name = e.Label.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        if (name.Length == 0) { e.CancelEdit = true; return; }
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";

        var newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, name);
        if (string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase) && name == Path.GetFileName(oldPath)) { e.CancelEdit = true; return; }
        try
        {
            if (File.Exists(newPath) && !string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"\"{name}\" already exists in that folder.");
            File.Move(oldPath, newPath);
            e.CancelEdit = true;   // we set the text ourselves (adds .pdf)
            item.Text = name;
            item.Tag = newPath;
            item.ToolTipText = newPath;
        }
        catch (Exception ex)
        {
            e.CancelEdit = true;
            MessageBox.Show(this, "Couldn't rename: " + ex.Message, "Rename", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── Tray / lifetime ─────────────────────────────────────────────────────

    private void Notify(string title, string text, ToolTipIcon icon)
    {
        if (string.IsNullOrWhiteSpace(text)) text = " ";
        tray.ShowBalloonTip(4000, title, text, icon);
    }

    private void Sound(SystemSound s)
    {
        if (settings.PlaySounds) s.Play();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ExitApp()
    {
        exiting = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && settings.CloseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!trayHintShown)
            {
                trayHintShown = true;
                Notify("Better Scanner is still running",
                    engine.IsRunning ? "Hands-free scanning keeps going. Right-click the tray icon to stop or exit." : "Right-click the tray icon to open it again or exit.",
                    ToolTipIcon.Info);
            }
            return;
        }

        engine.Stop();
        locks.Dispose(); // tells coworkers the printer is free right away
        Native.KeepAwake(false);
        statusTimer.Stop();
        tray.Visible = false;
        settings.Save();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Dispose();
            statusTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private string? Prompt(string title, string text, string okText = "Add", string placeholder = "e.g. 192.168.1.50")
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font,
            AutoScaleDimensions = new SizeF(96f, 96f), AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(380, 124),
        };
        var lbl = new Label { Text = text, Left = 12, Top = 12, Width = 356, Height = 22 };
        var tb = new TextBox { Left = 12, Top = 38, Width = 356 };
        Native.SetPlaceholder(tb, placeholder);
        var ok = new Button { Text = okText, DialogResult = DialogResult.OK, Left = 200, Top = 82, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 288, Top = 82, Width = 80 };
        f.Controls.AddRange([lbl, tb, ok, cancel]);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? tb.Text.Trim() : null;
    }
}
