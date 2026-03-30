namespace EMMA.Domain;

public enum DownloadJobState
{
    Queued = 0,
    Running = 1,
    Paused = 2,
    Completed = 3,
    Failed = 4,
    Canceled = 5
}
