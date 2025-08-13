// Resolve MicVAD from global namespaces loaded via script tag in Index.cshtml
// Try to find MicVAD from several possible global locations (for compatibility with different script sources)
const MicVAD = (
  window.MicVAD ||
  (window.vad && (window.vad.MicVAD || window.vad.default)) ||
  (window.vadWeb && (window.vadWeb.MicVAD || window.vadWeb.default)) ||
  window.MicVAD
);

// DOM Elements (readable names)
const connectStartButton = document.getElementById('connect-start-button');
const stopButton = document.getElementById('stop-button');
const transcriptTextElement = document.getElementById('transcript-text');
const transcriptEmptyElement = document.getElementById('transcript-empty');
const assistantTextElement = document.getElementById('assistant-text');
const assistantEmptyElement = document.getElementById('assistant-empty');
const ttsStatusElement = document.getElementById('tts-status');
const vadModelStatusElement = document.getElementById('vad-model-status');

// State Variables
let signalRConnection = null;
let audioContext = null;
let micSourceNode = null;
let audioProcessorNode = null;
let micMediaStream = null;
let nextAudioPlaybackTime = 0; // For scheduling WebAudio playback
let isMicInitialized = false;
let currentSessionId = null;
let currentSegmentId = null;
let vadInstance = null;
let isVadModelReady = false;
const vadModelUrl = 'https://cdn.jsdelivr.net/npm/@ricky0123/vad-web@0.0.22/dist/silero_vad_legacy.onnx';

// Event Listeners
connectStartButton.addEventListener('click', async () => {
  try {
    connectStartButton.disabled = true;
    connectStartButton.textContent = 'Connecting…';
    vadModelStatusElement.textContent = 'Connecting...';
    await ensureSignalRConnection();
    await prefetchVadModel();
    await startMicrophone();
    connectStartButton.textContent = 'Connected';
  } catch (err) {
    console.error('Start failed', err);
    connectStartButton.textContent = 'Connect & Start';
    connectStartButton.disabled = false;
  }
});

stopButton.addEventListener('click', async () => {
  await stopMicrophone();
});

// SignalR Connection
async function ensureSignalRConnection() {
  // Only create a new connection if not already connected
  if (signalRConnection && signalRConnection.state === 'Connected') return;

  signalRConnection = new signalR.HubConnectionBuilder()
    .withUrl('/chat')
    .withAutomaticReconnect()
    .build();

  registerSignalREventHandlers(signalRConnection);
  await signalRConnection.start();
  currentSessionId = await signalRConnection.invoke('Join', null);

  // Update button when connected
  connectStartButton.textContent = 'Connected';
  connectStartButton.disabled = true;
}

function registerSignalREventHandlers(conn) {
  conn.on('SessionStarted', () => {});

  conn.on('FinalTranscription', ({ segmentId, text }) => {
    currentSegmentId = segmentId;
    if (transcriptEmptyElement) transcriptEmptyElement.classList.add('hidden');
    if (transcriptTextElement) transcriptTextElement.classList.remove('hidden');
    transcriptTextElement.textContent += `${text}`;
  });

  conn.on('LLMDelta', ({ segmentId, text }) => {
    if (!text) return;
    if (assistantEmptyElement) assistantEmptyElement.classList.add('hidden');
    if (assistantTextElement) assistantTextElement.classList.remove('hidden');
    assistantTextElement.textContent += text;
  });

  conn.on('LLMComplete', () => {});

  conn.on('TTSReady', ({ segmentId }) => {
    ttsStatusElement.textContent = 'Voice ready';
    if (audioContext) {
      const now = audioContext.currentTime;
      // Make sure nextAudioPlaybackTime is always ahead of current time, add a small offset to avoid glitches
      nextAudioPlaybackTime = Math.max(nextAudioPlaybackTime, now) + 0.02;
    } else {
      nextAudioPlaybackTime = 0;
    }
  });

  conn.on('TTSChunk', (audioBase64 /*, meta*/) => {
    playPcm16AudioChunk(audioBase64);
  });

  conn.on('PlaybackComplete', () => {
    transcriptTextElement.textContent += '\n';
    assistantTextElement.textContent += '\n';
    ttsStatusElement.textContent = '';
    // Reset playback time to current time to avoid drift
    nextAudioPlaybackTime = audioContext ? audioContext.currentTime : 0;
  });

  // Interrupt events are handled server-side; no-op here
  conn.on('Error', (error) => {
    console.error('SignalR Hub error', error);
  });

  // Handle connection lifecycle for button text
  conn.onreconnecting(() => {
    connectStartButton.textContent = 'Connecting…';
    connectStartButton.disabled = true;
  });
  conn.onreconnected(() => {
    connectStartButton.textContent = 'Connected';
    connectStartButton.disabled = true;
  });
  conn.onclose(() => {
    connectStartButton.textContent = 'Connect & Start';
    connectStartButton.disabled = false;
  });
}

