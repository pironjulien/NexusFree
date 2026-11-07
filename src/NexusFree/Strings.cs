using System.Globalization;

namespace NexusFree;

/// <summary>
/// Every text NexusFree shows: English by default, French when the Windows display language is French. Read on each
/// call from <see cref="CultureInfo.CurrentUICulture"/>, so a test can switch language for its own async flow.
/// </summary>
internal static class Strings
{
    internal static bool French => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr";

    private static string T(string english, string french) => French ? french : english;

    internal static string Instructions => T(
        "For any Windows operation that needs Administrator / SYSTEM elevation, use free directly with the command, binary or script suited to the requested operation. This routing depends on the privileges needed, not on a list of command names. No non-elevated attempt or other elevation method first. Check the syntax only when needed, then run it through free. Keep to the requested scope: diagnosis, repair or change. free_status is for diagnosing a failure, not a routine prerequisite. SYSTEM has its own profile: use absolute paths. A wait timeout does not necessarily stop the command: check its state before running it again.",
        "Pour toute opération Windows nécessitant une élévation Administrateur / SYSTEM, utiliser directement free avec la commande, le binaire ou le script adapté à l’opération demandée. Ce routage dépend des privilèges nécessaires, pas d’une liste de noms de commandes. Aucun essai non élevé ni autre méthode d’élévation préalable. Vérifier la syntaxe seulement si nécessaire, puis exécuter via free. Respecter la portée demandée : diagnostic, réparation ou modification. free_status sert au diagnostic d’un échec, pas de préalable systématique. SYSTEM a son propre profil : utiliser des chemins absolus. Un timeout d’attente n’arrête pas nécessairement la commande : vérifier son état avant de relancer.");

    internal static string FreeDescription => T(
        "Direct route for any Windows command, binary or script that needs Administrator / SYSTEM elevation, without a UAC prompt. The choice depends on the privileges needed, not on the command's name. Runs the requested operation; no non-elevated attempt and no free_status call are needed first.",
        "Voie directe pour toute commande, tout binaire ou script Windows nécessitant une élévation Administrateur / SYSTEM, sans invite UAC. Le choix dépend des privilèges requis, pas du nom de la commande. Exécute l’opération demandée ; aucun essai non élevé ni free_status préalable n’est nécessaire.");

    internal static string CommandDescription => T(
        "The shell or PowerShell command to run with SYSTEM elevation.",
        "La commande shell ou PowerShell à exécuter avec élévation SYSTEM.");

    internal static string TimeoutDescription => T(
        "Longest wait in seconds (default: 600).",
        "Délai maximal d'attente en secondes (par défaut : 600).");

    internal static string StatusDescription => T(
        "Checks that the NexusFree elevation bridge works (SYSTEM task, spool, communication).",
        "Vérifie l'état opérationnel du pont d'élévation NexusFree (tâche SYSTEM, sas, communication).");

    internal static string ExitCodeLine(int code) => T($"[NexusFree] Exit code: {code}", $"[NexusFree] Code de sortie : {code}");

    internal static string ErrorLine(string error) => T($"[Error] {error}", $"[Erreur] {error}");

    internal static string UnknownTool(string name) => T($"Unknown tool: {name}", $"Outil inconnu : {name}");

    internal static string ToolFailed(string name, string message) => T(
        $"Tool {name} failed: {message}",
        $"Erreur d'exécution de l'outil {name} : {message}");

    internal static string UnsupportedMethod(string method) => T($"Unsupported method: {method}", $"Méthode non supportée : {method}");

    internal static string EmptyCommand => T("The command to run is empty.", "La commande à exécuter est vide.");

    internal static string SpoolMissing(string inbox) => T(
        $"NexusFree spool not found ({inbox}). Run install.ps1 -Apply once to register the SYSTEM task.",
        $"Sas NexusFree introuvable ({inbox}). Lancez install.ps1 -Apply une fois pour enregistrer la tâche SYSTEM.");

    internal static string TriggerFailed(string task, string message) => T(
        $"Could not start the task '{task}': {message}",
        $"Échec du déclenchement de la tâche '{task}' : {message}");

    internal static string Cancelled => T("Operation cancelled.", "Opération annulée.");

    internal static string NotAnObject => T("JSON answer that is not an object", "réponse JSON qui n'est pas un objet");

    internal static string Unreadable(string file, string problem) => T(
        $" The answer {file} stayed unreadable ({problem}); it is kept for diagnosis.",
        $" La réponse {file} est restée illisible ({problem}) ; elle est conservée pour diagnostic.");

    internal static string TimedOut(double seconds, string id, string detail) => T(
        $"Timed out ({seconds.ToString("0.###", CultureInfo.InvariantCulture)} s) without an answer from the SYSTEM task for request {id}.{detail}",
        $"Délai dépassé ({seconds.ToString("0.###", CultureInfo.InvariantCulture)} s) sans réponse de la tâche SYSTEM pour la requête {id}.{detail}");

    internal static string TaskStateName(int state) => state switch
    {
        1 => T("Disabled", "Désactivée"),
        2 => T("Queued", "En file d'attente"),
        3 => T("Ready", "Prête"),
        4 => T("Running", "En cours"),
        _ => T("Unknown", "Inconnue"),
    };

    internal static string TaskMissing => T("Not installed", "Non installée");

    internal static string TaskError(string message) => T($"Error: {message}", $"Erreur : {message}");

    internal static string RequestRefused(string owner) => T(
        $"Request refused: its file belongs to {owner}, not to the authorized account.",
        $"Requête refusée : son fichier appartient à {owner}, pas au compte autorisé.");

    internal static string RequestWithoutCommand => T(
        "Request without a command ('command' missing or empty).",
        "Requête sans commande (champ 'command' manquant ou vide).");

    internal static string RequestUnreadable(string message) => T(
        $"Unreadable request: {message}",
        $"Requête illisible : {message}");

    internal static string ShellNotStarted(string message) => T(
        $"PowerShell could not be started: {message}",
        $"Impossible de démarrer PowerShell : {message}");
}
