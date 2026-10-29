using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace GentlemansClub
{
    // The club's music. The game turns Unity's own audio off (all its sound goes through Wwise), so an
    // AudioSource would stay silent; Mp3Stream decodes and plays the MP3 through Windows directly.
    // Plays every MP3 in the audio folder one after another (or loops one chosen song), fades in when you
    // walk into the club, out when you leave, pauses with the game, and drops to half volume while you
    // are talking to someone. Follows the game's Main and Music volume sliders on top of the mod's own
    // Volume setting. Lives on the plugin object, which is always active.
    internal class ClubMusic : MonoBehaviour
    {
        internal string Folder;
        internal string Song;      // one file to loop; null for the whole folder as a playlist
        internal bool Shuffle = true;
        internal float Volume = 0.6f;
        internal Func<bool> IsInside = () => false;
        private Mp3Stream _stream;
        private bool _failed, _errorLogged;
        private float _level; // current fade level 0..1
        private readonly Queue<string> _queue = new Queue<string>();
        private string _current;

        private void Update()
        {
            bool paused = Singleton<GameManager>.HasMain && Singleton<GameManager>.main.gameState == GameState.Paused;
            bool talking = Singleton<GameManager>.HasMain && Singleton<GameManager>.main.gameState == GameState.Dialogue;
            float target = IsInside() && !paused && Application.isFocused ? (talking ? 0.5f : 1f) : 0f;
            _level = Mathf.MoveTowards(_level, target, Time.unscaledDeltaTime / 1.2f); // ~1 s fades

            if (_stream != null && _stream.Ended) { _stream.Dispose(); _stream = null; } // next song
            if (_level > 0f && _stream == null && !_failed) Open();
            if (_stream == null) return;
            _stream.Volume = _level * Volume * GameVolume();
            _stream.Paused = _level <= 0f;
            if (_stream.Error != null && !_errorLogged)
            {
                GentlemansClubPlugin.Log.LogWarning("Club music stopped: " + _stream.Error);
                _errorLogged = true;
            }
        }

        // The game's own Main x Music sliders (0..1, changed live from its options menu).
        static float GameVolume()
        {
            var audio = OptionsPanel.Options?.audioData;
            return audio == null ? 0.75f * 0.75f : Mathf.Clamp01(audio.main) * Mathf.Clamp01(audio.music);
        }

        // The playlist, read fresh each time round, so songs dropped into the folder join the next pass.
        List<string> Songs()
        {
            if (Song != null) return new List<string> { Path.Combine(Folder, Song) };
            if (!Directory.Exists(Folder)) return new List<string>();
            var songs = Directory.GetFiles(Folder, "*.mp3").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (Shuffle)
            {
                for (int i = songs.Count - 1; i > 0; i--)
                {
                    int j = UnityEngine.Random.Range(0, i + 1);
                    (songs[i], songs[j]) = (songs[j], songs[i]);
                }
                // Not the song that just finished straight away again.
                if (songs.Count > 1 && songs[0] == _current) (songs[0], songs[1]) = (songs[1], songs[0]);
            }
            return songs;
        }

        private void Open()
        {
            // Try each song once at most; give up only if none of them will play.
            for (int tries = 0; ; tries++)
            {
                if (_queue.Count == 0)
                {
                    var songs = Songs();
                    if (songs.Count == 0 || tries > songs.Count)
                    {
                        GentlemansClubPlugin.Log.LogWarning("Club music unavailable: no playable MP3 in " + Folder);
                        _failed = true;
                        return;
                    }
                    foreach (var s in songs) _queue.Enqueue(s);
                }
                var path = _queue.Dequeue();
                try
                {
                    if (!File.Exists(path)) throw new FileNotFoundException(path);
                    _stream = new Mp3Stream(path) { Repeat = Song != null, Volume = 0f };
                    _stream.Start();
                    _current = path;
                    _errorLogged = false;
                    GentlemansClubPlugin.Log.LogInfo("Club music: " + Path.GetFileName(path) + " (" + _stream.Describe() + ").");
                    return;
                }
                catch (Exception e)
                {
                    GentlemansClubPlugin.Log.LogWarning("Skipping " + Path.GetFileName(path) + ": " + e.Message);
                    _stream?.Dispose();
                    _stream = null;
                }
            }
        }

        private void OnDestroy() => _stream?.Dispose();
    }
}
