// Charts page functionality

let chart = null;
let dateRangePicker = null;
let autoRefreshInterval = null;
let isAutoRefreshing = false;
let currentChartType = 'line';  // line, area, bar, scatter

// V2.0.0: Server filter state
let selectedServerId = null;
let availableServers = [];

// Colors for multiple series
const chartColors = [
  'rgb(54, 162, 235)',
  'rgb(255, 99, 132)',
  'rgb(75, 192, 192)',
  'rgb(255, 159, 64)',
  'rgb(153, 102, 255)',
  'rgb(255, 205, 86)',
  'rgb(201, 203, 207)',
  'rgb(255, 99, 71)'
];

// Initialize page
document.addEventListener('DOMContentLoaded', async () => {
  if (!initPage()) return;

  // Initialize Select2
  $('#nodeSelect').select2({
    theme: 'bootstrap4',
    allowClear: true
  });

  // Initialize DateRangePicker
  moment.locale('fr');
  dateRangePicker = $('#dateRange').daterangepicker({
    timePicker: true,
    timePicker24Hour: true,
    timePickerSeconds: false,
    startDate: moment().subtract(1, 'hours'),
    endDate: moment(),
    locale: {
      format: 'DD/MM/YYYY HH:mm',
      applyLabel: 'Appliquer',
      cancelLabel: 'Annuler',
      customRangeLabel: 'Personnalise',
      daysOfWeek: ['Di', 'Lu', 'Ma', 'Me', 'Je', 'Ve', 'Sa'],
      monthNames: ['Janvier', 'Fevrier', 'Mars', 'Avril', 'Mai', 'Juin', 'Juillet', 'Aout', 'Septembre', 'Octobre', 'Novembre', 'Decembre']
    },
    ranges: {
      'Derniere heure': [moment().subtract(1, 'hours'), moment()],
      '6 heures': [moment().subtract(6, 'hours'), moment()],
      '24 heures': [moment().subtract(24, 'hours'), moment()],
      '7 jours': [moment().subtract(7, 'days'), moment()],
      '30 jours': [moment().subtract(30, 'days'), moment()]
    }
  });

  // Initialize Chart.js
  const ctx = document.getElementById('mainChart').getContext('2d');
  chart = new Chart(ctx, {
    type: 'line',
    data: {
      datasets: []
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      animation: false,  // Disable all animations for smoother real-time updates
      interaction: {
        mode: 'index',
        intersect: false
      },
      plugins: {
        legend: {
          position: 'top'
        },
        tooltip: {
          callbacks: {
            title: function(context) {
              return moment(context[0].parsed.x).format('DD/MM/YYYY HH:mm:ss');
            }
          }
        }
      },
      scales: {
        x: {
          type: 'time',
          time: {
            displayFormats: {
              second: 'HH:mm:ss',
              minute: 'HH:mm',
              hour: 'DD/MM HH:mm',
              day: 'DD/MM'
            }
          },
          title: {
            display: true,
            text: 'Temps'
          }
        },
        y: {
          title: {
            display: true,
            text: 'Valeur'
          }
        }
      }
    }
  });

  // V2.0.0: Load servers list first
  await loadServers();

  // Check for serverId in URL (V2.0.0)
  const urlParams = new URLSearchParams(window.location.search);
  const serverIdParam = urlParams.get('serverId');
  if (serverIdParam) {
    selectedServerId = serverIdParam;
    document.getElementById('serverFilter').value = serverIdParam;
  }

  // Load nodes list (with server filter if set)
  await loadNodes();

  // Check for nodeId in URL
  const nodeId = urlParams.get('nodeId');
  if (nodeId) {
    $('#nodeSelect').val([nodeId]).trigger('change');
    loadChart();
  }

  // Auto-refresh toggle
  document.getElementById('autoRefresh').addEventListener('change', function() {
    if (this.checked) {
      startAutoRefresh();
    } else {
      stopAutoRefresh();
    }
  });

  // Refresh interval change
  document.getElementById('refreshInterval').addEventListener('change', function() {
    if (isAutoRefreshing) {
      stopAutoRefresh();
      startAutoRefresh();
    }
  });
});

// V2.0.0: Load available servers
async function loadServers() {
  try {
    const response = await api('/data/servers');
    if (!response) return;

    availableServers = await response.json();
    updateServerDropdown();
  } catch (error) {
    console.error('Error loading servers:', error);
  }
}

