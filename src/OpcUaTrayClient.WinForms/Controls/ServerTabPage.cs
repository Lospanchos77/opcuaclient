using OpcUaTrayClient.Core.Configuration;
using OpcUaTrayClient.Core.Models;
using OpcUaTrayClient.OpcUa;

namespace OpcUaTrayClient.WinForms.Controls;

/// <summary>
/// V2.0.0: User control for managing a single OPC UA server's subscriptions.
/// Contains node browser, subscription grid, and server-specific controls.
/// </summary>
public sealed class ServerTabPage : UserControl
{
    private readonly OpcUaClientManager _clientManager;
    private readonly ConfigurationService _configService;
    private readonly string _serverId;

    // Controls
    private TreeView _treeNodes = null!;
    private Button _btnRefreshNodes = null!;
    private DataGridView _gridSubscriptions = null!;
    private NumericUpDown _numSamplingInterval = null!;
    private NumericUpDown _numPublishingInterval = null!;
    private Button _btnAddSubscription = null!;
    private Button _btnRemoveSubscription = null!;
    private Label _lblServerStatus = null!;

    // Delegate for logging to parent form
    public Action<string, Color>? LogMessage;

    public ServerTabPage(
        OpcUaClientManager clientManager,
        ConfigurationService configService,
        string serverId)
    {
        _clientManager = clientManager;
        _configService = configService;
        _serverId = serverId;

        InitializeComponent();
        RefreshSubscriptionsGrid();
        UpdateServerStatus();
    }

