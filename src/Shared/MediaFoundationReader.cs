// Decodes compressed recordings (MP3, M4A/AAC, WMA) with Windows Media Foundation, the decoders built
// into Windows, so check-recordings needs no extra libraries. Windows "N" editions need the Media
// Feature Pack for these decoders.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BeepTone
{
    public static class RecordingFiles
    {
        public static readonly string[] Extensions = new string[] { ".wav", ".mp3", ".m4a", ".wma" };

        public static bool IsRecording(string path)
        {
            return Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;
        }

        // WAV goes through the plain reader; anything it cannot read, and every other format,
        // goes through Media Foundation.
        public static IFrameSource Open(string path)
        {
            if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                try { return new WavReader(path); }
                catch (InvalidDataException) { }
            }
            return new MediaFoundationReader(path);
        }
    }

    public sealed class MediaFoundationReader : IFrameSource
    {
        const int MfVersion = 0x00020070;
        const uint FirstAudioStream = 0xFFFFFFFD;
        const uint AllStreams = 0xFFFFFFFE;
        const uint EndOfStream = 0x2;
        static Guid MajorTypeKey = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static Guid SubtypeKey = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static Guid ChannelsKey = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        static Guid RateKey = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        static Guid AudioMajorType = new Guid("73647561-0000-0010-8000-00AA00389B71");
        static Guid FloatSubtype = new Guid("00000003-0000-0010-8000-00AA00389B71");
        static bool started;
        static readonly object Gate = new object();

        IMFSourceReader reader;
        float[] samples = new float[0];
        int count;
        int position;
        bool ended;

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }

        public MediaFoundationReader(string path)
        {
            Startup();
            Check(MFCreateSourceReaderFromURL(Path.GetFullPath(path), IntPtr.Zero, out reader));
            Check(reader.SetStreamSelection(AllStreams, false));
            Check(reader.SetStreamSelection(FirstAudioStream, true));
            // Ask for 32-bit float at the file's own rate and channel count. Naming both matters: left to
            // itself, the AAC decoder assumes a stream might double its rate and add a channel, and then
            // labels 16 kHz mono output as 32 kHz stereo.
            int nativeChannels = 0, nativeRate = 0;
            IMFMediaType native;
            if (reader.GetNativeMediaType(FirstAudioStream, 0, out native) >= 0 && native != null)
            {
                try
                {
                    native.GetUINT32(ref ChannelsKey, out nativeChannels);
                    native.GetUINT32(ref RateKey, out nativeRate);
                }
                finally { Marshal.ReleaseComObject(native); }
            }
            if (!RequestFloat(nativeChannels, nativeRate)) Check(SetFloat(0, 0));
            IMFMediaType actual;
            Check(reader.GetCurrentMediaType(FirstAudioStream, out actual));
            try
            {
                int channels, rate;
                Check(actual.GetUINT32(ref ChannelsKey, out channels));
                Check(actual.GetUINT32(ref RateKey, out rate));
                Channels = channels;
                SampleRate = rate;
            }
            finally { Marshal.ReleaseComObject(actual); }
            if (Channels < 1 || SampleRate < 1000) throw new InvalidDataException("the recording has no usable audio");
        }

        bool RequestFloat(int channels, int rate)
        {
            return channels > 0 && rate > 0 && SetFloat(channels, rate) >= 0;
        }

        int SetFloat(int channels, int rate)
        {
            IMFMediaType wanted;
            Check(MFCreateMediaType(out wanted));
            try
            {
                Check(wanted.SetGUID(ref MajorTypeKey, ref AudioMajorType));
                Check(wanted.SetGUID(ref SubtypeKey, ref FloatSubtype));
                if (channels > 0) Check(wanted.SetUINT32(ref ChannelsKey, channels));
                if (rate > 0) Check(wanted.SetUINT32(ref RateKey, rate));
                return reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, wanted);
            }
            finally { Marshal.ReleaseComObject(wanted); }
        }

        public bool ReadFrame(float[] frame)
        {
            while (position + Channels > count)
            {
                if (ended) return false;
                ReadNextSample();
            }
            for (int c = 0; c < Channels; c++) frame[c] = samples[position + c];
            position += Channels;
            return true;
        }

        void ReadNextSample()
        {
            uint streamIndex, flags;
            long timestamp;
            IMFSample sample;
            Check(reader.ReadSample(FirstAudioStream, 0, out streamIndex, out flags, out timestamp, out sample));
            if ((flags & EndOfStream) != 0) ended = true;
            if (sample == null) return;
            try
            {
                IMFMediaBuffer buffer;
                Check(sample.ConvertToContiguousBuffer(out buffer));
                try
                {
                    IntPtr data;
                    int max, length;
                    Check(buffer.Lock(out data, out max, out length));
                    try
                    {
                        int n = length / 4;
                        if (samples.Length < n) samples = new float[n];
                        Marshal.Copy(data, samples, 0, n);
                        count = n;
                        position = 0;
                    }
                    finally { buffer.Unlock(); }
                }
                finally { Marshal.ReleaseComObject(buffer); }
            }
            finally { Marshal.ReleaseComObject(sample); }
        }

        public void Dispose()
        {
            if (reader != null) Marshal.ReleaseComObject(reader);
            reader = null;
        }

        static void Startup()
        {
            lock (Gate)
            {
                if (started) return;
                WasapiNative.ComInit();
                try { Check(MFStartup(MfVersion, 1)); }
                catch (DllNotFoundException)
                {
                    throw new InvalidDataException("Windows Media Foundation is not installed. On Windows N editions, install the Media Feature Pack to check MP3, M4A and WMA recordings.");
                }
                started = true;
            }
        }

        static void Check(int hr)
        {
            if (hr >= 0) return;
            switch ((uint)hr)
            {
                case 0xC00D36C4: throw new InvalidDataException("Windows cannot read this file type");
                case 0xC00D5212: throw new InvalidDataException("Windows has no decoder for this format; on Windows N editions install the Media Feature Pack");
                case 0x80070002:
                case 0x80070003: throw new FileNotFoundException("the file was not found");
            }
            throw new InvalidDataException("not a recording Windows can read (0x" + hr.ToString("X8") + ")");
        }

        [DllImport("mfplat.dll")]
        static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll")]
        static extern int MFCreateMediaType(out IMFMediaType mediaType);

        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IMFSourceReader reader);

        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);
            [PreserveSig] int SetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
            [PreserveSig] int GetNativeMediaType(uint streamIndex, uint typeIndex, out IMFMediaType mediaType);
            [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType mediaType);
            [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType mediaType);
            [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, IntPtr position);
            [PreserveSig] int ReadSample(uint streamIndex, uint controlFlags, out uint actualStreamIndex, out uint streamFlags, out long timestamp, out IMFSample sample);
            [PreserveSig] int Flush(uint streamIndex);
            [PreserveSig] int GetServiceForStream(uint streamIndex, ref Guid service, ref Guid riid, out IntPtr obj);
            [PreserveSig] int GetPresentationAttribute(uint streamIndex, ref Guid attribute, IntPtr value);
        }

        // IMFMediaType starts with the 30 IMFAttributes methods; only their order matters here.
        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaType
        {
            [PreserveSig] int GetItem();
            [PreserveSig] int GetItemType();
            [PreserveSig] int CompareItem();
            [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32(ref Guid key, out int value);
            [PreserveSig] int GetUINT64();
            [PreserveSig] int GetDouble();
            [PreserveSig] int GetGUID();
            [PreserveSig] int GetStringLength();
            [PreserveSig] int GetString();
            [PreserveSig] int GetAllocatedString();
            [PreserveSig] int GetBlobSize();
            [PreserveSig] int GetBlob();
            [PreserveSig] int GetAllocatedBlob();
            [PreserveSig] int GetUnknown();
            [PreserveSig] int SetItem();
            [PreserveSig] int DeleteItem();
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid key, int value);
            [PreserveSig] int SetUINT64();
            [PreserveSig] int SetDouble();
            [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSample
        {
            [PreserveSig] int GetItem();
            [PreserveSig] int GetItemType();
            [PreserveSig] int CompareItem();
            [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32();
            [PreserveSig] int GetUINT64();
            [PreserveSig] int GetDouble();
            [PreserveSig] int GetGUID();
            [PreserveSig] int GetStringLength();
            [PreserveSig] int GetString();
            [PreserveSig] int GetAllocatedString();
            [PreserveSig] int GetBlobSize();
            [PreserveSig] int GetBlob();
            [PreserveSig] int GetAllocatedBlob();
            [PreserveSig] int GetUnknown();
            [PreserveSig] int SetItem();
            [PreserveSig] int DeleteItem();
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32();
            [PreserveSig] int SetUINT64();
            [PreserveSig] int SetDouble();
            [PreserveSig] int SetGUID();
            [PreserveSig] int SetString();
            [PreserveSig] int SetBlob();
            [PreserveSig] int SetUnknown();
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount();
            [PreserveSig] int GetItemByIndex();
            [PreserveSig] int CopyAllItems();
            [PreserveSig] int GetSampleFlags();
            [PreserveSig] int SetSampleFlags();
            [PreserveSig] int GetSampleTime();
            [PreserveSig] int SetSampleTime();
            [PreserveSig] int GetSampleDuration();
            [PreserveSig] int SetSampleDuration();
            [PreserveSig] int GetBufferCount();
            [PreserveSig] int GetBufferByIndex();
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            [PreserveSig] int Unlock();
        }
    }
}