// Microphone & VAD
async function startMicrophone() {
  if (isMicInitialized) return;

  // Create a new AudioContext with 16kHz sample rate (required for VAD and ASR)
  audioContext = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 16000 });
  micMediaStream = await navigator.mediaDevices.getUserMedia({ audio: true });

  if (!MicVAD) {
    console.error('MicVAD not available');
    return;
  }

  vadInstance = await MicVAD.new({
    positiveSpeechThreshold: 0.85,
    negativeSpeechThreshold: 0.65,
    minSpeechFrames: 5,
    preSpeechPadFrames: 5,
    redemptionFrames: 10,
    modelURL: vadModelUrl,
    onSpeechStart: () => {},
    onSpeechEnd: async (audioFrames) => {
      // --- This block normalizes the VAD output to a single Float32Array ---
      // audioFrames can be an array of Float32Array or a single Float32Array
      let floatAudio;
      if (Array.isArray(audioFrames)) {
        // Calculate total length of all frames
        const totalLength = audioFrames.reduce((sum, frame) => sum + (frame?.length || 0), 0);
        floatAudio = new Float32Array(totalLength);
        let offset = 0;
        for (const frame of audioFrames) {
          if (!(frame instanceof Float32Array)) continue;
          floatAudio.set(frame, offset); // Copy each frame into the result array
          offset += frame.length;
        }
      } else if (audioFrames instanceof Float32Array) {
        floatAudio = audioFrames;
      } else {
        // If the payload is not as expected, log a warning and skip
        console.warn('Unexpected VAD onSpeechEnd payload', audioFrames);
        return;
      }

      // Convert the audio to WAV format (mono, 16kHz, 16-bit)
      const wavBuffer = convertPcmToWav(floatAudio, 16000);
      const wavBase64 = uint8ToBase64(new Uint8Array(wavBuffer));
      await signalRConnection.invoke('AudioInput', wavBase64);
    },
    onFrameProcessed: async () => {}
  });

  vadModelStatusElement.textContent = 'Loading VAD model...';
  await vadInstance.start();
  isVadModelReady = true;
  vadModelStatusElement.textContent = 'Model ready — you can speak now.';
  // VAD ready: stay Connected label
  connectStartButton.textContent = 'Connected';
  connectStartButton.disabled = true;
  isMicInitialized = true;
}

async function stopMicrophone() {
  if (!isMicInitialized) return;
  try { await vadInstance?.pause(); } catch {}
  if (audioProcessorNode) audioProcessorNode.disconnect();
  if (micSourceNode) micSourceNode.disconnect();
  if (micMediaStream) micMediaStream.getTracks().forEach(track => track.stop());
  if (audioContext) {
    try { await audioContext.close(); } catch {}
    audioContext = null;
  }
  nextAudioPlaybackTime = 0;
  isMicInitialized = false;
  // On stop, allow reconnect
  if (!signalRConnection || signalRConnection.state !== 'Connected') {
    connectStartButton.textContent = 'Connect & Start';
    connectStartButton.disabled = false;
  }
}

// Audio Conversion Utilities
function float32ToInt16PCM(float32Array) {
  // Convert Float32Array [-1, 1] to Int16 PCM format
  const buffer = new ArrayBuffer(float32Array.length * 2);
  const view = new DataView(buffer);
  for (let i = 0; i < float32Array.length; i++) {
    let sample = Math.max(-1, Math.min(1, float32Array[i]));
    // Scale and clamp to 16-bit signed integer range
    view.setInt16(i * 2, sample < 0 ? sample * 0x8000 : sample * 0x7FFF, true);
  }
  return new Uint8Array(buffer);
}

