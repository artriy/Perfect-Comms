use opusic_c::sys;
use std::env;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::Path;
use std::ptr::NonNull;

const FIXTURE_MAGIC: &[u8; 8] = b"PCOPUS01";
const FIXTURE_FRAMES: usize = 100;
const FIXTURE_MAX_PACKET_BYTES: usize = 1_275;
const ENCODER_PACKET_BYTES: usize = 4_000;
const SAMPLE_RATE: i32 = 48_000;
const FRAME_SIZE: usize = 960;
const MANAGED_TONE_FREQUENCY: f64 = 659.255_113_825_739_8;
const NATIVE_TONE_FREQUENCY: f64 = 173.0;

fn main() {
    if let Err(code) = run() {
        eprintln!("starlight-opus-probe.failed code={code}");
        std::process::exit(1);
    }
}

fn run() -> Result<(), &'static str> {
    let mut args = env::args_os();
    let _program = args.next();
    if args.next().as_deref() != Some(std::ffi::OsStr::new("exchange")) {
        return Err("arguments");
    }
    let managed_path = args.next().ok_or("arguments")?;
    let native_path = args.next().ok_or("arguments")?;
    if args.next().is_some() {
        return Err("arguments");
    }

    let managed_packets = read_fixture(Path::new(&managed_path))?;
    validate_managed_packets(&managed_packets)?;
    let native_packets = encode_native_packets()?;
    validate_native_recovery(&native_packets)?;
    write_fixture(Path::new(&native_path), &native_packets)
}

fn read_fixture(path: &Path) -> Result<Vec<Vec<u8>>, &'static str> {
    let metadata = fs::metadata(path).map_err(|_| "managed-fixture")?;
    if !metadata.is_file() || metadata.len() > 512 * 1024 {
        return Err("managed-fixture");
    }
    let mut input = Vec::with_capacity(metadata.len() as usize);
    File::open(path)
        .and_then(|mut file| file.read_to_end(&mut input))
        .map_err(|_| "managed-fixture")?;
    let mut offset = 0usize;
    if take(&input, &mut offset, FIXTURE_MAGIC.len())? != FIXTURE_MAGIC {
        return Err("managed-fixture");
    }
    let count = read_u32(&input, &mut offset)? as usize;
    if count != FIXTURE_FRAMES {
        return Err("managed-fixture");
    }
    let mut packets = Vec::with_capacity(count);
    for _ in 0..count {
        let length = read_u32(&input, &mut offset)? as usize;
        if length == 0 || length > FIXTURE_MAX_PACKET_BYTES {
            return Err("managed-fixture");
        }
        packets.push(take(&input, &mut offset, length)?.to_vec());
    }
    if offset != input.len() {
        return Err("managed-fixture");
    }
    Ok(packets)
}

fn write_fixture(path: &Path, packets: &[Vec<u8>]) -> Result<(), &'static str> {
    if packets.len() != FIXTURE_FRAMES {
        return Err("native-fixture");
    }
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|_| "native-fixture")?;
    file.write_all(FIXTURE_MAGIC)
        .and_then(|_| file.write_all(&(packets.len() as u32).to_le_bytes()))
        .map_err(|_| "native-fixture")?;
    for packet in packets {
        if packet.is_empty() || packet.len() > FIXTURE_MAX_PACKET_BYTES {
            return Err("native-fixture");
        }
        let length = u32::try_from(packet.len()).map_err(|_| "native-fixture")?;
        file.write_all(&length.to_le_bytes())
            .and_then(|_| file.write_all(packet))
            .map_err(|_| "native-fixture")?;
    }
    file.flush().map_err(|_| "native-fixture")
}

fn validate_managed_packets(packets: &[Vec<u8>]) -> Result<(), &'static str> {
    let baseline = decode_baseline(
        packets,
        MANAGED_TONE_FREQUENCY,
        "managed-decoder",
        "managed-decode",
        "managed-tone",
    )?;
    if count_fec_recoveries(
        packets,
        &baseline,
        "managed-decoder",
        "managed-decode",
        "managed-fec",
    )? == 0
    {
        return Err("managed-fec");
    }
    Ok(())
}

fn encode_native_packets() -> Result<Vec<Vec<u8>>, &'static str> {
    let mut encoder = OpusEncoder::new()?;
    let mut frame = [0.0f32; FRAME_SIZE];
    let mut packets = Vec::with_capacity(FIXTURE_FRAMES);
    let mut sample_offset = 0usize;
    for _ in 0..FIXTURE_FRAMES {
        fill_native_fixture(&mut frame, sample_offset);
        sample_offset += FRAME_SIZE;
        let packet = encoder.encode(&frame)?.to_vec();
        if packet.is_empty() || packet.len() > FIXTURE_MAX_PACKET_BYTES {
            return Err("native-encode");
        }
        packets.push(packet);
    }
    Ok(packets)
}

