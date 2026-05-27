<script setup lang="ts">
/**
 * Real Pixel Streaming player (Phase I — replaces Phase G's iframe shim).
 *
 * Uses Epic's official `@epicgames-ps/lib-pixelstreamingfrontend-ue5.5` NPM
 * package (locked to v1.2.5 in package.json — the latest stable on npm at
 * the time of Phase I, and the closest published frontend lib to the UE 5.7
 * artist template targeted by Phase D).
 *
 * Decision matrix (see plan §1 — "which Pixel Streaming frontend to use"):
 *
 *   Option A  npm @epicgames-ps/lib-pixelstreamingfrontend-ue5.5  ← picked
 *   Option B  hand-rolled RTCPeerConnection + signalling client
 *   Option C  iframe at Cirrus's static `/player.html`
 *
 * Option A is API-compatible with UE 5.5 → 5.7 streamers per Epic's release
 * notes; the wire protocol between the lib and Cirrus is fixed for the
 * `4.27` / `5.x` Pixel Streaming generation. Option C was Phase G's stop-
 * gap — it could not work cross-origin once signalling moved behind the
 * PRISM proxy. Option B is preserved as a fallback if Epic ever ships a
 * lib version that breaks against UE 5.7 — at that point we'd vendor the
 * `Frontend/library/` from `EpicGamesExt/PixelStreamingInfrastructure`
 * (UE5.7 branch) directly under `web/src/admin/vendor/`.
 *
 * Architecture
 * ------------
 *   browser  ⇄  PRISM signalling proxy ws://…/signalling?token=<jwt>
 *               ⇄ agent SignallingBridge ⇄ local Cirrus ⇄ UE Pixel Streaming
 *
 * The lib expects a direct ws:// to Cirrus. PRISM proxies that transparently
 * (`server/src/ws/signallingProxy.ts`), authenticated via a short-lived JWT
 * minted by `POST /api/visualiser/streams/:runId/signalling-token`. We
 * inject a fresh token on each (re)connect via `setSignallingUrlBuilder`,
 * so a reconnect after a token expiry still grabs a usable URL.
 *
 * TURN
 * ----
 * Cirrus normally publishes ICE servers via its `config` message; in dev
 * the agent's local Cirrus knows only public STUN. To inject the PRISM-
 * minted TURN bundle (see `server/src/visualiser/turnCredentials.ts`) we
 * hook `WebRtcPlayerController.handleOnConfigMessage` and merge our turn
 * servers into the `peerConnectionOptions.iceServers` list before the
 * RTCPeerConnection is created. This is the lib's public surface — the
 * controller field is exposed on `psInstance.webRtcController`.
 */
import { onBeforeUnmount, onMounted, ref } from 'vue';
import {
  Config,
  Flags,
  PixelStreaming,
  TextParameters,
  type WebRtcDisconnectedEvent,
  type PlayStreamErrorEvent,
} from '@epicgames-ps/lib-pixelstreamingfrontend-ue5.5';
import { visualiserApi, type VisualiserTurnBundle } from '../../shared/api';

const props = defineProps<{
  runId: string;
  signallingUrl: string;
  turn?: VisualiserTurnBundle | null;
}>();

type Status = 'idle' | 'connecting' | 'streaming' | 'failed';

const containerRef = ref<HTMLDivElement | null>(null);
const status = ref<Status>('idle');
const error = ref<string | null>(null);

let psInstance: PixelStreaming | null = null;

/**
 * Build the signalling URL with a freshly-minted token. Called on first
 * connect and re-invoked by the lib on every reconnect, so a 5-minute
 * token expiry over a longer session is handled transparently.
 */
async function buildSignallingUrlAsync(): Promise<string> {
  const { token } = await visualiserApi.signallingToken(props.runId);
  const sep = props.signallingUrl.includes('?') ? '&' : '?';
  return `${props.signallingUrl}${sep}token=${encodeURIComponent(token)}`;
}

