const express = require('express');
const { DataPointService } = require('../models/DataPoint');
const { auth } = require('../middleware/auth');

const router = express.Router();

// All routes require authentication
router.use(auth);

// V2.0.0: GET /api/data/servers - Get list of all servers
router.get('/servers', async (req, res) => {
  try {
    const servers = await DataPointService.getServers();
    res.json(servers);
  } catch (error) {
    console.error('Error fetching servers:', error);
    res.status(500).json({ error: 'Erreur lors de la récupération des serveurs' });
  }
});

// GET /api/data/latest - Get latest values for all nodes (V2.0.0: optional serverId filter)
router.get('/latest', async (req, res) => {
  try {
    const maxAge = parseInt(req.query.maxAge) || 60000; // Default 60 seconds
    const serverId = req.query.serverId || null;  // V2.0.0
    const data = await DataPointService.getLatest(maxAge, serverId);
    res.json(data);
  } catch (error) {
    console.error('Error fetching latest:', error);
    res.status(500).json({ error: 'Erreur lors de la récupération des données' });
  }
});

// GET /api/data/history - Get historical data for a node (V2.0.0: optional serverId filter)
router.get('/history', async (req, res) => {
  try {
    const { nodeId, start, end, limit, serverId } = req.query;  // V2.0.0: added serverId

    if (!nodeId) {
      return res.status(400).json({ error: 'nodeId requis' });
    }

    const data = await DataPointService.getHistory(
      nodeId,
      start,
      end,
      parseInt(limit) || 1000,
      serverId || null  // V2.0.0
    );

    res.json(data);
  } catch (error) {
    console.error('Error fetching history:', error);
    res.status(500).json({ error: 'Erreur lors de la récupération de l\'historique' });
  }
});

// GET /api/data/nodes - Get list of all nodes (V2.0.0: optional serverId filter)
router.get('/nodes', async (req, res) => {
  try {
    const serverId = req.query.serverId || null;  // V2.0.0
    const nodes = await DataPointService.getNodes(serverId);
    res.json(nodes);
  } catch (error) {
    console.error('Error fetching nodes:', error);
    res.status(500).json({ error: 'Erreur lors de la récupération des noeuds' });
  }
});

// GET /api/data/stats - Get statistics (V2.0.0: optional serverId filter)
router.get('/stats', async (req, res) => {
  try {
    const { start, end, serverId } = req.query;  // V2.0.0: added serverId
    const stats = await DataPointService.getStats(start, end, serverId || null);
    res.json(stats);
  } catch (error) {
    console.error('Error fetching stats:', error);
    res.status(500).json({ error: 'Erreur lors de la récupération des statistiques' });
  }
});

// GET /api/data/storage - Get storage info
router.get('/storage', async (req, res) => {
  try {
    const info = await DataPointService.getStorageInfo();
    res.json(info);
  } catch (error) {
    console.error('Error fetching storage info:', error);
    res.status(500).json({ error: 'Erreur lors de la récupération des infos stockage' });
  }
});

module.exports = router;
