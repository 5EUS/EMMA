namespace EMMA.Plugin.Common;

/// <summary>
/// Defines telemetry meter names and tag keys shared by the plugin SDK and host.
/// </summary>
public static class EmmaTelemetry
{
    /// <summary>
    /// The meter name used by the plugin host.
    /// </summary>
    public const string PluginHostMeterName = "emma.plugin.host";

    /// <summary>
    /// The meter name used by the plugin SDK.
    /// </summary>
    public const string PluginSdkMeterName = "emma.plugin.sdk";

    /// <summary>
    /// Contains standard telemetry tag names.
    /// </summary>
    public static class Tags
    {
        /// <summary>
        /// The plugin identifier tag.
        /// </summary>
        public const string PluginId = "plugin.id";

        /// <summary>
        /// The logical service name tag.
        /// </summary>
        public const string Service = "service";

        /// <summary>
        /// The method name tag.
        /// </summary>
        public const string Method = "method";

        /// <summary>
        /// The operation name tag.
        /// </summary>
        public const string Operation = "operation";

        /// <summary>
        /// The outcome tag.
        /// </summary>
        public const string Outcome = "outcome";

        /// <summary>
        /// The failure-reason tag.
        /// </summary>
        public const string Reason = "reason";
    }

    /// <summary>
    /// Contains standard telemetry outcome labels.
    /// </summary>
    public static class Outcomes
    {
        /// <summary>
        /// Indicates successful completion.
        /// </summary>
        public const string Ok = "ok";

        /// <summary>
        /// Indicates failed completion.
        /// </summary>
        public const string Error = "error";

        /// <summary>
        /// Indicates cancellation.
        /// </summary>
        public const string Cancelled = "cancelled";
    }
}