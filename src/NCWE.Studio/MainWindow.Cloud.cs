// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NCWE.Collab;
using NCWE.Collab.Client;
using NCWE.Core;
using NCWE.Formats;
using NCWE.Studio.Session;

namespace NCWE.Studio;

// Cloud saves: the world (with the custom meshes and sounds it uses) saved as a new version of a
// project in the user's Open//77 cloud, on the collaboration service (collab.open77.dev), and
// opened back from there on any PC. They require the account (RequireAccountAsync); the editor
// itself never does. Each world remembers the cloud project it saves to (per account).
public sealed partial class MainWindow
{
    // The service: collab.open77.dev, or NCWE_COLLAB_URL (a local server while developing).
    internal static readonly Uri CollabService = ResolveCollabService();

    // A developer token for a local server only (dev:<name>@<namespace>, the servers' dev mode).
    internal static string? CollabDevToken =>
        CollabService.IsLoopback && Environment.GetEnvironmentVariable("NCWE_COLLAB_DEV_TOKEN") is { Length: > 0 } token ? token : null;

    private static Uri ResolveCollabService()
    {
        if (Uri.TryCreate(Environment.GetEnvironmentVariable("NCWE_COLLAB_URL"), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            return new(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        return new(CollabProtocol.DefaultService);
    }

    private CollabClient? _collab;
    private string? _collabToken;
    private MeResponse? _me;
    private StudioPackager? _packager;
    private bool _cloudBusy;
    // The world last opened from or saved to the cloud (Ctrl+S saves a new version there while it
    // has no file on this PC).
    private Guid? _cloudProjectOf;

    // A client for the current account (a new one after signing in again).
    private CollabClient? Collab()
    {
        string? token = CollabDevToken ?? _account?.Token;
        if (token is null) return null;
        if (_collab is null || _collabToken != token)
        {
            if (_collab is not null && _live?.Client != _collab) _collab.Dispose();
            _collab = new CollabClient(CollabService, token);
            _collabToken = token; _me = null;
        }
        return _collab;
    }

    private StudioPackager Packager
    {
        get
        {
            if (_packager is not null) return _packager;
            var s = Session ?? throw new InvalidOperationException("The editor is not ready yet.");
            _packager = new StudioPackager(s);
            _packager.Installed += installed => DispatcherQueue.Post(() => OnPackageInstalled(s, installed));
            return _packager;
        }
    }

    // Meshes, sounds and decals a world brought in: drawn and listed at once.
    private static void OnPackageInstalled(StudioSession s, PackageInstall installed)
    {
        foreach (var id in installed.Meshes) s.CustomMeshChanged(CustomMeshInfo.Fnv1a64(CustomMeshPaths.Mesh(id)));
        foreach (var id in installed.Decals) s.CustomDecalChanged(id);
        var parts = new List<string>();
        if (installed.Meshes.Count > 0) parts.Add(installed.Meshes.Count == 1 ? Loc.T("1 custom mesh") : Loc.F("{0} custom meshes", installed.Meshes.Count));
        if (installed.Sounds.Count > 0) parts.Add(installed.Sounds.Count == 1 ? Loc.T("1 custom sound") : Loc.F("{0} custom sounds", installed.Sounds.Count));
        if (installed.Decals.Count > 0) parts.Add(installed.Decals.Count == 1 ? Loc.T("1 custom decal") : Loc.F("{0} custom decals", installed.Decals.Count));
        s.Status = Loc.F("Added {0} to your library (used by this world).", string.Join(Loc.T(" and "), parts));
    }

    private async Task<MeResponse> MeAsync(CollabClient client) => _me ??= await client.MeAsync();

    private static string CloudKey(MeResponse me, ProjectData project) => $"{me.Account.AccountId:N}/{project.Id:N}";

    private bool CloudLinked => Session is { } s && s.ProjectPath is null && _cloudProjectOf == s.Document.Project.Id && (_account is not null || CollabDevToken is not null);

    // What went wrong, for the user; a refused account signs out (its session ended).
    private string CollabError(Exception ex)
    {
        switch (ex)
        {
            case CollabApiException { Status: 401 }:
                AccountRejected();
                return Loc.T("your Open//77 session has ended: sign in again.");
            case CollabApiException { Code: CollabErrors.QuotaExceeded }:
                return _me is { } me ? Loc.F("your cloud is full ({0} of {1} used): delete worlds you no longer need in File ▸ Open from the cloud…", Size(me.Cloud.UsedBytes), Size(me.Cloud.QuotaBytes))
                    : Loc.T("your cloud is full: delete worlds you no longer need in File ▸ Open from the cloud…");
            case CollabApiException { Code: CollabErrors.TooManyProjects } api: return Loc.F("{0} (File ▸ Open from the cloud…).", api.Message.TrimEnd('.'));
            case CollabApiException api: return api.Message.TrimEnd('.') + ".";
            case HttpRequestException: return Loc.F("{0} could not be reached: check your connection.", CollabService.Host);
            case TaskCanceledException: return Loc.F("{0} did not answer in time.", CollabService.Host);
            default: return ex.Message;
        }
    }

    private static bool IsCollabFailure(Exception ex) => ex is CollabApiException or HttpRequestException or TaskCanceledException or IOException or InvalidDataException
        or System.Text.Json.JsonException or System.Net.WebSockets.WebSocketException;

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:F1} MB",
        _ => $"{bytes / 1073741824.0:F2} GB"
    };

    private static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        if (span.TotalMinutes < 1) return Loc.T("just now");
        if (span.TotalHours < 1) return Loc.F("{0} min ago", (int)span.TotalMinutes);
        if (span.TotalDays < 1) return Loc.F("{0} h ago", (int)span.TotalHours);
        if (span.TotalDays < 30) return Loc.F("{0} d ago", (int)span.TotalDays);
        return at.ToLocalTime().ToString("d MMM yyyy", Loc.Culture);
    }

