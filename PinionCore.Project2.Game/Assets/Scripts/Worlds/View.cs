using PinionCore.Project2.Protocols.Worlds;
using UnityEngine;
using System.Linq;
namespace PinionCore.Project2.Worlds
{
    public class View : MonoBehaviour
    {
        public WorldConfig[] WorldInfos;
        public View ()
        {

        }

        public void Setup(IView view)
        {
            var info = WorldInfos.FirstOrDefault(x => x.Name == view.Name.Value);
            if (info ==null) {
                Debug.LogError($"[View] 找不到對應的 WorldInfo: {view.Name.Value}");
                return;
            }

            // todo create client terrain with info.TerrainPrefab


        }

    }
}
