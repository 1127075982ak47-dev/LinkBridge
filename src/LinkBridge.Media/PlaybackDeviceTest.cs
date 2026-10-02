using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

[assembly: InternalsVisibleTo("PlaybackDeviceTestProbe")]

namespace LinkBridge.Media;

public static class PlaybackDeviceTest
{
    public static Task PlayAsync(string endpointId, CancellationToken cancellationToken = default)
        => PlayOnWorkerAsync(endpointId, cancellationToken, silence: false);

    // The native probe uses the exact Mac UID/open/pump/stop/dispose path with zero PCM only.
    internal static Task PlaySilentlyForProbeAsync(string endpointId, CancellationToken cancellationToken = default)
        => PlayOnWorkerAsync(endpointId, cancellationToken, silence: true);

    private static Task PlayOnWorkerAsync(string endpointId, CancellationToken cancellationToken, bool silence)
    {
        ArgumentNullException.ThrowIfNull(endpointId);
        cancellationToken.ThrowIfCancellationRequested();
        // Device resolution, native construction and disposal never execute on the UI thread.
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                if (silence) throw new PlatformNotSupportedException("静音原生探针仅用于 Mac 指定 UID 验证。");
                using var enumerator = new MMDeviceEnumerator();
                using var endpoint = endpointId.Length == 0
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : enumerator.GetDevice(endpointId);
                if (endpoint.DataFlow != DataFlow.Render || endpoint.State != DeviceState.Active)
                    throw new InvalidOperationException("所选播放输出已断开或不是输出端点；未使用默认设备。");
                using var player = new WasapiOut(endpoint, AudioClientShareMode.Shared, false, 80);
                var tone = new SignalGenerator(48000, 2) { Frequency = 660, Gain = 0.08, Type = SignalGeneratorType.Sin };
                var fade = new FadeInOutSampleProvider(tone, true);
                player.Init(fade);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    player.Play();
                    fade.BeginFadeIn(40);
                    await Task.Delay(450, cancellationToken).ConfigureAwait(false);
                    fade.BeginFadeOut(100);
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }
                finally { player.Stop(); }
                return;
            }
            if (OperatingSystem.IsMacOS())
            {
                var uid = CoreAudioEndpoints.ResolveOutputUid(endpointId);
                cancellationToken.ThrowIfCancellationRequested();
                using var output = new MacPcmOutput(uid);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await PumpMacToneAsync(block => output.Write(block.Span), () => output.Fault,
                        cancellationToken, silence).ConfigureAwait(false);
                }
                finally { output.Stop(); } // using also disposes if Stop reports a native failure.
                return;
            }
            throw new PlatformNotSupportedException();
        }, cancellationToken);
    }

    /// <summary>One short tone only: paced 20ms stereo frames, bounded below the existing 80ms native sink ring.</summary>
    internal static async Task PumpMacToneAsync(Action<ReadOnlyMemory<byte>> write, Func<Exception?> fault,
        CancellationToken cancellationToken, bool silence = false)
    {
        const int rate = 48000, framesPerBlock = 960, blocks = 30;
        var pcm = new byte[framesPerBlock * 4];
        var clock = Stopwatch.StartNew();
        void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fault() is { } error) throw new IOException("所选 Mac 音响试听失败。", error);
        }
        for (var block = 0; block < blocks; block++)
        {
            Check();
            for (var frame = 0; frame < framesPerBlock; frame++)
            {
                var time = (block * framesPerBlock + frame) / (double)rate;
                var envelope = Math.Min(1, time / 0.04) * Math.Clamp((0.55 - time) / 0.1, 0, 1);
                var sample = silence ? (short)0 : (short)Math.Round(Math.Sin(2 * Math.PI * 660 * time) * envelope * 0.08 * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * 4), sample);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * 4 + 2), sample);
            }
            // Never catch up by dumping multiple old blocks into the sink after a scheduling stall.
            Check();
            var written = clock.Elapsed;
            write(pcm);
            var wait = TimeSpan.FromMilliseconds(20) - (clock.Elapsed - written);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            Check();
        }
        // Let the 30ms native queue and at most a few paced blocks drain before immediate disposal.
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        Check();
    }
}
