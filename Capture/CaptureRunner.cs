using System;
using BepInExposition.Config;
using UnityEngine;

namespace BepInExposition.Capture;

internal sealed class CaptureRunner : MonoBehaviour
{
    private CaptureCoordinator? _coordinator;

    public CaptureRunner(IntPtr pointer)
        : base(pointer)
    {
    }

    private void Awake()
    {
        // Keep one hidden Unity object alive so the coordinator can tick without patching game code.
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        gameObject.hideFlags = HideFlags.HideAndDontSave;

        _coordinator = new CaptureCoordinator();
        _coordinator.Start();
    }

    private void Update()
    {
        if (!CaptureSettings.Enabled.Value)
        {
            return;
        }

        _coordinator?.Tick(Time.realtimeSinceStartup);
    }

    private void OnApplicationQuit()
    {
        _coordinator?.Shutdown("application_quit");
        _coordinator = null;
    }

    private void OnDestroy()
    {
        _coordinator?.Shutdown("runner_destroyed");
        _coordinator = null;
    }
}
