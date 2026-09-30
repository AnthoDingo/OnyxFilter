#!/usr/bin/env bash
# Installe ou réinstalle OnyxFilter comme service systemd (Linux x64).
#
#   curl -fsSL https://github.com/AnthoDingo/OnyxFilter/releases/latest/download/install.sh | sudo bash
#   sudo bash install.sh [--version 1.4.0] [--port 8080] [--dir /opt/onyxfilter]
#
# Les données et réglages d'une installation existante (base, appsettings*.json, caches) sont conservés.
# Les mises à jour suivantes se font depuis l'interface (Configuration > Mises à jour) ou l'API.
set -euo pipefail

REPOSITORY="${ONYXFILTER_REPOSITORY:-AnthoDingo/OnyxFilter}"
INSTALL_DIR="/opt/onyxfilter"
PORT="8080"
VERSION="latest"
SERVICE_NAME="onyxfilter"
SERVICE_USER="onyxfilter"
UNIT_PATH="/etc/systemd/system/${SERVICE_NAME}.service"

fail() {
    echo "Erreur : $*" >&2
    exit 1
}

usage() {
    cat <<'USAGE'
Installe ou réinstalle OnyxFilter comme service systemd (Linux x64).

  sudo bash install.sh [--version 1.4.0] [--port 8080] [--dir /opt/onyxfilter]

  --version   version à installer (par défaut : la dernière publiée)
  --port      port de l'interface web (par défaut : 8080)
  --dir       dossier d'installation (par défaut : /opt/onyxfilter)
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="${2:?--version attend un numéro, ex. 1.4.0}"; shift 2 ;;
        --port) PORT="${2:?--port attend un numéro de port}"; shift 2 ;;
        --dir) INSTALL_DIR="${2:?--dir attend un dossier}"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) fail "option inconnue : $1" ;;
    esac
done

[[ "$(id -u)" -eq 0 ]] || fail "lancez ce script en root (sudo)."
[[ "$(uname -s)" == "Linux" && "$(uname -m)" == "x86_64" ]] || fail "seul Linux x64 est pris en charge (système : $(uname -s) $(uname -m))."
command -v systemctl >/dev/null || fail "systemd est requis."
for tool in curl tar sha256sum; do
    command -v "$tool" >/dev/null || fail "outil manquant : $tool."
done
[[ "$PORT" =~ ^[0-9]+$ ]] || fail "port invalide : $PORT."

if [[ "$VERSION" == "latest" ]]; then
    TAG="$(curl -fsSL "https://api.github.com/repos/${REPOSITORY}/releases/latest" | grep -m1 '"tag_name"' | sed -E 's/.*"tag_name": *"([^"]+)".*/\1/')"
    [[ -n "$TAG" ]] || fail "aucune publication trouvée sur https://github.com/${REPOSITORY}/releases."
else
    TAG="v${VERSION#v}"
fi

VERSION="${TAG#v}"
ARCHIVE="OnyxFilter-${VERSION}-linux-x64.tar.gz"
BASE_URL="https://github.com/${REPOSITORY}/releases/download/${TAG}"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

echo "Téléchargement d'OnyxFilter ${VERSION}…"
curl -fsSL -o "${WORK_DIR}/${ARCHIVE}" "${BASE_URL}/${ARCHIVE}"
curl -fsSL -o "${WORK_DIR}/checksums.txt" "${BASE_URL}/checksums.txt"

echo "Vérification de l'empreinte SHA-256…"
(
    cd "$WORK_DIR"
    grep -E "^[0-9a-f]{64}  \*?${ARCHIVE//./\\.}$" checksums.txt > archive.sha256 || fail "archive absente de checksums.txt."
    sha256sum --check --status archive.sha256 || fail "empreinte invalide : archive corrompue ou altérée."
)

tar -xzf "${WORK_DIR}/${ARCHIVE}" -C "$WORK_DIR"
[[ -x "${WORK_DIR}/OnyxFilter/OnyxFilter" ]] || fail "archive inattendue (exécutable OnyxFilter absent)."

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    echo "Création du compte système « ${SERVICE_USER} »…"
    useradd --system --home-dir "$INSTALL_DIR" --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

if systemctl is-active --quiet "$SERVICE_NAME"; then
    echo "Arrêt du service en cours…"
    systemctl stop "$SERVICE_NAME"
fi

mkdir -p "$INSTALL_DIR"

# Réglages existants conservés : ils ne sont pas remplacés par ceux de l'archive.
for settings_file in "${WORK_DIR}"/OnyxFilter/appsettings*.json; do
    if [[ -e "${INSTALL_DIR}/$(basename "$settings_file")" ]]; then
        rm -f "$settings_file"
    fi
done

cp -a "${WORK_DIR}/OnyxFilter/." "${INSTALL_DIR}/"
chown -R "${SERVICE_USER}:${SERVICE_USER}" "$INSTALL_DIR"
chmod 0750 "$INSTALL_DIR"

if [[ -e "$UNIT_PATH" ]]; then
    echo "Service existant conservé : ${UNIT_PATH} (le modèle à jour est dans ${INSTALL_DIR}/onyxfilter.service)."
else
    sed -e "s#/opt/onyxfilter#${INSTALL_DIR}#g" \
        -e "s#http://0.0.0.0:8080#http://0.0.0.0:${PORT}#" \
        "${INSTALL_DIR}/onyxfilter.service" > "$UNIT_PATH"
fi

# Le port 53 est souvent déjà pris par le résolveur local de systemd-resolved.
if command -v ss >/dev/null && ss -Hlun 'sport = :53' | grep -q .; then
    echo "Attention : le port 53 est déjà utilisé sur cette machine (souvent par systemd-resolved)."
    echo "  Libérez-le, par exemple : DNSStubListener=no dans /etc/systemd/resolved.conf, puis"
    echo "  sudo systemctl restart systemd-resolved && sudo systemctl restart ${SERVICE_NAME}"
fi

systemctl daemon-reload
systemctl enable --now "$SERVICE_NAME"

echo
echo "OnyxFilter ${VERSION} est installé dans ${INSTALL_DIR} et démarré."
echo "  Interface : http://$(hostname -I 2>/dev/null | awk '{print $1}'):${PORT}"
echo "  Premier démarrage : identifiants du compte « admin » dans le journal :"
echo "    sudo journalctl -u ${SERVICE_NAME} | grep -A3 'Compte administrateur'"
echo "  Administration en ligne de commande : sudo -u ${SERVICE_USER} ${INSTALL_DIR}/OnyxFilter --help"
