namespace Gatherum.Client.Emulation.GameBoy;

/// <summary>The Game Boy's sound: two square waves (the first with a frequency sweep on
/// it), a four-bit wave channel a game fills with any thirty-two samples it likes, and
/// a noise generator. A sequencer at 512 Hz clocks the length counters, the sweep and
/// the envelopes, which is why every sound effect on the machine is built out of those
/// three things and their fractions.
///
/// The timers are stepped by whole machine cycles rather than one clock at a time —
/// the output is resampled to about 44 kHz on the way out, so nothing above twenty
/// kilohertz survives the trip anyway.</summary>
public sealed class GameBoyApu
{
    public const int SampleRate = 44100;
    private const int Clock = 4194304;

    private static readonly byte[][] DutyPatterns =
    [
        [0, 0, 0, 0, 0, 0, 0, 1],
        [1, 0, 0, 0, 0, 0, 0, 1],
        [1, 0, 0, 0, 0, 1, 1, 1],
        [0, 1, 1, 1, 1, 1, 1, 0],
    ];

    private static readonly int[] NoiseDivisors = [8, 16, 32, 48, 64, 80, 96, 112];

    private sealed class Envelope
    {
        public byte Volume, Initial, Period;
        public bool Increasing;
        public int Timer;

        public void Trigger()
        {
            Volume = Initial;
            Timer = Period;
        }

        public void Clock()
        {
            if (Period == 0)
                return;
            if (--Timer > 0)
                return;
            Timer = Period;
            if (Increasing && Volume < 15)
                Volume++;
            else if (!Increasing && Volume > 0)
                Volume--;
        }
    }

    private sealed class Square
    {
        public readonly Envelope Envelope = new();
        public bool Enabled, DacEnabled, LengthEnabled;
        public byte Duty, Position;
        public int Frequency, Timer, Length;

        public bool SweepEnabled, SweepDecreasing;
        public byte SweepPeriod, SweepShift;
        public int SweepTimer, SweepShadow;
        public bool SweepActive;

        public void Step(int cycles)
        {
            Timer -= cycles;
            while (Timer <= 0)
            {
                Timer += (2048 - Frequency) * 4;
                Position = (byte)((Position + 1) & 7);
            }
        }

        public void Trigger(int lengthMaximum)
        {
            Enabled = DacEnabled;
            if (Length == 0)
                Length = lengthMaximum;
            Timer = (2048 - Frequency) * 4;
            Envelope.Trigger();
        }

        public void ClockLength()
        {
            if (!LengthEnabled || Length == 0)
                return;
            if (--Length == 0)
                Enabled = false;
        }

        public void ClockSweep()
        {
            if (!SweepActive || SweepPeriod == 0)
                return;
            if (--SweepTimer > 0)
                return;
            SweepTimer = SweepPeriod;
            var next = NextSweepFrequency();
            if (next > 2047)
            {
                Enabled = false;
                return;
            }
            if (SweepShift == 0)
                return;
            SweepShadow = next;
            Frequency = next;
            // The sweep checks a second time with the new value and cuts the channel
            // if that one would overflow too, without writing it back.
            if (NextSweepFrequency() > 2047)
                Enabled = false;
        }

        public int NextSweepFrequency()
        {
            var change = SweepShadow >> SweepShift;
            return SweepDecreasing ? SweepShadow - change : SweepShadow + change;
        }

        public int Output => Enabled && DacEnabled
            ? DutyPatterns[Duty][Position] * Envelope.Volume
            : 0;
    }

    private readonly Square pulse1 = new();
    private readonly Square pulse2 = new();

    private readonly byte[] waveRam = new byte[16];
    private bool waveEnabled, waveDacEnabled, waveLengthEnabled;
    private int waveFrequency, waveTimer, wavePosition, waveLength;
    private byte waveVolume;

    private readonly Envelope noiseEnvelope = new();
    private bool noiseEnabled, noiseDacEnabled, noiseLengthEnabled, noiseShortWidth;
    private int noiseTimer, noiseLength, noiseDivisorCode, noiseShiftAmount;
    private ushort noiseShift = 0x7FFF;

