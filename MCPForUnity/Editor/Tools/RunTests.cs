using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Resources.Tests;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Executes Unity tests for a specified mode and returns detailed results.
    /// Supports filtered runs (testNames, groupNames, categoryNames, assemblyNames), optional waiting, and re-attachment to in-flight runs.
    /// </summary>
    [McpForUnityTool("run_tests")]
    public static class RunTests
    {
        private const int DefaultTimeoutSeconds = 600; // 10 minutes

        public static async Task<object> HandleCommand(JObject @params)
        {
            string modeStr = @params?["mode"]?.ToString();
            if (string.IsNullOrWhiteSpace(modeStr))
            {
                modeStr = "EditMode";
            }

            if (!ModeParser.TryParse(modeStr, out var parsedMode, out var parseError))
            {
                return Response.Error(parseError);
            }

            bool waitForCompletion = ParseBool(@params?["waitForCompletion"], defaultValue: true);
            int timeoutSeconds = ParseTimeout(@params, DefaultTimeoutSeconds);

            var testNames = ParseStringList(@params?["testNames"] ?? @params?["tests"]);
            var groupNames = ParseStringList(@params?["groupNames"]);
            var categoryNames = ParseStringList(@params?["categoryNames"] ?? @params?["categories"]);
            var assemblyNames = ParseStringList(@params?["assemblyNames"] ?? @params?["assemblies"]);

            var request = new TestRunRequest(
                parsedMode.Value,
                testNames,
                groupNames,
                categoryNames,
                assemblyNames);

            var testService = MCPServiceLocator.Tests;
            TestRunHandle handle;
            try
            {
                handle = testService.StartRun(request);
            }
            catch (Exception ex)
            {
                return Response.Error($"Failed to start test run: {ex.Message}");
            }

            if (!waitForCompletion)
            {
                testService.TryGetStatus(handle.RunId, out var snapshot);
                var message = handle.StartedNewRun
                    ? $"Started test run '{handle.RunId}'"
                    : $"Run '{handle.RunId}' already in progress; returning current status.";
                var payload = snapshot?.ToSerializable() ?? new
                {
                    runId = handle.RunId,
                    state = handle.StartedNewRun ? TestRunState.Queued.ToString() : TestRunState.Running.ToString(),
                };

                return Response.Success(message, payload);
            }

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
            var completed = await Task.WhenAny(handle.CompletionTask, timeoutTask).ConfigureAwait(true);

            if (completed != handle.CompletionTask)
            {
                testService.TryGetStatus(handle.RunId, out var snapshot);
                var data = snapshot?.ToSerializable() ?? new { runId = handle.RunId, state = TestRunState.Running.ToString() };
                return Response.Error($"Test run '{handle.RunId}' is still running after {timeoutSeconds} seconds.", data);
            }

            var result = await handle.CompletionTask.ConfigureAwait(true);
            string summaryMessage =
                $"Run '{result.RunId}' finished with state {result.Summary.ResultState}: {result.Passed}/{result.Total} passed, {result.Failed} failed, {result.Skipped} skipped.";

            var responseData = result.ToSerializable();
            return Response.Success(summaryMessage, responseData);
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
                // Ignore parse issues and use fallback
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

        private static IReadOnlyList<string> ParseStringList(JToken token)
        {
            if (token == null)
            {
                return Array.Empty<string>();
            }

            if (token.Type == JTokenType.String)
            {
                var value = token.ToString();
                return string.IsNullOrWhiteSpace(value)
                    ? Array.Empty<string>()
                    : new[] { value };
            }

            if (token is JArray array)
            {
                return array
                    .Where(t => t.Type == JTokenType.String)
                    .Select(t => t.ToString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }

            return Array.Empty<string>();
        }
    }
}
