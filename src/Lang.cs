using System.Collections.Generic;

namespace Mimi
{
    /// <summary>
    /// Translations. English is the default; French can be picked in the settings.
    /// To add a language: add a dictionary below and an entry in <see cref="Languages"/>.
    /// </summary>
    static class L
    {
        public static string Code = "en";

        /// <summary>(code, name shown in the settings)</summary>
        public static readonly string[][] Languages =
        {
            new[] { "en", "English" },
            new[] { "fr", "Français" },
        };

        public static string T(string key)
        {
            if (Code == "fr" && Fr.TryGetValue(key, out var s)) return s;
            return En.TryGetValue(key, out s) ? s : key;
        }

        public static string F(string key, params object[] args) => string.Format(T(key), args);

        static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            // installer
            ["install.ask"] = "Install Mimi on this PC?\n\n• it starts automatically with Windows\n• it stays quietly next to the clock 💗\n• Windows may ask you to allow network access\n\nYes: install      No: just try it",
            ["install.already"] = "Mimi is already installed on this PC.\n\nYes: update it with this version\nNo: just run this copy",
            ["install.failed"] = "Oops, the installation failed:\n{0}",
            ["uninstall.ask"] = "Uninstall Mimi from this PC?",
            ["uninstall.done"] = "Mimi has been uninstalled. See you soon 💔",
            ["shortcut.desc"] = "Hearts for your other half",

            // tray & app
            ["menu.send"] = "Send a heart",
            ["menu.test"] = "Test the animation",
            ["menu.settings"] = "Settings & pairing…",
            ["menu.update"] = "Check for updates",
            ["menu.uninstall"] = "Uninstall…",
            ["menu.quit"] = "Quit",
            ["net.problem"] = "Network problem",
            ["net.portused"] = "Port {0} is already in use ({1})",
            ["your.half"] = "your other half",
            ["Your.half"] = "Your other half",
            ["status.neterr"] = "Network error",
            ["status.unpaired"] = "Not paired yet",
            ["status.online"] = "Connected to {0}",
            ["status.waiting"] = "Waiting for {0}…",
            ["toast.paired"] = "Paired with {0} 💞",
            ["toast.notpaired"] = "Mimi isn't paired yet",
            ["toast.sends"] = "{0} is sending you hearts",
            ["hk.unavailable.title"] = "Shortcut unavailable",
            ["hk.unavailable"] = "{0} is already used by another app. Pick another one in the settings.",
            ["hk.taken"] = "This shortcut is already taken by another app 😕",

            // updates
            ["upd.title"] = "Mimi update",
            ["upd.uptodate"] = "You have the latest version ({0}) 👍",
            ["upd.failed"] = "Couldn't update Mimi: {0}",
            ["upd.done"] = "Mimi updated to {0} 💗",

