using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using OpcUaTrayClient.Core.Configuration;
using OpcUaTrayClient.Core.Models;
using OpcUaTrayClient.OpcUa;
using OpcUaTrayClient.Persistence;
using OpcUaTrayClient.Persistence.MongoDB;
using OpcUaTrayClient.WinForms.Controls;

namespace OpcUaTrayClient.WinForms.Forms;

/// <summary>
/// V2.0.0: Main configuration form with multi-server support.
/// Tabs: Serveurs (list + MongoDB), [Dynamic Server Tabs], Etat, Journaux
/// </summary>
public partial class ConfigForm : Form
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ConfigForm> _logger;
    private readonly ConfigurationService _configService;
    private readonly OpcUaClientManager _clientManager;
    private readonly MongoHealthMonitor _healthMonitor;
    private readonly DataPersistenceService _persistenceService;

    /// <summary>
    /// Event raised when user requests acquisition start.
    /// </summary>
    public event EventHandler? StartAcquisitionRequested;

    // Controls
    private TabControl _tabControl = null!;
    private readonly Dictionary<string, TabPage> _serverTabs = new();
    private readonly Dictionary<string, ServerTabPage> _serverTabPages = new();

    // Servers tab
    private ListView _listServers = null!;
    private Button _btnAddServer = null!;
    private Button _btnRemoveServer = null!;
    private Button _btnEditServer = null!;
    private TextBox _txtMongoConnectionString = null!;
    private TextBox _txtMongoDatabase = null!;
    private TextBox _txtMongoCollection = null!;
    private Button _btnTestMongo = null!;
    private Label _lblMongoStatus = null!;

    // Status tab
    private Label _lblOpcUaConnectionState = null!;
    private Label _lblPersistenceMode = null!;
    private Label _lblMongoHealth = null!;
    private Label _lblQueueDepth = null!;
    private Label _lblPointsPerSecond = null!;
    private Label _lblTotalAcquired = null!;
    private Label _lblTotalPersisted = null!;
    private Label _lblDropped = null!;
    private Label _lblConnectedServers = null!;
    private CheckBox _chkForceJsonOnly = null!;
    private CheckBox _chkDryRunMode = null!;
    private TextBox _txtJsonFallbackPath = null!;
    private Button _btnBrowseJsonPath = null!;

    // Logs tab
    private RichTextBox _txtLogs = null!;
    private Button _btnClearLogs = null!;

    public ConfigForm(IServiceProvider services)
    {
        _services = services;
        _logger = services.GetRequiredService<ILogger<ConfigForm>>();
        _configService = services.GetRequiredService<ConfigurationService>();
        _clientManager = services.GetRequiredService<OpcUaClientManager>();
        _healthMonitor = services.GetRequiredService<MongoHealthMonitor>();
        _persistenceService = services.GetRequiredService<DataPersistenceService>();

        InitializeComponent();
        LoadConfiguration();
        CreateServerTabs();

        // Subscribe to server state changes
        _clientManager.ServerConnectionStateChanged += OnServerConnectionStateChanged;
    }

    private void InitializeComponent()
    {
        // Get version from assembly
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var versionString = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "2.0.0";

        // Form properties
        Text = $"I.F OPC-UA CLIENT V.{versionString}";
        Size = new Size(900, 650);
        MinimumSize = new Size(700, 500);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;

        // Menu bar
        var menuStrip = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("Fichier");

        var saveMenuItem = new ToolStripMenuItem("Enregistrer", null, async (s, e) => await SaveConfigurationAsync());
        saveMenuItem.ShortcutKeys = Keys.Control | Keys.S;
        fileMenu.DropDownItems.Add(saveMenuItem);

        fileMenu.DropDownItems.Add(new ToolStripSeparator());

        var quitMenuItem = new ToolStripMenuItem("Quitter", null, (s, e) => Application.Exit());
        quitMenuItem.ShortcutKeys = Keys.Alt | Keys.F4;
        fileMenu.DropDownItems.Add(quitMenuItem);

        menuStrip.Items.Add(fileMenu);

        // Help menu
        var helpMenu = new ToolStripMenuItem("?");
        var aboutMenuItem = new ToolStripMenuItem("A propos", null, (s, e) =>
        {
            MessageBox.Show(
                $"I.F OPC-UA CLIENT\nVersion {versionString}\n\nClient OPC UA multi-serveur avec persistance MongoDB",
                "A propos",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        });
        helpMenu.DropDownItems.Add(aboutMenuItem);
        menuStrip.Items.Add(helpMenu);

        MainMenuStrip = menuStrip;

        // Save button at bottom
        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50
        };

        var btnSave = new Button
        {
            Text = "Enregistrer",
            Size = new Size(150, 30),
            Location = new Point(10, 10)
        };
        btnSave.Click += async (s, e) => await SaveConfigurationAsync();

        var btnClose = new Button
        {
            Text = "Fermer",
            Size = new Size(80, 30),
            Location = new Point(170, 10)
        };
        btnClose.Click += (s, e) => Close();

        buttonPanel.Controls.Add(btnSave);
        buttonPanel.Controls.Add(btnClose);

        // Tab control
        _tabControl = new TabControl
        {
            Dock = DockStyle.Fill
        };

        _tabControl.TabPages.Add(CreateServersTab());
        // Dynamic server tabs will be added after loading config
        _tabControl.TabPages.Add(CreateStatusTab());
        _tabControl.TabPages.Add(CreateLogsTab());

        // Add controls
        SuspendLayout();
        Controls.Add(_tabControl);
        Controls.Add(buttonPanel);
        Controls.Add(menuStrip);
        ResumeLayout(true);
    }

    private TabPage CreateServersTab()
    {
        var tab = new TabPage("Serveurs");
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10)
        };

        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        // ─── Server List Section ───
        var serverPanel = new GroupBox
        {
            Text = "Serveurs OPC UA",
            Dock = DockStyle.Fill
        };

        var serverLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(5)
        };
        serverLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        serverLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

        _listServers = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false
        };
        _listServers.Columns.Add("Nom", 150);
        _listServers.Columns.Add("URL", 300);
        _listServers.Columns.Add("Etat", 80);
        _listServers.Columns.Add("Abonnements", 80);
        _listServers.DoubleClick += (s, e) => EditSelectedServer();
        serverLayout.Controls.Add(_listServers, 0, 0);

        var buttonStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };

        _btnAddServer = new Button { Text = "Ajouter", Width = 100, Height = 28 };
        _btnAddServer.Click += async (s, e) => await AddServerAsync();
        buttonStack.Controls.Add(_btnAddServer);

        _btnEditServer = new Button { Text = "Modifier", Width = 100, Height = 28, Margin = new Padding(0, 5, 0, 0) };
        _btnEditServer.Click += (s, e) => EditSelectedServer();
        buttonStack.Controls.Add(_btnEditServer);

        _btnRemoveServer = new Button { Text = "Supprimer", Width = 100, Height = 28, Margin = new Padding(0, 5, 0, 0) };
        _btnRemoveServer.Click += async (s, e) => await RemoveSelectedServerAsync();
        buttonStack.Controls.Add(_btnRemoveServer);

        serverLayout.Controls.Add(buttonStack, 1, 0);
        serverPanel.Controls.Add(serverLayout);
        mainPanel.Controls.Add(serverPanel, 0, 0);

        // ─── MongoDB Section ───
        var mongoPanel = new GroupBox
        {
            Text = "MongoDB (stockage partage)",
            Dock = DockStyle.Fill
        };

        var mongoLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 5,
            Padding = new Padding(5)
        };
        mongoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        mongoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mongoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

        var row = 0;

        mongoLayout.Controls.Add(new Label { Text = "Chaine de connexion :", Anchor = AnchorStyles.Left }, 0, row);
        _txtMongoConnectionString = new TextBox { Dock = DockStyle.Fill };
        mongoLayout.Controls.Add(_txtMongoConnectionString, 1, row);
        _btnTestMongo = new Button { Text = "Tester", Dock = DockStyle.Fill };
        _btnTestMongo.Click += async (s, e) => await TestMongoConnectionAsync();
        mongoLayout.Controls.Add(_btnTestMongo, 2, row++);

        _lblMongoStatus = new Label { Text = "", ForeColor = Color.Gray, Dock = DockStyle.Fill };
        mongoLayout.Controls.Add(_lblMongoStatus, 1, row++);
        mongoLayout.SetColumnSpan(_lblMongoStatus, 2);

        mongoLayout.Controls.Add(new Label { Text = "Base de donnees :", Anchor = AnchorStyles.Left }, 0, row);
        _txtMongoDatabase = new TextBox { Dock = DockStyle.Fill };
        mongoLayout.Controls.Add(_txtMongoDatabase, 1, row++);

        mongoLayout.Controls.Add(new Label { Text = "Collection :", Anchor = AnchorStyles.Left }, 0, row);
        _txtMongoCollection = new TextBox { Dock = DockStyle.Fill };
        mongoLayout.Controls.Add(_txtMongoCollection, 1, row++);

        mongoPanel.Controls.Add(mongoLayout);
        mainPanel.Controls.Add(mongoPanel, 0, 1);

        tab.Controls.Add(mainPanel);
        return tab;
    }

    private TabPage CreateStatusTab()
    {
        var tab = new TabPage("Etat et diagnostics");
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 18,
            Padding = new Padding(10)
        };

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        var row = 0;

        // Status section
        var lblStatus = new Label { Text = "Etat", Font = new Font(Font, FontStyle.Bold), AutoSize = true };
        panel.Controls.Add(lblStatus, 0, row++);

        panel.Controls.Add(new Label { Text = "Connexion OPC UA :" }, 0, row);
        _lblOpcUaConnectionState = new Label { Text = "Deconnecte", ForeColor = Color.Gray };
        panel.Controls.Add(_lblOpcUaConnectionState, 1, row++);

        panel.Controls.Add(new Label { Text = "Serveurs connectes :" }, 0, row);
        _lblConnectedServers = new Label { Text = "0 / 0", ForeColor = Color.Gray };
        panel.Controls.Add(_lblConnectedServers, 1, row++);

        panel.Controls.Add(new Label { Text = "Mode de persistance :" }, 0, row);
        _lblPersistenceMode = new Label { Text = "Inconnu", ForeColor = Color.Gray };
        panel.Controls.Add(_lblPersistenceMode, 1, row++);

        panel.Controls.Add(new Label { Text = "Etat MongoDB :" }, 0, row);
        _lblMongoHealth = new Label { Text = "Inconnu", ForeColor = Color.Gray };
        panel.Controls.Add(_lblMongoHealth, 1, row++);

        // Separator
        panel.Controls.Add(new Label { Height = 20 }, 0, row++);

        // Statistics section
        var lblStats = new Label { Text = "Statistiques", Font = new Font(Font, FontStyle.Bold), AutoSize = true };
        panel.Controls.Add(lblStats, 0, row++);

        panel.Controls.Add(new Label { Text = "Profondeur de file :" }, 0, row);
        _lblQueueDepth = new Label { Text = "0" };
        panel.Controls.Add(_lblQueueDepth, 1, row++);

        panel.Controls.Add(new Label { Text = "Points/seconde :" }, 0, row);
        _lblPointsPerSecond = new Label { Text = "0" };
        panel.Controls.Add(_lblPointsPerSecond, 1, row++);

        panel.Controls.Add(new Label { Text = "Total acquis :" }, 0, row);
        _lblTotalAcquired = new Label { Text = "0" };
        panel.Controls.Add(_lblTotalAcquired, 1, row++);

        panel.Controls.Add(new Label { Text = "Total persiste :" }, 0, row);
        _lblTotalPersisted = new Label { Text = "0" };
        panel.Controls.Add(_lblTotalPersisted, 1, row++);

        panel.Controls.Add(new Label { Text = "Perdus :" }, 0, row);
        _lblDropped = new Label { Text = "0", ForeColor = Color.Red };
        panel.Controls.Add(_lblDropped, 1, row++);

        // Separator
        panel.Controls.Add(new Label { Height = 20 }, 0, row++);

        // Options section
        var lblOptions = new Label { Text = "Options", Font = new Font(Font, FontStyle.Bold), AutoSize = true };
        panel.Controls.Add(lblOptions, 0, row++);

        _chkForceJsonOnly = new CheckBox { Text = "Forcer le mode JSON uniquement", AutoSize = true };
        _chkForceJsonOnly.CheckedChanged += async (s, e) => await OnForceJsonOnlyChangedAsync();
        panel.Controls.Add(_chkForceJsonOnly, 0, row++);
        panel.SetColumnSpan(_chkForceJsonOnly, 3);

        _chkDryRunMode = new CheckBox { Text = "Mode test (sans persistance)", AutoSize = true };
        _chkDryRunMode.CheckedChanged += async (s, e) => await OnDryRunModeChangedAsync();
        panel.Controls.Add(_chkDryRunMode, 0, row++);
        panel.SetColumnSpan(_chkDryRunMode, 3);

        // JSON Fallback path
        var jsonPathPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 35,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 5, 0, 0)
        };

        jsonPathPanel.Controls.Add(new Label { Text = "Dossier JSON fallback :", AutoSize = true, Margin = new Padding(0, 6, 5, 0) });
        _txtJsonFallbackPath = new TextBox { Width = 400 };
        jsonPathPanel.Controls.Add(_txtJsonFallbackPath);
        _btnBrowseJsonPath = new Button { Text = "Parcourir", Width = 80, Height = 25, Margin = new Padding(5, 0, 0, 0) };
        _btnBrowseJsonPath.Click += OnBrowseJsonPath;
        jsonPathPanel.Controls.Add(_btnBrowseJsonPath);

        panel.Controls.Add(jsonPathPanel, 0, row);
        panel.SetColumnSpan(jsonPathPanel, 3);

        tab.Controls.Add(panel);
        return tab;
    }

    private TabPage CreateLogsTab()
    {
        var tab = new TabPage("Journaux");
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 35,
            FlowDirection = FlowDirection.LeftToRight
        };

        _btnClearLogs = new Button { Text = "Effacer", Width = 80 };
        _btnClearLogs.Click += (s, e) => _txtLogs.Clear();
        toolbar.Controls.Add(_btnClearLogs);

        var btnExportLogs = new Button { Text = "Exporter...", Width = 80 };
        btnExportLogs.Click += ExportLogs;
        toolbar.Controls.Add(btnExportLogs);

        panel.Controls.Add(toolbar);

        _txtLogs = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Consolas", 9),
            BackColor = Color.Black,
            ForeColor = Color.LightGray
        };

        panel.Controls.Add(_txtLogs);

        tab.Controls.Add(panel);
        return tab;
    }

    private void LoadConfiguration()
    {
        var config = _configService.Current;

        _txtMongoConnectionString.Text = config.MongoConnectionString;
        _txtMongoDatabase.Text = config.MongoDatabaseName;
        _txtMongoCollection.Text = config.MongoCollectionName;

        _chkForceJsonOnly.Checked = config.ForceJsonOnly;
        _chkDryRunMode.Checked = config.DryRunMode;

        _txtJsonFallbackPath.Text = !string.IsNullOrWhiteSpace(config.JsonFallbackPath)
            ? config.JsonFallbackPath
            : _configService.DataFolderPath;

        // Load server list
        RefreshServerList();
    }

    private void RefreshServerList()
    {
        _listServers.Items.Clear();

        foreach (var server in _configService.Current.Servers)
        {
            var state = _clientManager.GetServerState(server.Id);
            var item = new ListViewItem(server.Name)
            {
                Tag = server.Id
            };
            item.SubItems.Add(server.EndpointUrl);
            item.SubItems.Add(state.ToString());
            item.SubItems.Add(server.Subscriptions.Count.ToString());

            // Color based on state
            item.ForeColor = state switch
            {
                OpcUaConnectionState.Connected => Color.Green,
                OpcUaConnectionState.Connecting or OpcUaConnectionState.Reconnecting => Color.Orange,
                OpcUaConnectionState.Error => Color.Red,
                _ => Color.Gray
            };

            _listServers.Items.Add(item);
        }
    }

    private void CreateServerTabs()
    {
        // Remove old server tabs
        foreach (var tabPage in _serverTabs.Values)
        {
            _tabControl.TabPages.Remove(tabPage);
        }
        _serverTabs.Clear();
        _serverTabPages.Clear();

        // Create new tabs for each server
        var insertIndex = 1; // After "Serveurs" tab
        foreach (var server in _configService.Current.Servers)
        {
            var serverTabPage = new ServerTabPage(_clientManager, _configService, server.Id)
            {
                Dock = DockStyle.Fill,
                LogMessage = AppendLog
            };

            var tabPage = new TabPage(server.Name);
            tabPage.Controls.Add(serverTabPage);

            _tabControl.TabPages.Insert(insertIndex++, tabPage);
            _serverTabs[server.Id] = tabPage;
            _serverTabPages[server.Id] = serverTabPage;
        }
    }

    private async Task AddServerAsync()
    {
        using var dialog = new ServerEditDialog(null);
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            var newServer = dialog.ServerConfig;
            await _configService.AddServerAsync(newServer);

            RefreshServerList();
            CreateServerTabs();

            AppendLog($"Serveur ajoute: {newServer.Name}", Color.Green);
        }
    }

    private void EditSelectedServer()
    {
        if (_listServers.SelectedItems.Count == 0) return;

        var serverId = _listServers.SelectedItems[0].Tag as string;
        if (string.IsNullOrEmpty(serverId)) return;

        var server = _configService.GetServer(serverId);
        if (server == null) return;

        using var dialog = new ServerEditDialog(server);
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _ = _configService.UpdateServerAsync(serverId, s =>
            {
                s.Name = dialog.ServerConfig.Name;
                s.EndpointUrl = dialog.ServerConfig.EndpointUrl;
                s.Enabled = dialog.ServerConfig.Enabled;
            });

            RefreshServerList();

            // Update tab name
            if (_serverTabs.TryGetValue(serverId, out var tabPage))
            {
                tabPage.Text = dialog.ServerConfig.Name;
            }

            AppendLog($"Serveur modifie: {dialog.ServerConfig.Name}", Color.Green);
        }
    }

    private async Task RemoveSelectedServerAsync()
    {
        if (_listServers.SelectedItems.Count == 0) return;

        var serverId = _listServers.SelectedItems[0].Tag as string;
        if (string.IsNullOrEmpty(serverId)) return;

        var server = _configService.GetServer(serverId);
        if (server == null) return;

        var result = MessageBox.Show(
            $"Supprimer le serveur '{server.Name}' et tous ses abonnements ?",
            "Confirmation",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
        {
            await _clientManager.RemoveServerAsync(serverId);
            await _configService.RemoveServerAsync(serverId);

            RefreshServerList();
            CreateServerTabs();

            AppendLog($"Serveur supprime: {server.Name}", Color.Orange);
        }
    }

    private void OnBrowseJsonPath(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Selectionner le dossier pour le fallback JSON",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (!string.IsNullOrEmpty(_txtJsonFallbackPath.Text) && Directory.Exists(_txtJsonFallbackPath.Text))
        {
            dialog.InitialDirectory = _txtJsonFallbackPath.Text;
        }

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _txtJsonFallbackPath.Text = dialog.SelectedPath;
        }
    }

    private async Task SaveConfigurationAsync()
    {
        try
        {
            // Validate JSON fallback path if specified
            var jsonPath = _txtJsonFallbackPath.Text.Trim();
            if (!string.IsNullOrEmpty(jsonPath) && jsonPath != _configService.DataFolderPath)
            {
                if (!Directory.Exists(jsonPath))
                {
                    try
                    {
                        Directory.CreateDirectory(jsonPath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Impossible de creer le dossier JSON :\n{ex.Message}", "Erreur",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
            }

            await _configService.UpdateAsync(config =>
            {
                config.MongoConnectionString = _txtMongoConnectionString.Text;
                config.MongoDatabaseName = _txtMongoDatabase.Text;
                config.MongoCollectionName = _txtMongoCollection.Text;
                config.ForceJsonOnly = _chkForceJsonOnly.Checked;
                config.DryRunMode = _chkDryRunMode.Checked;

                var defaultPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OpcUaTrayClient", "Data");
                config.JsonFallbackPath = jsonPath != defaultPath ? jsonPath : "";
            });

            AppendLog("Configuration enregistree", Color.Green);
            MessageBox.Show("Configuration enregistree avec succes.", "Succes", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"Echec de l'enregistrement : {ex.Message}", Color.Red);
            MessageBox.Show($"Echec de l'enregistrement de la configuration :\n{ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task TestMongoConnectionAsync()
    {
        _btnTestMongo.Enabled = false;
        _lblMongoStatus.Text = "Test en cours...";
        _lblMongoStatus.ForeColor = Color.Gray;

        try
        {
            var settings = MongoClientSettings.FromConnectionString(_txtMongoConnectionString.Text);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
            settings.ConnectTimeout = TimeSpan.FromSeconds(5);

            var client = new MongoClient(settings);
            var database = client.GetDatabase(_txtMongoDatabase.Text);

            var collection = database.GetCollection<MongoDB.Bson.BsonDocument>(_txtMongoCollection.Text);
            var testDoc = new MongoDB.Bson.BsonDocument { { "test", DateTime.UtcNow } };

            await collection.InsertOneAsync(testDoc);
            await collection.DeleteOneAsync(MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", testDoc["_id"]));

            _lblMongoStatus.Text = "Connexion reussie !";
            _lblMongoStatus.ForeColor = Color.Green;
            AppendLog("Test de connexion MongoDB reussi", Color.Green);
        }
        catch (Exception ex)
        {
            _lblMongoStatus.Text = $"Echec : {ex.Message}";
            _lblMongoStatus.ForeColor = Color.Red;
            AppendLog($"Echec du test de connexion MongoDB : {ex.Message}", Color.Red);
        }
        finally
        {
            _btnTestMongo.Enabled = true;
        }
    }

    private void OnServerConnectionStateChanged(object? sender, ServerConnectionStateChangedEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnServerConnectionStateChanged(sender, e));
            return;
        }

        // Update server list
        RefreshServerList();

        // Update server tab status
        if (_serverTabPages.TryGetValue(e.ServerId, out var serverTabPage))
        {
            serverTabPage.UpdateServerStatus();
        }

        AppendLog($"Serveur {e.ServerName}: {e.OldState} -> {e.NewState}",
            e.NewState == OpcUaConnectionState.Connected ? Color.Green : Color.Orange);
    }

    private async Task OnForceJsonOnlyChangedAsync()
    {
        await _configService.UpdateAsync(c => c.ForceJsonOnly = _chkForceJsonOnly.Checked);

        if (_chkForceJsonOnly.Checked)
        {
            _persistenceService.ForceMode(PersistenceMode.JsonFallback);
            AppendLog("Mode force : JSON uniquement", Color.Orange);
        }
        else if (!_chkDryRunMode.Checked)
        {
            _persistenceService.ForceMode(PersistenceMode.MongoDB);
            AppendLog("Mode force : MongoDB", Color.Green);
        }
    }

    private async Task OnDryRunModeChangedAsync()
    {
        await _configService.UpdateAsync(c => c.DryRunMode = _chkDryRunMode.Checked);

        if (_chkDryRunMode.Checked)
        {
            _persistenceService.ForceMode(PersistenceMode.DryRun);
            AppendLog("Mode force : Test (sans persistance)", Color.Gray);
        }
        else if (_chkForceJsonOnly.Checked)
        {
            _persistenceService.ForceMode(PersistenceMode.JsonFallback);
            AppendLog("Mode force : JSON uniquement", Color.Orange);
        }
        else
        {
            _persistenceService.ForceMode(PersistenceMode.MongoDB);
            AppendLog("Mode force : MongoDB", Color.Green);
        }
    }

    private void ExportLogs(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Fichiers texte|*.txt|Tous les fichiers|*.*",
            FileName = $"journaux_opcua_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            File.WriteAllText(dialog.FileName, _txtLogs.Text);
            AppendLog($"Journaux exportes vers {dialog.FileName}", Color.Green);
        }
    }

    /// <summary>
    /// Updates the status display with the latest status information.
    /// Called from TrayApplicationContext.
    /// </summary>
    public void UpdateStatus(StatusUpdate update)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateStatus(update));
            return;
        }

        _lblOpcUaConnectionState.Text = update.ConnectionState.ToString();
        _lblOpcUaConnectionState.ForeColor = update.ConnectionState switch
        {
            OpcUaConnectionState.Connected => Color.Green,
            OpcUaConnectionState.Connecting or OpcUaConnectionState.Reconnecting => Color.Orange,
            _ => Color.Red
        };

        _lblConnectedServers.Text = $"{update.ConnectedServerCount} / {update.TotalServerCount}";
        _lblConnectedServers.ForeColor = update.ConnectedServerCount == update.TotalServerCount && update.TotalServerCount > 0
            ? Color.Green
            : (update.ConnectedServerCount > 0 ? Color.Orange : Color.Red);

        _lblPersistenceMode.Text = update.PersistenceMode.ToString();
        _lblPersistenceMode.ForeColor = update.PersistenceMode switch
        {
            PersistenceMode.MongoDB => Color.Green,
            PersistenceMode.JsonFallback => Color.Orange,
            PersistenceMode.DryRun => Color.Gray,
            _ => Color.Red
        };

        _lblMongoHealth.Text = update.MongoHealth.ToString();
        _lblMongoHealth.ForeColor = update.MongoHealth switch
        {
            StorageHealth.Healthy => Color.Green,
            StorageHealth.Degraded => Color.Orange,
            StorageHealth.Unhealthy => Color.Red,
            _ => Color.Gray
        };

        _lblQueueDepth.Text = update.QueueDepth.ToString("N0");
        _lblPointsPerSecond.Text = update.DataPointsPerSecond.ToString("N0");
        _lblTotalAcquired.Text = update.TotalDataPointsReceived.ToString("N0");
        _lblTotalPersisted.Text = update.TotalDataPointsPersisted.ToString("N0");
        _lblDropped.Text = update.DroppedCount.ToString("N0");
        _lblDropped.ForeColor = update.DroppedCount > 0 ? Color.Red : Color.Green;
    }

    private const int MaxLogLines = 1000;

    private void AppendLog(string message, Color color)
    {
        if (_txtLogs.InvokeRequired)
        {
            _txtLogs.BeginInvoke(() => AppendLog(message, color));
            return;
        }

        if (_txtLogs.Lines.Length > MaxLogLines)
        {
            var linesToRemove = _txtLogs.Lines.Length - MaxLogLines + 100;
            var firstLineLength = 0;
            for (int i = 0; i < linesToRemove && i < _txtLogs.Lines.Length; i++)
            {
                firstLineLength += _txtLogs.Lines[i].Length + 1;
            }

            _txtLogs.Select(0, firstLineLength);
            _txtLogs.SelectedText = "";
        }

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _txtLogs.SelectionStart = _txtLogs.TextLength;
        _txtLogs.SelectionLength = 0;
        _txtLogs.SelectionColor = color;
        _txtLogs.AppendText($"[{timestamp}] {message}\n");
        _txtLogs.ScrollToCaret();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clientManager.ServerConnectionStateChanged -= OnServerConnectionStateChanged;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// V2.0.0: Dialog for adding/editing server configuration with connection test.
/// </summary>
public class ServerEditDialog : Form
{
    private TextBox _txtName = null!;
    private TextBox _txtEndpoint = null!;
    private CheckBox _chkEnabled = null!;
    private Button _btnTest = null!;
    private Label _lblTestStatus = null!;

    public OpcUaServerConfiguration ServerConfig { get; private set; }

    public ServerEditDialog(OpcUaServerConfiguration? existingServer)
    {
        ServerConfig = existingServer ?? new OpcUaServerConfiguration();
        InitializeComponent();
        LoadValues();
    }

    private void InitializeComponent()
    {
        Text = string.IsNullOrEmpty(ServerConfig.Id) ? "Ajouter un serveur" : "Modifier le serveur";
        Size = new Size(550, 250);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 6,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

        var row = 0;

        layout.Controls.Add(new Label { Text = "Nom :", Anchor = AnchorStyles.Left }, 0, row);
        _txtName = new TextBox { Dock = DockStyle.Fill };
        layout.Controls.Add(_txtName, 1, row);
        layout.SetColumnSpan(_txtName, 2);
        row++;

        layout.Controls.Add(new Label { Text = "URL :", Anchor = AnchorStyles.Left }, 0, row);
        _txtEndpoint = new TextBox { Dock = DockStyle.Fill };
        layout.Controls.Add(_txtEndpoint, 1, row);
        _btnTest = new Button { Text = "Tester", Dock = DockStyle.Fill };
        _btnTest.Click += async (s, e) => await TestConnectionAsync();
        layout.Controls.Add(_btnTest, 2, row);
        row++;

        _lblTestStatus = new Label { Text = "", Dock = DockStyle.Fill, ForeColor = Color.Gray };
        layout.Controls.Add(_lblTestStatus, 1, row);
        layout.SetColumnSpan(_lblTestStatus, 2);
        row++;

        _chkEnabled = new CheckBox { Text = "Active", Checked = true };
        layout.Controls.Add(_chkEnabled, 1, row);
        row++;

        // Spacer
        layout.Controls.Add(new Label(), 0, row++);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft
        };

        var btnCancel = new Button { Text = "Annuler", Width = 80, DialogResult = DialogResult.Cancel };
        buttonPanel.Controls.Add(btnCancel);

        var btnOk = new Button { Text = "OK", Width = 80 };
        btnOk.Click += OnOkClick;
        buttonPanel.Controls.Add(btnOk);

        layout.Controls.Add(buttonPanel, 0, row);
        layout.SetColumnSpan(buttonPanel, 3);

        Controls.Add(layout);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void LoadValues()
    {
        _txtName.Text = ServerConfig.Name;
        _txtEndpoint.Text = ServerConfig.EndpointUrl;
        _chkEnabled.Checked = ServerConfig.Enabled;
    }

    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(_txtEndpoint.Text))
        {
            MessageBox.Show("L'URL est requise pour tester la connexion.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnTest.Enabled = false;
        _lblTestStatus.Text = "Test en cours...";
        _lblTestStatus.ForeColor = Color.Gray;

        try
        {
            // Create a temporary client to test the connection
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // Use OPC UA SDK directly for testing
            var endpointUrl = _txtEndpoint.Text.Trim();
            var config = new Opc.Ua.ApplicationConfiguration
            {
                ApplicationName = "OpcUaTrayClient_Test",
                ApplicationType = Opc.Ua.ApplicationType.Client,
                SecurityConfiguration = new Opc.Ua.SecurityConfiguration
                {
                    ApplicationCertificate = new Opc.Ua.CertificateIdentifier(),
                    AutoAcceptUntrustedCertificates = true
                },
                ClientConfiguration = new Opc.Ua.ClientConfiguration { DefaultSessionTimeout = 10000 }
            };
            await config.Validate(Opc.Ua.ApplicationType.Client);

            var endpoint = Opc.Ua.CoreClientUtils.SelectEndpoint(endpointUrl, useSecurity: false, 5000);
            var endpointConfig = Opc.Ua.EndpointConfiguration.Create(config);
            var configuredEndpoint = new Opc.Ua.ConfiguredEndpoint(null, endpoint, endpointConfig);

            using var session = await Opc.Ua.Client.Session.Create(
                config,
                configuredEndpoint,
                false,
                "TestSession",
                10000,
                new Opc.Ua.UserIdentity(new Opc.Ua.AnonymousIdentityToken()),
                null);

            // Try to browse root to verify connection works
            var browseResult = session.Browse(
                null,
                null,
                Opc.Ua.ObjectIds.ObjectsFolder,
                0u,
                Opc.Ua.BrowseDirection.Forward,
                Opc.Ua.ReferenceTypeIds.HierarchicalReferences,
                true,
                0,
                out _,
                out var references);

            await session.CloseAsync();

            var nodeCount = references?.Count ?? 0;
            _lblTestStatus.Text = $"Connexion reussie ! {nodeCount} noeuds trouves.";
            _lblTestStatus.ForeColor = Color.Green;
        }
        catch (Exception ex)
        {
            _lblTestStatus.Text = $"Echec : {ex.Message}";
            _lblTestStatus.ForeColor = Color.Red;
        }
        finally
        {
            _btnTest.Enabled = true;
        }
    }

    private void OnOkClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_txtName.Text))
        {
            MessageBox.Show("Le nom est requis.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(_txtEndpoint.Text))
        {
            MessageBox.Show("L'URL est requise.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ServerConfig.Name = _txtName.Text.Trim();
        ServerConfig.EndpointUrl = _txtEndpoint.Text.Trim();
        ServerConfig.Enabled = _chkEnabled.Checked;

        DialogResult = DialogResult.OK;
        Close();
    }
}