    private bool powered = true;
    private byte leftVolume = 7, rightVolume = 7, panning = 0xF3;

    private int sequencerCounter;
    private int sequencerStep;

    private float sampleSum;
    private int sampleCount;
    private int sampleTimer;
    private float highPass;

    private readonly short[] queue = new short[SampleRate];
    private int queueRead, queueWrite;

    public void Reset()
    {
        queueRead = queueWrite = 0;
        powered = true;
    }

    public void Step(int cycles)
    {
        if (!powered)
        {
            AccumulateSilence(cycles);
            return;
        }

        pulse1.Step(cycles);
        pulse2.Step(cycles);
        StepWave(cycles);
        StepNoise(cycles);

        sequencerCounter += cycles;
        while (sequencerCounter >= 8192)
        {
            sequencerCounter -= 8192;
            ClockSequencer();
        }

        var mixed = Mix();
        sampleSum += mixed * cycles;
        sampleCount += cycles;
        EmitSamples(cycles);
    }

    private void AccumulateSilence(int cycles)
    {
        sampleCount += cycles;
        EmitSamples(cycles);
    }

    private void StepWave(int cycles)
    {
        waveTimer -= cycles;
        while (waveTimer <= 0)
        {
            waveTimer += (2048 - waveFrequency) * 2;
            wavePosition = (wavePosition + 1) & 31;
        }
    }

    private void StepNoise(int cycles)
    {
        noiseTimer -= cycles;
        while (noiseTimer <= 0)
        {
            noiseTimer += NoiseDivisors[noiseDivisorCode] << noiseShiftAmount;
            var feedback = (noiseShift ^ noiseShift >> 1) & 1;
            noiseShift = (ushort)(noiseShift >> 1 | feedback << 14);
            if (noiseShortWidth)
                noiseShift = (ushort)(noiseShift & ~0x40 | feedback << 6);
        }
    }

    private void ClockSequencer()
    {
        if ((sequencerStep & 1) == 0)
        {
            pulse1.ClockLength();
            pulse2.ClockLength();
            ClockWaveLength();
            ClockNoiseLength();
        }
        if (sequencerStep is 2 or 6)
            pulse1.ClockSweep();
        if (sequencerStep == 7)
        {
            pulse1.Envelope.Clock();
            pulse2.Envelope.Clock();
            noiseEnvelope.Clock();
        }
        sequencerStep = (sequencerStep + 1) & 7;
    }

    private void ClockWaveLength()
    {
        if (!waveLengthEnabled || waveLength == 0)
            return;
        if (--waveLength == 0)
            waveEnabled = false;
    }

    private void ClockNoiseLength()
    {
        if (!noiseLengthEnabled || noiseLength == 0)
            return;
        if (--noiseLength == 0)
            noiseEnabled = false;
    }

    private int WaveOutput
    {
        get
        {
            if (!waveEnabled || !waveDacEnabled || waveVolume == 0)
                return 0;
            var sample = wavePosition % 2 == 0
                ? waveRam[wavePosition / 2] >> 4
                : waveRam[wavePosition / 2] & 0x0F;
            return sample >> waveVolume - 1;
        }
    }

    private int NoiseOutput => noiseEnabled && noiseDacEnabled && (noiseShift & 1) == 0
        ? noiseEnvelope.Volume
        : 0;

    /// <summary>Four channels into one. The console mixes two stereo halves with their
    /// own volume; the player has one channel to give the browser, so the halves are
    /// averaged rather than dropped — a game that pans an effect hard still gets heard.</summary>
    private float Mix()
    {
        var outputs = (float)pulse1.Output;
        var second = (float)pulse2.Output;
        var wave = (float)WaveOutput;
        var noise = (float)NoiseOutput;

        var left = ((panning & 0x10) != 0 ? outputs : 0) + ((panning & 0x20) != 0 ? second : 0)
            + ((panning & 0x40) != 0 ? wave : 0) + ((panning & 0x80) != 0 ? noise : 0);
        var right = ((panning & 0x01) != 0 ? outputs : 0) + ((panning & 0x02) != 0 ? second : 0)
            + ((panning & 0x04) != 0 ? wave : 0) + ((panning & 0x08) != 0 ? noise : 0);

        left *= leftVolume + 1;
        right *= rightVolume + 1;
        // Four channels of fifteen at volume eight is the loudest the chip goes.
        return (left + right) / (2 * 15 * 4 * 8);
    }

