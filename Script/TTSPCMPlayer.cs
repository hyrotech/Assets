using System;
using System.Collections.Generic;
using UnityEngine;
using System.IO; // ★ 追加

public class TTSPCMPlayer : MonoBehaviour
{
    [Serializable]
    private class BeginMsg { public string id; public int seq; public int ch; public double sr; public string fmt; public bool ilv; }
    [Serializable]
    private class ChunkMsg { public string id; public int seq; public string b64; public int frames; }
    [Serializable]
    private class EndMsg   { public string id; public int seq; }

    public AudioSource output;

    class StreamBuf
    {
        public int channels;
        public int sampleRate;
        public List<float> samples = new List<float>(131072);

        // ★ 重複検出用
        public HashSet<int> seenSeq = new HashSet<int>();
        public int lastSeq = -1;
    }

    private readonly Dictionary<string, StreamBuf> _streams = new Dictionary<string, StreamBuf>();

    void Awake()
    {
        if (!output) output = GetComponent<AudioSource>();
        if (!output) output = gameObject.AddComponent<AudioSource>();
        output.playOnAwake = false;
        output.loop = false;
    }

    // ---- Begin ----
    public void OnTTSStreamBegin(string json)
    {
        var b = JsonUtility.FromJson<BeginMsg>(json);
        if (b == null || string.IsNullOrEmpty(b.id)) return;

        // 既存があればクリア（同一idの再利用対策）
        if (_streams.ContainsKey(b.id))
        {
            Debug.LogWarning($"[TTSPCM] Begin: stream {b.id} already exists. Overwriting.");
            _streams.Remove(b.id);
        }

        var buf = new StreamBuf
        {
            channels = Mathf.Max(1, b.ch),
            sampleRate = Mathf.Max(8000, (int)Math.Round(b.sr)),
            lastSeq = -1
        };
        _streams[b.id] = buf;

        // ★ 重複チェック（Begin自体のseqも登録）
        if (!buf.seenSeq.Add(b.seq))
        {
            Debug.LogWarning($"[TTSPCM] Begin DUPLICATE ignored: id={b.id} seq={b.seq}");
            return;
        }
        buf.lastSeq = b.seq;

        Debug.Log($"[TTSPCM] Begin id={b.id} seq={b.seq} sr={buf.sampleRate}, ch={buf.channels}, fmt={b.fmt}, ilv={b.ilv}");
    }

    // ---- Chunk ----
    public void OnTTSStreamChunk(string json)
    {
        var c = JsonUtility.FromJson<ChunkMsg>(json);
        if (c == null || string.IsNullOrEmpty(c.id) || string.IsNullOrEmpty(c.b64)) return;
        if (!_streams.TryGetValue(c.id, out var buf)) { Debug.LogWarning($"[TTSPCM] Chunk for unknown stream id={c.id}"); return; }

        // ★ 重複検出
        if (!buf.seenSeq.Add(c.seq))
        {
            Debug.LogWarning($"[TTSPCM] Chunk DUPLICATE ignored: id={c.id} seq={c.seq} frames={c.frames}");
            return;
        }

        // ★ 順序チェック（ギャップや逆順をログ）
        if (buf.lastSeq >= 0 && c.seq != buf.lastSeq + 1)
        {
            Debug.LogWarning($"[TTSPCM] Chunk out-of-order: id={c.id} seq={c.seq} (expected {buf.lastSeq + 1}) frames={c.frames}");
        }
        buf.lastSeq = c.seq;

        var bytes = Convert.FromBase64String(c.b64);
        int floatCount = bytes.Length / 4;
        var floats = new float[floatCount];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        buf.samples.AddRange(floats);

        Debug.Log($"[TTSPCM] Chunk ok: id={c.id} seq={c.seq} frames={c.frames} floats={floatCount}");
    }

    // ---- End ----
    public void OnTTSStreamEnd(string json)
    {
        var e = JsonUtility.FromJson<EndMsg>(json);
        if (e == null || string.IsNullOrEmpty(e.id))
        {
            Debug.LogWarning("[TTSPCM] End with invalid id.");
            return;
        }
        if (!_streams.TryGetValue(e.id, out var buf))
        {
            Debug.LogWarning($"[TTSPCM] End with no active stream. id={e.id}");
            return;
        }

        // ★ End の重複検出
        if (!buf.seenSeq.Add(e.seq))
        {
            Debug.LogWarning($"[TTSPCM] End DUPLICATE ignored: id={e.id} seq={e.seq}");
            _streams.Remove(e.id); // 二重 End はクリーンアップだけでもOK
            return;
        }

        if (buf.lastSeq >= 0 && e.seq != buf.lastSeq + 1)
        {
            Debug.LogWarning($"[TTSPCM] End out-of-order: id={e.id} seq={e.seq} (expected {buf.lastSeq + 1})");
        }
        buf.lastSeq = e.seq;

        if (buf.samples.Count == 0)
        {
            Debug.LogWarning($"[TTSPCM] End with no samples. id={e.id}");
            _streams.Remove(e.id);
            return;
        }

        // ==== AudioClip 再生（既存の動きは維持）====
        int totalSamples = buf.samples.Count / buf.channels;
        var clip = AudioClip.Create($"tts-{e.id}", totalSamples, buf.channels, buf.sampleRate, false);
        clip.SetData(buf.samples.ToArray(), 0);
        output.Stop();
        output.clip = clip;
        output.Play();

        _streams.Remove(e.id);
    }
}