    // --- Save -------------------------------------------------------------------------------------

    private async void OnSaveToCloud(object sender, RoutedEventArgs e)
    {
        if (InWorld) await SaveWorldAsync(automatic: false);   // a shared world: its session goes into it
        else await SaveToCloudAsync(note: null, interactive: true);
    }

    // A new version of the world's cloud project (created on the first save: its name is asked).
    internal async Task<CloudVersionInfo?> SaveToCloudAsync(string? note, bool interactive)
    {
        if (Session is not { } s || _cloudBusy) return null;
        if (!await RequireAccountAsync(Loc.T("Saving to the cloud")) || Collab() is not { } client) return null;
        _cloudBusy = true;
        try
        {
            var me = await MeAsync(client);
            var project = s.Document.Project;
            string key = CloudKey(me, project);
            Guid? target = _settings.CloudProjects.TryGetValue(key, out var known) ? known : null;
            string cloudName = project.Name;
            if (target is null && interactive)
            {
                if (await AskFirstCloudSave(project.Name, me.Cloud) is not { } answer) return null;
                (cloudName, note) = answer;
            }
            s.Status = Loc.T("Packing the world for the cloud…");
            byte[] content = await Task.Run(() => Packager.Pack(project));
            s.Status = Loc.F("Uploading {0} to your cloud…", Size(content.Length));
            target ??= (await client.CreateProjectAsync(cloudName)).Id;
            CloudVersionInfo version;
            try { version = await client.UploadVersionAsync(target.Value, content, note, ProjectPackage.ContentType); }
            catch (CollabApiException missing) when (missing.Status == 404)
            {
                // Deleted meanwhile (from another PC): saved as a new cloud project.
                target = (await client.CreateProjectAsync(cloudName)).Id;
                version = await client.UploadVersionAsync(target.Value, content, note, ProjectPackage.ContentType);
            }
            _settings.CloudProjects[key] = target.Value; _settings.Save();
            _me = null;   // the usage changed
            _cloudProjectOf = project.Id;
            if (s.ProjectPath is null && ReferenceEquals(s.Document.Project, project)) s.Document.MarkSaved();
            s.Status = Loc.F("Saved to your cloud: “{0}”, version {1} ({2}).", cloudName, version.Version, Size(version.Size));
            UpdateProjectPanel();
            return version;
        }
        catch (Exception ex) when (IsCollabFailure(ex)) { s.Error = Loc.F("Cloud save failed: {0}", CollabError(ex)); return null; }
        finally { _cloudBusy = false; }
    }

