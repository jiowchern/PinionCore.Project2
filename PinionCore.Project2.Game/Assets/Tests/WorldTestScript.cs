using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using PinionCore.NetSync.UniRx;
using UniRx;
using System.Linq;
public class WorldTestScript
{
    // A Test behaves as an ordinary method
    [Test]
    public void IWorldTestScriptSimplePasses()
    {
        // Use the Assert class to test conditions
    }

    // A UnityTest behaves like a coroutine in Play Mode. In Edit Mode you can use
    // `yield return null;` to skip a frame.
    [UnityTest]
    public IEnumerator IWorldTestScriptWithEnumeratorPasses()
    {
        // Use the Assert class to test conditions.
        // Use yield to skip a frame.

        PinionCore.Project2.Protocols.Worlds.IWorld world = new PinionCore.Project2.Worlds.World();
        var obs = from result in world.LoadTerrain().RemoteValue()
                  select result;

        obs.Subscribe(x => Debug.Log(x));

        yield return obs;

        
        

    }
}
