# Image Docker d'OnyxFilter (linux/amd64 et linux/arm64).
#
#   docker build -t onyxfilter .
#   docker build --build-arg VERSION=1.4.0 -t onyxfilter:1.4.0 .
#
# Publiée sur ghcr.io/anthodingo/onyxfilter par .github/workflows/release.yml ; exemple de déploiement :
# docker-compose.yml. Les données (base, appsettings*.json, caches) sont dans le volume /data.

# La compilation tourne sur l'architecture de la machine de build, quelle que soit l'image cible :
# dotnet publish produit directement les binaires de l'architecture visée (-a).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /src

# Restauration des paquets dans une couche à part, réutilisée tant que le projet ne change pas.
COPY src/OnyxFilter/OnyxFilter.csproj src/OnyxFilter/
RUN dotnet restore src/OnyxFilter/OnyxFilter.csproj -a "$TARGETARCH"

COPY src/ src/
# Pas de --no-restore : le paquet des scripts Blazor (_framework/blazor.web.js) n'est ajouté qu'en voyant les
# composants .razor, absents lors de la restauration ci-dessus (csproj seul).
RUN dotnet publish src/OnyxFilter/OnyxFilter.csproj \
        --configuration Release \
        -a "$TARGETARCH" \
        --self-contained false \
        -p:Version="$VERSION" \
        --output /app \
    && rm -f /app/appsettings.Development.json /app/OnyxFilter.db

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=0.0.0-dev

LABEL org.opencontainers.image.title="OnyxFilter" \
      org.opencontainers.image.description="Résolveur DNS filtrant (bloqueur de publicités et de traqueurs)" \
      org.opencontainers.image.source="https://github.com/AnthoDingo/OnyxFilter" \
      org.opencontainers.image.licenses="AGPL-3.0" \
      org.opencontainers.image.version="$VERSION"

# libcap2-bin : setcap, pour écouter sur les ports privilégiés (53, 443, 853…) sans être root ; retiré
# aussitôt. libmsquic (DNS-over-QUIC) n'est pas dans les dépôts Ubuntu : sans lui, ce seul service reste
# inactif (voir DnsOverQuicService).
COPY --from=build /app /app
COPY deploy/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN apt-get update \
    && apt-get install -y --no-install-recommends libcap2-bin \
    && setcap cap_net_bind_service=+ep /app/OnyxFilter \
    && apt-get purge -y libcap2-bin \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/* \
    && chmod +x /usr/local/bin/docker-entrypoint.sh \
    && ln -s /app/OnyxFilter /usr/local/bin/OnyxFilter \
    && mkdir -p /data \
    && chown "$APP_UID" /data

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_HTTP_PORTS= \
    DOTNET_NOLOGO=1 \
    TZ=Etc/UTC

# Les commandes d'administration lancées par « docker exec » travaillent sur /data (dossier courant).
WORKDIR /data
VOLUME /data
USER $APP_UID

# DNS, interface web, HTTPS / DNS-over-HTTPS, DNS-over-TLS, DNS-over-QUIC, défi HTTP Let's Encrypt.
EXPOSE 53/udp 53/tcp 8080/tcp 443/tcp 853/tcp 784/udp 80/tcp

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["/bin/bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/${ASPNETCORE_URLS##*:} 2>/dev/null || exit 1"]

ENTRYPOINT ["docker-entrypoint.sh"]
CMD ["--server", "--data-dir", "/data"]
