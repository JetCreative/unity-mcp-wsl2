using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Requests cancellation for the active Unity test run or a specific run id.
    /// </summary>
    [McpForUnityTool("cancel_test_run")]
    public static class CancelTestRun
    {
        public static object HandleCommand(JObject @params)
        {
            string runId = @params?["runId"]?.ToString();
            var testService = MCPServiceLocator.Tests;

            if (!testService.TryCancelRun(runId, out var error))
            {
                return Response.Error(error ?? "Unable to cancel test run.");
            }

            testService.TryGetStatus(runId, out var status);
            string resolvedId = status?.RunId ?? runId;
            string message = string.IsNullOrEmpty(resolvedId)
                ? "Cancel requested for active test run."
                : $"Cancel requested for run '{resolvedId}'.";

            var payload = status?.ToSerializable() ?? new { runId = resolvedId };
            return Response.Success(message, payload);
        }
    }
}
