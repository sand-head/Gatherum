namespace Gatherum.Client.Emulation.Nes;

/// <summary>The 2A03's sound half: two pulse channels, a triangle, a noise generator
/// and a one-bit sample player, mixed the way the chip mixes them — which is not by
/// adding. The two summing networks are resistive, so a channel's contribution falls off
/// as the others get louder; the tables below are that curve, precomputed.
///
/// Everything is clocked from the CPU's own cycle, and samples are taken out by
/// averaging: the chip runs at 1.79 MHz and the browser wants about 44 kHz, so each
/// sample is the mean of the forty-odd cycles that went into it rather than whichever
/// one happened to land on the boundary.</summary>
public sealed class NesApu(NesConsole console)
{
    public const int SampleRate = 44100;
    private const int CpuClock = 1789773;

    private static readonly byte[] LengthTable =
    [
        10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30,
    ];

    private static readonly byte[][] DutySequences =
    [
        [0, 1, 0, 0, 0, 0, 0, 0],
        [0, 1, 1, 0, 0, 0, 0, 0],
        [0, 1, 1, 1, 1, 0, 0, 0],
        [1, 0, 0, 1, 1, 1, 1, 1],
    ];

    private static readonly byte[] TriangleSequence =
    [
        15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    ];

    private static readonly ushort[] NoisePeriods =
        [4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068];

    private static readonly ushort[] DmcPeriods =
        [428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54];

    private static readonly float[] PulseMix = BuildPulseMix();
    private static readonly float[] TndMix = BuildTndMix();

    private static float[] BuildPulseMix()
    {
        var table = new float[31];
        for (var i = 1; i < table.Length; i++)
            table[i] = 95.52f / (8128.0f / i + 100.0f);
        return table;
    }

    private static float[] BuildTndMix()
    {
        var table = new float[203];
        for (var i = 1; i < table.Length; i++)
            table[i] = 163.67f / (24329.0f / i + 100.0f);
        return table;
    }

    private sealed class Envelope
    {
        public bool Loop, ConstantVolume, Start;
        public byte Period, Divider, Decay;

        public void Save(ref StateWriter state)
        {
            state.Write(Loop);
            state.Write(ConstantVolume);
            state.Write(Start);
            state.Write(Period);
            state.Write(Divider);
            state.Write(Decay);
        }

        public void Load(ref StateReader state)
        {
            Loop = state.ReadBool();
            ConstantVolume = state.ReadBool();
            Start = state.ReadBool();
            Period = state.ReadByte();
            Divider = state.ReadByte();
            Decay = state.ReadByte();
        }

        public byte Volume => ConstantVolume ? Period : Decay;

        public void Clock()
        {
            if (Start)
            {
                Start = false;
                Decay = 15;
                Divider = Period;
                return;
            }
            if (Divider > 0)
            {
                Divider--;
                return;
            }
            Divider = Period;
            if (Decay > 0)
                Decay--;
            else if (Loop)
                Decay = 15;
        }
    }

    private sealed class Pulse(bool onesComplementSweep)
    {
        public readonly Envelope Envelope = new();
        public bool Enabled, LengthHalt;
        public byte Duty, Length, SequenceStep;
        public int Timer, TimerPeriod;
        public bool SweepEnabled, SweepNegate, SweepReload;
        public byte SweepPeriod, SweepShift, SweepDivider;

        /// <summary>The first pulse channel's sweep subtracts one too many, which is
        /// why the two channels detune slightly when swept the same way.</summary>
        public int SweepTarget
        {
            get
            {
                var change = TimerPeriod >> SweepShift;
                if (!SweepNegate)
                    return TimerPeriod + change;
                return TimerPeriod - change - (onesComplementSweep ? 1 : 0);
            }
        }

        public bool Muted => TimerPeriod < 8 || SweepTarget > 0x7FF;

        public void ClockTimer()
        {
            if (Timer > 0)
            {
                Timer--;
                return;
            }
            Timer = TimerPeriod;
            SequenceStep = (byte)((SequenceStep + 1) & 7);
        }

        public void ClockSweep()
        {
            if (SweepDivider == 0 && SweepEnabled && SweepShift > 0 && !Muted)
            {
                var target = SweepTarget;
                if (target >= 0)
                    TimerPeriod = target;
            }
            if (SweepDivider == 0 || SweepReload)
            {
                SweepDivider = SweepPeriod;
                SweepReload = false;
            }
            else
            {
                SweepDivider--;
            }
        }

        public void ClockLength()
        {
            if (Length > 0 && !LengthHalt)
                Length--;
        }