fn validate_native_recovery(packets: &[Vec<u8>]) -> Result<(), &'static str> {
    let baseline = decode_baseline(
        packets,
        NATIVE_TONE_FREQUENCY,
        "native-decoder",
        "native-decode",
        "native-tone",
    )?;
    if count_fec_recoveries(
        packets,
        &baseline,
        "native-decoder",
        "native-decode",
        "native-fec",
    )? == 0
    {
        return Err("native-fec");
    }

    const LOST_FRAMES: usize = 5;
    let recovery_index = packets.len() - 1;
    let gap_start = recovery_index - LOST_FRAMES;
    let mut dred_decoder = OpusDecoder::new("native-dred-decode")?;
    let mut frame = [0.0f32; FRAME_SIZE];
    for packet in &packets[..gap_start] {
        if dred_decoder.decode(packet, &mut frame, "native-dred-decode")? != FRAME_SIZE {
            return Err("native-dred-decode");
        }
    }
    let info = dred_decoder
        .parse_dred(&packets[recovery_index], LOST_FRAMES * FRAME_SIZE)?
        .ok_or("native-dred-absent")?;
    let mut recovered = 0usize;
    let mut total_energy = 0.0f32;
    for lost in (2..=LOST_FRAMES).rev() {
        let offset_samples = lost * FRAME_SIZE;
        if offset_samples > info.oldest_offset_samples || offset_samples <= info.end_silence_samples
        {
            continue;
        }
        if dred_decoder.decode_dred(offset_samples, &mut frame)? != FRAME_SIZE
            || frame.iter().any(|sample| !sample.is_finite())
        {
            return Err("native-dred-decode");
        }
        total_energy += frame.iter().map(|sample| sample * sample).sum::<f32>();
        recovered += 1;
    }
    if recovered < 3 || total_energy <= 1e-6 {
        return Err("native-dred-decode");
    }
    Ok(())
}

fn count_fec_recoveries(
    packets: &[Vec<u8>],
    baseline: &[[f32; FRAME_SIZE]],
    create_error: &'static str,
    decode_error: &'static str,
    fec_error: &'static str,
) -> Result<usize, &'static str> {
    for index in 1..packets.len() {
        let mut decoder = OpusDecoder::new(create_error)?;
        let mut frame = [0.0f32; FRAME_SIZE];
        for packet in &packets[..index - 1] {
            if decoder.decode(packet, &mut frame, decode_error)? != FRAME_SIZE {
                return Err(decode_error);
            }
        }
        let recovered = decoder.decode_fec(&packets[index], &mut frame, fec_error)?;
        if recovered == FRAME_SIZE && is_finite_correlated_pair(&frame, &baseline[index - 1]) {
            return Ok(1);
        }
    }
    Ok(0)
}

fn decode_baseline(
    packets: &[Vec<u8>],
    frequency: f64,
    create_error: &'static str,
    decode_error: &'static str,
    tone_error: &'static str,
) -> Result<Vec<[f32; FRAME_SIZE]>, &'static str> {
    let mut decoder = OpusDecoder::new(create_error)?;
    let mut baseline = Vec::with_capacity(packets.len());
    let mut correlated = 0usize;
    for (index, packet) in packets.iter().enumerate() {
        let mut frame = [0.0f32; FRAME_SIZE];
        if decoder.decode(packet, &mut frame, decode_error)? != FRAME_SIZE {
            return Err(decode_error);
        }
        if index >= 2 && is_finite_correlated_tone(&frame, frequency) {
            correlated += 1;
        }
        baseline.push(frame);
    }
    if correlated < FIXTURE_FRAMES - 4 {
        return Err(tone_error);
    }
    Ok(baseline)
}

struct OpusEncoder {
    inner: NonNull<sys::OpusEncoder>,
    packet: [u8; ENCODER_PACKET_BYTES],
}

