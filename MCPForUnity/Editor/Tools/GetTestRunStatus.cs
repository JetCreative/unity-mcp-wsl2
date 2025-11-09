using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Returns the current status of the active or specified Unity test run.
    /// </summary>
    [McpForUnityTool("get_test_run_status")]
    public static class GetTestRunStatus
    {
        public static object HandleCommand(JObject @params)
        {
            string runId = @params?["runId"]?.ToString();
            var testService = MCPServiceLocator.Tests;

            if (!testService.TryGetStatus(runId, out var status) || status == null)
            {
                string message = string.IsNullOrEmpty(runId)
                    ? "No active or recent test run found."
                    : $"Test run '{runId}' not found.";
                return Response.Error(message);
            }

            var payload = status.ToSerializable();
            string summary = $"Run '{status.RunId}' is {status.State}";
            return Response.Success(summary, payload);
        }
    }
}
