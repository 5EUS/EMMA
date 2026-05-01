namespace EMMA.Plugin.Common;

public static class EmmaTelemetry
{
    public const string PluginHostMeterName = "emma.plugin.host";
    public const string PluginSdkMeterName = "emma.plugin.sdk";

    public static class Tags
    {
        public const string PluginId = "plugin.id";
        public const string Service = "service";
        public const string Method = "method";
        public const string Operation = "operation";
        public const string Outcome = "outcome";
        public const string Reason = "reason";
    }

    public static class Outcomes
    {
        public const string Ok = "ok";
        public const string Error = "error";
        public const string Cancelled = "cancelled";
    }
}