using static RouteFinder.App.StudioTheme;

namespace RouteFinder.App;

internal sealed partial class MainForm
{
    readonly Panel overviewPage = new() { Dock = DockStyle.Fill };
    readonly Button overviewNav = Button("Overview");
    readonly Label overviewApps = Label("", 19, true);
    readonly Label overviewRoute = Label("", 19, true);
    readonly Label overviewSource = Label("", 10, false, Muted);
    readonly Label overviewScope = Label("", 10, false, Muted);
    readonly Label overviewHint = Label("", 10, false, Muted);
    readonly Button overviewDetails = Button("Connection details");

    void BuildOverview()
    {
        var page = Stack(30, 57, 42, 224, 65, 0);
        page.Controls.Add(Label("ROUTING WORKSPACE", 9, true, Accent), 0, 0);
        page.Controls.Add(Label("Route your apps", 30, true), 0, 1);
        page.Controls.Add(Label("Choose what to route, then choose how it connects.", 11, false, Muted), 0, 2);

        var cards = Split();
        var appsCard = BuildOverviewCard("01   TRAFFIC", overviewApps, overviewScope, "Choose applications", appsPage);
        appsCard.Margin = new Padding(0, 6, 7, 4);
        cards.Controls.Add(appsCard, 0, 0);
        var routeCard = BuildOverviewCard("02   ROUTE", overviewRoute, overviewSource, "Browse routes", routePage);
        routeCard.Margin = new Padding(7, 6, 0, 4);
        cards.Controls.Add(routeCard, 1, 0);
        page.Controls.Add(cards, 0, 3);
        var help = Split(172);
        help.Controls.Add(overviewHint, 0, 0);
        overviewDetails.Anchor = AnchorStyles.Right;
        overviewDetails.Click += (_, _) => ShowConnectionDetails();
        help.Controls.Add(overviewDetails, 1, 0);
        page.Controls.Add(help, 0, 4);
        var art = new NetworkArt { Margin = Padding.Empty };
        page.Controls.Add(art, 0, 5);
        overviewPage.Controls.Add(page);
    }

    Control BuildOverviewCard(string heading, Label value, Label detail, string action, Panel destination)
    {
        var card = Stack(30, 56, 56, 0);
        card.BackColor = Card;
        card.Padding = new Padding(20, 13, 20, 14);
        card.Controls.Add(Label(heading, 9, true, Accent), 0, 0);
        card.Controls.Add(value, 0, 1);
        card.Controls.Add(detail, 0, 2);
        var actions = Actions();
        Add(actions, action, () => Navigate(destination));
        card.Controls.Add(actions, 0, 3);
        return card;
    }

    string ConnectionHint() => UiLanguage.Text(selected is null
        ? "Choose a route in Routes to get started."
        : routingMode.SelectedIndex == 0 && apps.CheckedItems.Count == 0
            ? "Choose applications or switch to Entire PC."
            : "Ready to connect.");

    void UpdateOverview(int count)
    {
        bool entirePc = routingMode.SelectedIndex == 1;
        overviewApps.Text = UiLanguage.Text(entirePc ? "Entire PC" : count == 0 ? "Choose what to route" :
            count == 1 ? "1 app selected" : $"{count} apps selected");
        overviewScope.Text = UiLanguage.Text(entirePc ? "This route applies to the whole computer, subject to exclusions." :
            "Only the apps you check will use the route.");
        var route = connected ?? selected;
        overviewRoute.Text = route?.Title ?? UiLanguage.Text("Choose a route");
        overviewSource.Text = route?.Source ?? UiLanguage.Text("Import a config or add an EZConnect account.");
        overviewHint.Text = connection.HasTunnel
            ? UiLanguage.Text("Connection status and traffic are shown below.")
            : UiLanguage.Text("Find connection diagnostics and test results in Activity.");
        overviewDetails.Visible = connection.HasTunnel;
        if (!busy && !connection.HasTunnel && !connection.NeedsCleanup)
            traffic.Text = ConnectionHint();
    }
}
