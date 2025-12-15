// Dashboard specific functionality

let refreshInterval = null;

// Initialize dashboard
document.addEventListener('DOMContentLoaded', () => {
  if (!initPage()) return;

  // Load data immediately
  refreshData();

  // Refresh every 5 seconds
  refreshInterval = setInterval(refreshData, 5000);
});

// Refresh all data
async function refreshData() {
  try {
    await Promise.all([
      loadLatestValues(),
      loadStorageInfo()
    ]);

    document.getElementById('lastUpdate').textContent = new Date().toLocaleTimeString('fr-FR');
    document.getElementById('connectionStatus').innerHTML = '<i class="fas fa-circle text-success"></i> Connecté';
  } catch (error) {
    console.error('Error refreshing data:', error);
    document.getElementById('connectionStatus').innerHTML = '<i class="fas fa-circle text-danger"></i> Erreur';
  }
}

// Load latest values
async function loadLatestValues() {
  const response = await api('/data/latest');
  if (!response) return;

  const data = await response.json();
  const container = document.getElementById('valuesContainer');

  // Update counters
  document.getElementById('nodeCount').textContent = data.length;
  const goodCount = data.filter(d => d.quality && d.quality.toLowerCase().includes('good')).length;
  document.getElementById('goodQuality').textContent = goodCount;

  if (data.length === 0) {
    container.innerHTML = `
      <div class="col-12 text-center py-5">
        <i class="fas fa-inbox fa-3x text-muted"></i>
        <p class="mt-3 text-muted">Aucune donnée disponible</p>
        <p class="text-muted small">Vérifiez que le client OPC UA est en cours d'exécution</p>
      </div>
    `;
    return;
  }

  // Build value cards
  container.innerHTML = data.map(item => `
    <div class="col-xl-3 col-lg-4 col-md-6 col-sm-6 mb-3">
      <div class="card value-card h-100">
        <div class="card-body">
          <div class="d-flex justify-content-between align-items-start mb-2">
            <span class="badge badge-${getQualityBadge(item.quality)}">${item.quality || 'Unknown'}</span>
            <a href="charts.html?nodeId=${encodeURIComponent(item.nodeId)}" class="text-muted" title="Voir historique">
              <i class="fas fa-chart-line"></i>
            </a>
          </div>
          <div class="value-display ${getQualityClass(item.quality)} mb-2">
            ${formatValue(item.lastValue)}
          </div>
          <div class="node-path" title="${item.browsePath || item.displayName}">
            ${truncatePath(item.browsePath || item.displayName, 40)}
          </div>
          <div class="timestamp">
            ${formatRelativeTime(item.lastTimestamp)}
          </div>
        </div>
      </div>
    </div>
  `).join('');
}

// Load storage info
async function loadStorageInfo() {
  const response = await api('/data/storage');
  if (!response) return;

  const info = await response.json();
  document.getElementById('storageMB').textContent = `${info.totalSizeMB} MB`;

  // Get stats for document count
  const statsResponse = await api('/data/stats');
  if (statsResponse) {
    const stats = await statsResponse.json();
    const totalPoints = stats.reduce((sum, s) => sum + s.pointCount, 0);
    document.getElementById('docCount').textContent = totalPoints.toLocaleString();
  }
}

// Helper functions
function getQualityBadge(quality) {
  if (!quality) return 'secondary';
  const q = quality.toLowerCase();
  if (q.includes('good')) return 'success';
  if (q.includes('bad')) return 'danger';
  return 'warning';
}

function truncatePath(path, maxLength) {
  if (!path || path.length <= maxLength) return path;
  return '...' + path.slice(-maxLength + 3);
}
