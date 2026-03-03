using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WinPanX.Agent.Runtime;
using WinPanX.Core.Contracts;

namespace WinPanX.Agent.Audio;

internal sealed class WasapiMixer : IMixer
{
    private readonly object _sync = new();
    private readonly float[] _slotPans = new float[9];
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _outputDevice;
    private WasapiOut? _output;
    private MasterMixWaveProvider? _provider;
    private List<SlotInput> _slots = [];
    private bool _started;
    private bool _disposed;
    private long _totalFramesMixed;
    private long _underrunCount;
    private long _overrunCount;
    private float _peakMasterLeft;
    private float _peakMasterRight;

    public Task StartAsync(MixerStartRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_started)
            {
                throw new InvalidOperationException("Mixer already started.");
            }

            if (request.InputSlots.Count == 0)
            {
                throw new InvalidOperationException("Mixer requires at least one input slot.");
            }

            _enumerator = new MMDeviceEnumerator();
            _outputDevice = ResolveOutputDevice(request.OutputEndpointId);

            var outputFormat = _outputDevice.AudioClient.MixFormat;
            ValidateOutputFormat(outputFormat);

            _slots = CreateSlots(request.InputSlots, request.FramesPerBuffer, outputFormat.SampleRate);
            _provider = new MasterMixWaveProvider(
                this,
                _slots,
                outputFormat);

            _output = new WasapiOut(_outputDevice, AudioClientShareMode.Shared, true, 10);
            _output.Init(_provider);

