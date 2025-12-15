const mongoose = require('mongoose');

// This model connects to the existing datapoints collection
// created by the C# OPC UA client

const dataPointSchema = new mongoose.Schema({
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

const DataPoint = mongoose.model('DataPoint', dataPointSchema);

// Helper functions for data access
const DataPointService = {
  // Get latest value for each node
  async getLatest(maxAge = 60000) {
    const since = new Date(Date.now() - maxAge);

    return DataPoint.aggregate([
      { $match: { timestampUtc: { $gte: since } } },
      { $sort: { timestampUtc: -1 } },
      {
        $group: {
          _id: '$nodeId',
          lastValue: { $first: '$value' },
          lastTimestamp: { $first: '$timestampUtc' },
          displayName: { $first: '$displayName' },
          browsePath: { $first: '$browsePath' },
          quality: { $first: '$quality' }
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
          quality: 1
        }
      },
      { $sort: { browsePath: 1 } }
    ]);
  },

  // Get history for a specific node
  async getHistory(nodeId, start, end, limit = 1000) {
    const query = { nodeId };

    if (start || end) {
      query.timestampUtc = {};
      if (start) query.timestampUtc.$gte = new Date(start);
      if (end) query.timestampUtc.$lte = new Date(end);
    }

    return DataPoint.find(query)
      .sort({ timestampUtc: 1 })
      .limit(limit)
      .select('timestampUtc value quality -_id')
      .lean();
  },

  // Get list of distinct nodes
  async getNodes() {
    return DataPoint.aggregate([
      {
        $group: {
          _id: '$nodeId',
          displayName: { $first: '$displayName' },
          browsePath: { $first: '$browsePath' }
        }
      },
      {
        $project: {
          _id: 0,
          nodeId: '$_id',
          displayName: 1,
          browsePath: 1
        }
      },
      { $sort: { browsePath: 1 } }
    ]);
  },

  // Get statistics
  async getStats(start, end) {
    const match = {};

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
          pointCount: 1,
          avgValue: { $round: ['$avgValue', 2] },
          minValue: 1,
          maxValue: 1,
          lastValue: 1,
          lastTimestamp: 1
        }
      },
      { $sort: { browsePath: 1 } }
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
