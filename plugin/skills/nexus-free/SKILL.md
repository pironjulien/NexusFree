---
name: nexus-free
description: Exécuter toute commande ou script Windows nécessitant les privilèges Administrateur / SYSTEM sans invite UAC (sfc, dism, services, registre machine HKLM, drivers, installations).
---

# NexusFree (v1.0.0)

Serveur MCP d'élévation souveraine universelle partagé par la Trinité (Claude, Codex, Antigravity).

## Outils disponibles

### `free(command, timeout_seconds=600)`
Exécute n'importe quelle commande PowerShell ou binaire avec les droits `SYSTEM` sans aucune fenêtre UAC.
* `command` (requis) : chaîne de commande exacte à exécuter (ex. `sfc /scannow`, `Restart-Service wuauserv`, `Get-Process`).
* `timeout_seconds` (optionnel, défaut 600) : délai limite en secondes.
* Retour : code de sortie, `stdout`, `stderr`, durée d'exécution.

### `free_status()`
Vérifie la santé du pont SYSTEM, l'état de la tâche planifiée et du sas de communication.