            StartCaptures();
            _output.Play();
            _started = true;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_started)
            {
                return Task.CompletedTask;
            }

            StopCaptures();
            _output?.Stop();
            _output?.Dispose();
            _output = null;

            _outputDevice?.Dispose();
            _outputDevice = null;

            _enumerator?.Dispose();
            _enumerator = null;

            _provider = null;
            _slots.Clear();
            _started = false;
        }

        return Task.CompletedTask;
    }

    public void SetDedicatedSlotPan(int slotIndex, float pan)
    {
        if (slotIndex < 1 || slotIndex > 7)
        {
            return;
        }

        _slotPans[slotIndex] = Math.Clamp(pan, -1.0f, 1.0f);
    }

    public void SetOverflowPan(float pan)
    {
        _slotPans[8] = Math.Clamp(pan, -1.0f, 1.0f);
    }

    public Task SwitchOutputDeviceAsync(string outputEndpointId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_started || _provider is null || _enumerator is null)
            {
                return Task.CompletedTask;
            }

            var newOutput = ResolveOutputDevice(outputEndpointId);
            var newWasapi = new WasapiOut(newOutput, AudioClientShareMode.Shared, true, 10);
            newWasapi.Init(_provider);
            newWasapi.Play();

            _output?.Stop();
            _output?.Dispose();
            _outputDevice?.Dispose();

            _output = newWasapi;
            _outputDevice = newOutput;
        }

        return Task.CompletedTask;
    }

    public MixerStats GetStats()
    {
        return new MixerStats(
            TotalFramesMixed: Interlocked.Read(ref _totalFramesMixed),
            UnderrunCount: Interlocked.Read(ref _underrunCount),
            OverrunCount: Interlocked.Read(ref _overrunCount),
            PeakMasterLeft: _peakMasterLeft,
            PeakMasterRight: _peakMasterRight,
            CapturedUtc: DateTime.UtcNow);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
        }

        await StopAsync(CancellationToken.None);
        _disposed = true;
    }

    private MMDevice ResolveOutputDevice(string outputEndpointId)
    {
        if (string.Equals(outputEndpointId, "default", StringComparison.OrdinalIgnoreCase))
        {
            return _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        return _enumerator!.GetDevice(outputEndpointId);
    }

    private List<SlotInput> CreateSlots(
        IReadOnlyList<EndpointDescriptor> inputSlots,
        int framesPerBuffer,
        int mixSampleRate)
    {
        var capacityFrames = Math.Max(framesPerBuffer * 200, mixSampleRate);
        var slots = new List<SlotInput>(inputSlots.Count);

        foreach (var descriptor in inputSlots.OrderBy(s => s.SlotIndex))
        {
            var device = _enumerator!.GetDevice(descriptor.EndpointId);
            var capture = new WasapiLoopbackCapture(device);

            if (capture.WaveFormat.SampleRate != mixSampleRate)
            {
                // MVP limitation: slot capture and output must share sample rate.
                // A proper resampler stage should be added for heterogeneous endpoint formats.
                throw new NotSupportedException(
                    $"Slot {descriptor.SlotIndex} sample rate {capture.WaveFormat.SampleRate} does not match output rate {mixSampleRate}. Resampling is not implemented in this MVP.");
            }

            ValidateCaptureFormat(descriptor.SlotIndex, capture.WaveFormat);

            var slot = new SlotInput(
                descriptor.SlotIndex,
                descriptor.EndpointId,
                device,
                capture,
                new StereoCircularBuffer(capacityFrames));

            capture.DataAvailable += (_, args) => OnCaptureData(slot, args);
            capture.RecordingStopped += (_, args) =>
            {
                if (args.Exception is not null)
                {
                    SimpleLog.Error($"Slot {slot.SlotIndex} capture stopped: {args.Exception.Message}");
                }
            };

            slots.Add(slot);
        }

        return slots;
    }

    private static void ValidateOutputFormat(WaveFormat format)
    {
        var normalized = NormalizeWaveFormat(format);
        var supported = normalized.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat when normalized.BitsPerSample == 32 => true,
            WaveFormatEncoding.Pcm when normalized.BitsPerSample is 16 or 24 or 32 => true,
            _ => false
        };

        if (!supported)
        {
            throw new NotSupportedException(
                $"Unsupported output mix format {normalized.Encoding} {normalized.BitsPerSample}-bit (source {format.Encoding}).");
        }
    }

    private static void ValidateCaptureFormat(int slotIndex, WaveFormat format)
    {
        var normalized = NormalizeWaveFormat(format);
        var supported = normalized.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat when normalized.BitsPerSample == 32 => true,
            WaveFormatEncoding.Pcm when normalized.BitsPerSample is 16 or 24 or 32 => true,
            _ => false
        };

        if (!supported)
        {
            throw new NotSupportedException(
                $"Unsupported capture format on slot {slotIndex}: {normalized.Encoding} {normalized.BitsPerSample}-bit (source {format.Encoding}).");
        }
    }

    private void StartCaptures()
    {
        foreach (var slot in _slots)
        {
            slot.Capture.StartRecording();
        }
    }

    private void StopCaptures()
    {
        foreach (var slot in _slots)
        {
            try
            {
                slot.Capture.StopRecording();
            }
            catch
            {
            }

            slot.Capture.Dispose();
            slot.Device.Dispose();
        }
    }

    private void OnCaptureData(SlotInput slot, WaveInEventArgs args)
    {
        try
        {
            var format = slot.Capture.WaveFormat;
            var normalized = NormalizeWaveFormat(format);
            var bytesPerSample = format.BitsPerSample / 8;
            var bytesPerFrame = format.BlockAlign;
            if (bytesPerSample <= 0 || bytesPerFrame <= 0)
            {
                return;
            }

            var frameCount = args.BytesRecorded / bytesPerFrame;
            var data = args.Buffer.AsSpan(0, frameCount * bytesPerFrame);

            for (var frame = 0; frame < frameCount; frame++)
            {
                var frameOffset = frame * bytesPerFrame;
                var left = ReadSample(data, frameOffset, bytesPerSample, normalized.Encoding, normalized.BitsPerSample);
                var right = format.Channels > 1
                    ? ReadSample(data, frameOffset + bytesPerSample, bytesPerSample, normalized.Encoding, normalized.BitsPerSample)
                    : left;

                if (slot.Buffer.Write(left, right))
                {
                    Interlocked.Increment(ref _overrunCount);
                }
            }
        }
        catch (Exception ex)
        {
            SimpleLog.Error($"Capture callback error on slot {slot.SlotIndex}: {ex.Message}");
        }
    }

    private static float ReadSample(
        ReadOnlySpan<byte> data,
        int offset,
        int bytesPerSample,
        WaveFormatEncoding encoding,
        int bitsPerSample)
    {
        return encoding switch
        {
            WaveFormatEncoding.IeeeFloat when bitsPerSample == 32 => BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4))),
            WaveFormatEncoding.Pcm when bitsPerSample == 16 => BinaryPrimitives.ReadInt16LittleEndian(
                data.Slice(offset, 2)) / 32768.0f,
            WaveFormatEncoding.Pcm when bitsPerSample == 24 => ReadPcm24(data.Slice(offset, bytesPerSample)),
            WaveFormatEncoding.Pcm when bitsPerSample == 32 => BinaryPrimitives.ReadInt32LittleEndian(
                data.Slice(offset, 4)) / 2147483648.0f,
            _ => 0.0f
        };
    }

    private static float ReadPcm24(ReadOnlySpan<byte> sample)
    {
        var value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
        if ((value & 0x800000) != 0)
        {
            value |= unchecked((int)0xFF000000);
        }

        return value / 8388608.0f;
    }

    private static WaveFormat NormalizeWaveFormat(WaveFormat format)
    {
        if (format is WaveFormatExtensible extensible)
        {
            return extensible.ToStandardWaveFormat();
        }

        return format;
    }

    private float GetSlotPan(int slotIndex)
    {
        return slotIndex is >= 1 and <= 8 ? _slotPans[slotIndex] : 0.0f;
    }

    private void RecordMixedFrame(float left, float right)
    {
        Interlocked.Increment(ref _totalFramesMixed);
        var absLeft = MathF.Abs(left);
        var absRight = MathF.Abs(right);
        if (absLeft > _peakMasterLeft)
        {
            _peakMasterLeft = absLeft;
        }

        if (absRight > _peakMasterRight)
        {
            _peakMasterRight = absRight;
        }
    }

    private void RecordUnderrun()
    {
        Interlocked.Increment(ref _underrunCount);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WasapiMixer));
        }
    }

    private sealed class MasterMixWaveProvider : IWaveProvider
    {
        private readonly WasapiMixer _owner;
        private readonly IReadOnlyList<SlotInput> _slots;
        private readonly WaveFormat _outputFormat;

        public MasterMixWaveProvider(
            WasapiMixer owner,
            IReadOnlyList<SlotInput> slots,
            WaveFormat outputFormat)
        {
            _owner = owner;
            _slots = slots;
            _outputFormat = outputFormat;
        }

        public WaveFormat WaveFormat => _outputFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            var blockAlign = _outputFormat.BlockAlign;
            var frameCount = count / blockAlign;
            var writeOffset = offset;
            var gainLeft = new float[_slots.Count];
            var gainRight = new float[_slots.Count];

            for (var i = 0; i < _slots.Count; i++)
            {
                var pan = _owner.GetSlotPan(_slots[i].SlotIndex);
                ComputeUnityCenterBalanceGains(pan, out gainLeft[i], out gainRight[i]);
            }

            for (var frame = 0; frame < frameCount; frame++)
            {
                var masterLeft = 0.0f;
                var masterRight = 0.0f;

                for (var i = 0; i < _slots.Count; i++)
                {
                    if (_slots[i].Buffer.TryRead(out var left, out var right))
                    {
                        masterLeft += left * gainLeft[i];
                        masterRight += right * gainRight[i];
                    }
                    else
                    {
                        _owner.RecordUnderrun();
                    }
                }

                masterLeft = SoftLimit(masterLeft);
                masterRight = SoftLimit(masterRight);
                _owner.RecordMixedFrame(masterLeft, masterRight);
                WriteFrame(buffer, ref writeOffset, masterLeft, masterRight, _outputFormat);
            }

            return frameCount * blockAlign;
        }

        private static float SoftLimit(float value)
        {
            const float threshold = 0.98f;
            var abs = MathF.Abs(value);
            if (abs <= threshold)
            {
                return value;
            }

            // Transparent below threshold, gently compress only near/above full scale.
            var sign = MathF.Sign(value);
            var over = abs - threshold;
            var compressed = threshold + (1.0f - MathF.Exp(-over * 4.0f)) * (1.0f - threshold);
            return sign * compressed;
        }

        private static void ComputeUnityCenterBalanceGains(float pan, out float leftGain, out float rightGain)
        {
            pan = Math.Clamp(pan, -1.0f, 1.0f);
            if (pan <= 0.0f)
            {
                leftGain = 1.0f;
                rightGain = 1.0f + pan; // pan=-1 => 0, pan=0 => 1
                return;
            }

            leftGain = 1.0f - pan; // pan=+1 => 0, pan=0 => 1
            rightGain = 1.0f;
        }

        private static void WriteFrame(
            byte[] buffer,
            ref int offset,
            float left,
            float right,
            WaveFormat format)
        {
            var normalized = NormalizeWaveFormat(format);
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var sample = channel switch
                {
                    0 => left,
                    1 => right,
                    _ => 0.0f
                };

                WriteSample(buffer, ref offset, sample, normalized.Encoding, normalized.BitsPerSample);
            }
        }

        private static void WriteSample(
            byte[] buffer,
            ref int offset,
            float sample,
            WaveFormatEncoding encoding,
            int bitsPerSample)
        {
            sample = Math.Clamp(sample, -1.0f, 1.0f);

            if (encoding == WaveFormatEncoding.IeeeFloat && bitsPerSample == 32)
            {
                var bits = BitConverter.SingleToInt32Bits(sample);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), bits);
                offset += 4;
                return;
            }

            if (encoding == WaveFormatEncoding.Pcm && bitsPerSample == 16)
            {
                var pcm = (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset, 2), pcm);
                offset += 2;
                return;
            }

            if (encoding == WaveFormatEncoding.Pcm && bitsPerSample == 24)
            {
                var pcm = (int)Math.Clamp(sample * 8388607.0f, -8388608.0f, 8388607.0f);
                buffer[offset++] = (byte)(pcm & 0xFF);
                buffer[offset++] = (byte)((pcm >> 8) & 0xFF);
                buffer[offset++] = (byte)((pcm >> 16) & 0xFF);
                return;
            }

            if (encoding == WaveFormatEncoding.Pcm && bitsPerSample == 32)
            {
                var pcm = (int)Math.Clamp(sample * int.MaxValue, int.MinValue, int.MaxValue);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), pcm);
                offset += 4;
                return;
            }

            throw new NotSupportedException($"Unsupported output sample format {encoding} {bitsPerSample}-bit.");
        }
    }

    private sealed class SlotInput
    {
        public SlotInput(
            int slotIndex,
            string endpointId,
            MMDevice device,
            WasapiLoopbackCapture capture,
            StereoCircularBuffer buffer)
        {
            SlotIndex = slotIndex;
            EndpointId = endpointId;
            Device = device;
            Capture = capture;
            Buffer = buffer;
        }

        public int SlotIndex { get; }

        public string EndpointId { get; }

        public MMDevice Device { get; }

        public WasapiLoopbackCapture Capture { get; }

        public StereoCircularBuffer Buffer { get; }
    }

    private sealed class StereoCircularBuffer
    {
        private readonly object _gate = new();
        private readonly float[] _frames;
        private readonly int _capacityFrames;
        private int _readFrame;
        private int _writeFrame;
        private int _countFrames;

        public StereoCircularBuffer(int capacityFrames)
        {
            _capacityFrames = Math.Max(capacityFrames, 128);
            _frames = new float[_capacityFrames * 2];
        }

        public bool Write(float left, float right)
        {
            lock (_gate)
            {
                var dropped = false;
                if (_countFrames == _capacityFrames)
                {
                    _readFrame = (_readFrame + 1) % _capacityFrames;
                    _countFrames--;
                    dropped = true;
                }

                var writeOffset = _writeFrame * 2;
                _frames[writeOffset] = left;
                _frames[writeOffset + 1] = right;
                _writeFrame = (_writeFrame + 1) % _capacityFrames;
                _countFrames++;
                return dropped;
            }
        }

        public bool TryRead(out float left, out float right)
        {
            lock (_gate)
            {
                if (_countFrames == 0)
                {
                    left = 0.0f;
                    right = 0.0f;
                    return false;
                }

                var readOffset = _readFrame * 2;
                left = _frames[readOffset];
                right = _frames[readOffset + 1];
                _readFrame = (_readFrame + 1) % _capacityFrames;
                _countFrames--;
                return true;
            }
        }
    }
}

