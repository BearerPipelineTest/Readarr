using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Queue;
using NzbDrone.SignalR;
using Readarr.Http;
using Readarr.Http.REST;

namespace Readarr.Api.V1.Queue
{
    [V1ApiController("queue/status")]
    public class QueueStatusController : RestControllerWithSignalR<QueueStatusResource, NzbDrone.Core.Queue.Queue>,
                               IHandle<QueueUpdatedEvent>, IHandle<PendingReleasesUpdatedEvent>
    {
        private readonly IQueueService _queueService;
        private readonly IPendingReleaseService _pendingReleaseService;
        private readonly Debouncer _broadcastDebounce;

        public QueueStatusController(IBroadcastSignalRMessage broadcastSignalRMessage, IQueueService queueService, IPendingReleaseService pendingReleaseService)
            : base(broadcastSignalRMessage)
        {
            _queueService = queueService;
            _pendingReleaseService = pendingReleaseService;

            _broadcastDebounce = new Debouncer(BroadcastChange, TimeSpan.FromSeconds(5));
        }

        protected override QueueStatusResource GetResourceById(int id)
        {
            throw new NotImplementedException();
        }

        [HttpGet]
        public QueueStatusResource GetQueueStatus()
        {
            _broadcastDebounce.Pause();

            var queue = _queueService.GetQueue();
            var pending = _pendingReleaseService.GetPendingQueue();

            var resource = new QueueStatusResource
            {
                TotalCount = queue.Count + pending.Count,
                Count = queue.Count(q => q.Author != null) + pending.Count,
                UnknownCount = queue.Count(q => q.Author == null),
                Errors = queue.Any(q => q.Author != null && q.TrackedDownloadStatus == TrackedDownloadStatus.Error),
                Warnings = queue.Any(q => q.Author != null && q.TrackedDownloadStatus == TrackedDownloadStatus.Warning),
                UnknownErrors = queue.Any(q => q.Author == null && q.TrackedDownloadStatus == TrackedDownloadStatus.Error),
                UnknownWarnings = queue.Any(q => q.Author == null && q.TrackedDownloadStatus == TrackedDownloadStatus.Warning)
            };

            _broadcastDebounce.Resume();

            return resource;
        }

        private void BroadcastChange()
        {
            BroadcastResourceChange(ModelAction.Updated, GetQueueStatus());
        }

        [NonAction]
        public void Handle(QueueUpdatedEvent message)
        {
            _broadcastDebounce.Execute();
        }

        [NonAction]
        public void Handle(PendingReleasesUpdatedEvent message)
        {
            _broadcastDebounce.Execute();
        }
    }
}
