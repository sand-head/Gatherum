namespace Gatherum.Client.Emulation.Sega;

/// <summary>The SN76489: three square waves and a noise generator, driven by a single
/// byte-wide port with no addresses at all. A write either latches a channel and the low
/// half of a value, or continues the value last latched — so the chip remembers which
/// register a program was talking to between writes.
///
/// The Game Gear adds the one thing the Master System never had: a register saying which
/// of the four channels reach which ear.</summary>
public sealed class SegaPsg(bool gameGear)
{
    public const int SampleRate = 44100;

    /// <summary>The chip is fed the processor's clock divided by sixteen.</summary>
    private const int Clock = 3579545 / 16;

    /// <summary>Attenuation is in two-decibel steps, fifteen of them and then silence.
    /// The table is the chip's, not a curve fitted to it.</summary>
    private static readonly float[] Volumes = BuildVolumes();

    private static float[] BuildVolumes()
    {
        var table = new float[16];
        for (var step = 0; step < 15; step++)
            table[step] = (float)Math.Pow(10.0, -0.1 * step);
        table[15] = 0;
        return table;
    }

    private readonly int[] tone = new int[4];
    private readonly int[] volume = [15, 15, 15, 15];
    private readonly int[] counter = new int[4];
    private readonly bool[] output = [false, false, false, false];

    private int latchedChannel;
    private bool latchedVolume;

    /// <summary>Fifteen bits, tapped at the bottom two for white noise. Shifting a
    /// zero in for ever is the one state it can never leave, so it starts with a bit
    /// set.</summary>
    private int noiseShift = 0x4000;
    private int noiseControl;

    /// <summary>Which channels reach which ear. All of them, until a Game Gear says
    /// otherwise.</summary>
    private byte stereo = 0xFF;

    private int sampleTimer;
    private float leftSum, rightSum;
    private int sampleCount;

    private readonly short[] queue = new short[SampleRate * 2];
    private int queueRead, queueWrite;

    public void Reset()
    {
        Array.Clear(tone);
        Array.Clear(counter);
        for (var channel = 0; channel < 4; channel++)
        {
            volume[channel] = 15;
            output[channel] = false;
        }
        latchedChannel = 0;
        latchedVolume = false;
        noiseShift = 0x4000;
        noiseControl = 0;
        stereo = 0xFF;
        sampleTimer = 0;
        leftSum = rightSum = 0;
        sampleCount = 0;
        queueRead = queueWrite = 0;
    }

    public void Write(byte value)
    {
        if ((value & 0x80) != 0)
        {
            latchedChannel = value >> 5 & 3;
            latchedVolume = (value & 0x10) != 0;
            if (latchedVolume)
            {
                volume[latchedChannel] = value & 0x0F;
                return;
            }
            if (latchedChannel == 3)
            {
                WriteNoise(value & 0x0F);
                return;
            }
            tone[latchedChannel] = tone[latchedChannel] & 0x3F0 | value & 0x0F;
            return;
        }

        if (latchedVolume)
        {
            volume[latchedChannel] = value & 0x0F;
            return;
        }
        if (latchedChannel == 3)
        {
            WriteNoise(value & 0x0F);
            return;
        }
        tone[latchedChannel] = tone[latchedChannel] & 0x0F | (value & 0x3F) << 4;
    }

    private void WriteNoise(int value)
    {
        noiseControl = value & 0x07;
        // Any write to the noise register restarts the sequence, which is what makes
        // a drum hit sound the same every time.
        noiseShift = 0x4000;
    }

    /// <summary>The Game Gear's stereo port: the high nibble is the left ear, the low
    /// nibble the right, one bit per channel.</summary>
    public void WriteStereo(byte value)
    {
        if (gameGear)
            stereo = value;
    }

