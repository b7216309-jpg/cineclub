# Architecture de Cineclub 2.0

Ce document décrit l'implémentation actuelle. Cineclub vise une séance simple entre trois ou quatre personnes de confiance, avec un hôte Windows qui possède le fichier original. Il ne vise ni la diffusion publique, ni l'adaptation automatique du débit, ni une forte isolation entre utilisateurs.

## Vue d'ensemble

| Composant | Responsabilité |
| --- | --- |
| Interface WPF | Accueil, salle, fauteuils, chat et commandes de lecture. |
| `NativeMediaPlayer` | Décode le média avec LibVLC, rend la vidéo dans la fenêtre WPF et charge les sous-titres externes choisis localement. |
| `ClientSession` | Contrat stable entre l'interface et le réseau. |
| `SignalRRoomTransport` | Connexion WebSocket, reprise de session, estimation d'horloge et corrections de dérive. |
| `EmbeddedRoomServer` | Démarre Kestrel sur un port de boucle locale libre et expose le média et le hub. |
| `RoomCoordinator` / `RoomHub` | État autoritatif en mémoire, participants, chat et commandes planifiées. |
| `CloudflareTunnelService` | Lance le Quick Tunnel qui rend le serveur local joignable en HTTPS. |

L'hôte exécute à la fois le serveur et un client. Son propre client rejoint le hub par l'adresse de boucle locale; les invités utilisent l'origine HTTPS du tunnel. Le chemin du fichier ne quitte jamais l'hôte : le snapshot ne contient qu'un endpoint média relatif.

## Création et connexion

1. L'hôte choisit un fichier.
2. Kestrel écoute sur `127.0.0.1` avec un port alloué par le système.
3. Le client hôte rejoint `/room` avec le jeton de salle et la clé d'hôte.
4. `cloudflared` ouvre un Quick Tunnel vers l'origine locale. Deux tentatives courtes sont faites.
5. Une invitation `https://…trycloudflare.com/join?token=…` est créée seulement lorsque le tunnel est prêt.
6. Un invité extrait l'origine et le jeton du lien, ouvre `/room` directement en WebSocket puis rejoint la salle.

Si les deux tentatives de tunnel échouent, l'hébergement échoue et aucun lien `127.0.0.1` n'est présenté comme partageable. Si le processus meurt après publication du lien, l'interface signale que le lien public est interrompu; il n'y a pas de redémarrage automatique du tunnel dans la version actuelle.

## Surface réseau

- `GET /health` : état minimal du serveur.
- `GET /media/{token}` : octets du fichier, avec prise en charge des requêtes HTTP Range.
- `HEAD /media/{token}` : métadonnées nécessaires au lecteur.
- `/room` : hub SignalR utilisé en WebSocket.

Le serveur lit le fichier à la demande. Il ne transcode pas, ne remuxe pas et ne crée pas de copie intermédiaire. LibVLC choisit localement le démultiplexeur et le décodeur. Cette conception supprime l'attente d'encodage, au prix d'un débit montant par invité proche du débit de la source et de l'absence de qualité adaptative.

## Identités et jetons

- Le jeton d'accès est inclus dans l'invitation et autorise l'entrée dans la salle ainsi que la lecture du média.
- La clé d'hôte est conservée dans le processus hôte et autorise les commandes globales.
- Le jeton de reprise est remis à chaque participant après son premier `Join`; le client le réutilise après une reconnexion pour retrouver la même identité et le même fauteuil.

Ces valeurs sont aléatoires et comparées côté serveur sans exposer le chemin local. Elles ne remplacent pas une authentification utilisateur. Toute personne possédant l'invitation doit être considérée comme autorisée pendant la durée de la séance.

## Synchronisation de lecture

Le serveur conserve l'état autoritatif : position ancrée, état lecture/pause, instant serveur de l'ancrage, révision et éventuelle commande future. Les commandes transportent une heure d'exécution serveur; chaque client la convertit vers son horloge locale.

À la connexion et après une reprise, le transport effectue cinq pings. L'échantillon au plus faible aller-retour sert à estimer le décalage entre l'horloge locale et celle du serveur. Le premier lancement demande que tous les participants soient prêts et programme un compte à rebours d'environ trois secondes. Les lectures, pauses et déplacements suivants sont des commandes planifiées normales.

Les snapshots périodiques permettent aussi de corriger la dérive pendant une lecture non bloquée :

- moins de 150 ms : retour à la vitesse normale;
- de 150 à 750 ms : correction douce à 0,98× ou 1,02×, sans saut;
- plus de 750 ms : repositionnement ferme;
- au plus une correction par seconde.

La cible est extrapolée jusqu'à l'heure serveur estimée au moment du snapshot, et non figée à l'heure d'émission du snapshot. Les révisions empêchent une ancienne commande réseau d'écraser une commande plus récente.

## Reconnexion et cycle de vie

SignalR utilise une reconnexion automatique avec délais croissants. Après le retour du WebSocket, le transport recalcule l'horloge, renvoie le jeton de reprise et récupère un snapshot. Les événements d'une ancienne connexion sont ignorés grâce à une durée de vie propre à chaque session.

Quitter une salle arrête et libère, dans cet ordre, la connexion SignalR, le tunnel puis Kestrel. L'arrêt continue même si l'un des composants renvoie une erreur. Fermer l'application suit le même chemin afin de ne pas laisser `cloudflared` ou le serveur local en arrière-plan.

## Publication

`cloudflared.exe` n'est pas stocké dans Git. `Obtenir-Cloudflared.ps1` télécharge la version épinglée, vérifie son SHA-256 puis l'installe dans `Cineclub.App/Tools`. `Publier-Cineclub.ps1` exécute ce bootstrap et produit l'application Windows x64 autonome avec le profil `WinX64.pubxml`.

Le mode single-file peut extraire les bibliothèques natives et le contenu embarqué dans un répertoire d'exécution géré par .NET. Le code recherche donc LibVLC et `Tools/cloudflared.exe` dans le répertoire de base ainsi que dans les répertoires natifs annoncés par le runtime.

## Validation et limites

Le projet `Cineclub.Smoke` vérifie le serveur, les réponses Range, un scénario SignalR à six clients dans un seul processus et le rejet d'un septième. Il ne remplace pas une séance manuelle avec un vrai fichier, LibVLC, le tunnel Internet et plusieurs PC. Les limites maintenues à jour sont listées dans le [README](../README.md#limites-connues).
