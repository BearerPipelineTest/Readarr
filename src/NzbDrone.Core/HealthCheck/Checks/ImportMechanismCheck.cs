using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Nzbget;
using NzbDrone.Core.Download.Clients.Sabnzbd;

namespace NzbDrone.Core.HealthCheck.Checks
{
    public class ImportMechanismCheck : HealthCheckBase
    {
        private readonly IConfigService _configService;
        private readonly IProvideDownloadClient _provideDownloadClient;


        public ImportMechanismCheck(IConfigService configService, IProvideDownloadClient provideDownloadClient)
        {
            _configService = configService;
            _provideDownloadClient = provideDownloadClient;
        }

        public override HealthCheck Check()
        {
            List<ImportMechanismCheckStatus> downloadClients;

            try
            {
                downloadClients = _provideDownloadClient.GetDownloadClients().Select(v => new ImportMechanismCheckStatus
                {
                    DownloadClient = v,
                    Status = v.GetStatus()
                }).ToList();
            }
            catch (DownloadClientException)
            {
                // One or more download clients failed, assume the health is okay and verify later
                return new HealthCheck(GetType());
            }

            var downloadClientIsLocalHost = downloadClients.All(v => v.Status.IsLocalhost);

            if (!_configService.IsDefined("EnableCompletedDownloadHandling"))
            {
                // Migration helper logic
                if (!downloadClientIsLocalHost)
                {
                    return new HealthCheck(GetType(), HealthCheckResult.Warning, "Enable Completed Download Handling if possible (Multi-Computer unsupported)", "Migrating-to-Completed-Download-Handling#Unsupported-download-client-on-different-computer");
                }

                if (downloadClients.All(v => v.DownloadClient is Sabnzbd))
                {
                    return new HealthCheck(GetType(), HealthCheckResult.Warning, "Enable Completed Download Handling if possible (Sabnzbd)", "Migrating-to-Completed-Download-Handling#sabnzbd-enable-completed-download-handling");
                }

                if (downloadClients.All(v => v.DownloadClient is Nzbget))
                {
                    return new HealthCheck(GetType(), HealthCheckResult.Warning, "Enable Completed Download Handling if possible (Nzbget)", "Migrating-to-Completed-Download-Handling#nzbget-enable-completed-download-handling");
                }

                return new HealthCheck(GetType(), HealthCheckResult.Warning, "Enable Completed Download Handling if possible", "Migrating-to-Completed-Download-Handling");
            }

            if (!_configService.EnableCompletedDownloadHandling)
            {
                return new HealthCheck(GetType(), HealthCheckResult.Warning, "Enable Completed Download Handling");
            }

            return new HealthCheck(GetType());
        }
    }

    public class ImportMechanismCheckStatus
    {
        public IDownloadClient DownloadClient { get; set; }
        public DownloadClientStatus Status { get; set; }
    }
}
