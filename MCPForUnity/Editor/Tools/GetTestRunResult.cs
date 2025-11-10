using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Returns the full serialized results for a completed Unity test run.
    /// </summary>
    [McpForUnityTool("get_test_run_result")]
    public static class GetTestRunResult
    {
        public static object HandleCommand(JObject @params)
        {
            string runId = @params?["runId"]?.ToString();
            var testService = MCPServiceLocator.Tests;

            if (!testService.TryGetResult(runId, out var result) || result == null)
            {
                string message = string.IsNullOrEmpty(runId)
                    ? "No completed test run with available results."
                    : $"Results not found for test run '{runId}'.";
                return Response.Error(message);
            }

            return Response.Success($"Results for run '{result.RunId}'", result.ToSerializable());
        }
    }
}