function convertPcmToWav(float32Array, sampleRate) {
  // This function creates a WAV file header and appends PCM16 data
  const pcm16 = float32ToInt16PCM(float32Array);
  const numChannels = 1;
  const byteRate = sampleRate * numChannels * 2;
  const blockAlign = numChannels * 2;
  const buffer = new ArrayBuffer(44 + pcm16.length);
  const view = new DataView(buffer);
  let offset = 0;

  function writeString(str) {
    for (let i = 0; i < str.length; i++) view.setUint8(offset++, str.charCodeAt(i));
  }
  function writeUint32(val) { view.setUint32(offset, val, true); offset += 4; }
  function writeUint16(val) { view.setUint16(offset, val, true); offset += 2; }

  // Write WAV file header (see WAV file format specification)
  writeString('RIFF');
  writeUint32(36 + pcm16.length);
  writeString('WAVE');
  writeString('fmt ');
  writeUint32(16);
  writeUint16(1);
  writeUint16(numChannels);
  writeUint32(sampleRate);
  writeUint32(byteRate);
  writeUint16(blockAlign);
  writeUint16(16);
  writeString('data');
  writeUint32(pcm16.length);
  new Uint8Array(buffer, 44).set(pcm16);
  return buffer;
}

// Audio Playback
function playPcm16AudioChunk(base64Audio) {
  if (!base64Audio) return;
  try {
    if (!audioContext) {
      audioContext = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 16000 });
    }
    if (audioContext.state === 'suspended') {
      audioContext.resume().catch(() => {});
    }

    const audioBytes = base64ToUint8Array(base64Audio);
    if (!audioBytes || audioBytes.length < 2) return;

    // Check for WAV header (RIFF, 44 bytes)
    // If the audio chunk is a WAV file, skip the 44-byte header to get to PCM data
    let pcmOffset = 0;
    if (
      audioBytes.length >= 44 &&
      audioBytes[0] === 0x52 && audioBytes[1] === 0x49 &&
      audioBytes[2] === 0x46 && audioBytes[3] === 0x46
    ) {
      pcmOffset = 44;
    }
    const pcmBytes = audioBytes.subarray(pcmOffset);
    // Each PCM16 sample is 2 bytes
    const sampleCount = (pcmBytes.length / 2) | 0;
    const float32Data = new Float32Array(sampleCount);
    for (let i = 0; i < sampleCount; i++) {
      // Convert 2 bytes (little-endian) to signed 16-bit integer
      const lo = pcmBytes[i * 2];
      const hi = pcmBytes[i * 2 + 1];
      let val = (hi << 8) | lo;
      if (val & 0x8000) val = val - 0x10000; // Convert to signed
      float32Data[i] = Math.max(-1, Math.min(1, val / 32768));
    }

    // Create an AudioBuffer and schedule playback
    const buffer = audioContext.createBuffer(1, sampleCount, 16000);
    buffer.getChannelData(0).set(float32Data);

    const source = audioContext.createBufferSource();
    source.buffer = buffer;
    source.connect(audioContext.destination);

    const now = audioContext.currentTime;
    // Schedule playback to avoid audio glitches if previous audio is still playing
    if (!nextAudioPlaybackTime || nextAudioPlaybackTime < now) {
      nextAudioPlaybackTime = now + 0.02; // small scheduling offset
    }
    source.start(nextAudioPlaybackTime);
    nextAudioPlaybackTime += buffer.duration;

    source.onended = () => {
      // If playback is behind, reset the playback time to current time
      const gap = nextAudioPlaybackTime - audioContext.currentTime;
      if (gap < 0) {
        nextAudioPlaybackTime = audioContext.currentTime;
      }
    };
  } catch (err) {
    console.warn('PCM16 playback failed:', err);
  }
}

// Base64 Utilities
function uint8ToBase64(uint8Arr) {
  // Convert Uint8Array to base64 string in chunks to avoid stack overflow for large arrays
  let binary = '';
  const chunkSize = 0x8000;
  for (let i = 0; i < uint8Arr.length; i += chunkSize) {
    const sub = uint8Arr.subarray(i, i + chunkSize);
    binary += String.fromCharCode.apply(null, sub);
  }
  return btoa(binary);
}

function base64ToUint8Array(base64Str) {
  // Convert base64 string to Uint8Array
  try {
    const binary = atob(base64Str);
    const len = binary.length;
    const bytes = new Uint8Array(len);
    for (let i = 0; i < len; i++) {
      bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
  } catch {
    return new Uint8Array();
  }
}

// VAD Model Prefetch
async function prefetchVadModel() {
  try {
    if (isVadModelReady) return;
    vadModelStatusElement.textContent = 'Downloading VAD model...';
    // Use fetch with cache: 'reload' to force download the model (avoid using cached version)
    const response = await fetch(vadModelUrl, { cache: 'reload' });
    if (!response.ok) throw new Error('Failed to fetch model');
    await response.arrayBuffer();
    vadModelStatusElement.textContent = 'Model downloaded. Initializing...';
  } catch (err) {
    console.warn('Prefetch model failed', err);
  }
}
