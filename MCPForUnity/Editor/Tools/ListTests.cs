using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Resources.Tests;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Lists available Unity tests exposed through the Test Runner API.
    /// </summary>
    [McpForUnityTool("list_tests")]
    public static class ListTests
    {
        public static async Task<object> HandleCommand(JObject @params)
        {
            string modeStr = @params?["mode"]?.ToString();
            TestMode? mode = null;
            if (!string.IsNullOrWhiteSpace(modeStr))
            {
                if (!ModeParser.TryParse(modeStr, out var parsedMode, out var parseError))
                {
                    return Response.Error(parseError);
                }

                mode = parsedMode.Value;
            }

            IReadOnlyList<Dictionary<string, string>> tests;
            try
            {
                tests = await MCPServiceLocator.Tests.GetTestsAsync(mode).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                return Response.Error($"Failed to retrieve tests: {ex.Message}");
            }

            string scope = mode.HasValue ? mode.Value.ToString() : "EditMode + PlayMode";
            string message = $"Retrieved {tests.Count} {scope} tests";
            return Response.Success(message, tests);
        }
    }
}
