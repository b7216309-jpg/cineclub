# Historique des versions

Ce projet suit [Semantic Versioning](https://semver.org/lang/fr/) lorsque des versions sont publiées.

## [2.0.0] - 2026-08-17

### Ajouté

- Application Windows x64 WPF autonome pour les hôtes et les invités.
- Lecture native avec LibVLC intégré, y compris MKV, HEVC, pistes audio et sous-titres pris en charge par LibVLC.
- Ajout local de fichiers de sous-titres SRT, ASS, SSA, VTT ou SUB pour chaque participant.
- Service du fichier original avec Kestrel et requêtes HTTP Range, sans transcodage ni remuxage.
- Invitations HTTPS temporaires par Quick Tunnel Cloudflare.
- Salle SignalR avec fauteuils, statut prêt, chat et réactions.
- Commandes de lecture planifiées, compte à rebours initial, estimation d'horloge et correction de dérive.
- Reconnexion SignalR avec reprise d'identité par `ResumeToken`.
- Scripts reproductibles pour récupérer `cloudflared` avec version et SHA-256 épinglés, puis publier l'application.

### Corrigé

- Amorçage LibVLC sans bloquer le lecteur dans un état de buffering initial.
- Séquencement de la surface vidéo WPF afin d'éviter la fenêtre externe « VLC (Direct3D11 output) ».
- Nettoyage ordonné du hub, du tunnel et du serveur lors d'une fermeture ou d'une annulation.
- Reprise de salle après reconnexion et resynchronisation d'une révision de lecture déjà connue.

### Limites de validation

- Le smoke test automatise six clients dans un même processus, le rejet d'un septième et des octets média synthétiques.
- Cette entrée ne prétend pas valider tous les GPU, codecs, réseaux domestiques, Quick Tunnels ou scénarios sur plusieurs PC physiques.

[2.0.0]: https://github.com/b7216309-jpg/cineclub/releases/tag/v2.0.0