        public byte Output => !Enabled || Length == 0 || Muted
            ? (byte)0
            : (byte)(DutySequences[Duty][SequenceStep] * Envelope.Volume);

        public void Save(ref StateWriter state)
        {
            Envelope.Save(ref state);
            state.Write(Enabled);
            state.Write(LengthHalt);
            state.Write(Duty);
            state.Write(Length);
            state.Write(SequenceStep);
            state.Write(Timer);
            state.Write(TimerPeriod);
            state.Write(SweepEnabled);
            state.Write(SweepNegate);
            state.Write(SweepReload);
            state.Write(SweepPeriod);
            state.Write(SweepShift);
            state.Write(SweepDivider);
        }

        public void Load(ref StateReader state)
        {
            Envelope.Load(ref state);
            Enabled = state.ReadBool();
            LengthHalt = state.ReadBool();
            Duty = state.ReadByte();
            Length = state.ReadByte();
            SequenceStep = state.ReadByte();
            Timer = state.ReadInt32();
            TimerPeriod = state.ReadInt32();
            SweepEnabled = state.ReadBool();
            SweepNegate = state.ReadBool();
            SweepReload = state.ReadBool();
            SweepPeriod = state.ReadByte();
            SweepShift = state.ReadByte();
            SweepDivider = state.ReadByte();
        }
    }

    private readonly Pulse pulse1 = new(onesComplementSweep: true);
    private readonly Pulse pulse2 = new(onesComplementSweep: false);

    private bool triangleEnabled, triangleHalt, triangleLinearReload;
    private byte triangleLength, triangleLinear, triangleLinearPeriod, triangleStep;
    private int triangleTimer, triangleTimerPeriod;

    private readonly Envelope noiseEnvelope = new();
    private bool noiseEnabled, noiseHalt, noiseShortMode;
    private byte noiseLength;
    private int noiseTimer, noiseTimerPeriod;
    private ushort noiseShift = 1;

    private bool dmcEnabled, dmcLoop, dmcIrqEnabled, dmcIrqFlag, dmcSilent = true;
    private byte dmcOutput, dmcSampleBuffer, dmcShift, dmcBitsRemaining;
    private bool dmcBufferFull;
    private int dmcTimer, dmcTimerPeriod = 428;
    private ushort dmcSampleAddress, dmcAddress;
    private int dmcSampleLength, dmcBytesRemaining;

    private int frameCounter;
    private bool fiveStepMode, frameIrqInhibit, frameIrqFlag;

    private float sampleSum;
    private int sampleCount;
    private int sampleTimer;
    private float highPass;

    private readonly short[] queue = new short[SampleRate];
    private int queueRead, queueWrite;

    public bool IrqPending => frameIrqFlag || dmcIrqFlag;

    /// <summary>Everything the sound chip can act on. The resampling accumulators and
    /// the queue of finished samples are deliberately left out: how often a browser
    /// asks for sound is the browser's business, and a machine's state must not depend
    /// on it — two people playing the same game must agree about the console whether or
    /// not one of them has muted it.</summary>
    internal void Save(ref StateWriter state)
    {
        pulse1.Save(ref state);
        pulse2.Save(ref state);

        state.Write(triangleEnabled);
        state.Write(triangleHalt);
        state.Write(triangleLinearReload);
        state.Write(triangleLength);
        state.Write(triangleLinear);
        state.Write(triangleLinearPeriod);
        state.Write(triangleStep);
        state.Write(triangleTimer);
        state.Write(triangleTimerPeriod);

        noiseEnvelope.Save(ref state);
        state.Write(noiseEnabled);
        state.Write(noiseHalt);
        state.Write(noiseShortMode);
        state.Write(noiseLength);
        state.Write(noiseTimer);
        state.Write(noiseTimerPeriod);
        state.Write(noiseShift);

        state.Write(dmcEnabled);
        state.Write(dmcLoop);
        state.Write(dmcIrqEnabled);
        state.Write(dmcIrqFlag);
        state.Write(dmcSilent);
        state.Write(dmcBufferFull);
        state.Write(dmcOutput);
        state.Write(dmcSampleBuffer);
        state.Write(dmcShift);
        state.Write(dmcBitsRemaining);
        state.Write(dmcTimer);
        state.Write(dmcTimerPeriod);
        state.Write(dmcSampleAddress);
        state.Write(dmcAddress);
        state.Write(dmcSampleLength);
        state.Write(dmcBytesRemaining);

        state.Write(frameCounter);
        state.Write(fiveStepMode);
        state.Write(frameIrqInhibit);
        state.Write(frameIrqFlag);
    }

