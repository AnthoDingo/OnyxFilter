# OnyxFilter

Résolveur DNS filtrant auto-hébergé : il bloque publicités, traqueurs et domaines indésirables pour tous les
appareils du réseau, avec une interface web d'administration, une API d'automatisation et un outil en ligne
de commande, le tout dans un seul binaire.

Écrit en C# (.NET 10, ASP.NET Core Blazor), pensé pour tourner sur une petite machine (2 Go de RAM suffisent).

## Fonctionnalités

- **Serveur DNS** en UDP/TCP (port 53), et en DNS chiffré : DNS-over-HTTPS, DNS-over-TLS et DNS-over-QUIC
  (ce dernier nécessite le paquet `libmsquic` sous Linux).
- **Filtrage** : listes de blocage et d'autorisation (format Adblock ou fichier hosts, actualisées
  automatiquement), règles personnalisées (y compris expressions régulières), réécritures DNS, blocage de
  services populaires en un clic avec pause programmable.
- **Protections** : sécurité de navigation (logiciels malveillants, hameçonnage), contrôle parental,
  recherche sécurisée imposée, par défaut ou client par client.
- **Serveurs en amont** : UDP, TCP, DNS-over-TLS et DNS-over-HTTPS, en répartition, en parallèle ou au plus
  rapide, avec serveurs de secours et d'amorçage, cache et limitation de débit.
- **Supervision** : vue d'ensemble (activité horaire, taux de blocage, classements), journal des requêtes
  filtrable, suspension temporaire de la protection.
- **Administration** : interface web en français (thèmes clair et sombre, mobile), connexion par mot de
  passe ou clé d'accès (passkey), API HTTP par jeton, commandes en ligne de commande.
- **Mises à jour** en un clic depuis l'interface, à partir des publications GitHub.

## Installation (Linux x64)

Prérequis : Linux x64 avec systemd. La version publiée est autonome (elle embarque le runtime .NET) : rien
d'autre à installer.

```sh
curl -fsSL https://github.com/AnthoDingo/OnyxFilter/releases/latest/download/install.sh | sudo bash
```

Le script télécharge la dernière publication, vérifie son empreinte SHA-256, l'installe dans
`/opt/onyxfilter` sous un compte système dédié `onyxfilter`, puis active le service `onyxfilter`.
Options : `--version 1.4.0`, `--port 8080` (interface web), `--dir /opt/onyxfilter`. Relancé sur une
installation existante, il la met à jour en conservant données et réglages.

### Premier démarrage

L'interface est servie sur `http://<adresse-du-serveur>:8080`. Au premier démarrage, un compte `admin` est
créé avec un mot de passe aléatoire, affiché dans le journal du service :

```sh
sudo journalctl -u onyxfilter | grep -A3 'Compte administrateur'
```

Changez-le ensuite depuis « Mon compte », ou en ligne de commande (voir plus bas). Mot de passe perdu ou
introuvable dans le journal : définissez-en un nouveau avec
`sudo -u onyxfilter /opt/onyxfilter/OnyxFilter user:reset-password admin`. Il reste à indiquer l'adresse du
serveur comme DNS dans les réglages DHCP de votre box ou de votre routeur.

### Port 53 déjà utilisé

Sur beaucoup de distributions, `systemd-resolved` occupe déjà le port 53. Désactivez son écouteur local :

```sh
# /etc/systemd/resolved.conf
[Resolve]
DNSStubListener=no
```

puis `sudo systemctl restart systemd-resolved && sudo systemctl restart onyxfilter`.

### Installation manuelle

1. Téléchargez `OnyxFilter-<version>-linux-x64.tar.gz` et `checksums.txt` depuis les
   [publications](https://github.com/AnthoDingo/OnyxFilter/releases), vérifiez l'archive
   (`sha256sum --check --ignore-missing checksums.txt`) et extrayez-la dans `/opt/onyxfilter`.
2. Installez le service fourni : `sudo cp /opt/onyxfilter/onyxfilter.service /etc/systemd/system/`
   (adaptez `User`, `WorkingDirectory` et `ASPNETCORE_URLS` si besoin), créez le compte `onyxfilter` et
   donnez-lui la propriété du dossier.
3. `sudo systemctl daemon-reload && sudo systemctl enable --now onyxfilter`.

Le service doit conserver `Restart=always` : c'est ce qui relance OnyxFilter après l'installation d'une mise
à jour.

## Mises à jour

OnyxFilter recherche ses nouvelles versions sur GitHub toutes les 12 heures (désactivable) ; une pastille
apparaît alors dans le menu. Depuis **Configuration › Mises à jour**, un clic suffit :

1. téléchargement de l'archive linux-x64 et vérification de son empreinte SHA-256 (`checksums.txt`
   obligatoire) ;
