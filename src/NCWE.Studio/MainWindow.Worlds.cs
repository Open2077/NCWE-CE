// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NCWE.Collab;
using NCWE.Collab.Client;
using Windows.ApplicationModel.DataTransfer;

namespace NCWE.Studio;

// Shared worlds: a world in its owner's cloud that the people they invite (an invite code) work on
// too, whenever they like. Opening one always goes through its live session: whoever is working
// on it is joined (no password, the members are the world's), or a session starts from its newest
// saved version and the next ones to open it join. The session is saved into the world (Ctrl+S, a
// regular autosave, and when someone leaves); the last one leaving closes it, so the next opening
// continues from what was saved. Limits (shared worlds per account, collaborators per world) come
// from the accounts' plans.
public sealed partial class MainWindow
{
    private const double WorldAutosaveSeconds = 600;
    private CloudProjectInfo? _world;         // the shared world of the current live session
    private long _worldSavedSeq = -1;
    private double _worldSavedAt;
    private bool _savingWorld;

    private bool InWorld => _live is { WorldId: not null } && _world is not null;
    private bool OwnsWorld => _world is { YourRole: SessionRoles.Owner };

    // --- Opening ----------------------------------------------------------------------------------

    private async void OnJoinSharedWorld(object sender, RoutedEventArgs e)
    {
        if (Session is null || !await RequireAccountAsync(Loc.T("Shared worlds")) || Collab() is not { } client) return;
        var code = new TextBox { Header = Loc.T("Invite code"), PlaceholderText = "ABCD-EFGH", MaxLength = 12, CharacterCasing = CharacterCasing.Upper, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"] };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["DangerBrush"], Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionStyle"],
            Text = Loc.T("The code its owner gave you. The world then stays among yours (File ▸ Open from the cloud): open it whenever you like, with the others or after them.")
        });
        panel.Children.Add(code); panel.Children.Add(error);
        CloudProjectInfo? joined = null;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Join a shared world"), Content = panel,
            PrimaryButtonText = Loc.T("Join and open"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false
        };
        code.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = code.Text.Count(char.IsLetterOrDigit) >= 8;
        dialog.PrimaryButtonClick += async (d, args) =>
        {
            var wait = args.GetDeferral();
            try { joined = await client.JoinSharedAsync(code.Text.Trim()); }
            catch (Exception ex) when (IsCollabFailure(ex))
            {
                error.Text = ex is CollabApiException { Status: 404 } ? Loc.T("No shared world has this code (it may have expired): ask for a new one.") : CollabError(ex);
                error.Visibility = Visibility.Visible; args.Cancel = true;
            }
            wait.Complete();
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || joined is null) return;
        await OpenSharedWorldAsync(joined);
    }

    // Opens a shared world: joins whoever is working on it, or starts its session from its newest
    // version. Null when it opened, else why not.
    internal async Task<string?> OpenSharedWorldAsync(CloudProjectInfo world)
    {
        if (Session is not { } s) return Loc.T("The editor is not ready yet.");
        if (!await RequireAccountAsync(Loc.T("Shared worlds")) || Collab() is not { } client) return Loc.T("Sign in to Open//77 to open shared worlds.");
        if (_live is { } current && current.WorldId == world.Id) { ShowTab("Live"); return null; }
        if (!await ConfirmDiscardChanges()) return Loc.T("Cancelled.");
        if (s.Index is null) return Loc.T("Open the game first: the world is built on its archives.");
        try
        {
            s.Status = Loc.F("Opening “{0}”…", world.Name);
            ProjectLiveResponse opened;
            try { opened = await client.OpenProjectLiveAsync(world.Id); }
            catch (CollabApiException ex) when (ex.Code == CollabErrors.NotLive)
            {
                // A viewer, nobody working on it: the newest saved version, as a copy to look at.
                return await OpenCloudVersionAsync(client, world, null) ? null : s.Error;
            }
            var (live, project, baseSeq) = await LiveProject.EnterAsync(client, opened.Session, Packager, Post, RebuildDocument);
            if (!await s.OpenProjectDataAsync(project, Loc.F("Opened the shared world “{0}”.", world.Name)))
            {
                await live.DisposeAsync();
                return s.Error.Length > 0 ? s.Error : Loc.T("The world could not be opened.");
            }
            live.Start(s.Document, baseSeq);
            _cloudProjectOf = null;
            _world = world;
            _worldSavedSeq = baseSeq; _worldSavedAt = Now;
            Attach(live);
            s.Status = opened.Started
                ? Loc.F("You are working on “{0}”: whoever opens it now joins you, live.", world.Name)
                : Loc.F("You joined {0} on “{1}”, live.", opened.Session.Owner.DisplayName, world.Name);
            UpdateProjectPanel(); _entitiesDirty = true;
            return null;
        }
        catch (Exception ex) when (IsCollabFailure(ex))
        {
            s.Status = Loc.T("Ready");
            s.Error = Loc.F("“{0}” could not be opened: {1}", world.Name, CollabError(ex));
            return s.Error;
        }
    }

    private static double Now => Environment.TickCount64 / 1000.0;

    // --- Saving -----------------------------------------------------------------------------------

    // The session as it is now becomes the world's newest version (nothing new: nothing happens).
    internal async Task<CloudVersionInfo?> SaveWorldAsync(bool automatic)
    {
        if (_live is not { WorldId: not null } live || Session is not { } s || _savingWorld) return null;
        if (!live.CanEdit) { if (!automatic) s.Status = Loc.T("You can only look at this world: its owner made you a viewer."); return null; }
        _savingWorld = true;
        try
        {
            if (!automatic) s.Status = Loc.T("Saving into the shared world…");
            var snapshot = await live.SnapshotNowAsync(TimeSpan.FromSeconds(15));
            if (snapshot is null)
            {
                if (!automatic) s.Error = Loc.T("The world could not be saved now (edits still on their way): try again in a moment.");
                return null;
            }
            if (snapshot.Seq == _worldSavedSeq && _worldSavedSeq >= 0)
            {
                if (!automatic) s.Status = Loc.T("Nothing new to save since the last version.");
                _worldSavedAt = Now;
                return null;
            }
            var saved = await live.Client.SaveSnapshotToCloudAsync(live.Code, new SaveSnapshotRequest(Note: automatic ? "Autosave" : null));
            _worldSavedSeq = snapshot.Seq; _worldSavedAt = Now;
            if (live.Seq == snapshot.Seq) s.Document.MarkSaved();
            s.Status = Loc.F("{0} into “{1}”: version {2}.", (automatic ? Loc.T("Autosaved") : Loc.T("Saved")), _world?.Name ?? live.Session.Name, saved.Version.Version);
            return saved.Version;
        }
        catch (Exception ex) when (IsCollabFailure(ex))
        {
            if (!automatic) s.Error = Loc.F("The shared world could not be saved: {0}", CollabError(ex));
            return null;
        }
        finally { _savingWorld = false; }
    }

    // Every UI tick: the keeper of the session's snapshots saves the world now and then.
    private void TickWorld(LiveProject live)
    {
        if (!InWorld || !live.CanEdit || live.State != LiveState.Live || _savingWorld) return;
        if (Now - _worldSavedAt >= WorldAutosaveSeconds && live.Seq != _worldSavedSeq && live.IsSnapshotKeeper) _ = SaveWorldAsync(automatic: true);
    }

    // Leaving a shared world's session: the work is saved into the world; the last one leaving
    // closes the session (the next opening starts from what was saved).
    private async Task LeaveWorldAsync(LiveProject live)
    {
        if (live.CanEdit) await SaveWorldAsync(automatic: true);
        if (live.Seq == _worldSavedSeq || !live.CanEdit) Session?.Document.MarkSaved();   // the world has it
        bool alone = live.Participants.Count(p => p.AccountId != live.You?.AccountId) == 0;
        string? name = _world?.Name;
        try
        {
            if (alone && live.CanEdit) await live.CloseAsync();
        }
        catch (Exception ex) when (IsCollabFailure(ex)) { }
        DetachLive(live, alone ? Loc.F("You closed “{0}” (saved into the shared world).", name) : Loc.F("You left “{0}”: the others keep working on it.", name));
        _world = null; _worldSavedSeq = -1;
    }

    // --- Sharing ------------------------------------------------------------------------------------

    // Collaborate ▸ Share this world: the world goes to the cloud (if it is not there), an invite
    // code is made, and the owner can start working on it live.
    private async void OnShareWorld(object sender, RoutedEventArgs e)
    {
        if (Session is not { } s || !await RequireAccountAsync(Loc.T("Shared worlds")) || Collab() is not { } client) return;
        try
        {
            CloudProjectInfo? world = _world;
            if (world is null)
            {
                if (_live is not null) { s.Status = Loc.T("This live session is not a shared world: save it to your cloud first."); return; }
                var me = await MeAsync(client);
                if (!_settings.CloudProjects.TryGetValue(CloudKey(me, s.Document.Project), out var cloudId) || !CloudLinked)
                {
                    if (await SaveToCloudAsync(note: null, interactive: true) is null) return;
                    cloudId = _settings.CloudProjects[CloudKey(me, s.Document.Project)];
                }
                else if (s.Document.IsDirty && await SaveToCloudAsync(note: null, interactive: false) is null) return;
                world = (await client.GetProjectAsync(cloudId)).Project;
            }
            if (world.YourRole != SessionRoles.Owner) { await ShowMembersDialogAsync(client, world); return; }
            bool openLive = await ShowShareDialogAsync(client, world);
            if (openLive && _live is null) await OpenSharedWorldAsync((await client.GetProjectAsync(world.Id)).Project);
        }
        catch (Exception ex) when (IsCollabFailure(ex)) { s.Error = Loc.F("The world could not be shared: {0}", CollabError(ex)); }
    }

    // The owner's sharing panel: the invite code (new, copy, stop) and the members. True: the owner
    // wants to work on it live now.
    private async Task<bool> ShowShareDialogAsync(CollabClient client, CloudProjectInfo world)
    {
        var content = SharePanel(client, world, closeAfterStop: null);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.F("Share “{0}”", world.Name), Content = content,
            PrimaryButtonText = _live is null ? Loc.T("Work on it live now") : "", CloseButtonText = Loc.T("Done"), DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogMaxWidth"] = 560.0;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowMembersDialogAsync(CollabClient client, CloudProjectInfo world)
    {
        var list = new StackPanel { Spacing = 4, MinWidth = 360 };
        list.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionStyle"],
            Text = Loc.F("{0} shares this world: ask them for an invite code for someone new.", world.Owner?.DisplayName ?? Loc.T("Its owner"))
        });
        await FillMembers(client, world, list, manage: false);
        await new ContentDialog { XamlRoot = Root.XamlRoot, Title = Loc.F("Who works on “{0}”", world.Name), Content = list, CloseButtonText = Loc.T("Close") }.ShowAsync();
    }

    // The sharing controls (also in the cloud window's flyout): invite code and members.
    private FrameworkElement SharePanel(CollabClient client, CloudProjectInfo world, Action? closeAfterStop)
    {
        var panel = new StackPanel { Spacing = 10, Width = 440 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionStyle"],
            Text = Loc.T("Give the invite code to the people who should work on this world (Collaborate ▸ Join a shared world). Whoever opens it joins the others working on it, live, or continues from the last saved version.")
        });
        var codeText = new TextBlock { FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 80, VerticalAlignment = VerticalAlignment.Center };
        var expiry = new TextBlock { Style = (Style)Application.Current.Resources["HintStyle"] };
        var role = new ComboBox { MinWidth = 150 };
        role.Items.Add(new ComboBoxItem { Content = Loc.T("They edit"), Tag = SessionRoles.Editor });
        role.Items.Add(new ComboBoxItem { Content = Loc.T("They watch"), Tag = SessionRoles.Viewer });
        role.SelectedIndex = 0;
        var copy = new Button { Content = Loc.T("Copy"), IsEnabled = false };
        var fresh = new Button { Content = Loc.T("New code") };
        var stop = new Button { Content = Loc.T("Stop inviting"), IsEnabled = false };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["DangerBrush"], Visibility = Visibility.Collapsed };
        var members = new StackPanel { Spacing = 2 };
        void Show(ProjectInviteInfo? invite)
        {
            codeText.Text = invite?.Code ?? Loc.T("No code yet");
            codeText.Opacity = invite is null ? .5 : 1;
            expiry.Text = invite is null ? Loc.T("Make one to invite people.") : Loc.F("Valid until {0:d MMM HH:mm} · they will {1}", invite.ExpiresAt.ToLocalTime(), (invite.Role == SessionRoles.Viewer ? Loc.T("watch") : Loc.T("edit")));
            copy.IsEnabled = stop.IsEnabled = invite is not null;
            copy.Tag = invite?.Code;
        }
        void Fail(Exception ex) { error.Text = CollabError(ex); error.Visibility = Visibility.Visible; }
        copy.Click += (_, _) =>
        {
            if (copy.Tag is not string code) return;
            var package = new DataPackage();
            package.SetText(Loc.F("Join my NCWE world “{0}”: Collaborate ▸ Join a shared world, code {1}", world.Name, code));
            Clipboard.SetContent(package);
            copy.Content = Loc.T("Copied");
        };
        fresh.Click += async (_, _) =>
        {
            try { error.Visibility = Visibility.Collapsed; Show(await client.CreateInviteAsync(world.Id, (role.SelectedItem as ComboBoxItem)?.Tag as string)); copy.Content = Loc.T("Copy"); }
            catch (Exception ex) when (IsCollabFailure(ex)) { Fail(ex); }
        };
        stop.Click += async (_, _) =>
        {
            try { await client.RevokeInviteAsync(world.Id); Show(null); closeAfterStop?.Invoke(); }
            catch (Exception ex) when (IsCollabFailure(ex)) { Fail(ex); }
        };
        var codeRow = new Grid { ColumnSpacing = 8 };
        codeRow.ColumnDefinitions.Add(new ColumnDefinition());
        codeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        codeRow.Children.Add(codeText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { copy, fresh } };
        Grid.SetColumn(buttons, 1);
        codeRow.Children.Add(buttons);
        panel.Children.Add(codeRow);
        panel.Children.Add(expiry);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { role, stop } });
        panel.Children.Add(error);
        panel.Children.Add(new TextBlock { Text = Loc.T("People"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new(0, 6, 0, 0) });
        panel.Children.Add(members);
        _ = LoadAsync();
        return panel;

        async Task LoadAsync()
        {
            try
            {
                var details = await client.GetProjectAsync(world.Id);
                // The first code is made at once (the owner opened this to share).
                Show(details.Invite ?? await client.CreateInviteAsync(world.Id));
                await FillMembers(client, world, members, manage: true);
            }
            catch (Exception ex) when (IsCollabFailure(ex)) { Fail(ex); }
        }
    }

    // The world's people; the owner may change roles and remove members.
    private async Task FillMembers(CollabClient client, CloudProjectInfo world, StackPanel list, bool manage)
    {
        IReadOnlyList<ProjectMemberInfo> people;
        try { people = await client.ProjectMembersAsync(world.Id); }
        catch (Exception ex) when (IsCollabFailure(ex)) { list.Children.Add(new TextBlock { Text = CollabError(ex), TextWrapping = TextWrapping.Wrap }); return; }
        int keep = list.Children.Count > 0 && list.Children[0] is TextBlock { Tag: null } intro && !manage ? 1 : 0;
        while (list.Children.Count > keep) list.Children.RemoveAt(list.Children.Count - 1);
        foreach (var person in people)
        {
            var row = new Grid { ColumnSpacing = 8, Padding = new(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Text = $"{person.DisplayName} · {(person.Role == SessionRoles.Owner ? Loc.T("owner") : person.Role == SessionRoles.Editor ? Loc.T("edits") : Loc.T("watches"))}"
            });
            if (manage && person.Role != SessionRoles.Owner)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                string other = person.Role == SessionRoles.Editor ? SessionRoles.Viewer : SessionRoles.Editor;
                var change = new Button { Content = other == SessionRoles.Editor ? Loc.T("Let edit") : Loc.T("Watch only"), FontSize = 12, Padding = new(8, 2, 8, 3) };
                var remove = new Button { Content = Loc.T("Remove"), FontSize = 12, Padding = new(8, 2, 8, 3) };
                var who = person;
                change.Click += async (_, _) =>
                {
                    try { await client.SetProjectRoleAsync(world.Id, who.AccountId, other); await FillMembers(client, world, list, manage); }
                    catch (Exception ex) when (IsCollabFailure(ex)) { change.Content = Loc.T("Failed"); ToolTipService.SetToolTip(change, CollabError(ex)); }
                };
                remove.Click += async (_, _) =>
                {
                    try { await client.RemoveProjectMemberAsync(world.Id, who.AccountId); await FillMembers(client, world, list, manage); }
                    catch (Exception ex) when (IsCollabFailure(ex)) { remove.Content = Loc.T("Failed"); ToolTipService.SetToolTip(remove, CollabError(ex)); }
                };
                actions.Children.Add(change); actions.Children.Add(remove);
                Grid.SetColumn(actions, 1);
                row.Children.Add(actions);
            }
            list.Children.Add(row);
        }
        if (people.Count == 1) list.Children.Add(new TextBlock { Text = Loc.T("Nobody else yet."), Style = (Style)Application.Current.Resources["HintStyle"] });
    }

    // Leaves a world shared with you (it disappears from your worlds).
    private async Task<bool> LeaveSharedWorldAsync(CollabClient client, CloudProjectInfo world)
    {
        try
        {
            var me = await MeAsync(client);
            await client.RemoveProjectMemberAsync(world.Id, me.Account.AccountId);
            _me = null;
            return true;
        }
        catch (Exception ex) when (IsCollabFailure(ex)) { if (Session is { } s) s.Error = CollabError(ex); return false; }
    }
}
