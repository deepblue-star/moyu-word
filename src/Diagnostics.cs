using System;
using System.IO;
using System.Web.Script.Serialization;

namespace MoyuWord
{
    // Opt-in local verification only. No default log, no sockets, no telemetry.
    internal static class Diagnostics
    {
        internal static string Path;
        internal static void Record(string kind, object state)
        {
            if (String.IsNullOrWhiteSpace(Path)) return;
            try
            {
                File.AppendAllText(Path, new JavaScriptSerializer().Serialize(new { Time = DateTime.UtcNow.ToString("o"), Event = kind, State = state }) + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
