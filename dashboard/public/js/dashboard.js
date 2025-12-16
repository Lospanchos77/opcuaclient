// Dashboard specific functionality

let refreshInterval = null;
let miniCharts = {};  // Store mini chart instances

// Initialize dashboard
document.addEventListener('DOMContentLoaded', () => {
  if (!initPage()) return;

  // Load data immediately
  refreshData();

  // Refresh every 2 seconds for smoother real-time
  refreshInterval = setInterval(refreshData, 2000);
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

  // Check if cards already exist (to avoid recreating charts)
  const existingCards = container.querySelectorAll('.value-card').length;

  if (existingCards !== data.length) {
    // Build value cards with mini charts
    container.innerHTML = data.map((item, index) => `
      <div class="col-xl-3 col-lg-4 col-md-6 col-sm-6 mb-3">
        <div class="card value-card h-100">
          <div class="card-body">
            <div class="d-flex justify-content-between align-items-start">
              <div class="node-path" title="${item.browsePath || item.displayName}">
                ${getNodeName(item.browsePath || item.displayName)}
              </div>
              <a href="charts.html?nodeId=${encodeURIComponent(item.nodeId)}" class="text-muted" title="Voir historique">
                <i class="fas fa-expand-alt"></i>
              </a>
            </div>
            <div class="value-display ${getValueColorClass(item.lastValue)}" id="value-${index}">
              ${formatValue(item.lastValue)}
            </div>
            <div class="timestamp" id="timestamp-${index}">
              ${formatRelativeTime(item.lastTimestamp)}
            </div>
            <div class="mini-chart">
              <canvas id="minichart-${index}"></canvas>
            </div>
          </div>
        </div>
      </div>
    `).join('');

    // Initialize mini charts
    data.forEach((item, index) => {
      initMiniChart(index, item.nodeId);
    });
  } else {
    // Just update values without recreating cards
    data.forEach((item, index) => {
      const valueEl = document.getElementById(`value-${index}`);
      const timestampEl = document.getElementById(`timestamp-${index}`);
      if (valueEl) {
        valueEl.textContent = formatValue(item.lastValue);
        valueEl.className = `value-display ${getValueColorClass(item.lastValue)}`;
      }
      if (timestampEl) {
        timestampEl.textContent = formatRelativeTime(item.lastTimestamp);
      }
    });
  }

  // Update mini charts data
  await updateMiniCharts(data);
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

// Get color class based on value (positive = green, negative = red)
function getValueColorClass(value) {
  const numValue = typeof value === 'number' ? value : parseFloat(value);
  if (isNaN(numValue)) return '';
  if (numValue > 0) return 'text-success';
  if (numValue < 0) return 'text-danger';
  return '';  // zero = neutral
}

function truncatePath(path, maxLength) {
  if (!path || path.length <= maxLength) return path;
  return '...' + path.slice(-maxLength + 3);
}

// Get only the last part of the node path (variable name) in uppercase
function getNodeName(path) {
  if (!path) return 'UNKNOWN';
  const parts = path.split('/');
  const name = parts[parts.length - 1] || path;
  return name.toUpperCase();
}

// Initialize a mini chart
function initMiniChart(index, nodeId) {
  const canvas = document.getElementById(`minichart-${index}`);
  if (!canvas) return;

  const ctx = canvas.getContext('2d');

  miniCharts[index] = {
    nodeId: nodeId,
    chart: new Chart(ctx, {
      type: 'line',
      data: {
        datasets: [{
          data: [],
          borderColor: 'rgb(54, 162, 235)',
          backgroundColor: 'rgba(54, 162, 235, 0.1)',
          borderWidth: 1.5,
          pointRadius: 0,
          tension: 0.3,
          fill: true
        }]
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        plugins: {
          legend: { display: false },
          tooltip: {
            enabled: true,
            mode: 'index',
            intersect: false,
            callbacks: {
              title: (ctx) => moment(ctx[0].parsed.x).format('HH:mm:ss'),
              label: (ctx) => ctx.parsed.y.toFixed(2)
            }
          }
        },
        scales: {
          x: {
            type: 'time',
            display: true,
            grid: { display: false },
            ticks: {
              font: { size: 9 },
              maxTicksLimit: 4,
              callback: function(value) {
                return moment(value).format('HH:mm:ss');
              }
            }
          },
          y: {
            display: true,
            position: 'right',
            grid: { display: false },
            ticks: {
              font: { size: 9 },
              maxTicksLimit: 3,
              callback: function(value) {
                return value.toFixed(1);
              }
            }
          }
        },
        interaction: {
          mode: 'nearest',
          axis: 'x',
          intersect: false
        }
      }
    })
  };
}

// Update mini charts with recent data
async function updateMiniCharts(latestData) {
  const now = new Date();
  const start = new Date(now.getTime() - 60000);  // Last 1 minute

  for (let index = 0; index < latestData.length; index++) {
    const item = latestData[index];
    const chartInfo = miniCharts[index];

    if (!chartInfo || chartInfo.nodeId !== item.nodeId) continue;

    try {
      const response = await api(`/data/history?nodeId=${encodeURIComponent(item.nodeId)}&start=${start.toISOString()}&end=${now.toISOString()}&limit=100`);
      if (!response) continue;

      const history = await response.json();

      if (history.length > 0) {
        const points = history.map(d => ({
          x: new Date(d.timestampUtc),
          y: typeof d.value === 'number' ? d.value : parseFloat(d.value) || 0
        }));

        chartInfo.chart.data.datasets[0].data = points;
        chartInfo.chart.update('none');
      }
    } catch (error) {
      console.error(`Error loading mini chart for ${item.nodeId}:`, error);
    }
  }
}