    private async Task<(string Name, string? Note)?> AskFirstCloudSave(string name, CloudUsage usage)
    {
        var nameBox = new TextBox { Header = Loc.T("Name in your cloud"), Text = name, MaxLength = 120 };
        var noteBox = new TextBox { Header = Loc.T("Note (optional)"), PlaceholderText = Loc.T("What changed"), MaxLength = 500 };
        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionStyle"],
            Text = Loc.T("The world is saved with the custom meshes and sounds it uses. Each save keeps a new version; open it from any PC with File ▸ Open from the cloud.")
        });
        panel.Children.Add(nameBox);
        panel.Children.Add(noteBox);
        panel.Children.Add(UsageView(usage));
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Save to the cloud"), Content = panel,
            PrimaryButtonText = Loc.T("Save"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary
        };
        nameBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = nameBox.Text.Trim().Length > 0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return (nameBox.Text.Trim(), noteBox.Text.Trim() is { Length: > 0 } note ? note : null);
    }

    private static FrameworkElement UsageView(CloudUsage usage)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new ProgressBar { Maximum = Math.Max(1, usage.QuotaBytes), Value = Math.Min(usage.UsedBytes, usage.QuotaBytes) });
        panel.Children.Add(new TextBlock
        {
            Style = (Style)Application.Current.Resources["HintStyle"],
            Text = Loc.F("{0} of {1} used · {2} of {3} worlds · {4} of {5} shared", Size(usage.UsedBytes), Size(usage.QuotaBytes), usage.Projects, usage.MaxProjects, usage.SharedProjects, usage.MaxSharedProjects)
        });
        return panel;
    }

    // --- Open ---------------------------------------------------------------------------------------

    private async void OnOpenFromCloud(object sender, RoutedEventArgs e) => await OpenFromCloudAsync();

    internal async Task OpenFromCloudAsync()
    {
        if (Session is not { } s || _cloudBusy) return;
        if (!await RequireAccountAsync(Loc.T("Opening worlds from the cloud")) || Collab() is not { } client) return;
        if (!await ConfirmDiscardChanges()) return;
        ProjectListResponse list;
        try { s.Status = Loc.T("Listing your cloud…"); list = await client.ListProjectsAsync(); s.Status = Loc.T("Ready"); }
        catch (Exception ex) when (IsCollabFailure(ex)) { s.Error = Loc.F("Your cloud could not be listed: {0}", CollabError(ex)); return; }
        if (await PickCloudProject(client, list) is not { } picked) return;
        // A shared world opens live (with whoever is working on it); an older version, as a copy.
        if (picked.Project.Shared && picked.Version is null) await OpenSharedWorldAsync(picked.Project);
        else await OpenCloudVersionAsync(client, picked.Project, picked.Version);
    }

    internal async Task<bool> OpenCloudVersionAsync(CollabClient client, CloudProjectInfo info, int? version)
    {
        if (Session is not { } s || _cloudBusy) return false;
        _cloudBusy = true;
        try
        {
            s.Status = Loc.F("Downloading “{0}”…", info.Name);
            var (downloaded, content) = await client.DownloadVersionAsync(info.Id, version);
            var project = await Task.Run(() => Packager.Unpack(content));
            if (!await s.OpenProjectDataAsync(project, Loc.F("Opened “{0}” from your cloud (version {1}).", info.Name, downloaded.Version))) return false;
            var me = await MeAsync(client);
            _settings.CloudProjects[CloudKey(me, project)] = info.Id; _settings.Save();
            _cloudProjectOf = project.Id;
            UpdateProjectPanel(); _entitiesDirty = true;
            return true;
        }
        catch (Exception ex) when (IsCollabFailure(ex)) { s.Error = Loc.F("“{0}” could not be opened: {1}", info.Name, CollabError(ex)); return false; }
        finally { _cloudBusy = false; }
    }

    // The cloud's worlds: pick one (and a version), rename or delete them.
    private async Task<(CloudProjectInfo Project, int? Version)?> PickCloudProject(CollabClient client, ProjectListResponse list)
    {
        var projects = list.Projects.OrderByDescending(p => p.UpdatedAt).ToList();
        var listView = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 300, Padding = new(0) };
        var versions = new ComboBox { Header = Loc.T("Version"), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["DangerBrush"], Visibility = Visibility.Collapsed };
        var empty = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionStyle"], Margin = new(0, 20, 0, 20),
            Text = Loc.T("No world in your cloud yet. File ▸ Save to the cloud puts the one you are editing there; Collaborate ▸ Join a shared world enters one someone shared with you."),
            Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed
        };
        var rename = new Button { Content = Loc.T("Rename…"), IsEnabled = false };
        var delete = new Button { Content = Loc.T("Delete…"), IsEnabled = false };
        var share = new Button { Content = Loc.T("Share…"), IsEnabled = false };
        void Fail(string message) { error.Text = message; error.Visibility = Visibility.Visible; }
        void Fill()
        {
            listView.Items.Clear();
            foreach (var p in projects) listView.Items.Add(new ListViewItem { Content = CloudRow(p), Tag = p, Padding = new(10, 6, 10, 6) });
            empty.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        CloudProjectInfo? Selected() => (listView.SelectedItem as ListViewItem)?.Tag as CloudProjectInfo;
        Fill();
        int loading = 0;
        listView.SelectionChanged += async (_, _) =>
        {
            var selected = Selected();
            bool owner = selected is { YourRole: null or SessionRoles.Owner };
            rename.IsEnabled = selected is not null && owner;
            delete.IsEnabled = selected is not null;
            delete.Content = owner ? Loc.T("Delete…") : Loc.T("Leave…");
            share.IsEnabled = selected is not null;
            share.Content = owner ? Loc.T("Share…") : Loc.T("People…");
            versions.Items.Clear(); versions.IsEnabled = false;
            if (selected is null) return;
            versions.Items.Add(new ComboBoxItem
            {
                Content = selected.Shared ? (selected.Live is { } live ? Loc.F("Live now with {0} (join them)", live.StartedBy.DisplayName) : Loc.F("Latest (version {0}), live with whoever opens it next", selected.LatestVersion))
                    : Loc.F("Latest (version {0})", selected.LatestVersion),
                Tag = null
            });
            versions.SelectedIndex = 0;
            int ticket = ++loading;
            try
            {
                var details = await client.GetProjectAsync(selected.Id);
                if (ticket != loading) return;
                foreach (var v in details.Versions.Where(v => v.Status == VersionStatus.Ready).OrderByDescending(v => v.Version))
                    versions.Items.Add(new ComboBoxItem
                    {
                        Content = Loc.F("Version {0} · {1:d MMM HH:mm}{2} · {3}{4}{5}", v.Version, v.CreatedAt.ToLocalTime(), (v.AuthorName is { } by && details.Project.Shared ? " · " + by : ""), Size(v.Size), (v.Note is { Length: > 0 } note ? " · " + note : ""), (details.Project.Shared ? Loc.T(" (opens as a copy)") : "")),
                        Tag = v.Version
                    });
                versions.IsEnabled = versions.Items.Count > 1;
            }
            catch (Exception ex) when (IsCollabFailure(ex)) { Fail(CollabError(ex)); }
        };
        // Rename and delete act at once (flyouts: a dialog cannot open over this one).
        var renameBox = new TextBox { MaxLength = 120, Width = 260 };
        var renameGo = new Button { Content = Loc.T("Rename"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        var renameFlyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { renameBox, renameGo } } };
        rename.Flyout = renameFlyout;
        renameFlyout.Opening += (_, _) => renameBox.Text = Selected()?.Name ?? "";
        renameGo.Click += async (_, _) =>
        {
            if (Selected() is not { } p || renameBox.Text.Trim() is not { Length: > 0 } name) return;
            try
            {
                var updated = await client.UpdateProjectAsync(p.Id, new UpdateProjectRequest(Name: name));
                projects[projects.IndexOf(p)] = updated;
                renameFlyout.Hide(); Fill();
                listView.SelectedIndex = projects.IndexOf(updated);
            }
            catch (Exception ex) when (IsCollabFailure(ex)) { renameFlyout.Hide(); Fail(CollabError(ex)); }
        };
        var deleteText = new TextBlock { TextWrapping = TextWrapping.Wrap, Width = 280 };
        var deleteGo = new Button { Content = Loc.T("Delete for good"), HorizontalAlignment = HorizontalAlignment.Right, Foreground = (Brush)Application.Current.Resources["DangerBrush"] };
        var deleteFlyout = new Flyout { Content = new StackPanel { Spacing = 10, Children = { deleteText, deleteGo } } };
        delete.Flyout = deleteFlyout;
        deleteFlyout.Opening += (_, _) =>
        {
            var p = Selected();
            bool owner = p is { YourRole: null or SessionRoles.Owner };
            deleteGo.Content = owner ? Loc.T("Delete for good") : Loc.T("Leave");
            deleteText.Text = p is null ? ""
                : !owner ? Loc.F("Leave “{0}”? It disappears from your worlds; ask {1} for a new code to come back.", p.Name, p.Owner?.DisplayName ?? Loc.T("its owner"))
                : Loc.F("Delete “{0}” and its {1} version{2} from your cloud?{3} This cannot be undone. Copies on PCs stay.", p.Name, p.Versions, (p.Versions == 1 ? "" : "s"), (p.Shared ? Loc.T(" The people you shared it with lose it too.") : ""));
        };
        deleteGo.Click += async (_, _) =>
        {
            if (Selected() is not { } p) return;
            try
            {
                if (p.YourRole is not (null or SessionRoles.Owner))
                {
                    if (await LeaveSharedWorldAsync(client, p)) { projects.Remove(p); deleteFlyout.Hide(); Fill(); }
                    return;
                }
                await client.DeleteProjectAsync(p.Id);
                projects.Remove(p);
                foreach (var key in _settings.CloudProjects.Where(m => m.Value == p.Id).Select(m => m.Key).ToArray()) _settings.CloudProjects.Remove(key);
                _settings.Save(); _me = null;
                deleteFlyout.Hide(); Fill();
            }
            catch (Exception ex) when (IsCollabFailure(ex)) { deleteFlyout.Hide(); Fail(CollabError(ex)); }
        };
        // Sharing: the owner's code and members; a member sees who works on it.
        var shareFlyout = WideFlyout();
        share.Flyout = shareFlyout;
        shareFlyout.Opening += (_, _) =>
        {
            if (Selected() is not { } p) return;
            if (p.YourRole is null or SessionRoles.Owner) shareFlyout.Content = SharePanel(client, p, closeAfterStop: null);
            else
            {
                var people = new StackPanel { Spacing = 4, Width = 340 };
                shareFlyout.Content = people;
                _ = FillMembers(client, p, people, manage: false);
            }
        };
        shareFlyout.Closed += async (_, _) =>
        {
            // Sharing may have changed: the list shows it.
            try { var fresh = await client.ListProjectsAsync(); projects = fresh.Projects.OrderByDescending(p => p.UpdatedAt).ToList(); var keep = Selected()?.Id; Fill(); listView.SelectedIndex = projects.FindIndex(p => p.Id == keep); }
            catch (Exception ex) when (IsCollabFailure(ex)) { }
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { share, rename, delete } };
        var panel = new StackPanel { Spacing = 10, MinWidth = 440 };
        panel.Children.Add(UsageView(list.Usage));
        panel.Children.Add(new Grid { Children = { listView, empty } });
        panel.Children.Add(versions);
        panel.Children.Add(actions);
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = Loc.T("Open from the cloud"), Content = panel,
            PrimaryButtonText = Loc.T("Open"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false
        };
        dialog.Resources["ContentDialogMaxWidth"] = 640.0;
        listView.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = Selected() is not null;
        if (_probeShareFlyout)
        {
            // Automation (checks): the first world selected and its sharing flyout opened.
            _probeShareFlyout = false;
            dialog.Opened += (_, _) =>
            {
                if (projects.Count == 0) return;
                listView.SelectedIndex = 0;
                DispatcherQueue.Post(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => share.Flyout?.ShowAt(share));
            };
        }
        listView.DoubleTapped += (_, _) => { if (Selected() is not null) { _pickedByDoubleTap = true; dialog.Hide(); } };
        _pickedByDoubleTap = false;
        var result = await dialog.ShowAsync();
        if ((result != ContentDialogResult.Primary && !_pickedByDoubleTap) || Selected() is not { } chosen) return null;
        return (chosen, (versions.SelectedItem as ComboBoxItem)?.Tag as int?);
    }

    private bool _pickedByDoubleTap;
    private bool _probeShareFlyout;

    // A flyout for wide content (sharing): the text wraps at its width instead of scrolling sideways.
    private static Flyout WideFlyout()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 600.0));
        style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled));
        style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        return new Flyout { FlyoutPresenterStyle = style };
    }

    private static FrameworkElement CloudRow(CloudProjectInfo p)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new FontIcon { Glyph = p.Shared ? "" : "", FontSize = 16, Foreground = (Brush)Application.Current.Resources["AccentBrush"], VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 1 };
        Grid.SetColumn(text, 1);
        text.Children.Add(new TextBlock { Text = p.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        bool mine = p.YourRole is null or SessionRoles.Owner;
        string sharing = !p.Shared ? "" : mine ? (p.Members == 0 ? Loc.T("Shared (nobody joined yet) · ") : Loc.F("Shared with {0} {1} · ", p.Members, (p.Members == 1 ? Loc.T("person") : Loc.T("people"))))
            : Loc.F("Shared by {0}{1} · ", p.Owner?.DisplayName ?? Loc.T("someone"), (p.YourRole == SessionRoles.Viewer ? Loc.T(" (you watch)") : ""));
        text.Children.Add(new TextBlock
        {
            Style = (Style)Application.Current.Resources["HintStyle"], TextTrimming = TextTrimming.CharacterEllipsis,
            Text = Loc.F("{0}Version {1} · {2} · saved {3}", sharing, p.LatestVersion, Size(p.Bytes), Ago(p.UpdatedAt))
        });
        if (p.Live is { } live)
            text.Children.Add(new TextBlock
            {
                FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x3D, 0xDC, 0x84)),
                Text = Loc.F("● Live now: {0} opened it {1}{2}", live.StartedBy.DisplayName, Ago(live.StartedAt), (live.Online > 0 ? Loc.F(" · {0} working", live.Online) : ""))
            });
        grid.Children.Add(text);
        return grid;
    }

    // --- Automation (tests) -------------------------------------------------------------------------

    internal async Task<object> CloudUi(string action, string? note, Guid? project, int? version)
    {
        if (Session is not { } s) throw new InvalidOperationException("Viewport not ready.");
        var client = Collab() ?? throw new InvalidOperationException("Not signed in to Open//77 (cloud saves need the account).");
        switch (action)
        {
            case "list":
                var list = await client.ListProjectsAsync();
                return new { ok = true, service = CollabService.AbsoluteUri, usage = list.Usage, projects = list.Projects };
            case "save":
                var saved = await SaveToCloudAsync(note, interactive: false);
                return new { ok = saved is not null, version = saved, error = s.Error, linked = CloudLinked };
            case "open":
                var info = (await client.ListProjectsAsync()).Projects.FirstOrDefault(p => p.Id == project) ?? throw new KeyNotFoundException("No such cloud project.");
                bool opened = await OpenCloudVersionAsync(client, info, version);
                return new { ok = opened, name = s.Document.Project.Name, objects = s.Document.Project.Changes.Added.Count, error = s.Error, linked = CloudLinked };
            case "delete":
                await client.DeleteProjectAsync(project ?? throw new ArgumentException("project is required."));
                return new { ok = true };
            // Shared worlds.
            case "invite":
            {
                var target = project ?? (await client.ListProjectsAsync()).Projects.FirstOrDefault(p => p.Name == s.Document.Project.Name && p.YourRole == SessionRoles.Owner)?.Id
                    ?? throw new ArgumentException("project is required.");
                var invite = await client.CreateInviteAsync(target, note);
                return new { ok = true, project = target, invite };
            }
            case "join":
            {
                var joined = await client.JoinSharedAsync(note ?? throw new ArgumentException("code (note) is required."));
                return new { ok = true, project = joined };
            }
            case "open-shared":
            {
                var world = (await client.ListProjectsAsync()).Projects.FirstOrDefault(p => p.Id == project) ?? throw new KeyNotFoundException("No such cloud project.");
                string? failure = await OpenSharedWorldAsync(world);
                return new { ok = failure is null, error = failure, live = _live?.Code, started = _live?.IsOwner, objects = s.Document.Project.Changes.Added.Count };
            }
            case "save-world":
            {
                var saved2 = await SaveWorldAsync(automatic: false);
                return new { ok = saved2 is not null, version = saved2, status = s.Status, error = s.Error };
            }
            case "leave-world":
                if (_live is { } leaving && InWorld) await LeaveWorldAsync(leaving);
                return new { ok = true, live = _live is not null };
            case "members":
                return new { ok = true, members = await client.ProjectMembersAsync(project ?? throw new ArgumentException("project is required.")) };
            case "share-ui":
                // The cloud window with the first world's sharing flyout open (checks; dialog-close closes it).
                _probeShareFlyout = true;
                _ = OpenFromCloudAsync();
                await Task.Delay(2500);
                return new { ok = true };
            case "remove-member":
                await client.RemoveProjectMemberAsync(project ?? throw new ArgumentException("project is required."), Guid.Parse(note ?? throw new ArgumentException("account (note) is required.")));
                return new { ok = true };
            case "state":
                var me = await MeAsync(client);
                return new { ok = true, service = CollabService.AbsoluteUri, account = me.Account, cloud = me.Cloud, linked = CloudLinked, mapped = _settings.CloudProjects.GetValueOrDefault(CloudKey(me, s.Document.Project)) };
            default: throw new ArgumentException("Unknown cloud action (state, list, save, open, delete, invite, join, open-shared, save-world, leave-world, members, remove-member).");
        }
    }
}
