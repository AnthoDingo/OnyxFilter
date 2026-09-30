using System;
using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.Updates;

// Mises à jour d'OnyxFilter depuis les publications GitHub : vérification (périodique et à la demande) et
// installation en un clic, suivie d'un redémarrage par systemd. Utilisé par la page « Mises à jour »
// (/settings/updates), la pastille de la barre latérale et l'API (/api/v1/update).
public interface IUpdateService
{
    UpdateStatus Status { get; }

    // Levé à chaque changement d'état, depuis un thread quelconque : les composants Blazor abonnés doivent
    // repasser par InvokeAsync.
    event EventHandler? StatusChanged;

    // Interroge GitHub. Sans "force", réutilise le résultat s'il date de moins de 10 minutes.
    Task<UpdateStatus> CheckAsync(bool force, CancellationToken cancellationToken);

    // Télécharge, vérifie et installe la publication la plus récente, puis arrête l'application pour
    // qu'elle soit relancée sur la nouvelle version. Retourne dès l'arrêt programmé (état Restarting) ou
    // l'échec (état Failed, voir LastError).
    Task<UpdateStatus> InstallAsync(CancellationToken cancellationToken);
}
