#!/bin/sh
# Point d'entrée de l'image Docker (voir Dockerfile).
#
# Au premier démarrage, le volume /data est vide : on y copie les réglages par défaut de l'image
# (appsettings.json : chaîne de connexion, listes de blocage initiales). Ensuite ils ne sont plus remplacés,
# comme pour une installation systemd (deploy/install.sh).
set -e

DATA_DIR=/data

if [ ! -w "$DATA_DIR" ]; then
    echo "Erreur : $DATA_DIR n'est pas accessible en écriture pour l'utilisateur $(id -u)." >&2
    echo "  Avec un dossier de l'hôte monté sur $DATA_DIR : sudo chown -R $(id -u) <dossier>" >&2
    exit 1
fi

if [ ! -e "$DATA_DIR/appsettings.json" ]; then
    cp /app/appsettings.json "$DATA_DIR/appsettings.json"
fi

exec /app/OnyxFilter "$@"
