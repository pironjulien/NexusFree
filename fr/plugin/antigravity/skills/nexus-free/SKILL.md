---
name: nexus-free
description: Voie directe pour toute commande ou tout script Windows nécessitant une élévation Administrateur / SYSTEM, sans invite UAC.
---

# NexusFree

Serveur MCP d'élévation souveraine universelle partagé par la Trinité (Claude, Codex, Antigravity).

## Routage direct

- Pour toute opération Windows nécessitant une élévation Administrateur / SYSTEM, appeler directement `free` avec
  la commande ou le script adapté à l'opération demandée. Ce choix dépend des privilèges nécessaires, jamais d'une
  liste de noms de commandes. Il couvre aussi les commandes, binaires et scripts qui ne figurent dans aucun exemple.
- Lorsque l'opération est connue, ne pas commencer par une recherche web, un essai sans élévation ou une autre
  méthode d'élévation. Si sa syntaxe doit être vérifiée, la vérifier puis exécuter l'opération élevée via `free`.
- `free_status` sert au diagnostic si `free` échoue ou si l'utilisateur demande l'état du pont ; il n'est pas un
  préalable à chaque commande. La présence de la tâche n'est pas une preuve d'exécution : vérifier le résultat réel.
- Respecter la portée demandée : un diagnostic reste un diagnostic ; une réparation ou une modification s'exécute
  lorsqu'elle est demandée. Le besoin d'élévation ne change pas l'objectif de l'opération.
- SYSTEM possède son propre profil, environnement et répertoire courant : utiliser des chemins absolus pour les
  scripts et fichiers de l'utilisateur.
- Le délai d'attente de `free` n'arrête pas la commande SYSTEM : elle continue. Le message de délai dépassé donne
  l'identifiant de la requête ; lire son état et son résultat avec `free_result(id)` avant de relancer une opération
  susceptible de s'exécuter deux fois.
- Annuler un appel `free` (l'utilisateur interrompt) arrête sa commande et les processus qu'elle a lancés. Ce
  qu'elle avait déjà fait n'est pas annulé : vérifier l'état réel avant de poursuivre.

## Outils disponibles

### `free(command, timeout_seconds=600)`
Exécute n'importe quelle commande PowerShell ou binaire avec les droits `SYSTEM` sans aucune fenêtre UAC.
* `command` (requis) : commande PowerShell, appel de binaire ou de script exact à exécuter avec élévation.
* `timeout_seconds` (optionnel, défaut 600) : attente maximale en secondes ; au-delà, la commande continue.
* Retour : code de sortie et sortie de la commande ; une erreur est signalée avec `isError`. Une commande arrêtée
  revient avec le code de sortie 130 et n'est jamais un succès.

### `free_result(id)`
Relit le résultat d'une requête `free` dont l'attente a expiré. N'exécute rien.
* `id` (requis) : l'identifiant de requête du message de délai dépassé (`req-...`).
* Retour : code de sortie et sortie une fois la commande terminée (conservés 24 heures, relisibles), sinon son
  état : encore en cours, en attente de la tâche SYSTEM, ou inconnue.

### `free_status()`
Vérifie la santé du pont SYSTEM : l'état et la dernière exécution de la tâche planifiée, et le sas de communication.