    private void EmitSamples(int cycles)
    {
        sampleTimer += SampleRate * cycles;
        if (sampleTimer < Clock)
            return;
        sampleTimer -= Clock;

        var average = sampleCount == 0 ? 0 : sampleSum / sampleCount;
        sampleSum = 0;
        sampleCount = 0;
        highPass += (average - highPass) * 0.0008f;
        var value = Math.Clamp((average - highPass) * 2.4f, -1.0f, 1.0f);

        var next = (queueWrite + 1) % queue.Length;
        if (next == queueRead)
            queueRead = (queueRead + 1) % queue.Length;
        queue[queueWrite] = (short)(value * short.MaxValue);
        queueWrite = next;
    }

    public int ReadAudio(short[] destination)
    {
        var written = 0;
        while (written < destination.Length && queueRead != queueWrite)
        {
            destination[written++] = queue[queueRead];
            queueRead = (queueRead + 1) % queue.Length;
        }
        return written;
    }

    public byte ReadRegister(ushort address)
    {
        if (address is >= 0xFF30 and <= 0xFF3F)
            return waveRam[address - 0xFF30];
        return address switch
        {
            0xFF10 => (byte)(0x80 | pulse1.SweepPeriod << 4
                | (pulse1.SweepDecreasing ? 0x08 : 0) | pulse1.SweepShift),
            0xFF11 or 0xFF16 => (byte)(0x3F | (address == 0xFF11 ? pulse1.Duty : pulse2.Duty) << 6),
            0xFF12 => EnvelopeByte(pulse1.Envelope),
            0xFF17 => EnvelopeByte(pulse2.Envelope),
            0xFF14 or 0xFF19 or 0xFF1E or 0xFF23 => (byte)(0xBF |
                ((address switch
                {
                    0xFF14 => pulse1.LengthEnabled,
                    0xFF19 => pulse2.LengthEnabled,
                    0xFF1E => waveLengthEnabled,
                    _ => noiseLengthEnabled,
                })
                    ? 0x40 : 0)),
            0xFF1A => (byte)(0x7F | (waveDacEnabled ? 0x80 : 0)),
            0xFF1C => (byte)(0x9F | waveVolume << 5),
            0xFF21 => EnvelopeByte(noiseEnvelope),
            0xFF22 => (byte)(noiseShiftAmount << 4 | (noiseShortWidth ? 0x08 : 0)
                | noiseDivisorCode),
            0xFF24 => (byte)(leftVolume << 4 | rightVolume),
            0xFF25 => panning,
            0xFF26 => (byte)(0x70 | (powered ? 0x80 : 0)
                | (pulse1.Enabled ? 0x01 : 0) | (pulse2.Enabled ? 0x02 : 0)
                | (waveEnabled ? 0x04 : 0) | (noiseEnabled ? 0x08 : 0)),
            _ => 0xFF,
        };
    }

    private static byte EnvelopeByte(Envelope envelope) =>
        (byte)(envelope.Initial << 4 | (envelope.Increasing ? 0x08 : 0) | envelope.Period);

