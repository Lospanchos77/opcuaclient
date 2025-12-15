const express = require('express');
const mongoose = require('mongoose');
const cors = require('cors');
const helmet = require('helmet');
const path = require('path');
const config = require('./config/default.json');
const User = require('./models/User');

const app = express();

// Middleware
app.use(helmet({
  contentSecurityPolicy: false  // Allow inline scripts for AdminLTE
}));
app.use(cors());
app.use(express.json());
app.use(express.static(path.join(__dirname, 'public')));

// Routes
app.use('/api/auth', require('./routes/auth'));
app.use('/api/data', require('./routes/api'));
app.use('/api/users', require('./routes/users'));

// Serve index.html for root
app.get('/', (req, res) => {
  res.sendFile(path.join(__dirname, 'public', 'index.html'));
});

// Connect to MongoDB and start server
async function start() {
  try {
    // Connect to MongoDB
    await mongoose.connect(`${config.mongodb.uri}/${config.mongodb.database}`);
    console.log(`Connected to MongoDB: ${config.mongodb.database}`);

    // Create admin user if not exists
    const adminExists = await User.findOne({ username: config.admin.username });
    if (!adminExists) {
      const admin = new User({
        username: config.admin.username,
        email: config.admin.email,
        password: config.admin.password,
        role: 'admin'
      });
      await admin.save();
      console.log(`Admin user created: ${config.admin.username}`);
    }

    // Start server
    app.listen(config.port, () => {
      console.log(`
========================================
  OPC UA Dashboard
========================================
  Server:    http://localhost:${config.port}
  MongoDB:   ${config.mongodb.uri}
  Database:  ${config.mongodb.database}
----------------------------------------
  Login:     ${config.admin.username} / ${config.admin.password}
========================================
      `);
    });
  } catch (error) {
    console.error('Failed to start server:', error);
    process.exit(1);
  }
}

start();