// V2.0.0: Update server dropdown
function updateServerDropdown() {
  const dropdown = document.getElementById('serverFilter');
  if (!dropdown) return;

  dropdown.innerHTML = '<option value="">Tous les serveurs</option>';
  availableServers.forEach(server => {
    const name = server.serverName || server.serverId || 'Serveur inconnu';
    dropdown.innerHTML += `<option value="${server.serverId || ''}">${name}</option>`;
  });

  // Restore selected value if any
  if (selectedServerId) {
    dropdown.value = selectedServerId;
  }
}

// V2.0.0: Handle server filter change
function onServerFilterChange() {
  const dropdown = document.getElementById('serverFilter');
  selectedServerId = dropdown.value || null;

  // Reload nodes for selected server
  loadNodes().then(() => {
    // Clear current selection and chart
    $('#nodeSelect').val([]).trigger('change');
    chart.data.datasets = [];
    chart.update();
    document.getElementById('noData').style.display = 'block';
    document.getElementById('chartContainer').style.display = 'none';
    document.getElementById('statsRow').style.display = 'none';
  });
}

// Load available nodes (V2.0.0: with server filter)
async function loadNodes() {
  try {
    let url = '/data/nodes';
    if (selectedServerId) {
      url += `?serverId=${encodeURIComponent(selectedServerId)}`;
    }

    const response = await api(url);
    if (!response) return;

    const nodes = await response.json();
    const select = document.getElementById('nodeSelect');

    // V2.0.0: Show server name in label if not filtered
    select.innerHTML = nodes.map(node => {
      let label = node.browsePath || node.displayName || node.nodeId;
      if (!selectedServerId && node.serverName) {
        label = `[${node.serverName}] ${label}`;
      }
      return `<option value="${node.nodeId}" data-server-id="${node.serverId || ''}">${label}</option>`;
    }).join('');

    $('#nodeSelect').trigger('change');
  } catch (error) {
    console.error('Error loading nodes:', error);
    showToast('Erreur lors du chargement des variables', 'danger');
  }
}

// Load chart data (V2.0.0: with server filter)
async function loadChart() {
  const selectedNodes = $('#nodeSelect').val();

  if (!selectedNodes || selectedNodes.length === 0) {
    document.getElementById('noData').style.display = 'block';
    document.getElementById('chartContainer').style.display = 'none';
    document.getElementById('statsRow').style.display = 'none';
    return;
  }

  document.getElementById('noData').style.display = 'none';
  document.getElementById('chartContainer').style.display = 'block';

  const picker = $('#dateRange').data('daterangepicker');
  const start = picker.startDate.toISOString();
  const end = picker.endDate.toISOString();

  // Add 1 minute padding to the right for visual effect
  const endWithPadding = moment(picker.endDate).add(1, 'minutes').toDate();

  // Update chart type
  const chartType = currentChartType === 'area' ? 'line' : (currentChartType === 'scatter' ? 'scatter' : currentChartType);
  chart.config.type = chartType;

  // Set x-axis max to include padding
  chart.options.scales.x.max = endWithPadding;

  // Clear previous data
  chart.data.datasets = [];

  let totalPoints = 0;
  let allValues = [];

  // Load data for each selected node
  for (let i = 0; i < selectedNodes.length; i++) {
    const nodeId = selectedNodes[i];

    try {
      // V2.0.0: Include serverId in API call if filtered
      let url = `/data/history?nodeId=${encodeURIComponent(nodeId)}&start=${start}&end=${end}&limit=5000`;
      if (selectedServerId) {
        url += `&serverId=${encodeURIComponent(selectedServerId)}`;
      }
      const response = await api(url);
      if (!response) continue;

      const data = await response.json();

      if (data.length === 0) continue;

      // Get node name from select option
      const option = document.querySelector(`#nodeSelect option[value="${nodeId}"]`);
      const label = option ? option.textContent : nodeId;

      // Prepare data points
      const points = data.map(d => ({
        x: new Date(d.timestampUtc),
        y: typeof d.value === 'number' ? d.value : parseFloat(d.value) || 0
      }));

      // Extract numeric values for stats
      const numericValues = points.map(p => p.y).filter(v => !isNaN(v));
      allValues = allValues.concat(numericValues);

      // Determine fill and point settings based on chart type
      const isFill = currentChartType === 'area';
      const pointRadius = currentChartType === 'scatter' ? 4 : (points.length > 100 ? 0 : 3);
      const showLine = currentChartType !== 'scatter';
      const barPercentage = currentChartType === 'bar' ? 0.8 : undefined;

      // Add dataset
      chart.data.datasets.push({
        label: truncateLabel(label, 30),
        data: points,
        borderColor: chartColors[i % chartColors.length],
        backgroundColor: currentChartType === 'bar'
          ? chartColors[i % chartColors.length].replace('rgb', 'rgba').replace(')', ', 0.7)')
          : chartColors[i % chartColors.length].replace('rgb', 'rgba').replace(')', ', 0.2)'),
        borderWidth: currentChartType === 'bar' ? 1 : 2,
        pointRadius: pointRadius,
        tension: 0.1,
        fill: isFill,
        showLine: showLine,
        barPercentage: barPercentage
      });

      totalPoints += data.length;
    } catch (error) {
      console.error(`Error loading data for ${nodeId}:`, error);
    }
  }

  // Update chart
  chart.update();

  // Update point count
  document.getElementById('pointCount').textContent = `${totalPoints.toLocaleString()} points`;

  // Update stats
  if (allValues.length > 0) {
    document.getElementById('statsRow').style.display = 'flex';
    document.getElementById('statMin').textContent = Math.min(...allValues).toFixed(2);
    document.getElementById('statMax').textContent = Math.max(...allValues).toFixed(2);
    document.getElementById('statAvg').textContent = (allValues.reduce((a, b) => a + b, 0) / allValues.length).toFixed(2);
    document.getElementById('statCount').textContent = allValues.length.toLocaleString();
  } else {
    document.getElementById('statsRow').style.display = 'none';
  }
}

