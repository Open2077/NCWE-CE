// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NCWE.Export;

namespace NCWE.Studio;

// The Open//77 account: the editor never needs it, cloud saves and live collaboration require it
// (RequireAccountAsync asks for it when one of them is used signed out). Signing in is the
// launcher's: the browser opens open2077.net, the person signs in there, and Studio receives a
// session for this Windows user (Open77Account). The home screen puts it forward; the title bar
// shows who is signed in.
public sealed partial class MainWindow
{
    private enum AccountState { SignedOut, Waiting, SignedIn }

    private readonly Open77Account _open77 = new(Open77Endpoints.Default());
    // Beside the settings (a --state-dir instance keeps its own).
    private readonly Open77SessionStore _accountStore = new(Path.Combine(NCWE.Studio.Session.StudioSettings.StateDirectory, "open77-session-v1.dat"));
    private Open77Session? _account;
    private CancellationTokenSource? _signInCancel;
    private string? _accountError;
    private bool _accountOffline;
    private AccountState _accountState;

    internal Open77Session? Account => _account;

    // A session kept from a previous run is shown at once, then checked with the platform: a revoked
    // or expired one signs out; an unreachable platform keeps it (offline).
    private async void InitializeAccount()
    {
        _account = _accountStore.Load();
        _accountState = _account is null ? AccountState.SignedOut : AccountState.SignedIn;
        ShowAccount();
        if (_account is not { } kept) return;
        try
        {
            var fresh = await _open77.RefreshAsync(kept);
            if (_account != kept) return;   // signed out or in again meanwhile
            if (fresh is null)
            {
                _accountStore.Clear(); _account = null; _accountState = AccountState.SignedOut;
                _accountError = Loc.T("Your Open//77 session has ended: sign in again.");
            }
            else { _account = fresh; _accountStore.Save(fresh); }
            _accountOffline = false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { _accountOffline = true; }
        ShowAccount();
    }

    private void OnAccountSignIn(object sender, RoutedEventArgs e) => _ = SignInAsync(Brand.OpenUrl);

    // A new sign-in replaces one still waiting for the browser (its loopback port is freed).
    internal async Task SignInAsync(Action<string> openBrowser)
    {
        _signInCancel?.Cancel();
        var cancel = _signInCancel = new CancellationTokenSource();
        _accountError = null; _accountState = AccountState.Waiting;
        ShowAccount();
        try
        {
            var session = await _open77.SignInAsync(url => openBrowser(url.AbsoluteUri), Open77Account.SignInTimeout, cancel.Token);
            _account = session; _accountOffline = false;
            _accountStore.Save(session);
            BringToFront();
            if (Session is { } s) s.Status = Loc.F("Signed in to Open//77 as {0}.", session.DisplayName);
        }
        catch (OperationCanceledException) { if (_signInCancel == cancel) _accountError = Loc.T("Sign-in cancelled."); }
        catch (TimeoutException) { if (_signInCancel == cancel) _accountError = Loc.T("The browser did not come back in time: try again."); }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or InvalidDataException or System.Text.Json.JsonException or System.Net.HttpListenerException)
        {
            if (_signInCancel == cancel) _accountError = ex is HttpRequestException ? Loc.T("Could not reach open2077.net: check your connection and try again.") : ex.Message;
        }
        finally
        {
            if (_signInCancel == cancel) { _signInCancel = null; _accountState = _account is null ? AccountState.SignedOut : AccountState.SignedIn; }
            cancel.Dispose();
            ShowAccount();
        }
    }

    private void OnAccountCancel(object sender, RoutedEventArgs e) => _signInCancel?.Cancel();

    private void OnAccountSignOut(object sender, RoutedEventArgs e) => SignOut();

