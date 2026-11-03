---
name: nexus-free
description: Exécuter toute commande ou script Windows nécessitant les privilèges Administrateur / SYSTEM sans invite UAC (sfc, dism, services, registre machine HKLM, drivers, installations).
---

# NexusFree

Serveur MCP d'élévation souveraine universelle partagé par la Trinité (Claude, Codex, Antigravity).

## Routage direct

- Pour une demande DISM (même écrit « dsim »), SFC ou toute opération Windows nécessitant Administrateur / SYSTEM,
  appeler directement `free` avec la commande demandée. Ne pas commencer par une recherche web, un essai sans
  élévation ou une autre méthode d'élévation lorsque la commande est connue.
- `free_status` sert au diagnostic si `free` échoue ou si l'utilisateur demande l'état du pont ; il n'est pas un
  préalable à chaque commande. La présence de la tâche n'est pas une preuve d'exécution : vérifier le résultat réel.
- Respecter l'opération demandée : `DISM /Online /Cleanup-Image /CheckHealth` vérifie l'état ;
  `DISM /Online /Cleanup-Image /RestoreHealth` répare. Ne pas substituer l'une à l'autre.
- SYSTEM possède son propre profil, environnement et répertoire courant : utiliser des chemins absolus pour les
  scripts et fichiers de l'utilisateur.
- Le délai d'attente de `free` n'arrête pas nécessairement la commande SYSTEM. Après un timeout ou une déconnexion,
  vérifier l'état réel et la requête avant de relancer une opération susceptible de s'exécuter deux fois.

## Outils disponibles

### `free(command, timeout_seconds=600)`
Exécute n'importe quelle commande PowerShell ou binaire avec les droits `SYSTEM` sans aucune fenêtre UAC.
* `command` (requis) : chaîne de commande exacte à exécuter (ex. `sfc /scannow`, `Restart-Service wuauserv`, `Get-Process`).
* `timeout_seconds` (optionnel, défaut 600) : délai limite en secondes.
* Retour : code de sortie et sortie de la commande ; une erreur est signalée avec `isError`.

### `free_status()`
Vérifie la santé du pont SYSTEM, l'état de la tâche planifiée et du sas de communication.