    internal void Load(ref StateReader state)
    {
        pulse1.Load(ref state);
        pulse2.Load(ref state);

        triangleEnabled = state.ReadBool();
        triangleHalt = state.ReadBool();
        triangleLinearReload = state.ReadBool();
        triangleLength = state.ReadByte();
        triangleLinear = state.ReadByte();
        triangleLinearPeriod = state.ReadByte();
        triangleStep = state.ReadByte();
        triangleTimer = state.ReadInt32();
        triangleTimerPeriod = state.ReadInt32();

        noiseEnvelope.Load(ref state);
        noiseEnabled = state.ReadBool();
        noiseHalt = state.ReadBool();
        noiseShortMode = state.ReadBool();
        noiseLength = state.ReadByte();
        noiseTimer = state.ReadInt32();
        noiseTimerPeriod = state.ReadInt32();
        noiseShift = state.ReadUInt16();

        dmcEnabled = state.ReadBool();
        dmcLoop = state.ReadBool();
        dmcIrqEnabled = state.ReadBool();
        dmcIrqFlag = state.ReadBool();
        dmcSilent = state.ReadBool();
        dmcBufferFull = state.ReadBool();
        dmcOutput = state.ReadByte();
        dmcSampleBuffer = state.ReadByte();
        dmcShift = state.ReadByte();
        dmcBitsRemaining = state.ReadByte();
        dmcTimer = state.ReadInt32();
        dmcTimerPeriod = state.ReadInt32();
        dmcSampleAddress = state.ReadUInt16();
        dmcAddress = state.ReadUInt16();
        dmcSampleLength = state.ReadInt32();
        dmcBytesRemaining = state.ReadInt32();

        frameCounter = state.ReadInt32();
        fiveStepMode = state.ReadBool();
        frameIrqInhibit = state.ReadBool();
        frameIrqFlag = state.ReadBool();
    }

    public void Reset()
    {
        WriteRegister(0x4015, 0);
        frameCounter = 0;
        frameIrqFlag = dmcIrqFlag = false;
        queueRead = queueWrite = 0;
    }

    public byte ReadStatus()
    {
        var value = (byte)(
            (pulse1.Length > 0 ? 0x01 : 0) | (pulse2.Length > 0 ? 0x02 : 0) |
            (triangleLength > 0 ? 0x04 : 0) | (noiseLength > 0 ? 0x08 : 0) |
            (dmcBytesRemaining > 0 ? 0x10 : 0) |
            (frameIrqFlag ? 0x40 : 0) | (dmcIrqFlag ? 0x80 : 0));
        frameIrqFlag = false;
        return value;
    }

