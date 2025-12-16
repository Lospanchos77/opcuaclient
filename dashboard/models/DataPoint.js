const mongoose = require('mongoose');

// This model connects to the existing datapoints collection
// created by the C# OPC UA client

const dataPointSchema = new mongoose.Schema({
  serverId: String,          // V2.0.0: Server identifier
  serverName: String,        // V2.0.0: Server display name
  timestampUtc: Date,
  nodeId: String,
  displayName: String,
  browsePath: String,
  dataType: String,
  value: mongoose.Schema.Types.Mixed,
  statusCode: Number,
  quality: String,
  sourceTimestamp: Date,
  serverTimestamp: Date
}, {
  collection: 'datapoints',  // Use existing collection
  strict: false              // Allow extra fields
});

// Index for efficient queries
dataPointSchema.index({ nodeId: 1, timestampUtc: -1 });
dataPointSchema.index({ timestampUtc: -1 });
// V2.0.0: Indexes for server-filtered queries
dataPointSchema.index({ serverId: 1, nodeId: 1, timestampUtc: -1 });
dataPointSchema.index({ serverId: 1, timestampUtc: -1 });

const DataPoint = mongoose.model('DataPoint', dataPointSchema);

// Helper functions for data access
const DataPointService = {
  // V2.0.0: Get list of distinct servers
  async getServers() {
    return DataPoint.aggregate([
      {
        $group: {
          _id: '$serverId',
          serverName: { $first: '$serverName' }
        }
      },
      {
        $project: {
          _id: 0,
          serverId: '$_id',
          serverName: { $ifNull: ['$serverName', 'Serveur inconnu'] }
        }
      },
      { $sort: { serverName: 1 } }
    ]);
  },

  // Get latest value for each node (V2.0.0: optional server filter)
  async getLatest(maxAge = 60000, serverId = null) {
    const since = new Date(Date.now() - maxAge);
    const match = { timestampUtc: { $gte: since } };
    if (serverId) match.serverId = serverId;

    return DataPoint.aggregate([
      { $match: match },
      { $sort: { timestampUtc: -1 } },
      {
        $group: {
          _id: '$nodeId',
          lastValue: { $first: '$value' },
          lastTimestamp: { $first: '$timestampUtc' },
          displayName: { $first: '$displayName' },
          browsePath: { $first: '$browsePath' },
          quality: { $first: '$quality' },
          serverId: { $first: '$serverId' },        // V2.0.0
          serverName: { $first: '$serverName' }     // V2.0.0
        }
      },
      {
        $project: {
          _id: 0,
          nodeId: '$_id',
          displayName: 1,
          browsePath: 1,
          lastValue: 1,
          lastTimestamp: 1,
          quality: 1,
          serverId: 1,                              // V2.0.0
          serverName: { $ifNull: ['$serverName', 'Serveur inconnu'] }  // V2.0.0
        }
      },
      { $sort: { serverName: 1, browsePath: 1 } }
    ]);
  },

  // Get history for a specific node (V2.0.0: optional server filter)
  async getHistory(nodeId, start, end, limit = 1000, serverId = null) {
    const query = { nodeId };
    if (serverId) query.serverId = serverId;  // V2.0.0

    if (start || end) {
      query.timestampUtc = {};
      if (start) query.timestampUtc.$gte = new Date(start);
      if (end) query.timestampUtc.$lte = new Date(end);
    }

    return DataPoint.find(query)
      .sort({ timestampUtc: 1 })
      .limit(limit)
      .select('timestampUtc value quality serverId serverName -_id')  // V2.0.0: include server info
      .lean();
  },

  // Get list of distinct nodes (V2.0.0: optional server filter)
  async getNodes(serverId = null) {
    const match = serverId ? { serverId } : {};

    return DataPoint.aggregate([
      { $match: match },  // V2.0.0: filter by server
      {
        $group: {
          _id: '$nodeId',
          displayName: { $first: '$displayName' },
          browsePath: { $first: '$browsePath' },
          serverId: { $first: '$serverId' },        // V2.0.0
          serverName: { $first: '$serverName' }     // V2.0.0
        }
      },
      {
        $project: {
          _id: 0,
          nodeId: '$_id',
          displayName: 1,
          browsePath: 1,
          serverId: 1,                              // V2.0.0
          serverName: { $ifNull: ['$serverName', 'Serveur inconnu'] }  // V2.0.0
        }
      },
      { $sort: { serverName: 1, browsePath: 1 } }
    ]);
  },

  // Get statistics (V2.0.0: optional server filter)
  async getStats(start, end, serverId = null) {
    const match = {};
    if (serverId) match.serverId = serverId;  // V2.0.0

    if (start || end) {
      match.timestampUtc = {};
      if (start) match.timestampUtc.$gte = new Date(start);
      if (end) match.timestampUtc.$lte = new Date(end);
    } else {
      // Default: last 24 hours
      match.timestampUtc = { $gte: new Date(Date.now() - 24 * 60 * 60 * 1000) };
    }

    return DataPoint.aggregate([
      { $match: match },
      {
        $group: {
          _id: '$nodeId',
          displayName: { $first: '$displayName' },
          browsePath: { $first: '$browsePath' },
          serverId: { $first: '$serverId' },        // V2.0.0
          serverName: { $first: '$serverName' },    // V2.0.0
          pointCount: { $sum: 1 },
          avgValue: { $avg: '$value' },
          minValue: { $min: '$value' },
          maxValue: { $max: '$value' },
          lastValue: { $last: '$value' },
          lastTimestamp: { $max: '$timestampUtc' }
        }
      },
      {
        $project: {
          _id: 0,
          nodeId: '$_id',
          displayName: 1,
          browsePath: 1,
          serverId: 1,                              // V2.0.0
          serverName: { $ifNull: ['$serverName', 'Serveur inconnu'] },  // V2.0.0
          pointCount: 1,
          avgValue: { $round: ['$avgValue', 2] },
          minValue: 1,
          maxValue: 1,
          lastValue: 1,
          lastTimestamp: 1
        }
      },
      { $sort: { serverName: 1, browsePath: 1 } }
    ]);
  },

  // Get storage info
  async getStorageInfo() {
    // Use db.command instead of deprecated collection.stats()
    const db = mongoose.connection.db;
    const stats = await db.command({ collStats: 'datapoints' });

    const oldest = await DataPoint.findOne()
      .sort({ timestampUtc: 1 })
      .select('timestampUtc')
      .lean();

    const newest = await DataPoint.findOne()
      .sort({ timestampUtc: -1 })
      .select('timestampUtc')
      .lean();

    return {
      documentCount: stats.count,
      totalSizeMB: Math.round(stats.size / (1024 * 1024) * 100) / 100,
      avgDocumentSize: stats.count > 0 ? Math.round(stats.avgObjSize) : 0,
      oldestDocument: oldest?.timestampUtc || null,
      newestDocument: newest?.timestampUtc || null
    };
  }
};

module.exports = { DataPoint, DataPointService };