2. extraction contrôlée, et vérification que l'archive contient bien la version annoncée ;
3. remplacement des fichiers de l'application ; les fichiers remplacés sont sauvegardés dans
   `/opt/onyxfilter/.update/backup/`. Les données et réglages (base, `appsettings*.json`, caches) ne sont
   jamais modifiés ;
4. arrêt d'OnyxFilter, relancé aussitôt par systemd sur la nouvelle version (quelques secondes
   d'interruption du DNS).

En cas d'échec, l'installation d'origine est restaurée. Pour revenir manuellement à la version précédente :

```sh
sudo systemctl stop onyxfilter
sudo cp -a /opt/onyxfilter/.update/backup/. /opt/onyxfilter/
sudo systemctl start onyxfilter
```

L'installation intégrée n'est proposée que pour une version publiée autonome linux-x64 lancée par systemd,
dans un dossier accessible en écriture au compte du service. Ailleurs (compilation locale, conteneur, autre
plateforme), la nouvelle version est seulement signalée. Les préversions (`v1.5.0-beta.1`…) peuvent être
proposées en activant l'option correspondante.

La section `Updates` d'`appsettings.json` permet de suivre un autre dépôt (`Repository`), un miroir compatible
avec l'API GitHub (`ApiBaseUrl`), ou d'autoriser l'installation sous un autre superviseur que systemd
(`AssumeSupervised`).

## Ligne de commande

Le même binaire sert de serveur et d'outil d'administration. Sans argument, il affiche l'aide.

| Commande | Rôle |
| --- | --- |
| `OnyxFilter --server [--urls …]` | Lance le serveur (DNS, interface web, API). |
| `OnyxFilter status` | Résumé de l'installation : dossiers, base, protections, réglages. |
| `OnyxFilter user:list` | Liste les comptes. |
| `OnyxFilter user:add <nom> [mot de passe]` | Crée un compte (mot de passe demandé s'il est omis). |
| `OnyxFilter user:reset-password <nom> [mot de passe]` | Réinitialise un mot de passe (ancienne forme : `--reset`). |
| `OnyxFilter user:remove <nom> [--yes]` | Supprime un compte (jamais le dernier). |
| `OnyxFilter rules:list` / `rules:add <règle>` / `rules:remove <règle>` | Règles de filtrage personnalisées. |
| `OnyxFilter lists:list` | Listes de blocage et d'autorisation. |
| `OnyxFilter update:check [--pre]` | Recherche une nouvelle version. |
| `OnyxFilter --version` | Affiche la version. |

Les commandes travaillent sur le dossier des données : le dossier courant s'il en contient, sinon celui de
l'exécutable, ou celui indiqué par `--data-dir <dossier>`. Sur un serveur installé, lancez-les avec le compte
du service pour que les fichiers modifiés lui restent accessibles, par exemple
`sudo -u onyxfilter /opt/onyxfilter/OnyxFilter user:list`. Un serveur démarré ne relit pas les réglages
modifiés en ligne de commande : redémarrez-le (`sudo systemctl restart onyxfilter`).

## API HTTP

L'API `/api/v1` permet d'automatiser OnyxFilter (domotique, scripts, supervision). Créez un jeton depuis
**Configuration › Accès API**, puis transmettez-le dans l'en-tête `Authorization: Bearer <jeton>` (ou
`X-Api-Key`). La page détaille chaque point d'accès et donne des exemples.

| Point d'accès | Rôle |
| --- | --- |
| `GET /api/v1/protection` | État du filtrage. |
| `POST /api/v1/protection/disable` | Suspend le filtrage, `{"durationSeconds": 600}` ou `{"until": "…"}` optionnels. |
| `POST /api/v1/protection/enable` | Réactive le filtrage. |
| `GET /api/v1/stats` | Statistiques d'usage sur 24 heures. |
| `GET /api/v1/querylog` | Journal des requêtes (`limit`, `offset`, `search`, `reason`). |
| `GET /api/v1/update` | État des mises à jour. |
| `POST /api/v1/update/check` | Recherche une nouvelle version. |
| `POST /api/v1/update/install` | Installe la nouvelle version puis redémarre. |
| `GET /api/v1/access` | Accès des clients au DNS : mode et listes (autorisés, refusés, toujours autorisés). |
| `GET /api/v1/access/check?ip=…` | Indique si une adresse est servie, et par quelle règle. |
| `POST /api/v1/access/block` | Bloque une adresse IP ou un sous-réseau, `{"client": "192.168.1.50"}`. |
| `POST /api/v1/access/allow` | Autorise une adresse IP ou un sous-réseau (même format). |

