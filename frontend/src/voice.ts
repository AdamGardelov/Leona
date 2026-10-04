import { api, send } from './api';

// Voice for Leona: recordings are turned into text on the computer (KB-Whisper), and replies are read aloud
// with the device's own voices. Recording needs a secure page: the computer itself or the Tailscale https
// address, not plain http on the home network.

export function canRecord() {
  return window.isSecureContext && Boolean(navigator.mediaDevices?.getUserMedia);
}

export type Recording = {
  // Stops and returns 16 kHz mono WAV, or null when nothing but silence was heard.
  stop: () => Promise<Blob | null>;
  cancel: () => void;
};

const rate = 16000;
const maxSeconds = 60;

export async function record(
  onLevel: (level: number) => void,
  onLimit: () => void,
): Promise<Recording> {
  const stream = await navigator.mediaDevices.getUserMedia({
    audio: {
      channelCount: 1,
      echoCancellation: true,
      noiseSuppression: true,
      autoGainControl: true,
    },
  });
  const context = new AudioContext();
  await context.resume();
  const source = context.createMediaStreamSource(stream);
  // ScriptProcessor is old but works in every browser, including iOS home screen apps.
  const processor = context.createScriptProcessor(4096, 1, 1);
  const chunks: Float32Array[] = [];
  let length = 0;
  let peak = 0;
  processor.onaudioprocess = (event) => {
    const input = event.inputBuffer.getChannelData(0);
    chunks.push(new Float32Array(input));
    length += input.length;
    let level = 0;
    for (const sample of input) {
      level = Math.max(level, Math.abs(sample));
    }
    peak = Math.max(peak, level);
    onLevel(level);
    if (length / context.sampleRate >= maxSeconds) {
      onLimit();
    }
  };
  source.connect(processor);
  processor.connect(context.destination);

  function close() {
    processor.disconnect();
    source.disconnect();
    stream.getTracks().forEach((track) => track.stop());
    void context.close();
  }

  return {
    cancel: close,
    async stop() {
      close();
      if (peak < 0.02 || length < context.sampleRate * 0.4) {
        return null;
      }
      const samples = new Float32Array(length);
      let offset = 0;
      for (const chunk of chunks) {
        samples.set(chunk, offset);
        offset += chunk.length;
      }
      return wav(resample(samples, context.sampleRate));
    },
  };
}

// Averages the samples that fall into each output sample, which also filters out what 16 kHz cannot hold.
function resample(input: Float32Array, from: number) {
  if (from === rate) {
    return input;
  }
  const ratio = from / rate;
  const output = new Float32Array(Math.floor(input.length / ratio));
  for (let i = 0; i < output.length; i++) {
    const start = Math.floor(i * ratio);
    const end = Math.min(input.length, Math.floor((i + 1) * ratio));
    let sum = 0;
    for (let j = start; j < end; j++) {
      sum += input[j];
    }
    output[i] = sum / Math.max(1, end - start);
  }
  return output;
}

function wav(samples: Float32Array) {
  const buffer = new ArrayBuffer(44 + samples.length * 2);
  const view = new DataView(buffer);
  const text = (offset: number, value: string) => {
    for (let i = 0; i < value.length; i++) {
      view.setUint8(offset + i, value.charCodeAt(i));
    }
  };
  text(0, 'RIFF');
  view.setUint32(4, 36 + samples.length * 2, true);
  text(8, 'WAVE');
  text(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, rate, true);
  view.setUint32(28, rate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  text(36, 'data');
  view.setUint32(40, samples.length * 2, true);
  samples.forEach((sample, i) => {
    view.setInt16(44 + i * 2, Math.max(-1, Math.min(1, sample)) * 0x7fff, true);
  });
  return new Blob([buffer], { type: 'audio/wav' });
}

// The recording's type (audio/wav) goes along as its Content-Type.
export async function transcribe(audio: Blob) {
  const result = await send<{ text?: string }>('/speech/transcribe?language=sv', 'POST', audio);
  return String(result?.text ?? '').trim();
}

export async function speechAvailable() {
  try {
    const result = await api<{ available: boolean }>('/speech');
    return result.available;
  } catch {
    return false;
  }
}

// Reading aloud: the device's Swedish voice (Alva on iPhone) when the text is Swedish.
export function canSpeak() {
  return 'speechSynthesis' in window;
}

function voiceFor(language: string) {
  const voices = window.speechSynthesis
    .getVoices()
    .filter((v) => v.lang.replace('_', '-').startsWith(language));
  return (
    voices.find((v) => /premium|enhanced|förbättrad/i.test(v.name)) ??
    voices.find((v) => /alva|klara|oskar/i.test(v.name)) ??
    voices.find((v) => v.localService) ??
    voices[0]
  );
}

// iOS only lets a page speak after it has spoken once during a tap, so the microphone and speaker
// buttons call this first.
export function unlockSpeech() {
  if (!canSpeak()) {
    return;
  }
  const silent = new SpeechSynthesisUtterance(' ');
  silent.volume = 0;
  window.speechSynthesis.speak(silent);
}

export function stopSpeaking() {
  if (canSpeak()) {
    window.speechSynthesis.cancel();
  }
}

export function speak(markdown: string, onEnd?: () => void) {
  const text = speakable(markdown);
  if (!canSpeak() || !text) {
    onEnd?.();
    return;
  }
  window.speechSynthesis.cancel();
  const swedish = /[åäö]|\b(och|att|det|är|jag|du|inte)\b/i.test(text);
  // Long replies are read sentence group by sentence group; iOS cuts very long utterances short.
  const parts = text.match(/[^.!?\n]+[.!?]*\s*/g)?.reduce<string[]>((groups, sentence) => {
    const last = groups[groups.length - 1];
    if (last !== undefined && last.length + sentence.length < 220) {
      groups[groups.length - 1] = last + sentence;
    } else {
      groups.push(sentence);
    }
    return groups;
  }, []) ?? [text];
  const voice = voiceFor(swedish ? 'sv' : 'en');
  parts.forEach((part, index) => {
    const utterance = new SpeechSynthesisUtterance(part.trim());
    utterance.lang = swedish ? 'sv-SE' : 'en-US';
    if (voice) {
      utterance.voice = voice;
    }
    utterance.rate = 1.05;
    if (index === parts.length - 1) {
      utterance.onend = () => onEnd?.();
      utterance.onerror = () => onEnd?.();
    }
    window.speechSynthesis.speak(utterance);
  });
}

// What a reply sounds like read aloud: no code, links, sources or Markdown signs.
function speakable(markdown: string) {
  return markdown
    .split(/\n#{1,3} Sources\n/)[0]
    .replace(/```draft\n([\s\S]*?)```/g, '$1')
    .replace(/```[\s\S]*?```/g, ' (kod) ')
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/https?:\/\/\S+/g, '')
    .replace(/`([^`]*)`/g, '$1')
    .replace(/^\s*[-*+]\s+/gm, '')
    .replace(/^\s*#+\s*/gm, '')
    .replace(/[*_~|>]/g, '')
    .replace(/\n{2,}/g, '.\n')
    .replace(/\s+/g, ' ')
    .trim();
}
