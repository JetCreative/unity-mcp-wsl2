using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Provides access to Unity Test Runner data, scheduling, and status tracking.
    /// </summary>
    public interface ITestRunnerService
    {
        /// <summary>
        /// Retrieve the list of tests for the requested mode(s).
        /// When <paramref name="mode"/> is null, tests for both EditMode and PlayMode are returned.
        /// </summary>
        Task<IReadOnlyList<Dictionary<string, string>>> GetTestsAsync(TestMode? mode);

        /// <summary>
        /// Schedule a Unity test run using the supplied request payload.
        /// Returns a handle that can be awaited for completion or polled via status APIs.
        /// </summary>
        TestRunHandle StartRun(TestRunRequest request);

        /// <summary>
        /// Retrieve the latest status snapshot for the supplied run id.
        /// If <paramref name="runId"/> is null, the active run (or most recent completed run) is used.
        /// </summary>
        bool TryGetStatus(string runId, out TestRunStatusSnapshot status);

        /// <summary>
        /// Attempt to cancel the specified run (or the active run when runId is null).
        /// Returns true when Unity accepts the cancel request, false otherwise.
        /// </summary>
        bool TryCancelRun(string runId, out string error);

        /// <summary>
        /// Retrieve failed tests for the specified run id (or the latest completed run when null).
        /// </summary>
        IReadOnlyList<TestRunTestResult> GetFailedTests(string runId = null);

        /// <summary>
        /// Retrieve the cached results for a completed run.
        /// </summary>
        bool TryGetResult(string runId, out TestRunResult result);
    }
}
