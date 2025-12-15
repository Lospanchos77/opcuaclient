const express = require('express');
const { DataPointService } = require('../models/DataPoint');
const { auth } = require('../middleware/auth');

const router = express.Router();

// All routes require authentication
router.use(auth);

// GET /api/data/latest - Get latest values for all nodes
router.get('/latest', async (req, res) => {
  try {
    const maxAge = parseInt(req.query.maxAge) || 60000; // Default 60 seconds
    const data = await DataPointService.getLatest(maxAge);
    res.json(data);
  } catch (error) {
    console.error('Error fetching latest:', error);
    res.status(500).json({ error: 'Erreur lors de la récupération des données' });
  }
});

// GET /api/data/history - Get historical data for a node
router.get('/history', async (req, res) => {
  try {
    const { nodeId, start, end, limit } = req.query;

    if (!nodeId) {
      return res.status(400).json({ error: 'nodeId requis' });
    }

    const data = await DataPointService.getHistory(
      nodeId,
      start,
      end,
      parseInt(limit) || 1000
    );

    res.json(data);
  } catch (error) {
    console.error('Error fetching history:', error);
    res.status(500).json({ error: 'Erreur lors de la récupération de l\'historique' });
  }
});

// GET /api/data/nodes - Get list of all nodes
router.get('/nodes', async (req, res) => {
  try {
    const nodes = await DataPointService.getNodes();
    res.json(nodes);
  } catch (error) {
    console.error('Error fetching nodes:', error);
    res.status(500).json({ error: 'Erreur lors de la récupération des noeuds' });
  }
});

// GET /api/data/stats - Get statistics
router.get('/stats', async (req, res) => {
  try {
    const { start, end } = req.query;
    const stats = await DataPointService.getStats(start, end);
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
