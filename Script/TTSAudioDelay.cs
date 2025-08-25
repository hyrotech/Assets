using UnityEngine;

public class TTSAudioDelay : MonoBehaviour
{
    public AudioSource audioSource;

    void Start()
    {
        if (audioSource)
        {
            audioSource.PlayDelayed(1.0f); // 100ms 遅延
        }
    }
}