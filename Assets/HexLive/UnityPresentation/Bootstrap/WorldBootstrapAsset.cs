#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Bootstrap
{

public sealed class WorldBootstrapAsset : MonoBehaviour
{
    [SerializeField] private TextAsset? _worldJson;

    public TextAsset? WorldJson => _worldJson;

    public void SetWorldJson(TextAsset worldJson)
    {
        _worldJson = worldJson;
    }
}

}
