# Cineclub 2.0

[![CI](https://github.com/b7216309-jpg/cineclub/actions/workflows/ci.yml/badge.svg)](https://github.com/b7216309-jpg/cineclub/actions/workflows/ci.yml)
[![Licence MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)

Cineclub est une petite application Windows pour regarder un film à distance avec quelques amis. Tous les participants utilisent l'application native : l'hôte fournit le fichier original et chaque PC le décode localement avec LibVLC.

Téléchargement : [dernière version publiée](https://github.com/b7216309-jpg/cineclub/releases/latest)

## Ce que fait Cineclub

- Sert les octets du fichier original avec HTTP Range.
- Lit MKV, HEVC/H.265, pistes audio et sous-titres avec LibVLC intégré.
- Permet à chacun d'ajouter localement un fichier SRT, ASS, SSA, VTT ou SUB.
- Synchronise lecture, pause et déplacements avec SignalR WebSocket.
- Propose une salle privée légère : prénoms, fauteuils, statut prêt, chat et réactions.
- Crée un lien HTTPS temporaire avec un Quick Tunnel Cloudflare.

Cineclub ne transcode pas, ne remuxe pas et ne prépare pas une copie du film. Il n'y a donc pas d'attente d'encodage, mais chaque invité reçoit le débit complet de la source et son PC doit savoir la décoder.

## Utiliser une version publiée

Prérequis : Windows 10 ou 11 en 64 bits. Pour le HEVC 1080p, un décodage matériel récent est recommandé.

### Hôte

1. Télécharge la dernière archive ZIP depuis la page des Releases.
2. Lance `Cineclub.exe`, saisis ton prénom puis choisis **Héberger une séance**.
3. Sélectionne le fichier vidéo original.
4. Attends la création du lien public, puis envoie ce lien uniquement aux invités.
5. Quand tous les participants sont prêts, lance le film. Le premier départ utilise un compte à rebours de trois secondes.

Si le tunnel public échoue après deux tentatives, la création de salle échoue clairement et l'application revient à l'accueil. Cineclub n'affiche pas un faux lien `127.0.0.1` aux invités.

### Invité

1. Lance la même version de `Cineclub.exe`.
2. Saisis ton prénom et choisis **Rejoindre avec un lien**.
3. Colle l'invitation reçue puis attends le chargement de la première image.
4. Choisis ton fauteuil et indique que tu es prêt.

L'hôte contrôle lecture, pause et navigation. Le volume, la piste audio, les sous-titres et le plein écran restent locaux à chaque PC. Le bouton **SRT +** permet d'ajouter un fichier de sous-titres externe sans l'envoyer aux autres participants.

## Débit nécessaire

Le débit montant de l'hôte augmente presque linéairement avec le nombre d'invités distants :

```text
débit montant utile ≈ débit moyen du film × nombre d'invités
```

Ajoute une marge pour les pics de débit, les recherches dans le film et le protocole réseau. Exemple mesuré pendant le développement : une source moyenne à 3,72 Mbit/s demande environ 11,2 Mbit/s bruts pour trois invités; viser au moins 15 Mbit/s laisse une marge raisonnable. Cet exemple ne garantit rien pour un autre fichier : certains remux Blu-ray peuvent être beaucoup plus lourds.

## Sécurité et confidentialité

- Le lien contient un jeton d'accès aléatoire. Toute personne qui possède le lien peut rejoindre la salle et demander les octets du film tant que l'hôte est en ligne.
- Le lien public est en HTTPS, mais il ne fournit ni compte utilisateur, ni mot de passe, ni chiffrement de bout en bout applicatif.
- Le trafic distant passe par le Quick Tunnel Cloudflare. Ne considère pas Cineclub comme une solution adaptée à des contenus sensibles.
- L'état de la salle et le chat restent en mémoire et disparaissent à l'arrêt de l'hôte.

Consulte [SECURITY.md](SECURITY.md) pour le modèle de sécurité complet.

## Dépannage

### Le lien public ne se crée pas

- Vérifie l'accès Internet de l'hôte et que le pare-feu ou l'antivirus n'empêche pas `cloudflared` de sortir en HTTPS.
- Réessaie quelques minutes plus tard : les Quick Tunnels n'ont pas de garantie de disponibilité.
- Avec une compilation personnelle, exécute `./Obtenir-Cloudflared.ps1` avant de publier.

### Les invités ont des coupures

- Mesure surtout le débit **montant** de l'hôte.
- Ferme les sauvegardes cloud, téléchargements et diffusions simultanées.
- Utilise Ethernet ou un Wi-Fi stable sur le PC hôte.
- Une source trop lourde ne sera pas automatiquement réduite : Cineclub n'a pas de mode adaptatif.

### Le HEVC consomme beaucoup de processeur

Le décodage est fait sur chaque PC. Mets à jour le pilote graphique et vérifie que la machine prend en charge le HEVC matériel. Un ancien PC peut décoder en logiciel et ne pas tenir le 1080p.

### Windows SmartScreen affiche un avertissement

Les builds communautaires ne sont pas nécessairement signés. Vérifie que le fichier vient bien de la page Releases officielle du dépôt avant de l'exécuter.

### Une fenêtre « VLC (Direct3D11 output) » séparée apparaît

Ce comportement n'est pas normal : la vidéo doit rester intégrée à Cineclub. Ferme la session et ouvre un rapport de bug avec la version de Windows et du pilote graphique.

## Limites connues

- Windows x64 uniquement.
- Dépendance au service Quick Tunnel Cloudflare, sans SLA.
- Aucun transcodage ni changement automatique de qualité.
- Aucune persistance de salle et aucun transfert d'hôte.
- Sécurité volontairement minimale pour un petit groupe de confiance.
- Exécutable non signé par défaut.
- Le smoke test automatisé utilise six clients SignalR dans un seul processus et vérifie le rejet d'un septième. Il valide le serveur, HTTP Range et le protocole, pas l'interface, le décodage d'un vrai film, le tunnel Internet ni plusieurs PC physiques.

## Compiler

Prérequis développeur : Windows x64, Git et le SDK .NET 10.

```powershell
git clone https://github.com/b7216309-jpg/cineclub.git
cd cineclub
dotnet restore .\Cineclub.slnx
dotnet build .\Cineclub.slnx -c Release --no-restore -warnaserror
dotnet run --project .\Cineclub.Smoke -c Release --no-build
```

Le build source fonctionne sans binaire `cloudflared` dans Git. Pour une application capable d'héberger à distance, télécharge la version épinglée et vérifiée :

```powershell
.\Obtenir-Cloudflared.ps1
```

## Publier

Le script de publication récupère `cloudflared` 2026.7.3, vérifie son SHA-256, puis publie une application autonome Windows x64 :

```powershell
.\Publier-Cineclub.ps1
```

La sortie se trouve dans `publish\win-x64`. Le profil active le mode single-file sans trimming et embarque les bibliothèques natives; .NET et les fichiers natifs peuvent être extraits dans un répertoire temporaire au lancement. Les fichiers de licence sont copiés à côté de l'exécutable.

## Documentation du projet

- [Architecture](docs/ARCHITECTURE.md)
- [Contribuer](CONTRIBUTING.md)
- [Politique de sécurité](SECURITY.md)
- [Historique](CHANGELOG.md)
- [Composants tiers](THIRD_PARTY_NOTICES.md)

Le code propre à Cineclub est sous licence [MIT](LICENSE). Les composants tiers conservent leurs licences respectives.
