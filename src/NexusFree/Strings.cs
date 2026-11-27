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
        "For any Windows operation that needs Administrator / SYSTEM elevation, use free directly with the command, binary or script suited to the requested operation. This routing depends on the privileges needed, not on a list of command names. No non-elevated attempt or other elevation method first. Check the syntax only when needed, then run it through free. Keep to the requested scope: diagnosis, repair or change. free_status is for diagnosing a failure, not a routine prerequisite. SYSTEM has its own profile: use absolute paths. A wait timeout does not stop the command: read its state and its result with free_result and the request id of the timeout message before running it again. Cancelling a free call stops its command and the processes it started.",
        "Pour toute opération Windows nécessitant une élévation Administrateur / SYSTEM, utiliser directement free avec la commande, le binaire ou le script adapté à l’opération demandée. Ce routage dépend des privilèges nécessaires, pas d’une liste de noms de commandes. Aucun essai non élevé ni autre méthode d’élévation préalable. Vérifier la syntaxe seulement si nécessaire, puis exécuter via free. Respecter la portée demandée : diagnostic, réparation ou modification. free_status sert au diagnostic d’un échec, pas de préalable systématique. SYSTEM a son propre profil : utiliser des chemins absolus. Un délai d’attente dépassé n’arrête pas la commande : lire son état et son résultat avec free_result et l’identifiant de requête du message de délai avant de la relancer. Annuler un appel free arrête sa commande et les processus qu’elle a lancés.");

    internal static string FreeDescription => T(
        "Direct route for any Windows command, binary or script that needs Administrator / SYSTEM elevation, without a UAC prompt. The choice depends on the privileges needed, not on the command's name. Runs the requested operation; no non-elevated attempt and no free_status call are needed first.",
        "Voie directe pour toute commande, tout binaire ou script Windows nécessitant une élévation Administrateur / SYSTEM, sans invite UAC. Le choix dépend des privilèges requis, pas du nom de la commande. Exécute l’opération demandée ; aucun essai non élevé ni free_status préalable n’est nécessaire.");

    internal static string CommandDescription => T(
        "The shell or PowerShell command to run with SYSTEM elevation.",
        "La commande shell ou PowerShell à exécuter avec élévation SYSTEM.");

    internal static string TimeoutDescription => T(
        "Longest wait in seconds (default: 600). When it runs out the command keeps running: free_result reads its result later.",
        "Délai maximal d'attente en secondes (par défaut : 600). Une fois dépassé, la commande continue : free_result relit son résultat plus tard.");

    internal static string ResultDescription => T(
        "Reads by its id the result of a free request whose wait timed out: the exit code and output once the command has ended (kept 24 hours), otherwise its state (still running, waiting, unknown). Runs nothing.",
        "Relit par son identifiant le résultat d'une requête free dont l'attente a expiré : le code de sortie et la sortie une fois la commande terminée (conservés 24 heures), sinon son état (encore en cours, en attente, inconnue). N'exécute rien.");

    internal static string ResultIdDescription => T(
        "The request id given by the timeout message (req-...).",
        "L'identifiant de requête donné par le message de délai dépassé (req-...).");

    internal static string StillRunning(string id, DateTime sinceLocal) => T(
        $"[NexusFree] Request {id} is still running (since {sinceLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}). Read it again later; do not run it a second time.",
        $"[NexusFree] La requête {id} est encore en cours (depuis {sinceLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}). La relire plus tard ; ne pas la relancer.");

    internal static string StillQueued(string id) => T(
        $"[NexusFree] Request {id} is waiting for the SYSTEM task: nothing ran yet.",
        $"[NexusFree] La requête {id} attend la tâche SYSTEM : rien n'a encore été exécuté.");

    internal static string UnknownRequest(string id) => T(
        $"No result and no running command for request {id}: unknown id, a result older than 24 hours (removed), or a SYSTEM task interrupted before it answered.",
        $"Ni résultat ni commande en cours pour la requête {id} : identifiant inconnu, résultat de plus de 24 heures (supprimé), ou tâche SYSTEM interrompue avant de répondre.");

    internal static string InvalidRequestId => T(
        "Not a NexusFree request id (req- followed by 32 hexadecimal characters).",
        "Ce n'est pas un identifiant de requête NexusFree (req- suivi de 32 caractères hexadécimaux).");

    internal static string Stopped => T(
        "Stopped at the caller's request before it ended: the command and the processes it started were ended. What it had already done is not undone.",
        "Arrêtée à la demande de l'appelant avant la fin : la commande et les processus qu'elle avait lancés ont été arrêtés. Ce qu'elle avait déjà fait n'est pas annulé.");

    internal static string StoppedBeforeStart => T(
        "Stopped at the caller's request before it started: nothing ran.",
        "Arrêtée à la demande de l'appelant avant son démarrage : rien n'a été exécuté.");

    internal static string StopUnconfirmed(string id, double seconds) => T(
        $"Stop requested, but the SYSTEM task did not confirm it within {seconds.ToString("0.###", CultureInfo.InvariantCulture)} s: request {id} may still be running. Read its state with free_result.",
        $"Arrêt demandé, mais la tâche SYSTEM ne l'a pas confirmé en {seconds.ToString("0.###", CultureInfo.InvariantCulture)} s : la requête {id} est peut-être encore en cours. Lire son état avec free_result.");

    internal static string StopNotSent(string id, string message) => T(
        $"The stop request for {id} could not be written ({message}): the command may still be running. Read its state with free_result.",
        $"La demande d'arrêt de {id} n'a pas pu être écrite ({message}) : la commande est peut-être encore en cours. Lire son état avec free_result.");

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
        $"Timed out ({seconds.ToString("0.###", CultureInfo.InvariantCulture)} s) without an answer from the SYSTEM task for request {id}. The command was not stopped: read its state and its result with free_result and this id (NexusFree.exe --result {id}); the result is kept 24 hours.{detail}",
        $"Délai dépassé ({seconds.ToString("0.###", CultureInfo.InvariantCulture)} s) sans réponse de la tâche SYSTEM pour la requête {id}. La commande n'a pas été arrêtée : lire son état et son résultat avec free_result et cet identifiant (NexusFree.exe --result {id}) ; le résultat est conservé 24 heures.{detail}");

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
