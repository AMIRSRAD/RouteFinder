using Microsoft.UI.Xaml;
using RouteFinder.App;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    // Keeps the Providers page in step with the account and the route library.
    void RefreshProviders()
    {
        bool signedIn = session.SignedIn;
        int imported = profiles.Count;
        int provided = serviceRoutes.Count;
        bool editable = !busy && !session.NeedsCleanup && !restoringAccount;

        EzStatus.Text = UiLanguage.Text(signedIn ? signedInEmail ?? "Signed in" : preview ? "Sample data" : "Sign in to load your routes");
        EzBadgeText.Text = UiLanguage.Text(signedIn ? "Signed in" : preview ? "Preview" : "Not signed in");
        EzDetail.Text = UiLanguage.Text(signedIn
            ? $"{provided} routes · {accountCatalog?.Games.Count ?? 0} games and apps in the catalog"
            : preview ? "This window uses sample routes."
            : "Sign in to add EZConnect routes and the game catalog to your library.");
        var neutral = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        EzBadgeText.Foreground = signedIn ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] : neutral;

        EzSignInButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        EzRefreshButton.Visibility = EzAccountButton.Visibility = EzSignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        EzCatalogButton.Visibility = signedIn && accountCatalog is { Games.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        EzRetryButton.Visibility = !signedIn && !preview ? Visibility.Visible : Visibility.Collapsed;
        EzRetryButton.IsEnabled = editable;
        EzSignInButton.IsEnabled = EzRefreshButton.IsEnabled = EzSignOutButton.IsEnabled = editable;
        EzAccountButton.IsEnabled = !busy && !restoringAccount;
        ImportProviderButton.IsEnabled = !preview && editable;
        ImportedBadgeText.Text = UiLanguage.Text($"{imported} route{(imported == 1 ? "" : "s")}");
    }

    async void RefreshProviderRoutes(object sender, RoutedEventArgs args)
    {
        if (busy || !session.SignedIn) return;
        await Run("Refreshing EZConnect routes…", async () => ApplyCatalog(await session.ReloadAsync()));
        RefreshProviders();
    }

    async void RetrySavedSignIn(object sender, RoutedEventArgs args)
    {
        await RetryRememberedSignIn();
        RefreshProviders();
    }

    async void SignOutProvider(object sender, RoutedEventArgs args)
    {
        if (busy || !session.SignedIn) return;
        await SignOutAccount();
        RefreshProviders();
    }
}