    private void InitializeComponent()
    {
        var splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
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
            Text = "Parcourir les noeuds OPC UA",
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

        _lblServerStatus = new Label
        {
            AutoSize = true,
            Margin = new Padding(20, 6, 0, 0),
            ForeColor = Color.Gray
        };
        headerPanel.Controls.Add(_lblServerStatus);

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

        settingsPanel.Controls.Add(new Label { Text = "Echant. (ms):", AutoSize = true, Margin = new Padding(0, 8, 3, 0) });
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

        _gridSubscriptions.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "NodeId", HeaderText = "ID noeud", Width = 150 });
        _gridSubscriptions.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "BrowsePath", HeaderText = "Chemin", Width = 250 });
        _gridSubscriptions.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "SamplingIntervalMs", HeaderText = "Echant.", Width = 60 });
        _gridSubscriptions.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Enabled", HeaderText = "Actif", Width = 50 });

        bottomPanel.Controls.Add(_gridSubscriptions);

        // Important: Add grid first so it fills remaining space
        bottomPanel.Controls.SetChildIndex(_gridSubscriptions, 0);
        bottomPanel.Controls.SetChildIndex(settingsPanel, 1);

        splitContainer.Panel2.Controls.Add(bottomPanel);

        Controls.Add(splitContainer);
    }

    /// <summary>
    /// Updates the server status label.
    /// </summary>
    public void UpdateServerStatus()
    {
        if (InvokeRequired)
        {
            BeginInvoke(UpdateServerStatus);
            return;
        }

        var state = _clientManager.GetServerState(_serverId);
        _lblServerStatus.Text = $"Etat: {state}";
        _lblServerStatus.ForeColor = state switch
        {
            OpcUaConnectionState.Connected => Color.Green,
            OpcUaConnectionState.Connecting or OpcUaConnectionState.Reconnecting => Color.Orange,
            _ => Color.Red
        };
    }

    private async Task RefreshNodesAsync()
    {
        _btnRefreshNodes.Enabled = false;
        _treeNodes.Nodes.Clear();

        try
        {
            var client = _clientManager.GetClient(_serverId);
            if (client == null || client.ConnectionState != OpcUaConnectionState.Connected)
            {
                Log("Non connecte au serveur OPC UA", Color.Orange);
                return;
            }

            // Niveau 1 : enfants de ObjectsFolder
            var nodes = await client.BrowseAsync(null);

            Log($"Browse niveau 1: {nodes.Count} nodes trouves", Color.Cyan);
            foreach (var n in nodes)
            {
                Log($"  - {n.DisplayName} (ns={n.NamespaceIndex}, {n.NodeId})", Color.Cyan);
            }

            foreach (var node in nodes)
            {
                var treeNode = CreateTreeNode(node);
                _treeNodes.Nodes.Add(treeNode);

                // Charger automatiquement le niveau 2
                if (node.HasChildren)
                {
                    treeNode.Nodes.Clear();
                    try
                    {
                        var children = await client.BrowseAsync(node.NodeId);
                        foreach (var child in children)
                        {
                            var childTreeNode = CreateTreeNode(child, node.BrowsePath);
                            treeNode.Nodes.Add(childTreeNode);
                        }
                    }
                    catch
                    {
                        treeNode.Nodes.Add(new TreeNode("Chargement..."));
                    }
                }
            }

            // Etendre automatiquement les nodes de niveau 1
            foreach (TreeNode treeNode in _treeNodes.Nodes)
            {
                if (treeNode.Nodes.Count > 0)
                {
                    treeNode.Expand();
                }
            }

            Log($"{nodes.Count} noeuds racines charges (avec enfants)", Color.LightGray);
        }
        catch (Exception ex)
        {
            Log($"Echec du parcours des noeuds : {ex.Message}", Color.Red);
        }
        finally
        {
            _btnRefreshNodes.Enabled = true;
        }
    }

    private TreeNode CreateTreeNode(OpcUaNode node, string parentPath = "")
    {
        node.BrowsePath = string.IsNullOrEmpty(parentPath)
            ? node.DisplayName
            : $"{parentPath}/{node.DisplayName}";

        var treeNode = new TreeNode(node.DisplayName)
        {
            Tag = node,
            ToolTipText = $"{node.BrowsePath}\n({node.NodeId})"
        };

        if (node.HasChildren)
        {
            treeNode.Nodes.Add(new TreeNode("Chargement..."));
        }

        if (node.IsVariable)
        {
            treeNode.ForeColor = Color.Blue;
        }

        return treeNode;
    }

    private async Task OnNodeExpandingAsync(TreeViewCancelEventArgs e)
    {
        if (e.Node == null) return;

        if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Text == "Chargement...")
        {
            e.Node.Nodes.Clear();

            try
            {
                var parentNode = e.Node.Tag as OpcUaNode;
                if (parentNode == null)
                {
                    Log($"Erreur: Tag null pour {e.Node.Text}", Color.Red);
                    return;
                }

                var client = _clientManager.GetClient(_serverId);
                if (client == null)
                {
                    Log("Client non disponible", Color.Red);
                    return;
                }

                Log($"Browse de '{parentNode.DisplayName}' ({parentNode.NodeId})...", Color.Gray);
                var children = await client.BrowseAsync(parentNode.NodeId);
                Log($"  -> {children.Count} enfants trouves", Color.Gray);

                if (children.Count == 0)
                {
                    e.Node.Nodes.Add(new TreeNode("(vide)") { ForeColor = Color.Gray });
                }
                else
                {
                    foreach (var child in children)
                    {
                        var treeNode = CreateTreeNode(child, parentNode.BrowsePath);
                        treeNode.Checked = e.Node.Checked;
                        e.Node.Nodes.Add(treeNode);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Erreur browse: {ex.Message}", Color.Red);
                e.Node.Nodes.Add(new TreeNode($"Erreur : {ex.Message}") { ForeColor = Color.Red });
            }
        }
    }

    private void OnNodeAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (e.Action == TreeViewAction.Unknown) return;
        if (e.Node == null) return;

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
            var client = _clientManager.GetClient(_serverId);
            if (client == null)
            {
                Log("Client non disponible", Color.Red);
                return;
            }

            // D'abord, charger tous les enfants des noeuds coches recursivement
            await LoadCheckedNodesChildrenAsync(_treeNodes.Nodes, client);

            // Maintenant, recuperer tous les noeuds coches
            var selectedNodes = GetCheckedNodes(_treeNodes.Nodes).ToList();
            var subscribableNodes = selectedNodes.Where(n => n.IsSubscribable).ToList();

            if (subscribableNodes.Count == 0)
            {
                Log("Aucun noeud abonnable selectionne", Color.Orange);
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

                await _configService.AddSubscriptionAsync(_serverId, subscription);
            }

            Log($"{subscribableNodes.Count} abonnement(s) ajoute(s)", Color.Green);
            RefreshSubscriptionsGrid();
        }
        catch (Exception ex)
        {
            Log($"Erreur lors de l'ajout des abonnements : {ex.Message}", Color.Red);
        }
        finally
        {
            _btnAddSubscription.Enabled = true;
            _btnAddSubscription.Text = "Ajouter";
        }
    }

    private async Task LoadCheckedNodesChildrenAsync(TreeNodeCollection nodes, OpcUaClientService client)
    {
        foreach (TreeNode treeNode in nodes)
        {
            if (treeNode.Checked)
            {
                if (treeNode.Nodes.Count == 1 && treeNode.Nodes[0].Text == "Chargement...")
                {
                    await LoadNodeChildrenAsync(treeNode, client);
                }

                await LoadCheckedNodesChildrenAsync(treeNode.Nodes, client);
            }
        }
    }

    private async Task LoadNodeChildrenAsync(TreeNode treeNode, OpcUaClientService client)
    {
        if (treeNode.Tag is not OpcUaNode parentNode) return;

        treeNode.Nodes.Clear();

        try
        {
            var children = await client.BrowseAsync(parentNode.NodeId);

            foreach (var child in children)
            {
                var childTreeNode = CreateTreeNode(child, parentNode.BrowsePath);
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
                await _configService.RemoveSubscriptionAsync(_serverId, sub.NodeId);
            }
        }

        RefreshSubscriptionsGrid();
    }

    private void RefreshSubscriptionsGrid()
    {
        _gridSubscriptions.DataSource = null;

        var server = _configService.Current.Servers.FirstOrDefault(s => s.Id == _serverId);
        if (server != null)
        {
            _gridSubscriptions.DataSource = server.Subscriptions;
        }
    }

    private void Log(string message, Color color)
    {
        LogMessage?.Invoke(message, color);
    }
}
