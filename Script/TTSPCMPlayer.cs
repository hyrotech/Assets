using System;
using System.Collections.Generic;
using UnityEngine;

// 受け口：OnTTSStreamBegin / OnTTSStreamChunk / OnTTSStreamEnd
// ・必ず Begin → Chunk → End の順に同じ id で処理
// ・順序逆転/混線しても無視できるように実装
public class TTSPCMPlayer : MonoBehaviour
{
    [Serializable]
    private class BeginMsg { public string id; public int ch; public double sr; public string fmt; public bool ilv; }
    [Serializable]
    private class ChunkMsg { public string id; public string b64; public int frames; }
    [Serializable]
    private class EndMsg { public string id; }

    public AudioSource output;  // 再生用（BridgeTarget 等に付ける）

    class StreamBuf
    {
        public int channels;
        public int sampleRate;
        public List<float> samples = new List<float>(131072);
    }

    private readonly Dictionary<string, StreamBuf> _streams = new Dictionary<string, StreamBuf>();

    void Awake()
    {
        if (!output) output = GetComponent<AudioSource>();
        if (!output) output = gameObject.AddComponent<AudioSource>();
        output.playOnAwake = false;
        output.loop = false;
    }

    // ---- Unity 受け口 ----
    public void OnTTSStreamBegin(string json)
    {
        var b = JsonUtility.FromJson<BeginMsg>(json);
        if (b == null || string.IsNullOrEmpty(b.id)) return;

        var buf = new StreamBuf
        {
            channels = Mathf.Max(1, b.ch),
            sampleRate = Mathf.Max(8000, (int)Math.Round(b.sr))
        };
        _streams[b.id] = buf;

        Debug.Log($"[TTSPCM] Begin sr={buf.sampleRate}, ch={buf.channels}, fmt={b.fmt}, ilv={b.ilv}");
    }

    public void OnTTSStreamChunk(string json)
    {
        var c = JsonUtility.FromJson<ChunkMsg>(json);
        if (c == null || string.IsNullOrEmpty(c.id) || string.IsNullOrEmpty(c.b64)) return;
        if (!_streams.TryGetValue(c.id, out var buf)) return; // Begin まだ

        var bytes = Convert.FromBase64String(c.b64);
        // Float32 Interleaved 前提
        int floatCount = bytes.Length / 4;
        var floats = new float[floatCount];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        buf.samples.AddRange(floats);
    }

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
            Debug.LogWarning("[TTSPCM] End with no active stream.");
            return;
        }

        if (buf.samples.Count == 0)
        {
            Debug.LogWarning("[TTSPCM] End with no samples.");
            _streams.Remove(e.id);
            return;
        }

        // AudioClip を生成して再生
        int totalSamples = buf.samples.Count / buf.channels;
        var clip = AudioClip.Create($"tts-{e.id}", totalSamples, buf.channels, buf.sampleRate, false);
        // SetData はチャンネルインターリーブ想定
        clip.SetData(buf.samples.ToArray(), 0);

        output.Stop();
        output.clip = clip;
        output.Play();

        _streams.Remove(e.id);
    }
}