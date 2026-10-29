using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GentlemansClub
{
    // Plays an MP3 (on a loop, or once) without Unity audio (the game turns it off) and without DirectShow
    // (MCI's MP3 player fails on some PCs): Windows' built-in ACM MP3 codec decodes it, and the PCM
    // is streamed to the default output through waveOut. Volume is applied to the samples, so the
    // game's own sound is never touched. 64-bit Windows only (the header layouts below are x64).
    internal sealed class Mp3Stream : IDisposable
    {
        public volatile float Volume;   // 0..1, read by the audio thread
        public volatile bool Paused;
        public bool Repeat = true;        // false: play once, then Ended goes true and it plays silence
        public volatile bool Ended;

        readonly byte[] _file;
        readonly int _dataStart;
        readonly int _sampleRate, _channels, _bitrate;
        IntPtr _acm, _acmHeader, _acmSrc, _acmDst;
        int _srcSize, _dstSize, _srcFill, _readPos;
        IntPtr _waveOut;
        readonly IntPtr[] _waveHeaders = new IntPtr[4];
        readonly IntPtr[] _waveBuffers = new IntPtr[4];
        const int WaveBufferBytes = 32768;
        System.Threading.Thread _thread;
        volatile bool _stop;
        bool _outputPaused;
        readonly byte[] _pending = new byte[1 << 20];
        int _pendingLen;

        public Mp3Stream(string path)
        {
            _file = File.ReadAllBytes(path);
            _dataStart = SkipId3(_file);
            int frame = FindFrame(_file, _dataStart);
            if (frame < 0) throw new InvalidDataException("No MP3 frame found");
            ParseHeader(_file, frame, out _sampleRate, out _channels, out _bitrate);
            _dataStart = frame;
            _readPos = _dataStart;
        }

        public string Describe() { return _sampleRate + " Hz, " + _channels + " ch, " + _bitrate / 1000 + " kbps"; }

        // ---- decoding ---------------------------------------------------------------------------

        public void OpenDecoder()
        {
            int blockSize = 144 * _bitrate / _sampleRate;
            var src = new byte[30];
            W16(src, 0, 0x55); W16(src, 2, _channels); W32(src, 4, _sampleRate); W32(src, 8, _bitrate / 8);
            W16(src, 12, 1); W16(src, 14, 0); W16(src, 16, 12);
            W16(src, 18, 1); W32(src, 20, 2); W16(src, 24, blockSize); W16(src, 26, 1); W16(src, 28, 1393);
            var dst = Pcm();
            int err = acmStreamOpen(out _acm, IntPtr.Zero, src, dst, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0);
            if (err != 0) throw new IOException("acmStreamOpen failed: " + err);
            _srcSize = blockSize * 32;
            err = acmStreamSize(_acm, _srcSize, out _dstSize, 0);
            if (err != 0) throw new IOException("acmStreamSize failed: " + err);
            _acmSrc = Marshal.AllocHGlobal(_srcSize);
            _acmDst = Marshal.AllocHGlobal(_dstSize);
            _acmHeader = Marshal.AllocHGlobal(128);
            for (int i = 0; i < 128; i += 4) Marshal.WriteInt32(_acmHeader, i, 0);
            Marshal.WriteInt32(_acmHeader, 0, 128);              // cbStruct
            Marshal.WriteIntPtr(_acmHeader, 16, _acmSrc);        // pbSrc
            Marshal.WriteInt32(_acmHeader, 24, _srcSize);        // cbSrcLength
            Marshal.WriteIntPtr(_acmHeader, 40, _acmDst);        // pbDst
            Marshal.WriteInt32(_acmHeader, 48, _dstSize);        // cbDstLength
            err = acmStreamPrepareHeader(_acm, _acmHeader, 0);
            if (err != 0) throw new IOException("acmStreamPrepareHeader failed: " + err);
        }

        bool AtEnd => !Repeat && _readPos >= _file.Length && _srcFill == 0;

        // Decodes the next chunk of the file (looping at the end); returns PCM bytes written to buffer.
        public int DecodeChunk(byte[] buffer)
        {
            int want = _srcSize - _srcFill;
            int avail = _file.Length - _readPos;
            int take = Math.Min(want, avail);
            if (take > 0) { Marshal.Copy(_file, _readPos, _acmSrc + _srcFill, take); _readPos += take; _srcFill += take; }
            if (_readPos >= _file.Length && Repeat) _readPos = _dataStart;
            Marshal.WriteInt32(_acmHeader, 24, _srcFill);
            int err = acmStreamConvert(_acm, _acmHeader, 0x4); // ACM_STREAMCONVERTF_BLOCKALIGN
            if (err != 0) throw new IOException("acmStreamConvert failed: " + err);
            int used = Marshal.ReadInt32(_acmHeader, 28);
            int produced = Marshal.ReadInt32(_acmHeader, 52);
            int left = _srcFill - used;
            if (left > 0 && used > 0)
            {
                var tmp = new byte[left];
                Marshal.Copy(_acmSrc + used, tmp, 0, left);
                Marshal.Copy(tmp, 0, _acmSrc, left);
            }
            _srcFill = used > 0 ? left : 0; // a chunk that decodes nothing is dropped, so we never stall
            int n = Math.Min(produced, buffer.Length);
            Marshal.Copy(_acmDst, buffer, 0, n);
            return n;
        }

        // ---- playback ---------------------------------------------------------------------------

        public void Start()
        {
            OpenDecoder();
            int err = waveOutOpen(out _waveOut, -1, Pcm(), IntPtr.Zero, IntPtr.Zero, 0);
            if (err != 0) throw new IOException("waveOutOpen failed: " + err);
            for (int i = 0; i < _waveHeaders.Length; i++)
            {
                _waveBuffers[i] = Marshal.AllocHGlobal(WaveBufferBytes);
                _waveHeaders[i] = Marshal.AllocHGlobal(48);
                for (int b = 0; b < 48; b += 4) Marshal.WriteInt32(_waveHeaders[i], b, 0);
                Marshal.WriteInt32(_waveHeaders[i], 24, 1); // WHDR_DONE: free to fill
            }
            _thread = new System.Threading.Thread(Loop) { IsBackground = true, Name = "GentlemansClub music" };
            _thread.Start();
        }

        void Loop()
        {
            var chunk = new byte[Math.Max(_dstSize, 65536)];
            var fill = new byte[WaveBufferBytes];
            try
            {
                while (!_stop)
                {
                    if (Paused != _outputPaused)
                    {
                        if (Paused) waveOutPause(_waveOut); else waveOutRestart(_waveOut);
                        _outputPaused = Paused;
                    }
                    bool wrote = false;
                    for (int i = 0; i < _waveHeaders.Length && !Paused; i++)
                    {
                        var h = _waveHeaders[i];
                        if ((Marshal.ReadInt32(h, 24) & 1) == 0) continue; // still playing
                        if ((Marshal.ReadInt32(h, 24) & 2) != 0) waveOutUnprepareHeader(_waveOut, h, 48);
                        while (_pendingLen < WaveBufferBytes)
                        {
                            int n = DecodeChunk(chunk);
                            if (n == 0 && AtEnd)
                            {
                                // Song over: pad with silence until the player moves on.
                                Array.Clear(_pending, _pendingLen, WaveBufferBytes - _pendingLen);
                                _pendingLen = WaveBufferBytes;
                                Ended = true;
                                break;
                            }
                            Buffer.BlockCopy(chunk, 0, _pending, _pendingLen, n);
                            _pendingLen += n;
                        }
                        Buffer.BlockCopy(_pending, 0, fill, 0, WaveBufferBytes);
                        Buffer.BlockCopy(_pending, WaveBufferBytes, _pending, 0, _pendingLen - WaveBufferBytes);
                        _pendingLen -= WaveBufferBytes;
                        float v = Math.Max(0f, Math.Min(1f, Volume));
                        for (int s = 0; s + 1 < WaveBufferBytes; s += 2)
                        {
                            int sample = (short)(fill[s] | (fill[s + 1] << 8));
                            sample = (int)(sample * v);
                            fill[s] = (byte)sample; fill[s + 1] = (byte)(sample >> 8);
                        }
                        Marshal.Copy(fill, 0, _waveBuffers[i], WaveBufferBytes);
                        Marshal.WriteIntPtr(h, 0, _waveBuffers[i]);
                        Marshal.WriteInt32(h, 8, WaveBufferBytes);
                        Marshal.WriteInt32(h, 24, 0);
                        waveOutPrepareHeader(_waveOut, h, 48);
                        waveOutWrite(_waveOut, h, 48);
                        wrote = true;
                    }
                    if (!wrote) System.Threading.Thread.Sleep(10);
                }
            }
            catch (Exception e) { Error = e.Message; }
        }

        public volatile string Error;

        public void Dispose()
        {
            _stop = true;
            if (_thread != null) _thread.Join(500);
            if (_waveOut != IntPtr.Zero)
            {
                waveOutReset(_waveOut);
                foreach (var h in _waveHeaders) if (h != IntPtr.Zero) waveOutUnprepareHeader(_waveOut, h, 48);
                waveOutClose(_waveOut);
                _waveOut = IntPtr.Zero;
            }
            foreach (var p in _waveHeaders) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            foreach (var p in _waveBuffers) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            if (_acm != IntPtr.Zero)
            {
                Marshal.WriteInt32(_acmHeader, 24, _srcSize);
                acmStreamUnprepareHeader(_acm, _acmHeader, 0);
                acmStreamClose(_acm, 0);
                _acm = IntPtr.Zero;
            }
            if (_acmHeader != IntPtr.Zero) Marshal.FreeHGlobal(_acmHeader);
            if (_acmSrc != IntPtr.Zero) Marshal.FreeHGlobal(_acmSrc);
            if (_acmDst != IntPtr.Zero) Marshal.FreeHGlobal(_acmDst);
            _acmHeader = _acmSrc = _acmDst = IntPtr.Zero;
        }

        // ---- MP3 file helpers -------------------------------------------------------------------

        static int SkipId3(byte[] f)
        {
            if (f.Length > 10 && f[0] == 'I' && f[1] == 'D' && f[2] == '3')
                return 10 + ((f[6] & 0x7f) << 21 | (f[7] & 0x7f) << 14 | (f[8] & 0x7f) << 7 | (f[9] & 0x7f));
            return 0;
        }

        static int FindFrame(byte[] f, int from)
        {
            for (int i = from; i + 4 < f.Length; i++)
                if (f[i] == 0xFF && (f[i + 1] & 0xE0) == 0xE0 && ((f[i + 1] >> 1) & 3) == 1 && ((f[i + 2] >> 4) & 0xF) != 0xF) return i;
            return -1;
        }

        static void ParseHeader(byte[] f, int i, out int rate, out int channels, out int bitrate)
        {
            int version = (f[i + 1] >> 3) & 3; // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
            int[] br1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
            int[] br2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
            int bi = (f[i + 2] >> 4) & 0xF, si = (f[i + 2] >> 2) & 3;
            int[] r1 = { 44100, 48000, 32000 };
            rate = r1[si] / (version == 3 ? 1 : version == 2 ? 2 : 4);
            bitrate = (version == 3 ? br1[bi] : br2[bi]) * 1000;
            channels = ((f[i + 3] >> 6) & 3) == 3 ? 1 : 2;
        }

        byte[] Pcm()
        {
            var w = new byte[18];
            W16(w, 0, 1); W16(w, 2, _channels); W32(w, 4, _sampleRate); W32(w, 8, _sampleRate * _channels * 2);
            W16(w, 12, _channels * 2); W16(w, 14, 16); W16(w, 16, 0);
            return w;
        }

        static void W16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        static void W32(byte[] b, int o, int v) { W16(b, o, v); W16(b, o + 2, v >> 16); }

        [DllImport("msacm32.dll")] static extern int acmStreamOpen(out IntPtr has, IntPtr had, byte[] src, byte[] dst, IntPtr filter, IntPtr cb, IntPtr inst, int flags);
        [DllImport("msacm32.dll")] static extern int acmStreamSize(IntPtr has, int input, out int output, int flags);
        [DllImport("msacm32.dll")] static extern int acmStreamPrepareHeader(IntPtr has, IntPtr hdr, int flags);
        [DllImport("msacm32.dll")] static extern int acmStreamUnprepareHeader(IntPtr has, IntPtr hdr, int flags);
        [DllImport("msacm32.dll")] static extern int acmStreamConvert(IntPtr has, IntPtr hdr, int flags);
        [DllImport("msacm32.dll")] static extern int acmStreamClose(IntPtr has, int flags);
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, int device, byte[] format, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
    }
}
