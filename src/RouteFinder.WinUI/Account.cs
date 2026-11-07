using System.Diagnostics;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RouteFinder.App;
using RouteFinder.Service;

namespace RouteFinder.WinUI;

public sealed partial class MainWindow
{
    string? signedInEmail;
    string? rememberedEmail;
    BootstrapData? accountCatalog;
    DateTimeOffset? accountLoadedAt;

    async void RestoreAccountOnLoad(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= RestoreAccountOnLoad;
        await RestoreRememberedSignIn();
    }

    Task RetryRememberedSignIn() => RestoreRememberedSignIn(interactive: true);

    async Task RestoreRememberedSignIn(bool interactive = false)
    {
        if (busy) return;
        if (session.SignedIn)
        {
            if (interactive) Report("Your EZConnect account is already signed in.", InfoBarSeverity.Informational);
            return;
        }
        ProfileStore.RememberedLogin? saved;
        try { saved = ProfileStore.LoadLogin(); }
        catch (Exception error) { Report($"Could not read the saved EZConnect sign-in. {error.Message}"); return; }
        if (saved is null)
        {
            if (interactive) Report("There is no saved EZConnect sign-in on this PC.", InfoBarSeverity.Informational);
            return;
        }

        await Run("Restoring EZConnect sign-in…", async () =>
        {
            bool refreshed = false;
            try
            {
                var catalog = await session.RestoreAsync(saved.RefreshToken, token =>
                {
                    refreshed = true;
                    SaveLogin(saved.Email, token);
                });
                signedInEmail = rememberedEmail = saved.Email;
                ApplyCatalog(catalog, restoreLastRoute: true);
            }
            catch (EzConnectServiceException error) when (!refreshed && error.StatusCode is 401 or 403)
            {
                ClearLogin();
                Report("Your saved EZConnect sign-in has expired. Sign in again to load your routes.");
            }
        });
    }

    void ApplyCatalog(BootstrapData catalog, bool restoreLastRoute = false)
    {
        accountCatalog = catalog;
        accountLoadedAt = DateTimeOffset.UtcNow;
        if (selectedGame is { } game)
            selectedGame = catalog.Games.FirstOrDefault(item => item.Name.Equals(game.Name, StringComparison.OrdinalIgnoreCase));
        var previous = restoreLastRoute ? preferences.LastConnectedRoute ?? route?.Key : route?.Key;
        serviceRoutes.Clear();
        serviceRoutes.AddRange(catalog.Routes.DistinctBy(item => (item.RegionCode, item.Name))
            .SelectMany(item => item.HasIpv6 ? new[] { item, item with { OverIpv6 = true } } : [item]));
        route = Routes().FirstOrDefault(item => item.Key == previous) ?? Routes().FirstOrDefault();
        AddActivity($"Loaded {serviceRoutes.Count} EZConnect routes.");
    }

    void SaveLogin(string email, string token)
    {
        try { ProfileStore.SaveLogin(email, token); }
        catch (Exception error) { Report($"Could not save the EZConnect sign-in. {error.Message}"); }
    }

    void ClearLogin()
    {
        try { ProfileStore.ClearLogin(); }
        catch (Exception error) { Report($"Could not clear the saved EZConnect sign-in. {error.Message}"); }
    }

