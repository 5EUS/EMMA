namespace EMMA.Domain;

/// <summary>The lifecycle state of a download job.</summary>
public enum DownloadState
{
    /// <summary>Waiting to start.</summary>
    Queued = 0,

    /// <summary>In progress.</summary>
    Running = 1,

    /// <summary>Paused by the user.</summary>
    Paused = 2,

    /// <summary>Finished successfully.</summary>
    Completed = 3,

    /// <summary>Stopped by an error.</summary>
    Failed = 4,

    /// <summary>Cancelled by the user.</summary>
    Canceled = 5,
}
