# Contribuer à Cineclub

Merci de garder le projet simple : une application Windows native, une salle en mémoire et aucun pipeline de transcodage.

## Préparer l'environnement

- Windows 10 ou 11 x64
- Git
- SDK .NET 10

```powershell
dotnet restore .\Cineclub.slnx
dotnet build .\Cineclub.slnx -c Debug --no-restore -warnaserror
```

Le build et le smoke test ne nécessitent pas `cloudflared.exe`. Le script suivant télécharge la version épinglée uniquement pour tester l'hébergement public ou publier :

```powershell
.\Obtenir-Cloudflared.ps1
```

Ne committe jamais `Cineclub.App/Tools/cloudflared.exe`, un film, une URL d'invitation, un jeton ou un fichier produit dans `publish/`.

## Vérifications avant une proposition

Exécute au minimum :

```powershell
dotnet build .\Cineclub.slnx -c Release --no-restore -warnaserror
dotnet run --project .\Cineclub.Smoke -c Release --no-build
```

Le smoke test vérifie Kestrel, HTTP Range, six connexions SignalR, le rejet d'un septième client, les fauteuils, le statut prêt, le chat, les commandes et le `ResumeToken`. Il ne vérifie pas LibVLC, WPF, Cloudflare ni plusieurs machines.

Pour une modification du lecteur, du tunnel ou de la synchronisation, décris aussi le test manuel effectué. Les scénarios utiles sont : hôte et invité sur deux processus, arrivée pendant pause et pendant lecture, recherche dans le film, microcoupure réseau, fermeture pendant la connexion et lecture d'un vrai fichier 1080p.

## Style et portée

- Active les types nullable et conserve zéro avertissement de compilation.
- Préfère des changements ciblés et des messages d'erreur compréhensibles en français.
- Ne journalise pas les jetons, les URL complètes d'invitation ou les chemins locaux des films.
- Ne réintroduis pas FFmpeg, HLS ou un transcodage sans discussion d'architecture préalable.
- Toute nouvelle dépendance doit être justifiée, épinglée et ajoutée à `THIRD_PARTY_NOTICES.md`.
- Les fichiers texte suivent `.gitattributes`; les binaires générés restent hors Git.

## Publication

`Publier-Cineclub.ps1` est le chemin de publication de référence. Il vérifie `cloudflared`, produit le paquet autonome x64 et copie les licences dans `publish/win-x64`. Il n'effectue ni signature de code ni publication GitHub automatique.

En contribuant, tu acceptes que ton code soit distribué sous la licence MIT du projet et confirmes avoir le droit de le proposer.
