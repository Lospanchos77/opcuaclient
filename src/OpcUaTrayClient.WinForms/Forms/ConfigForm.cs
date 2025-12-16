using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using OpcUaTrayClient.Core.Configuration;
using OpcUaTrayClient.Core.Models;
using OpcUaTrayClient.OpcUa;
using OpcUaTrayClient.Persistence;
using OpcUaTrayClient.Persistence.MongoDB;

namespace OpcUaTrayClient.WinForms.Forms;

/// <summary>
/// Main configuration form with tabs for Connection, Subscriptions, Status, and Logs.
/// </summary>
public partial class ConfigForm : Form
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ConfigForm> _logger;
    private readonly ConfigurationService _configService;
    private readonly OpcUaClientService _opcUaClient;
    private readonly MongoHealthMonitor _healthMonitor;
    private readonly DataPersistenceService _persistenceService;

    /// <summary>
    /// Événement déclenché quand l'utilisateur demande le démarrage de l'acquisition.
    /// </summary>
    public event EventHandler? StartAcquisitionRequested;

    // Controls
    private TabControl _tabControl = null!;

    // Connection tab
    private TextBox _txtOpcUaEndpoint = null!;
    private Button _btnTestOpcUa = null!;
    private Label _lblOpcUaStatus = null!;
    private TextBox _txtMongoConnectionString = null!;
    private TextBox _txtMongoDatabase = null!;
    private TextBox _txtMongoCollection = null!;
    private Button _btnTestMongo = null!;
    private Label _lblMongoStatus = null!;

    // Subscriptions tab
    private TreeView _treeNodes = null!;
    private Button _btnRefreshNodes = null!;
    private DataGridView _gridSubscriptions = null!;
    private NumericUpDown _numSamplingInterval = null!;
    private NumericUpDown _numPublishingInterval = null!;
    private Button _btnAddSubscription = null!;
    private Button _btnRemoveSubscription = null!;

    // Status tab
    private Label _lblOpcUaConnectionState = null!;
    private Label _lblPersistenceMode = null!;
    private Label _lblMongoHealth = null!;
    private Label _lblQueueDepth = null!;
    private Label _lblPointsPerSecond = null!;
    private Label _lblTotalAcquired = null!;
    private Label _lblTotalPersisted = null!;
    private Label _lblDropped = null!;
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
        _opcUaClient = services.GetRequiredService<OpcUaClientService>();
        _healthMonitor = services.GetRequiredService<MongoHealthMonitor>();
        _persistenceService = services.GetRequiredService<DataPersistenceService>();

        InitializeComponent();
        LoadConfiguration();
    }

    private void InitializeComponent()
    {
        // Get version from assembly
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var versionString = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";

        // Form properties
        Text = $"I.F OPC-UA CLIENT V.{versionString}";
        Size = new Size(800, 600);
        MinimumSize = new Size(600, 400);
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
                $"I.F OPC-UA CLIENT\nVersion {versionString}\n\nClient OPC UA avec persistance MongoDB",
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

        _tabControl.TabPages.Add(CreateConnectionTab());
        _tabControl.TabPages.Add(CreateSubscriptionsTab());
        _tabControl.TabPages.Add(CreateStatusTab());
        _tabControl.TabPages.Add(CreateLogsTab());

        // Add controls - TabControl (Fill) first, then docked panels
        // This ensures TabControl fills remaining space after other docks are applied
        SuspendLayout();
        Controls.Add(_tabControl);
        Controls.Add(buttonPanel);
        Controls.Add(menuStrip);
        ResumeLayout(true);
    }

    private TabPage CreateConnectionTab()
    {
        var tab = new TabPage("Connexion");
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 10,
            Padding = new Padding(10)
        };

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

        var row = 0;

        // OPC UA Section
        var lblOpcUa = new Label { Text = "Connexion OPC UA", Font = new Font(Font, FontStyle.Bold), AutoSize = true };
        panel.Controls.Add(lblOpcUa, 0, row++);

        panel.Controls.Add(new Label { Text = "URL du serveur :", Anchor = AnchorStyles.Left }, 0, row);
        _txtOpcUaEndpoint = new TextBox { Dock = DockStyle.Fill };
        panel.Controls.Add(_txtOpcUaEndpoint, 1, row);
        _btnTestOpcUa = new Button { Text = "Tester", Dock = DockStyle.Fill };
        _btnTestOpcUa.Click += async (s, e) => await TestOpcUaConnectionAsync();
        panel.Controls.Add(_btnTestOpcUa, 2, row++);

        _lblOpcUaStatus = new Label { Text = "", ForeColor = Color.Gray, Dock = DockStyle.Fill };
        panel.Controls.Add(_lblOpcUaStatus, 1, row++);
        panel.SetColumnSpan(_lblOpcUaStatus, 2);

        // Separator
        panel.Controls.Add(new Label { Height = 20 }, 0, row++);

        // MongoDB Section
        var lblMongo = new Label { Text = "Connexion MongoDB", Font = new Font(Font, FontStyle.Bold), AutoSize = true };
        panel.Controls.Add(lblMongo, 0, row++);

        panel.Controls.Add(new Label { Text = "Chaîne de connexion :", Anchor = AnchorStyles.Left }, 0, row);
        _txtMongoConnectionString = new TextBox { Dock = DockStyle.Fill };
        panel.Controls.Add(_txtMongoConnectionString, 1, row);
        _btnTestMongo = new Button { Text = "Tester", Dock = DockStyle.Fill };
        _btnTestMongo.Click += async (s, e) => await TestMongoConnectionAsync();
        panel.Controls.Add(_btnTestMongo, 2, row++);

        _lblMongoStatus = new Label { Text = "", ForeColor = Color.Gray, Dock = DockStyle.Fill };
        panel.Controls.Add(_lblMongoStatus, 1, row++);
        panel.SetColumnSpan(_lblMongoStatus, 2);

        panel.Controls.Add(new Label { Text = "Base de données :", Anchor = AnchorStyles.Left }, 0, row);
        _txtMongoDatabase = new TextBox { Dock = DockStyle.Fill };
        panel.Controls.Add(_txtMongoDatabase, 1, row++);

        panel.Controls.Add(new Label { Text = "Collection :", Anchor = AnchorStyles.Left }, 0, row);
        _txtMongoCollection = new TextBox { Dock = DockStyle.Fill };
        panel.Controls.Add(_txtMongoCollection, 1, row++);

        tab.Controls.Add(panel);
        return tab;
    }

    private TabPage CreateSubscriptionsTab()
    {
        var tab = new TabPage("Abonnements");
        var splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,  // Haut/Bas au lieu de Gauche/Droite
            SplitterDistance = 250,
            Panel1MinSize = 150,
            Panel2MinSize = 150
        };

        // Top panel - TreeView for browsing nodes
        var topPanel = new Panel { Dock = DockStyle.Fill };

        var headerPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        var lblBrowse = new Label
        {
            Text = "Parcourir les nœuds OPC UA",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 6, 10, 0)
        };
        headerPanel.Controls.Add(lblBrowse);

        _btnRefreshNodes = new Button
        {
            Text = "Actualiser",
            Width = 100,
            Height = 25
        };
        _btnRefreshNodes.Click += async (s, e) => await RefreshNodesAsync();
        headerPanel.Controls.Add(_btnRefreshNodes);

        topPanel.Controls.Add(headerPanel);

        _treeNodes = new TreeView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true
        };
        _treeNodes.BeforeExpand += async (s, e) => await OnNodeExpandingAsync(e);
        _treeNodes.AfterCheck += OnNodeAfterCheck;
        topPanel.Controls.Add(_treeNodes);

        // Important: Add TreeView first so it fills remaining space
        topPanel.Controls.SetChildIndex(_treeNodes, 0);
        topPanel.Controls.SetChildIndex(headerPanel, 1);

        splitContainer.Panel1.Controls.Add(topPanel);

        // Bottom panel - Subscription grid and controls
        var bottomPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0) };

        // Settings panel with all controls on one line
        var settingsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 35,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        settingsPanel.Controls.Add(new Label { Text = "Échant. (ms):", AutoSize = true, Margin = new Padding(0, 8, 3, 0) });
        _numSamplingInterval = new NumericUpDown { Minimum = 10, Maximum = 60000, Value = 100, Width = 60 };
        settingsPanel.Controls.Add(_numSamplingInterval);

        settingsPanel.Controls.Add(new Label { Text = "Publ. (ms):", AutoSize = true, Margin = new Padding(10, 8, 3, 0) });
        _numPublishingInterval = new NumericUpDown { Minimum = 100, Maximum = 60000, Value = 500, Width = 60 };
        settingsPanel.Controls.Add(_numPublishingInterval);

        _btnAddSubscription = new Button { Text = "Ajouter", Width = 80, Height = 28, Margin = new Padding(15, 3, 3, 0) };
        _btnAddSubscription.Click += async (s, e) => await AddSelectedSubscriptionsAsync();
        settingsPanel.Controls.Add(_btnAddSubscription);

        _btnRemoveSubscription = new Button { Text = "Supprimer", Width = 80, Height = 28, Margin = new Padding(5, 3, 0, 0) };
        _btnRemoveSubscription.Click += async (s, e) => await RemoveSelectedSubscriptionsAsync();
        settingsPanel.Controls.Add(_btnRemoveSubscription);

        bottomPanel.Controls.Add(settingsPanel);

        _gridSubscriptions = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AllowUserToAddRows = false,
            ReadOnly = true
        };

        _gridSubscriptions.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "NodeId", HeaderText = "ID nœud", Width = 150 });
        _gridSubscriptions.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "BrowsePath", HeaderText = "Chemin", Width = 250 });
        _gridSubscriptions.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "SamplingIntervalMs", HeaderText = "Échant.", Width = 60 });
        _gridSubscriptions.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Enabled", HeaderText = "Actif", Width = 50 });

        bottomPanel.Controls.Add(_gridSubscriptions);

        // Important: Add grid first so it fills remaining space
        bottomPanel.Controls.SetChildIndex(_gridSubscriptions, 0);
        bottomPanel.Controls.SetChildIndex(settingsPanel, 1);

        splitContainer.Panel2.Controls.Add(bottomPanel);

        tab.Controls.Add(splitContainer);
        return tab;
    }

    private TabPage CreateStatusTab()
    {
        var tab = new TabPage("État et diagnostics");
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 16,
            Padding = new Padding(10)
        };

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        var row = 0;

        // Status section
        var lblStatus = new Label { Text = "État", Font = new Font(Font, FontStyle.Bold), AutoSize = true };
        panel.Controls.Add(lblStatus, 0, row++);

        panel.Controls.Add(new Label { Text = "Connexion OPC UA :" }, 0, row);
        _lblOpcUaConnectionState = new Label { Text = "Déconnecté", ForeColor = Color.Gray };
        panel.Controls.Add(_lblOpcUaConnectionState, 1, row++);

        panel.Controls.Add(new Label { Text = "Mode de persistance :" }, 0, row);
        _lblPersistenceMode = new Label { Text = "Inconnu", ForeColor = Color.Gray };
        panel.Controls.Add(_lblPersistenceMode, 1, row++);

        panel.Controls.Add(new Label { Text = "État MongoDB :" }, 0, row);
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

        panel.Controls.Add(new Label { Text = "Total persisté :" }, 0, row);
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

    private void OnBrowseJsonPath(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Sélectionner le dossier pour le fallback JSON",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        // Set initial path if exists
        if (!string.IsNullOrEmpty(_txtJsonFallbackPath.Text) && Directory.Exists(_txtJsonFallbackPath.Text))
        {
            dialog.InitialDirectory = _txtJsonFallbackPath.Text;
        }

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _txtJsonFallbackPath.Text = dialog.SelectedPath;
        }
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

        _txtOpcUaEndpoint.Text = config.OpcUaEndpointUrl;
        _txtMongoConnectionString.Text = config.MongoConnectionString;
        _txtMongoDatabase.Text = config.MongoDatabaseName;
        _txtMongoCollection.Text = config.MongoCollectionName;

        _chkForceJsonOnly.Checked = config.ForceJsonOnly;
        _chkDryRunMode.Checked = config.DryRunMode;

        // JSON fallback path - show actual path used (custom or default)
        _txtJsonFallbackPath.Text = !string.IsNullOrWhiteSpace(config.JsonFallbackPath)
            ? config.JsonFallbackPath
            : _configService.DataFolderPath;

        // Load subscriptions grid
        RefreshSubscriptionsGrid();
    }

    private async Task SaveConfigurationAsync()
    {
        try
        {
            // Validate JSON fallback path if specified
            var jsonPath = _txtJsonFallbackPath.Text.Trim();
            if (!string.IsNullOrEmpty(jsonPath) && jsonPath != _configService.DataFolderPath)
            {
                // Try to create directory if it doesn't exist
                if (!Directory.Exists(jsonPath))
                {
                    try
                    {
                        Directory.CreateDirectory(jsonPath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Impossible de créer le dossier JSON :\n{ex.Message}", "Erreur",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
            }

            await _configService.UpdateAsync(config =>
            {
                config.OpcUaEndpointUrl = _txtOpcUaEndpoint.Text;
                config.MongoConnectionString = _txtMongoConnectionString.Text;
                config.MongoDatabaseName = _txtMongoDatabase.Text;
                config.MongoCollectionName = _txtMongoCollection.Text;
                config.ForceJsonOnly = _chkForceJsonOnly.Checked;
                config.DryRunMode = _chkDryRunMode.Checked;

                // Save custom path only if different from default
                var defaultPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OpcUaTrayClient", "Data");
                config.JsonFallbackPath = jsonPath != defaultPath ? jsonPath : "";
            });

            AppendLog("Configuration enregistrée", Color.Green);
            MessageBox.Show("Configuration enregistrée avec succès.", "Succès", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"Échec de l'enregistrement : {ex.Message}", Color.Red);
            MessageBox.Show($"Échec de l'enregistrement de la configuration :\n{ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task TestOpcUaConnectionAsync()
    {
        _btnTestOpcUa.Enabled = false;
        _lblOpcUaStatus.Text = "Test en cours...";
        _lblOpcUaStatus.ForeColor = Color.Gray;

        try
        {
            // Create temporary client for testing
            var testClient = new OpcUaClientService(
                _services.GetRequiredService<Core.Channel.DataPointChannel>(),
                _services.GetRequiredService<ILogger<OpcUaClientService>>());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await testClient.ConnectAsync(_txtOpcUaEndpoint.Text, cts.Token);

            // Try to browse root
            var nodes = await testClient.BrowseAsync(null, cts.Token);

            await testClient.DisconnectAsync();
            testClient.Dispose();

            _lblOpcUaStatus.Text = $"Connecté ! {nodes.Count} nœuds racines trouvés.";
            _lblOpcUaStatus.ForeColor = Color.Green;
            AppendLog($"Test de connexion OPC UA réussi : {nodes.Count} nœuds racines", Color.Green);

            // Proposer de démarrer l'acquisition
            var result = MessageBox.Show(
                "Connexion au serveur OPC UA réussie !\n\nVoulez-vous démarrer l'acquisition maintenant ?",
                "Connexion réussie",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                StartAcquisitionRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _lblOpcUaStatus.Text = $"Échec : {ex.Message}";
            _lblOpcUaStatus.ForeColor = Color.Red;
            AppendLog($"Échec du test de connexion OPC UA : {ex.Message}", Color.Red);
        }
        finally
        {
            _btnTestOpcUa.Enabled = true;
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

            // Insert and delete test document
            var collection = database.GetCollection<MongoDB.Bson.BsonDocument>(_txtMongoCollection.Text);
            var testDoc = new MongoDB.Bson.BsonDocument { { "test", DateTime.UtcNow } };

            await collection.InsertOneAsync(testDoc);
            await collection.DeleteOneAsync(MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", testDoc["_id"]));

            _lblMongoStatus.Text = "Connexion réussie !";
            _lblMongoStatus.ForeColor = Color.Green;
            AppendLog("Test de connexion MongoDB réussi", Color.Green);
        }
        catch (Exception ex)
        {
            _lblMongoStatus.Text = $"Échec : {ex.Message}";
            _lblMongoStatus.ForeColor = Color.Red;
            AppendLog($"Échec du test de connexion MongoDB : {ex.Message}", Color.Red);
        }
        finally
        {
            _btnTestMongo.Enabled = true;
        }
    }

    private async Task RefreshNodesAsync()
    {
        _btnRefreshNodes.Enabled = false;
        _treeNodes.Nodes.Clear();

        try
        {
            if (_opcUaClient.ConnectionState != OpcUaConnectionState.Connected)
            {
                AppendLog("Non connecté au serveur OPC UA", Color.Orange);
                return;
            }

            // Niveau 1 : enfants de ObjectsFolder
            var nodes = await _opcUaClient.BrowseAsync(null);

            AppendLog($"Browse niveau 1: {nodes.Count} nodes trouvés", Color.Cyan);
            foreach (var n in nodes)
            {
                AppendLog($"  - {n.DisplayName} (ns={n.NamespaceIndex}, {n.NodeId})", Color.Cyan);
            }

            foreach (var node in nodes)
            {
                var treeNode = CreateTreeNode(node);
                _treeNodes.Nodes.Add(treeNode);

                // Charger automatiquement le niveau 2 (pour voir DataBlocksInstance, etc.)
                if (node.HasChildren)
                {
                    treeNode.Nodes.Clear(); // Supprimer le dummy "Chargement..."
                    try
                    {
                        var children = await _opcUaClient.BrowseAsync(node.NodeId);
                        foreach (var child in children)
                        {
                            var childTreeNode = CreateTreeNode(child, node.BrowsePath);
                            treeNode.Nodes.Add(childTreeNode);
                        }
                    }
                    catch
                    {
                        // Remettre le dummy en cas d'erreur
                        treeNode.Nodes.Add(new TreeNode("Chargement..."));
                    }
                }
            }

            // Étendre automatiquement les nodes de niveau 1
            foreach (TreeNode treeNode in _treeNodes.Nodes)
            {
                if (treeNode.Nodes.Count > 0)
                {
                    treeNode.Expand();
                }
            }

            AppendLog($"{nodes.Count} nœuds racines chargés (avec enfants)", Color.LightGray);
        }
        catch (Exception ex)
        {
            AppendLog($"Échec du parcours des nœuds : {ex.Message}", Color.Red);
        }
        finally
        {
            _btnRefreshNodes.Enabled = true;
        }
    }

    private TreeNode CreateTreeNode(OpcUaNode node, string parentPath = "")
    {
        // Build the full browse path
        node.BrowsePath = string.IsNullOrEmpty(parentPath)
            ? node.DisplayName
            : $"{parentPath}/{node.DisplayName}";

        var treeNode = new TreeNode(node.DisplayName)
        {
            Tag = node,
            ToolTipText = $"{node.BrowsePath}\n({node.NodeId})"
        };

        // Add dummy child for lazy loading
        if (node.HasChildren)
        {
            treeNode.Nodes.Add(new TreeNode("Chargement..."));
        }

        // Style based on node type
        if (node.IsVariable)
        {
            treeNode.ForeColor = Color.Blue;
        }

        return treeNode;
    }

    private async Task OnNodeExpandingAsync(TreeViewCancelEventArgs e)
    {
        if (e.Node == null) return;

        // Check if already loaded
        if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Text == "Chargement...")
        {
            e.Node.Nodes.Clear();

            try
            {
                var parentNode = e.Node.Tag as OpcUaNode;
                if (parentNode == null)
                {
                    AppendLog($"Erreur: Tag null pour {e.Node.Text}", Color.Red);
                    return;
                }

                AppendLog($"Browse de '{parentNode.DisplayName}' ({parentNode.NodeId})...", Color.Gray);
                var children = await _opcUaClient.BrowseAsync(parentNode.NodeId);
                AppendLog($"  -> {children.Count} enfants trouvés", Color.Gray);

                if (children.Count == 0)
                {
                    e.Node.Nodes.Add(new TreeNode("(vide)") { ForeColor = Color.Gray });
                }
                else
                {
                    foreach (var child in children)
                    {
                        // Pass parent's browse path for hierarchical context
                        var treeNode = CreateTreeNode(child, parentNode.BrowsePath);
                        // Hériter l'état coché du parent lors du chargement
                        treeNode.Checked = e.Node.Checked;
                        e.Node.Nodes.Add(treeNode);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Erreur browse: {ex.Message}", Color.Red);
                e.Node.Nodes.Add(new TreeNode($"Erreur : {ex.Message}") { ForeColor = Color.Red });
            }
        }
    }

    private void OnNodeAfterCheck(object? sender, TreeViewEventArgs e)
    {
        // Avoid recursive calls
        if (e.Action == TreeViewAction.Unknown) return;
        if (e.Node == null) return;

        // Check/uncheck all children
        SetChildNodesChecked(e.Node, e.Node.Checked);
    }

    private void SetChildNodesChecked(TreeNode node, bool isChecked)
    {
        foreach (TreeNode child in node.Nodes)
        {
            child.Checked = isChecked;
            SetChildNodesChecked(child, isChecked);
        }
    }

    private async Task AddSelectedSubscriptionsAsync()
    {
        _btnAddSubscription.Enabled = false;
        _btnAddSubscription.Text = "Chargement...";

        try
        {
            // D'abord, charger tous les enfants des nœuds cochés récursivement
            await LoadCheckedNodesChildrenAsync(_treeNodes.Nodes);

            // Maintenant, récupérer tous les nœuds cochés (y compris les enfants chargés)
            var selectedNodes = GetCheckedNodes(_treeNodes.Nodes).ToList();
            var subscribableNodes = selectedNodes.Where(n => n.IsSubscribable).ToList();

            if (subscribableNodes.Count == 0)
            {
                AppendLog("Aucun nœud abonnable sélectionné", Color.Orange);
                return;
            }

            foreach (var node in subscribableNodes)
            {
                var subscription = new SubscriptionDefinition
                {
                    NodeId = node.NodeId,
                    DisplayName = node.DisplayName,
                    BrowsePath = node.BrowsePath,
                    SamplingIntervalMs = (int)_numSamplingInterval.Value,
                    PublishingIntervalMs = (int)_numPublishingInterval.Value,
                    Enabled = true
                };

                await _configService.AddSubscriptionAsync(subscription);
            }

            AppendLog($"{subscribableNodes.Count} abonnement(s) ajouté(s)", Color.Green);
            RefreshSubscriptionsGrid();
        }
        catch (Exception ex)
        {
            AppendLog($"Erreur lors de l'ajout des abonnements : {ex.Message}", Color.Red);
        }
        finally
        {
            _btnAddSubscription.Enabled = true;
            _btnAddSubscription.Text = "Ajouter";
        }
    }

    /// <summary>
    /// Charge récursivement tous les enfants des nœuds cochés.
    /// </summary>
    private async Task LoadCheckedNodesChildrenAsync(TreeNodeCollection nodes)
    {
        foreach (TreeNode treeNode in nodes)
        {
            if (treeNode.Checked)
            {
                // Si ce nœud a des enfants non chargés (dummy "Chargement...")
                if (treeNode.Nodes.Count == 1 && treeNode.Nodes[0].Text == "Chargement...")
                {
                    await LoadNodeChildrenAsync(treeNode);
                }

                // Charger récursivement les enfants de ce nœud
                await LoadCheckedNodesChildrenAsync(treeNode.Nodes);
            }
        }
    }

    /// <summary>
    /// Charge les enfants d'un nœud spécifique.
    /// </summary>
    private async Task LoadNodeChildrenAsync(TreeNode treeNode)
    {
        if (treeNode.Tag is not OpcUaNode parentNode) return;

        treeNode.Nodes.Clear();

        try
        {
            var children = await _opcUaClient.BrowseAsync(parentNode.NodeId);

            foreach (var child in children)
            {
                // Pass parent's browse path for hierarchical context
                var childTreeNode = CreateTreeNode(child, parentNode.BrowsePath);
                // Hériter l'état coché du parent
                childTreeNode.Checked = treeNode.Checked;
                treeNode.Nodes.Add(childTreeNode);
            }
        }
        catch (Exception ex)
        {
            treeNode.Nodes.Add(new TreeNode($"Erreur : {ex.Message}") { ForeColor = Color.Red });
        }
    }

    private IEnumerable<OpcUaNode> GetCheckedNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode treeNode in nodes)
        {
            if (treeNode.Checked && treeNode.Tag is OpcUaNode node)
            {
                yield return node;
            }

            foreach (var child in GetCheckedNodes(treeNode.Nodes))
            {
                yield return child;
            }
        }
    }

    private async Task RemoveSelectedSubscriptionsAsync()
    {
        var selectedRows = _gridSubscriptions.SelectedRows;
        foreach (DataGridViewRow row in selectedRows)
        {
            if (row.DataBoundItem is SubscriptionDefinition sub)
            {
                await _configService.RemoveSubscriptionAsync(sub.NodeId);
            }
        }

        RefreshSubscriptionsGrid();
    }

    private void RefreshSubscriptionsGrid()
    {
        _gridSubscriptions.DataSource = null;
        _gridSubscriptions.DataSource = _configService.Current.Subscriptions;
    }

    private async Task OnForceJsonOnlyChangedAsync()
    {
        await _configService.UpdateAsync(c => c.ForceJsonOnly = _chkForceJsonOnly.Checked);

        // Appliquer le changement immédiatement au service de persistance
        if (_chkForceJsonOnly.Checked)
        {
            _persistenceService.ForceMode(PersistenceMode.JsonFallback);
            AppendLog("Mode forcé : JSON uniquement", Color.Orange);
        }
        else if (!_chkDryRunMode.Checked)
        {
            _persistenceService.ForceMode(PersistenceMode.MongoDB);
            AppendLog("Mode forcé : MongoDB", Color.Green);
        }
    }

    private async Task OnDryRunModeChangedAsync()
    {
        await _configService.UpdateAsync(c => c.DryRunMode = _chkDryRunMode.Checked);

        // Appliquer le changement immédiatement au service de persistance
        if (_chkDryRunMode.Checked)
        {
            _persistenceService.ForceMode(PersistenceMode.DryRun);
            AppendLog("Mode forcé : Test (sans persistance)", Color.Gray);
        }
        else if (_chkForceJsonOnly.Checked)
        {
            _persistenceService.ForceMode(PersistenceMode.JsonFallback);
            AppendLog("Mode forcé : JSON uniquement", Color.Orange);
        }
        else
        {
            _persistenceService.ForceMode(PersistenceMode.MongoDB);
            AppendLog("Mode forcé : MongoDB", Color.Green);
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
            AppendLog($"Journaux exportés vers {dialog.FileName}", Color.Green);
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

    // Maximum number of lines in the log display (prevents memory leak)
    private const int MaxLogLines = 1000;

    private void AppendLog(string message, Color color)
    {
        if (_txtLogs.InvokeRequired)
        {
            _txtLogs.BeginInvoke(() => AppendLog(message, color));
            return;
        }

        // Trim old lines if we exceed the limit
        if (_txtLogs.Lines.Length > MaxLogLines)
        {
            var linesToRemove = _txtLogs.Lines.Length - MaxLogLines + 100; // Remove 100 extra to avoid frequent trimming
            var firstLineLength = 0;
            for (int i = 0; i < linesToRemove && i < _txtLogs.Lines.Length; i++)
            {
                firstLineLength += _txtLogs.Lines[i].Length + 1; // +1 for newline
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
}
