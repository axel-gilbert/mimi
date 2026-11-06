# Mimi 💗

> *« c'est mimi »* : des cœurs pour ta moitié.

Mimi est un tout petit logiciel Windows pour les couples. Tu appuies sur ton raccourci
(par défaut **Ctrl + Alt + H**), et un flux de petits cœurs monte dans le coin de l'écran
de l'autre, comme les likes d'un live TikTok, avec un petit son tout doux.
Plus tu spammes, plus le flux est dense (sans jamais envahir l'écran). Au 10ᵉ, 25ᵉ et 50ᵉ cœur
d'affilée, un gros cœur monte et éclate en mini-cœurs.

- **Un seul fichier** `Mimi.exe` (~150 Ko), rien à installer à côté (.NET Framework 4.8, déjà dans Windows 10/11).
- **Pair-à-pair** : les deux PC se parlent directement en UDP, sans serveur ni compte.
- **Appairage par code** à 6 chiffres.
- **Démarre avec Windows** et se connecte tout seul.
- **Léger** : 0 % CPU au repos (la fenêtre de rendu est cachée). Pendant un spam de 40 cœurs,
  environ 5 % d'un cœur CPU (rendu logiciel maison, seule la zone qui bouge est mise à jour,
  budget de particules auto-régulé, priorité « inférieure à la normale »).
- **S'adapte à l'écran** : tailles calculées d'après la hauteur de l'écran (16:9, 21:9, 32:9, 4K, mise à l'échelle Windows).
- **Discret** : les cœurs restent dans un coin (bas droite, bas gauche ou bas centre, au choix), et les clics passent au travers.

## Installer

1. Copie `Mimi.exe` sur chaque PC (clé USB, Discord, etc.).
2. Double-clique dessus et réponds **Oui** à « Installer Mimi sur ce PC ? ».
   - Mimi s'installe dans `%LOCALAPPDATA%\Programs\Mimi` (pas besoin d'être administrateur).
   - Windows demande une autorisation pour ajouter la règle du pare-feu ; accepte-la.
     Si tu la refuses, Windows posera la question au premier lancement.
3. La fenêtre de réglages s'ouvre : mets ton prénom.

Pour mettre à jour, relance simplement un `Mimi.exe` plus récent et réponds **Oui**.

## S'appairer (une seule fois)

Les deux PC doivent être sur **la même box / le même Wi-Fi** pour l'appairage.

1. Sur le PC n°1 : **Créer un code**. Un code à 6 chiffres s'affiche (valable 5 minutes).
2. Sur le PC n°2 : tape ce code puis clique sur **Rejoindre**.
3. Les cœurs pleuvent, c'est appairé 💞

Ensuite, si l'adresse IP d'un des PC change (box redémarrée…), Mimi retrouve l'autre tout seul sur le réseau local.

### Et à distance, sur deux réseaux différents ?

Mimi est en pair-à-pair, sans serveur, donc les deux PC doivent pouvoir se joindre.
Le plus simple est d'installer [Tailscale](https://tailscale.com) (gratuit) sur les deux PC.
Ensuite, dans **Réglages → Avancé**, mets l'adresse Tailscale de l'autre (`100.x.y.z`) des deux côtés.
Autre solution : rediriger le port UDP **47777** sur la box.

## Utilisation

| Action | Comment |
|---|---|
| Envoyer un cœur | ton raccourci (configurable dans les réglages) |
| Pluie de cœurs | spamme le raccourci ou garde-le appuyé |
| Changer de coin | Réglages → *Les cœurs apparaissent…* |
| Réglages / appairage | clic sur l'icône cœur près de l'horloge |
| Tester l'animation chez soi | clic droit sur l'icône → *Tester l'animation* |
| Désinstaller | clic droit → *Désinstaller…*, ou Paramètres Windows → Applications |

L'icône est **rose** quand ta moitié est connectée, **grise** sinon.

> Les cœurs s'affichent au-dessus de toutes les fenêtres, y compris les jeux en *plein écran fenêtré / sans bordure*.
> En plein écran *exclusif*, Windows ne laisse rien s'afficher par-dessus.

## Tester sur un seul PC

```powershell
.\Mimi.exe --profile A --pair-host 123456
.\Mimi.exe --profile B --pair-join 123456
```

Chaque profil a ses propres réglages (`%APPDATA%\Mimi\<profil>`). Il faut donner un port
différent à chacun : le profil A utilise 47777, les autres 47778 par défaut.
Le raccourci de B est **Ctrl + Alt + J**.

En cas de souci, les erreurs sont notées dans `%APPDATA%\Mimi\<profil>\erreurs.log`.

## Compiler

Prérequis : le SDK .NET (8 ou plus récent). Il cible .NET Framework 4.8 grâce aux assemblys de référence NuGet.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1   # -> dist\Mimi.exe
```

## Comment ça marche

| Fichier | Rôle |
|---|---|
| `src/Overlay.cs` | Fenêtre transparente (*layered*, traversée par les clics), flux de cœurs : pop avec rebond, ondulation, inclinaison, squash & stretch, battement de cœur |
| `src/Blitter.cs` | Rendu logiciel maison en virgule fixe + grille de zones modifiées (on n'efface et n'envoie que ce qui bouge) |
| `src/Art.cs` | Cœurs dessinés en code (dégradé, reflets, halo), en plusieurs tailles pour rester nets |
| `src/SoundBank.cs` | Sons synthétisés : *bloop* pentatonique, carillon « mi-mi », arpège du gros cœur |
| `src/PeerLink.cs` | Réseau UDP pair-à-pair, appairage par broadcast, ping/présence, suivi d'adresse |
| `src/Installer.cs` | Auto-installation, démarrage avec Windows, menu Démarrer, pare-feu, désinstallation |
| `src/SettingsForm.cs` | Fenêtre de réglages |

Protocole (UDP, port 47777) : `MIMI|1|<tag>|<TYPE>|<prénom>|<instance>`.
`tag` est l'empreinte SHA-256 du code du couple : le code ne circule jamais en clair,
et les paquets d'autres couples sont ignorés.