    async void ToggleAccount(object sender, RoutedEventArgs args)
    {
        if (preview)
        {
            var demo = MakeDialog("EZConnect sign-in", new TextBlock
            {
                Text = "This window uses sample routes. Open RouteFinder with your saved profiles and account to sign in.",
                TextWrapping = TextWrapping.Wrap
            }, "Open RouteFinder");
            if (await demo.ShowAsync() == ContentDialogResult.Primary && Environment.ProcessPath is { } executable)
                SwitchTo(executable);
            return;
        }
        if (session.SignedIn) { await ManageAccount(); return; }

        var email = new TextBox { Header = "Email", PlaceholderText = "name@example.com" };
        var password = new PasswordBox { Header = "Password" };
        var remember = new CheckBox { Content = "Remember me on this PC", IsChecked = true };
        var content = new StackPanel { Spacing = 12, MinWidth = 300 };
        content.Children.Add(new TextBlock { Text = "Add EZConnect routes to your library.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(email);
        content.Children.Add(password);
        content.Children.Add(remember);
        content.Children.Add(new HyperlinkButton
        {
            Content = "Forgot password?",
            NavigateUri = new Uri("https://ezconnect.ir/users/forgot")
        });
        var signInError = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        content.Children.Add(signInError);
        var dialog = MakeDialog("Sign in to EZConnect", content, "Sign in");
        dialog.IsPrimaryButtonEnabled = false;
        void Validate() => dialog.IsPrimaryButtonEnabled =
            MailAddress.TryCreate(email.Text.Trim(), out _) && password.Password.Length > 0;
        email.TextChanged += (_, _) => Validate();
        password.PasswordChanged += (_, _) => Validate();
        dialog.PrimaryButtonClick += async (_, click) =>
        {
            var deferral = click.GetDeferral();
            try
            {
                await Run("Signing in to EZConnect…", async () =>
                {
                    var address = new MailAddress(email.Text.Trim()).Address;
                    var catalog = await session.SignInAsync(address, password.Password);
                    signedInEmail = address;
                    rememberedEmail = remember.IsChecked == true ? address : null;
                    if (rememberedEmail is not null && session.RefreshToken is { } token) SaveLogin(address, token);
                    else ClearLogin();
                    ApplyCatalog(catalog);
                });
                click.Cancel = !session.SignedIn;
                if (click.Cancel)
                {
                    signInError.Text = Notice.Message;
                    signInError.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                password.Password = "";
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
        password.Password = "";
    }

    async Task ManageAccount()
    {
        var catalog = accountCatalog;
        var details = new StackPanel { Spacing = 9 };
        details.Children.Add(new TextBlock { Text = signedInEmail ?? "Signed in", TextWrapping = TextWrapping.Wrap });
        details.Children.Add(new TextBlock { Text = $"{serviceRoutes.Count} routes · {catalog?.Games.Count ?? 0} catalog entries" });
        if (catalog is not null && catalog.Raw.ValueKind == JsonValueKind.Object &&
            catalog.Raw.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object)
        {
            if (account.TryGetProperty("user_status", out var status) && status.ValueKind == JsonValueKind.String)
                details.Children.Add(new TextBlock { Text = $"Service status  ·  {status.GetString()}", TextWrapping = TextWrapping.Wrap });
            if (ReadNumber(account, "remaining_seconds") is double seconds && seconds <= 315576000 && accountLoadedAt is { } loaded)
                details.Children.Add(new TextBlock { Text = $"Access expires  ·  {loaded.AddSeconds(seconds).ToLocalTime():g}" });
            var used = ReadNumber(account, "service_usage_gb");
            var cap = ReadNumber(account, "data_cap_gb");
            bool unlimited = account.TryGetProperty("is_unlimited", out var flag) && flag.ValueKind == JsonValueKind.True;
            string? usage = unlimited ? used is double gb ? $"{gb:0.##} GB used · Unlimited" : "Unlimited"
                : used is double usageGb ? cap is double capGb ? $"{usageGb:0.##} / {capGb:0.##} GB" : $"{usageGb:0.##} GB used" : null;
            if (usage is not null) details.Children.Add(new TextBlock { Text = $"Traffic  ·  {usage}" });
        }
        if (catalog is not null && (ReadReferral(catalog.Raw) ??
            (catalog.Raw.ValueKind == JsonValueKind.Object && catalog.Raw.TryGetProperty("account", out var accountDetails)
                ? ReadReferral(accountDetails) : null)) is { } referral)
        {
            var copy = new Button { Content = "Copy referral link" };
            copy.Click += (_, _) =>
            {
                try
                {
                    var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    data.SetText("https://ezconnect.ir/users/register?id=" + Uri.EscapeDataString(referral));
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                    copy.Content = "Referral link copied";
                }
                catch (Exception error) { Report(error.Message); }
            };
            details.Children.Add(copy);
        }
        if (!string.IsNullOrWhiteSpace(catalog?.News))
            details.Children.Add(new ScrollViewer { MaxHeight = 140, Content = new TextBlock { Text = catalog.News.Trim(), TextWrapping = TextWrapping.Wrap } });

        var dialog = MakeDialog("EZConnect account", details, "Refresh routes");
        dialog.SecondaryButtonText = "Sign out";
        dialog.CloseButtonText = "Close";
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                await Run("Refreshing EZConnect routes…", async () => ApplyCatalog(await session.ReloadAsync()));
                break;
            case ContentDialogResult.Secondary:
                await Run("Signing out of EZConnect…", async () =>
                {
                    try { await session.SignOutAsync(); }
                    finally
                    {
                        if (!session.SignedIn)
                        {
                            signedInEmail = rememberedEmail = null;
                            accountCatalog = null;
                            accountLoadedAt = null;
                            selectedGame = null;
                            ClearLogin();
                            serviceRoutes.Clear();
                            if (route?.Service is not null) route = Routes().FirstOrDefault();
                        }
                    }
                });
                break;
        }
    }

    static double? ReadNumber(JsonElement source, string field) =>
        source.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double result) && double.IsFinite(result) && result >= 0 ? result : null;

    static string? ReadReferral(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("refrals_id", out var value)) return null;
        string? code = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number when value.TryGetInt64(out long number) && number > 0 => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(code) || new[] { "0", "false", "null", "none" }.Contains(code, StringComparer.OrdinalIgnoreCase) ? null : code;
    }

    void SwitchTo(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
            Close();
        }
        catch (Exception error) { Report($"Could not open RouteFinder. {error.Message}"); }
    }
}