impl OpusEncoder {
    fn new() -> Result<Self, &'static str> {
        let mut error = sys::OPUS_OK;
        let raw = unsafe {
            sys::opus_encoder_create(SAMPLE_RATE, 1, sys::OPUS_APPLICATION_VOIP, &mut error)
        };
        let inner = NonNull::new(raw).ok_or("native-encoder")?;
        let mut encoder = Self {
            inner,
            packet: [0; ENCODER_PACKET_BYTES],
        };
        encoder.set(sys::OPUS_SET_BITRATE_REQUEST, 48_000)?;
        encoder.set(sys::OPUS_SET_COMPLEXITY_REQUEST, 10)?;
        encoder.set(sys::OPUS_SET_SIGNAL_REQUEST, sys::OPUS_AUTO)?;
        encoder.set(sys::OPUS_SET_VBR_REQUEST, 1)?;
        encoder.set(sys::OPUS_SET_VBR_CONSTRAINT_REQUEST, 1)?;
        encoder.set(sys::OPUS_SET_DTX_REQUEST, 0)?;
        encoder.set(sys::OPUS_SET_INBAND_FEC_REQUEST, 2)?;
        encoder.set(sys::OPUS_SET_PACKET_LOSS_PERC_REQUEST, 15)?;
        encoder
            .set(sys::OPUS_SET_DRED_DURATION_REQUEST, 10)
            .map_err(|_| "native-dred-ctl")?;
        if encoder
            .get(sys::OPUS_GET_DRED_DURATION_REQUEST)
            .map_err(|_| "native-dred-ctl")?
            != 10
        {
            return Err("native-dred-ctl");
        }
        Ok(encoder)
    }

    fn set(&mut self, request: i32, value: i32) -> Result<(), &'static str> {
        let status = unsafe { sys::opus_encoder_ctl(self.inner.as_ptr(), request, value) };
        if status < 0 {
            Err("native-encoder")
        } else {
            Ok(())
        }
    }

    fn get(&mut self, request: i32) -> Result<i32, &'static str> {
        let mut value = 0i32;
        let status = unsafe { sys::opus_encoder_ctl(self.inner.as_ptr(), request, &mut value) };
        if status < 0 {
            Err("native-encoder")
        } else {
            Ok(value)
        }
    }

    fn encode<'a>(&'a mut self, pcm: &[f32; FRAME_SIZE]) -> Result<&'a [u8], &'static str> {
        let length = unsafe {
            sys::opus_encode_float(
                self.inner.as_ptr(),
                pcm.as_ptr(),
                FRAME_SIZE as i32,
                self.packet.as_mut_ptr(),
                self.packet.len() as i32,
            )
        };
        if length <= 0 {
            return Err("native-encode");
        }
        Ok(&self.packet[..length as usize])
    }
}

impl Drop for OpusEncoder {
    fn drop(&mut self) {
        unsafe { sys::opus_encoder_destroy(self.inner.as_ptr()) };
    }
}

struct DredResources {
    decoder: NonNull<sys::OpusDREDDecoder>,
    packet: NonNull<sys::OpusDRED>,
}

impl DredResources {
    fn new(error_code: &'static str) -> Result<Self, &'static str> {
        let mut decoder_error = sys::OPUS_OK;
        let decoder = NonNull::new(unsafe { sys::opus_dred_decoder_create(&mut decoder_error) })
            .ok_or(error_code)?;
        let mut packet_error = sys::OPUS_OK;
        let packet = match NonNull::new(unsafe { sys::opus_dred_alloc(&mut packet_error) }) {
            Some(packet) => packet,
            None => {
                unsafe { sys::opus_dred_decoder_destroy(decoder.as_ptr()) };
                return Err(error_code);
            }
        };
        Ok(Self { decoder, packet })
    }
}

impl Drop for DredResources {
    fn drop(&mut self) {
        unsafe {
            sys::opus_dred_free(self.packet.as_ptr());
            sys::opus_dred_decoder_destroy(self.decoder.as_ptr());
        }
    }
}

struct DredInfo {
    oldest_offset_samples: usize,
    end_silence_samples: usize,
}

struct OpusDecoder {
    inner: NonNull<sys::OpusDecoder>,
    dred: DredResources,
}

impl OpusDecoder {
    fn new(error_code: &'static str) -> Result<Self, &'static str> {
        let mut error = sys::OPUS_OK;
        let inner = NonNull::new(unsafe { sys::opus_decoder_create(SAMPLE_RATE, 1, &mut error) })
            .ok_or(error_code)?;
        let dred = match DredResources::new(error_code) {
            Ok(dred) => dred,
            Err(error) => {
                unsafe { sys::opus_decoder_destroy(inner.as_ptr()) };
                return Err(error);
            }
        };
        Ok(Self { inner, dred })
    }

    fn decode(
        &mut self,
        packet: &[u8],
        output: &mut [f32; FRAME_SIZE],
        error_code: &'static str,
    ) -> Result<usize, &'static str> {
        self.decode_packet(packet, output, false, error_code)
    }

    fn decode_fec(
        &mut self,
        packet: &[u8],
        output: &mut [f32; FRAME_SIZE],
        error_code: &'static str,
    ) -> Result<usize, &'static str> {
        self.decode_packet(packet, output, true, error_code)
    }

