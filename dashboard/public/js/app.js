// Common app functionality

// Check authentication
function checkAuth() {
  const token = localStorage.getItem('token');
  if (!token) {
    window.location.href = '/';
    return false;
  }
  return true;
}

// Get current user
function getCurrentUser() {
  const userStr = localStorage.getItem('user');
  return userStr ? JSON.parse(userStr) : null;
}

// Logout
function logout() {
  localStorage.removeItem('token');
  localStorage.removeItem('user');
  window.location.href = '/';
}

// API call helper
async function api(endpoint, options = {}) {
  const token = localStorage.getItem('token');

  const defaultOptions = {
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${token}`
    }
  };

  const response = await fetch(`/api${endpoint}`, {
    ...defaultOptions,
    ...options,
    headers: {
      ...defaultOptions.headers,
      ...options.headers
    }
  });

  // Handle 401 - redirect to login
  if (response.status === 401) {
    logout();
    return null;
  }

  return response;
}

// Format value for display
function formatValue(value) {
  if (typeof value === 'number') {
    return value.toFixed(2);
  }
  if (typeof value === 'boolean') {
    return value ? 'Vrai' : 'Faux';
  }
  return String(value);
}

// Format date for display
function formatDate(dateStr) {
  if (!dateStr) return '-';
  const date = new Date(dateStr);
  return date.toLocaleString('fr-FR');
}

// Format relative time
function formatRelativeTime(dateStr) {
  if (!dateStr) return '-';
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = now - date;
  const diffSec = Math.floor(diffMs / 1000);

  if (diffSec < 60) return `il y a ${diffSec}s`;
  if (diffSec < 3600) return `il y a ${Math.floor(diffSec / 60)}min`;
  if (diffSec < 86400) return `il y a ${Math.floor(diffSec / 3600)}h`;
  return formatDate(dateStr);
}

// Get quality class
function getQualityClass(quality) {
  if (!quality) return '';
  const q = quality.toLowerCase();
  if (q.includes('good')) return 'quality-good';
  if (q.includes('bad')) return 'quality-bad';
  return 'quality-uncertain';
}

// Show toast notification
function showToast(message, type = 'info') {
  const toast = document.createElement('div');
  toast.className = `alert alert-${type} position-fixed`;
  toast.style.cssText = 'top: 20px; right: 20px; z-index: 9999; min-width: 200px;';
  toast.innerHTML = message;
  document.body.appendChild(toast);

  setTimeout(() => {
    toast.remove();
  }, 3000);
}

// Initialize page (call this on each page)
function initPage() {
  if (!checkAuth()) return false;

  const user = getCurrentUser();
  if (user) {
    const userNameEl = document.getElementById('userName');
    if (userNameEl) userNameEl.textContent = user.username;

    // Show admin menu items
    if (user.role === 'admin') {
      const usersMenu = document.getElementById('usersMenuItem');
      if (usersMenu) usersMenu.style.display = 'block';
    }
  }

  return true;
}