onMounted(async () => {
  if (!containerRef.value) return;
  try {
    status.value = 'connecting';

    // Prime the URL synchronously by fetching a token first — the lib's
    // `setSignallingUrlBuilder` callback is sync, so we cache the most
    // recent URL into a closure variable and refresh it before each
    // reconnect.
    let cachedUrl = await buildSignallingUrlAsync();

    const config = new Config({
      initialSettings: {
        // The PS lib's TextParameters.SignallingServerUrl ("ss") is what
        // gets surfaced into the URL in the settings panel; we override
        // dynamically below via setSignallingUrlBuilder.
        ss: cachedUrl,
        AutoConnect: true,
        AutoPlayVideo: true,
        StartVideoMuted: false,
        // Input wiring — keyboard + mouse forward back to UE so the
        // operator can drive the viewport. Touch + gamepad on for free.
        KeyboardInput: true,
        MouseInput: true,
        TouchInput: true,
        GamepadInput: true,
        // Hide the lib's built-in settings/info overlay — we render our
        // own minimal status chrome around it.
        HideUI: true,
      },
    });

    psInstance = new PixelStreaming(config, { videoElementParent: containerRef.value });

    // Refresh the JWT on every (re)connect. Falls back to the cached URL
    // when the API call fails — better to attempt a connect with a stale
    // token than to silently stop trying entirely (the lib's
    // disconnect/reconnect loop will surface the rejection).
    psInstance.setSignallingUrlBuilder(() => cachedUrl);
    // Kick off background refresh of the cached URL so subsequent
    // reconnects pick up a fresh token. Best-effort — failures here are
    // logged and ignored.
    void (async () => {
      try { cachedUrl = await buildSignallingUrlAsync(); } catch { /* ignore */ }
    })();

    // Inject TURN credentials into the Cirrus `config` message before
    // the RTCPeerConnection is created. The lib's WebRtcPlayerController
    // exposes the handler as `handleOnConfigMessage`; we monkey-patch it
    // with a closure that augments the iceServers array. This is the
    // documented public surface for ICE-server overrides — the field is
    // declared in WebRtcPlayerController.d.ts.
    const turn = props.turn;
    if (turn?.urls?.length) {
      const ctrl = psInstance.webRtcController as unknown as {
        handleOnConfigMessage: (m: { peerConnectionOptions?: RTCConfiguration }) => void;
      };
      const original = ctrl.handleOnConfigMessage.bind(ctrl);
      ctrl.handleOnConfigMessage = (messageConfig) => {
        const existing = messageConfig.peerConnectionOptions?.iceServers ?? [];
        messageConfig.peerConnectionOptions = {
          ...(messageConfig.peerConnectionOptions ?? {}),
          iceServers: [
            ...existing,
            {
              urls: turn.urls,
              username: turn.username,
              credential: turn.credential,
            },
          ],
        };
        original(messageConfig);
      };
    }

    psInstance.addEventListener('webRtcConnecting',   () => { status.value = 'connecting'; });
    psInstance.addEventListener('webRtcConnected',    () => { status.value = 'streaming'; error.value = null; });
    psInstance.addEventListener('webRtcDisconnected', (ev: Event & WebRtcDisconnectedEvent) => {
      status.value = 'idle';
      // Surface the disconnect reason for diagnostics; the lib's
      // auto-reconnect will retry if `allowClickToReconnect` was true.
      if (ev.data?.eventString) error.value = ev.data.eventString;
    });
    psInstance.addEventListener('webRtcFailed',       () => { status.value = 'failed'; error.value ??= 'WebRTC connection failed'; });
    psInstance.addEventListener('playStreamError',    (ev: Event & PlayStreamErrorEvent) => {
      status.value = 'failed';
      error.value = ev.data?.message ?? 'stream play error';
    });

    // With AutoConnect on the lib will dial out immediately; but on some
    // browsers AutoConnect needs a manual nudge once the user has
    // interacted with the page. Calling .connect() here is idempotent
    // when AutoConnect already fired.
    if (!config.isFlagEnabled(Flags.AutoConnect)) psInstance.connect();
    // Silence unused warning for TextParameters — kept as a hint to the
    // reader that the `ss` key in initialSettings maps to this enum.
    void TextParameters.SignallingServerUrl;
  } catch (ex: unknown) {
    error.value = ex instanceof Error ? ex.message : String(ex);
    status.value = 'failed';
  }
});

onBeforeUnmount(() => {
  try { psInstance?.disconnect(); } catch { /* ignore */ }
  psInstance = null;
});
</script>

<template>
  <div class="ps-player">
    <div ref="containerRef" class="ps-canvas" />
    <div v-if="status !== 'streaming'" class="ps-overlay">
      <div v-if="status === 'connecting'" class="ps-status">
        Connecting to workstation…
      </div>
      <div v-else-if="status === 'failed'" class="ps-status ps-error">
        {{ error ?? 'Pixel Streaming failed' }}
      </div>
      <div v-else class="ps-status muted">
        {{ error ?? 'Idle' }}
      </div>
    </div>
  </div>
</template>

<style scoped>
.ps-player {
  position: relative;
  width: 100%;
  height: 100%;
  min-height: 480px;
  background: var(--color-bg-input);
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  overflow: hidden;
}
.ps-canvas {
  width: 100%;
  height: 100%;
  background: #000;
}
.ps-canvas :deep(video) {
  width: 100%;
  height: 100%;
  object-fit: contain;
  background: #000;
}
.ps-overlay {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(0, 0, 0, 0.55);
  color: #fff;
  pointer-events: none;
}
.ps-status {
  font-weight: 600;
  font-size: 13px;
  text-align: center;
  padding: 12px 18px;
  border-radius: var(--radius);
  background: rgba(0, 0, 0, 0.4);
  max-width: 80%;
}
.ps-error {
  color: var(--color-error);
  font-family: var(--font-mono);
  font-size: 12px;
}
.muted { color: var(--color-text-muted); }
</style>
