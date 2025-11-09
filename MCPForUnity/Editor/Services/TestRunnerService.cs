using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Concrete implementation of <see cref="ITestRunnerService"/>.
    /// Coordinates Unity Test Runner operations and produces structured results.
    /// </summary>
    internal sealed class TestRunnerService : ITestRunnerService, ICallbacks, IDisposable
    {
        private static readonly TestMode[] AllModes = { TestMode.EditMode, TestMode.PlayMode };

        private readonly TestRunnerApi _testRunnerApi;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private readonly List<ITestResultAdaptor> _leafResults = new List<ITestResultAdaptor>();
        private readonly object _stateLock = new object();
        private readonly Dictionary<string, ManagedTestRun> _runHistory = new Dictionary<string, ManagedTestRun>(StringComparer.Ordinal);
        private readonly Queue<string> _runOrder = new Queue<string>();
        private TaskCompletionSource<TestRunResult> _runCompletionSource;
        private ManagedTestRun _activeRun;
        private ManagedTestRun _lastCompletedRun;

        private const int RunHistoryLimit = 10;

        public TestRunnerService()
        {
            _testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            _testRunnerApi.RegisterCallbacks(this);
        }

        public async Task<IReadOnlyList<Dictionary<string, string>>> GetTestsAsync(TestMode? mode)
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var modes = mode.HasValue ? new[] { mode.Value } : AllModes;

                var results = new List<Dictionary<string, string>>();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var m in modes)
                {
                    var root = await RetrieveTestRootAsync(m).ConfigureAwait(true);
                    if (root != null)
                    {
                        CollectFromNode(root, m, results, seen, new List<string>());
                    }
                }

                return results;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public TestRunHandle StartRun(TestRunRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            _operationLock.Wait();
            try
            {
                if (_runCompletionSource != null && !_runCompletionSource.Task.IsCompleted)
                {
                    ManagedTestRun existing;
                    lock (_stateLock)
                    {
                        existing = _activeRun;
                    }

                    if (existing == null || string.IsNullOrEmpty(existing.RunId))
                    {
                        throw new InvalidOperationException("A Unity test run is already in progress, but its identifier is unavailable.");
                    }

                    return new TestRunHandle(existing.RunId, false, _runCompletionSource.Task);
                }

                if (EditorApplication.isCompiling)
                {
                    throw new InvalidOperationException("Unity is still compiling scripts. Wait for compilation to finish before running tests.");
                }

                _leafResults.Clear();
                _runCompletionSource = new TaskCompletionSource<TestRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);

                var filter = BuildFilter(request);
                var executionSettings = new ExecutionSettings(filter)
                {
                    filters = new[] { filter }
                };

                try
                {
                    string runGuid = _testRunnerApi.Execute(executionSettings);
                    var managedRun = new ManagedTestRun(runGuid, request);

                    lock (_stateLock)
                    {
                        _activeRun = managedRun;
                        _runHistory[runGuid] = managedRun;
                        _runOrder.Enqueue(runGuid);
                        TrimHistory();
                    }

                    return new TestRunHandle(runGuid, true, _runCompletionSource.Task);
                }
                catch
                {
                    _runCompletionSource = null;
                    throw;
                }
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public bool TryGetStatus(string runId, out TestRunStatusSnapshot status)
        {
            var run = ResolveRun(runId, string.IsNullOrEmpty(runId));
            if (run == null)
            {
                status = null;
                return false;
            }

            status = CreateSnapshot(run);
            return true;
        }

        public bool TryCancelRun(string runId, out string error)
        {
            error = null;
            var run = ResolveRun(runId, string.IsNullOrEmpty(runId));
            if (run == null)
            {
                error = string.IsNullOrEmpty(runId)
                    ? "No active test run to cancel."
                    : $"Test run '{runId}' not found.";
                return false;
            }

            if (run.State == TestRunState.Completed || run.State == TestRunState.Failed || run.State == TestRunState.Canceled)
            {
                error = $"Test run '{run.RunId}' already finished.";
                return false;
            }

            bool accepted = TestRunnerApi.CancelTestRun(run.RunId);
            if (!accepted)
            {
                error = $"Unity rejected cancel request for run '{run.RunId}'.";
                return false;
            }

            lock (_stateLock)
            {
                run.State = TestRunState.Cancelling;
            }

            return true;
        }

        public IReadOnlyList<TestRunTestResult> GetFailedTests(string runId = null)
        {
            var run = ResolveRun(runId, string.IsNullOrEmpty(runId));
            if (run?.Result == null)
            {
                return Array.Empty<TestRunTestResult>();
            }

            return run.Result.Results
                .Where(r => string.Equals(r.State, "Failed", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public bool TryGetResult(string runId, out TestRunResult result)
        {
            var run = ResolveRun(runId, string.IsNullOrEmpty(runId));
            if (run?.Result == null)
            {
                result = null;
                return false;
            }

            result = run.Result;
            return true;
        }

        public void Dispose()
        {
            try
            {
                _testRunnerApi?.UnregisterCallbacks(this);
            }
            catch
            {
                // Ignore cleanup errors
            }

            if (_testRunnerApi != null)
            {
                ScriptableObject.DestroyImmediate(_testRunnerApi);
            }

            _operationLock.Dispose();
        }

        #region TestRunnerApi callbacks

        public void RunStarted(ITestAdaptor testsToRun)
        {
            _leafResults.Clear();
            lock (_stateLock)
            {
                if (_activeRun != null)
                {
                    _activeRun.State = TestRunState.Running;
                    _activeRun.StartTimeUtc = DateTime.UtcNow;
                }
            }
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            if (_runCompletionSource == null)
            {
                return;
            }

            ManagedTestRun run;
            lock (_stateLock)
            {
                run = _activeRun;
            }

            var payload = TestRunResult.Create(run?.RunId ?? string.Empty, run?.Request.Mode ?? TestMode.EditMode, result, _leafResults);
            _runCompletionSource.TrySetResult(payload);
            _runCompletionSource = null;

            if (run != null)
            {
                run.Result = payload;
                run.EndTimeUtc = DateTime.UtcNow;
                run.State = MapState(payload);

                lock (_stateLock)
                {
                    _lastCompletedRun = run;
                    _activeRun = null;
                }
            }
        }

        public void TestStarted(ITestAdaptor test)
        {
            // No-op
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (result == null)
            {
                return;
            }

            if (!result.HasChildren)
            {
                _leafResults.Add(result);
            }
        }

        private ManagedTestRun ResolveRun(string runId, bool allowFallback)
        {
            lock (_stateLock)
            {
                if (!string.IsNullOrEmpty(runId) && _runHistory.TryGetValue(runId, out var specified))
                {
                    return specified;
                }

                if (!allowFallback)
                {
                    return null;
                }

                return _activeRun ?? _lastCompletedRun;
            }
        }

        private void TrimHistory()
        {
            while (_runOrder.Count > RunHistoryLimit)
            {
                var oldest = _runOrder.Dequeue();
                if (_activeRun != null && string.Equals(_activeRun.RunId, oldest, StringComparison.Ordinal))
                {
                    // Skip removal for the currently running entry; it will be trimmed on next pass.
                    continue;
                }

                _runHistory.Remove(oldest);
            }
        }

        private static Filter BuildFilter(TestRunRequest request)
        {
            return new Filter
            {
                testMode = request.Mode,
                testNames = ToArrayOrNull(request.TestNames),
                groupNames = ToArrayOrNull(request.GroupNames),
                categoryNames = ToArrayOrNull(request.CategoryNames),
                assemblyNames = ToArrayOrNull(request.AssemblyNames),
            };
        }

        private static string[] ToArrayOrNull(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return null;
            }

            var filtered = values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return filtered.Length == 0 ? null : filtered;
        }

        private static TestRunState MapState(TestRunResult result)
        {
            if (result == null)
            {
                return TestRunState.Faulted;
            }

            var state = result.Summary?.ResultState;
            if (string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return TestRunState.Failed;
            }

            if (string.Equals(state, "Cancelled", StringComparison.OrdinalIgnoreCase) || string.Equals(state, "Canceled", StringComparison.OrdinalIgnoreCase))
            {
                return TestRunState.Canceled;
            }

            if (string.Equals(state, "Passed", StringComparison.OrdinalIgnoreCase))
            {
                return TestRunState.Completed;
            }

            return TestRunState.Completed;
        }

        private static TestRunStatusSnapshot CreateSnapshot(ManagedTestRun run)
        {
            return new TestRunStatusSnapshot(
                run.RunId,
                run.Request.Mode,
                run.State,
                run.StartTimeUtc,
                run.EndTimeUtc,
                run.Result?.Summary);
        }

        #endregion

        #region Test list helpers

        private async Task<ITestAdaptor> RetrieveTestRootAsync(TestMode mode)
        {
            var tcs = new TaskCompletionSource<ITestAdaptor>(TaskCreationOptions.RunContinuationsAsynchronously);

            _testRunnerApi.RetrieveTestList(mode, root =>
            {
                tcs.TrySetResult(root);
            });

            // Ensure the editor pumps at least one additional update in case the window is unfocused.
            EditorApplication.QueuePlayerLoopUpdate();

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(true);
            if (completed != tcs.Task)
            {
                McpLog.Warn($"[TestRunnerService] Timeout waiting for test retrieval callback for {mode}");
                return null;
            }

            try
            {
                return await tcs.Task.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                McpLog.Error($"[TestRunnerService] Error retrieving tests for {mode}: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        private static void CollectFromNode(
            ITestAdaptor node,
            TestMode mode,
            List<Dictionary<string, string>> output,
            HashSet<string> seen,
            List<string> path)
        {
            if (node == null)
            {
                return;
            }

            bool hasName = !string.IsNullOrEmpty(node.Name);
            if (hasName)
            {
                path.Add(node.Name);
            }

            bool hasChildren = node.HasChildren && node.Children != null;

            if (!hasChildren)
            {
                string fullName = string.IsNullOrEmpty(node.FullName) ? node.Name ?? string.Empty : node.FullName;
                string key = $"{mode}:{fullName}";

                if (!string.IsNullOrEmpty(fullName) && seen.Add(key))
                {
                    string computedPath = path.Count > 0 ? string.Join("/", path) : fullName;
                    output.Add(new Dictionary<string, string>
                    {
                        ["name"] = node.Name ?? fullName,
                        ["full_name"] = fullName,
                        ["path"] = computedPath,
                        ["mode"] = mode.ToString(),
                    });
                }
            }
            else if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    CollectFromNode(child, mode, output, seen, path);
                }
            }

            if (hasName && path.Count > 0)
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        #endregion

        private sealed class ManagedTestRun
        {
            internal ManagedTestRun(string runId, TestRunRequest request)
            {
                RunId = runId;
                Request = request;
                StartTimeUtc = DateTime.UtcNow;
                State = TestRunState.Queued;
            }

            public string RunId { get; }
            public TestRunRequest Request { get; }
            public DateTime StartTimeUtc { get; set; }
            public DateTime? EndTimeUtc { get; set; }
            public TestRunState State { get; set; }
            public TestRunResult Result { get; set; }
        }
    }

    /// <summary>
    /// Summary of a Unity test run.
    /// </summary>
    public sealed class TestRunResult
    {
        internal TestRunResult(string runId, TestMode mode, TestRunSummary summary, IReadOnlyList<TestRunTestResult> results)
        {
            RunId = runId;
            Mode = mode;
            Summary = summary;
            Results = results;
        }

        public string RunId { get; }
        public TestMode Mode { get; }
        public TestRunSummary Summary { get; }
        public IReadOnlyList<TestRunTestResult> Results { get; }

        public int Total => Summary.Total;
        public int Passed => Summary.Passed;
        public int Failed => Summary.Failed;
        public int Skipped => Summary.Skipped;

        public object ToSerializable()
        {
            return new
            {
                runId = RunId,
                mode = Mode.ToString(),
                summary = Summary.ToSerializable(),
                results = Results.Select(r => r.ToSerializable()).ToList(),
            };
        }

        internal static TestRunResult Create(string runId, TestMode mode, ITestResultAdaptor summary, IReadOnlyList<ITestResultAdaptor> tests)
        {
            var materializedTests = tests.Select(TestRunTestResult.FromAdaptor).ToList();

            int passed = summary?.PassCount
                ?? materializedTests.Count(t => string.Equals(t.State, "Passed", StringComparison.OrdinalIgnoreCase));
            int failed = summary?.FailCount
                ?? materializedTests.Count(t => string.Equals(t.State, "Failed", StringComparison.OrdinalIgnoreCase));
            int skipped = summary?.SkipCount
                ?? materializedTests.Count(t => string.Equals(t.State, "Skipped", StringComparison.OrdinalIgnoreCase));

            double duration = summary?.Duration
                ?? materializedTests.Sum(t => t.DurationSeconds);

            int total = summary != null ? passed + failed + skipped : materializedTests.Count;

            var summaryPayload = new TestRunSummary(
                total,
                passed,
                failed,
                skipped,
                duration,
                summary?.ResultState ?? "Unknown");

            return new TestRunResult(runId, mode, summaryPayload, materializedTests);
        }
    }

    public sealed class TestRunSummary
    {
        internal TestRunSummary(int total, int passed, int failed, int skipped, double durationSeconds, string resultState)
        {
            Total = total;
            Passed = passed;
            Failed = failed;
            Skipped = skipped;
            DurationSeconds = durationSeconds;
            ResultState = resultState;
        }

        public int Total { get; }
        public int Passed { get; }
        public int Failed { get; }
        public int Skipped { get; }
        public double DurationSeconds { get; }
        public string ResultState { get; }

        internal object ToSerializable()
        {
            return new
            {
                total = Total,
                passed = Passed,
                failed = Failed,
                skipped = Skipped,
                durationSeconds = DurationSeconds,
                resultState = ResultState,
            };
        }
    }

    public sealed class TestRunTestResult
    {
        internal TestRunTestResult(
            string name,
            string fullName,
            string state,
            double durationSeconds,
            string message,
            string stackTrace,
            string output)
        {
            Name = name;
            FullName = fullName;
            State = state;
            DurationSeconds = durationSeconds;
            Message = message;
            StackTrace = stackTrace;
            Output = output;
        }

        public string Name { get; }
        public string FullName { get; }
        public string State { get; }
        public double DurationSeconds { get; }
        public string Message { get; }
        public string StackTrace { get; }
        public string Output { get; }

        internal object ToSerializable()
        {
            return new
            {
                name = Name,
                fullName = FullName,
                state = State,
                durationSeconds = DurationSeconds,
                message = Message,
                stackTrace = StackTrace,
                output = Output,
            };
        }

        internal static TestRunTestResult FromAdaptor(ITestResultAdaptor adaptor)
        {
            if (adaptor == null)
            {
                return new TestRunTestResult(string.Empty, string.Empty, "Unknown", 0.0, string.Empty, string.Empty, string.Empty);
            }

            return new TestRunTestResult(
                adaptor.Name,
                adaptor.FullName,
                adaptor.ResultState,
                adaptor.Duration,
                adaptor.Message,
                adaptor.StackTrace,
                adaptor.Output);
        }
    }
    
    public sealed class TestRunHandle
    {
        internal TestRunHandle(string runId, bool startedNewRun, Task<TestRunResult> completionTask)
        {
            RunId = runId;
            StartedNewRun = startedNewRun;
            CompletionTask = completionTask ?? throw new ArgumentNullException(nameof(completionTask));
        }

        public string RunId { get; }
        public bool StartedNewRun { get; }
        public Task<TestRunResult> CompletionTask { get; }
    }

    public sealed class TestRunRequest
    {
        public TestRunRequest(
            TestMode mode,
            IReadOnlyList<string> testNames = null,
            IReadOnlyList<string> groupNames = null,
            IReadOnlyList<string> categoryNames = null,
            IReadOnlyList<string> assemblyNames = null)
        {
            Mode = mode;
            TestNames = testNames;
            GroupNames = groupNames;
            CategoryNames = categoryNames;
            AssemblyNames = assemblyNames;
        }

        public TestMode Mode { get; }
        public IReadOnlyList<string> TestNames { get; }
        public IReadOnlyList<string> GroupNames { get; }
        public IReadOnlyList<string> CategoryNames { get; }
        public IReadOnlyList<string> AssemblyNames { get; }
    }

    public sealed class TestRunStatusSnapshot
    {
        internal TestRunStatusSnapshot(
            string runId,
            TestMode mode,
            TestRunState state,
            DateTime startUtc,
            DateTime? endUtc,
            TestRunSummary summary)
        {
            RunId = runId;
            Mode = mode;
            State = state;
            StartTimeUtc = startUtc;
            EndTimeUtc = endUtc;
            Summary = summary;
        }

        public string RunId { get; }
        public TestMode Mode { get; }
        public TestRunState State { get; }
        public DateTime StartTimeUtc { get; }
        public DateTime? EndTimeUtc { get; }
        public TestRunSummary Summary { get; }
        public double ElapsedSeconds
        {
            get
            {
                var end = EndTimeUtc ?? DateTime.UtcNow;
                return Math.Max(0, (end - StartTimeUtc).TotalSeconds);
            }
        }

        public object ToSerializable()
        {
            return new
            {
                runId = RunId,
                mode = Mode.ToString(),
                state = State.ToString(),
                startedAt = StartTimeUtc.ToString("o"),
                completedAt = EndTimeUtc?.ToString("o"),
                elapsedSeconds = ElapsedSeconds,
                summary = Summary?.ToSerializable(),
            };
        }
    }

    public enum TestRunState
    {
        Queued,
        Running,
        Cancelling,
        Completed,
        Failed,
        Canceled,
        Faulted,
    }
}