    internal void SignOut()
    {
        if (_account is { } session) _ = _open77.SignOutAsync(session);   // revoked at the platform, best effort
        _accountStore.Clear();
        _account = null; _accountError = null; _accountOffline = false; _accountState = AccountState.SignedOut;
        EndLiveOnSignOut();
        _me = null; _cloudProjectOf = null;
        ShowAccount();
        if (Session is { } s) s.Status = Loc.T("Signed out of Open//77.");
    }

    private void OnAccountManage(object sender, RoutedEventArgs e) => Brand.OpenUrl(_open77.Endpoints.AccountPage.AbsoluteUri);

    // The home card and the title bar chip, for the current state.
    private void ShowAccount()
    {
        var state = _signInCancel is not null ? AccountState.Waiting : _account is null ? AccountState.SignedOut : AccountState.SignedIn;
        _accountState = state;
        HomeSignedOut.Visibility = state == AccountState.SignedOut ? Visibility.Visible : Visibility.Collapsed;
        HomeSigningIn.Visibility = state == AccountState.Waiting ? Visibility.Visible : Visibility.Collapsed;
        HomeSignedIn.Visibility = state == AccountState.SignedIn ? Visibility.Visible : Visibility.Collapsed;
        HomeAccountBadge.Visibility = state == AccountState.SignedIn && !_accountOffline ? Visibility.Collapsed : Visibility.Visible;
        HomeAccountBadgeText.Text = state == AccountState.SignedIn ? Loc.T("OFFLINE") : Loc.T("OPTIONAL");
        HomeAccountError.Text = _accountError ?? "";
        HomeAccountError.Visibility = string.IsNullOrEmpty(_accountError) ? Visibility.Collapsed : Visibility.Visible;
        string initial = _account is { DisplayName.Length: > 0 } a ? a.DisplayName[..1].ToUpperInvariant() : "";
        if (_account is { } account)
        {
            HomeAccountInitial.Text = initial;
            HomeAccountName.Text = account.DisplayName;
            HomeAccountDetail.Text = _accountOffline ? Loc.T("Open//77 account · open2077.net unreachable for now")
                : account.Email is { Length: > 0 } email ? Loc.F("Open//77 account · {0}", email) : Loc.T("Open//77 account");
        }
        // Title bar: who is signed in, or an invitation.
        AccountAvatar.Visibility = state == AccountState.SignedIn ? Visibility.Visible : Visibility.Collapsed;
        AccountInitial.Text = state == AccountState.SignedIn ? initial : "";
        AccountIcon.Visibility = state == AccountState.SignedIn ? Visibility.Collapsed : Visibility.Visible;
        AccountLabel.Text = state switch { AccountState.SignedIn => _account!.DisplayName, AccountState.Waiting => Loc.T("Signing in…"), _ => Loc.T("Sign in") };
        ToolTipService.SetToolTip(AccountButton, state switch
        {
            AccountState.SignedIn => Loc.F("Signed in to Open//77 as {0}", _account!.DisplayName),
            AccountState.Waiting => Loc.T("Finish signing in in your browser"),
            _ => Loc.T("Sign in with your Open//77 account (optional for editing; required for cloud saves and live collaboration)")
        });
    }

    private void OnAccountMenuOpening(object sender, object e)
    {
        AccountMenu.Items.Clear();
        switch (_accountState)
        {
            case AccountState.SignedIn:
                AccountMenu.Items.Add(new MenuFlyoutItem { Text = Loc.F("Signed in as {0}", _account!.DisplayName), IsEnabled = false, Icon = new FontIcon { Glyph = "" } });
                if (_account.Email is { Length: > 0 } email) AccountMenu.Items.Add(new MenuFlyoutItem { Text = email, IsEnabled = false });
                if (_accountOffline) AccountMenu.Items.Add(new MenuFlyoutItem { Text = Loc.T("open2077.net is unreachable for now"), IsEnabled = false });
                AccountMenu.Items.Add(new MenuFlyoutSeparator());
                AccountMenu.Items.Add(Item(Loc.T("Manage account on open2077.net"), "", () => OnAccountManage(this, new RoutedEventArgs())));
                AccountMenu.Items.Add(Item(Loc.T("Sign out"), "", SignOut));
                break;
            case AccountState.Waiting:
                AccountMenu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Finish signing in in your browser"), IsEnabled = false });
                AccountMenu.Items.Add(Item(Loc.T("Open the sign-in page again"), "", () => _ = SignInAsync(Brand.OpenUrl)));
                AccountMenu.Items.Add(Item(Loc.T("Cancel"), "", () => _signInCancel?.Cancel()));
                break;
            default:
                AccountMenu.Items.Add(Item(Loc.T("Sign in with Open//77…"), "", () => _ = SignInAsync(Brand.OpenUrl)));
                AccountMenu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Optional for editing. Required for cloud saves and live collaboration."), IsEnabled = false });
                break;
        }
    }

