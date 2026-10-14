// <copyright project="Enzisoft.Packages.Editor" file="PackagesWindow.cs">
// Copyright © 2026 Thomas Enzenebner. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Enzisoft.Packages.Editor
{
    /// <summary>
    /// Lists every Enzisoft package (free, owned and paid) on the left and the selected one's details on the right, and
    /// installs, updates or removes owned packages in the open project.
    /// </summary>
    internal sealed class PackagesWindow : EditorWindow
    {
        // EditorPrefs live per user outside the project, so the token never ends up in source control or a build.
        private const string TokenKey = "Enzisoft.Packages.Token";
        private const string AccountKey = "Enzisoft.Packages.Account";
        private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(10);
        private const string StylePath = "Packages/com.enzisoft.packages/Editor/PackagesWindow.uss";

        private static readonly (string Kind, string Label)[] DocKinds = { ("readme", "Readme"), ("changelog", "Changelog"), ("license", "License") };

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<object> _rows = new List<object>();
        private readonly Dictionary<(string, string, string), string> _docs = new Dictionary<(string, string, string), string>();
        private Dictionary<string, InstalledPackage> _installed = new Dictionary<string, InstalledPackage>();

        private RegistryClient _client;
        private CancellationTokenSource _operation;
        private Entry _selected;
        private string _selectedVersion;
        private string _openDoc;

        private Toolbar _toolbar;
        private ToolbarSearchField _search;
        private Label _count;
        private ListView _list;
        private Label _refreshStatus;
        private Button _refreshButton;
        private VisualElement _empty;
        private VisualElement _details;

        private Label _title;
        private Label _packageName;
        private VisualElement _badges;
        private Label _description;
        private DropdownField _versionField;
        private Button _actionButton;
        private Button _removeButton;
        private Button _cancelButton;
        private Label _accessNote;
        private ProgressBar _progress;
        private Label _status;
        private VisualElement _links;
        private VisualElement _doc;
        private VisualElement _meta;
        private VisualElement _dependencies;
        private Label _keywords;

        private static string Token
        {
            get => EditorPrefs.GetString(TokenKey, string.Empty);
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    EditorPrefs.DeleteKey(TokenKey);
                else
                    EditorPrefs.SetString(TokenKey, value.Trim());
            }
        }

        private RegistryClient Client => _client ??= new RegistryClient(RegistryClient.DefaultUrl, Token);

        private bool IsBusy => _operation != null;

        [MenuItem("Window/Package Management/Enzisoft Packages")]
        public static void Open() => GetWindow<PackagesWindow>("Enzisoft Packages");

        private void OnEnable() => Events.registeredPackages += OnRegisteredPackages;

        private void OnDisable()
        {
            Events.registeredPackages -= OnRegisteredPackages;
            _operation?.Cancel();
            _client?.Dispose();
            _client = null;
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (style != null)
                root.styleSheets.Add(style);

            _toolbar = new Toolbar();
            root.Add(_toolbar);
            BuildToolbar();

            var split = new TwoPaneSplitView(0, 300, TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
            root.Add(split);

            var listPane = new VisualElement();
            listPane.AddToClassList("nz-pm-list-pane");
            split.Add(listPane);

            _search = new ToolbarSearchField();
            _search.AddToClassList("nz-pm-search");
            _search.RegisterValueChangedCallback(_ => RebuildRows());
            listPane.Add(_search);

            _count = new Label();
            _count.AddToClassList("nz-pm-count");
            listPane.Add(_count);

            _list = new ListView
            {
                itemsSource = _rows,
                makeItem = MakeRow,
                bindItem = BindRow,
                selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
            };
            _list.AddToClassList("nz-pm-list");
            _list.selectionChanged += selection =>
            {
                // Group headers are rows too; selecting one keeps the current package.
                if (selection.FirstOrDefault() is Entry entry)
                    Select(entry);
            };
            listPane.Add(_list);

            var refreshBar = new VisualElement();
            refreshBar.AddToClassList("nz-pm-refresh-bar");
            _refreshStatus = new Label();
            _refreshStatus.AddToClassList("nz-pm-refresh-bar__status");
            _refreshButton = new Button(Refresh) { text = "Refresh", tooltip = "Reload the catalog and the project's packages." };
            refreshBar.Add(_refreshStatus);
            refreshBar.Add(_refreshButton);
            listPane.Add(refreshBar);

            var scroll = new ScrollView();
            scroll.AddToClassList("nz-pm-details-scroll");
            split.Add(scroll);

            _empty = new Label("Select a package to see its details.");
            _empty.AddToClassList("nz-pm-empty");
            scroll.Add(_empty);

            _details = new VisualElement();
            _details.AddToClassList("nz-pm-details");
            scroll.Add(_details);
            BuildDetails();

            Select(null);
            Refresh();
        }

        // ---------------------------------------------------------------- toolbar

        private static string AccountName
        {
            get => EditorPrefs.GetString(AccountKey, string.Empty);
            set => EditorPrefs.SetString(AccountKey, value ?? string.Empty);
        }

        private void BuildToolbar()
        {
            _toolbar.Clear();
            var label = new Label(string.IsNullOrEmpty(Token)
                ? "Not signed in. Sign in to install free packages and the ones you bought."
                : $"Signed in as {(string.IsNullOrEmpty(AccountName) ? "your Enzisoft account" : AccountName)}");
            label.AddToClassList("nz-pm-toolbar__account");
            _toolbar.Add(label);

            if (string.IsNullOrEmpty(Token))
            {
                _toolbar.Add(new ToolbarButton(SignIn) { text = "Sign in", tooltip = "Sign in with Google, GitHub or email in your browser." });
            }
            else
            {
                _toolbar.Add(new ToolbarButton(() => Application.OpenURL(RegistryClient.DefaultUrl + "/account")) { text = "Account" });
                _toolbar.Add(new ToolbarButton(SignOut) { text = "Sign out", tooltip = "Sign out and revoke this computer's token." });
            }
        }

        private void SignIn() => _ = RunAsync(_refreshStatus, "Finish signing in in your browser...", async cancellationToken =>
        {
            var verifier = Pkce.NewVerifier();
            var state = Pkce.NewState();
            using var callback = new LoopbackCallback();
            Application.OpenURL($"{RegistryClient.DefaultUrl}/editor/signin?port={callback.Port}&state={state}" +
                $"&code_challenge={Pkce.Challenge(verifier)}&device={Uri.EscapeDataString(SystemInfo.deviceName)}");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SignInTimeout);
            var code = await callback.WaitForCodeAsync(state, timeout.Token);

            Token = await Client.ExchangeCodeAsync(code, verifier, cancellationToken);
            ResetClient();
            AccountName = (await Client.WhoAmIAsync(cancellationToken))?.FirstOrDefault();
            BuildToolbar();
            SetStatus(_refreshStatus, "Signed in.");
            await LoadAsync(cancellationToken);
        });

        private void SignOut() => _ = RunAsync(_refreshStatus, "Signing out...", async cancellationToken =>
        {
            try
            {
                await Client.RevokeAsync(cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Offline: forget the token here anyway; it can be revoked on the account page later.
                Debug.LogWarning($"Enzisoft Packages: could not revoke the token on the registry: {e.Message}");
            }

            ClearSignIn();
            await LoadAsync(cancellationToken);
        });

        private void ClearSignIn()
        {
            Token = null;
            AccountName = null;
            ResetClient();
            BuildToolbar();
        }

        private void ResetClient()
        {
            _client?.Dispose();
            _client = null;
        }

        // ---------------------------------------------------------------- list

        private sealed class Entry
        {
            public string Name;

            /// <summary>Null when the registry could not be reached or no longer lists the package.</summary>
            public CatalogEntry Catalog;

            /// <summary>Accessible versions; loaded on selection.</summary>
            public PackageDocument Document;

            public PackageVersion Latest => Catalog?.Latest;
            public bool CanDownload => Catalog != null && Catalog.Access != PackageAccess.None && Catalog.LatestAccessible != null;
        }

        private void Refresh() => _ = RunAsync(_refreshStatus, "Refreshing...", LoadAsync);

        private async Task LoadAsync(CancellationToken cancellationToken)
        {
            _installed = ProjectPackages.Read();
            string error = null;
            List<CatalogEntry> catalog = null;
            try
            {
                if (!string.IsNullOrEmpty(Token) && await Client.WhoAmIAsync(cancellationToken) == null)
                {
                    // Revoked on the account page or expired: continue signed out.
                    ClearSignIn();
                    Debug.Log("Enzisoft Packages: the saved sign-in is no longer valid. Sign in again to install packages.");
                }

                catalog = await Client.GetCatalogAsync(cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Installed packages stay listed offline, so they can still be inspected and removed.
                error = $"Registry unavailable: {e.Message}";
            }

            var selected = _selected?.Name;
            _entries.Clear();
            _docs.Clear();
            foreach (var entry in catalog ?? new List<CatalogEntry>())
                _entries.Add(new Entry { Name = entry.Name, Catalog = entry });

            foreach (var name in _installed.Keys.Where(IsManaged).Where(n => _entries.All(e => e.Name != n)))
                _entries.Add(new Entry { Name = name });

            RebuildRows();
            Select(_entries.FirstOrDefault(e => e.Name == selected));
            SetStatus(_refreshStatus, error ?? $"Updated {DateTime.Now:t}", error != null);
        }

        private static bool IsManaged(string name) => name.StartsWith(InstallPlanner.ManagedScope, StringComparison.Ordinal);

        private void OnRegisteredPackages(PackageRegistrationEventArgs args)
        {
            _installed = ProjectPackages.Read();
            RebuildRows();
            RenderDetails();
        }

        private void RebuildRows()
        {
            if (_list == null)
                return;

            var filter = _search.value?.Trim() ?? string.Empty;
            var visible = _entries.Where(e => Matches(e, filter)).OrderBy(e => DisplayName(e), StringComparer.OrdinalIgnoreCase).ToList();

            _rows.Clear();
            AddGroup("Installed", visible.Where(e => _installed.ContainsKey(e.Name)));
            AddGroup("Available", visible.Where(e => !_installed.ContainsKey(e.Name) && e.CanDownload));
            AddGroup("Not owned", visible.Where(e => !_installed.ContainsKey(e.Name) && !e.CanDownload));
            _count.text = visible.Count == 1 ? "1 package" : $"{visible.Count} packages";
            _list.RefreshItems();

            var index = _rows.IndexOf(_selected);
            if (index >= 0)
                _list.SetSelectionWithoutNotify(new[] { index });
            else
                _list.ClearSelection();

            void AddGroup(string title, IEnumerable<Entry> entries)
            {
                var list = entries.ToList();
                if (list.Count == 0)
                    return;

                _rows.Add(title);
                _rows.AddRange(list);
            }
        }

        private bool Matches(Entry entry, string filter)
        {
            if (filter.Length == 0)
                return true;

            var meta = Metadata(entry, null);
            return new[] { entry.Name, meta?.DisplayName, meta?.Description, meta?.Author }
                .Concat(meta?.Keywords ?? Array.Empty<string>())
                .Any(text => text != null && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private VisualElement MakeRow()
        {
            var item = new VisualElement();
            var group = new Label { name = "group" };
            group.AddToClassList("nz-pm-group");
            item.Add(group);

            var row = new VisualElement { name = "row" };
            row.AddToClassList("nz-pm-row");
            var text = new VisualElement();
            text.AddToClassList("nz-pm-row__text");
            var title = new Label { name = "title" };
            title.AddToClassList("nz-pm-row__name");
            var sub = new Label { name = "sub" };
            sub.AddToClassList("nz-pm-row__sub");
            text.Add(title);
            text.Add(sub);
            var version = new Label { name = "version" };
            version.AddToClassList("nz-pm-row__version");
            row.Add(text);
            row.Add(version);
            item.Add(row);
            return item;
        }

        private void BindRow(VisualElement item, int index)
        {
            var group = item.Q<Label>("group");
            var row = item.Q("row");
            if (_rows[index] is string title)
            {
                group.text = title;
                group.style.display = DisplayStyle.Flex;
                row.style.display = DisplayStyle.None;
                return;
            }

            var entry = (Entry)_rows[index];
            group.style.display = DisplayStyle.None;
            row.style.display = DisplayStyle.Flex;
            item.Q<Label>("title").text = DisplayName(entry);
            item.Q<Label>("sub").text = entry.Name;
            item.Q<Label>("version").text = RowVersion(entry);
        }

        private string RowVersion(Entry entry)
        {
            if (_installed.TryGetValue(entry.Name, out var local))
            {
                return HasUpdate(entry, local) ? $"{local.Version}  ↑ {entry.Catalog.LatestAccessible}" : local.Version;
            }

            return entry.Catalog?.Access switch
            {
                PackageAccess.None => "Paid",
                PackageAccess.Free => $"{entry.Catalog.LatestAccessible}  Free".TrimStart(), // no version while signed out
                _ => entry.Catalog?.LatestAccessible ?? string.Empty,
            };
        }

        private static bool HasUpdate(Entry entry, InstalledPackage local) =>
            !local.IsProtected && entry.CanDownload && SemVer.IsValid(local.Version)
            && SemVer.Compare(entry.Catalog.LatestAccessible, local.Version) > 0;

        private string DisplayName(Entry entry) => Metadata(entry, null)?.DisplayName ?? entry.Name;

        /// <summary>Package metadata of a version: from the registry when known, else from the installed copy.</summary>
        private PackageVersion Metadata(Entry entry, string version)
        {
            if (version != null && entry.Document != null && entry.Document.Versions.TryGetValue(version, out var accessible))
                return accessible;
            if (entry.Latest != null && (version == null || version == entry.Latest.Version || !_installed.ContainsKey(entry.Name)))
                return entry.Latest;

            var info = PackageInfo.FindForPackageName(entry.Name);
            if (info == null)
                return entry.Latest;

            return new PackageVersion
            {
                Name = info.name,
                Version = info.version,
                DisplayName = info.displayName,
                Description = info.description,
                Author = info.author?.name,
                Keywords = info.keywords ?? Array.Empty<string>(),
                Dependencies = info.dependencies.ToDictionary(d => d.name, d => d.version),
            };
        }

        // ---------------------------------------------------------------- details

        private void BuildDetails()
        {
            var header = new VisualElement();
            header.AddToClassList("nz-pm-header");
            var headerText = new VisualElement();
            headerText.AddToClassList("nz-pm-header__text");
            _title = new Label();
            _title.AddToClassList("nz-pm-title");
            _packageName = new Label();
            _packageName.AddToClassList("nz-pm-package-name");
            _packageName.selection.isSelectable = true;
            headerText.Add(_title);
            headerText.Add(_packageName);
            _badges = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            header.Add(headerText);
            header.Add(_badges);
            _details.Add(header);

            _description = new Label();
            _description.AddToClassList("nz-pm-description");
            _details.Add(_description);

            var actions = new VisualElement();
            actions.AddToClassList("nz-pm-action-row");
            _versionField = new DropdownField("Version");
            _versionField.AddToClassList("nz-pm-version-field");
            _versionField.RegisterValueChangedCallback(e =>
            {
                _selectedVersion = e.newValue;
                _openDoc = null;
                RenderDetails();
            });
            _actionButton = new Button(() => _ = InstallSelected());
            _actionButton.AddToClassList("nz-pm-action");
            _removeButton = new Button(() => _ = RemoveSelected()) { text = "Remove" };
            _cancelButton = new Button(() => _operation?.Cancel()) { text = "Cancel" };
            actions.Add(_versionField);
            actions.Add(_actionButton);
            actions.Add(_removeButton);
            actions.Add(_cancelButton);
            _details.Add(actions);

            _accessNote = new Label();
            _accessNote.AddToClassList("nz-pm-note");
            _details.Add(_accessNote);

            _progress = new ProgressBar();
            _progress.AddToClassList("nz-pm-progress");
            _details.Add(_progress);

            _status = new Label();
            _status.AddToClassList("nz-pm-note");
            _details.Add(_status);

            _links = new VisualElement();
            _links.AddToClassList("nz-pm-link-row");
            _details.Add(_links);

            _doc = new VisualElement();
            _doc.AddToClassList("nz-pm-doc");
            _details.Add(_doc);

            var divider = new VisualElement();
            divider.AddToClassList("nz-pm-divider");
            _details.Add(divider);

            _details.Add(SectionTitle("Details"));
            _meta = new VisualElement();
            _details.Add(_meta);

            _details.Add(SectionTitle("Dependencies"));
            _dependencies = new VisualElement();
            _details.Add(_dependencies);

            _details.Add(SectionTitle("Keywords"));
            _keywords = new Label();
            _keywords.AddToClassList("nz-pm-meta-value");
            _details.Add(_keywords);
        }

        private static Label SectionTitle(string text)
        {
            var label = new Label(text);
            label.AddToClassList("nz-pm-section-title");
            return label;
        }

        private void Select(Entry entry)
        {
            _selected = entry;
            _openDoc = null;
            _installed.TryGetValue(entry?.Name ?? string.Empty, out var local);
            _selectedVersion = entry?.Catalog?.LatestAccessible ?? local?.Version ?? entry?.Latest?.Version;
            RenderDetails();

            if (entry != null && entry.CanDownload && entry.Document == null)
                _ = LoadDocumentAsync(entry);
        }

        private async Task LoadDocumentAsync(Entry entry)
        {
            try
            {
                entry.Document = await Client.GetPackageAsync(entry.Name, CancellationToken.None);
            }
            catch (Exception e)
            {
                SetStatus(_status, $"Could not load versions: {e.Message}", true);
                return;
            }

            if (_selected == entry)
                RenderDetails();
        }

        private void RenderDetails()
        {
            if (_details == null)
                return;

            var entry = _selected;
            _empty.style.display = entry == null ? DisplayStyle.Flex : DisplayStyle.None;
            _details.style.display = entry == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (entry == null)
                return;

            _installed.TryGetValue(entry.Name, out var local);
            var meta = Metadata(entry, _selectedVersion);

            _title.text = meta?.DisplayName ?? entry.Name;
            _packageName.text = entry.Name;
            _description.text = meta?.Description ?? string.Empty;

            _badges.Clear();
            if (entry.Catalog != null)
            {
                var (text, modifier) = entry.Catalog.Access switch
                {
                    PackageAccess.Free => ("Free", "free"),
                    PackageAccess.Owned => ("Owned", "owned"),
                    _ => ("Paid", "paid"),
                };
                _badges.Add(Badge(text, modifier));
            }

            if (local != null)
            {
                _badges.Add(local.IsProtected ? Badge("Folder copy", null)
                    : HasUpdate(entry, local) ? Badge("Update available", "update")
                    : Badge("Installed", null));
            }

            RenderActions(entry, local);
            RenderLinks(entry);
            RenderMeta(entry, local, meta);
            RenderDependencies(meta);
            _keywords.text = meta?.Keywords is { Length: > 0 } keywords ? string.Join("  •  ", keywords) : "None";
        }

        private static Label Badge(string text, string modifier)
        {
            var badge = new Label(text);
            badge.AddToClassList("nz-pm-badge");
            if (modifier != null)
                badge.AddToClassList("nz-pm-badge--" + modifier);
            badge.style.marginLeft = 4;
            return badge;
        }

        private void RenderActions(Entry entry, InstalledPackage local)
        {
            var versions = entry.Document?.Versions.Keys.OrderByDescending(v => v, Comparer<string>.Create(SemVer.Compare)).ToList()
                ?? (entry.Catalog?.LatestAccessible != null ? new List<string> { entry.Catalog.LatestAccessible } : new List<string>());
            _versionField.choices = versions;
            _versionField.SetValueWithoutNotify(versions.Contains(_selectedVersion) ? _selectedVersion : versions.FirstOrDefault());
            _versionField.style.display = entry.CanDownload ? DisplayStyle.Flex : DisplayStyle.None;

            var selected = _versionField.value;
            var canInstall = entry.CanDownload && selected != null && entry.Document != null;
            var action = canInstall ? PackageAction.For(selected, local) : default;
            _actionButton.style.display = entry.CanDownload ? DisplayStyle.Flex : DisplayStyle.None;
            _actionButton.text = entry.Document == null ? "Loading..." : action.Label;
            _actionButton.SetEnabled(!IsBusy && canInstall && action.Kind != PackageActionKind.None);

            _removeButton.style.display = local is { IsManagedTarball: true } ? DisplayStyle.Flex : DisplayStyle.None;
            _removeButton.SetEnabled(!IsBusy);
            _cancelButton.style.display = IsBusy ? DisplayStyle.Flex : DisplayStyle.None;
            _progress.style.display = IsBusy ? DisplayStyle.Flex : DisplayStyle.None;

            _accessNote.text = AccessNote(entry, local);
            _accessNote.style.display = _accessNote.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static string AccessNote(Entry entry, InstalledPackage local)
        {
            if (local != null && local.IsProtected)
                return "This package is a folder in Packages/ or a local folder dependency. Update it manually; the manager never changes it.";
            if (entry.Catalog == null)
                return "Not listed by the registry right now. The installed copy keeps working.";

            switch (entry.Catalog.Access)
            {
                case PackageAccess.Free when string.IsNullOrEmpty(Token):
                    return "Free package. Sign in above to install it.";
                case PackageAccess.None:
                    return string.IsNullOrEmpty(Token)
                        ? "Paid package. If you bought it, sign in above."
                        : "Paid package, not owned by this account. Purchases are made on the website.";
                case PackageAccess.Owned when entry.Catalog.LatestAccessible == null:
                    return "Your purchase covers no released version yet.";
                case PackageAccess.Owned when entry.Catalog.LatestAccessible != entry.Catalog.Latest.Version:
                    return $"Version {entry.Catalog.Latest.Version} was released after your update period ended. "
                        + $"Your purchase includes everything up to {entry.Catalog.LatestAccessible}.";
                default:
                    return string.Empty;
            }
        }

        private void RenderLinks(Entry entry)
        {
            _links.Clear();
            foreach (var (kind, label) in DocKinds)
            {
                var button = new Button(() => ToggleDoc(kind)) { text = label };
                button.AddToClassList("nz-pm-link");
                if (_openDoc == kind)
                    button.AddToClassList("nz-pm-link--active");
                button.SetEnabled(entry.Catalog != null && entry.Catalog.Docs.Contains(kind));
                _links.Add(button);
            }

            _doc.style.display = _openDoc == null ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void ToggleDoc(string kind)
        {
            var entry = _selected;
            _openDoc = _openDoc == kind ? null : kind;
            RenderDetails();
            if (_openDoc == null || entry == null)
                return;

            // Docs come from the newest release unless an older owned version is selected.
            var version = entry.CanDownload && _versionField.value != null ? _versionField.value : entry.Latest?.Version;
            if (version == null)
                return;

            _ = ShowDocAsync(entry, version, kind);
        }

        private async Task ShowDocAsync(Entry entry, string version, string kind)
        {
            _doc.Clear();
            _doc.Add(new Label("Loading..."));
            var key = (entry.Name, version, kind);
            if (!_docs.TryGetValue(key, out var text))
            {
                try
                {
                    text = await Client.GetDocAsync(entry.Name, version, kind, CancellationToken.None);
                    _docs[key] = text;
                }
                catch (Exception e)
                {
                    text = null;
                    SetStatus(_status, $"Could not load the {kind}: {e.Message}", true);
                }
            }

            if (_selected != entry || _openDoc != kind)
                return;

            _doc.Clear();
            var label = new Label(text == null ? $"Version {version} does not include a {kind}." : Markdown.ToRichText(text))
            {
                enableRichText = true,
            };
            label.selection.isSelectable = true;
            label.AddToClassList("nz-pm-doc__text");
            _doc.Add(label);
        }

        private void RenderMeta(Entry entry, InstalledPackage local, PackageVersion meta)
        {
            _meta.Clear();
            AddMeta("Technical name", entry.Name);
            AddMeta("Installed", local == null ? "No" : $"{local.Version} ({Describe(local)})");
            if (entry.Catalog != null)
            {
                var published = DateTimeOffset.TryParse(entry.Catalog.PublishedAt, out var date) ? $" ({date.LocalDateTime:d})" : string.Empty;
                AddMeta("Latest release", entry.Catalog.Latest.Version + published);
            }

            AddMeta("Minimum Unity version", meta?.Unity ?? "Not specified");
            AddMeta("Author", string.IsNullOrEmpty(meta?.Author) ? "Enzisoft" : meta.Author);

            void AddMeta(string key, string value)
            {
                var row = new VisualElement();
                row.AddToClassList("nz-pm-meta-row");
                var keyLabel = new Label(key);
                keyLabel.AddToClassList("nz-pm-meta-key");
                var valueLabel = new Label(value);
                valueLabel.AddToClassList("nz-pm-meta-value");
                valueLabel.selection.isSelectable = true;
                row.Add(keyLabel);
                row.Add(valueLabel);
                _meta.Add(row);
            }
        }

        private void RenderDependencies(PackageVersion meta)
        {
            _dependencies.Clear();
            if (meta == null || meta.Dependencies.Count == 0)
            {
                _dependencies.Add(new Label("None"));
                return;
            }

            foreach (var (name, version) in meta.Dependencies.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                var row = new VisualElement();
                row.AddToClassList("nz-pm-meta-row");
                var nameLabel = new Label(name);
                nameLabel.AddToClassList("nz-pm-meta-value");
                var state = _installed.TryGetValue(name, out var local) ? $"{version}   (installed {local.Version})" : $"{version}   (not installed)";
                var versionLabel = new Label(state);
                versionLabel.AddToClassList("nz-pm-meta-key");
                versionLabel.style.width = 220;
                row.Add(nameLabel);
                row.Add(versionLabel);
                _dependencies.Add(row);
            }
        }

        private static string Describe(InstalledPackage local) => local.Source switch
        {
            InstalledSource.Embedded => "embedded folder",
            InstalledSource.LocalFolder => "local folder",
            InstalledSource.LocalTarball => local.IsManagedTarball ? "Packages/" + TarballFiles.FileName(local.Name) : "other .tgz",
            InstalledSource.Git => "git",
            InstalledSource.Registry => "scoped registry",
            _ => "other",
        };

        // ---------------------------------------------------------------- operations

        private Task InstallSelected()
        {
            var entry = _selected;
            if (entry?.Document == null || !entry.Document.Versions.TryGetValue(_versionField.value ?? string.Empty, out var version))
                return Task.CompletedTask;

            return RunAsync(_status, $"Preparing {version.Id}...", async cancellationToken =>
            {
                var plan = await InstallPlanner.PlanAsync(version, ProjectPackages.Read(), Client.GetPackageAsync, cancellationToken);
                foreach (var warning in plan.Warnings)
                    Debug.LogWarning($"Enzisoft Packages: {warning}");

                await ProjectPackages.InstallAsync(plan, Client, (message, value) => SetStatus(_status, message, progress: value), cancellationToken);
                _installed = ProjectPackages.Read();
                SetStatus(_status, $"Installed {string.Join(", ", plan.Packages.Select(p => p.Version.Id))}.");
            });
        }

        private Task RemoveSelected()
        {
            var name = _selected?.Name;
            if (name == null)
                return Task.CompletedTask;

            return RunAsync(_status, $"Removing {name}...", async _ =>
            {
                if (!EditorUtility.DisplayDialog("Remove package",
                        $"Remove {name} from the project and delete Packages/{TarballFiles.FileName(name)}?", "Remove", "Cancel"))
                {
                    SetStatus(_status, null);
                    return;
                }

                await ProjectPackages.RemoveAsync(name);
                _installed = ProjectPackages.Read();
                SetStatus(_status, $"Removed {name}.");
            });
        }

        /// <summary>Runs one operation at a time with progress, cancellation and error reporting.</summary>
        private async Task RunAsync(Label status, string message, Func<CancellationToken, Task> operation)
        {
            if (IsBusy)
                return;

            _operation = new CancellationTokenSource();
            SetStatus(status, message, progress: 0);
            UpdateBusy();
            try
            {
                await operation(_operation.Token);
            }
            catch (OperationCanceledException)
            {
                SetStatus(status, "Cancelled. Nothing was changed.");
            }
            catch (Exception e)
            {
                SetStatus(status, e.Message, true);
                Debug.LogException(e);
            }
            finally
            {
                _operation.Dispose();
                _operation = null;
                UpdateBusy();
                RebuildRows();
                RenderDetails();
            }
        }

        private void UpdateBusy()
        {
            if (_toolbar == null)
                return;

            _toolbar.SetEnabled(!IsBusy);
            _refreshButton.SetEnabled(!IsBusy);
            RenderDetails();
        }

        private void SetStatus(Label label, string message, bool error = false, float progress = -1)
        {
            if (label == null)
                return;

            label.text = message ?? string.Empty;
            label.EnableInClassList("nz-pm-error", error);
            if (progress >= 0)
                _progress.value = progress * 100;
        }
    }
}
