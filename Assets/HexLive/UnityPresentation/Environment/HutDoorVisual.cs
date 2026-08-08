#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{

/// <summary>
/// Presentation-only transform for a hut door leaf. Simulation and navigation
/// decide when a door may be used; this component only preserves and blends
/// the authored closed/open poses around one real hinge.
/// </summary>
public sealed class HutDoorVisual : MonoBehaviour
{
    [SerializeField] private Vector3 _closedLocalPosition;
    [SerializeField] private Vector3 _closedLocalEuler;
    [SerializeField] private Vector3 _openLocalPosition;
    [SerializeField] private Vector3 _openLocalEuler;
    [SerializeField] private float _transitionSeconds = 0.32f;
    [SerializeField] private bool _isOpen = true;

    private Quaternion _closedLocalRotation = Quaternion.identity;
    private Quaternion _openLocalRotation = Quaternion.identity;
    private float _openAmount = 1f;

    public bool IsOpen => _isOpen;
    public float OpenAmount => _openAmount;

    public void Configure(Transform closedState, Transform openState, bool startOpen = true)
    {
        _closedLocalPosition = closedState.localPosition;
        _closedLocalRotation = closedState.localRotation;
        _closedLocalEuler = _closedLocalRotation.eulerAngles;
        _openLocalPosition = openState.localPosition;
        _openLocalRotation = openState.localRotation;
        _openLocalEuler = _openLocalRotation.eulerAngles;
        _isOpen = startOpen;
        _openAmount = startOpen ? 1f : 0f;
        ApplyPose();
    }

    public void Configure(Vector3 closedPosition, Quaternion closedRotation,
        Vector3 openPosition, Quaternion openRotation, bool startOpen = true)
    {
        _closedLocalPosition = closedPosition;
        _closedLocalRotation = closedRotation;
        _closedLocalEuler = closedRotation.eulerAngles;
        _openLocalPosition = openPosition;
        _openLocalRotation = openRotation;
        _openLocalEuler = openRotation.eulerAngles;
        _isOpen = startOpen;
        _openAmount = startOpen ? 1f : 0f;
        ApplyPose();
    }

    public void Open() => SetOpen(true);
    public void Close() => SetOpen(false);

    public void SetOpen(bool open, bool immediate = false)
    {
        _isOpen = open;
        if (!immediate) return;
        _openAmount = open ? 1f : 0f;
        ApplyPose();
    }

    public void SetOpenAmount(float amount)
    {
        _openAmount = Mathf.Clamp01(amount);
        _isOpen = _openAmount >= 0.5f;
        ApplyPose();
    }

    private void Awake()
    {
        // Serialized Euler values keep both authored poses inspectable even
        // before another system begins driving the door.
        _closedLocalRotation = Quaternion.Euler(_closedLocalEuler);
        _openLocalRotation = Quaternion.Euler(_openLocalEuler);
        _openAmount = _isOpen ? 1f : 0f;
        ApplyPose();
    }

    private void Update()
    {
        var target = _isOpen ? 1f : 0f;
        if (Mathf.Approximately(_openAmount, target)) return;
        var speed = _transitionSeconds <= 0.001f ? 1000f : 1f / _transitionSeconds;
        _openAmount = Mathf.MoveTowards(_openAmount, target, speed * Time.deltaTime);
        ApplyPose();
    }

    private void ApplyPose()
    {
        transform.localPosition = Vector3.Lerp(_closedLocalPosition, _openLocalPosition, _openAmount);
        transform.localRotation = Quaternion.Slerp(_closedLocalRotation, _openLocalRotation, _openAmount);
    }
}

}
