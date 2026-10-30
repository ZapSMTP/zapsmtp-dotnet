using System;

namespace ZapSMTP.Internal
{
    internal static class SdkInfo
    {
        /// <summary>The package version; a test keeps it equal to the assembly's.</summary>
        public const string Version = "0.1.0";

        /// <summary>Sent with every request: zapsmtp-dotnet/&lt;version&gt; (dotnet/&lt;runtime version&gt;).</summary>
        public static readonly string UserAgent = $"zapsmtp-dotnet/{Version} (dotnet/{Environment.Version})";
    }
}