    public void WriteRegister(ushort address, byte value)
    {
        switch (address)
        {
            case 0x4000:
            case 0x4004:
            {
                var channel = address == 0x4000 ? pulse1 : pulse2;
                channel.Duty = (byte)(value >> 6);
                channel.LengthHalt = (value & 0x20) != 0;
                channel.Envelope.Loop = channel.LengthHalt;
                channel.Envelope.ConstantVolume = (value & 0x10) != 0;
                channel.Envelope.Period = (byte)(value & 0x0F);
                break;
            }
            case 0x4001:
            case 0x4005:
            {
                var channel = address == 0x4001 ? pulse1 : pulse2;
                channel.SweepEnabled = (value & 0x80) != 0;
                channel.SweepPeriod = (byte)(value >> 4 & 0x07);
                channel.SweepNegate = (value & 0x08) != 0;
                channel.SweepShift = (byte)(value & 0x07);
                channel.SweepReload = true;
                break;
            }
            case 0x4002:
            case 0x4006:
            {
                var channel = address == 0x4002 ? pulse1 : pulse2;
                channel.TimerPeriod = channel.TimerPeriod & 0x700 | value;
                break;
            }
            case 0x4003:
            case 0x4007:
            {
                var channel = address == 0x4003 ? pulse1 : pulse2;
                channel.TimerPeriod = channel.TimerPeriod & 0xFF | (value & 0x07) << 8;
                if (channel.Enabled)
                    channel.Length = LengthTable[value >> 3];
                channel.SequenceStep = 0;
                channel.Envelope.Start = true;
                break;
            }

            case 0x4008:
                triangleHalt = (value & 0x80) != 0;
                triangleLinearPeriod = (byte)(value & 0x7F);
                break;
            case 0x400A:
                triangleTimerPeriod = triangleTimerPeriod & 0x700 | value;
                break;
            case 0x400B:
                triangleTimerPeriod = triangleTimerPeriod & 0xFF | (value & 0x07) << 8;
                if (triangleEnabled)
                    triangleLength = LengthTable[value >> 3];
                triangleLinearReload = true;
                break;

            case 0x400C:
                noiseHalt = (value & 0x20) != 0;
                noiseEnvelope.Loop = noiseHalt;
                noiseEnvelope.ConstantVolume = (value & 0x10) != 0;
                noiseEnvelope.Period = (byte)(value & 0x0F);
                break;
            case 0x400E:
                noiseShortMode = (value & 0x80) != 0;
                noiseTimerPeriod = NoisePeriods[value & 0x0F];
                break;
            case 0x400F:
                if (noiseEnabled)
                    noiseLength = LengthTable[value >> 3];
                noiseEnvelope.Start = true;
                break;

            case 0x4010:
                dmcIrqEnabled = (value & 0x80) != 0;
                if (!dmcIrqEnabled)
                    dmcIrqFlag = false;
                dmcLoop = (value & 0x40) != 0;
                dmcTimerPeriod = DmcPeriods[value & 0x0F];
                break;
            case 0x4011:
                dmcOutput = (byte)(value & 0x7F);
                break;
            case 0x4012:
                dmcSampleAddress = (ushort)(0xC000 + value * 64);
                break;
            case 0x4013:
                dmcSampleLength = value * 16 + 1;
                break;

            case 0x4015:
                pulse1.Enabled = (value & 0x01) != 0;
                if (!pulse1.Enabled) pulse1.Length = 0;
                pulse2.Enabled = (value & 0x02) != 0;
                if (!pulse2.Enabled) pulse2.Length = 0;
                triangleEnabled = (value & 0x04) != 0;
                if (!triangleEnabled) triangleLength = 0;
                noiseEnabled = (value & 0x08) != 0;
                if (!noiseEnabled) noiseLength = 0;
                dmcEnabled = (value & 0x10) != 0;
                if (!dmcEnabled)
                {
                    dmcBytesRemaining = 0;
                }
                else if (dmcBytesRemaining == 0)
                {
                    dmcAddress = dmcSampleAddress;
                    dmcBytesRemaining = dmcSampleLength;
                }
                dmcIrqFlag = false;
                break;

            case 0x4017:
                fiveStepMode = (value & 0x80) != 0;
                frameIrqInhibit = (value & 0x40) != 0;
                if (frameIrqInhibit)
                    frameIrqFlag = false;
                frameCounter = 0;
                // Switching to the five-step sequence clocks everything once
                // immediately, which is how a game resets the envelopes in step.
                if (fiveStepMode)
                {
                    ClockQuarterFrame();
                    ClockHalfFrame();
                }
                break;
        }
    }

    /// <summary>One CPU cycle. The pulse and noise timers run at half that rate, the
    /// triangle at the full rate — which is why it can reach an octave lower.</summary>
    public void Tick(bool evenCycle)
    {
        if (evenCycle)
        {
            pulse1.ClockTimer();
            pulse2.ClockTimer();
            ClockNoise();
        }
        ClockTriangle();
        ClockDmc();
        ClockFrameCounter();
        TakeSample();
    }

    private void ClockTriangle()
    {
        if (triangleTimer > 0)
        {
            triangleTimer--;
            return;
        }
        triangleTimer = triangleTimerPeriod;
        // A period under two would be an inaudible ultrasonic buzz on the real chip
        // and a click here, so the sequencer holds where it is.
        if (triangleLength > 0 && triangleLinear > 0 && triangleTimerPeriod >= 2)
            triangleStep = (byte)((triangleStep + 1) & 31);
    }

    private void ClockNoise()
    {
        if (noiseTimer > 0)
        {
            noiseTimer--;
            return;
        }
        noiseTimer = noiseTimerPeriod;
        var feedback = (noiseShift ^ (noiseShortMode ? noiseShift >> 6 : noiseShift >> 1)) & 1;
        noiseShift = (ushort)(noiseShift >> 1 | feedback << 14);
    }