            // settings
            ["set.title"] = "Mimi — settings",
            ["set.subtitle"] = "hearts for your other half",
            ["set.name"] = "Your name (your other half will see it)",
            ["set.pairing"] = "Pairing",
            ["set.createcode"] = "Create a code",
            ["set.or"] = "or",
            ["set.codecue"] = "code",
            ["set.join"] = "Join",
            ["set.cancel"] = "Cancel",
            ["set.unpair"] = "Unpair",
            ["set.pair.hint.idle"] = "One PC creates a code, the other types it in.\nBoth PCs must be on the same Wi-Fi / router.",
            ["set.pair.hint.host"] = "Type this code on the other PC, then click \"Join\".\nValid for 5 minutes.",
            ["set.pair.searching"] = "Looking for your other half…",
            ["set.pair.paired"] = "💞  Paired with {0}",
            ["set.pair.online"] = "●  connected, ready to receive hearts",
            ["set.pair.offline"] = "●  offline for now (PC turned off or on another network)",
            ["set.unpair.ask"] = "Unpair Mimi from {0}?",
            ["set.code6"] = "The code has 6 digits 🙂",
            ["set.netunavail"] = "Network unavailable",
            ["set.pair.notfound"] = "Not found 😕 Same Wi-Fi? Is the code still shown on the other PC?",
            ["set.shortcut"] = "Shortcut",
            ["set.shortcut.label"] = "To send a heart (hold it or spam it 😏)",
            ["set.hk.hint"] = "Click the box, then press your keys",
            ["set.hk.press"] = "Press your keys…\n(Esc = cancel)",
            ["set.hk.ok"] = "Perfect! Don't forget to save.",
            ["set.hk.needmod"] = "Add Ctrl, Alt or Shift 🙂",
            ["set.sound"] = "Sound & options",
            ["set.sound.on"] = "Sound on",
            ["set.echo"] = "Also show my hearts when I send them",
            ["set.autostart"] = "Start Mimi with Windows",
            ["set.autoupdate"] = "Update Mimi automatically",
            ["set.corner"] = "Hearts appear",
            ["set.corner.br"] = "bottom right",
            ["set.corner.bl"] = "bottom left",
            ["set.corner.bc"] = "bottom center",
            ["set.language"] = "Language",
            ["set.advanced"] = "Advanced",
            ["set.peer"] = "Other half's address (filled in by pairing)",
            ["set.peer.cue"] = "e.g. 192.168.1.42 or 100.64.0.2",
            ["set.port"] = "Port",
            ["set.myaddr"] = "My address: {0}",
            ["set.test"] = "Test the animation",
            ["set.save"] = "Save 💗",
            ["set.choosehk"] = "Pick a shortcut 🙂",
            ["set.saved"] = "Saved ✓",

            // key names
            ["key.ctrl"] = "Ctrl", ["key.alt"] = "Alt", ["key.shift"] = "Shift", ["key.win"] = "Win",
            ["key.space"] = "Space", ["key.enter"] = "Enter", ["key.numpad"] = "Num",
            ["key.scroll"] = "Scroll Lock", ["key.insert"] = "Ins", ["key.home"] = "Home",
            ["key.end"] = "End", ["key.pgup"] = "PgUp", ["key.pgdn"] = "PgDn",
        };

