# Kairn

> *Kairos*, dieu grec du moment opportun, et *cairn*, le petit tas de pierres qui balise un sentier : des repères pour ta journée, posés au bon moment.

Un planning de journée pour Windows, léger et 100 % local, qui s'ouvre au démarrage du PC, affiche ce que tu as prévu heure par heure et t'aide à ne pas te disperser.

- **Aujourd'hui** : **Mes outils** (tes apps de travail en un clic : dessin, Excel, Canva…), tes **mails** (masqués pendant les blocs de travail, sauf le nombre de non lus), ou simplement un bouton qui ouvre ta messagerie dans le navigateur si tu préfères ne pas donner de mot de passe et la tâche en cours avec un compte à rebours, la suivante et les cases à cocher. La progression prend la forme d'un cairn qui gagne une pierre à chaque tâche accomplie, avec le temps de travail du jour et de la semaine. On voit ce qui est fait, jamais un pourcentage de ce qui manque.
- **Report automatique** : une tâche de travail non terminée passe à ta prochaine session dans « À reprendre ». Le bouton « Reporter la suite » le fait à tout moment, et « Planifier » lui redonne un horaire.
- **Rythme de travail (Pomodoro & co)** : une tâche longue se découpe toute seule en sessions et pauses (Pomodoro 25/5 avec une grande pause toutes les 4 sessions, Petits pas 15/5, 50/10, 90/20 ou perso). Rien à planifier à la main : le rythme se choisit dans la tâche, s'écrit dans le programme collé (`14h - 16h Faire mon CV #pomodoro`, `#50/10`), ou s'applique automatiquement à toutes les tâches longues (Réglages). La carte « En cours » montre la session et le compte à rebours jusqu'à la pause. Un petit bandeau (avec un son léger, désactivable) annonce la pause et la reprise. Le garde se repose pendant les pauses, et seul le temps de travail compte dans ton cumul.
- **Planning** : un calendrier mensuel. Chaque tâche peut avoir des **liens à ouvrir** (Canva, une vidéo YouTube, un fichier, un de tes outils) : un bouton apparaît quand c'est l'heure, avec l'option de les ouvrir automatiquement. Tu ajoutes des tâches sur une plage horaire, ou tu **colles un programme en texte libre** (`9h30 Réviser`, `12h00 - 13h00 : Repas`, sous-puces = notes…) avec un aperçu en direct.
- **Objectifs (assistant)** : « j'aimerais savoir dessiner des personnages, j'ai 2 mois, le soir en semaine ». L'assistant pose 2-3 questions si besoin, fait une feuille de route en phases et détaille les deux prochaines semaines en séances concrètes (consignes pas à pas + tutos). Kairn place ensuite les séances **sans IA** dans tes créneaux vraiment libres (dates toujours justes), tu relis et tu valides. Les ressources vont dans la Bibliothèque (une catégorie par objectif, une sous-catégorie par phase). « Préparer la suite » construit les deux semaines suivantes selon ce que tu as fait et ton ressenti (trop facile / trop dur).
  - **Local (par défaut)** : modèle intégré Qwen3 4B (Apache 2.0) via llama.cpp, téléchargé une seule fois (~2,6 Go) depuis Réglages, sur la carte graphique si possible. Il ne tourne que pendant une demande puis libère la mémoire. Ollama et LM Studio sont aussi pris en charge.
  - **En ligne (facultatif)** : avec ta propre clé d'API (Claude, ou un service compatible OpenAI), programmes plus précis et vraies ressources trouvées sur le web.
- **Bibliothèque** : des catégories (ex. « Dessin ») où ranger liens Pinterest et YouTube, notes, images et fichiers. Les fichiers sont copiés en local. Une tâche liée à une catégorie affiche ses ressources pendant qu'elle est en cours.
- **Garde anti-distraction** (onglet Garde) : un catalogue de ~30 apps courantes (Discord, WhatsApp, Steam, Epic, Riot, Spotify, navigateurs…) où Kairn repère celles installées sur le PC avec leur icône. Un clic suffit pour en surveiller une. Tu peux aussi ajouter n'importe quel programme (fenêtres ouvertes ou fichier .exe), et activer « Jeux en plein écran » pour les jeux lancés depuis un launcher. Trois modes :
  - *Doux* : un bandeau rappelle la tâche en cours si l'app passe au premier plan.
  - *Fermeture* : l'app est fermée au début de chaque bloc de travail.
  - *Strict* : l'app est refermée dès qu'elle se relance pendant un bloc. Il faut attendre 10 s pour quitter ce mode.
  - Les pauses ne sont jamais surveillées. Le menu de la barre des tâches propose « Pause du garde 15 min ».
