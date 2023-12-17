﻿using SharpDX;
using SharpDX.Multimedia;
using SharpDX.XAudio2;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FamiStudio
{
    public class XAudio2Stream : IAudioStream
    {
        private XAudio2 xaudio2;
        private MasteringVoice masteringVoice;

        // Regular voice for streaming NES audio data.
        private WaveFormat waveFormat;
        private SourceVoice sourceVoice;
        private AudioBuffer[] audioBuffersRing;
        private DataPointer[] memBuffers;
        private Semaphore bufferSemaphore;
        private ManualResetEvent quitEvent;
        private GetBufferDataCallback bufferFill;
        private StreamStartingCallback streamStarting;
        private Task playingTask;
        private short[] lastSamples = null;
        private int lastSamplesOffset = 0;
        private int bufferPrefillCount;
        private int queuedBufferCount;
        //private int frameSizeInBytes;

        // Immediate voice for playing raw PCM data (used by DPCM editor).
        private SourceVoice immediateVoice;
        private AudioBuffer immediateAudioBuffer;
        private bool immediateDonePlaying;

        public XAudio2Stream(int rate, bool stereo, int bufferSizeMs, GetBufferDataCallback bufferFillCallback, StreamStartingCallback streamStartCallback)
        {
            xaudio2 = new XAudio2();

            //xaudio2 = new XAudio2(XAudio2Version.Version27); // To simulate Windows 7 behavior.
            //xaudio2.CriticalError += Xaudio2_CriticalError;

            var numBuffers = Math.Max(4, bufferSizeMs / 25);
            var bufferSizeBytes = Utils.RoundUp(rate * bufferSizeMs / 1000 * sizeof(short) * (stereo ? 2 : 1) / numBuffers, sizeof(short) * 2);

            masteringVoice = new MasteringVoice(xaudio2);
            waveFormat = new WaveFormat(rate, 16, stereo ? 2 : 1);
            audioBuffersRing = new AudioBuffer[numBuffers];
            memBuffers = new DataPointer[audioBuffersRing.Length];

            for (int i = 0; i < audioBuffersRing.Length; i++)
            {
                audioBuffersRing[i] = new AudioBuffer();
                memBuffers[i].Size = bufferSizeBytes;
                memBuffers[i].Pointer = Utilities.AllocateMemory(memBuffers[i].Size);
            }

            bufferFill = bufferFillCallback;
            streamStarting = streamStartCallback;
            quitEvent = new ManualResetEvent(false);
        }

        private void Xaudio2_CriticalError(object sender, ErrorEventArgs e)
        {
            Debug.WriteLine("CRITICAL ERROR!");
        }

        private void SourceVoice_BufferEnd(IntPtr obj)
        {
            bufferSemaphore.Release();
            Interlocked.Decrement(ref queuedBufferCount);
        }

        public bool IsPlaying => sourceVoice != null;

        public void Start()
        {
            Debug.Assert(sourceVoice == null);
            Debug.Assert(bufferSemaphore == null);

            lastSamplesOffset = 0;
            queuedBufferCount = 0;
            bufferSemaphore = new Semaphore(audioBuffersRing.Length, audioBuffersRing.Length);
            bufferPrefillCount = audioBuffersRing.Length;

            sourceVoice = new SourceVoice(xaudio2, waveFormat);
            sourceVoice.BufferEnd += SourceVoice_BufferEnd;

            quitEvent.Reset();

            playingTask = Task.Factory.StartNew(PlayAsync, TaskCreationOptions.LongRunning);
        }

        public void Stop(bool abort)
        {
            if (playingTask != null)
            {
                quitEvent.Set();
                playingTask.Wait();
                playingTask = null;
            }

            if (sourceVoice != null)
            {
                // Wait until all buffers are consumed on non-looping songs.
                if (!abort)
                {
                    while (queuedBufferCount > 0)
                        Thread.Sleep(1);
                }

                sourceVoice.Stop();
                sourceVoice.FlushSourceBuffers();
                sourceVoice.DestroyVoice();
                sourceVoice.BufferEnd -= SourceVoice_BufferEnd;
                sourceVoice.Dispose();
                sourceVoice = null;
            }

            if (bufferSemaphore != null)
            {
                bufferSemaphore.Dispose();
                bufferSemaphore = null;
            }
        }

        public void Dispose()
        {
            Stop(true);
            StopImmediate();

            for (int i = 0; i < audioBuffersRing.Length; i++)
            {
                Utilities.FreeMemory(memBuffers[i].Pointer);
                memBuffers[i].Pointer = IntPtr.Zero;
            }

            masteringVoice.DestroyVoice();
            masteringVoice.Dispose();
            masteringVoice = null;

            xaudio2.Dispose();
            xaudio2 = null;
        }

        private void PlayAsync()
        {
#if DEBUG
            try
            {
#endif
                var nextBuffer = 0;
                var waitEvents = new WaitHandle[] { quitEvent, bufferSemaphore };
                var done = false;

                while (!done)
                {
                    int idx = WaitHandle.WaitAny(waitEvents);

                    if (idx == 0)
                    {
                        break;
                    }

                    var buffer = memBuffers[nextBuffer];
                    var bufferSampleCount = buffer.Size / sizeof(short);
                    var bufferPtr = buffer.Pointer;

                    do
                    {
                        if (lastSamplesOffset == 0)
                        {
                            while (true)
                            {
                                lastSamples = bufferFill(out done);
                                
                                // If we are done, pad the last buffer with zeroes so the stream can start
                                // for super-short (ex: 1-frame) non-looping songs.
                                if (done)
                                    lastSamples = new short[bufferSampleCount - lastSamplesOffset];

                                if (lastSamples != null)
                                    break;
                                
                                if (quitEvent.WaitOne(0))
                                    return;
                                
                                Thread.Sleep(1);
                            }
                        }

                        var numSamplesToCopy = Math.Min(bufferSampleCount, lastSamples.Length - lastSamplesOffset);
                        Marshal.Copy(lastSamples, lastSamplesOffset, bufferPtr, numSamplesToCopy);

                        lastSamplesOffset += numSamplesToCopy;
                        if (lastSamplesOffset == lastSamples.Length)
                            lastSamplesOffset = 0;

                        bufferPtr = IntPtr.Add(bufferPtr, numSamplesToCopy * sizeof(short));
                        bufferSampleCount = bufferSampleCount - numSamplesToCopy;
                    }
                    while (bufferSampleCount != 0);

                    // MATTT : There is a "End of stream" flag, we should use that.
                    audioBuffersRing[nextBuffer].AudioDataPointer = buffer.Pointer;
                    audioBuffersRing[nextBuffer].AudioBytes = buffer.Size;

                    Interlocked.Increment(ref queuedBufferCount);
                    sourceVoice.SubmitSourceBuffer(audioBuffersRing[nextBuffer], null);

                    // Only start the stream once the buffers are pre-filled.
                    if ((bufferPrefillCount > 0 && --bufferPrefillCount == 0) || done)
                    {
                        if (!done)
                            streamStarting();

                        sourceVoice.Start();
                    }

                    nextBuffer = ++nextBuffer % audioBuffersRing.Length;
                }
#if DEBUG
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
                if (Debugger.IsAttached)
                    Debugger.Break();
            }
#endif
        }

        public void PlayImmediate(short[] data, int sampleRate, float volume, int channel = 0)
        {
            Debug.Assert(Platform.IsInMainThread());

            StopImmediate();

            immediateDonePlaying = false;

            immediateAudioBuffer = new AudioBuffer();
            immediateAudioBuffer.AudioDataPointer = Utilities.AllocateMemory(data.Length * sizeof(short));
            immediateAudioBuffer.AudioBytes = data.Length * sizeof(short);
            Marshal.Copy(data, 0, immediateAudioBuffer.AudioDataPointer, data.Length);

            var waveFormat = new WaveFormat(sampleRate, 16, 1);

            immediateVoice = new SourceVoice(xaudio2, waveFormat);
            immediateVoice.BufferEnd += ImmediateVoice_BufferEnd;
            immediateVoice.SetVolume(volume);
            immediateVoice.SubmitSourceBuffer(immediateAudioBuffer, null);
            immediateVoice.Start();
        }

        public void StopImmediate()
        {
            Debug.Assert(Platform.IsInMainThread());

            if (immediateVoice != null)
            {
                immediateVoice.Stop();
                immediateVoice.FlushSourceBuffers();
                immediateVoice.BufferEnd -= ImmediateVoice_BufferEnd;
                immediateVoice.DestroyVoice();
                immediateVoice.Dispose();
                immediateVoice = null;

                Utilities.FreeMemory(immediateAudioBuffer.AudioDataPointer);
                immediateAudioBuffer = null;
                immediateDonePlaying = true;
            }
        }

        private void ImmediateVoice_BufferEnd(IntPtr obj)
        {
            immediateDonePlaying = true;
        }

        public int ImmediatePlayPosition
        {
            get
            {
                if (immediateVoice != null && !immediateDonePlaying)
                {
                    return (int)immediateVoice.State.SamplesPlayed;
                }
                else
                {
                    return -1;
                }
            }
        }

        public static bool TryDetectXAudio2()
        {
            try
            {
                var xaudio2 = new XAudio2();

                xaudio2.Dispose();
                xaudio2 = null;

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
