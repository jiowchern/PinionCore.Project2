using PinionCore.Project2.Shared;
using TMPro;
using UnityEngine;

namespace PinionCore.Project2.Client
{
    public class Actor: MonoBehaviour
    {
        public TMPro.TextMeshPro DisplayName;
        public Transform Target;
        public Actor()
        {

        }

        public void Setup(IActor actor)
        {
            DisplayName.text = actor.DisplayName;
            Target.position = actor.Position;
        }
        
    }

}