- **Import / export .ics** (Apple Calendrier, Google Agenda, Outlook).
- **Apparence entièrement modifiable** : 12 thèmes préfaits (Kairn, le noir d'encre par défaut, Pierre & sable, Papier, Néon, Haut contraste…), les 12 couleurs de l'interface avec un sélecteur complet (teinte, saturation, transparence), une **image de fond pour chaque bloc** (fond de l'app, barre latérale, carte « En cours », cartes, lignes de tâches…) avec un voile réglable pour garder le texte lisible, l'opacité de chaque bloc, n'importe quelle police installée, la taille de l'interface, les arrondis et les bordures. Tes thèmes s'enregistrent, et s'exportent en `.kairntheme` (couleurs + images) pour les partager.

## Confidentialité

Aucun compte, aucune télémétrie. Les connexions réseau sont toutes facultatives et déclenchées par toi : ta messagerie (IMAP), directement de ton PC à ton serveur mail, avec un mot de passe chiffré par Windows (DPAPI) ; le téléchargement unique du modèle local (GitHub ggml-org/llama.cpp et Hugging Face, fichiers vérifiés par empreinte SHA-256) ; et le mode en ligne de l'assistant, qui n'envoie que ton objectif et tes réponses au fournisseur choisi, jamais ton calendrier ni tes notes (clé d'API chiffrée par DPAPI). Les données sont dans `%AppData%\Kairn` (`data.json`, `settings.json`, `library\`).
**Mode portable** : crée un dossier `data` à côté de `Kairn.exe` et l'app y rangera tout.

## Installer

Télécharge `Kairn-Setup-x.y.z.exe` dans les [Releases](https://github.com/zakenoo/Kairn/releases) et lance-le : choix de la langue, de l'ambiance (appliquée en direct), de ton prénom et de quelques réglages, puis Kairn s'installe dans `%LocalAppData%\Programs\Kairn`, sans droits administrateur. Tout est prêt au premier lancement. La désinstallation se fait depuis Paramètres Windows → Applications, et tes données sont gardées sauf si tu demandes à les supprimer.

Windows peut afficher « Windows a protégé votre ordinateur » au premier lancement (l'exe n'est pas signé) : clique sur **Informations complémentaires → Exécuter quand même**.

**Mises à jour** : si tu l'as accepté pendant l'installation, Kairn demande une fois par jour à GitHub s'il existe une version plus récente (rien d'autre n'est envoyé) et affiche un bouton « Mettre à jour » : il télécharge le nouveau setup, vérifie son empreinte, remplace Kairn et le relance, en gardant tes données.

## Publier une nouvelle version

```powershell
.\publish.ps1 1.0.1
```

Le script met la version à jour dans `Kairn.csproj` et crée `release\Kairn-Setup-1.0.1.exe`. Ensuite : commit + push, puis sur GitHub **Releases → Draft a new release**, tag `v1.0.1`, joins le fichier et publie. Les Kairn installés le verront tout seuls (le dépôt doit être public pour que la vérification fonctionne).

## Compiler

Prérequis : [SDK .NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet publish Kairn -c Release -o dist
```

Tu obtiens `dist/Kairn.exe`, un seul fichier d'environ 0,5 Mo qui nécessite le runtime .NET 10 Desktop.
Pour un exe autonome qui n'a besoin de rien d'installé (~70 Mo) : ajoute `--self-contained true`.

## Langues

Kairn est traduit en 12 langues : English, 中文（简体）, हिन्दी, Español, Français, العربية (interface en miroir, de droite à gauche), বাংলা, Português, Русский, 日本語, Deutsch, Bahasa Indonesia. Au premier lancement, il prend la langue de Windows. Tu peux la changer dans Réglages, en direct.

Les dates, les jours et les durées suivent la langue choisie. Le collage de programme comprend aussi le format `2pm` / `9:30am` et reconnaît les pauses dans plusieurs langues (« lunch », « pausa », « 休息 »…).

**Contribuer une traduction** : les textes sont dans `Kairn/i18n/*.json`, une clé par texte, avec `fr.json` comme référence. Pour corriger une langue ou en ajouter une sans recompiler, dépose un fichier `xx.json` dans le dossier `lang` des données (Réglages → Ouvrir le dossier des langues). Une nouvelle langue a besoin de `"_name"` (nom affiché), `"_culture"` (ex. `"it-IT"`) et, si elle s'écrit de droite à gauche, `"_rtl": "true"`. Les clés absentes retombent sur l'anglais.

**Captures** : `Kairn.exe --snapshot <dossier> [langue]` enregistre une image de chaque page, puis quitte.
