using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// MiniJSON expected: {"dur":0.18,"vowels":["a"]} / {"dur":0.12,"vowels":[]}

public class TTSMouthDriver : MonoBehaviour
{
    [Header("BlendShapes")]
    public SkinnedMeshRenderer face;
    public int visemeA = 6, visemeI = 7, visemeU = 8, visemeE = 9, visemeO = 10;

    [Header("Motion")]
    [Range(0,100)] public float vowelWeight = 85f;
    [Range(0,100)] public float neutralWeight = 10f;
    public float minPerVowel = 0.06f;                // 1 音素の最短表示
    [Range(0f,0.5f)] public float overlap = 0.25f;   // 前後オーバーラップ率（0で等分）
    public float neutralHold = 0.08f;                // 無母音の最短ホールド

    [Header("Smoothing")]
    public float openTime = 0.04f;
    public float closeTime = 0.09f;

    [Header("Sync")]
    [Tooltip("音声出力の遅延補正（+で口を遅らせる, 単位 ms）。全チャンクの開始時刻に一律で適用されます。")]
    public float lipDelayMs = 120f;
    [Tooltip("minPerVowel未満のチャンクは前の音素と結合する（“スキップ”はしません）")]
    public bool mergeTooShort = true;

    // --- internal state (SmoothDamp) ---
    float targetA, targetI, targetU, targetE, targetO;
    float currA,   currI,   currU,   currE,   currO;
    float velA,    velI,    velU,    velE,    velO;

    // --- scheduler queue ---
    struct LipEvt
    {
        public float dur;
        public List<string> vowels;
        public double start;     // 再生予定(絶対)時刻: Time.realtimeSinceStartup ベース
        public double end;       // 予定終了時刻（デバッグ用）
    }
    readonly Queue<LipEvt> _queue = new Queue<LipEvt>();
    Coroutine _consumer;
    double _lastScheduledEnd = -1; // 直前イベントの予定終了（モノトニック化に使用）
    float _carry;                  // mergeTooShort の持ち越し

    void Awake()
    {
        if (!IsValidFace(face)) face = AutoFindFaceRenderer();
        if (!IsValidFace(face))
        {
            Debug.LogError("[TTSMouthDriver] SkinnedMeshRenderer が見つかりません。");
            return;
        }

        face.updateWhenOffscreen = true;
#if UNITY_6000_1_OR_NEWER
        face.quality = SkinQuality.Bone4; // 6000系
#endif
        ResolveVisemeIndices(face.sharedMesh);

        var m = face.sharedMesh;
        Debug.Log($"[TTSMouthDriver] Face Mesh='{m.name}', BlendShapes={m.blendShapeCount}, A={visemeA}, I={visemeI}, U={visemeU}, E={visemeE}, O={visemeO}");

        StartCoroutine(_BootTest());
    }