    public void WriteRegister(ushort address, byte value)
    {
        if (address is >= 0xFF30 and <= 0xFF3F)
        {
            waveRam[address - 0xFF30] = value;
            return;
        }
        // With the sound hardware off, only the power register answers — which is how
        // a game silences everything with one write.
        if (!powered && address != 0xFF26)
            return;

        switch (address)
        {
            case 0xFF10:
                pulse1.SweepPeriod = (byte)(value >> 4 & 0x07);
                pulse1.SweepDecreasing = (value & 0x08) != 0;
                pulse1.SweepShift = (byte)(value & 0x07);
                pulse1.SweepEnabled = pulse1.SweepPeriod != 0 || pulse1.SweepShift != 0;
                break;
            case 0xFF11: pulse1.Duty = (byte)(value >> 6); pulse1.Length = 64 - (value & 0x3F); break;
            case 0xFF12: WriteEnvelope(pulse1, value); break;
            case 0xFF13: pulse1.Frequency = pulse1.Frequency & 0x700 | value; break;
            case 0xFF14: WriteControl(pulse1, value, 64); break;

            case 0xFF16: pulse2.Duty = (byte)(value >> 6); pulse2.Length = 64 - (value & 0x3F); break;
            case 0xFF17: WriteEnvelope(pulse2, value); break;
            case 0xFF18: pulse2.Frequency = pulse2.Frequency & 0x700 | value; break;
            case 0xFF19: WriteControl(pulse2, value, 64); break;

            case 0xFF1A:
                waveDacEnabled = (value & 0x80) != 0;
                if (!waveDacEnabled)
                    waveEnabled = false;
                break;
            case 0xFF1B: waveLength = 256 - value; break;
            case 0xFF1C: waveVolume = (byte)(value >> 5 & 0x03); break;
            case 0xFF1D: waveFrequency = waveFrequency & 0x700 | value; break;
            case 0xFF1E:
                waveFrequency = waveFrequency & 0xFF | (value & 0x07) << 8;
                waveLengthEnabled = (value & 0x40) != 0;
                if ((value & 0x80) != 0)
                {
                    waveEnabled = waveDacEnabled;
                    if (waveLength == 0)
                        waveLength = 256;
                    waveTimer = (2048 - waveFrequency) * 2;
                    wavePosition = 0;
                }
                break;

            case 0xFF20: noiseLength = 64 - (value & 0x3F); break;
            case 0xFF21:
                noiseEnvelope.Initial = (byte)(value >> 4);
                noiseEnvelope.Increasing = (value & 0x08) != 0;
                noiseEnvelope.Period = (byte)(value & 0x07);
                noiseDacEnabled = (value & 0xF8) != 0;
                if (!noiseDacEnabled)
                    noiseEnabled = false;
                break;
            case 0xFF22:
                noiseShiftAmount = value >> 4;
                noiseShortWidth = (value & 0x08) != 0;
                noiseDivisorCode = value & 0x07;
                break;
            case 0xFF23:
                noiseLengthEnabled = (value & 0x40) != 0;
                if ((value & 0x80) != 0)
                {
                    noiseEnabled = noiseDacEnabled;
                    if (noiseLength == 0)
                        noiseLength = 64;
                    noiseShift = 0x7FFF;
                    noiseTimer = NoiseDivisors[noiseDivisorCode] << noiseShiftAmount;
                    noiseEnvelope.Trigger();
                }
                break;

            case 0xFF24: leftVolume = (byte)(value >> 4 & 0x07); rightVolume = (byte)(value & 0x07); break;
            case 0xFF25: panning = value; break;
            case 0xFF26:
                powered = (value & 0x80) != 0;
                if (!powered)
                {
                    pulse1.Enabled = pulse2.Enabled = waveEnabled = noiseEnabled = false;
                    panning = 0;
                    leftVolume = rightVolume = 0;
                }
                break;
        }
    }

    private static void WriteEnvelope(Square channel, byte value)
    {
        channel.Envelope.Initial = (byte)(value >> 4);
        channel.Envelope.Increasing = (value & 0x08) != 0;
        channel.Envelope.Period = (byte)(value & 0x07);
        // The upper five bits feed the digital-to-analogue converter; all zero and the
        // channel is off entirely, however loud its envelope says it is.
        channel.DacEnabled = (value & 0xF8) != 0;
        if (!channel.DacEnabled)
            channel.Enabled = false;
    }

    private static void WriteControl(Square channel, byte value, int lengthMaximum)
    {
        channel.Frequency = channel.Frequency & 0xFF | (value & 0x07) << 8;
        channel.LengthEnabled = (value & 0x40) != 0;
        if ((value & 0x80) == 0)
            return;
        channel.Trigger(lengthMaximum);
        channel.SweepShadow = channel.Frequency;
        channel.SweepTimer = channel.SweepPeriod == 0 ? 8 : channel.SweepPeriod;
        channel.SweepActive = channel.SweepEnabled;
        if (channel.SweepShift != 0 && channel.NextSweepFrequency() > 2047)
            channel.Enabled = false;
    }
}
