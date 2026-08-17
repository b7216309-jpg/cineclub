# Politique de sécurité

## Versions prises en charge

Ce projet personnel ne maintient pas plusieurs branches de sécurité. Seule la dernière version publiée est destinée à recevoir des corrections.

## Signaler un problème

Utilise le signalement privé de vulnérabilité du dépôt GitHub s'il est activé. Sinon, contacte le mainteneur par un canal privé avant de publier les détails. N'inclus jamais un lien d'invitation actif, un jeton de salle ou un fichier vidéo dans un rapport public.

Pour être utile, un rapport doit préciser la version de Cineclub, la version de Windows, les étapes minimales de reproduction et l'impact observé.

## Modèle de sécurité

Cineclub vise un petit groupe d'amis de confiance; ce n'est pas une plateforme publique durcie.

- L'invitation est un jeton porteur aléatoire placé dans l'URL. La possession du lien donne accès à la salle et au flux média pendant la session.
- Le lien invité n'inclut pas la clé d'hôte. Les commandes de lecture sont refusées aux invités par le serveur.
- Kestrel écoute uniquement sur l'interface loopback. `cloudflared` établit une connexion sortante et expose le service avec une URL HTTPS temporaire.
- Le chemin local du film n'est jamais envoyé aux invités. Seuls le titre, la taille et une route média opaque sont annoncés.
- Le fichier est transmis à la demande à travers le tunnel; il n'existe pas de chiffrement applicatif de bout en bout au-dessus de HTTPS.
- L'état, les noms et le chat sont conservés uniquement en mémoire jusqu'à l'arrêt de l'hôte.
- Il n'y a ni comptes, ni rôles administratifs persistants, ni modération, ni journal d'audit.

## Bonnes pratiques d'utilisation

- Partage le lien uniquement avec les invités et ne le publie pas dans un canal ouvert.
- Ferme la salle après la séance; l'URL temporaire ne doit pas être réutilisée.
- Télécharge les builds depuis les Releases du dépôt et vérifie leur provenance.
- N'utilise pas Cineclub pour des fichiers confidentiels ou dans un environnement hostile.
- Respecte les droits applicables aux médias que tu diffuses.

Les choix de sécurité et leurs limites techniques sont détaillés dans [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
