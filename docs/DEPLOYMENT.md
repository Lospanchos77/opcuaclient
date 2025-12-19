# Guide de déploiement sur Ubuntu Server

Ce guide explique comment déployer MongoDB et le Dashboard sur un serveur Ubuntu.

## Prérequis

- Ubuntu Server 22.04 ou 24.04
- Accès root ou sudo
- Connexion internet

## Étape 1 : Préparer le serveur

```bash
# Mettre à jour le système
sudo apt update && sudo apt upgrade -y

# Installer les outils de base
sudo apt install -y curl wget gnupg software-properties-common git
```

## Étape 2 : Installer Docker

MongoDB nécessite Docker sur Ubuntu 24.04 en raison de problèmes de compatibilité avec les versions récentes.

```bash
# Installer Docker
sudo apt install -y docker.io

# Démarrer et activer Docker
sudo systemctl start docker
sudo systemctl enable docker

# Vérifier l'installation
docker --version
```

## Étape 3 : Lancer MongoDB avec Docker

```bash
# Lancer MongoDB 4.4
sudo docker run -d \
  --name mongodb \
  --restart always \
  -p 27017:27017 \
  -v mongodb_data:/data/db \
  mongo:4.4

# Vérifier que MongoDB fonctionne
sudo docker ps
sudo docker exec -it mongodb mongo --eval "db.version()"
```

### Commandes utiles MongoDB

```bash
# Voir les logs
sudo docker logs mongodb

# Arrêter MongoDB
sudo docker stop mongodb

# Démarrer MongoDB
sudo docker start mongodb

# Redémarrer MongoDB
sudo docker restart mongodb

# Accéder au shell MongoDB
sudo docker exec -it mongodb mongo
```

## Étape 4 : Installer Node.js

```bash
# Installer Node.js 20 LTS
curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash -
sudo apt install -y nodejs

# Vérifier l'installation
node --version
npm --version
```

## Étape 5 : Déployer le Dashboard

### Option A : Via Git

```bash
# Créer le répertoire
sudo mkdir -p /opt/opcua-dashboard
cd /opt/opcua-dashboard

# Cloner le dépôt
sudo git clone https://github.com/Lospanchos77/opcuaclient.git .
```

### Option B : Via SCP (depuis Windows)

```powershell
# Depuis PowerShell sur Windows
scp -r D:\CLientOPCUA\dashboard user@IP_SERVEUR:/opt/opcua-dashboard/
```

### Installer les dépendances

```bash
cd /opt/opcua-dashboard/dashboard
npm install --production
```

### Configurer le dashboard

Modifier le fichier `config/default.json` :

```bash
nano /opt/opcua-dashboard/dashboard/config/default.json
```

**Important :** Changer les valeurs suivantes en production :

```json
{
  "port": 3000,
  "mongodb": {
    "uri": "mongodb://localhost:27017",
    "database": "opcua_data"
  },
  "jwt": {
    "secret": "VOTRE_CLE_SECRETE_ALEATOIRE",
    "expiresIn": "24h"
  },
  "admin": {
    "username": "admin",
    "password": "VOTRE_MOT_DE_PASSE_SECURISE",
    "email": "admin@votredomaine.com"
  }
}
```

Pour générer une clé secrète :
```bash
openssl rand -base64 32
```

## Étape 6 : Créer le service systemd

Créer le fichier de service :

```bash
sudo nano /etc/systemd/system/opcua-dashboard.service
```

Contenu :

```ini
[Unit]
Description=OPC UA Dashboard
After=network.target docker.service
Wants=docker.service

[Service]
Type=simple
WorkingDirectory=/opt/opcua-dashboard/dashboard
ExecStart=/usr/bin/node server.js
Restart=on-failure
RestartSec=10
Environment=NODE_ENV=production

[Install]
WantedBy=multi-user.target
```

Activer et démarrer le service :

```bash
sudo systemctl daemon-reload
sudo systemctl enable opcua-dashboard
sudo systemctl start opcua-dashboard
sudo systemctl status opcua-dashboard
```

## Étape 7 : Configurer le pare-feu

```bash
# Autoriser SSH
sudo ufw allow ssh

# Autoriser le dashboard
sudo ufw allow 3000/tcp

# Activer le pare-feu
sudo ufw enable
sudo ufw status
```

## Étape 8 : Configuration Nginx (Optionnel)

Pour accéder au dashboard sur le port 80/443 avec un nom de domaine.

### Installer Nginx

