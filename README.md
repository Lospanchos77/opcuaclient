# OPC UA Tray Client

Client OPC UA avec dashboard web pour la visualisation et l'historisation des données industrielles.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        OPC UA Servers                            │
│              (Machines industrielles, PLCs, etc.)                │
└──────────────────────────┬──────────────────────────────────────┘
                           │ OPC UA Protocol
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│                   OPC UA Tray Client (Windows)                   │
│                    - Collecte des données                        │
│                    - Multi-serveurs (v2.0.0+)                    │
│                    - Failover JSON automatique                   │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│                        MongoDB 4.4+                              │
│                    Base: opcua_data                              │
│                    Collections: datapoints, users                │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│                     Dashboard Web (Node.js)                      │
│                    - Visualisation temps réel                    │
│                    - Historiques et graphiques                   │
│                    - Gestion des utilisateurs                    │
└─────────────────────────────────────────────────────────────────┘
```

## Composants

### 1. Client OPC UA (Windows)

Application Windows Forms (.NET 8.0) qui se connecte aux serveurs OPC UA et collecte les données.

**Fonctionnalités :**
- Connexion multi-serveurs OPC UA
- Collecte automatique des données
- Stockage MongoDB avec batch insert
- Failover automatique vers fichiers JSON
- Circuit breaker pour la résilience
- Health monitoring

**Emplacement :** `src/`

### 2. Dashboard Web (Node.js)

Interface web pour visualiser et analyser les données collectées.

**Fonctionnalités :**
- Authentification JWT
- Visualisation temps réel
- Graphiques historiques
- Gestion des utilisateurs (admin)
- Filtrage par serveur OPC UA

**Emplacement :** `dashboard/`

## Installation rapide

### Prérequis

- Windows 10/11 pour le client OPC UA
- MongoDB 4.4+
- Node.js 20 LTS

### Client Windows

1. Télécharger la dernière release
2. Extraire et lancer `OpcUaTrayClient.exe`
3. Configurer les serveurs OPC UA via l'interface

### Dashboard

```bash
cd dashboard
npm install
npm start
```

Le dashboard est accessible sur `http://localhost:3000`

**Identifiants par défaut :**
- Utilisateur : `admin`
- Mot de passe : `admin123`

## Configuration

### Client OPC UA

Configuration stockée dans : `%AppData%/OpcUaTrayClient/config.json`

| Paramètre | Description | Défaut |
|-----------|-------------|--------|
| mongoConnectionString | URI MongoDB | mongodb://localhost:27017 |
| mongoDatabaseName | Nom de la base | opcua_data |
| mongoBatchSize | Taille des batchs | 100 |
| mongoTtlDays | Rétention des données | 30 jours |

### Dashboard

Configuration dans : `dashboard/config/default.json`

```json
{
  "port": 3000,
  "mongodb": {
    "uri": "mongodb://localhost:27017",
    "database": "opcua_data"
  },
  "jwt": {
    "secret": "CHANGER_EN_PRODUCTION",
    "expiresIn": "24h"
  }
}
```

## Déploiement

Voir [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) pour le guide de déploiement sur Ubuntu Server.

## Structure du projet

```
CLientOPCUA/
├── src/                              # Code source C#
│   ├── OpcUaTrayClient.Core/         # Modèles et configuration
│   ├── OpcUaTrayClient.OpcUa/        # Client OPC UA
│   ├── OpcUaTrayClient.Persistence/  # MongoDB + JSON fallback
│   └── OpcUaTrayClient.WinForms/     # Interface Windows
├── dashboard/                         # Dashboard Node.js
│   ├── config/                        # Configuration
│   ├── models/                        # Schémas Mongoose
│   ├── routes/                        # API REST
│   ├── middleware/                    # Auth JWT
│   └── public/                        # Frontend HTML/JS
├── publish/                           # Builds Windows
└── docs/                              # Documentation
```

## API REST

### Authentification

| Endpoint | Méthode | Description |
|----------|---------|-------------|
| /api/auth/login | POST | Connexion |
| /api/auth/me | GET | Utilisateur courant |

### Données

| Endpoint | Méthode | Description |
|----------|---------|-------------|
| /api/data/servers | GET | Liste des serveurs OPC UA |
| /api/data/latest | GET | Dernières valeurs |
| /api/data/history | GET | Historique d'un node |
| /api/data/nodes | GET | Liste des nodes |
| /api/data/stats | GET | Statistiques |

### Utilisateurs (admin)

| Endpoint | Méthode | Description |
|----------|---------|-------------|
| /api/users | GET | Liste des utilisateurs |
| /api/users | POST | Créer un utilisateur |
| /api/users/:id | PUT | Modifier un utilisateur |
| /api/users/:id | DELETE | Supprimer un utilisateur |

## Licence

Propriétaire - I.F OPC-UA CLIENT

## Version

v2.5.0
