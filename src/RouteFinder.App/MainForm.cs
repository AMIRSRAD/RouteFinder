using static RouteFinder.App.WindowsNetworkTools;
using System.Diagnostics;
using System.Security.Principal;
using RouteFinder.Core;
using RouteFinder.Service;
using static RouteFinder.App.StudioTheme;

namespace RouteFinder.App;

internal sealed partial class MainForm : Form
{
    readonly ConnectionSession connection = new(new EzConnectClient());
    readonly Panel workspace = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    readonly Panel routePage = new() { Dock = DockStyle.Fill };
    readonly Panel appsPage = new() { Dock = DockStyle.Fill };
    readonly Panel activityPage = new() { Dock = DockStyle.Fill };
    readonly Panel settingsPage = new() { Dock = DockStyle.Fill };
    readonly Button routesNav = Button("Routes");
    readonly Button appsNav = Button("Applications");
    readonly Button activityNav = Button("Activity");
    readonly Button settingsNav = Button("Settings");
    readonly Button signIn = Button("Sign in");
    readonly Button refresh = Button("Refresh");
    readonly Button connect = Button("Connect", true);
    readonly Button test = Button("Test route");
    readonly Button compare = Button("Test routes");
    readonly Button import = Button("Import config");
    readonly Button favorite = Button("☆ Favorite");
    readonly CheckBox favoritesOnly = new() { Text = "Favorites", AutoSize = true, ForeColor = Ink, BackColor = Base, Margin = new Padding(8, 6, 0, 0) };
    readonly CheckBox minimizeToTray = new() { Text = "Minimize to system tray while connected", AutoSize = true, ForeColor = Ink, BackColor = Card, Margin = Padding.Empty };
    readonly CheckBox killSwitch = new() { Text = "Kill switch", AutoSize = true, ForeColor = Ink, BackColor = Card, Margin = Padding.Empty };
    readonly CheckBox portableEngine = new() { Text = "Use WireSock virtual adapter (off: transparent mode)", AutoSize = true, ForeColor = Ink, BackColor = Card, Margin = Padding.Empty };
    readonly CheckBox ipv6Bindings = new() { Text = "Enable IPv6 on active network adapters", AutoSize = true, ForeColor = Ink, BackColor = Card, Margin = Padding.Empty };
    readonly CheckBox autoMtu = new() { Text = "Auto MTU optimization", AutoSize = true, ForeColor = Ink, BackColor = Card, Margin = Padding.Empty };
    readonly CheckBox autoAddLinkedApps = new() { Text = "Include linked apps when finding catalog executables", Checked = true, AutoSize = true, ForeColor = Ink, BackColor = Base };
    static readonly Size[] WindowSizes = [new(1200, 745), new(1050, 690), new(1440, 900), new(1600, 1000)];
    readonly ComboBox windowPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145, AccessibleName = "Window resolution" };
    readonly CheckBox showTutorials = new() { Text = "Show tutorials", AutoSize = true, ForeColor = Ink, BackColor = Base };
    readonly CheckBox bypassLan = new() { Text = "Bypass local IPv4 networks", AutoSize = true, ForeColor = Ink, BackColor = Base };
    readonly TextBox dnsOverride = Input("Optional, e.g. 1.1.1.1, 1.0.0.1");
    readonly Label account = Label("", 9, true, Muted);
    readonly Label banner = Label("Disconnected", 12, true);
    readonly Label traffic = Label("Choose a route and select your apps.", 9, false, Muted);
    readonly Label routeCount = Label("", 10, false, Muted);
    readonly Label routeTitle = Label("Select a route", 16, true);
    readonly Label routeSubtitle = Label("", 10, false, Muted);
    readonly Label routeEndpoint = Label("", 10, false, Muted);
    readonly Label routeHealth = Label("", 13, true);
    readonly Label routeHealthDetail = Label("", 10, false, Muted);
    readonly Label appsCount = Label("", 10, false, Muted);
    readonly Label appHint = Label("Add an executable or pick a running application.", 10, false, Muted);
    readonly TextBox search = Input("Search city, region, or route");
    readonly TextBox journal = Input();
    readonly DataGridView grid = new();
    readonly Label emptyRoutes = Label("", 12, true, Muted);
    readonly CheckedListBox apps = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true };
    readonly ComboBox games = new() { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox routingMode = new() { Width = 245, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "RouteFinder", Visible = false };
    readonly Dictionary<string, RouteTestResult> measurements = new(StringComparer.Ordinal);
    readonly bool preview;
    List<SavedProfile> profiles = [];
    IReadOnlyList<ServiceRoute> serviceRoutes = [];
    Choice? selected;
    Choice? connected;
    string? connectedEndpoint;
    DateTimeOffset? connectionStartedAt;
    bool connectedEntirePc;
    bool connectedConsole;
    Func<Task>? restartConnection;
    bool recoveryAttempted;
    string? armedRoute;
    string[] armedApps = [];
    long? lastAppCheck;
    bool onDemandOwnsTunnel;
    bool holdOnDemandTunnel;
    long? appsIdleSince;
    bool peerTimeoutLogged;
    DateTimeOffset? accountExpiry;
    string? signedInEmail;
    string? accountUsage;
    string? accountServiceStatus;
    string? accountReferralCode;
    string serviceOverview = "";
    bool rememberCurrentSignIn;
    CancellationTokenSource? testCancellation;
    bool busy, closing, populating, loadingPreferences;
    ProfileStore.Preferences preferences = new(false, []);

    sealed record Choice(string Key, string Title, string Region, string Source, ServiceRoute? Route, SavedProfile? Profile)
    {
        public TunnelProfile? Local() => Profile == null ? null : TunnelProfile.Parse(Profile.Configuration, Profile.Name);
    }

    public MainForm(bool preview = false)
    {
        this.preview = preview;
        Text = "RouteFinder";
        RightToLeft = UiLanguage.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = UiLanguage.IsRightToLeft;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1200, 745);
        MinimumSize = new Size(1050, 690);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        BackColor = Base;
        ForeColor = Ink;
        Icon = SystemIcons.Application;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.Controls.Add(BuildSidebar(), 0, 0);
        var main = Stack(0, 86);
        main.Padding = new Padding(28, 20, 28, 20);
        main.Controls.Add(workspace, 0, 0);
        main.Controls.Add(BuildFooter(), 0, 1);
        shell.Controls.Add(main, 1, 0);
        Controls.Add(shell);

        BuildOverview();
        BuildRoutes();
        BuildApps();
        BuildActivity();
        BuildSettings();
        connection.Diagnostic += WriteActivity;
        connection.RefreshTokenChanged += token =>
        {
            if (!rememberCurrentSignIn || signedInEmail is not { } email) return;
            try { ProfileStore.SaveLogin(email, token); }
            catch (Exception error) { WriteActivity("Could not save the refreshed sign-in. " + error.Message); }
        };
        workspace.Controls.AddRange([settingsPage, activityPage, routePage, appsPage, overviewPage]);
        overviewNav.Click += (_, _) => Navigate(overviewPage);
        routesNav.Click += (_, _) => Navigate(routePage);
        appsNav.Click += (_, _) => Navigate(appsPage);
        activityNav.Click += (_, _) => Navigate(activityPage);
        settingsNav.Click += (_, _) => Navigate(settingsPage);
        signIn.Click += async (_, _) => await ToggleSignIn();
        refresh.Click += async (_, _) => await Run("Refreshing routes…", async () => SetCatalog(await connection.ReloadAsync()));
        connect.Click += async (_, _) => await ToggleConnection();
        test.Click += async (_, _) =>
        {
            if (testCancellation is { } active) { active.Cancel(); test.Enabled = false; return; }
            await TestRoute();
        };
        compare.Click += async (_, _) => await CompareRoutes(visibleOnly: true);
        import.Click += (_, _) => ImportProfile();
        favorite.Click += (_, _) => ToggleFavorite();
        favoritesOnly.CheckedChanged += (_, _) => PopulateRoutes();
        minimizeToTray.CheckedChanged += (_, _) => SavePreferences();
        killSwitch.CheckedChanged += (_, _) => SavePreferences();
        portableEngine.CheckedChanged += (_, _) => SavePreferences();
        ipv6Bindings.CheckedChanged += async (_, _) => await ApplyIpv6SettingFromUi();
        autoMtu.CheckedChanged += (_, _) => SavePreferences();
        autoAddLinkedApps.CheckedChanged += (_, _) => SavePreferences();
        dnsOverride.TextChanged += (_, _) => SavePreferences();
        routingMode.SelectedIndexChanged += (_, _) =>
        {
            SavePreferences();
            appHint.Text = routingMode.SelectedIndex == 1
                ? "Entire PC mode uses the profile's routes and bypass rules, including any app exclusions. Local profiles need 0.0.0.0/0 and ::/0 to carry both IP families."
                : "Only checked executables use the connected route.";
            UpdateControls();
        };
        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns.Count - 1 && grid.Rows[e.RowIndex].Tag is Choice choice)
            { selected = choice; ToggleFavorite(); }
        };
        search.TextChanged += (_, _) => PopulateRoutes();
        grid.SelectionChanged += (_, _) => { if (!populating) SelectRow(); };
        apps.ItemCheck += (_, _) =>
        {
            if (!populating && IsHandleCreated) BeginInvoke((Action)(() => { UpdateApps(); SaveApps(); }));
        };
        timer.Tick += async (_, _) =>
        {
            UpdateConnection();
            await CheckOnDemandConnection();
            if (!IsDisposed && !busy && preferences.RecoverStoppedTunnel && !recoveryAttempted &&
                restartConnection is not null && connection.HasTunnel && nativeTunnelStopped)
            {
                recoveryAttempted = true;
                WriteActivity("WireSock stopped the tunnel. Attempting one reconnect.");
                await Reconnect(automatic: true);
            }
            if (!IsDisposed) UpdateTrayStatus();
        };
        tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open RouteFinder", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        var disconnectItem = menu.Items.Add("Disconnect", null, async (_, _) =>
        {
            if (!busy && (connection.HasTunnel || connection.NeedsCleanup)) await ToggleConnection();
        });
        var reconnectItem = menu.Items.Add("Reconnect", null, async (_, _) => await Reconnect());
        var recoverItem = new ToolStripMenuItem("Reconnect once if tunnel stops");
        recoverItem.Click += (_, _) =>
        {
            preferences = preferences with { RecoverStoppedTunnel = !preferences.RecoverStoppedTunnel };
            SavePreferences();
        };
        menu.Items.Add(recoverItem);
        var detailsItem = menu.Items.Add("Connection details", null, (_, _) => ShowConnectionDetails());
        var cancelTestItem = menu.Items.Add("Cancel route test", null, (_, _) => testCancellation?.Cancel());
        var onDemandItem = menu.Items.Add("Connect when a selected app starts", null, (_, _) => ToggleOnDemandConnection());
        var holdItem = new ToolStripMenuItem("Keep on-demand tunnel connected");
        var idleItem = menu.Items.Add("Set on-demand idle timeout", null, (_, _) => EditOnDemandTimeout());
        holdItem.Click += (_, _) =>
        {
            holdOnDemandTunnel = !holdOnDemandTunnel;
            appsIdleSince = null;
            WriteActivity(holdOnDemandTunnel ? "On-demand tunnel held open." : "On-demand idle disconnect resumed.");
        };
        menu.Items.Add(holdItem);
        menu.Opening += (_, _) =>
        {
            recoverItem.Checked = preferences.RecoverStoppedTunnel;
            disconnectItem.Enabled = !busy && (connection.HasTunnel || connection.NeedsCleanup);
            disconnectItem.Text = !connection.HasTunnel && connection.NeedsCleanup ? "Retry cleanup" : "Disconnect";
            cancelTestItem.Visible = testCancellation is not null;
            cancelTestItem.Enabled = testCancellation is { IsCancellationRequested: false };
            reconnectItem.Text = connection.HasTunnel ? "Reconnect" : "Retry last connection";
            reconnectItem.Enabled = !busy && restartConnection is not null;
            detailsItem.Enabled = !busy && connection.HasTunnel;
            onDemandItem.Text = armedRoute is null ? "Connect when a selected app starts" : "Cancel on-demand connection";
            holdItem.Enabled = !busy && onDemandOwnsTunnel && connection.HasTunnel;
            holdItem.Checked = holdOnDemandTunnel;
            idleItem.Enabled = !busy;
        };
        menu.Items.Add("Exit RouteFinder", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        FormClosing += OnFormClosing;
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && minimizeToTray.Checked && connection.HasTunnel)
            { Hide(); tray.Visible = true; }
        };
        Shown += async (_, _) =>
        {
            if (!preview)
            {
                _ = CheckStartupUpdateAsync();
                ApplyWindowPreset();
                _ = CheckStartupNetworkLockAsync();
            }
            if (preferences.Ipv6PreferenceConfigured)
            {
                try { await ApplyIpv6Bindings(preferences.EnableIpv6Bindings); }
                catch (Exception error) { WriteActivity("Could not apply the saved IPv6 adapter setting. " + error.Message); }
            }
            await RestoreRememberedLogin();
        };
        FormClosed += (_, _) => { timer.Dispose(); tray.Dispose(); menu.Dispose(); connection.Dispose(); };

        if (preview) LoadPreview();
        else LoadLocalData();
        PopulateRoutes(restoreLastConnection: true);
        Navigate(overviewPage);
        UpdateControls();
        timer.Start();
    }

    Control BuildSidebar()
    {
        var side = Stack(43, 30, 26, 44, 44, 44, 44, 44, 0, 48);
        side.Padding = new Padding(14, 20, 14, 16);
        side.BackColor = Sidebar;
        side.Controls.Add(Label("◈  RouteFinder", 13, true, Accent), 0, 0);
        side.Controls.Add(Label("     APP ROUTER", 8, true, Muted), 0, 1);
        var buttons = new[] { overviewNav, appsNav, routesNav, activityNav, settingsNav };
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Dock = DockStyle.Fill;
            buttons[i].TextAlign = ContentAlignment.MiddleLeft;
            buttons[i].Margin = new Padding(0, 3, 0, 4);
            side.Controls.Add(buttons[i], 0, i + 3);
        }
        side.Controls.Add(Label("     WINDOWS DESKTOP", 8, true, Muted), 0, 9);
        return side;
    }

    Control BuildFooter()
    {
        var outer = new Panel { Dock = DockStyle.Fill, BackColor = Card, Margin = new Padding(0, 12, 0, 0), Padding = new Padding(16, 9, 14, 9) };
        var status = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 12, 0) };
        banner.Dock = DockStyle.Top;
        banner.Height = 25;
        status.Controls.Add(traffic);
        status.Controls.Add(banner);
        connect.Dock = UiLanguage.IsRightToLeft ? DockStyle.Left : DockStyle.Right;
        connect.Width = 150;
        connect.AutoSize = false;
        outer.Controls.Add(status);
        outer.Controls.Add(connect);
        return outer;
    }

    void BuildRoutes()
    {
        var page = Stack(30, 55, 39, 78, 50, 0);
        page.Controls.Add(Label("ROUTE LIBRARY", 9, true, Accent), 0, 0);
        page.Controls.Add(Label("Routes", 30, true), 0, 1);
        page.Controls.Add(Label("Choose the connection your traffic will use.", 11, false, Muted), 0, 2);
        var sources = Split();
        var local = Stack(23, 0);
        local.BackColor = Card; local.Padding = new Padding(12, 5, 8, 5);
        local.Margin = new Padding(0, 0, 7, 6);
        local.Controls.Add(Label("LOCAL CONFIGURATION", 8, true, Accent), 0, 0);
        var localActions = Actions();
        localActions.Controls.Add(import);
        local.Controls.Add(localActions, 0, 1);
        sources.Controls.Add(local, 0, 0);
        var service = Stack(23, 0);
        service.BackColor = Card; service.Padding = new Padding(12, 5, 8, 5);
        service.Margin = new Padding(7, 0, 0, 6);
        var sourceHeader = Split(145);
        sourceHeader.Controls.Add(Label("EZCONNECT ACCOUNT", 8, true, Accent), 0, 0);
        account.TextAlign = ContentAlignment.MiddleRight;
        sourceHeader.Controls.Add(account, 1, 0);
        service.Controls.Add(sourceHeader, 0, 0);
        var serviceActions = Actions();
        serviceActions.Controls.AddRange([signIn, refresh]);
        Add(serviceActions, "Account", ShowAccountDetails);
        service.Controls.Add(serviceActions, 0, 1);
        sources.Controls.Add(service, 1, 0);
        page.Controls.Add(sources, 0, 3);

        routeCategory.Width = 137;
        routeCategory.Margin = new Padding(0, 5, 8, 0);
        routeSort.Margin = new Padding(0, 5, 8, 0);
        StylePicker(routeCategory);
        StylePicker(routeSort);
        routeCategory.Items.AddRange(new[] { "All routes", "Game routes", "Download servers", "Local profiles" }.Select(UiLanguage.Text).ToArray());
        routeCategory.SelectedIndex = 0;
        routeCategory.SelectedIndexChanged += (_, _) => PopulateRoutes();
        var toolbar = Split(520);
        toolbar.Padding = new Padding(0, 6, 0, 0);
        search.Dock = DockStyle.Top; search.AccessibleName = "Search routes";
        toolbar.Controls.Add(search, 0, 0);
        var routeFilters = Actions(); routeFilters.Padding = Padding.Empty;
        routeSort.Items.AddRange(["Sort by latency", "Sort by packet loss", "Sort by jitter"]);
        routeSort.SelectedIndex = 0;
        routeSort.SelectedIndexChanged += (_, _) =>
        {
            if (loadingPreferences) return;
            SavePreferences();
            PopulateRoutes();
        };
        routeFilters.Controls.AddRange([routeCategory, routeSort, favoritesOnly, compare]);
        compare.Margin = new Padding(9, 0, 0, 0);
        toolbar.Controls.Add(routeFilters, 1, 0);
        page.Controls.Add(toolbar, 0, 4);

        var body = Split(278);
        StyleGrid();
        var listHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Card };
        emptyRoutes.TextAlign = ContentAlignment.MiddleCenter;
        emptyRoutes.Padding = new Padding(30);
        listHost.Controls.Add(emptyRoutes);
        listHost.Controls.Add(grid);
        var results = Stack(0, 25);
        results.Controls.Add(listHost, 0, 0);
        results.Controls.Add(routeCount, 0, 1);
        body.Controls.Add(results, 0, 0);
        var card = Stack(23, 42, 26, 30, 24, 29, 0, 48);
        card.Padding = new Padding(14, 8, 10, 6); card.BackColor = Card;
        card.Margin = new Padding(12, 0, 0, 0);
        card.Controls.Add(Label("SELECTED ROUTE", 8, true, Accent), 0, 0);
        card.Controls.Add(routeTitle, 0, 1);
        card.Controls.Add(routeSubtitle, 0, 2);
        card.Controls.Add(routeEndpoint, 0, 3);
        card.Controls.Add(Label("LAST HANDSHAKE TEST", 8, true, Muted), 0, 4);
        card.Controls.Add(routeHealth, 0, 5);
        card.Controls.Add(routeHealthDetail, 0, 6);
        var actions = Actions(); actions.Controls.AddRange([test, favorite]);
        card.Controls.Add(actions, 0, 7);
        body.Controls.Add(card, 1, 0);
        page.Controls.Add(body, 0, 5);
        routePage.Controls.Add(page);
    }

    void StyleGrid()
    {
        grid.Dock = DockStyle.Fill;
        grid.Margin = Padding.Empty;
        grid.AccessibleName = "Available routes and saved profiles";
        grid.BackgroundColor = Card;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Base;
        grid.EnableHeadersVisualStyles = false;
        grid.RowHeadersVisible = false;
        grid.ColumnHeadersHeight = 37;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersDefaultCellStyle = new() { BackColor = Raised, ForeColor = Muted, SelectionBackColor = Raised, SelectionForeColor = Muted, Padding = new Padding(10, 0, 0, 0), Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        grid.DefaultCellStyle = new() { BackColor = Card, ForeColor = Ink, SelectionBackColor = Color.FromArgb(34, 76, 66), SelectionForeColor = Ink, Padding = new Padding(10, 0, 0, 0) };
        grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = false;
        grid.ReadOnly = true;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.RowTemplate.Height = 42;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (var (name, weight) in new[] { ("Route", 44), ("Region", 13), ("Last test", 18), ("Source", 18), ("★", 8) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = UiLanguage.Text(name), FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns[^1].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        var profileActions = new ContextMenuStrip();
        profileActions.Items.Add("Edit local profile", null, (_, _) => EditProfile());
        profileActions.Items.Add("Save checked apps to profile", null, (_, _) => SaveProfileApps());
        profileActions.Items.Add("Load profile apps", null, (_, _) => LoadProfileApps());
        profileActions.Items.Add("Connect with saved apps", null, async (_, _) =>
        {
            if (busy || connection.HasTunnel || connection.NeedsCleanup) return;
            if (selected?.Profile?.Applications is not { Count: > 0 } saved)
            {
                ShowMessage("Save checked apps to this profile before connecting with its app list.");
                return;
            }
            await ToggleConnection(savedApps: saved.ToArray());
        });
        profileActions.Items.Add("Export local profile…", null, (_, _) => ExportProfile());
        profileActions.Items.Add("Remove local profile", null, (_, _) => RemoveProfile());
        var profileItems = profileActions.Items.Cast<ToolStripItem>().ToArray();
        var clearMeasurement = profileActions.Items.Add("Clear saved route test", null, (_, _) =>
        {
            if (busy || selected is not { } route || !measurements.Remove(route.Key)) return;
            SaveMeasurements();
            PopulateRoutes();
            WriteActivity($"Cleared the saved test for {route.Title}.");
        });
        profileActions.Opening += (_, args) =>
        {
            bool canEditProfile = selected?.Profile is not null && !busy && !connection.HasTunnel;
            foreach (var item in profileItems) item.Visible = canEditProfile;
            bool canClear = !busy && selected is { } route && measurements.ContainsKey(route.Key);
            clearMeasurement.Available = canClear;
            args.Cancel = !canEditProfile && !canClear;
        };
        grid.ContextMenuStrip = profileActions;
        grid.CellMouseDown += (_, args) =>
        {
            if (args.Button == MouseButtons.Right && args.RowIndex >= 0)
                grid.CurrentCell = grid.Rows[args.RowIndex].Cells[0];
        };
        FormClosed += (_, _) => profileActions.Dispose();
    }

    void BuildApps()
    {
        var page = Stack(30, 55, 39, 49, 50, 48, 33, 0, 43);
        page.Controls.Add(Label("TRAFFIC SCOPE", 9, true, Accent), 0, 0);
        page.Controls.Add(Label("Applications", 30, true), 0, 1);
        page.Controls.Add(Label("Choose which traffic uses your route.", 11, false, Muted), 0, 2);
        var actions = Actions();
        Add(actions, "Add running app", () => PickRunning(null));
        Add(actions, "Browse for app…", AddFile);
        Add(actions, "Remove selected", RemoveApp);
        Add(actions, "Connect on app launch", ToggleOnDemandConnection);
        page.Controls.Add(actions, 0, 4);
        routingMode.Items.AddRange(new[] { "Selected apps", "Entire PC (IPv4 + supported IPv6)" }.Select(UiLanguage.Text).ToArray());
        routingMode.SelectedIndex = 0;
        var modeRow = Actions();
        var scopeLabel = Label("Route traffic from", 10, true);
        scopeLabel.Width = 133;
        scopeLabel.Height = 28;
        modeRow.Controls.Add(scopeLabel);
        routingMode.AccessibleName = "Traffic scope";
        StylePicker(routingMode);
        modeRow.Controls.Add(routingMode);
        page.Controls.Add(modeRow, 0, 3);
        var gameRow = Actions();
        games.AccessibleName = "EZConnect app and game catalog";
        StylePicker(games);
        games.Margin = new Padding(0, 6, 10, 0);
        games.DisplayMember = nameof(CatalogGame.DisplayName);
        gameRow.Controls.Add(games);
        Add(gameRow, "Find running catalog app", () =>
        {
            if (games.SelectedItem is not CatalogGame game) { appHint.Text = "Sign in to browse the EZConnect app and game catalog."; return; }
            PickRunning(autoAddLinkedApps.Checked ? CatalogExecutables(game) : game.ExecutableNames);
        });
        page.Controls.Add(gameRow, 0, 5);
        var catalogActions = new ContextMenuStrip();
        catalogActions.Items.Add("Recommended game route", null, (_, _) => SelectRecommendedGameRoute());
        catalogActions.Items.Add("Test recommended region", null, async (_, _) =>
        {
            if (games.SelectedItem is not CatalogGame { IsApplication: false } game ||
                string.IsNullOrWhiteSpace(game.RecommendedRegion))
            {
                ShowMessage("Select a catalog game with a recommended region.");
                return;
            }
            await CompareRoutes(region: game.RecommendedRegion);
        });
        catalogActions.Items.Add("Game servers", null, (_, _) => ShowGameServers());
        catalogActions.Items.Add("Test custom destination", null, async (_, _) => await TestCustomDestination());
        var linkedApps = new ToolStripMenuItem(autoAddLinkedApps.Text) { CheckOnClick = true };
        linkedApps.CheckedChanged += (_, _) => autoAddLinkedApps.Checked = linkedApps.Checked;
        catalogActions.Items.Add(linkedApps);
        catalogActions.Opening += (_, _) => linkedApps.Checked = autoAddLinkedApps.Checked;
        var more = Button("Catalog tools ▾");
        more.Click += (_, _) => catalogActions.Show(more, new Point(0, more.Height));
        gameRow.Controls.Add(more);
        FormClosed += (_, _) => { catalogActions.Dispose(); autoAddLinkedApps.Dispose(); };
        page.Controls.Add(appsCount, 0, 6);
        apps.BackColor = Card; apps.ForeColor = Ink; apps.BorderStyle = BorderStyle.None;
        apps.Font = new Font("Segoe UI", 11); apps.Margin = Padding.Empty;
        apps.AccessibleName = "Applications selected for routing";
        page.Controls.Add(apps, 0, 7);
        page.Controls.Add(appHint, 0, 8);
        appsPage.Controls.Add(page);
    }

    void BuildActivity()
    {
        var page = Stack(52, 38, 44, 44, 0);
        page.Controls.Add(Label("Activity", 24, true), 0, 0);
        page.Controls.Add(Label("Sign-in, route tests, and tunnel events from this run.", 10, false, Muted), 0, 1);
        journal.Multiline = true; journal.ReadOnly = true; journal.Dock = DockStyle.Fill; journal.ScrollBars = ScrollBars.Vertical;
        journal.Margin = Padding.Empty; journal.BackColor = Card;
        var actions = Actions();
        var diagnostics = Button("Run network diagnostics");
        diagnostics.Click += async (_, _) =>
        {
            diagnostics.Enabled = false;
            try { await Run("Checking network…", RunNetworkDiagnostics); }
            finally { if (!diagnostics.IsDisposed) diagnostics.Enabled = true; }
        };
        actions.Controls.Add(diagnostics);
        var updates = Button("Check for updates");
        updates.Click += async (_, _) =>
        {
            updates.Enabled = false;
            try
            {
                var result = await UpdateCheck.CheckAsync();
                if (IsDisposed || Disposing) return;
                if (result is { Available: true, Download: { } package })
                {
                    using var picker = new SaveFileDialog
                    {
                        Title = result.Message,
                        FileName = "RouteFinder-update.exe",
                        Filter = "Windows executable (*.exe)|*.exe",
                        DefaultExt = "exe"
                    };
                    if (picker.ShowDialog(this) == DialogResult.OK)
                    {
                        await UpdateCheck.DownloadAsync(package, picker.FileName);
                        if (!IsDisposed && !Disposing) ShowMessage("Update saved and matched the release checksum.");
                    }
                }
                else if (result is { Available: true, ReleasePage: { } page })
                {
                    if (MessageBox.Show(this, result.Message + "\n\nOpen the release page?", "RouteFinder update",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
                }
                else if (result is null) ConfigureUpdateFeed();
                else ShowMessage(result.Message);
            }
            catch (Exception error) { if (!IsDisposed && !Disposing) ShowMessage($"Update check failed. {error.Message}"); }
            finally { if (!updates.IsDisposed) updates.Enabled = true; }
        };
        actions.Controls.Add(updates);
        Add(actions, "Update feed", ConfigureUpdateFeed);
        Add(actions, "Mobile hotspot", ShowHotspotStatus);
        var recoveryActions = Actions();
        var restore = Button("Retry saved sign-in");
        restore.Click += async (_, _) => await RestoreRememberedLogin(interactive: true);
        recoveryActions.Controls.Add(restore);
        Add(recoveryActions, "Save support report", SaveSupportReport);
        Add(recoveryActions, "Repair WireSock", RepairWireSock);
        Add(recoveryActions, "Service news", ShowServiceOverview);
        Add(recoveryActions, "Reset network lock", () =>
        {
            if (busy || connection.HasTunnel || connection.NeedsCleanup)
            {
                ShowMessage("Disconnect and finish session cleanup before resetting the network lock.");
                return;
            }
            if (!EnsureAdministrator()) return;
            if (MessageBox.Show(this, "This resets WireSock's driver-wide kill switch. Disconnect any other WireSock clients first. Reset it now?",
                "Reset network lock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try
            {
                using var sdk = new WireSockTunnel();
                sdk.ResetDriverNetworkLock();
                activityNav.Text = UiLanguage.Text("Activity");
                WriteActivity("WireSock reported that the driver network lock was reset.");
            }
            catch (Exception error) { ShowMessage(error.Message); }
        });
        page.Controls.Add(actions, 0, 2);
        page.Controls.Add(recoveryActions, 0, 3);
        page.Controls.Add(journal, 0, 4);
        activityPage.Controls.Add(page);
    }

    void ConfigureUpdateFeed()
    {
        try
        {
            using var dialog = Dialog("RouteFinder update feed", new Size(610, 170));
            var layout = Stack(48, 40, 48);
            layout.Controls.Add(Label("Enter the HTTPS URL of the release manifest. Leave blank to disable checks."), 0, 0);
            var endpoint = Input("https://");
            endpoint.Dock = DockStyle.Top;
            endpoint.RightToLeft = RightToLeft.No;
            endpoint.Text = UpdateCheck.ReadFeed();
            layout.Controls.Add(endpoint, 0, 1);
            var actions = Actions();
            Add(actions, "Save update feed", () =>
            {
                try { UpdateCheck.SaveFeed(endpoint.Text); dialog.DialogResult = DialogResult.OK; }
                catch (Exception error) { MessageBox.Show(dialog, error.Message, "RouteFinder update feed"); }
            });
            layout.Controls.Add(actions, 0, 2);
            dialog.Controls.Add(layout);
            dialog.ShowDialog(this);
        }
        catch (Exception error) { ShowMessage(error.Message); }
    }

    async Task CheckStartupUpdateAsync()
    {
        try
        {
            var result = await UpdateCheck.CheckAsync();
            if (result is { Available: true } && !IsDisposed && !Disposing)
                WriteActivity(result.Message + " Open Activity and choose Check for updates for details.");
        }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing) WriteActivity($"Startup update check failed. {error.Message}");
        }
    }

    void ShowHotspotStatus()
    {
        string status;
        bool hasSharedDownstream = false;
        var downstreamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var managerType = Type.GetTypeFromProgID("HNetCfg.HNetShare", throwOnError: true)!;
            dynamic manager = Activator.CreateInstance(managerType)!;
            var shared = new List<string>();
            foreach (object entry in manager.EnumEveryConnection)
            {
                dynamic properties = manager.NetConnectionProps(entry);
                dynamic configuration = manager.INetSharingConfigurationForINetConnection(entry);
                if (!configuration.SharingEnabled) continue;
                string name = properties.Name;
                int role = configuration.SharingConnectionType;
                hasSharedDownstream |= role == 1;
                if (role == 1) downstreamNames.Add(name);
                shared.Add($"{name} • {(role == 0 ? "shared upstream" : "shared downstream")}");
            }
            status = shared.Count == 0 ? "Windows reports no active Internet Connection Sharing configuration."
                : string.Join(Environment.NewLine, shared);
        }
        catch (Exception error)
        {
            status = $"Windows sharing status is unavailable. {error.Message}";
        }

        using var dialog = Dialog("Mobile hotspot and console setup", new Size(660, 320));
        var layout = Stack(38, 0, 48);
        layout.Controls.Add(Label("Windows Internet Connection Sharing", 12, true), 0, 0);
        var details = Input();
        details.Multiline = true;
        details.ReadOnly = true;
        details.ScrollBars = ScrollBars.Vertical;
        details.Dock = DockStyle.Fill;
        details.Text = status;
        if (downstreamNames.Count > 0)
            dialog.Shown += async (_, _) =>
            {
                try
                {
                    var neighbors = await ReadHotspotNeighborsAsync(downstreamNames);
                    if (!dialog.IsDisposed)
                        details.AppendText(Environment.NewLine + Environment.NewLine +
                            "Recently seen IPv4 neighbors on shared adapters" + Environment.NewLine +
                            (neighbors.Length == 0 ? "No entries reported by Windows." : string.Join(Environment.NewLine, neighbors)));
                }
                catch (Exception error)
                {
                    if (!dialog.IsDisposed) details.AppendText(Environment.NewLine + $"Neighbor status unavailable. {error.Message}");
                }
            };
        layout.Controls.Add(details, 0, 1);
        var actions = Actions();
        Add(actions, "Open Windows Mobile hotspot", () =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true }); }
            catch (Exception error) { ShowMessage($"Could not open Mobile hotspot settings. {error.Message}"); }
        });
        var console = Button("Connect selected route for console");
        console.Enabled = !busy && !connection.HasTunnel && !connection.NeedsCleanup &&
            hasSharedDownstream && selected?.Route is { IsDownload: false };
        console.Click += (_, _) => { dialog.DialogResult = DialogResult.OK; };
        actions.Controls.Add(console);
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _ = ToggleConnection(consoleMode: true);
    }

    async Task CheckStartupNetworkLockAsync()
    {
        try
        {
            if (await QueryNetworkLockAsync() != true || IsDisposed || Disposing || connection.HasTunnel)
                return;

            WriteActivity("WireSock reported an active driver network lock at startup. If no other WireSock client is connected, use Activity → Reset network lock.");
            activityNav.Text = UiLanguage.Text("Activity") + " ⚠";
        }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing)
                WriteActivity($"Startup network lock check was unavailable. {error.Message}");
        }
    }

    void SaveSupportReport()
    {
        using var picker = new SaveFileDialog
        {
            Filter = "Text report (*.txt)|*.txt",
            FileName = "RouteFinder-support.txt",
            DefaultExt = "txt",
            AddExtension = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllLines(picker.FileName, SupportSnapshot()); }
        catch (Exception error) { ShowMessage($"Could not save the support report. {error.Message}"); }
    }

    void ShowConnectionDetails()
    {
        if (busy || !connection.HasTunnel) return;
        using var dialog = Dialog("Connection details", new Size(500, 305));
        var details = Input();
        details.Multiline = true;
        details.ReadOnly = true;
        details.Dock = DockStyle.Fill;
        details.ScrollBars = ScrollBars.Vertical;
        var layout = Stack(0, 82);
        layout.Controls.Add(details, 0, 0);
        var actions = Actions();
        var destination = Input("Destination IP");
        destination.Width = 210;
        actions.Controls.Add(destination);
        var probe = Button("Ping while connected");
        actions.Controls.Add(probe);
        var tcp = Button("Test TCP port");
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 443, Width = 70, AccessibleName = "TCP port" };
        Add(actions, "Copy details", () =>
        {
            try { Clipboard.SetText(details.Text); }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                MessageBox.Show(dialog, "The clipboard is busy. Try copying again.");
            }
        });
        actions.SetFlowBreak(actions.Controls[^1], true);
        actions.Controls.Add(port);
        actions.Controls.Add(tcp);
        Add(actions, "App sockets", ShowAppSockets);
        Add(actions, "Reset tunnel TCP", async () =>
        {
            if (busy || !connection.HasTunnel) return;
            if (MessageBox.Show(dialog, "Drop active TCP connections through this tunnel? Apps may need to reconnect.",
                "Reset tunnel TCP", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await Run("Resetting tunnel TCP connections…", async () =>
            {
                await Task.Run(connection.ResetTunnelTcpConnections);
                WriteActivity("WireSock reset TCP connections through the active tunnel.");
            });
        });
        layout.Controls.Add(actions, 0, 1);
        dialog.Controls.Add(layout);
        using var probeCancellation = new CancellationTokenSource();
        dialog.FormClosed += (_, _) => probeCancellation.Cancel();
        probe.Click += async (_, _) => await ProbeConnected(false);
        tcp.Click += async (_, _) => await ProbeConnected(true);
        async Task ProbeConnected(bool useTcp)
        {
            if (!connection.HasTunnel || !System.Net.IPAddress.TryParse(destination.Text.Trim(), out var address))
            {
                MessageBox.Show(dialog, "An active connection and a valid destination IP are required.");
                return;
            }
            probe.Enabled = tcp.Enabled = false;
            try
            {
                var report = useTcp
                    ? await Diagnostics.TcpProbe(address.ToString(), (int)port.Value, probeCancellation.Token)
                    : await Diagnostics.Probe(address.ToString(), cancellation: probeCancellation.Token);
                if (!probeCancellation.IsCancellationRequested)
                    MessageBox.Show(dialog, report, "Windows probe · Tunnel path unverified");
            }
            catch (OperationCanceledException) when (probeCancellation.IsCancellationRequested) { }
            finally { if (!probe.IsDisposed) probe.Enabled = tcp.Enabled = true; }
        }
        (ulong Rx, ulong Tx, long Timestamp)? previousTraffic = null;
        void RefreshDetails()
        {
            if (!connection.HasTunnel)
            {
                details.Text = "Disconnected";
                return;
            }
            var lines = new List<string>
            {
                $"Route   {connected?.Title}",
                $"Endpoint   {connectedEndpoint}",
                $"Configured MTU   {(string.IsNullOrEmpty(connection.ActiveMtu) ? "Engine default" : connection.ActiveMtu)}",
                $"Configured keepalive   {(string.IsNullOrEmpty(connection.ActiveKeepalive) ? "Profile omitted" : connection.ActiveKeepalive + " s")}",
                $"Mode   {(connectedConsole ? "Console" : connectedEntirePc ? "Entire PC" : "Selected apps")}",
                $"Tunnel uptime   {DateTimeOffset.UtcNow - connectionStartedAt.GetValueOrDefault():hh\\:mm\\:ss}"
            };
            try
            {
                if (connection.State is { } stats)
                {
                    long timestamp = Stopwatch.GetTimestamp();
                    if (previousTraffic is { } previous && stats.RxBytes >= previous.Rx && stats.TxBytes >= previous.Tx)
                    {
                        double elapsed = Stopwatch.GetElapsedTime(previous.Timestamp, timestamp).TotalSeconds;
                        if (elapsed > 0)
                        {
                            lines.Add($"Download rate   {FormatBytes((ulong)((stats.RxBytes - previous.Rx) / elapsed))}/s");
                            lines.Add($"Upload rate   {FormatBytes((ulong)((stats.TxBytes - previous.Tx) / elapsed))}/s");
                        }
                    }
                    previousTraffic = (stats.RxBytes, stats.TxBytes, timestamp);
                    lines.Add(stats.TimeSinceLastHandshakeMilliseconds < 0 ? "Handshake   Not reported" :
                        $"Handshake   {stats.TimeSinceLastHandshakeMilliseconds / 1000}s ago");
                    lines.Add($"Received   {FormatBytes(stats.RxBytes)}");
                    lines.Add($"Sent   {FormatBytes(stats.TxBytes)}");
                    if (stats.EstimatedRtt > 0) lines.Add($"Estimated RTT   {stats.EstimatedRtt} ms");
                    if (stats.RxBytes > 0 && float.IsFinite(stats.EstimatedLoss) && stats.EstimatedLoss is >= 0 and <= 100)
                        lines.Add($"Estimated loss   {stats.EstimatedLoss:0.#}%");
                }
            }
            catch (Exception error) { lines.Add($"Statistics unavailable   {error.Message}"); }
            lines.Add("");
            lines.AddRange(connection.ActiveRoutingDetails);
            if (connection.ActiveProxyStatus is { } proxyStatus)
                lines.Add($"SOCKS5 status   {proxyStatus}");
            if (connection.ActiveTunnelError is uint tunnelError)
                lines.Add($"Last WireSock tunnel error   {tunnelError} (0x{tunnelError:X8})");
            string snapshot = string.Join(Environment.NewLine, lines);
            if (details.Text != snapshot)
            {
                int start = details.SelectionStart;
                int length = details.SelectionLength;
                details.Text = snapshot;
                details.Select(Math.Min(start, snapshot.Length), Math.Min(length, Math.Max(0, snapshot.Length - start)));
            }
        }
        using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => RefreshDetails();
        RefreshDetails();
        timer.Start();
        dialog.ShowDialog(this);
    }

    async void ShowAppSockets()
    {
        using var dialog = Dialog("App sockets", new Size(870, 460));
        var layout = Stack(35, 0, 44);
        layout.Controls.Add(Label("Windows TCP and UDP sockets by process. Tunnel membership and byte counts are unavailable.", 10), 0, 0);
        var output = Input();
        output.Multiline = true;
        output.ReadOnly = true;
        output.WordWrap = false;
        output.ScrollBars = ScrollBars.Both;
        output.Dock = DockStyle.Fill;
        output.Font = new Font(FontFamily.GenericMonospace, 9);
        layout.Controls.Add(output, 0, 1);
        var actions = Actions();
        var refreshSockets = Button("Refresh");
        actions.Controls.Add(refreshSockets);
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        using var cancellation = new CancellationTokenSource();
        dialog.FormClosed += (_, _) => cancellation.Cancel();
        async Task RefreshSockets()
        {
            refreshSockets.Enabled = false;
            try
            {
                string snapshot = await Diagnostics.ReadSocketSnapshot(cancellation.Token);
                if (!dialog.IsDisposed) output.Text = snapshot.Length == 0 ? "No sockets reported." : snapshot;
            }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested && !dialog.IsDisposed)
                    output.Text = error is OperationCanceledException ? "Socket lookup timed out." : error.Message;
            }
            finally { if (!dialog.IsDisposed) refreshSockets.Enabled = true; }
        }
        refreshSockets.Click += async (_, _) => await RefreshSockets();
        dialog.Shown += async (_, _) => await RefreshSockets();
        dialog.ShowDialog(this);
    }

    IEnumerable<string> SupportSnapshot()
    {
        yield return $"RouteFinder {typeof(MainForm).Assembly.GetName().Version} • {DateTimeOffset.Now:O}";
        yield return $"Windows {Environment.OSVersion.Version} • {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}";
        yield return $"EZConnect account {(connection.SignedIn ? "signed in" : "signed out")}";
        string sdkVersion;
        try { sdkVersion = FileVersionInfo.GetVersionInfo(WireSockTunnel.FindSdkPath()).FileVersion ?? "unknown"; }
        catch (Exception error) { sdkVersion = $"unavailable ({error.GetType().Name})"; }
        yield return $"WireSock SDK {sdkVersion}";
        yield return $"Tunnel {(connection.HasTunnel ? "running" : "stopped")} • Cleanup pending {connection.NeedsCleanup && !connection.HasTunnel}";
        if (connection.HasTunnel)
        {
            yield return $"Mode {(connectedConsole ? "Console" : connectedEntirePc ? "Entire PC" : "Selected apps")} • Backend {(connectedConsole || portableEngine.Checked ? "virtual adapter" : "transparent")}";
            if (connection.State is { } stats)
                yield return $"Handshake age {stats.TimeSinceLastHandshakeMilliseconds} ms • RX {stats.RxBytes} bytes • TX {stats.TxBytes} bytes";
        }
        foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            bool ipv6 = adapter.Supports(System.Net.NetworkInformation.NetworkInterfaceComponent.IPv6);
            yield return $"{adapter.NetworkInterfaceType} • {adapter.OperationalStatus} • IPv6 {(ipv6 ? "available" : "unavailable")}";
        }
    }

    async Task RunNetworkDiagnostics()
    {
        foreach (string line in await Task.Run(ReadWireSockStatus))
            WriteActivity(line);
        try
        {
            var networkLock = await QueryNetworkLockAsync();
            WriteActivity(networkLock switch
            {
                true => "WireSock driver network lock is active.",
                false => "WireSock reports no active driver network lock.",
                null => "WireSock driver network lock status is unavailable."
            });
        }
        catch (Exception error) { WriteActivity($"WireSock driver network lock status is unavailable. {error.Message}"); }

        WriteActivity($"Windows {Environment.OSVersion.Version}. Account {(connection.SignedIn ? "signed in" : "signed out")}.");
        foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            bool ipv6 = adapter.Supports(System.Net.NetworkInformation.NetworkInterfaceComponent.IPv6);
            WriteActivity($"{adapter.Name} • {adapter.NetworkInterfaceType} • {adapter.OperationalStatus} • IPv6 {(ipv6 ? "available" : "unavailable")}");
        }

        WriteActivity("Probing 1.1.1.1 over the current Windows route.");
        using var ping = new System.Net.NetworkInformation.Ping();
        var times = new List<long>();
        var differences = new List<long>();
        long? previous = null;
        const int count = 8;
        for (int i = 1; i <= count; i++)
        {
            var reply = await ping.SendPingAsync(System.Net.IPAddress.Parse("1.1.1.1"), 1000);
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
            {
                long rtt = reply.RoundtripTime;
                if (previous is long last) differences.Add(Math.Abs(rtt - last));
                previous = rtt;
                times.Add(rtt);
                WriteActivity($"Probe {i} • {rtt} ms • Reply TTL {reply.Options?.Ttl.ToString() ?? "unavailable"}");
            }
            else
            {
                previous = null;
                WriteActivity($"Probe {i} • {reply.Status}");
            }
            if (i < count) await Task.Delay(150);
        }

        string latency = times.Count == 0 ? "unavailable" : $"{times.Average():0.0} ms";
        string variation = differences.Count == 0 ? "unavailable" : $"{differences.Average():0.0} ms";
        WriteActivity($"ICMP summary • {times.Count}/{count} replies • Loss {(count - times.Count) * 100.0 / count:0.#}% • Mean RTT {latency} • Mean consecutive RTT variation {variation}");
    }

    async void RepairWireSock()
    {
        if (busy || connection.HasTunnel || connection.NeedsCleanup)
        {
            ShowMessage("Disconnect and finish session cleanup before repairing WireSock.");
            return;
        }
        try
        {
            using var uninstall = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            var packages = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in uninstall?.GetSubKeyNames() ?? [])
            {
                if (!Guid.TryParseExact(key, "B", out var product)) continue;
                using var registration = uninstall!.OpenSubKey(key);
                if (registration?.GetValue("WindowsInstaller") is not 1 ||
                    registration.GetValue("DisplayName") is not string name ||
                    name is not ("WireSock Secure Connect SDK" or "WireSock Kernel Drivers")) continue;
                packages.TryAdd(name, product);
            }
            using var dialog = Dialog("WireSock setup and repair", new Size(640, 225));
            var layout = Stack(76, 38, 48);
            layout.Controls.Add(Label(packages.Count == 0
                ? "No WireSock MSI package is registered. Open the vendor download page to get the Windows x64 SDK."
                : "Disconnect other WireSock clients before repairing. For a clean reinstall, get the x64 SDK installer first, then remove the old package in Installed apps."), 0, 0);
            var selection = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
            selection.Items.AddRange(packages.Keys.ToArray());
            if (selection.Items.Count > 0) selection.SelectedIndex = 0;
            selection.Enabled = packages.Count > 0;
            layout.Controls.Add(selection, 0, 1);
            var actions = Actions();
            var repair = Button("Start repair", true);
            repair.Enabled = packages.Count > 0;
            repair.DialogResult = DialogResult.OK;
            actions.Controls.Add(repair);
            Add(actions, "Official SDK download", () => OpenSetupPage("https://wiresock.net/wiresock-sdk"));
            Add(actions, "Windows Installed apps", () => OpenSetupPage("ms-settings:appsfeatures"));
            layout.Controls.Add(actions, 0, 2);
            dialog.Controls.Add(layout);
            if (dialog.ShowDialog(this) != DialogResult.OK || selection.SelectedItem is not string selectedPackage) return;
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"/fomus {packages[selectedPackage]:B} /norestart"
            };
            await Run($"Repairing {selectedPackage}…", async () =>
            {
                using var installer = Process.Start(start) ??
                    throw new InvalidOperationException("Windows Installer did not return a repair process.");
                WriteActivity($"Windows Installer repair started for {selectedPackage}.");
                await installer.WaitForExitAsync();
                string result = installer.ExitCode switch
                {
                    0 => $"{selectedPackage} repair completed.",
                    3010 or 1641 => $"{selectedPackage} repair completed. Restart Windows before connecting.",
                    1602 => $"{selectedPackage} repair was canceled.",
                    1618 => "Another Windows Installer operation is running. Try repair again after it finishes.",
                    _ => $"Windows Installer repair exited with code {installer.ExitCode}."
                };
                WriteActivity(result);
                ShowMessage(result);
            });
        }
        catch (Exception error) { ShowMessage($"Could not open WireSock repair. {error.Message}"); }
    }

    void OpenSetupPage(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception error) { ShowMessage($"Could not open the setup page. {error.Message}"); }
    }

    void BuildSettings()
    {
        var page = Stack(52, 35, 54, 35, 36, 42, 35, 42, 35, 42, 35, 42, 35, 40, 0, 46);
        page.Padding = new Padding(4, 2, 4, 0);
        page.Controls.Add(Label("Settings", 24, true), 0, 0);
        page.Controls.Add(Label("RouteFinder preferences and local diagnostics.", 10, false, Muted), 0, 1);
        page.Controls.Add(Label("WINDOW", 9, true, Accent), 0, 2);
        var windowActions = Actions();
        windowActions.Controls.Add(minimizeToTray);
        windowPreset.Items.AddRange(WindowSizes.Select(size => $"{size.Width} × {size.Height}").ToArray());
        windowPreset.SelectedIndex = 0;
        windowPreset.SelectedIndexChanged += (_, _) =>
        {
            if (loadingPreferences) return;
            ApplyWindowPreset();
            SavePreferences();
        };
        windowActions.Controls.Add(Label("Window resolution", 9, true));
        windowActions.Controls.Add(windowPreset);
        showTutorials.CheckedChanged += (_, _) => SavePreferences();
        windowActions.Controls.Add(showTutorials);
        page.Controls.Add(windowActions, 0, 3);
        page.Controls.Add(Label("CONNECTION", 9, true, Accent), 0, 4);
        page.Controls.Add(ipv6Bindings, 0, 5);
        page.Controls.Add(Label("Changes IPv6 binding on active adapters. Windows may briefly reconnect them; administrator access is required.", 9, false, Muted), 0, 6);
        page.Controls.Add(autoMtu, 0, 7);
        page.Controls.Add(Label("Probes path MTU with ICMP Don't Fragment packets and lowers the tunnel MTU when a responsive target is available.", 9, false, Muted), 0, 8);
        page.Controls.Add(killSwitch, 0, 9);
        page.Controls.Add(Label("Blocks traffic that the tunnel does not carry while connected. In Selected apps mode, other apps will lose network access until you disconnect.", 9, false, Muted), 0, 10);
        var adapterActions = Actions();
        bypassLan.CheckedChanged += (_, _) => SavePreferences();
        adapterActions.Controls.AddRange([portableEngine, bypassLan]);
        Add(adapterActions, "Edit IP bypass", EditBypassDestinations);
        Add(adapterActions, "MTU / Keepalive", EditKeepalive);
        page.Controls.Add(adapterActions, 0, 11);
        var dns = Stack(24, 0); dns.Controls.Add(Label("Tunnel DNS override (leave blank to use the profile DNS)", 9, true), 0, 0);
        dnsOverride.Dock = DockStyle.Top; dns.Controls.Add(dnsOverride, 0, 1); page.Controls.Add(dns, 0, 12);
        page.Controls.Add(Label("Route tests run through WireSock. Settings, favorites, and test results are saved on this PC, encrypted for your Windows account.", 9, false, Muted), 0, 13);
        var clearActions = Actions();
        Add(clearActions, "SOCKS5 proxy", EditSocksProxy);
        Add(clearActions, "Exclude apps", EditExcludedApplications);
        var language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = UiLanguage.Text("Language") };
        language.Items.AddRange(["English", "فارسی", "العربية"]);
        language.SelectedIndex = UiLanguage.Index;
        clearActions.Controls.Add(language);
        Add(clearActions, "Apply language", () =>
        {
            try
            {
                UiLanguage.Save(language.SelectedIndex);
                ShowMessage(UiLanguage.Text("Restart RouteFinder to apply the language."));
            }
            catch (Exception error) { ShowMessage(error.Message); }
        });
        Add(clearActions, "Clear saved test results", () =>
        {
            measurements.Clear(); SaveMeasurements(); PopulateRoutes(); WriteActivity("Saved route-test results cleared.");
        });
        page.Controls.Add(clearActions, 0, 15);
        settingsPage.Controls.Add(page);
    }

    static void Add(Control parent, string caption, Action action)
    {
        var button = Button(caption);
        button.Click += (_, _) => action();
        parent.Controls.Add(button);
    }

    void Navigate(Panel page)
    {
        foreach (Control panel in workspace.Controls) panel.Visible = panel == page;
        page.BringToFront();
        overviewNav.BackColor = page == overviewPage ? Raised : Sidebar;
        routesNav.BackColor = page == routePage ? Raised : Sidebar;
        appsNav.BackColor = page == appsPage ? Raised : Sidebar;
        activityNav.BackColor = page == activityPage ? Raised : Sidebar;
        settingsNav.BackColor = page == settingsPage ? Raised : Sidebar;
        if (page == overviewPage || !Visible || preview || busy || !showTutorials.Checked) return;
        var (flag, tip) = page == routePage
            ? (1, "Choose a route category, select a route, and use Test route to check for a WireGuard response. Use Applications to choose what the tunnel will carry.")
            : page == appsPage
                ? (2, "Choose Selected apps and add the executables to route, or choose Entire PC. You can also select an EZConnect catalog entry to find running apps.")
                : page == activityPage
                    ? (4, "Route test results and connection events appear here. Use Run network diagnostics or Save support report for troubleshooting.")
                    : (8, "Connection settings apply when you start a tunnel. The kill switch can block traffic outside the tunnel until you disconnect.");
        if ((preferences.SeenTutorials & flag) != 0) return;
        preferences = preferences with { SeenTutorials = preferences.SeenTutorials | flag };
        SavePreferences();
        ShowMessage(tip);
    }

    void LoadLocalData()
    {
        try
        {
            preferences = ProfileStore.LoadPreferences();
            loadingPreferences = true;
            minimizeToTray.Checked = preferences.MinimizeToTray;
            killSwitch.Checked = preferences.KillSwitch;
            portableEngine.Checked = preferences.PortableEngine;
            ipv6Bindings.Checked = preferences.EnableIpv6Bindings;
            autoMtu.Checked = preferences.AutoMtu;
            autoAddLinkedApps.Checked = preferences.AutoAddLinkedApps;
            bypassLan.Checked = preferences.BypassLan;
            routeSort.SelectedIndex = Math.Clamp(preferences.RouteSortMetric, 0, routeSort.Items.Count - 1);
            windowPreset.SelectedIndex = Math.Clamp(preferences.WindowPreset, 0, WindowSizes.Length - 1);
            showTutorials.Checked = preferences.ShowTutorials;
            dnsOverride.Text = preferences.DnsOverride;
            routingMode.SelectedIndex = preferences.EntirePcMode ? 1 : 0;
            loadingPreferences = false;
            foreach (var pair in ProfileStore.LoadMeasurements()) measurements[pair.Key] = pair.Value;
            profiles = ProfileStore.Load();
            foreach (var path in ProfileStore.LoadApplications() ?? profiles.SelectMany(item => item.Applications).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                apps.Items.Add(path, true);
        }
        catch (Exception error) { WriteActivity("Could not load local settings. " + error.Message); }
        WriteActivity("RouteFinder is ready. Sign in or import a local profile.");
    }

    void LoadPreview()
    {
        serviceRoutes = [
            new("1", "DE", "Direct → Frankfurt", true),
            new("10", "DE", "Tehran → Frankfurt", false),
            new("1", "AM", "Direct → Yerevan", false),
            new("1", "NL", "Direct → Amsterdam", true),
            new("1", "SE", "Direct → Stockholm", false),
            new("3", "TR", "Direct → Istanbul", false)
        ];
        measurements["DE:1"] = new("192.0.2.10:3478", true, 42, 1);
        apps.Items.Add(@"C:\Games\Example\game.exe", true);
        apps.Items.Add(@"C:\Games\Example\launcher.exe", true);
        WriteActivity("Preview catalog loaded.");
    }

    void SetCatalog(BootstrapData catalog)
    {
        bool firstCatalog = serviceRoutes.Count == 0;
        serviceRoutes = catalog.Routes.DistinctBy(route => (route.RegionCode, route.Name))
            .SelectMany(route => route.HasIpv6 ? new[] { route, route with { OverIpv6 = true } } : new[] { route })
            .ToList();
        games.DataSource = catalog.Games.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToList();
        accountExpiry = null;
        accountUsage = accountServiceStatus = null;
        accountReferralCode = ReadReferralCode(catalog.Raw);
        if (catalog.Raw.TryGetProperty("account", out var accountData) &&
            accountData.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            accountReferralCode ??= ReadReferralCode(accountData);
            double? Number(string key) => accountData.TryGetProperty(key, out var value) &&
                value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 ? number : null;
            if (Number("remaining_seconds") is double seconds && seconds <= 315576000)
                accountExpiry = DateTimeOffset.UtcNow.AddSeconds(seconds);
            bool unlimited = accountData.TryGetProperty("is_unlimited", out var flag) &&
                flag.ValueKind == System.Text.Json.JsonValueKind.True;
            var used = Number("service_usage_gb");
            var cap = Number("data_cap_gb");
            accountUsage = unlimited ? (used.HasValue ? $"{used:0.##} GB used • Unlimited" : "Unlimited") :
                used.HasValue && cap.HasValue ? $"{used:0.##} / {cap:0.##} GB" :
                used.HasValue ? $"{used:0.##} GB used" : null;
            if (accountData.TryGetProperty("user_status", out var status) &&
                status.ValueKind == System.Text.Json.JsonValueKind.String)
                accountServiceStatus = status.GetString();
        }
        WriteActivity($"Loaded {serviceRoutes.Count} routes and {catalog.Games.Count} game entries.");
        var overview = new List<string>();
        var stats = catalog.Raw;
        if (stats.TryGetProperty("stats", out var nested) && nested.ValueKind == System.Text.Json.JsonValueKind.Object)
            stats = nested;
        AddStat("added_games_mounth", "Titles added this month");
        AddStat("total_add_games", "Titles supported");
        AddStat("total_connections", "Reported connections");
        AddStat("number_of_routes", "Reported routes");
        if (overview.Count == 0)
            overview.Add($"{catalog.Games.Count} catalog entries • {catalog.Routes.Count} routes loaded");
        overview.Add("");
        overview.Add(string.IsNullOrWhiteSpace(catalog.News) ? "There is no service news at the moment." : catalog.News.Trim());
        var newest = catalog.Games.Where(entry => entry.CatalogId.HasValue)
            .OrderByDescending(entry => entry.CatalogId).DistinctBy(entry => entry.CatalogId).Take(10).ToArray();
        if (newest.Length > 0)
        {
            overview.Add("");
            overview.Add("Latest added games and apps");
            overview.AddRange(newest.Select(entry => $"• {entry.DisplayName}"));
        }
        serviceOverview = string.Join(Environment.NewLine, overview);
        PopulateRoutes(restoreLastConnection: firstCatalog);

        void AddStat(string field, string caption)
        {
            if (stats.TryGetProperty(field, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                value.TryGetInt64(out long count) && count >= 0)
                overview.Add($"{caption}  {count:N0}");
        }
    }

    void ShowServiceOverview()
    {
        if (!connection.SignedIn)
        {
            ShowMessage("Sign in to view EZConnect news.");
            return;
        }
        using var dialog = Dialog("EZConnect service news", new Size(620, 390));
        var details = Input();
        details.Multiline = true;
        details.ReadOnly = true;
        details.ScrollBars = ScrollBars.Vertical;
        details.Dock = DockStyle.Fill;
        details.Text = serviceOverview;
        dialog.Controls.Add(details);
        dialog.ShowDialog(this);
    }

    static string? ReadReferralCode(System.Text.Json.JsonElement data)
    {
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !data.TryGetProperty("refrals_id", out var value)) return null;
        string? code = value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => value.GetString()?.Trim(),
            System.Text.Json.JsonValueKind.Number when value.TryGetInt64(out long number) && number > 0 => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrEmpty(code) || code.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : code;
    }

    void ShowAccountDetails()
    {
        if (!connection.SignedIn)
        {
            ShowMessage("Sign in to view EZConnect account details.");
            return;
        }
        string access = accountExpiry is { } expires ? expires.ToLocalTime().ToString("g") : "Unavailable";
        string summary = $"EZConnect account: {signedInEmail ?? "Signed in"}{Environment.NewLine}" +
            $"Service status: {accountServiceStatus ?? "Unavailable"}{Environment.NewLine}" +
            $"Access expires: {access}{Environment.NewLine}" +
            $"Traffic usage: {accountUsage ?? "Unavailable"}";
        if (accountReferralCode is not { } code)
        {
            ShowMessage(summary);
            return;
        }
        using var dialog = Dialog("EZConnect account", new Size(570, 250));
        var layout = Stack(0, 45);
        layout.Controls.Add(Label(summary), 0, 0);
        var actions = Actions();
        Add(actions, "Copy referral link", () =>
        {
            try
            {
                Clipboard.SetText("https://ezconnect.ir/users/register?id=" + Uri.EscapeDataString(code));
                MessageBox.Show(dialog, "Referral link copied.", "RouteFinder");
            }
            catch (Exception error) { MessageBox.Show(dialog, error.Message, "RouteFinder"); }
        });
        layout.Controls.Add(actions, 0, 1);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }

    readonly ComboBox routeCategory = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Route category" };
    readonly ComboBox routeSort = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145, AccessibleName = "Route sort metric" };

    async void ShowGameServers()
    {
        if (games.SelectedItem is not CatalogGame { Servers.Count: > 0 } game)
        {
            ShowMessage("The selected catalog entry has no published game servers.");
            return;
        }
        using var dialog = Dialog(game.Name, new Size(620, 430));
        var layout = Stack(36, 105, 44, 0);
        layout.Controls.Add(Label("Published game destinations", 12, true), 0, 0);
        var servers = new ListBox { Dock = DockStyle.Fill };
        servers.Items.AddRange(game.Servers.Cast<object>().ToArray());
        servers.SelectedIndex = 0;
        layout.Controls.Add(servers, 0, 1);
        var actions = Actions();
        var probe = Button("Ping over Windows route");
        actions.Controls.Add(probe);
        var routeProbe = Button("Ping through selected route");
        routeProbe.Enabled = !busy && !connection.HasTunnel && !connection.NeedsCleanup &&
            selected?.Route is { IsDownload: false };
        actions.Controls.Add(routeProbe);
        var compareDestination = Button("Compare routes");
        compareDestination.Enabled = !busy && !connection.HasTunnel && !connection.NeedsCleanup &&
            serviceRoutes.Any(route => !route.IsDownload);
        compareDestination.Click += (_, _) => dialog.DialogResult = DialogResult.Retry;
        actions.Controls.Add(compareDestination);
        layout.Controls.Add(actions, 0, 2);
        var results = Input();
        results.Multiline = true;
        results.ReadOnly = true;
        results.ScrollBars = ScrollBars.Vertical;
        results.Dock = DockStyle.Fill;
        layout.Controls.Add(results, 0, 3);
        probe.Click += async (_, _) =>
        {
            if (servers.SelectedItem is not GameServer server) return;
            bool canProbeRoute = routeProbe.Enabled;
            probe.Enabled = routeProbe.Enabled = servers.Enabled = false;
            try
            {
                var report = await Diagnostics.Probe(server.Address.ToString());
                if (!dialog.IsDisposed) results.Text = report;
            }
            finally
            {
                if (!dialog.IsDisposed)
                {
                    probe.Enabled = servers.Enabled = true;
                    routeProbe.Enabled = canProbeRoute;
                }
            }
        };
        routeProbe.Click += async (_, _) =>
        {
            if (selected?.Route is not { IsDownload: false } route ||
                servers.SelectedItem is not GameServer server || !EnsureAdministrator()) return;
            bool canCompare = compareDestination.Enabled;
            probe.Enabled = routeProbe.Enabled = servers.Enabled = compareDestination.Enabled = false;
            FormClosingEventHandler preventClose = (_, args) =>
            {
                args.Cancel = true;
                testCancellation?.Cancel();
            };
            dialog.FormClosing += preventClose;
            try
            {
                results.Text = "Probing… Close this window to cancel.";
                await RunTests("Probing the game destination…", async cancellation =>
                    results.Text = await connection.ProbeGameServerAsync(route, server, portableEngine.Checked, cancellation,
                        autoMtu: autoMtu.Checked, tunnelMtu: preferences.TunnelMtu,
                        keepaliveSeconds: preferences.KeepaliveSeconds, socksProxyOverride: ConfiguredProxy));
                if (results.Text == "Probing… Close this window to cancel.")
                    results.Text = "Probe stopped. See Activity for details.";
            }
            finally
            {
                dialog.FormClosing -= preventClose;
                probe.Enabled = servers.Enabled = true;
                routeProbe.Enabled = !connection.NeedsCleanup;
                compareDestination.Enabled = canCompare && !connection.NeedsCleanup;
            }
        };
        dialog.Controls.Add(layout);
        if (dialog.ShowDialog(this) == DialogResult.Retry && servers.SelectedItem is GameServer destination)
            await CompareRoutes(destination);
    }

    void SelectRecommendedGameRoute()
    {
        if (games.SelectedItem is not CatalogGame { IsApplication: false } game ||
            string.IsNullOrWhiteSpace(game.RecommendedRegion))
        {
            ShowMessage("Select a catalog game with a recommended region.");
            return;
        }
        if (!serviceRoutes.Any(route => !route.IsDownload &&
            route.RegionCode.Equals(game.RecommendedRegion, StringComparison.OrdinalIgnoreCase)))
        {
            ShowMessage($"{game.Name}'s recommended region ({game.RecommendedRegion}) has no available game routes.");
            return;
        }

        favoritesOnly.Checked = false;
        routeCategory.SelectedIndex = 1;
        search.Clear();
        var recommended = grid.Rows.Cast<DataGridViewRow>()
            .Where(row => row.Tag is Choice { Route: { IsDownload: false } route } &&
                route.RegionCode.Equals(game.RecommendedRegion, StringComparison.OrdinalIgnoreCase))
            .Select(row => (Row: row, Result: measurements.GetValueOrDefault(((Choice)row.Tag!).Key)))
            .OrderBy(item => item.Result is null ? 1 : item.Result.Succeeded ? 0 : 2)
            .ThenBy(item => item.Result?.Loss ?? float.PositiveInfinity)
            .ThenBy(item => item.Result?.Latency ?? int.MaxValue)
            .FirstOrDefault();
        if (recommended.Row is null) return;
        grid.CurrentCell = recommended.Row.Cells[0];
        Navigate(routePage);
    }

    static Choice ServiceChoice(ServiceRoute route)
    {
        string title = string.IsNullOrWhiteSpace(route.CityName) ? $"Route {route.Name}" : route.CityName;
        return new Choice($"{route.RegionCode}:{route.Name}" + (route.OverIpv6 ? ":ipv6" : ""),
            title + (route.OverIpv6 ? " (IPv6)" : ""), route.RegionCode, $"EZConnect • Route {route.Name}", route, null);
    }

    void PopulateRoutes(bool restoreLastConnection = false)
    {
        string? previous = selected?.Key;
        var choices = serviceRoutes.Select(ServiceChoice)
            .Concat(profiles.Select(profile => new Choice("local:" + profile.Name, profile.Name, "Local", "Saved WireGuard profile", null, profile)));
        var query = search.Text.Trim();
        var visible = choices.Where(route => $"{route.Title} {route.Region} {route.Source}".Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(choice => routeCategory.SelectedIndex switch
            {
                1 => choice.Route is { IsDownload: false },
                2 => choice.Route is { IsDownload: true },
                3 => choice.Profile is not null,
                _ => true
            })
            .Where(route => !favoritesOnly.Checked || preferences.Favorites.Contains(route.Key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(route => measurements.TryGetValue(route.Key, out var metric) && metric.Succeeded ? 0 : 1)
            .ThenBy(route =>
            {
                if (!measurements.TryGetValue(route.Key, out var metric) || !metric.Succeeded)
                    return double.PositiveInfinity;
                return routeSort.SelectedIndex switch
                {
                    1 => metric.Loss ?? float.PositiveInfinity,
                    2 => metric.Jitter ?? float.PositiveInfinity,
                    _ => 0
                };
            })
            .ThenBy(route => measurements.TryGetValue(route.Key, out var metric) && metric.Succeeded ? metric.Latency ?? int.MaxValue : int.MaxValue)
            .ThenBy(route => route.Region).ThenBy(route => route.Title, StringComparer.OrdinalIgnoreCase).ToList();
        populating = true;
        try
        {
            grid.Rows.Clear();
            grid.Columns[2].HeaderText = UiLanguage.Text(routeSort.SelectedIndex switch
            {
                1 => "Est. loss",
                2 => "Jitter",
                _ => "Last test"
            });
            foreach (var choice in visible)
            {
                string metricLabel = TestLabel(choice.Key);
                if (measurements.GetValueOrDefault(choice.Key) is { Succeeded: true } result)
                {
                    metricLabel = routeSort.SelectedIndex switch
                    {
                        1 => result.Loss is float loss ? $"{loss:0.#}%" : "—",
                        2 => result.Jitter is float jitter ? $"{jitter:0.#} ms" : "—",
                        _ => metricLabel
                    };
                }
                int index = grid.Rows.Add(choice.Title, choice.Region, metricLabel,
                    choice.Profile is null ? "EZConnect" : UiLanguage.Text("Config"),
                    preferences.Favorites.Contains(choice.Key, StringComparer.OrdinalIgnoreCase) ? "★" : "☆");
                grid.Rows[index].Tag = choice;
            }
            if (grid.Rows.Count > 0)
            {
                int index = restoreLastConnection
                    ? visible.FindIndex(route => route.Key == preferences.LastConnectedRoute)
                    : -1;
                if (index < 0) index = visible.FindIndex(route => route.Key == previous);
                grid.CurrentCell = grid.Rows[Math.Max(0, index)].Cells[0];
            }
        }
        finally { populating = false; }
        routeCount.Text = string.Format(UiLanguage.Text("{0} shown   /   {1} available"), visible.Count, serviceRoutes.Count + profiles.Count);
        grid.Visible = visible.Count > 0;
        emptyRoutes.Visible = visible.Count == 0;
        emptyRoutes.Text = UiLanguage.Text(query.Length > 0 ? "No routes match your search."
            : connection.SignedIn ? "No routes are available. Import a profile or refresh."
            : "Sign in to load your EZConnect routes, or import a local profile.");
        SelectRow();
    }

    void ToggleFavorite()
    {
        if (selected == null) return;
        if (preferences.Favorites.Contains(selected.Key, StringComparer.OrdinalIgnoreCase))
            preferences.Favorites.RemoveAll(key => key.Equals(selected.Key, StringComparison.OrdinalIgnoreCase));
        else preferences.Favorites.Add(selected.Key);
        SavePreferences();
        PopulateRoutes();
    }

    void ApplyWindowPreset()
    {
        var requested = WindowSizes[Math.Max(0, windowPreset.SelectedIndex)];
        var area = Screen.FromControl(this).WorkingArea;
        WindowState = FormWindowState.Normal;
        Size = new Size(Math.Max(MinimumSize.Width, Math.Min(requested.Width, area.Width)),
            Math.Max(MinimumSize.Height, Math.Min(requested.Height, area.Height)));
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    void EditExcludedApplications()
    {
        if (busy || connection.HasTunnel) return;
        using var dialog = Dialog("Entire PC app exclusions", new Size(540, 310));
        var layout = Stack(38, 0, 44);
        var excluded = preferences.ExcludedApplications ?? [];
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        foreach (var path in apps.Items.Cast<string>().Concat(excluded).Distinct(StringComparer.OrdinalIgnoreCase))
            list.Items.Add(path, excluded.Contains(path, StringComparer.OrdinalIgnoreCase));
        layout.Controls.Add(Label("Check apps to exclude. The kill switch may block excluded apps.", 10), 0, 0);
        layout.Controls.Add(list, 0, 1);
        var actions = Actions();
        Add(actions, "Browse executables", () =>
        {
            using var picker = new OpenFileDialog
            {
                Filter = "Windows executables (*.exe)|*.exe",
                Multiselect = true,
                CheckFileExists = true
            };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            foreach (var path in picker.FileNames)
            {
                if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.Contains('|')) continue;
                int index = list.Items.Cast<string>().ToList()
                    .FindIndex(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
                if (index < 0) list.Items.Add(path, true);
                else list.SetItemChecked(index, true);
            }
        });
        Add(actions, "Save", () =>
        {
            preferences = preferences with { ExcludedApplications = list.CheckedItems.Cast<string>().ToArray() };
            SavePreferences();
            UpdateControls();
            dialog.DialogResult = DialogResult.OK;
        });
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }

    ProxyOverride? ConfiguredProxy => preferences.SocksProxyCredentials ??
        (string.IsNullOrWhiteSpace(preferences.SocksProxyOverride) ? null :
            new ProxyOverride(preferences.SocksProxyOverride, "", ""));

    void EditSocksProxy()
    {
        if (busy || connection.HasTunnel) return;
        using var dialog = Dialog("SOCKS5 proxy override", new Size(515, 272));
        var layout = Stack(46, 38, 38, 38, 34, 44);
        var hint = Label("Enter host:port and optional credentials. Clear the endpoint to use the profile settings.", 10);
        var input = Input("SOCKS5 proxy endpoint");
        input.Text = ConfiguredProxy?.Endpoint ?? "";
        var username = Input("Proxy username");
        username.Text = ConfiguredProxy?.Username ?? "";
        var password = Input("Proxy password");
        password.UseSystemPasswordChar = true;
        password.Text = ConfiguredProxy?.Password ?? "";
        var allTraffic = new CheckBox
        {
            Text = "Use WireSock's all-traffic SOCKS5 mode",
            AutoSize = true,
            Checked = ConfiguredProxy?.AllTraffic == true
        };
        var actions = Actions();
        Add(actions, "Save", () =>
        {
            string endpoint = input.Text.Trim();
            try { if (endpoint.Length > 0) TunnelProfile.SplitEndpoint(endpoint); }
            catch (FormatException)
            {
                hint.Text = "Enter host:port or [IPv6]:port, or leave it blank.";
                return;
            }
            if (endpoint.Length > 0 && ((username.Text.Length == 0) != (password.Text.Length == 0) ||
                System.Text.Encoding.UTF8.GetByteCount(username.Text) > 255 ||
                System.Text.Encoding.UTF8.GetByteCount(password.Text) > 255 ||
                username.Text.Contains('\0') || password.Text.Contains('\0')))
            {
                hint.Text = "Enter both credentials, up to 255 bytes each, or leave both blank.";
                return;
            }
            preferences = preferences with
            {
                SocksProxyOverride = "",
                SocksProxyCredentials = endpoint.Length == 0 ? null :
                    new ProxyOverride(endpoint, username.Text, password.Text, allTraffic.Checked)
            };
            SavePreferences();
            dialog.DialogResult = DialogResult.OK;
        });
        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(input, 0, 1);
        layout.Controls.Add(username, 0, 2);
        layout.Controls.Add(password, 0, 3);
        layout.Controls.Add(allTraffic, 0, 4);
        layout.Controls.Add(actions, 0, 5);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }

    void EditKeepalive()
    {
        if (busy || connection.HasTunnel) return;
        using var dialog = Dialog("WireGuard tunnel overrides", new Size(520, 395));
        var layout = Stack(42, 38, 42, 38, 42, 38, 42, 38, 44);
        var testWait = new NumericUpDown
        {
            Minimum = 5, Maximum = 120, Value = Math.Clamp(preferences.RouteTestSeconds, 5, 120),
            AccessibleName = "Standard route test time limit in seconds"
        };
        var peerWait = new NumericUpDown
        {
            Minimum = 5, Maximum = 180, Value = Math.Clamp(preferences.PeerResponseSeconds, 5, 180),
            AccessibleName = "Peer response wait in seconds"
        };
        var hint = Label("Leave blank for the route default. Enter 0 to disable, or seconds up to 65535.", 10);
        var input = Input();
        input.Text = preferences.KeepaliveSeconds?.ToString() ?? "";
        var mtuHint = Label("MTU (576–65535). Leave blank to use the profile or Auto MTU.", 10);
        var mtuInput = Input("MTU override");
        mtuInput.Text = preferences.TunnelMtu?.ToString() ?? "";
        var actions = Actions();
        Add(actions, "Save", () =>
        {
            string value = input.Text.Trim();
            int seconds = 0;
            if (value.Length > 0 && (!int.TryParse(value, out seconds) || seconds is < 0 or > 65535))
            {
                hint.Text = "Enter a whole number from 0 to 65535, or leave it blank.";
                return;
            }
            string mtuValue = mtuInput.Text.Trim();
            int mtu = 0;
            if (mtuValue.Length > 0 && (!int.TryParse(mtuValue, out mtu) || mtu is < 576 or > 65535))
            {
                mtuHint.Text = "Enter an MTU between 576 and 65535, or leave it blank.";
                return;
            }
            preferences = preferences with
            {
                KeepaliveSeconds = value.Length == 0 ? null : seconds,
                TunnelMtu = mtuValue.Length == 0 ? null : mtu,
                PeerResponseSeconds = (int)peerWait.Value,
                RouteTestSeconds = (int)testWait.Value
            };
            SavePreferences();
            dialog.DialogResult = DialogResult.OK;
        });
        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(input, 0, 1);
        layout.Controls.Add(mtuHint, 0, 2);
        layout.Controls.Add(mtuInput, 0, 3);
        layout.Controls.Add(Label("Seconds before showing No peer response", 10), 0, 4);
        layout.Controls.Add(peerWait, 0, 5);
        layout.Controls.Add(Label("Standard route test time limit (seconds)", 10), 0, 6);
        layout.Controls.Add(testWait, 0, 7);
        layout.Controls.Add(actions, 0, 8);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }

    void EditBypassDestinations()
    {
        if (busy || connection.HasTunnel) return;
        using var dialog = Dialog("Destination bypass", new Size(520, 210));
        var layout = Stack(40, 0, 44);
        var hint = Label("Enter IP addresses, CIDR ranges, or hostnames, one per line.", 10);
        var input = Input();
        input.Multiline = true;
        input.Dock = DockStyle.Fill;
        input.Text = (preferences.BypassDestinations ?? "").Replace(",", Environment.NewLine);
        var actions = Actions();
        Add(actions, "Save", () =>
        {
            var values = input.Text.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (values.Any(value => !System.Net.IPAddress.TryParse(value, out _) &&
                !System.Net.IPNetwork.TryParse(value, out _) &&
                Uri.CheckHostName(value) != UriHostNameType.Dns))
            {
                hint.Text = "Use an IP address, a CIDR range, or a hostname such as example.com.";
                return;
            }
            preferences = preferences with { BypassDestinations = string.Join(",", values.Distinct()) };
            SavePreferences();
            dialog.DialogResult = DialogResult.OK;
        });
        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(input, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }

    void SavePreferences()
    {
        if (preview || loadingPreferences) return;
        preferences = preferences with
        {
            MinimizeToTray = minimizeToTray.Checked,
            EntirePcMode = routingMode.SelectedIndex == 1,
            KillSwitch = killSwitch.Checked,
            PortableEngine = portableEngine.Checked,
            DnsOverride = dnsOverride.Text.Trim(),
            EnableIpv6Bindings = ipv6Bindings.Checked,
            AutoMtu = autoMtu.Checked,
            AutoAddLinkedApps = autoAddLinkedApps.Checked,
            WindowPreset = Math.Max(0, windowPreset.SelectedIndex),
            ShowTutorials = showTutorials.Checked,
            BypassLan = bypassLan.Checked,
            RouteSortMetric = Math.Max(0, routeSort.SelectedIndex)
        };
        try { ProfileStore.SavePreferences(preferences); }
        catch (Exception error) { WriteActivity("Could not save preferences. " + error.Message); }
    }

    async Task ApplyIpv6SettingFromUi()
    {
        if (loadingPreferences || preview) return;
        bool desired = ipv6Bindings.Checked;
        bool previous = preferences.EnableIpv6Bindings;
        if (!EnsureAdministrator())
        {
            loadingPreferences = true; ipv6Bindings.Checked = previous; loadingPreferences = false;
            return;
        }
        ipv6Bindings.Enabled = false;
        try
        {
            await ApplyIpv6Bindings(desired);
            preferences = preferences with { Ipv6PreferenceConfigured = true, EnableIpv6Bindings = desired };
            SavePreferences();
            WriteActivity($"IPv6 bindings {(desired ? "enabled" : "disabled")} on active network adapters.");
        }
        catch (Exception error)
        {
            loadingPreferences = true; ipv6Bindings.Checked = previous; loadingPreferences = false;
            ShowMessage("Could not update IPv6 on network adapters. " + error.Message);
        }
        finally { ipv6Bindings.Enabled = !busy && !connection.HasTunnel; }
    }

    void SaveMeasurements()
    {
        if (preview) return;
        try { ProfileStore.SaveMeasurements(measurements); }
        catch (Exception error) { WriteActivity("Could not save route-test results. " + error.Message); }
    }

    void SelectRow()
    {
        selected = grid.CurrentRow?.Tag as Choice;
        routeTitle.Text = selected?.Title ?? UiLanguage.Text("No route selected");
        routeSubtitle.Text = selected?.Source ?? UiLanguage.Text("Sign in for EZConnect routes or import a profile.");
        RouteTestResult? result = null;
        if (selected != null) measurements.TryGetValue(selected.Key, out result);
        string? endpoint = selected?.Key == connected?.Key ? connectedEndpoint : result?.Endpoint;
        try { endpoint ??= selected?.Local()?.Endpoint; }
        catch (FormatException) { }
        routeEndpoint.Text = endpoint == null ? UiLanguage.Text("Endpoint available after allocation.") : $"{UiLanguage.Text("Endpoint")}  {endpoint}";
        routeHealth.Text = selected == null ? "—" : TestLabel(selected.Key);
        routeHealth.ForeColor = result == null ? Muted : result.Succeeded ? Accent : Warning;
        routeHealthDetail.Text = result == null ? UiLanguage.Text("Test for a WireGuard response.") :
            result.ProfileUnavailable ? "EZConnect did not provide a usable tunnel profile." :
            result.Succeeded ? TestMetrics(result) : string.Format(UiLanguage.Text("No handshake after {0} traffic probe(s)."), result.Probes);
        if (result?.TcpReached is bool tcpReached)
            routeHealthDetail.Text += Environment.NewLine + TcpProbeStatus(tcpReached);
        if (result?.TestedAt is { } testedAt)
            routeHealthDetail.Text += Environment.NewLine + $"Tested {testedAt.ToLocalTime():g}";
        if (result?.ProbeTiming is { Length: > 0 } timing)
            routeHealthDetail.Text += Environment.NewLine + timing;
        favorite.Text = selected != null && preferences.Favorites.Contains(selected.Key, StringComparer.OrdinalIgnoreCase) ? "★ Favorited" : "☆ Favorite";
        UpdateControls();
    }

    static string TcpProbeStatus(bool reached) => reached
        ? "TCP probe reached port 443. Probe path unverified."
        : "TCP probe did not connect to port 443.";

    static string TestMetrics(RouteTestResult result) =>
        $"{UiLanguage.Text("Est. loss")}: {result.Loss?.ToString("0.#") ?? "—"}{(result.Loss.HasValue ? "%" : "")}   •   {UiLanguage.Text("Jitter")}: {result.Jitter?.ToString("0.#") ?? "—"}{(result.Jitter.HasValue ? " ms" : "")}   •   {UiLanguage.Text("RTT spread")}: {result.RttSpread?.ToString() ?? "—"}{(result.RttSpread.HasValue ? " ms" : "")}";

    string TestLabel(string key) => measurements.TryGetValue(key, out var result)
        ? result.ProfileUnavailable ? "Unavailable" :
            result.Succeeded ? result.Latency is { } latency ? $"{latency} ms" : UiLanguage.Text("Responded") : UiLanguage.Text("No response")
        : UiLanguage.Text("Not tested");

    async Task ToggleSignIn()
    {
        if (busy) return;
        if (connection.SignedIn)
        {
            await Run("Signing out…", async () =>
            {
                try { await connection.SignOutAsync(); }
                finally
                {
                    if (!connection.SignedIn)
                    {
                        if (armedRoute is not null) ToggleOnDemandConnection();
                        ClearConnectionDisplay();
                        rememberCurrentSignIn = false;
                        ProfileStore.ClearLogin();
                        signedInEmail = null;
                        serviceRoutes = [];
                        accountExpiry = null;
                        accountUsage = accountServiceStatus = null;
                        accountReferralCode = null;
                        serviceOverview = "";
                        games.DataSource = null;
                        foreach (var key in measurements.Keys.Where(key => !key.StartsWith("local:", StringComparison.Ordinal)).ToArray())
                            measurements.Remove(key);
                        PopulateRoutes();
                    }
                }
            });
            return;
        }
        using var dialog = new SignInDialog(async (email, password) =>
        {
            SetCatalog(await connection.SignInAsync(email, password));
            signedInEmail = email;
        });
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            rememberCurrentSignIn = dialog.Remember();
            // Store only the refresh token, protected for this Windows account. Never retain the password.
            try
            {
                if (rememberCurrentSignIn && connection.RefreshToken is { } refreshToken) ProfileStore.SaveLogin(dialog.Email(), refreshToken);
                else ProfileStore.ClearLogin();
            }
            catch (Exception error) { WriteActivity("Signed in, but the remembered session could not be saved. " + error.Message); }
            WriteActivity("Signed in to EZConnect.");
        }
        UpdateControls();
    }

    async Task RestoreRememberedLogin(bool interactive = false)
    {
        if (preview || busy || connection.SignedIn) return;
        ProfileStore.RememberedLogin? saved;
        try { saved = ProfileStore.LoadLogin(); }
        catch (Exception error) { WriteActivity("Could not read remembered sign-in. " + error.Message); return; }
        if (saved == null)
        {
            if (interactive) ShowMessage("No remembered EZConnect sign-in was found.");
            return;
        }
        await Run("Restoring sign-in…", async () =>
        {
            bool refreshed = false;
            try
            {
                SetCatalog(await connection.RestoreAsync(saved.RefreshToken, token =>
                {
                    refreshed = true;
                    try { ProfileStore.SaveLogin(saved.Email, token); }
                    catch (Exception error) { WriteActivity("Could not save the refreshed sign-in. " + error.Message); }
                }));
                signedInEmail = saved.Email;
                rememberCurrentSignIn = true;
                WriteActivity("Remembered sign-in restored and route catalog refreshed.");
            }
            catch (EzConnectServiceException error) when (!refreshed && error.StatusCode is 401 or 403)
            {
                ProfileStore.ClearLogin();
                throw;
            }
        });
    }

    void EditOnDemandTimeout()
    {
        if (busy) return;
        using var dialog = Dialog("On-demand idle timeout", new Size(420, 155));
        var layout = Stack(42, 38, 42);
        var seconds = new NumericUpDown
        {
            Minimum = 5,
            Maximum = 3600,
            Value = Math.Clamp(preferences.OnDemandIdleSeconds, 5, 3600),
            AccessibleName = "Idle timeout in seconds"
        };
        layout.Controls.Add(Label("Seconds to wait after all watched apps exit", 10), 0, 0);
        layout.Controls.Add(seconds, 0, 1);
        var actions = Actions();
        Add(actions, "Save", () =>
        {
            preferences = preferences with { OnDemandIdleSeconds = (int)seconds.Value };
            appsIdleSince = null;
            SavePreferences();
            dialog.DialogResult = DialogResult.OK;
        });
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }

    void ToggleOnDemandConnection()
    {
        if (armedRoute is not null)
        {
            armedRoute = null;
            armedApps = [];
            onDemandOwnsTunnel = false;
            holdOnDemandTunnel = false;
            appsIdleSince = null;
            WriteActivity("On-demand connection canceled.");
            return;
        }
        if (busy || connection.HasTunnel || connection.NeedsCleanup) return;
        if (selected is null || routingMode.SelectedIndex != 0 || apps.CheckedItems.Count == 0)
        {
            ShowMessage("Choose a route and check at least one executable in Selected apps mode.");
            return;
        }
        if (!EnsureAdministrator()) return;
        armedRoute = selected.Key;
        armedApps = apps.CheckedItems.Cast<string>().ToArray();
        lastAppCheck = null;
        tray.Visible = true;
        WriteActivity($"Watching {armedApps.Length} selected app(s) for {selected.Title}.");
    }

    void UpdateTrayStatus()
    {
        tray.Text = armedRoute is null ? "RouteFinder" :
            holdOnDemandTunnel ? "RouteFinder · On-demand tunnel held open" :
            appsIdleSince is { } idle && onDemandOwnsTunnel
                ? $"RouteFinder · Idle disconnect in {Math.Max(0, Math.Ceiling(Math.Clamp(preferences.OnDemandIdleSeconds, 5, 3600) - Stopwatch.GetElapsedTime(idle).TotalSeconds))} s"
                : onDemandOwnsTunnel ? "RouteFinder · On-demand tunnel active"
                : $"RouteFinder · Watching {armedApps.Length} app(s)";
    }

    async Task CheckOnDemandConnection()
    {
        if (busy || armedRoute is null ||
            lastAppCheck is long previous && Stopwatch.GetElapsedTime(previous) < TimeSpan.FromSeconds(3)) return;
        lastAppCheck = Stopwatch.GetTimestamp();
        if ((!onDemandOwnsTunnel && (connection.HasTunnel || connection.NeedsCleanup)) || selected?.Key != armedRoute ||
            routingMode.SelectedIndex != 0 ||
            !armedApps.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(apps.CheckedItems.Cast<string>()))
        {
            ToggleOnDemandConnection();
            return;
        }
        bool processIdentityUnknown = false;
        foreach (var app in armedApps)
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app));
            bool matched = false;
            foreach (var process in processes)
            {
                using (process)
                {
                    try { matched |= string.Equals(process.MainModule?.FileName, app, StringComparison.OrdinalIgnoreCase); }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        processIdentityUnknown = true;
                    }
                    catch (InvalidOperationException)
                    {
                        // A process can exit between enumeration and reading its executable path.
                    }
                }
            }
            if (!matched) continue;
            appsIdleSince = null;
            if (onDemandOwnsTunnel) return;
            WriteActivity($"Detected {Path.GetFileName(app)}. Starting the selected route.");
            await ToggleConnection();
            onDemandOwnsTunnel = connection.HasTunnel;
            if (!onDemandOwnsTunnel && armedRoute is not null) ToggleOnDemandConnection();
            return;
        }
        if (processIdentityUnknown)
        {
            appsIdleSince = null;
            return;
        }
        if (!onDemandOwnsTunnel || holdOnDemandTunnel) return;
        appsIdleSince ??= Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(appsIdleSince.Value) <
            TimeSpan.FromSeconds(Math.Clamp(preferences.OnDemandIdleSeconds, 5, 3600))) return;
        await Run("Releasing idle tunnel…", async () =>
        {
            try
            {
                await connection.DisconnectAsync();
                WriteActivity("Watched apps exited. Waiting for the next launch.");
            }
            finally
            {
                onDemandOwnsTunnel = false;
                appsIdleSince = null;
                ClearConnectionDisplay();
                if (connection.NeedsCleanup && armedRoute is not null) ToggleOnDemandConnection();
            }
        });
    }

    void ClearConnectionDisplay()
    {
        restartConnection = null;
        connected = null;
        connectedEntirePc = false;
        connectedConsole = false;
        connectedEndpoint = null;
        connectionStartedAt = null;
        SelectRow();
    }

    async Task Reconnect(bool automatic = false)
    {
        if (busy || restartConnection is not { } restart) return;
        if (!automatic) recoveryAttempted = false;
        await Run("Reconnecting…", async () =>
        {
            try { await connection.DisconnectAsync(); }
            finally
            {
                ClearConnectionDisplay();
                restartConnection = restart;
            }
            await restart();
        });
    }

    async Task ToggleConnection(bool consoleMode = false, string[]? savedApps = null)
    {
        if (busy) return;
        if (connection.HasTunnel || connection.NeedsCleanup)
        {
            if (armedRoute is not null) ToggleOnDemandConnection();
            await Run("Disconnecting…", async () =>
            {
                try { await connection.DisconnectAsync(); WriteActivity("Disconnected and released the route."); }
                finally
                {
                    ClearConnectionDisplay();
                }
            });
            return;
        }
        var choice = selected;
        bool entirePc = consoleMode || (savedApps is null && routingMode.SelectedIndex == 1);
        var paths = entirePc ? Array.Empty<string>() : savedApps ?? apps.CheckedItems.Cast<string>().ToArray();
        if (choice == null || (!entirePc && paths.Length == 0) || !EnsureAdministrator()) return;
        if (paths.Any(path => !File.Exists(path)))
        {
            ShowMessage(savedApps is null ? "Remove or uncheck application files that no longer exist." :
                "Some saved executables no longer exist. Load the profile apps, update the selection, and save it again.");
            Navigate(appsPage);
            return;
        }
        var game = savedApps is null ? games.SelectedItem as CatalogGame : null;
        var expected = game is null ? [] : CatalogExecutables(game);
        bool matchesCatalog = !entirePc && expected.Any(name => MatchesExecutable(paths, name));
        if (matchesCatalog && game is not null)
        {
            var conflicts = CatalogEntries(game.IncompatibleAppNames)
                .Where(entry => entry.ExecutableNames.Any(name => MatchesExecutable(paths, name)))
                .Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (conflicts.Length > 0)
            {
                ShowMessage($"{game.Name} is incompatible with these checked catalog apps: {string.Join(", ", conflicts)}.");
                Navigate(appsPage);
                return;
            }
        }
        var settings = (Lock: killSwitch.Checked, Portable: portableEngine.Checked,
            Dns: dnsOverride.Text, AutoMtu: autoMtu.Checked, BypassLan: bypassLan.Checked);
        var keepaliveSeconds = preferences.KeepaliveSeconds;
        var tunnelMtu = preferences.TunnelMtu;
        var socksProxyOverride = ConfiguredProxy;
        var excludedApplications = entirePc && !consoleMode ? preferences.ExcludedApplications : null;
        var customBypass = (preferences.BypassDestinations ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        async Task StartSelectedRoute()
        {
            var bypass = choice.Route is { IsDownload: false } route && !entirePc &&
                game is { IsApplication: false } &&
                paths.All(path => expected.Any(name =>
                    string.Equals(Path.GetFileName(path), Path.GetFileName(name), StringComparison.OrdinalIgnoreCase)))
                ? EzConnectClient.GetGameBypass(game, route.RegionCode) : (Array.Empty<string>(), Array.Empty<string>());
            var customHosts = customBypass.Where(value => Uri.CheckHostName(value) == UriHostNameType.Dns).ToArray();
            connectedEndpoint = await connection.ConnectAsync(choice.Route, choice.Local(), paths, entirePc,
                settings.Lock, settings.Portable, settings.Dns, settings.AutoMtu,
                bypass.Item1.Concat(customBypass.Except(customHosts)).ToArray(),
                bypass.Item2.Concat(customHosts).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                consoleMode, settings.BypassLan, keepaliveSeconds, excludedApplications, tunnelMtu, socksProxyOverride);
            restartConnection = StartSelectedRoute;
            connected = choice;
            nativeTunnelStopped = false;
            preferences = preferences with { LastConnectedRoute = choice.Key };
            SavePreferences();
            connectedEntirePc = entirePc;
            connectedConsole = consoleMode;
            connectionStartedAt = DateTimeOffset.UtcNow;
            peerTimeoutLogged = false;
            WriteActivity(consoleMode ? $"Console tunnel started on {choice.Title}." :
                entirePc ? $"Entire PC tunnel started on {choice.Title}." : $"Tunnel started for {paths.Length} app(s) on {choice.Title}.");
            tray.Visible = true;
            SelectRow();
        }
        restartConnection = StartSelectedRoute;
        recoveryAttempted = false;
        tray.Visible = true;
        await Run($"Connecting to {choice.Title}…", StartSelectedRoute);
    }

    async Task TestRoute()
    {
        var choice = selected;
        if (busy || choice == null || connection.HasTunnel || !EnsureAdministrator()) return;
        await RunTests($"Testing {choice.Title}…", async cancellation =>
        {
            var result = await connection.TestAsync(choice.Route, choice.Local(), cancellation,
                portableEngine.Checked, dnsOverride.Text, autoMtu.Checked,
                preferences.TunnelMtu, preferences.KeepaliveSeconds, preferences.RouteTestSeconds, ConfiguredProxy);
            measurements[choice.Key] = result with { TestedAt = DateTimeOffset.UtcNow };
            SaveMeasurements();
            WriteActivity($"{choice.Title}: {TestLabel(choice.Key)} after {result.Probes} traffic probe(s).");
            PopulateRoutes();
        });
    }

    async Task TestCustomDestination()
    {
        if (busy || connection.HasTunnel || connection.NeedsCleanup) return;
        if (profiles.Count == 0 && !serviceRoutes.Any(route => !route.IsDownload))
        {
            ShowMessage("Import a profile or sign in to load routes before testing a destination.");
            return;
        }
        using var dialog = Dialog("Test custom destination", new Size(490, 155));
        var layout = Stack(36, 40, 44);
        var hint = Label("Enter a hostname or IP address to test through your routes.", 10);
        var input = Input("Hostname or IP address");
        var actions = Actions();
        var next = Button("Choose routes", true);
        System.Net.IPAddress? destination = null;
        string? hostname = null;
        static bool IsProbeAddress(System.Net.IPAddress address) =>
            !System.Net.IPAddress.IsLoopback(address) &&
            (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? address.GetAddressBytes()[0] is > 0 and < 224
                : !address.IsIPv6Multicast && !address.IsIPv6LinkLocal && !address.IsIPv4MappedToIPv6 &&
                  !address.Equals(System.Net.IPAddress.IPv6Any) && address.ScopeId == 0);
        next.Click += (_, _) =>
        {
            string value = input.Text.Trim();
            if (System.Net.IPAddress.TryParse(value, out destination) ? !IsProbeAddress(destination) :
                Uri.CheckHostName(value) != UriHostNameType.Dns)
            {
                hint.Text = "Enter a hostname or a usable unicast IP address.";
                return;
            }
            hostname = destination is null ? value : null;
            dialog.DialogResult = DialogResult.OK;
        };
        actions.Controls.Add(next);
        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(input, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = next;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (hostname is { } host)
        {
            await Run("Resolving destination…", async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var addresses = await System.Net.Dns.GetHostAddressesAsync(host, deadline.Token);
                destination = addresses.Where(IsProbeAddress)
                    .OrderBy(address => address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    .FirstOrDefault() ?? throw new InvalidOperationException("The hostname has no usable IP address.");
                WriteActivity($"{host} resolved to {destination} for this comparison.");
            });
        }
        if (destination is not null)
            await CompareRoutes(new GameServer("", "", destination));
    }

    async Task CompareRoutes(GameServer? destination = null, string? region = null, bool visibleOnly = false)
    {
        if (busy || connection.HasTunnel || connection.NeedsCleanup || !EnsureAdministrator()) return;
        using var dialog = Dialog(destination is null ? "Compare routes" : $"Compare routes to {destination.Address}", new Size(620, 470));
        var layout = Stack(42, 0, destination is null ? 46 : 118);
        var hint = Label("Choose routes or test all shown. Tests run one at a time.", 11, true);
        layout.Controls.Add(hint, 0, 0);
        var choices = serviceRoutes.Select(ServiceChoice)
            .Concat(profiles.Select(profile => new Choice("local:" + profile.Name, profile.Name, "Local", "Saved WireGuard profile", null, profile))).ToList();
        if (visibleOnly)
        {
            choices = grid.Rows.Cast<DataGridViewRow>().Where(row => row.Visible)
                .Select(row => row.Tag).OfType<Choice>().ToList();
            if (choices.Count == 0)
            {
                ShowMessage("No routes match the current library filters.");
                return;
            }
            hint.Text = "Choose from the routes shown in the library, or test all shown.";
        }
        if (destination is not null) choices.RemoveAll(choice => choice.Route is { IsDownload: true });
        if (region is not null)
        {
            choices.RemoveAll(choice => choice.Route is not { IsDownload: false } route ||
                !string.Equals(route.RegionCode, region, StringComparison.OrdinalIgnoreCase));
            if (choices.Count == 0)
            {
                ShowMessage($"No game routes are available in {region}.");
                return;
            }
            hint.Text = $"Choose game routes in {region}, or test all shown.";
        }
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, BackColor = Card, ForeColor = Ink };
        foreach (var choice in choices) list.Items.Add($"{choice.Region}   {choice.Title}   ·   {choice.Source}", choice.Key == selected?.Key);
        layout.Controls.Add(list, 0, 1);
        var actions = Actions();
        Add(actions, "Run comparison", () =>
        {
            if (list.CheckedItems.Count < 1)
            {
                hint.Text = "Select at least one route.";
                return;
            }
            dialog.DialogResult = DialogResult.OK;
        });
        Add(actions, "Test all shown", () =>
        {
            for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true);
            dialog.DialogResult = DialogResult.OK;
        });
        var probeCount = new NumericUpDown
        {
            Minimum = 4, Maximum = 30, Value = 4, Width = 62,
            AccessibleName = "Pings per route"
        };
        using var probeInterval = new NumericUpDown
        {
            Minimum = 0, Maximum = 5000, Value = 1000, Increment = 100, Width = 72,
            AccessibleName = "Pause between pings in milliseconds"
        };
        using var probeSize = new NumericUpDown
        {
            Minimum = 0, Maximum = 1472, Value = 32, Width = 62, AccessibleName = "Ping payload bytes"
        };
        using var probeTimeout = new NumericUpDown
        {
            Minimum = 100, Maximum = 5000, Value = 1500, Increment = 100, Width = 72,
            AccessibleName = "Ping timeout in milliseconds"
        };
        using var probeTtl = new NumericUpDown
        {
            Minimum = 1, Maximum = 255, Value = 128, Width = 62, AccessibleName = "Outgoing ping TTL"
        };
        using var tcpPort = new NumericUpDown
        {
            Minimum = 0, Maximum = 65535, Width = 72, AccessibleName = "TCP port or zero for ping"
        };
        if (destination is not null)
        {
            actions.Controls.Add(Label("Pings per route", 9));
            actions.Controls.Add(probeCount);
            actions.Controls.Add(Label("TTL", 9));
            actions.Controls.Add(probeTtl);
            actions.SetFlowBreak(probeTtl, true);
            actions.Controls.Add(Label("Pause between pings (ms)", 9));
            actions.Controls.Add(probeInterval);
            actions.Controls.Add(Label("Bytes", 9));
            actions.Controls.Add(probeSize);
            actions.Controls.Add(Label("Timeout (ms)", 9));
            actions.Controls.Add(probeTimeout);
            actions.SetFlowBreak(probeTimeout, true);
            actions.Controls.Add(Label("TCP port (0 for ping)", 9));
            actions.Controls.Add(tcpPort);
            tcpPort.ValueChanged += (_, _) =>
            {
                bool isPing = tcpPort.Value == 0;
                probeCount.Enabled = isPing;
                probeInterval.Enabled = isPing;
                probeSize.Enabled = isPing;
                probeTtl.Enabled = isPing;
                hint.Text = isPing
                    ? "Choose routes to compare with ICMP ping."
                    : "Choose routes to compare with one TCP connection attempt each.";
            };
        }
        else probeCount.Dispose();
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var chosen = list.CheckedIndices.Cast<int>().Select(index => choices[index]).ToList();
        int pingCount = destination is null ? 4 : (int)probeCount.Value;
        int pingInterval = (int)probeInterval.Value;
        int? comparisonPort = tcpPort.Value == 0 ? null : (int)tcpPort.Value;
        if (destination is not null) Navigate(routePage);
        var summaries = new List<string>();
        var probeDetails = new List<string>();
        int completedRoutes = 0;
        await RunTests("Comparing routes…", async cancellation =>
        {
            PingReport? baseline = null;
            if (destination is not null)
            {
                traffic.Text = $"Measuring Windows route to {destination.Address}…";
                baseline = comparisonPort is int port
                    ? await MeasureTcpBaselineAsync(port)
                    : await Diagnostics.MeasurePing(destination.Address.ToString(), pingCount,
                    cancellation: cancellation, intervalMilliseconds: pingInterval,
                    payloadBytes: (int)probeSize.Value, timeoutMilliseconds: (int)probeTimeout.Value,
                    ttl: (int)probeTtl.Value);
                summaries.Add($"Windows baseline · {(comparisonPort.HasValue ? baseline.Detail : baseline.Summary)}");
                probeDetails.Add($"Windows baseline{Environment.NewLine}{baseline.Detail}");
                WriteActivity($"Windows baseline • {destination.Address}{Environment.NewLine}{baseline.Detail}");

                async Task<PingReport> MeasureTcpBaselineAsync(int targetPort)
                {
                    var tcp = await Diagnostics.MeasureTcp(destination.Address.ToString(), targetPort,
                        cancellation, (int)probeTimeout.Value);
                    return new PingReport(tcp.Detail, Tcp: tcp);
                }
            }
            int unavailable = 0;
            int lastRetriedIndex = -1;
            long? lastServiceAttemptFinished = null;
            for (int i = 0; i < chosen.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var choice = chosen[i];
                traffic.Text = $"{i + 1} / {chosen.Count}   {choice.Title}";
                try
                {
                    if (choice.Route is not null && lastServiceAttemptFinished is long previousAttempt)
                    {
                        var remainingCooldown = TimeSpan.FromSeconds(10) - Stopwatch.GetElapsedTime(previousAttempt);
                        if (remainingCooldown > TimeSpan.Zero)
                            await Task.Delay(remainingCooldown, cancellation);
                    }
                    if (destination is not null)
                    {
                        var report = await connection.ProbeDestinationAsync(choice.Route, choice.Local(),
                            destination.Address, portableEngine.Checked, cancellation, pingCount, pingInterval,
                            (int)probeSize.Value, (int)probeTimeout.Value, (int)probeTtl.Value,
                            autoMtu.Checked, preferences.TunnelMtu, preferences.KeepaliveSeconds, comparisonPort,
                            ConfiguredProxy);
                        summaries.Add($"{choice.Region} · {choice.Title} · {report.Summary}" +
                            (baseline is null ? "" : report.CompareWith(baseline)));
                        probeDetails.Add($"{choice.Region} · {choice.Title}{Environment.NewLine}{report.Detail}");
                        WriteActivity($"{choice.Region} • {choice.Title} • {destination.Address}{Environment.NewLine}{report.Detail}");
                    }
                    else
                    {
                    var result = await connection.TestAsync(choice.Route, choice.Local(), cancellation,
                        portableEngine.Checked, dnsOverride.Text, autoMtu.Checked,
                        preferences.TunnelMtu, preferences.KeepaliveSeconds, preferences.RouteTestSeconds,
                        ConfiguredProxy);
                    measurements[choice.Key] = result with { TestedAt = DateTimeOffset.UtcNow };
                    if (result.ProfileUnavailable) unavailable++;
                    SaveMeasurements();
                    WriteActivity($"{choice.Title}: {TestLabel(choice.Key)}. {TestMetrics(result)}");
                    summaries.Add($"{choice.Region} · {choice.Title} · {TestLabel(choice.Key)} · {TestMetrics(result)}" +
                        (result.ProbeTiming.Length > 0 ? $" · {result.ProbeTiming}" : ""));
                    PopulateRoutes();
                    }
                }
                catch (EzConnectServiceException error) when (error.StatusCode == 429 &&
                    !connection.NeedsCleanup && lastRetriedIndex != i &&
                    (error.RetryAfter ?? TimeSpan.FromSeconds(30)) <= TimeSpan.FromMinutes(5))
                {
                    var delay = error.RetryAfter ?? TimeSpan.FromSeconds(30);
                    if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                    lastRetriedIndex = i;
                    traffic.Text = $"Service cooldown · retrying in {Math.Ceiling(delay.TotalSeconds)} seconds";
                    WriteActivity($"{choice.Title}: {traffic.Text}.");
                    await Task.Delay(delay, cancellation);
                    i--;
                    continue;
                }
                catch (EzConnectServiceException error) when (error.Code == "ROUTE_PROFILE_RELEASED" && !connection.NeedsCleanup)
                {
                    unavailable++;
                    summaries.Add($"{choice.Region} · {choice.Title} · Profile unavailable");
                    WriteActivity($"{choice.Title}: route configuration rejected. {error.Message}");
                }
                catch (Exception error) when (!connection.NeedsCleanup &&
                    !cancellation.IsCancellationRequested &&
                    (error is DestinationProbeException ||
                     destination is not null && error is (FormatException or System.Net.Sockets.SocketException)))
                {
                    unavailable++;
                    summaries.Add($"{choice.Region} · {choice.Title} · {error.Message}");
                    WriteActivity($"{choice.Title}: {error.Message}");
                }
                completedRoutes++;
                if (choice.Route is not null) lastServiceAttemptFinished = Stopwatch.GetTimestamp();
            }
            WriteActivity($"Comparison finished. {chosen.Count - unavailable} tested, {unavailable} unavailable.");
        });
        if (!closeAfterTest && !IsDisposed && summaries.Count > 0)
        {
            var heading = destination is null ? "Route comparison" : $"Destination comparison · {destination.Address}";
            string coverage = completedRoutes == chosen.Count
                ? $"Complete · {completedRoutes} route(s) processed"
                : $"Partial results · {completedRoutes} of {chosen.Count} routes processed";
            var report = coverage + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, summaries);
            WriteActivity(heading + Environment.NewLine + report);
            using var results = Dialog(heading, new Size(760, 350));
            var output = Input();
            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Vertical;
            output.Dock = DockStyle.Fill;
            output.Text = report;
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var summaryTab = new TabPage("Summary");
            summaryTab.Controls.Add(output);
            tabs.TabPages.Add(summaryTab);
            var detailTab = new TabPage("Individual probes");
            var detailOutput = Input();
            detailOutput.Multiline = true;
            detailOutput.ReadOnly = true;
            detailOutput.ScrollBars = ScrollBars.Vertical;
            detailOutput.Dock = DockStyle.Fill;
            detailOutput.Text = string.Join(Environment.NewLine + Environment.NewLine, probeDetails);
            detailTab.Controls.Add(detailOutput);
            if (destination is not null) tabs.TabPages.Add(detailTab);
            else detailTab.Dispose();
            var resultLayout = Stack(0, 44);
            resultLayout.Controls.Add(tabs, 0, 0);
            var resultActions = Actions();
            Add(resultActions, "Save report", () =>
            {
                using var picker = new SaveFileDialog
                {
                    FileName = "route-comparison.txt",
                    Filter = "Text report (*.txt)|*.txt",
                    DefaultExt = "txt",
                    AddExtension = true
                };
                if (picker.ShowDialog(results) != DialogResult.OK) return;
                try
                {
                    File.WriteAllLines(picker.FileName,
                    [
                        heading,
                        $"Recorded {DateTimeOffset.Now:O}",
                        comparisonPort is int port ? $"TCP port {port} · One connection attempt per route · Timeout {probeTimeout.Value} ms" :
                            destination is null ? "Standard WireSock route tests" :
                            $"Pings {pingCount} · Pause {pingInterval} ms · Payload {probeSize.Value} bytes · Timeout {probeTimeout.Value} ms · TTL {probeTtl.Value}",
                        "",
                        report,
                        "",
                        destination is null ? "" : detailOutput.Text
                    ]);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    MessageBox.Show(results, error.Message, "Could not save report");
                }
            });
            resultLayout.Controls.Add(resultActions, 0, 1);
            results.Controls.Add(resultLayout);
            results.ShowDialog(this);
        }
    }

    bool closeAfterTest;

    async Task RunTests(string message, Func<CancellationToken, Task> operation)
    {
        using var cancellation = new CancellationTokenSource();
        testCancellation = cancellation;
        try
        {
            await Run(message, async () =>
            {
                try { await operation(cancellation.Token); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { WriteActivity("Route test canceled."); }
            });
        }
        finally
        {
            testCancellation = null;
            UpdateControls();
            if (closeAfterTest && !IsDisposed)
                BeginInvoke(() =>
                {
                    closeAfterTest = false;
                    Close();
                });
        }
    }

    bool EnsureAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        ShowMessage("WireSock needs administrator access. Reopen RouteFinder with Run as administrator, then try again.");
        return false;
    }

    async Task Run(string message, Func<Task> operation)
    {
        if (busy) return;
        busy = true;
        banner.Text = message;
        banner.ForeColor = Accent;
        traffic.Text = "Please wait while RouteFinder completes this request.";
        UpdateControls();
        try { await operation(); }
        catch (Exception error) { WriteActivity(error.Message); ShowMessage(error.Message); }
        finally { busy = false; UpdateControls(); UpdateConnection(); }
    }

    void UpdateControls()
    {
        bool hasTunnel = connection.HasTunnel;
        bool cleanup = connection.NeedsCleanup && !hasTunnel;
        bool signed = connection.SignedIn || preview;
        account.Text = signed ? (string.IsNullOrWhiteSpace(signedInEmail) ? "Account connected" : signedInEmail) : "Optional account";
        if (signed && accountExpiry is { } expiry)
        {
            var remaining = expiry - DateTimeOffset.UtcNow;
            account.Text += remaining > TimeSpan.Zero
                ? $" · {(int)remaining.TotalHours}h {remaining.Minutes}m"
                : " · Access expired";
        }
        account.ForeColor = signed ? Accent : Muted;
        favorite.Enabled = !busy && selected != null && !preview;
        minimizeToTray.Enabled = !busy;
        killSwitch.Enabled = portableEngine.Enabled = dnsOverride.Enabled = ipv6Bindings.Enabled = autoMtu.Enabled =
            bypassLan.Enabled = !busy && !hasTunnel;
        signIn.Text = UiLanguage.Text(signed ? "Sign out" : "Add EZConnect");
        signIn.Enabled = !busy && !hasTunnel && !cleanup && !preview;
        refresh.Enabled = !busy && !hasTunnel && !cleanup && connection.SignedIn;
        import.Enabled = !busy && !hasTunnel && !cleanup;
        test.Text = UiLanguage.Text(testCancellation != null ? "Cancel test" : "Test route");
        test.Enabled = testCancellation is { } active ? !active.IsCancellationRequested :
            !busy && !hasTunnel && !cleanup && selected != null && !preview;
        compare.Enabled = !busy && testCancellation == null && test.Enabled && grid.Rows.Count > 1;
        connect.Text = UiLanguage.Text(hasTunnel ? "Disconnect" : cleanup ? "Retry cleanup" : "Connect");
        bool entirePc = routingMode.SelectedIndex == 1;
        connect.Enabled = !busy && !preview && (hasTunnel || cleanup || selected != null && (entirePc || apps.CheckedItems.Count > 0));
        // Keep the page readable when the app list is locked by an active tunnel.
        foreach (var control in appsPage.Controls.Cast<Control>().SelectMany(panel => panel.Controls.Cast<Control>()))
        {
            if (control is FlowLayoutPanel group)
                group.Enabled = !busy && !hasTunnel;
        }
        apps.Enabled = !busy && !hasTunnel && routingMode.SelectedIndex != 1;
        routingMode.Enabled = !busy && !hasTunnel;
        UpdateApps();
    }

    void UpdateApps()
    {
        int count = apps.CheckedItems.Count;
        int excludedCount = preferences.ExcludedApplications?.Length ?? 0;
        appsCount.Text = routingMode.SelectedIndex == 1
            ? $"{excludedCount} custom exclusion(s) · Edit in Settings → Exclude apps"
            : $"{count} selected  /  {apps.Items.Count} added";
        if (!busy && !connection.HasTunnel && !connection.NeedsCleanup)
            connect.Enabled = !preview && selected != null && (routingMode.SelectedIndex == 1 || count > 0);
        UpdateOverview(count);
    }

    bool nativeTunnelStopped;

    void UpdateConnection()
    {
        if (busy || IsDisposed) return;
        try
        {
            nativeTunnelStopped = connection.HasTunnel && !connection.TunnelIsActive;
            if (nativeTunnelStopped)
            {
                banner.Text = "Tunnel stopped";
                banner.ForeColor = Warning;
                traffic.Text = "WireSock reports an inactive tunnel. Use Reconnect or Disconnect.";
                return;
            }
            var state = connection.State;
            if (state is { } stats)
            {
                bool handshake = connection.PeerResponseObserved || stats.TimeSinceLastHandshakeMilliseconds >= 0 || stats.RxBytes > 0;
                int peerWait = Math.Clamp(preferences.PeerResponseSeconds, 5, 180);
                bool timedOut = !handshake && connectionStartedAt is { } started && DateTimeOffset.UtcNow - started >= TimeSpan.FromSeconds(peerWait);
                banner.Text = UiLanguage.Text(handshake ? "Connected" : timedOut ? "No peer response" : "Waiting for peer");
                banner.ForeColor = handshake ? Accent : Warning;
                traffic.Text = timedOut
                    ? $"{connected?.Title} hasn’t completed a handshake. Disconnect and try another route."
                    : $"{connected?.Title}   •   {(connectedConsole ? "Console" : connectedEntirePc ? "Entire PC" : "Selected apps")}   •   ↓ {FormatBytes(stats.RxBytes)}   ↑ {FormatBytes(stats.TxBytes)}" +
                      (handshake && stats.EstimatedRtt > 0 ? $"   •   {stats.EstimatedRtt} ms" : "") +
                      (stats.RxBytes > 0 && float.IsFinite(stats.EstimatedLoss) && stats.EstimatedLoss is >= 0 and <= 100 ? $"   •   Est. loss {stats.EstimatedLoss:0.#}%" : "") +
                      (stats.TimeSinceLastHandshakeMilliseconds >= 0 ? $"   •   Handshake {stats.TimeSinceLastHandshakeMilliseconds / 1000}s ago" : "");
                if (timedOut && !peerTimeoutLogged)
                {
                    peerTimeoutLogged = true;
                    WriteActivity($"{connected?.Title}: no WireGuard handshake after {peerWait} seconds.");
                }
            }
            else
            {
                banner.Text = UiLanguage.Text(connection.NeedsCleanup ? "Cleanup needed" : "Disconnected");
                banner.ForeColor = connection.NeedsCleanup ? Warning : Ink;
                traffic.Text = connection.NeedsCleanup ? "Retry cleanup to release the EZConnect session." :
                    ConnectionHint();
                tray.Visible = false;
                if (!Visible && !connection.NeedsCleanup) { Show(); WindowState = FormWindowState.Normal; }
            }
        }
        catch (Exception error) { timer.Stop(); WriteActivity("Could not read tunnel status. " + error.Message); }
    }

    static string FormatBytes(ulong bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.0} MB" : $"{bytes / 1024.0:0.0} KB";

    void AddFile()
    {
        using var picker = new OpenFileDialog { Filter = "Windows applications (*.exe)|*.exe", Multiselect = true };
        if (picker.ShowDialog(this) == DialogResult.OK) AddApps(picker.FileNames);
    }

    IEnumerable<CatalogGame> CatalogEntries(string[] names) => games.Items.OfType<CatalogGame>()
        .Where(entry => names.Contains(entry.Name, StringComparer.OrdinalIgnoreCase));

    string[] CatalogExecutables(CatalogGame game) => game.ExecutableNames
        .Concat(CatalogEntries(game.LinkedAppNames).SelectMany(entry => entry.ExecutableNames))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    static bool MatchesExecutable(string[] paths, string name) =>
        paths.Any(path => string.Equals(Path.GetFileName(path), Path.GetFileName(name), StringComparison.OrdinalIgnoreCase));

    void PickRunning(string[]? expected)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path != null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        (expected == null || expected.Any(name => Path.GetFileName(name).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))))
                        paths.Add(path);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        if (paths.Count == 0) { appHint.Text = "No matching processes are running. You can add the executable manually."; return; }
        using var dialog = Dialog("Choose running applications", new Size(700, 470));
        var layout = Stack(39, 0, 50);
        layout.Controls.Add(Label("Check the processes you want to add.", 11, true), 0, 0);
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true, BackColor = Card, ForeColor = Ink };
        foreach (string path in paths.Order(StringComparer.OrdinalIgnoreCase)) list.Items.Add(path, expected != null);
        layout.Controls.Add(list, 0, 1);
        var actions = Actions();
        var ok = Button("Add checked apps", true); ok.DialogResult = DialogResult.OK;
        actions.Controls.Add(ok); layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        if (dialog.ShowDialog(this) == DialogResult.OK) AddApps(list.CheckedItems.Cast<string>());
    }

    void AddApps(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            int existing = apps.Items.Cast<string>().ToList().FindIndex(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing < 0) apps.Items.Add(path, true);
            else apps.SetItemChecked(existing, true);
        }
        appHint.Text = "Restart apps with existing connections if they continue using the old route.";
        UpdateApps(); SaveApps();
    }

    void RemoveApp()
    {
        if (apps.SelectedIndex < 0) return;
        apps.Items.RemoveAt(apps.SelectedIndex);
        UpdateApps(); SaveApps();
    }

    void SaveApps()
    {
        if (preview) return;
        try { ProfileStore.SaveApplications(apps.CheckedItems.Cast<string>().ToList()); }
        catch (Exception error) { WriteActivity("Could not save app selections. " + error.Message); }
    }

    void ImportProfile()
    {
        using var picker = new OpenFileDialog
        {
            Filter = "Connection profiles (*.conf;*.json)|*.conf;*.json",
            Multiselect = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var updated = profiles.ToList();
        var failures = new List<string>();
        foreach (string path in picker.FileNames)
        {
          try
          {
            string name = Path.GetFileNameWithoutExtension(path);
            string config = File.ReadAllText(path);
            if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                config = EzConnectClient.ImportConfiguration(config, name, AskPrivateKey);
            var parsed = TunnelProfile.Parse(config, name);
            var configuredApps = parsed.GetInterface("AllowedApps")
                .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var savedApps = configuredApps.All(path => Path.IsPathFullyQualified(path) &&
                path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                path.IndexOfAny(['*', '?']) < 0)
                ? configuredApps.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            string baseName = name;
            for (int i = 2; updated.Any(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
                name = $"{baseName} ({i})";
            updated.Add(new SavedProfile { Name = name, Configuration = config, Applications = savedApps });
          }
          catch (OperationCanceledException) { break; }
          catch (Exception error) { failures.Add($"{Path.GetFileName(path)}: {error.Message}"); }
        }
        int imported = updated.Count - profiles.Count;
        if (imported > 0)
        {
            try { ProfileStore.Save(updated); }
            catch (Exception error) { ShowMessage(error.Message); return; }
            profiles = updated;
            routeCategory.SelectedIndex = 3;
            favoritesOnly.Checked = false;
            search.Clear();
            PopulateRoutes();
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Tag is Choice choice && choice.Profile == updated[^imported]) grid.CurrentCell = row.Cells[0];
            WriteActivity($"Imported {imported} local profile(s).");
        }
        if (failures.Count > 0)
            ShowMessage($"Imported {imported} profile(s). Some files could not be imported." +
                Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    void SaveProfileApps()
    {
        if (preview || busy || connection.HasTunnel || selected?.Profile is not { } profile) return;
        var replacement = new SavedProfile
        {
            Name = profile.Name,
            Configuration = profile.Configuration,
            Applications = apps.CheckedItems.Cast<string>().ToList()
        };
        var updated = profiles.Select(item => item == profile ? replacement : item).ToList();
        try
        {
            ProfileStore.Save(updated);
            profiles = updated;
            PopulateRoutes();
            WriteActivity($"Saved {replacement.Applications.Count} checked app(s) to {profile.Name}.");
        }
        catch (Exception error) { ShowMessage(error.Message); }
    }

    void LoadProfileApps()
    {
        if (preview || busy || connection.HasTunnel || selected?.Profile is not { } profile) return;
        var saved = profile.Applications ?? [];
        for (int i = 0; i < apps.Items.Count; i++) apps.SetItemChecked(i, false);
        routingMode.SelectedIndex = 0;
        AddApps(saved);
        Navigate(appsPage);
        WriteActivity($"Loaded {saved.Count} saved app(s) from {profile.Name}.");
    }

    void EditProfile()
    {
        if (selected?.Profile is not { } profile) return;
        using var dialog = Dialog("Edit " + profile.Name, new Size(620, 460));
        var layout = Stack(34, 0, 46);
        var config = Input(); config.Text = profile.Configuration; config.Multiline = true; config.Dock = DockStyle.Fill;
        config.ScrollBars = ScrollBars.Both; config.Font = new Font("Consolas", 10);
        layout.Controls.Add(Label("WireGuard configuration", 10, true), 0, 0);
        layout.Controls.Add(config, 0, 1);
        var actions = Actions();
        Add(actions, "Save profile", () =>
        {
            try
            {
                TunnelProfile.Parse(config.Text, profile.Name);
                var updated = profiles.Select(item => item == profile
                    ? new SavedProfile { Name = item.Name, Configuration = config.Text, Applications = item.Applications } : item).ToList();
                ProfileStore.Save(updated);
                profiles = updated;
                measurements.Remove("local:" + profile.Name);
                PopulateRoutes();
                dialog.Close();
            }
            catch (Exception error) { MessageBox.Show(dialog, error.Message, "Profile"); }
        });
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }

    void RemoveProfile()
    {
        if (selected?.Profile is not { } profile ||
            MessageBox.Show(this, $"Remove the saved profile {profile.Name}?", "Remove profile", MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        try
        {
            var updated = profiles.Where(item => item != profile).ToList();
            ProfileStore.Save(updated);
            profiles = updated;
            measurements.Remove("local:" + profile.Name);
            PopulateRoutes();
        }
        catch (Exception error) { ShowMessage(error.Message); }
    }

    void ExportProfile()
    {
        if (selected?.Profile is not { } profile) return;
        using var picker = new SaveFileDialog
        {
            FileName = profile.Name + ".conf",
            Filter = "WireGuard configuration (*.conf)|*.conf|Text file (*.txt)|*.txt",
            AddExtension = true,
            DefaultExt = "conf"
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(picker.FileName, profile.Configuration);
            WriteActivity($"Exported local profile {profile.Name}.");
        }
        catch (Exception error) { ShowMessage("Could not export the profile. " + error.Message); }
    }

    string AskPrivateKey()
    {
        using var dialog = Dialog("Profile private key", new Size(480, 145));
        var layout = Stack(41, 38, 44);
        var input = Input("WireGuard private key"); input.UseSystemPasswordChar = true; input.Dock = DockStyle.Top;
        layout.Controls.Add(Label("Enter the private key that belongs to this profile.", 10), 0, 0);
        layout.Controls.Add(input, 0, 1);
        var actions = Actions(); var ok = Button("Import", true); ok.DialogResult = DialogResult.OK;
        actions.Controls.Add(ok); layout.Controls.Add(actions, 0, 2);
        dialog.AcceptButton = ok; dialog.Controls.Add(layout);
        if (dialog.ShowDialog(this) != DialogResult.OK) throw new OperationCanceledException();
        return input.Text.Trim();
    }

    static Form Dialog(string title, Size size) => new()
    {
        Text = UiLanguage.Text(title), ClientSize = size, BackColor = Base, ForeColor = Ink, Padding = new Padding(16, 10, 16, 10),
        RightToLeft = UiLanguage.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No,
        RightToLeftLayout = UiLanguage.IsRightToLeft,
        StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false
    };

    void WriteActivity(string message)
    {
        if (journal.TextLength > 60000) journal.Text = journal.Text[^30000..];
        journal.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    void ShowMessage(string message) => MessageBox.Show(this, message, "RouteFinder", MessageBoxButtons.OK, MessageBoxIcon.Information);

    async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) return;
        if (busy)
        {
            e.Cancel = true;
            if (testCancellation is { } test)
            {
                closeAfterTest = true;
                test.Cancel();
            }
            return;
        }
        if (!connection.HasTunnel && !connection.NeedsCleanup) return;
        e.Cancel = true;
        busy = true;
        try { await connection.DisconnectAsync(); closing = true; Close(); }
        catch (Exception error)
        {
            WriteActivity(error.Message);
            if (MessageBox.Show(this, error.Message + "\r\n\r\nClose anyway?", "RouteFinder", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            { closing = true; Close(); }
        }
        finally { busy = false; if (!IsDisposed) { UpdateControls(); UpdateConnection(); } }
    }
}
