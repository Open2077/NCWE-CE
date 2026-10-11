// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NCWE.Collab;
using NCWE.Collab.Client;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Studio.Session;
using NCWE.Studio.Viewport;
using Windows.ApplicationModel.DataTransfer;

namespace NCWE.Studio;

// Live collaboration: the world opened to others in a session (a code and a password, on the
// collaboration service). Everyone's edits arrive as they are made (LiveProject); the others'
// cameras and selections show in the view (CollabPresence); what someone has selected is locked
// for everyone else; viewers watch and chat. The Live tab shows the session, who is in it, the
// owner's controls and the chat. Sessions require the Open//77 account; the editor never does.
public sealed partial class MainWindow
{
    private LiveProject? _live;
    private string _liveSelectionKey = "";
    private bool _liveDirty = true, _syncingLive, _fetchingAssets, _assetsWanted;
    private string? _liveEndedReason;
    private string? _filesFromSnapshot;   // the session snapshot custom files were last read from

    internal LiveProject? Live => _live;

    private void Post(Action action) => DispatcherQueue.Post(() => action());

    private EditorDocument RebuildDocument(ProjectData project) => Session!.BuildDocument(project);

    // --- Starting and joining ---------------------------------------------------------------------

    private void OnCollabButton(object sender, RoutedEventArgs e)
    {
        if (_live is not null) { ShowTab("Live"); return; }
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(Item(Loc.T("Start a live session…"), "", () => OnStartSession(this, new RoutedEventArgs())));
        menu.Items.Add(Item(Loc.T("Join a session…"), "", () => OnJoinSession(this, new RoutedEventArgs())));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem
        {
            IsEnabled = false,
            Text = _account is { } a ? Loc.F("As {0} (Open//77)", a.DisplayName) : CollabDevToken is not null ? Loc.T("Developer account (local server)") : Loc.T("Requires an Open//77 account")
        });
        menu.ShowAt((FrameworkElement)sender);
    }

    private void OnShowSessionPanel(object sender, RoutedEventArgs e) => ShowTab("Live");

    private async void OnStartSession(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        if (_live is not null) { ShowTab("Live"); return; }
        if (!await RequireAccountAsync(Loc.T("Live collaboration"))) return;
        var name = new TextBox { Header = Loc.T("Session name"), Text = s.Document.Project.Name, MaxLength = 80 };
        var password = new PasswordBox { Header = Loc.T("Password (4 characters or more)"), MaxLength = 128 };
        var max = new NumberBox { Header = Loc.T("Participants at most"), Minimum = 2, Maximum = 64, Value = 8, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var role = new ComboBox { Header = Loc.T("Who joins can"), HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        role.Items.Add(new ComboBoxItem { Content = Loc.T("Edit the world"), Tag = SessionRoles.Editor });
        role.Items.Add(new ComboBoxItem { Content = Loc.T("Watch and chat (you choose who edits)"), Tag = SessionRoles.Viewer });
        role.SelectedIndex = 0;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["DangerBrush"], Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionStyle"],
            Text = Loc.T("Opens this world to others: give them the session code and its password. They need NCWE Studio, the game and an Open//77 account. Custom meshes and sounds the world uses travel with it.")
        });
        panel.Children.Add(name); panel.Children.Add(password);
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(role, 1);
        row.Children.Add(max); row.Children.Add(role);
        panel.Children.Add(row);
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Start a live session"), Content = panel,
            PrimaryButtonText = Loc.T("Start"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false
        };
        void Validate() => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0 && password.Password.Length >= 4;
        name.TextChanged += (_, _) => Validate();
        password.PasswordChanged += (_, _) => Validate();
        dialog.PrimaryButtonClick += async (d, args) =>
        {
            var wait = args.GetDeferral();
            d.IsPrimaryButtonEnabled = false;
            string? failure = await HostSessionAsync(name.Text.Trim(), password.Password, (int)(double.IsNaN(max.Value) ? 8 : max.Value),
                (role.SelectedItem as ComboBoxItem)?.Tag as string ?? SessionRoles.Editor);
            if (failure is not null) { error.Text = failure; error.Visibility = Visibility.Visible; args.Cancel = true; Validate(); }
            wait.Complete();
        };
        await dialog.ShowAsync();
    }

    // Null when the session is live; else why not.
    internal async Task<string?> HostSessionAsync(string name, string password, int maxParticipants, string defaultRole)
    {
        if (Session is not { } s) return Loc.T("The editor is not ready yet.");
        if (_live is not null) return Loc.T("You are already in a session: leave it first.");
        if (!await RequireAccountAsync(Loc.T("Live collaboration")) || Collab() is not { } client) return Loc.T("Sign in to Open//77 to start a session.");
        var document = s.Document;
        // Until the session holds its first snapshot, edits would be missing from it: they wait.
        document.ReadOnly = true;
        try
        {
            s.Status = Loc.T("Opening the session…");
            var live = await LiveProject.HostAsync(client, new CreateSessionRequest(name, password, maxParticipants, defaultRole), document, Packager, Post, RebuildDocument);
            Attach(live);
            s.Status = Loc.F("Session {0} is live: give its code and password to the people you invite.", live.Code);
            return null;
        }
        catch (Exception ex) when (IsCollabFailure(ex))
        {
            document.ReadOnly = false;
            s.Status = Loc.T("Ready");
            return Loc.F("The session could not start: {0}", CollabError(ex));
        }
    }

    private async void OnJoinSession(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s) return;
        if (_live is not null) { ShowTab("Live"); return; }
        if (!await RequireAccountAsync(Loc.T("Live collaboration"))) return;
        if (!await ConfirmDiscardChanges()) return;
        var code = new TextBox { Header = Loc.T("Session code"), PlaceholderText = "ABCD-EFGH", Text = _settings.LastSessionCode ?? "", MaxLength = 12, CharacterCasing = CharacterCasing.Upper, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"] };
        var password = new PasswordBox { Header = Loc.T("Password"), MaxLength = 128 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["DangerBrush"], Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionStyle"],
            Text = Loc.T("The world you are editing is replaced by the session's (it stays in your autosaves). Once you have joined, the password is no longer asked on this account.")
        });
        panel.Children.Add(code); panel.Children.Add(password); panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Join a live session"), Content = panel,
            PrimaryButtonText = Loc.T("Join"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
        };
        void Validate() => dialog.IsPrimaryButtonEnabled = code.Text.Count(char.IsLetterOrDigit) >= 8;
        code.TextChanged += (_, _) => Validate();
        Validate();
        dialog.PrimaryButtonClick += async (d, args) =>
        {
            var wait = args.GetDeferral();
            d.IsPrimaryButtonEnabled = false;
            string? failure = await JoinSessionAsync(code.Text.Trim(), password.Password);
            if (failure is not null) { error.Text = failure; error.Visibility = Visibility.Visible; args.Cancel = true; Validate(); }
            wait.Complete();
        };
        await dialog.ShowAsync();
    }

    // Null once in the session; else why not.
    internal async Task<string?> JoinSessionAsync(string code, string password)
    {
        if (Session is not { } s) return Loc.T("The editor is not ready yet.");
        if (_live is not null) return Loc.T("You are already in a session: leave it first.");
        if (s.Index is null) return Loc.T("Open the game first: the session's world is built on its archives.");
        if (!await RequireAccountAsync(Loc.T("Live collaboration")) || Collab() is not { } client) return Loc.T("Sign in to Open//77 to join a session.");
        try
        {
            s.Status = Loc.T("Joining the session…");
            var (live, project, baseSeq) = await LiveProject.JoinAsync(client, code, password, Packager, Post, RebuildDocument);
            if (!await s.OpenProjectDataAsync(project, Loc.F("Joined the session “{0}”.", live.Session.Name)))
            {
                await live.DisposeAsync();
                return s.Error.Length > 0 ? s.Error : Loc.T("The session's world could not be opened.");
            }
            live.Start(s.Document, baseSeq);
            _cloudProjectOf = null;
            Attach(live);
            _settings.LastSessionCode = live.Code; _settings.Save();
            UpdateProjectPanel(); _entitiesDirty = true;
            return null;
        }
        catch (Exception ex) when (IsCollabFailure(ex))
        {
            s.Status = Loc.T("Ready");
            return ex is CollabApiException { Code: CollabErrors.NotFound } ? Loc.T("No session has this code (or it has closed).") : CollabError(ex);
        }
    }

    // --- In a session --------------------------------------------------------------------------------

    private void Attach(LiveProject live)
    {
        var s = Session!;
        _live = live; _liveEndedReason = null;
        live.Changed += () => _liveDirty = true;
        live.Notice += message =>
        {
            s.Status = message;
            AddChatLine(null, message, DateTimeOffset.Now, null);
        };
        live.ChatReceived += chat => AddChatLine(chat.DisplayName, chat.Text, chat.At, CollabPresence.ColorOf(chat.AccountId.ToString("N")));
        live.DocumentReplaced += document =>
        {
            s.ReplaceDocument(document, s.ProjectPath);
            _ = s.RestoreSourcesAsync(document.Project.Sources.Keys);
            _entitiesDirty = true;
            CheckMissingFiles(live);
        };
        live.RemoteEdit += delta =>
        {
            if (!delta.Sources.IsEmpty) _ = s.RestoreSourcesAsync(delta.Sources.Keys);
            if (UsesCustomFiles(delta)) CheckMissingFiles(live);
            _entitiesDirty = true;
        };
        // A custom mesh or sound placed in the session: the others get it from the next snapshot.
        live.LocalEdit += delta => { if (UsesCustomFiles(delta)) live.RequestSnapshot(); };
        live.SnapshotPublished += snapshot => { if (_assetsWanted) _ = FetchSessionFilesAsync(live); };
        s.RemoteLockOwner = id => live.LockedByOther(id)?.DisplayName;
        _liveSelectionKey = "";
        _filesFromSnapshot = null;
        _liveDirty = true;
        LiveChatList.Children.Clear();
        CheckMissingFiles(live);
        ShowTab("Live");
        UpdateLivePanel(force: true);
    }

    private static bool UsesCustomFiles(ProjectDelta delta)
        => delta.Added.Values.Any(a => a is not null && CustomMeshPaths.IsCustom(a.Mesh.Path))
        || delta.Elements.Values.Any(e => e?.Properties.Kind == ElementKind.CustomSound);

    // Custom meshes and sounds the session's world uses that this library lacks come from the
    // session's snapshot (the one who placed them uploads one).
    private void CheckMissingFiles(LiveProject live)
    {
        if (Session is not { } s) return;
        var (meshes, sounds) = ProjectPackage.Missing(s.Document.Project, s.CustomMeshes, s.CustomSounds);
        _assetsWanted = meshes.Count + sounds.Count + ProjectPackage.MissingDecals(s.Document.Project, s.CustomDecals).Count > 0;
        if (_assetsWanted) _ = FetchSessionFilesAsync(live);
    }

    // Each newer snapshot is read once, until nothing is missing. A snapshot published while one
    // is downloading is read right after it (a newer one may carry what the older one lacked).
    private async Task FetchSessionFilesAsync(LiveProject live)
    {
        if (_fetchingAssets || Session is not { } s) return;
        _fetchingAssets = true;
        try
        {
            while (_live == live && live.Session.Snapshot is { } snapshot && snapshot.Id != _filesFromSnapshot && MissingFiles(s) > 0)
            {
                _filesFromSnapshot = snapshot.Id;
                try
                {
                    var (read, content) = await live.Client.DownloadSnapshotAsync(live.Code);
                    _filesFromSnapshot = read.Id;
                    await Task.Run(() => Packager.Unpack(content));
                }
                catch (Exception ex) when (IsCollabFailure(ex)) { break; }
            }
        }
        finally { _fetchingAssets = false; }
        int missing = MissingFiles(s);
        _assetsWanted = missing > 0;
        if (_assetsWanted) s.Status = Loc.F("Waiting for {0} custom file{1} of the session (they come with its next snapshot).", missing, (missing == 1 ? "" : "s"));
    }

    private static int MissingFiles(StudioSession s)
    {
        var (meshes, sounds) = ProjectPackage.Missing(s.Document.Project, s.CustomMeshes, s.CustomSounds);
        return meshes.Count + sounds.Count + ProjectPackage.MissingDecals(s.Document.Project, s.CustomDecals).Count;
    }

    private void DetachLive(LiveProject live, string? reason = null)
    {
        if (_live != live) return;
        _live = null;
        _liveEndedReason = reason ?? live.EndReason;
        if (Session is { } s) { s.RemoteLockOwner = null; s.Document.ReadOnly = false; }
        if (_viewport is { } v) v.Collab.Peers = [];
        _ = live.DisposeAsync().AsTask();
        _world = null; _worldSavedSeq = -1;
        _liveDirty = true;
        UpdateLivePanel(force: true);
    }

    private async void OnLeaveSession(object sender, RoutedEventArgs e)
    {
        if (_live is not { } live) return;
        if (InWorld)
        {
            bool alone = live.Participants.Count(p => p.AccountId != live.You?.AccountId) == 0;
            var leave = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = Loc.F("Leave “{0}”?", _world!.Name),
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
                    Text = (live.CanEdit ? Loc.T("Your work is saved into the shared world. ") : "")
                        + (alone ? Loc.T("You are the last one here: the live session ends, and whoever opens the world next continues from what was saved.")
                                 : Loc.T("The others keep working on it; open it again to rejoin them."))
                },
                PrimaryButtonText = Loc.T("Leave"), CloseButtonText = Loc.T("Stay"), DefaultButton = ContentDialogButton.Primary
            };
            if (await leave.ShowAsync() == ContentDialogResult.Primary) await LeaveWorldAsync(live);
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = live.IsOwner ? Loc.T("Leave your session?") : Loc.F("Leave “{0}”?", live.Session.Name),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
                Text = live.IsOwner
                    ? Loc.T("You own this session. Disconnecting keeps it open for the others (it closes after a long idle time); closing it ends it for everyone. Either way you keep the world as it is now.")
                    : Loc.T("You keep the world as it is now. Joining again will ask for the password.")
            },
            PrimaryButtonText = live.IsOwner ? Loc.T("Disconnect") : Loc.T("Leave"),
            SecondaryButtonText = live.IsOwner ? Loc.T("Close for everyone") : "",
            CloseButtonText = Loc.T("Stay"), DefaultButton = ContentDialogButton.Close
        };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary: await LeaveLiveAsync(live, close: false); break;
            case ContentDialogResult.Secondary: await LeaveLiveAsync(live, close: true); break;
        }
    }

    private async void OnCloseSession(object sender, RoutedEventArgs e)
    {
        if (_live is not { IsOwner: true } live) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Close the session for everyone?"),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Text = Loc.T("Everyone is disconnected and keeps the world as it is now. The session's code stops working.") },
            PrimaryButtonText = Loc.T("Close the session"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await LeaveLiveAsync(live, close: true);
    }

    internal async Task LeaveLiveAsync(LiveProject live, bool close)
    {
        if (InWorld && _live == live) { await LeaveWorldAsync(live); return; }
        try
        {
            if (close) await live.CloseAsync();
            else await live.LeaveAsync();
        }
        catch (Exception ex) when (IsCollabFailure(ex)) { if (Session is { } s) s.Error = CollabError(ex); }
        DetachLive(live, close ? Loc.T("You closed the session.") : live.IsOwner ? Loc.T("You disconnected from your session (it stays open for the others).") : Loc.T("You left the session."));
    }

    // Leaving before opening another world (New, Open, Join): asked once.
    private async Task<bool> ConfirmLeaveLive()
    {
        if (_live is not { State: not LiveState.Ended } live) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Leave the live session?"),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
                Text = InWorld
                    ? Loc.F("Opening another world leaves “{0}”{1}. Open it again from the cloud to come back.", _world!.Name, (live.CanEdit ? Loc.T(" (your work is saved into it)") : ""))
                    : Loc.F("Opening another world disconnects you from “{0}”. You can join it again with its code.", live.Session.Name)
            },
            PrimaryButtonText = Loc.T("Leave and continue"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        await LeaveLiveAsync(live, close: false);
        return true;
    }

    // Signing out disconnects (the account stays a member: signing in again, it rejoins without the password).
    private void EndLiveOnSignOut()
    {
        if (_live is { } live) DetachLive(live, Loc.T("Signed out of Open//77: disconnected from the session."));
    }

    private async Task DisposeLiveAsync()
    {
        if (_live is not { } live) return;
        // Closing Studio in a shared world: the work goes into the world first (briefly).
        if (InWorld) await Task.WhenAny(LeaveWorldAsync(live), Task.Delay(TimeSpan.FromSeconds(20)));
        if (_live is null) return;
        _live = null;
        try { await live.DisposeAsync(); } catch (Exception ex) when (IsCollabFailure(ex) || ex is OperationCanceledException) { }
    }

    private async void OnCopyInvitation(object sender, RoutedEventArgs e)
    {
        if (_live is not { } live || Session is not { } s) return;
        if (InWorld && Collab() is { } client)
        {
            if (OwnsWorld) await ShowShareDialogAsync(client, _world!);
            else await ShowMembersDialogAsync(client, _world!);
            return;
        }
        var package = new DataPackage();
        package.SetText(Loc.F("Join my NCWE Studio session “{0}”: Collaborate ▸ Join a session, code {1} (I'll send you the password).", live.Session.Name, live.Code));
        Clipboard.SetContent(package);
        s.Status = Loc.F("Invitation copied: session {0}. Send the password separately.", live.Code);
    }

    // --- Every UI tick (10 Hz) -------------------------------------------------------------------------

    private void TickLive(StudioSession s, ViewportController v)
    {
        if (_live is not { } live) { if (_liveDirty) UpdateLivePanel(); return; }
        if (live.State == LiveState.Ended) { DetachLive(live); return; }
        // Locks follow the selection.
        var selection = LiveSelection(s);
        string key = string.Join('\n', selection);
        if (key != _liveSelectionKey) { _liveSelectionKey = key; live.SetSelection(selection); }
        var eye = Coordinates.ViewToGame(s.Camera.Eye);
        var forward = Coordinates.ViewToGame(s.Camera.Forward);
        live.UpdatePresence(new PresenceData([eye.X, eye.Y, eye.Z, forward.X, forward.Y, forward.Z], s.Camera.Yaw, s.Camera.Pitch, [.. selection.Take(64)], v.Tool.ToString()));
        v.Collab.Peers = Peers(live, s);
        TickWorld(live);
        UpdateLivePanel();
    }

    private static List<string> LiveSelection(StudioSession s)
    {
        var ids = new List<string>(s.SelectedIds);
        if (s.SelectedRoadId is { } road) ids.Add(road);
        if (s.SelectedElevatorId is { } elevator) ids.Add(elevator);
        return ids;
    }

    private static List<CollabPeer> Peers(LiveProject live, StudioSession s)
    {
        var peers = new List<CollabPeer>();
        foreach (var p in live.Participants)
        {
            if (p.ParticipantId == live.You?.ParticipantId || p.AccountId == live.You?.AccountId) continue;
            Vector3? eye = null; Vector3 forward = Vector3.UnitY; string? tool = null;
            var boxes = new List<Bounds3>();
            if (live.Presence.TryGetValue(p.ParticipantId, out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("camera", out var camera) && camera.ValueKind == JsonValueKind.Array && camera.GetArrayLength() >= 6)
                {
                    var c = camera.EnumerateArray().Take(6).Select(x => x.TryGetSingle(out float f) && float.IsFinite(f) ? f : 0).ToArray();
                    eye = new(c[0], c[1], c[2]);
                    forward = new(c[3], c[4], c[5]);
                }
                if (data.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String) tool = t.GetString();
                if (data.TryGetProperty("selection", out var selection) && selection.ValueKind == JsonValueKind.Array)
                    foreach (var id in selection.EnumerateArray().Take(32))
                    {
                        if (id.GetString() is not { } objectId || s.Document.TryGetObject(objectId) is not { } item) continue;
                        if (s.GameBounds(item) is { } box) boxes.Add(box);
                        else boxes.Add(new(item.Transform.Position - new Vector3(.5f), item.Transform.Position + new Vector3(.5f)));
                    }
            }
            peers.Add(new CollabPeer(p.ParticipantId, p.DisplayName, CollabPresence.ColorOf(p.AccountId.ToString("N")), eye, forward, boxes, tool, p.Role == SessionRoles.Viewer));
        }
        return peers;
    }

    // --- The Live tab and the title bar ------------------------------------------------------------

    private void UpdateLivePanel(bool force = false)
    {
        if (!_liveDirty && !force) return;
        _liveDirty = false;
        var live = _live;
        // Title bar.
        CollabDot.Visibility = live is null ? Visibility.Collapsed : Visibility.Visible;
        CollabDot.Fill = new SolidColorBrush(StateColor(live?.State));
        CollabLabel.Text = live is null ? Loc.T("Collaborate") : InWorld ? $"{_world!.Name} · {live.Participants.Count}" : $"{live.Code} · {live.Participants.Count}";
        ToolTipService.SetToolTip(CollabButton, live is null ? Loc.T("Build this world together, live (Open//77 account)") : Loc.F("Live session “{0}”: {1}", live.Session.Name, StateText(live)));
        CopyInvitationMenu.IsEnabled = LeaveSessionMenu.IsEnabled = live is not null;
        if (_tab != "Live" && !force) return;
        // Panel.
        LiveIdle.Visibility = live is null ? Visibility.Visible : Visibility.Collapsed;
        LiveActive.Visibility = live is null ? Visibility.Collapsed : Visibility.Visible;
        LivePeopleSection.Visibility = LiveChatSection.Visibility = LiveSaveSection.Visibility = live is null ? Visibility.Collapsed : Visibility.Visible;
        LiveSettingsSection.Visibility = live is { IsOwner: true } && !InWorld ? Visibility.Visible : Visibility.Collapsed;
        LiveStateDot.Fill = new SolidColorBrush(StateColor(live?.State));
        if (live is null)
        {
            LiveTitle.Text = Loc.T("Live collaboration");
            LiveSubtitle.Text = _liveEndedReason is { } reason
                ? Loc.F("{0} You keep the world as it is now.", reason)
                : Loc.T("Build this world together, live: everyone sees the others' edits, cameras and selections. Sessions run on your Open//77 account.");
            LiveAccountHint.Text = _account is { } a ? Loc.F("As {0}.", a.DisplayName) : CollabDevToken is not null ? Loc.T("Developer account (local server).") : Loc.T("Requires an Open//77 account: you will be asked to sign in.");
            return;
        }
        string onTheWay = live.PendingEdits > 0 ? Loc.F(" · {0} edit{1} on the way", live.PendingEdits, (live.PendingEdits == 1 ? "" : "s")) : "";
        if (InWorld)
        {
            var world = _world!;
            LiveTitle.Text = world.Name;
            LiveSubtitle.Text = Loc.F("Shared world of {0} · {1}{2}{3}", (OwnsWorld ? Loc.T("yours") : world.Owner?.DisplayName ?? Loc.T("someone")), StateText(live), (_worldSavedSeq >= 0 && live.Seq != _worldSavedSeq ? Loc.T(" · unsaved work (autosaved every 10 min)") : Loc.T(" · saved")), onTheWay);
            LiveCodeText.Text = OwnsWorld ? Loc.T("Invite people…") : Loc.T("Who works on it");
            LiveCodeText.FontFamily = new FontFamily("Segoe UI Variable Text,Segoe UI");
            LiveCodeText.CharacterSpacing = 0; LiveCodeText.FontSize = 13;
            LiveCloseButton.Visibility = Visibility.Collapsed;
            LiveLeaveButton.Content = Loc.T("Leave");
            LiveSaveCloudText.Text = Loc.T("Save into the shared world (Ctrl+S)");
            LiveRoleText.Text = live.CanEdit
                ? Loc.T("Whoever opens this world joins you here. What you select is locked for the others while you work on it; the last one leaving ends the session, and the world keeps everything.")
                : Loc.T("You watch this world and chat; its owner can let you edit.");
        }
        else
        {
            LiveTitle.Text = live.Session.Name;
            LiveSubtitle.Text = Loc.F("{0} · owned by {1}{2}", StateText(live), live.Session.Owner.DisplayName, onTheWay);
            LiveCodeText.Text = live.Code;
            LiveCodeText.FontFamily = (FontFamily)Application.Current.Resources["MonoFont"];
            LiveCodeText.CharacterSpacing = 60; LiveCodeText.FontSize = 15;
            LiveCloseButton.Visibility = live.IsOwner ? Visibility.Visible : Visibility.Collapsed;
            LiveLeaveButton.Content = live.IsOwner ? Loc.T("Disconnect") : Loc.T("Leave");
            LiveSaveCloudText.Text = Loc.T("Save to my cloud");
            LiveRoleText.Text = live.Role switch
            {
                SessionRoles.Owner => Loc.T("You own this session: you choose who edits, and can remove people."),
                SessionRoles.Editor => Loc.T("You can edit. What you select is locked for the others while you work on it."),
                _ => Loc.T("You are a viewer: you watch and chat. The owner can let you edit.")
            };
        }
        FillPeople(live);
        if (live.IsOwner)
        {
            _syncingLive = true;
            LiveLockedSwitch.IsOn = live.Session.Locked;
            LiveDefaultRoleBox.SelectedIndex = live.Session.DefaultRole == SessionRoles.Viewer ? 1 : 0;
            if (LiveMaxBox.FocusState == FocusState.Unfocused) LiveMaxBox.Value = live.Session.MaxParticipants;
            _syncingLive = false;
        }
    }

    private static Windows.UI.Color StateColor(LiveState? state) => state switch
    {
        LiveState.Live => Windows.UI.Color.FromArgb(255, 0x3D, 0xDC, 0x84),
        LiveState.Connecting or LiveState.Reconnecting => Windows.UI.Color.FromArgb(255, 0xFF, 0xB8, 0x4D),
        _ => Windows.UI.Color.FromArgb(255, 0x6E, 0x72, 0x7A)
    };

    private static string StateText(LiveProject live) => live.State switch
    {
        LiveState.Live => live.Participants.Count == 1 ? Loc.T("Live, only you for now") : Loc.F("Live with {0} other{1}", live.Participants.Count - 1, (live.Participants.Count == 2 ? "" : "s")),
        LiveState.Connecting => Loc.T("Connecting…"),
        LiveState.Reconnecting => Loc.T("Connection lost, reconnecting…"),
        _ => live.EndReason ?? Loc.T("Ended")
    };

    private string _peopleKey = "";

    private void FillPeople(LiveProject live)
    {
        var people = live.Participants.OrderBy(p => p.Role == SessionRoles.Owner ? 0 : p.Role == SessionRoles.Editor ? 1 : 2).ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        string key = string.Join('|', people.Select(p => $"{p.ParticipantId}:{p.Role}")) + "|" + live.Role + "|" + InWorld + OwnsWorld;
        if (key == _peopleKey && LivePeople.Children.Count > 0) return;
        _peopleKey = key;
        LivePeople.Children.Clear();
        foreach (var p in people)
        {
            bool you = p.AccountId == live.You?.AccountId;
            var grid = new Grid { ColumnSpacing = 9, Padding = new(2, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var color = CollabPresence.ColorOf(p.AccountId.ToString("N"));
            var badge = new Border
            {
                Width = 24, Height = 24, CornerRadius = new(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(color.X * 255), (byte)(color.Y * 255), (byte)(color.Z * 255))),
                Child = new TextBlock { Text = p.DisplayName.Length > 0 ? p.DisplayName[..1].ToUpperInvariant() : "?", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 6, 18, 26)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            grid.Children.Add(badge);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            text.Children.Add(new TextBlock { Text = p.DisplayName + (you ? Loc.T(" (you)") : ""), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 });
            // A shared world: the world's roles (whoever started its session is just one of its editors).
            string roleName = InWorld
                ? (p.AccountId == _world!.Owner?.AccountId ? Loc.T("Owner of the world") : p.Role == SessionRoles.Viewer ? Loc.T("Watches") : Loc.T("Edits"))
                : p.Role switch { SessionRoles.Owner => Loc.T("Owner"), SessionRoles.Editor => Loc.T("Editor"), _ => Loc.T("Viewer") };
            text.Children.Add(new TextBlock { Text = roleName, Style = (Style)Application.Current.Resources["HintStyle"] });
            grid.Children.Add(text);
            if (!you)
            {
                var more = new Button { Style = (Style)Application.Current.Resources["IconButtonStyle"], Content = "", Width = 30, Height = 30 };
                ToolTipService.SetToolTip(more, live.IsOwner ? Loc.F("Go to {0}'s view, roles, remove", p.DisplayName) : Loc.F("Go to {0}'s view", p.DisplayName));
                var menu = new MenuFlyout();
                var person = p;
                menu.Items.Add(Item(Loc.F("Go to {0}'s view", p.DisplayName), "", () => GoToPeer(live, person)));
                // A shared world: its owner manages its people (the world's roles and members).
                if (InWorld && OwnsWorld && Collab() is { } worldClient)
                {
                    var world = _world!;
                    menu.Items.Add(new MenuFlyoutSeparator());
                    if (p.Role == SessionRoles.Viewer) menu.Items.Add(Item(Loc.T("Let them edit"), "", () => _ = OwnerAction(live, () => worldClient.SetProjectRoleAsync(world.Id, person.AccountId, SessionRoles.Editor), Loc.F("{0} can edit now.", person.DisplayName))));
                    else menu.Items.Add(Item(Loc.T("Make them watch only"), "", () => _ = OwnerAction(live, () => worldClient.SetProjectRoleAsync(world.Id, person.AccountId, SessionRoles.Viewer), Loc.F("{0} watches now.", person.DisplayName))));
                    menu.Items.Add(Item(Loc.T("Remove from the shared world"), "", () => _ = OwnerAction(live, () => worldClient.RemoveProjectMemberAsync(world.Id, person.AccountId), Loc.F("{0} no longer works on this world.", person.DisplayName))));
                }
                else if (live.IsOwner && !InWorld)
                {
                    menu.Items.Add(new MenuFlyoutSeparator());
                    if (p.Role == SessionRoles.Viewer) menu.Items.Add(Item(Loc.T("Let them edit"), "", () => _ = OwnerAction(live, () => live.Client.SetRoleAsync(live.Code, person.AccountId, SessionRoles.Editor), Loc.F("{0} can edit now.", person.DisplayName))));
                    else if (p.Role == SessionRoles.Editor) menu.Items.Add(Item(Loc.T("Make them a viewer"), "", () => _ = OwnerAction(live, () => live.Client.SetRoleAsync(live.Code, person.AccountId, SessionRoles.Viewer), Loc.F("{0} watches now.", person.DisplayName))));
                    menu.Items.Add(Item(Loc.T("Give them the session"), "", () => _ = TransferAsync(live, person)));
                    menu.Items.Add(new MenuFlyoutSeparator());
                    menu.Items.Add(Item(Loc.T("Remove from the session"), "", () => _ = OwnerAction(live, () => live.Client.KickAsync(live.Code, person.AccountId), Loc.F("{0} was removed (they can join again with the password).", person.DisplayName))));
                    menu.Items.Add(Item(Loc.T("Ban from the session"), "", () => _ = OwnerAction(live, () => live.Client.KickAsync(live.Code, person.AccountId, ban: true), Loc.F("{0} was banned from the session.", person.DisplayName))));
                }
                more.Flyout = menu;
                Grid.SetColumn(more, 2);
                grid.Children.Add(more);
            }
            LivePeople.Children.Add(grid);
        }
    }

    private void GoToPeer(LiveProject live, ParticipantInfo person)
    {
        if (Session is not { } s || !live.Presence.TryGetValue(person.ParticipantId, out var data)) return;
        try
        {
            var camera = data.GetProperty("camera").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            float yaw = data.TryGetProperty("heading", out var h) ? h.GetSingle() : s.Camera.Yaw;
            float pitch = data.TryGetProperty("pitch", out var p) ? p.GetSingle() : s.Camera.Pitch;
            s.GoTo(new CameraBookmark(person.DisplayName, camera[0], camera[1], camera[2], yaw, pitch));
            s.Status = Loc.F("You see what {0} sees.", person.DisplayName);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException) { s.Status = Loc.F("{0} has not shared a view yet.", person.DisplayName); }
    }

    private async Task OwnerAction(LiveProject live, Func<Task> action, string done)
    {
        try { await action(); if (Session is { } s) s.Status = done; }
        catch (Exception ex) when (IsCollabFailure(ex)) { if (Session is { } s) s.Error = CollabError(ex); }
    }

    private async Task TransferAsync(LiveProject live, ParticipantInfo person)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.F("Give the session to {0}?", person.DisplayName),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Text = Loc.F("{0} becomes its owner (roles, removals, closing it). You stay in it as an editor.", person.DisplayName) },
            PrimaryButtonText = Loc.T("Give the session"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await OwnerAction(live, () => live.Client.TransferAsync(live.Code, person.AccountId), Loc.F("{0} owns the session now.", person.DisplayName));
    }

    // --- Owner settings --------------------------------------------------------------------------------

    private async void OnLiveLocked(object sender, RoutedEventArgs e)
    {
        if (_syncingLive || _live is not { IsOwner: true } live || LiveLockedSwitch.IsOn == live.Session.Locked) return;
        bool locked = LiveLockedSwitch.IsOn;
        await OwnerAction(live, () => live.Client.UpdateSessionAsync(live.Code, new UpdateSessionRequest(Locked: locked)), locked ? Loc.T("Session locked: nobody new can join.") : Loc.T("Session open to new members again."));
    }

    private async void OnLiveDefaultRole(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLive || _live is not { IsOwner: true } live || (LiveDefaultRoleBox.SelectedItem as ComboBoxItem)?.Tag is not string role || role == live.Session.DefaultRole) return;
        await OwnerAction(live, () => live.Client.UpdateSessionAsync(live.Code, new UpdateSessionRequest(DefaultRole: role)), role == SessionRoles.Viewer ? Loc.T("New members join as viewers.") : Loc.T("New members join as editors."));
    }

    private async void OnLiveMaxParticipants(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_syncingLive || _live is not { IsOwner: true } live || double.IsNaN(e.NewValue) || (int)e.NewValue == live.Session.MaxParticipants) return;
        int max = (int)e.NewValue;
        await OwnerAction(live, () => live.Client.UpdateSessionAsync(live.Code, new UpdateSessionRequest(MaxParticipants: max)), Loc.F("Up to {0} participants.", max));
    }

    private async void OnLivePassword(object sender, RoutedEventArgs e)
    {
        if (_live is not { IsOwner: true } live) return;
        var box = new PasswordBox { Header = Loc.T("New password (4 characters or more)"), MaxLength = 128 };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Change the session's password"), PrimaryButtonText = Loc.T("Change"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false,
            Content = new StackPanel
            {
                Spacing = 10, MinWidth = 340,
                Children = { new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionStyle"], Text = Loc.T("Members already in the session stay; new ones need the new password.") }, box }
            }
        };
        box.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Password.Length >= 4;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string password = box.Password;
        await OwnerAction(live, () => live.Client.UpdateSessionAsync(live.Code, new UpdateSessionRequest(Password: password)), Loc.T("Password changed."));
    }

    private async void OnLiveBans(object sender, RoutedEventArgs e)
    {
        if (_live is not { IsOwner: true } live || Session is not { } s) return;
        IReadOnlyList<BanInfo> bans;
        try { bans = await live.Client.BansAsync(live.Code); }
        catch (Exception ex) when (IsCollabFailure(ex)) { s.Error = CollabError(ex); return; }
        var list = new StackPanel { Spacing = 6, MinWidth = 360 };
        if (bans.Count == 0) list.Children.Add(new TextBlock { Text = Loc.T("Nobody is banned from this session."), Style = (Style)Application.Current.Resources["CaptionStyle"] });
        foreach (var ban in bans)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = ban.DisplayName + (ban.Reason is { Length: > 0 } r ? " · " + r : ""), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var unban = new Button { Content = Loc.T("Let back in"), FontSize = 12 };
            Grid.SetColumn(unban, 1);
            var account = ban.AccountId;
            unban.Click += async (_, _) =>
            {
                try { await live.Client.UnbanAsync(live.Code, account); unban.IsEnabled = false; unban.Content = Loc.T("Allowed"); }
                catch (Exception ex) when (IsCollabFailure(ex)) { unban.Content = Loc.T("Failed"); s.Error = CollabError(ex); }
            };
            row.Children.Add(unban);
            list.Children.Add(row);
        }
        await new ContentDialog { XamlRoot = Root.XamlRoot, Title = Loc.T("Banned accounts"), Content = list, CloseButtonText = Loc.T("Close") }.ShowAsync();
    }

    // --- Chat ------------------------------------------------------------------------------------------

    private void OnLiveChatKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { SendChat(); e.Handled = true; }
    }

    private void OnLiveChatSend(object sender, RoutedEventArgs e) => SendChat();

    private void SendChat()
    {
        if (_live is not { State: LiveState.Live } live || LiveChatBox.Text.Trim() is not { Length: > 0 } text) return;
        live.SendChat(text);
        LiveChatBox.Text = "";
    }

    // A message (name null: a notice of the session, in grey).
    private void AddChatLine(string? name, string text, DateTimeOffset at, Vector4? color)
    {
        var line = new StackPanel { Spacing = 1 };
        if (name is not null)
        {
            var c = color ?? new Vector4(.8f, .8f, .8f, 1);
            line.Children.Add(new TextBlock
            {
                Text = $"{name} · {at.ToLocalTime():HH:mm}", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255)))
            });
            line.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13 });
        }
        else line.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, FontStyle = Windows.UI.Text.FontStyle.Italic, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"] });
        LiveChatList.Children.Add(line);
        while (LiveChatList.Children.Count > 300) LiveChatList.Children.RemoveAt(0);
        LiveChatEmpty.Visibility = Visibility.Collapsed;
        DispatcherQueue.Post(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => LiveChatScroll.ChangeView(null, LiveChatScroll.ScrollableHeight, null, true));
    }

    // --- Automation (tests) -----------------------------------------------------------------------------

    internal async Task<object> CollabUi(JsonElement c)
    {
        string action = c.TryGetProperty("action", out var a) && a.GetString() is { } act ? act : "state";
        string? Str(string key) => c.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (action)
        {
            case "start":
                var started = await HostSessionAsync(Str("name") ?? Session?.Document.Project.Name ?? "Session", Str("password") ?? throw new ArgumentException("password is required."),
                    c.TryGetProperty("max", out var m) ? m.GetInt32() : 8, Str("role") ?? SessionRoles.Editor);
                if (started is not null) return new { ok = false, error = started };
                break;
            case "join":
                var joined = await JoinSessionAsync(Str("code") ?? throw new ArgumentException("code is required."), Str("password") ?? "");
                if (joined is not null) return new { ok = false, error = joined };
                break;
            case "leave": if (_live is { } leaving) await LeaveLiveAsync(leaving, close: false); break;
            case "close": if (_live is { } closing) await LeaveLiveAsync(closing, close: true); break;
            case "chat": if (_live is { } chatting) chatting.SendChat(Str("text") ?? ""); await Task.Delay(300); break;
            case "role":
                if (_live is { } owner && owner.Participants.FirstOrDefault(p => p.DisplayName == Str("name")) is { } who)
                    await owner.Client.SetRoleAsync(owner.Code, who.AccountId, Str("role") ?? SessionRoles.Viewer);
                await Task.Delay(400);
                break;
            case "kick":
                if (_live is { } host && host.Participants.FirstOrDefault(p => p.DisplayName == Str("name")) is { } kicked)
                    await host.Client.KickAsync(host.Code, kicked.AccountId, c.TryGetProperty("ban", out var b) && b.GetBoolean());
                await Task.Delay(400);
                break;
            case "snapshot": if (_live is { } snap) await snap.UploadSnapshotAsync(); break;
            case "state": break;
            default: throw new ArgumentException("Unknown collab action (state, start, join, leave, close, chat, role, kick, snapshot).");
        }
        var live = _live;
        return new
        {
            ok = true, service = CollabService.AbsoluteUri, live = live is not null,
            code = live?.Code, name = live?.Session.Name, state = live?.State.ToString(), role = live?.Role, seq = live?.Seq, pending = live?.PendingEdits,
            readOnly = Session?.Document.ReadOnly,
            participants = live?.Participants.Select(p => new { p.DisplayName, p.Role, p.ParticipantId }),
            locks = live?.Locks.Values.Select(l => new { l.Id, by = live.LockedByOther(l.Id)?.DisplayName ?? "you" }),
            peers = _viewport?.Collab.Peers.Select(p => new { p.Name, eye = p.Eye is { } e ? new[] { e.X, e.Y, e.Z } : null, selected = p.Selection.Count, p.Tool }),
            chat = live?.Chat.TakeLast(20).Select(m => $"{m.DisplayName}: {m.Text}"),
            missingFiles = _assetsWanted, ended = _liveEndedReason,
            objects = Session?.Document.Project.Changes.Added.Count
        };
    }
}