```sh
curl -X POST -H "Authorization: Bearer $ONYX_TOKEN" -H "Content-Type: application/json" \
     -d '{"durationSeconds": 600}' http://onyxfilter.lan:8080/api/v1/protection/disable
```

Les points d'accès `/access` modifient les listes de **Paramètres DNS › Contrôle d'accès**, avec effet
immédiat et sans changer de mode : sans liste de clients autorisés, bloquer ajoute le client aux refusés ; avec
une telle liste, bloquer l'en retire (jamais sa dernière règle, ce qui ouvrirait le DNS à tous). Une règle
précise l'emporte sur une règle plus large : autoriser `203.0.113.5` alors que `203.0.113.0/24` est refusé
l'ajoute aux clients toujours autorisés, et seule cette adresse est débloquée.

Hors de votre réseau local, n'exposez l'API qu'en HTTPS : le jeton accompagne chaque requête.

## Configuration

- **Interface web** : adresse d'écoute via `ASPNETCORE_URLS` (dans le service systemd) ou `--urls`.
- **Réglages de l'interface** : enregistrés dans `appsettings.local.json`, à côté de la base
  `OnyxFilter.db`. Ces deux fichiers constituent l'essentiel de ce qu'il faut sauvegarder.
- **Réglages par défaut** : `appsettings.json` (chaîne de connexion, listes de blocage initiales, section
  `Updates`).

## Développement

Prérequis : [SDK .NET 10](https://dotnet.microsoft.com/download).

```sh
cd src/OnyxFilter
dotnet run              # profil de lancement : ajoute --server, interface sur http://localhost:5259
dotnet run -- status    # n'importe quelle commande
```

Le port 53 demande des droits d'administration (ou `CAP_NET_BIND_SERVICE` sous Linux). Le code est organisé
ainsi :

- `Components/` : interface Blazor (pages, mise en page, composants partagés) ;
- `Services/` : moteur DNS, filtrage, statistiques, journal, API (`Services/Api`), mises à jour
  (`Services/Updates`) ;
- `Cli/` : commandes en ligne de commande ;
- `deploy/` : service systemd et script d'installation ;
- `.github/workflows/release.yml` : publication des versions.

## Publier une version

Poussez un tag au format `vMAJEUR.MINEUR.CORRECTIF` (ou `v1.5.0-beta.1` pour une préversion) :

```sh
git tag v1.4.0
git push origin v1.4.0
```

Le workflow « Publication » compile la version autonome linux-x64 avec ce numéro, crée
`OnyxFilter-1.4.0-linux-x64.tar.gz`, `checksums.txt` et joint `install.sh` à une publication GitHub. Les
installations existantes la proposent alors dans leur page « Mises à jour ».

## Crédits

- Catalogue des services bloquables repris de
  [AdguardTeam/HostlistsRegistry](https://github.com/AdguardTeam/HostlistsRegistry).
- Interface construite avec [Bootstrap](https://getbootstrap.com/) (licence MIT) et
  [Spectre.Console](https://spectreconsole.net/) pour la ligne de commande.
- QR codes générés avec [QRCoder](https://github.com/codebude/QRCoder) (licence MIT).
- Drapeaux des pays : police « Twemoji Country Flags »
  ([country-flag-emoji-polyfill](https://github.com/talkjs/country-flag-emoji-polyfill), MIT), graphismes
  [Twemoji](https://github.com/jdecked/twemoji) © Twitter, Inc. et contributeurs, licence CC-BY 4.0.
- Pays et fournisseurs d'accès des clients : base [iptoasn.com](https://iptoasn.com) (domaine public, PDDL).
- Carte des pays : frontières [Natural Earth](https://www.naturalearthdata.com/) (domaine public).

## Licence

OnyxFilter est un logiciel libre : vous pouvez le redistribuer et le modifier selon les termes de la
[GNU Affero General Public License](LICENSE) publiée par la Free Software Foundation, version 3 ou (à votre
choix) toute version ultérieure (`AGPL-3.0-or-later`).

Il est distribué dans l'espoir d'être utile, mais **sans aucune garantie**, ni explicite ni implicite, y
compris de qualité marchande ou d'adéquation à un usage particulier. Voir le fichier [LICENSE](LICENSE).

L'AGPL s'applique aussi à l'usage en réseau : si vous proposez une version modifiée d'OnyxFilter à des
utilisateurs à travers un réseau (interface web, DNS), vous devez leur donner accès au code source de cette
version.
