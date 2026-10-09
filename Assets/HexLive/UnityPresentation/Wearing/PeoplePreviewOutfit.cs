using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    // Only used by the §169 local review scene, never attached to catalog actor prefabs.
    public sealed class PeoplePreviewOutfit : MonoBehaviour
    {
        public ActorName actor;
        public Wear[] equipment;
        public Wear hair;

        private void Awake()
        {
            var body = GetComponent<BodyBones>();
            body.Construct(actor, (int)actor + 1);
            foreach (var item in equipment) body.Equip("preview_" + item.name, item, null);
            body.SetHair(hair);
            GetComponent<Animator>().cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }
    }
}
