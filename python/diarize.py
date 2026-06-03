from __future__ import annotations

import argparse
import json
import os
import re
import sys


def configure_cache_environment() -> None:
    app_data = os.environ.get("APPDATA") or os.path.expanduser("~")
    cache_root = os.path.join(app_data, "LaptopOutputRecorder", "Cache")

    defaults = {
        "MPLCONFIGDIR": os.path.join(cache_root, "matplotlib"),
        "HF_HOME": os.path.join(cache_root, "huggingface"),
        "HUGGINGFACE_HUB_CACHE": os.path.join(cache_root, "huggingface", "hub"),
        "TORCH_HOME": os.path.join(cache_root, "torch"),
    }

    for name, value in defaults.items():
        os.environ.setdefault(name, value)
        os.makedirs(os.environ[name], exist_ok=True)


def main() -> int:
    configure_cache_environment()

    parser = argparse.ArgumentParser(description="Run pyannote speaker diarization.")
    parser.add_argument("--audio", required=True, help="Path to the WAV file to diarize.")
    parser.add_argument("--output-json", required=True, help="Path for JSON diarization output.")
    parser.add_argument("--output-rttm", required=True, help="Path for RTTM diarization output.")
    parser.add_argument("--output-voiceprints", required=True, help="Path for speaker voiceprint output.")
    parser.add_argument("--model", default="pyannote/speaker-diarization-community-1")
    parser.add_argument("--embedding-model", default="pyannote/embedding")
    parser.add_argument("--device", default="auto", choices=["auto", "cpu", "cuda"])
    parser.add_argument("--token", default=os.environ.get("HF_TOKEN", ""))
    args = parser.parse_args()

    if not args.token:
        raise RuntimeError(
            "Hugging Face token is required. Set HF_TOKEN or configure diarization.huggingFaceToken."
        )

    from pyannote.audio import Pipeline
    import torch

    device = choose_device(args.device, torch)
    print(f"pyannote device: {device}", flush=True)

    pipeline = Pipeline.from_pretrained(args.model, token=args.token)
    pipeline.to(device)
    audio = load_audio_for_pyannote(args.audio, torch, device)
    output = pipeline(audio)

    diarization = getattr(output, "speaker_diarization", output)
    exclusive = getattr(output, "exclusive_speaker_diarization", None)

    result = {
        "model": args.model,
        "audio": os.path.abspath(args.audio),
        "segments": collect_segments(diarization),
        "exclusiveSegments": collect_segments(exclusive) if exclusive is not None else [],
    }

    with open(args.output_json, "w", encoding="utf-8") as output_file:
        json.dump(result, output_file, indent=2)

    write_rttm(args.output_rttm, result["segments"])
    write_voiceprints(
        args.output_voiceprints,
        audio,
        result["exclusiveSegments"] or result["segments"],
        args.embedding_model,
        args.token,
        device,
    )

    return 0


def choose_device(requested: str, torch_module):
    if requested == "cuda":
        if not torch_module.cuda.is_available():
            raise RuntimeError(
                "diarization.device is 'cuda', but torch.cuda.is_available() is false. "
                "Install a CUDA-enabled PyTorch build or set device to 'auto'/'cpu'."
            )
        return torch_module.device("cuda")

    if requested == "auto" and torch_module.cuda.is_available():
        return torch_module.device("cuda")

    return torch_module.device("cpu")


def load_audio_for_pyannote(audio_path: str, torch_module, device) -> dict:
    import soundfile as sf

    waveform, sample_rate = sf.read(audio_path, dtype="float32", always_2d=True)

    if waveform.shape[1] > 1:
        waveform = waveform.mean(axis=1, keepdims=True)

    return {
        "uri": safe_uri(audio_path),
        "waveform": torch_module.from_numpy(waveform.T).contiguous().to(device),
        "sample_rate": int(sample_rate),
    }


def safe_uri(audio_path: str) -> str:
    name = os.path.splitext(os.path.basename(audio_path))[0]
    sanitized = re.sub(r"[^A-Za-z0-9_.-]+", "_", name).strip("_")
    return sanitized or "recording"


def write_rttm(path: str, segments: list[dict[str, float | str]]) -> None:
    uri = safe_uri(path)

    with open(path, "w", encoding="utf-8") as output_file:
        for segment in segments:
            start = float(segment["start"])
            end = float(segment["end"])
            duration = max(0.0, end - start)
            speaker = str(segment["speaker"])
            output_file.write(
                f"SPEAKER {uri} 1 {start:.3f} {duration:.3f} <NA> <NA> {speaker} <NA> <NA>\n"
            )


def write_voiceprints(
    path: str,
    audio: dict,
    segments: list[dict[str, float | str]],
    embedding_model: str,
    token: str,
    device,
) -> None:
    import numpy as np
    from pyannote.audio import Inference, Model

    model = Model.from_pretrained(embedding_model, token=token)
    model.to(device)
    inference = Inference(model, window="whole")

    waveform = audio["waveform"]
    sample_rate = int(audio["sample_rate"])
    speakers: dict[str, list[dict[str, float | str]]] = {}

    for segment in segments:
        start = float(segment["start"])
        end = float(segment["end"])
        if end - start < 0.5:
            continue

        speakers.setdefault(str(segment["speaker"]), []).append(segment)

    voiceprints = []
    for speaker, speaker_segments in sorted(speakers.items()):
        weighted_embeddings = []
        total_duration = 0.0
        used_segments = []

        for segment in speaker_segments:
            start = float(segment["start"])
            end = float(segment["end"])
            start_sample = max(0, int(round(start * sample_rate)))
            end_sample = min(waveform.shape[1], int(round(end * sample_rate)))

            if end_sample <= start_sample:
                continue

            segment_waveform = waveform[:, start_sample:end_sample]
            duration = (end_sample - start_sample) / sample_rate
            if duration < 0.5:
                continue

            embedding = np.asarray(
                inference({"waveform": segment_waveform, "sample_rate": sample_rate})
            ).reshape(-1)
            weighted_embeddings.append(embedding * duration)
            total_duration += duration
            used_segments.append(
                {
                    "start": round(start, 3),
                    "end": round(end, 3),
                    "duration": round(duration, 3),
                }
            )

        if not weighted_embeddings or total_duration <= 0:
            continue

        centroid = np.sum(weighted_embeddings, axis=0) / total_duration
        norm = np.linalg.norm(centroid)
        if norm > 0:
            centroid = centroid / norm

        voiceprints.append(
            {
                "speaker": speaker,
                "dimension": int(centroid.shape[0]),
                "duration": round(total_duration, 3),
                "segmentCount": len(used_segments),
                "segments": used_segments,
                "embedding": [round(float(value), 8) for value in centroid.tolist()],
            }
        )

    with open(path, "w", encoding="utf-8") as output_file:
        json.dump(
            {
                "embeddingModel": embedding_model,
                "audio": audio["uri"],
                "sampleRate": sample_rate,
                "voiceprints": voiceprints,
            },
            output_file,
            indent=2,
        )


def collect_segments(annotation) -> list[dict[str, float | str]]:
    segments: list[dict[str, float | str]] = []

    for segment, _track, speaker in annotation.itertracks(yield_label=True):
        segments.append(
            {
                "start": round(float(segment.start), 3),
                "end": round(float(segment.end), 3),
                "speaker": str(speaker),
            }
        )

    return segments


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        raise