    fn decode_packet(
        &mut self,
        packet: &[u8],
        output: &mut [f32; FRAME_SIZE],
        fec: bool,
        error_code: &'static str,
    ) -> Result<usize, &'static str> {
        if packet.is_empty() || packet.len() > i32::MAX as usize {
            return Err(error_code);
        }
        let decoded = unsafe {
            sys::opus_decode_float(
                self.inner.as_ptr(),
                packet.as_ptr(),
                packet.len() as i32,
                output.as_mut_ptr(),
                FRAME_SIZE as i32,
                i32::from(fec),
            )
        };
        if decoded < 0 {
            Err(error_code)
        } else {
            Ok(decoded as usize)
        }
    }

    fn parse_dred(
        &mut self,
        packet: &[u8],
        maximum_samples: usize,
    ) -> Result<Option<DredInfo>, &'static str> {
        let mut dred_end = 0i32;
        let oldest = unsafe {
            sys::opus_dred_parse(
                self.dred.decoder.as_ptr(),
                self.dred.packet.as_ptr(),
                packet.as_ptr(),
                packet.len() as i32,
                maximum_samples as i32,
                SAMPLE_RATE,
                &mut dred_end,
                0,
            )
        };
        if oldest < 0 {
            Err("native-dred-decode")
        } else if oldest == 0 {
            Ok(None)
        } else {
            Ok(Some(DredInfo {
                oldest_offset_samples: oldest as usize,
                end_silence_samples: dred_end.max(0) as usize,
            }))
        }
    }

    fn decode_dred(
        &mut self,
        offset_samples: usize,
        output: &mut [f32; FRAME_SIZE],
    ) -> Result<usize, &'static str> {
        let decoded = unsafe {
            sys::opus_decoder_dred_decode_float(
                self.inner.as_ptr(),
                self.dred.packet.as_ptr(),
                offset_samples as i32,
                output.as_mut_ptr(),
                FRAME_SIZE as i32,
            )
        };
        if decoded < 0 {
            Err("native-dred-decode")
        } else {
            Ok(decoded as usize)
        }
    }
}

impl Drop for OpusDecoder {
    fn drop(&mut self) {
        unsafe { sys::opus_decoder_destroy(self.inner.as_ptr()) };
    }
}

fn fill_native_fixture(frame: &mut [f32; FRAME_SIZE], sample_offset: usize) {
    for (index, sample) in frame.iter_mut().enumerate() {
        let time = (sample_offset + index) as f32 / SAMPLE_RATE as f32;
        let envelope = 0.55f32 + 0.45f32 * (std::f32::consts::TAU * 3.7f32 * time).sin().abs();
        *sample = envelope
            * (0.22f32 * (std::f32::consts::TAU * 173.0f32 * time).sin()
                + 0.10f32 * (std::f32::consts::TAU * 347.0f32 * time).sin()
                + 0.05f32 * (std::f32::consts::TAU * 521.0f32 * time).sin());
    }
}

fn is_finite_correlated_tone(samples: &[f32], frequency: f64) -> bool {
    let mut energy = 0.0f64;
    let mut sine = 0.0f64;
    let mut cosine = 0.0f64;
    for (index, sample) in samples.iter().copied().enumerate() {
        if !sample.is_finite() {
            return false;
        }
        let phase = std::f64::consts::TAU * frequency * index as f64 / SAMPLE_RATE as f64;
        let sample = sample as f64;
        energy += sample * sample;
        sine += sample * phase.sin();
        cosine += sample * phase.cos();
    }
    if energy < samples.len() as f64 * 0.00001 {
        return false;
    }
    let correlated_energy = 2.0 * (sine * sine + cosine * cosine) / samples.len() as f64;
    correlated_energy / energy >= 0.55
}

fn is_finite_correlated_pair(actual: &[f32], expected: &[f32]) -> bool {
    if actual.len() != expected.len() {
        return false;
    }
    let mut actual_energy = 0.0f64;
    let mut expected_energy = 0.0f64;
    let mut product = 0.0f64;
    for (actual, expected) in actual.iter().copied().zip(expected.iter().copied()) {
        if !actual.is_finite() || !expected.is_finite() {
            return false;
        }
        actual_energy += (actual * actual) as f64;
        expected_energy += (expected * expected) as f64;
        product += (actual * expected) as f64;
    }
    if actual_energy < actual.len() as f64 * 0.00001
        || expected_energy < expected.len() as f64 * 0.00001
    {
        return false;
    }
    product / (actual_energy * expected_energy).sqrt() >= 0.5
}

fn read_u32(input: &[u8], offset: &mut usize) -> Result<u32, &'static str> {
    let bytes: [u8; 4] = take(input, offset, 4)?
        .try_into()
        .map_err(|_| "managed-fixture")?;
    Ok(u32::from_le_bytes(bytes))
}

fn take<'a>(input: &'a [u8], offset: &mut usize, length: usize) -> Result<&'a [u8], &'static str> {
    let end = offset.checked_add(length).ok_or("managed-fixture")?;
    let value = input.get(*offset..end).ok_or("managed-fixture")?;
    *offset = end;
    Ok(value)
}
