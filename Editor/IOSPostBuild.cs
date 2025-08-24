#if UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using System.IO;

public class IOSPostBuild {
  [PostProcessBuild]
  public static void OnPostprocessBuild(BuildTarget target, string path) {
    if (target != BuildTarget.iOS) return;

    var projPath = PBXProject.GetPBXProjectPath(path);
    var proj = new PBXProject(); proj.ReadFromFile(projPath);

#if UNITY_2019_3_OR_NEWER
    var main = proj.GetUnityMainTargetGuid();
    var fw   = proj.GetUnityFrameworkTargetGuid();
#else
    var main = proj.TargetGuidByName("Unity-iPhone");
    var fw   = main;
#endif
    // Swift有効化
    proj.SetBuildProperty(fw, "SWIFT_VERSION", "5.0");
    proj.SetBuildProperty(fw, "ALWAYS_EMBED_SWIFT_STANDARD_LIBRARIES", "YES");

    // 必要フレームワーク
    proj.AddFrameworkToProject(fw, "AVFoundation.framework", false);
    proj.AddFrameworkToProject(fw, "Speech.framework", false);
    proj.AddFrameworkToProject(fw, "WebKit.framework", false);

    // Info.plist 追記（権限）
    var plistPath = Path.Combine(path, "Info.plist");
    var plist = new PlistDocument(); plist.ReadFromFile(plistPath);
    var root = plist.root;
    root.SetString("NSMicrophoneUsageDescription", "音声入力に使用します。");
    root.SetString("NSSpeechRecognitionUsageDescription", "音声認識に使用します。");
    File.WriteAllText(plistPath, plist.WriteToString());

    proj.WriteToFile(projPath);
  }
}
#endif