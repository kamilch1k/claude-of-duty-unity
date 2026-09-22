using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Runs the game headless and reports what it actually does.
///
/// The first playable build was reported at ~0 fps and I had never measured it:
/// everything had been verified in edit mode, where Update does not run, so the
/// allocations, the per-frame raycasts and any exception spam were all invisible.
/// This enters play mode, pumps frames, and prints the frame-time distribution
/// and every error it saw.
///
///   Unity.exe -batchmode -projectPath . -executeMethod PlayTest.Run -logFile &lt;log&gt;
/// </summary>
public static class PlayTest
{
    const int Frames = 240;
    const string ScenePath = "Assets/Scenes/Street.unity";

    static int _frames;
    static readonly List<float> _times = new List<float>();
    static readonly List<string> _problems = new List<string>();

    public static void Run()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Application.logMessageReceived += OnLog;
        EditorApplication.update += Tick;
        EditorApplication.EnterPlaymode();
    }

    static void OnLog(string message, string stack, LogType type)
    {
        if (type != LogType.Exception && type != LogType.Error) return;
        if (_problems.Count < 20) _problems.Add(message);
    }

    static void Tick()
    {
        if (!Application.isPlaying) return;
        _frames++;
        _times.Add(Time.unscaledDeltaTime);
        if (_frames < Frames) return;

        EditorApplication.update -= Tick;
        Report();
        EditorApplication.Exit(_problems.Count == 0 ? 0 : 1);
    }

    static void Report()
    {
        _times.Sort();
        float sum = 0f;
        foreach (var t in _times) sum += t;
        float mean = sum / _times.Count;
        float p50 = _times[_times.Count / 2];
        float p95 = _times[Mathf.Min(_times.Count - 1, (int)(_times.Count * 0.95f))];
        float worst = _times[_times.Count - 1];

        var enemies = Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None);
        var alive = 0;
        foreach (var e in enemies) if (!e.Dead) alive++;

        Debug.Log($"[test] frames={_frames} mean={mean * 1000f:0.0}ms ({1f / mean:0.#} fps) " +
                  $"p50={p50 * 1000f:0.0}ms p95={p95 * 1000f:0.0}ms worst={worst * 1000f:0.0}ms");
        Debug.Log($"[test] scene: {enemies.Length} enemies ({alive} alive), " +
                  $"{Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Length} transforms, " +
                  $"cursor={Cursor.lockState}");
        Debug.Log($"[test] errors+exceptions: {_problems.Count}");
        for (int i = 0; i < _problems.Count && i < 6; i++) Debug.Log($"  ! {_problems[i]}");
    }
}
