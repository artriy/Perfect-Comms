using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace SipsorceryAnswer
{
    internal static class Program
    {
        private static async Task<int> Main()
        {
            var config = new RTCConfiguration { iceServers = new List<RTCIceServer>() };
            var pc = new RTCPeerConnection(config);

            var opus = new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1");
            var track = new MediaStreamTrack(
                SDPMediaTypesEnum.audio,
                false,
                new List<SDPAudioVideoMediaFormat> { new SDPAudioVideoMediaFormat(opus) },
                MediaStreamStatusEnum.SendRecv);
            pc.addTrack(track);

            int rtpCount = 0;
            pc.OnRtpPacketReceived += (IPEndPoint ep, SDPMediaTypesEnum media, RTPPacket pkt) =>
            {
                int n = Interlocked.Increment(ref rtpCount);
                if (n == 1 || n % 50 == 0)
                {
                    Console.Error.WriteLine($"[answer] RTP packets received: {n} (media={media}, pt={pkt.Header.PayloadType}, from={ep})");
                }
            };

            pc.onicecandidate += (RTCIceCandidate c) =>
            {
                if (c != null)
                {
                    Console.Error.WriteLine($"[answer] local-candidate {c.candidate}");
                }
            };
            pc.oniceconnectionstatechange += (RTCIceConnectionState s) =>
                Console.Error.WriteLine($"[answer] ICE connection state: {s}");

            byte[] opusFrame = new byte[80];
            opusFrame[0] = 0xFC;
            var cts = new CancellationTokenSource();
            int senderStarted = 0;
            pc.onconnectionstatechange += (RTCPeerConnectionState s) =>
            {
                Console.Error.WriteLine($"[answer] peer connection state: {s}");
                if (s == RTCPeerConnectionState.connected &&
                    Interlocked.CompareExchange(ref senderStarted, 1, 0) == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        while (!cts.IsCancellationRequested &&
                               pc.connectionState == RTCPeerConnectionState.connected)
                        {
                            pc.SendAudio(960, opusFrame);
                            await Task.Delay(20);
                        }
                    });
                }
                if (s == RTCPeerConnectionState.failed || s == RTCPeerConnectionState.closed)
                {
                    cts.Cancel();
                }
            };

            string offerLine = Console.In.ReadLine();
            if (string.IsNullOrWhiteSpace(offerLine))
            {
                Console.Error.WriteLine("[answer] no offer received on stdin");
                return 1;
            }

            string offerSdp;
            using (var doc = JsonDocument.Parse(offerLine))
            {
                offerSdp = doc.RootElement.GetProperty("sdp").GetString();
            }

            var setRes = pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = offerSdp
            });
            Console.Error.WriteLine($"[answer] setRemoteDescription result: {setRes}");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (pc.iceGatheringState != RTCIceGatheringState.complete && sw.ElapsedMilliseconds < 3000)
            {
                await Task.Delay(50);
            }
            Console.Error.WriteLine($"[answer] iceGatheringState={pc.iceGatheringState} after {sw.ElapsedMilliseconds}ms");

            var answer = pc.createAnswer(null);
            await pc.setLocalDescription(answer);

            bool hasCandidates = answer.sdp != null && answer.sdp.Contains("a=candidate");
            Console.Error.WriteLine($"[answer] answer sdp candidates embedded: {hasCandidates}");

            string outJson = JsonSerializer.Serialize(new { type = "answer", sdp = answer.sdp });
            Console.Out.WriteLine(outJson);
            Console.Out.Flush();
            Console.Error.WriteLine($"[answer] answer SDP sent ({answer.sdp?.Length ?? 0} bytes)");

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                if (pc.connectionState == RTCPeerConnectionState.failed ||
                    pc.connectionState == RTCPeerConnectionState.closed)
                {
                    break;
                }
            }

            Console.Error.WriteLine($"[answer] FINAL state={pc.connectionState} ice={pc.iceConnectionState} rtp_received={rtpCount}");
            pc.close();
            return rtpCount > 0 ? 0 : 3;
        }
    }
}