    // -------------------- Public entry --------------------
    public void OnTTSRange(string json)
    {
        if (!IsValidFace(face) || string.IsNullOrEmpty(json)) return;

        var dict = MiniJSON.Json.Deserialize(json) as Dictionary<string, object>;
        if (dict == null) return;

        float dur = 0.2f;
        List<string> vowels = null;

        if (dict.TryGetValue("dur", out var d))
        {
            if      (d is double dd) dur = (float)dd;
            else if (d is float  ff) dur = ff;
            else if (d is long   ll) dur = (float)ll;
            else if (d is int    ii) dur = ii;
        }
        if (dict.TryGetValue("vowels", out var vs) && vs is List<object> list)
        {
            vowels = list.Select(x => x?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToList();
        }

        // ---- スケジューラに投入（絶対時刻を付与）----
        Schedule(dur, vowels);

        if (_consumer == null) _consumer = StartCoroutine(ConsumeQueue());
    }

    // 受信したチャンクに「予定時刻」を付けてキューへ
    void Schedule(float dur, List<string> vowels)
    {
        // 一律オフセット（負値はデータ未着のため実質 0 に丸め）
        double now = Time.realtimeSinceStartupAsDouble;
        double delay = Mathf.Max(0f, lipDelayMs * 0.001f);

        // 受信時点からの予定開始
        double scheduledStart = now + delay;

        // モノトニック化：前の予定終了以降に必ず配置
        if (_lastScheduledEnd > 0 && scheduledStart < _lastScheduledEnd)
            scheduledStart = _lastScheduledEnd;

        // mergeTooShort: ここで “次の音声データを待って結合” だけを行い、スキップはしない
        if (mergeTooShort && (vowels != null && vowels.Count > 0))
        {
            // 小さすぎる dur は一旦持ち越す（結合ができなければそのまま再生）
            if (dur + _carry < minPerVowel * vowels.Count && _queue.Count == 0)
            {
                _carry += dur;
                // 予定はキープ（先頭のタイミングを固定したいので）
                // ただし実チャンクを入れない
                return;
            }
            if (_carry > 0f)
            {
                dur += _carry;
                _carry = 0f;
            }
        }

        var e = new LipEvt
        {
            dur = Mathf.Max(0.001f, dur),
            vowels = vowels,
            start = scheduledStart
        };
        e.end = e.start + e.dur;

        _queue.Enqueue(e);
        _lastScheduledEnd = e.end;
    }

    // -------------------- Consumer（絶対時刻駆動・音素は絶対にスキップしない） --------------------
    IEnumerator ConsumeQueue()
    {
        while (true)
        {
            if (_queue.Count == 0)
            {
                SetNeutralTargets();
                _consumer = null;
                _lastScheduledEnd = -1; // 次の発話で再スタート
                yield break;
            }

            var e = _queue.Peek();
            double now = Time.realtimeSinceStartupAsDouble;

            // 予定時刻まで待機（Realtime）
            if (now < e.start)
            {
                yield return new WaitForSecondsRealtime((float)(e.start - now));
                continue; // 予定時刻になったら次ループで確定
            }

            // ここで実行確定
            _queue.Dequeue();

            // 無母音：ニュートラルを保つ（閉じ切らない）
            if (e.vowels == null || e.vowels.Count == 0)
            {
                SetNeutralTargets();
                yield return new WaitForSecondsRealtime(Mathf.Max(neutralHold, e.dur));
                continue;
            }

            // 母音を等分（オーバーラップあり）
            float per = Mathf.Max(minPerVowel, e.dur / e.vowels.Count);
            float step = per * (1f - Mathf.Clamp01(overlap));

            for (int i = 0; i < e.vowels.Count; i++)
            {
                SetVowelTargets(e.vowels[i]);
                yield return new WaitForSecondsRealtime(step);
            }

            // 最後の残り分 + 余韻
            float tail = per * Mathf.Clamp01(overlap);
            if (tail > 0f) yield return new WaitForSecondsRealtime(tail);
            SetNeutralTargets();
        }
    }

    // -------------------- Smoothing write --------------------
    void LateUpdate()
    {
        if (!IsValidFace(face)) return;

        currA = Damp(currA, targetA, ref velA);
        currI = Damp(currI, targetI, ref velI);
        currU = Damp(currU, targetU, ref velU);
        currE = Damp(currE, targetE, ref velE);
        currO = Damp(currO, targetO, ref velO);

        face.SetBlendShapeWeight(visemeA, currA);
        face.SetBlendShapeWeight(visemeI, currI);
        face.SetBlendShapeWeight(visemeU, currU);
        face.SetBlendShapeWeight(visemeE, currE);
        face.SetBlendShapeWeight(visemeO, currO);
    }

    float Damp(float current, float target, ref float velocity)
    {
        float smoothTime = (target > current) ? Mathf.Max(0.001f, openTime)
                                              : Mathf.Max(0.001f, closeTime);
        return Mathf.SmoothDamp(current, target, ref velocity, smoothTime, Mathf.Infinity, Time.deltaTime);
    }

    // -------------------- Target setters --------------------
    void SetVowelTargets(string v)
    {
        float a=0,i=0,u=0,e=0,o=0;
        switch (v)
        {
            case "a": a = vowelWeight; break;
            case "i": i = vowelWeight; break;
            case "u": u = vowelWeight; break;
            case "e": e = vowelWeight; break;
            case "o": o = vowelWeight; break;
            default:  a = vowelWeight * 0.7f; break;
        }
        float side = vowelWeight * 0.08f;
        targetA = Mathf.Max(a, side, neutralWeight);
        targetI = Mathf.Max(i, side*0.6f, neutralWeight*0.5f);
        targetU = Mathf.Max(u, side*0.6f, neutralWeight*0.6f);
        targetE = Mathf.Max(e, side*0.6f, neutralWeight*0.5f);
        targetO = Mathf.Max(o, side, neutralWeight);
    }

    void SetNeutralTargets()
    {
        targetA = neutralWeight;
        targetI = neutralWeight * 0.5f;
        targetU = neutralWeight * 0.6f;
        targetE = neutralWeight * 0.5f;
        targetO = neutralWeight;
    }

    // -------------------- Helpers --------------------
    SkinnedMeshRenderer AutoFindFaceRenderer()
    {
        var rs = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var r in rs)
        {
            var m = r.sharedMesh;
            if (!m) continue;
            if (HasVisemeNames(m)) return r;
        }
        return null;
    }

    void ResolveVisemeIndices(Mesh mesh)
    {
        if (!mesh) return;
        int idx;
        idx = mesh.GetBlendShapeIndex("blendShape1.MTH_A"); if (idx >= 0) visemeA = idx;
        idx = mesh.GetBlendShapeIndex("blendShape1.MTH_I"); if (idx >= 0) visemeI = idx;
        idx = mesh.GetBlendShapeIndex("blendShape1.MTH_U"); if (idx >= 0) visemeU = idx;
        idx = mesh.GetBlendShapeIndex("blendShape1.MTH_E"); if (idx >= 0) visemeE = idx;
        idx = mesh.GetBlendShapeIndex("blendShape1.MTH_O"); if (idx >= 0) visemeO = idx;
    }

    bool HasVisemeNames(Mesh m)
    {
        return  m.GetBlendShapeIndex("blendShape1.MTH_A") >= 0 ||
                m.GetBlendShapeIndex("blendShape1.MTH_I") >= 0 ||
                m.GetBlendShapeIndex("blendShape1.MTH_U") >= 0 ||
                m.GetBlendShapeIndex("blendShape1.MTH_E") >= 0 ||
                m.GetBlendShapeIndex("blendShape1.MTH_O") >= 0;
    }
    bool IsValidFace(SkinnedMeshRenderer r) => r && r.sharedMesh && r.sharedMesh.blendShapeCount > 0;

    IEnumerator _BootTest()
    {
        if (!IsValidFace(face)) yield break;
        SetVowelTargets("a");
        yield return new WaitForSecondsRealtime(0.25f);
        SetNeutralTargets();
    }
}