    private void ClockDmc()
    {
        if (dmcTimer > 0)
        {
            dmcTimer--;
            return;
        }
        dmcTimer = dmcTimerPeriod;

        if (dmcBitsRemaining == 0)
        {
            dmcBitsRemaining = 8;
            if (dmcBufferFull)
            {
                dmcSilent = false;
                dmcShift = dmcSampleBuffer;
                dmcBufferFull = false;
            }
            else
            {
                dmcSilent = true;
            }
        }

        if (!dmcSilent)
        {
            if ((dmcShift & 1) != 0)
            {
                if (dmcOutput <= 125)
                    dmcOutput += 2;
            }
            else if (dmcOutput >= 2)
            {
                dmcOutput -= 2;
            }
        }
        dmcShift >>= 1;
        dmcBitsRemaining--;

        FillDmcBuffer();
    }

    /// <summary>The sample player reads straight out of program memory behind the
    /// CPU's back, and the CPU loses cycles to it. It cannot stall the bus from here —
    /// this runs inside a tick — so the debt is left with the processor to pay before
    /// its next instruction.</summary>
    private void FillDmcBuffer()
    {
        if (dmcBufferFull || dmcBytesRemaining == 0)
            return;
        dmcSampleBuffer = console.CpuRead(dmcAddress);
        dmcBufferFull = true;
        console.Cpu.StallCycles += 4;
        dmcAddress = dmcAddress == 0xFFFF ? (ushort)0x8000 : (ushort)(dmcAddress + 1);
        dmcBytesRemaining--;
        if (dmcBytesRemaining != 0)
            return;
        if (dmcLoop)
        {
            dmcAddress = dmcSampleAddress;
            dmcBytesRemaining = dmcSampleLength;
        }
        else if (dmcIrqEnabled)
        {
            dmcIrqFlag = true;
        }
    }

    private void ClockFrameCounter()
    {
        frameCounter++;
        if (!fiveStepMode)
        {
            switch (frameCounter)
            {
                case 7457: ClockQuarterFrame(); break;
                case 14913: ClockQuarterFrame(); ClockHalfFrame(); break;
                case 22371: ClockQuarterFrame(); break;
                case 29829:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    if (!frameIrqInhibit)
                        frameIrqFlag = true;
                    frameCounter = 0;
                    break;
            }
            return;
        }
        switch (frameCounter)
        {
            case 7457: ClockQuarterFrame(); break;
            case 14913: ClockQuarterFrame(); ClockHalfFrame(); break;
            case 22371: ClockQuarterFrame(); break;
            case 37281:
                ClockQuarterFrame();
                ClockHalfFrame();
                frameCounter = 0;
                break;
        }
    }

    private void ClockQuarterFrame()
    {
        pulse1.Envelope.Clock();
        pulse2.Envelope.Clock();
        noiseEnvelope.Clock();
        if (triangleLinearReload)
            triangleLinear = triangleLinearPeriod;
        else if (triangleLinear > 0)
            triangleLinear--;
        if (!triangleHalt)
            triangleLinearReload = false;
    }

    private void ClockHalfFrame()
    {
        pulse1.ClockLength();
        pulse1.ClockSweep();
        pulse2.ClockLength();
        pulse2.ClockSweep();
        if (triangleLength > 0 && !triangleHalt)
            triangleLength--;
        if (noiseLength > 0 && !noiseHalt)
            noiseLength--;
    }

    private byte TriangleOutput => triangleEnabled && triangleLength > 0 && triangleLinear > 0
        ? TriangleSequence[triangleStep]
        : (byte)0;

    private byte NoiseOutput => !noiseEnabled || noiseLength == 0 || (noiseShift & 1) != 0
        ? (byte)0
        : noiseEnvelope.Volume;

    private void TakeSample()
    {
        var mixed = PulseMix[pulse1.Output + pulse2.Output]
            + TndMix[3 * TriangleOutput + 2 * NoiseOutput + dmcOutput];
        sampleSum += mixed;
        sampleCount++;

        sampleTimer += SampleRate;
        if (sampleTimer < CpuClock)
            return;
        sampleTimer -= CpuClock;

        var average = sampleSum / sampleCount;
        sampleSum = 0;
        sampleCount = 0;

        // A one-pole high pass: the mix sits well above zero and a constant offset is
        // only wasted headroom on the way to a speaker.
        highPass += (average - highPass) * 0.0008f;
        var value = Math.Clamp((average - highPass) * 1.6f, -1.0f, 1.0f);
        Enqueue((short)(value * short.MaxValue));
    }

    private void Enqueue(short sample)
    {
        var next = (queueWrite + 1) % queue.Length;
        // A player that has stopped asking for sound is paused or gone; the oldest
        // samples are the ones to lose.
        if (next == queueRead)
            queueRead = (queueRead + 1) % queue.Length;
        queue[queueWrite] = sample;
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
}