```bash
sudo apt install -y nginx
```

### Configurer le proxy inverse

```bash
sudo nano /etc/nginx/sites-available/opcua-dashboard
```

Contenu :

```nginx
server {
    listen 80;
    server_name votre-domaine.com;

    location / {
        proxy_pass http://localhost:3000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection 'upgrade';
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_cache_bypass $http_upgrade;
    }
}
```

Activer la configuration :

```bash
sudo ln -s /etc/nginx/sites-available/opcua-dashboard /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl restart nginx
sudo ufw allow 'Nginx Full'
```

## Étape 9 : HTTPS avec Let's Encrypt (Optionnel)

```bash
sudo apt install -y certbot python3-certbot-nginx
sudo certbot --nginx -d votre-domaine.com
```

## Commandes de gestion

### Dashboard

```bash
# Statut
sudo systemctl status opcua-dashboard

# Démarrer
sudo systemctl start opcua-dashboard

# Arrêter
sudo systemctl stop opcua-dashboard

# Redémarrer
sudo systemctl restart opcua-dashboard

# Logs en temps réel
sudo journalctl -u opcua-dashboard -f
```

### MongoDB (Docker)

```bash
# Statut
sudo docker ps | grep mongodb

# Logs
sudo docker logs -f mongodb

# Shell MongoDB
sudo docker exec -it mongodb mongo

# Sauvegarde
sudo docker exec mongodb mongodump --out /backup
sudo docker cp mongodb:/backup ./backup-$(date +%Y%m%d)
```

### Vérifications

```bash
# Ports en écoute
sudo ss -tlnp | grep -E "27017|3000"

# Espace disque
df -h

# Mémoire
free -h

# Processus Node
ps aux | grep node
```

## Dépannage

### MongoDB ne démarre pas

```bash
# Vérifier les logs Docker
sudo docker logs mongodb

# Supprimer et recréer le conteneur
sudo docker rm -f mongodb
sudo docker run -d \
  --name mongodb \
  --restart always \
  -p 27017:27017 \
  -v mongodb_data:/data/db \
  mongo:4.4
```

### Dashboard ne démarre pas

```bash
# Vérifier les logs
sudo journalctl -u opcua-dashboard -n 100

# Vérifier que MongoDB est accessible
curl -s localhost:27017 || echo "MongoDB non accessible"

# Tester manuellement
cd /opt/opcua-dashboard/dashboard
node server.js
```

### Connexion refusée

```bash
# Vérifier le pare-feu
sudo ufw status

# Vérifier que le service écoute
sudo ss -tlnp | grep 3000
```

## Sauvegarde automatique

Créer un script de sauvegarde :

```bash
sudo nano /opt/backup-mongodb.sh
```

Contenu :

```bash
#!/bin/bash
BACKUP_DIR="/opt/backups/mongodb"
DATE=$(date +%Y%m%d_%H%M%S)

mkdir -p $BACKUP_DIR
docker exec mongodb mongodump --archive=/backup.gz --gzip
docker cp mongodb:/backup.gz $BACKUP_DIR/backup_$DATE.gz

# Garder seulement les 7 derniers jours
find $BACKUP_DIR -name "*.gz" -mtime +7 -delete
```

Rendre exécutable et planifier :

```bash
sudo chmod +x /opt/backup-mongodb.sh

# Ajouter au cron (tous les jours à 2h)
echo "0 2 * * * /opt/backup-mongodb.sh" | sudo tee -a /var/spool/cron/crontabs/root
```

## Résumé des accès

| Service | Port | URL |
|---------|------|-----|
| MongoDB | 27017 | localhost uniquement |
| Dashboard | 3000 | http://IP_SERVEUR:3000 |
| Nginx (optionnel) | 80/443 | http(s)://votre-domaine.com |

## Checklist de vérification

- [ ] Docker installé et en cours d'exécution
- [ ] MongoDB (Docker) démarré avec `--restart always`
- [ ] Node.js 20 installé
- [ ] Dashboard copié dans `/opt/opcua-dashboard/dashboard`
- [ ] Dépendances npm installées
- [ ] `config/default.json` modifié (JWT secret + mot de passe admin)
- [ ] Service systemd créé et activé
- [ ] Pare-feu configuré
- [ ] Dashboard accessible via navigateur
- [ ] (Optionnel) Nginx configuré
- [ ] (Optionnel) HTTPS activé
- [ ] (Optionnel) Sauvegarde automatique configurée