// Set date preset
function setPreset(preset) {
  let start, end = moment();

  switch (preset) {
    case '30s':
      start = moment().subtract(30, 'seconds');
      break;
    case '1m':
      start = moment().subtract(1, 'minutes');
      break;
    case '5m':
      start = moment().subtract(5, 'minutes');
      break;
    case '10m':
      start = moment().subtract(10, 'minutes');
      break;
    case '1h':
      start = moment().subtract(1, 'hours');
      break;
    case '6h':
      start = moment().subtract(6, 'hours');
      break;
    case '24h':
      start = moment().subtract(24, 'hours');
      break;
    case '7d':
      start = moment().subtract(7, 'days');
      break;
    case '30d':
      start = moment().subtract(30, 'days');
      break;
    default:
      start = moment().subtract(1, 'hours');
  }

  $('#dateRange').data('daterangepicker').setStartDate(start);
  $('#dateRange').data('daterangepicker').setEndDate(end);

  loadChart();
}

// Truncate label for display
function truncateLabel(label, maxLength) {
  if (!label || label.length <= maxLength) return label;
  return '...' + label.slice(-maxLength + 3);
}

// Start auto-refresh
function startAutoRefresh() {
  const selectedNodes = $('#nodeSelect').val();
  if (!selectedNodes || selectedNodes.length === 0) {
    document.getElementById('autoRefresh').checked = false;
    showToast('Sélectionnez une variable avant d\'activer le temps réel', 'warning');
    return;
  }

  isAutoRefreshing = true;
  const interval = parseInt(document.getElementById('refreshInterval').value);

  // Show indicator
  document.getElementById('autoRefreshIndicator').style.display = 'inline';

  // First refresh
  refreshWithSlidingWindow();

  // Set interval
  autoRefreshInterval = setInterval(refreshWithSlidingWindow, interval);

  console.log(`Auto-refresh started with ${interval}ms interval`);
}

// Stop auto-refresh
function stopAutoRefresh() {
  isAutoRefreshing = false;

  if (autoRefreshInterval) {
    clearInterval(autoRefreshInterval);
    autoRefreshInterval = null;
  }

  // Hide indicator
  document.getElementById('autoRefreshIndicator').style.display = 'none';

  console.log('Auto-refresh stopped');
}

// Refresh with sliding window (keeps the same time range but slides to now)
function refreshWithSlidingWindow() {
  const picker = $('#dateRange').data('daterangepicker');

  // Calculate the duration of the current range
  const duration = picker.endDate.diff(picker.startDate);

  // Slide the window to now
  const newEnd = moment();
  const newStart = moment().subtract(duration, 'milliseconds');

  // Update the date picker
  picker.setStartDate(newStart);
  picker.setEndDate(newEnd);

  // Reload chart
  loadChart();
}

// Set chart type
function setChartType(type) {
  currentChartType = type;

  // Update button states
  document.querySelectorAll('[data-chart-type]').forEach(btn => {
    btn.classList.remove('active');
    if (btn.dataset.chartType === type) {
      btn.classList.add('active');
    }
  });

  // Reload chart with new type
  loadChart();
}
