# Composants tiers

Ce fichier décrit les dépendances directes et transitives présentes dans la configuration `net10.0-windows` de Cineclub 2.0. Les versions ont été vérifiées avec `dotnet list package --include-transitive` le 17 août 2026.

Le code original de Cineclub est sous licence MIT. Les composants ci-dessous ne sont pas relicenciés par Cineclub.

## VideoLAN

| Composant | Version | Usage | Licence déclarée |
| --- | ---: | --- | --- |
| LibVLCSharp | 3.10.1 | API .NET de LibVLC | LGPL-2.1-or-later |
| LibVLCSharp.WPF | 3.10.1 | Surface vidéo WPF | LGPL-2.1-or-later |
| VideoLAN.LibVLC.Windows | 3.0.23.1 | Moteur natif et plugins Windows x64 | LGPL-2.1-or-later |

Sources :

- <https://code.videolan.org/videolan/LibVLCSharp>
- <https://code.videolan.org/videolan/libvlc-nuget>
- <https://code.videolan.org/videolan/vlc>

Le texte LGPL 2.1 est fourni dans [licenses/LGPL-2.1-or-later.txt](licenses/LGPL-2.1-or-later.txt). Les métadonnées NuGet de ces trois paquets déclarent l'expression SPDX `LGPL-2.1-or-later`. Le paquet LibVLC est modulaire; les informations de copyright et de licence de ses modules restent celles du projet VideoLAN et de leurs auteurs respectifs.

## Cloudflare cloudflared

| Composant | Version épinglée | Fichier | Licence |
| --- | ---: | --- | --- |
| cloudflared | 2026.7.3 | `cloudflared-windows-amd64.exe` | Apache-2.0 |

- Version amont : <https://github.com/cloudflare/cloudflared/releases/tag/2026.7.3>
- Code source : <https://github.com/cloudflare/cloudflared>
- SHA-256 du binaire distribué : `8635DA433B6DF8194746E88ED9D2589566C20E38BFC2A80E431A348B7C765841`
- Texte de licence : [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt)

Le binaire n'est pas conservé dans Git. `Obtenir-Cloudflared.ps1` télécharge exactement cette version et refuse de l'installer si le SHA-256 ne correspond pas.

## Microsoft .NET et ASP.NET Core

La configuration vérifiée utilise le SDK .NET 10.0.302 et les runtimes 10.0.10. Le profil de publication est autonome et peut donc redistribuer des composants du runtime .NET, de Windows Desktop/WPF et d'ASP.NET Core.

Paquets NuGet directs ou transitifs :

| Paquet | Version | Licence déclarée |
| --- | ---: | --- |
| Microsoft.AspNetCore.SignalR.Client | 10.0.10 | MIT |
| Microsoft.AspNetCore.SignalR.Client.Core | 10.0.10 | MIT |
| Microsoft.AspNetCore.Http.Connections.Client | 10.0.10 | MIT |

Sources :

- <https://github.com/dotnet/dotnet>
- <https://github.com/dotnet/aspnetcore>
- <https://github.com/dotnet/wpf>

Le texte MIT est fourni dans [licenses/MIT.txt](licenses/MIT.txt). Les composants .NET peuvent inclure d'autres logiciels avec leurs propres avis; consulte les avis Microsoft livrés avec le SDK/runtime correspondant et le fichier `THIRD-PARTY-NOTICES.TXT` inclus dans les paquets NuGet Microsoft.

## Référence non intégrée

[Syncplay](https://github.com/Syncplay/syncplay) a servi de référence conceptuelle pour comparer les stratégies de synchronisation. Aucun fichier, paquet ou code Syncplay n'est distribué par Cineclub 2.0; sa licence Apache-2.0 ne s'applique donc pas au binaire Cineclub.