    // Cloud saves and collaboration run on the account: signed out, the user is asked to sign in
    // (the browser), and the command goes on once they are. False: still signed out (cancelled).
    internal async Task<bool> RequireAccountAsync(string feature)
    {
        if (_account is not null || CollabDevToken is not null) return true;
        if (App.Background) { if (Session is { } s) s.Error = Loc.F("{0} requires an Open//77 account: sign in first.", feature); return false; }
        var ask = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Sign in to Open//77"),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
                Text = Loc.F("{0} uses your Open//77 account: your worlds are saved with it and collaborators see who you are.\n\nSigning in happens on open2077.net in your browser; your password is never typed into NCWE.", feature)
            },
            PrimaryButtonText = Loc.T("Sign in with Open//77"), CloseButtonText = Loc.T("Not now"), DefaultButton = ContentDialogButton.Primary
        };
        if (await ask.ShowAsync() != ContentDialogResult.Primary) return false;
        var waiting = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Finish signing in in your browser"),
            Content = new StackPanel
            {
                Spacing = 10, MaxWidth = 420,
                Children =
                {
                    new TextBlock { Text = Loc.T("open2077.net is open in your browser: sign in or create your account there, then come back here."), TextWrapping = TextWrapping.Wrap },
                    new ProgressBar { IsIndeterminate = true }
                }
            },
            CloseButtonText = Loc.T("Cancel")
        };
        var signIn = SignInAsync(Brand.OpenUrl);
        var shown = waiting.ShowAsync().AsTask();
        if (await Task.WhenAny(signIn, shown) == signIn) waiting.Hide();
        else { _signInCancel?.Cancel(); await signIn; }
        if (_account is null && _accountError is { } error && Session is { } session) session.Error = error;
        return _account is not null;
    }

    // The collaboration service refused the session (revoked or expired at the platform).
    internal void AccountRejected()
    {
        if (_account is null) return;
        _accountStore.Clear();
        _account = null; _accountOffline = false;
        _accountError = Loc.T("Your Open//77 session has ended: sign in again.");
        ShowAccount();
    }

    // After the browser: back to Studio.
    private void BringToFront()
    {
        Activate();
        SetForegroundWindow(_hwnd);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    // Automation (tests): the state, and a sign-in that opens no browser (its page address is returned).
    internal async Task<object> AccountUi(string action)
    {
        string? opened = null;
        switch (action)
        {
            case "sign-in-test":
                var waiting = SignInAsync(url => opened = url);
                await Task.Delay(300);
                return new { state = _accountState.ToString(), url = opened, done = waiting.IsCompleted };
            case "cancel": _signInCancel?.Cancel(); await Task.Delay(100); break;
            case "sign-out": SignOut(); break;
            case "state": break;
            default: throw new ArgumentException("Unknown account action (state, sign-in-test, cancel, sign-out).");
        }
        return new { state = _accountState.ToString(), name = _account?.DisplayName, email = _account?.Email, offline = _accountOffline, error = _accountError, store = _accountStore.Path };
    }
}