        static readonly Dictionary<string, string> Fr = new Dictionary<string, string>
        {
            ["install.ask"] = "Installer Mimi sur ce PC ?\n\n• il démarrera tout seul avec Windows\n• il reste discret, près de l'horloge 💗\n• Windows te demandera peut-être d'autoriser le réseau\n\nOui : installer      Non : juste l'essayer",
            ["install.already"] = "Mimi est déjà installé sur ce PC.\n\nOui : mettre à jour avec cette version\nNon : juste lancer cette copie",
            ["install.failed"] = "Oups, l'installation a échoué :\n{0}",
            ["uninstall.ask"] = "Désinstaller Mimi de ce PC ?",
            ["uninstall.done"] = "Mimi a été désinstallé. À bientôt 💔",
            ["shortcut.desc"] = "Des cœurs pour ta moitié",

            ["menu.send"] = "Envoyer un cœur",
            ["menu.test"] = "Tester l'animation",
            ["menu.settings"] = "Réglages et appairage…",
            ["menu.update"] = "Rechercher une mise à jour",
            ["menu.uninstall"] = "Désinstaller…",
            ["menu.quit"] = "Quitter",
            ["net.problem"] = "Problème réseau",
            ["net.portused"] = "Le port {0} est déjà utilisé ({1})",
            ["your.half"] = "ta moitié",
            ["Your.half"] = "Ta moitié",
            ["status.neterr"] = "Erreur réseau",
            ["status.unpaired"] = "Pas encore appairé",
            ["status.online"] = "Connecté à {0}",
            ["status.waiting"] = "En attente de {0}…",
            ["toast.paired"] = "Appairé avec {0} 💞",
            ["toast.notpaired"] = "Mimi n'est pas encore appairé",
            ["toast.sends"] = "{0} t'envoie des cœurs",
            ["hk.unavailable.title"] = "Raccourci indisponible",
            ["hk.unavailable"] = "{0} est déjà utilisé par une autre appli. Choisis-en un autre dans les réglages.",
            ["hk.taken"] = "Ce raccourci est déjà pris par une autre appli 😕",

            ["upd.title"] = "Mise à jour de Mimi",
            ["upd.uptodate"] = "Tu as la dernière version ({0}) 👍",
            ["upd.failed"] = "Impossible de mettre Mimi à jour : {0}",
            ["upd.done"] = "Mimi est à jour ({0}) 💗",

            ["set.title"] = "Mimi — réglages",
            ["set.subtitle"] = "des cœurs pour ta moitié",
            ["set.name"] = "Ton prénom (ta moitié le verra)",
            ["set.pairing"] = "Appairage",
            ["set.createcode"] = "Créer un code",
            ["set.or"] = "ou",
            ["set.codecue"] = "code",
            ["set.join"] = "Rejoindre",
            ["set.cancel"] = "Annuler",
            ["set.unpair"] = "Dissocier",
            ["set.pair.hint.idle"] = "Un PC crée le code, l'autre le tape.\nLes deux PC doivent être sur la même box / le même Wi-Fi.",
            ["set.pair.hint.host"] = "Tape ce code sur l'autre PC puis « Rejoindre ».\nValable 5 minutes.",
            ["set.pair.searching"] = "Recherche de ta moitié…",
            ["set.pair.paired"] = "💞  Appairé avec {0}",
            ["set.pair.online"] = "●  connecté, prêt à recevoir des cœurs",
            ["set.pair.offline"] = "●  hors ligne pour l'instant (PC éteint ou autre réseau)",
            ["set.unpair.ask"] = "Dissocier Mimi de {0} ?",
            ["set.code6"] = "Le code fait 6 chiffres 🙂",
            ["set.netunavail"] = "Réseau indisponible",
            ["set.pair.notfound"] = "Pas trouvé 😕 Même Wi-Fi ? Le code est encore affiché sur l'autre PC ?",
            ["set.shortcut"] = "Raccourci",
            ["set.shortcut.label"] = "Pour envoyer un cœur (garde appuyé ou spamme 😏)",
            ["set.hk.hint"] = "Clique dans la case puis appuie sur tes touches",
            ["set.hk.press"] = "Appuie sur tes touches…\n(Échap = annuler)",
            ["set.hk.ok"] = "Parfait ! Pense à enregistrer.",
            ["set.hk.needmod"] = "Ajoute Ctrl, Alt ou Maj 🙂",
            ["set.sound"] = "Son et options",
            ["set.sound.on"] = "Son activé",
            ["set.echo"] = "Voir aussi mes cœurs quand j'en envoie",
            ["set.autostart"] = "Lancer Mimi au démarrage de Windows",
            ["set.autoupdate"] = "Mettre Mimi à jour automatiquement",
            ["set.corner"] = "Les cœurs apparaissent",
            ["set.corner.br"] = "en bas à droite",
            ["set.corner.bl"] = "en bas à gauche",
            ["set.corner.bc"] = "en bas au centre",
            ["set.language"] = "Langue",
            ["set.advanced"] = "Avancé",
            ["set.peer"] = "Adresse de ta moitié (remplie par l'appairage)",
            ["set.peer.cue"] = "ex : 192.168.1.42 ou 100.64.0.2",
            ["set.port"] = "Port",
            ["set.myaddr"] = "Mon adresse : {0}",
            ["set.test"] = "Tester l'animation",
            ["set.save"] = "Enregistrer 💗",
            ["set.choosehk"] = "Choisis un raccourci 🙂",
            ["set.saved"] = "Enregistré ✓",

            ["key.shift"] = "Maj", ["key.space"] = "Espace", ["key.enter"] = "Entrée", ["key.numpad"] = "Pavé",
            ["key.scroll"] = "Arrêt défil", ["key.insert"] = "Inser", ["key.home"] = "Début",
            ["key.end"] = "Fin", ["key.pgup"] = "Pg préc", ["key.pgdn"] = "Pg suiv",
        };
    }
}