    public void Step(int cycles)
    {
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            // The chip sees one clock in sixteen, so only every sixteenth processor
            // cycle moves it.
            if ((++divider & 0x0F) != 0)
                continue;
            StepChannels();
            Mix();
            EmitSample();
        }
    }

    private int divider;

    private void StepChannels()
    {
        for (var channel = 0; channel < 3; channel++)
        {
            if (--counter[channel] > 0)
                continue;
            counter[channel] = tone[channel];
            output[channel] = !output[channel];
        }

        if (--counter[3] > 0)
            return;
        // The noise generator either has a divider of its own or borrows the third
        // channel's, which is how a game sweeps the pitch of an explosion.
        counter[3] = (noiseControl & 0x03) switch
        {
            0 => 0x10,
            1 => 0x20,
            2 => 0x40,
            _ => tone[2],
        };
        StepNoise();
    }

    private void StepNoise()
    {
        var bit = (noiseControl & 0x04) != 0
            // White noise taps two bits and feeds back their difference; periodic
            // noise just cycles the register, which is a buzz rather than a hiss.
            ? (noiseShift ^ noiseShift >> 1) & 1
            : noiseShift & 1;
        noiseShift = noiseShift >> 1 | bit << 14;
        output[3] = (noiseShift & 1) != 0;
    }

    private void Mix()
    {
        float left = 0, right = 0;
        for (var channel = 0; channel < 4; channel++)
        {
            // A channel whose divider is one or zero is not making a tone a person can
            // hear; the hardware runs it flat out and the result is silence.
            var level = channel < 3 && tone[channel] <= 1
                ? 0
                : (output[channel] ? 1f : -1f) * Volumes[volume[channel]];
            if ((stereo >> (4 + channel) & 1) != 0)
                left += level;
            if ((stereo >> channel & 1) != 0)
                right += level;
        }
        leftSum += left / 4;
        rightSum += right / 4;
        sampleCount++;
    }

    private void EmitSample()
    {
        sampleTimer += SampleRate;
        if (sampleTimer < Clock)
            return;
        sampleTimer -= Clock;

        var scale = sampleCount == 0 ? 0 : 1f / sampleCount;
        var left = Math.Clamp(leftSum * scale, -1f, 1f);
        var right = Math.Clamp(rightSum * scale, -1f, 1f);
        leftSum = rightSum = 0;
        sampleCount = 0;

        // Two channels' worth of samples, interleaved, because a Game Gear can put a
        // sound in one ear — and a Master System simply puts the same one in both.
        Enqueue((short)(left * short.MaxValue), (short)(right * short.MaxValue));
    }

    /// <summary>How many more values would fit. One slot is always left empty, because
    /// a full queue and an empty one would otherwise look the same.</summary>
    private int Room => (queueRead - queueWrite - 1 + queue.Length) % queue.Length;

    /// <summary>Both ears at once, and the oldest pair dropped when there is no room
    /// for them — never a single value. A queue that lost one would hand the browser
    /// every sample after it in the wrong ear, and it fills whenever the sound is
    /// switched off: nothing is draining it then, and the chip plays on regardless.</summary>
    private void Enqueue(short left, short right)
    {
        if (Room < 2)
            queueRead = (queueRead + 2) % queue.Length;
        queue[queueWrite] = left;
        queueWrite = (queueWrite + 1) % queue.Length;
        queue[queueWrite] = right;
        queueWrite = (queueWrite + 1) % queue.Length;
    }

    public int ReadAudio(short[] destination)
    {
        // Whole pairs only, for the same reason.
        var limit = destination.Length - destination.Length % 2;
        var written = 0;
        while (written < limit && queueRead != queueWrite)
        {
            destination[written++] = queue[queueRead];
            queueRead = (queueRead + 1) % queue.Length;
        }
        return written;
    }

    /// <summary>The chip, and nothing about how often the browser has been asking for
    /// sound: the resampling accumulators and the queue are the player's business, and
    /// a save state that carried them would make a muted console diverge from an
    /// unmuted one.</summary>
    internal void Save(ref StateWriter state)
    {
        state.Write(tone);
        state.Write(volume);
        state.Write(counter);
        for (var channel = 0; channel < 4; channel++)
            state.Write(output[channel]);
        state.Write(latchedChannel);
        state.Write(latchedVolume);
        state.Write(noiseShift);
        state.Write(noiseControl);
        state.Write(stereo);
        state.Write(divider);
    }

    internal void Load(ref StateReader state)
    {
        state.Read(tone);
        state.Read(volume);
        state.Read(counter);
        for (var channel = 0; channel < 4; channel++)
            output[channel] = state.ReadBool();
        latchedChannel = state.ReadInt32();
        latchedVolume = state.ReadBool();
        noiseShift = state.ReadInt32();
        noiseControl = state.ReadInt32();
        stereo = state.ReadByte();
        divider = state.ReadInt32();
    }
}
