using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Reruns the failed tests from a previous Unity test run.
    /// When no run id is provided, the most recent completed run is used.
    /// </summary>
    [McpForUnityTool("rerun_failed_tests")]
    public static class RerunFailedTests
    {
        private const int DefaultTimeoutSeconds = 600;

        public static async Task<object> HandleCommand(JObject @params)
        {
            string runId = @params?["runId"]?.ToString();
            bool waitForCompletion = ParseBool(@params?["waitForCompletion"], defaultValue: true);
            int timeoutSeconds = ParseTimeout(@params, DefaultTimeoutSeconds);

            var testService = MCPServiceLocator.Tests;
            IReadOnlyList<TestRunTestResult> failed = testService.GetFailedTests(runId);
            if (failed == null || failed.Count == 0)
            {
                string message = string.IsNullOrEmpty(runId)
                    ? "No failed tests available from the most recent completed run."
                    : $"No failed tests recorded for run '{runId}'.";
                return Response.Error(message);
            }

            if (!testService.TryGetStatus(runId, out var baseStatus) || baseStatus == null)
            {
                string message = string.IsNullOrEmpty(runId)
                    ? "Unable to locate the last completed run to rerun."
                    : $"Test run '{runId}' not found.";
                return Response.Error(message);
            }

            var failedNames = failed
                .Select(f => f.FullName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (failedNames.Count == 0)
            {
                return Response.Error("Failed tests do not contain valid identifiers to rerun.");
            }

            var request = new TestRunRequest(baseStatus.Mode, failedNames);

            TestRunHandle handle;
            try
            {
                handle = testService.StartRun(request);
            }
            catch (Exception ex)
            {
                return Response.Error($"Failed to start rerun: {ex.Message}");
            }

            if (!waitForCompletion)
            {
                testService.TryGetStatus(handle.RunId, out var snapshot);
                var payload = snapshot?.ToSerializable() ?? new { runId = handle.RunId, state = TestRunState.Queued.ToString() };
                string message = handle.StartedNewRun
                    ? $"Started rerun '{handle.RunId}' for failed tests."
                    : $"Reusing active run '{handle.RunId}'.";
                return Response.Success(message, payload);
            }

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
            var completed = await Task.WhenAny(handle.CompletionTask, timeoutTask).ConfigureAwait(true);
            if (completed != handle.CompletionTask)
            {
                testService.TryGetStatus(handle.RunId, out var snapshot);
                var data = snapshot?.ToSerializable() ?? new { runId = handle.RunId, state = TestRunState.Running.ToString() };
                return Response.Error($"Failed-test rerun '{handle.RunId}' is still running after {timeoutSeconds} seconds.", data);
            }

            var result = await handle.CompletionTask.ConfigureAwait(true);
            string summary =
                $"Rerun '{result.RunId}' finished with state {result.Summary.ResultState}: {result.Passed}/{result.Total} passed, {result.Failed} failed, {result.Skipped} skipped.";
            return Response.Success(summary, result.ToSerializable());
        }

        private static int ParseTimeout(JObject @params, int fallback)
        {
            try
            {
                var timeoutToken = @params?["timeoutSeconds"];
                if (timeoutToken != null && int.TryParse(timeoutToken.ToString(), out var parsed) && parsed > 0)
                {
                    return parsed;
                }
            }
            catch
            {
                // Ignore and use fallback
            }

            return fallback;
        }

        private static bool ParseBool(JToken token, bool defaultValue)
        {
            if (token == null)
            {
                return defaultValue;
            }

            if (bool.TryParse(token.ToString(), out var parsed))
            {
                return parsed;
            }

            return defaultValue;
        }
    }
}
