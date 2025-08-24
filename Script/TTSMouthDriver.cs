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
    public float minPerVowel = 0.06f;          // 1 音素の最短表示
    [Range(0f,0.5f)] public float overlap = 0.25f; // 前後オーバーラップ率
    public float neutralHold = 0.08f;          // 無母音の最短ホールド

    [Header("Smoothing")]
    public float openTime = 0.04f;
    public float closeTime = 0.09f;

    [Header("Sync")]
    [Tooltip("音声出力の遅延補正（+で口を遅らせる, 単位 ms）")]
    public float lipDelayMs = -200f;            // ←ここを端末に合わせて ± 調整
    [Tooltip("minPerVowel未満のチャンクは前の音素と結合する")]
    public bool mergeTooShort = true;

    // --- internal state (SmoothDamp) ---
    float targetA, targetI, targetU, targetE, targetO;
    float currA,   currI,   currU,   currE,   currO;
    float velA,    velI,    velU,    velE,    velO;

    // --- queue player ---
    struct LipEvt { public float dur; public List<string> vowels; }
    readonly Queue<LipEvt> _queue = new Queue<LipEvt>();
    Coroutine _consumer;
    float _carry; // 直前から持ち越す短時間

    void Awake()
    {
        if (!IsValidFace(face)) face = AutoFindFaceRenderer();
        if (!IsValidFace(face))
        {
            Debug.LogError("[TTSMouthDriver] SkinnedMeshRenderer が見つかりません。");
            return;
        }

        face.updateWhenOffscreen = true;
        face.quality = SkinQuality.Bone4;
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

        // ---- enqueue, don't restart the player ----
        _queue.Enqueue(new LipEvt { dur = dur, vowels = vowels });

        if (_consumer == null) _consumer = StartCoroutine(ConsumeQueue());
    }

    // -------------------- Consumer --------------------
    IEnumerator ConsumeQueue()
    {
        // 全体レイテンシ補正（音声が先に鳴るなら負、遅れて鳴くなら正）
        float delay = lipDelayMs * 0.001f;
        if (delay > 0f) yield return new WaitForSecondsRealtime(delay);

        while (true)
        {
            if (_queue.Count == 0)
            {
                // キュー空ならしばらく中立で待機
                SetNeutralTargets();
                _consumer = null;
                yield break;
            }

            var e = _queue.Dequeue();
            float dur = e.dur;

            // ①無母音：完全クローズせず基準口でつなぐ
            if (e.vowels == null || e.vowels.Count == 0)
            {
                SetNeutralTargets();
                yield return new WaitForSecondsRealtime(Mathf.Max(neutralHold, dur));
                continue;
            }

            // ②短すぎるチャンクは結合
            if (mergeTooShort)
            {
                dur += _carry;
                _carry = 0f;
                while (_queue.Count > 0 && dur < minPerVowel * e.vowels.Count)
                {
                    var next = _queue.Peek();
                    if (next.vowels == null || next.vowels.Count == 0) break; // 無母音は別扱い
                    dur += next.dur;
                    // vowels を後ろに連結
                    e.vowels.AddRange(next.vowels);
                    _queue.Dequeue();
                }
                if (dur < minPerVowel * e.vowels.Count)
                {
                    _carry = dur; // さらに次回へ持ち越し
                    continue;
                }
            }

            // ③オーバーラップしながら順次ターゲットを更新
            float per = Mathf.Max(minPerVowel, dur / e.vowels.Count);
            float step = per * (1f - Mathf.Clamp01(overlap));

            for (int i = 0; i < e.vowels.Count; i++)
            {
                SetVowelTargets(e.vowels[i]);
                yield return new WaitForSecondsRealtime(step);
            }

            // 最後の残り分 + 余韻
            yield return new WaitForSecondsRealtime(per * Mathf.Clamp01(overlap) + 0.01f);
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