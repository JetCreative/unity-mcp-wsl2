using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace MCPForUnityTests.PlayMode
{
    public class BasicPlayModeSampleTests
    {
        [UnityTest]
        public IEnumerator SamplePassingPlayModeTest_YieldsFrameAndPasses()
        {
            var go = new GameObject("PlayModePassingProbe");
            go.transform.position = Vector3.one;
            yield return null;
            Assert.AreEqual(Vector3.one, go.transform.position);
            Object.Destroy(go);
        }

        [UnityTest]
        public IEnumerator SampleFailingPlayModeTest_IntentionalFailure()
        {
            yield return null;
            Assert.Fail("Intentional PlayMode failure to exercise test tooling.");
        }
    }
}
