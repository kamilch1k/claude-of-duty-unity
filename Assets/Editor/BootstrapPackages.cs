using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

/// <summary>
/// Installs the packages the port needs that the URP template does not ship.
///
/// Run headless:
///   Unity.exe -batchmode -projectPath &lt;project&gt; -executeMethod BootstrapPackages.AddPackages -logFile &lt;log&gt;
///
/// It is a separate step from asset building because a package landing triggers
/// a full domain reload and reimport; doing both in one run makes the failure
/// modes impossible to tell apart.
/// </summary>
public static class BootstrapPackages
{
    static readonly string[] Wanted =
    {
        // Unity has no native glTF importer; the baked weapon/soldier GLBs need this.
        "com.unity.cloud.gltfast",
    };

    static AddRequest _req;
    static int _index;

    public static void AddPackages()
    {
        _index = 0;
        Next();
    }

    static void Next()
    {
        if (_index >= Wanted.Length)
        {
            Debug.Log($"[bootstrap] all packages resolved ({Wanted.Length})");
            EditorApplication.Exit(0);
            return;
        }
        Debug.Log($"[bootstrap] adding {Wanted[_index]}");
        _req = Client.Add(Wanted[_index]);
        EditorApplication.update += Progress;
    }

    static void Progress()
    {
        if (!_req.IsCompleted) return;
        EditorApplication.update -= Progress;
        if (_req.Status == StatusCode.Success)
        {
            Debug.Log($"[bootstrap] ok {_req.Result.packageId}");
            _index++;
            Next();
        }
        else
        {
            Debug.LogError($"[bootstrap] failed {Wanted[_index]}: {_req.Error?.message}");
            EditorApplication.Exit(1);
        }
    }
}
