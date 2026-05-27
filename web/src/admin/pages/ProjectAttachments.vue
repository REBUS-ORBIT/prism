<script setup lang="ts">
/**
 * Admin Project Attachments page (Phase J).
 *
 * Lets an operator attach MVR / GDTF lighting-design files to an ORBIT
 * project. The visualiser dispatcher forwards every live attachment as a
 * `ProjectAttachmentRef[]` on `StartVisualisation`; the orchestrator
 * stages them under `stage/{runId}/attachments/` so the MvrGdtfDetector
 * can wire them into the Unreal world via the DMX plugin.
 *
 * Reuses <OrbitPicker> for the project selector (model-id is unused —
 * attachments are per-project, not per-model). Drag-drop is the primary
 * upload affordance; the file input mirrors what convert/Workstations
 * use so keyboard / a11y still works.
 */
import { computed, ref, watch } from 'vue';
import OrbitPicker from '../../shared/OrbitPicker.vue';
import {
  projectAttachmentsApi,
  type ApiError,
  type ProjectAttachment,
} from '../../shared/api';

const target = ref<'prod' | 'dev'>('prod');
const projectId = ref('');
const modelId = ref('');
const modelName = ref('');

const attachments = ref<ProjectAttachment[]>([]);
const loading = ref(false);
const error = ref<string | null>(null);

const uploading = ref(false);
const uploadError = ref<string | null>(null);
const dragOver = ref(false);
const fileInputRef = ref<HTMLInputElement | null>(null);

const ACCEPTED_EXT = ['.mvr', '.gdtf', '.zip'];
const ACCEPTED_MIME = ['application/mvr', 'application/gdtf', 'application/zip', 'application/octet-stream'];
const MAX_BYTES = 50 * 1024 * 1024;

const acceptAttr = computed(() => [...ACCEPTED_EXT, ...ACCEPTED_MIME].join(','));

async function refresh() {
  if (!projectId.value) {
    attachments.value = [];
    return;
  }
  loading.value = true;
  error.value = null;
  try {
    attachments.value = (await projectAttachmentsApi.list(projectId.value)).attachments;
  } catch (err) {
    error.value = (err as ApiError).message ?? 'failed to list attachments';
    attachments.value = [];
  } finally {
    loading.value = false;
  }
}

watch(projectId, () => { void refresh(); });

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1024 / 1024).toFixed(2)} MB`;
}

function extOf(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot === -1 ? '' : name.slice(dot).toLowerCase();
}

function validateFile(file: File): string | null {
  const ext = extOf(file.name);
  if (!ACCEPTED_EXT.includes(ext)) {
    return `Unsupported extension ${ext || '(none)'} — expected one of ${ACCEPTED_EXT.join(', ')}.`;
  }
  if (file.size === 0) return 'File is empty.';
  if (file.size > MAX_BYTES) {
    return `File is ${formatBytes(file.size)}; max is ${formatBytes(MAX_BYTES)}.`;
  }
  return null;
}

async function uploadFile(file: File) {
  if (!projectId.value) { uploadError.value = 'Pick a project first.'; return; }
  const v = validateFile(file);
  if (v) { uploadError.value = v; return; }
  uploadError.value = null;
  uploading.value = true;
  try {
    await projectAttachmentsApi.upload(projectId.value, file);
    await refresh();
  } catch (err) {
    const e = err as ApiError;
    uploadError.value = e.message ?? 'upload failed';
  } finally {
    uploading.value = false;
  }
}

function onFileChosen(ev: Event) {
  const input = ev.target as HTMLInputElement;
  const file = input.files?.[0];
  if (file) void uploadFile(file);
  if (input) input.value = '';
}

function onDrop(ev: DragEvent) {
  ev.preventDefault();
  dragOver.value = false;
  const file = ev.dataTransfer?.files?.[0];
  if (file) void uploadFile(file);
}

function onDragOver(ev: DragEvent) {
  ev.preventDefault();
  dragOver.value = true;
}

function onDragLeave() {
  dragOver.value = false;
}

async function removeAttachment(att: ProjectAttachment) {
  if (!confirm(`Delete attachment "${att.filename}"? Active visualiser runs are unaffected; new runs will no longer receive it.`)) return;
  try {
    await projectAttachmentsApi.remove(att.projectId, att.id);
    await refresh();
  } catch (err) {
    error.value = (err as ApiError).message ?? 'delete failed';
  }
}

function downloadHref(att: ProjectAttachment): string {
  return projectAttachmentsApi.downloadUrl(att.projectId, att.id);
}
</script>

<template>
  <section>
    <header class="page-head">
      <div>
        <h1>Project attachments</h1>
        <p class="muted">
          Upload <code>.mvr</code> / <code>.gdtf</code> lighting-design files to an ORBIT project.
          The visualiser orchestrator stages every live attachment alongside the converted glTF
          and imports them into the Unreal world via the DMX plugin (Phase J).
        </p>
      </div>
    </header>

    <div class="card mt">
      <label class="form-row">
        <span>ORBIT target</span>
        <select v-model="target">
          <option value="prod">prod</option>
          <option value="dev">dev</option>
        </select>
      </label>

      <OrbitPicker
        :target="target"
        :project-id="projectId"
        :model-id="modelId"
        :model-name="modelName"
        @update:projectId="projectId = $event"
        @update:modelId="modelId = $event"
        @update:modelName="modelName = $event"
      />
    </div>

    <div v-if="projectId" class="mt">
      <div
        :class="['dropzone', { active: dragOver, disabled: uploading }]"
        @dragover="onDragOver"
        @dragleave="onDragLeave"
        @drop="onDrop"
        @click="fileInputRef?.click()"
      >
        <input
          ref="fileInputRef"
          type="file"
          :accept="acceptAttr"
          style="display: none;"
          @change="onFileChosen"
        />
        <strong v-if="uploading">Uploading…</strong>
        <strong v-else>Drop an MVR / GDTF file here, or click to choose</strong>
        <p class="muted small">
          Max {{ formatBytes(MAX_BYTES) }}. Accepted: {{ ACCEPTED_EXT.join(', ') }}.
        </p>
      </div>
      <div v-if="uploadError" class="alert err mt-sm">{{ uploadError }}</div>
    </div>

    <div v-if="error" class="alert err mt">{{ error }}</div>

    <div v-if="projectId" class="card mt">
      <table class="table">
        <thead>
          <tr>
            <th>Filename</th>
            <th>Content type</th>
            <th>Size</th>
            <th>Uploaded</th>
            <th>Uploaded by</th>
            <th class="row-actions"></th>
          </tr>
        </thead>
        <tbody>
          <tr v-if="loading"><td colspan="6" class="muted" style="text-align:center; padding: 16px;">Loading…</td></tr>
          <tr v-else-if="!attachments.length"><td colspan="6" class="muted" style="text-align:center; padding: 24px;">
            No attachments for this project yet.
          </td></tr>
          <tr v-for="a in attachments" :key="a.id">
            <td><code>{{ a.filename }}</code></td>
            <td><code class="muted">{{ a.contentType }}</code></td>
            <td>{{ formatBytes(a.sizeBytes) }}</td>
            <td>{{ new Date(a.uploadedAt).toLocaleString() }}</td>
            <td>
              <span v-if="a.uploadedByApiKeyId" class="muted mono">{{ a.uploadedByApiKeyId.slice(0, 8) }}</span>
              <span v-else class="muted">admin / session</span>
            </td>
            <td class="row-actions">
              <a :href="downloadHref(a)" class="btn btn-sm" target="_blank" rel="noopener">Download</a>
              <button class="btn btn-sm btn-danger" @click="removeAttachment(a)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <p v-else class="muted mt">Pick a project above to upload or view its lighting attachments.</p>
  </section>
</template>

<style scoped>
.page-head { display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px; }
.page-head p { margin: 4px 0 0; font-size: 13px; max-width: 720px; }

.muted { color: var(--color-text-muted); }
.small { font-size: 12px; }
.mono  { font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace); }

.mt    { margin-top: 16px; }
.mt-sm { margin-top: 8px; }

.card {
  background: var(--color-bg-elevated);
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  padding: 16px;
  display: flex; flex-direction: column; gap: 12px;
}

.form-row { display: flex; flex-direction: column; gap: 4px; font-size: 13px; }
.form-row select {
  padding: 6px 8px; border-radius: var(--radius);
  border: 1px solid var(--color-border); background: var(--color-bg); color: var(--color-text);
  font-size: 13px; max-width: 200px;
}

.dropzone {
  border: 2px dashed var(--color-border);
  border-radius: var(--radius);
  padding: 32px 16px;
  text-align: center;
  cursor: pointer;
  transition: background 80ms, border-color 80ms;
}
.dropzone:hover { background: var(--color-bg-elevated); }
.dropzone.active { border-color: var(--orbit-primary); background: var(--orbit-primary-fade, var(--color-bg-elevated)); }
.dropzone.disabled { opacity: 0.65; cursor: progress; }
.dropzone p { margin: 6px 0 0; }

.alert.err {
  border: 1px solid var(--color-danger, #c33);
  background: var(--color-danger-fade, rgba(204,51,51,0.08));
  padding: 8px 12px; border-radius: var(--radius);
}

.table { width: 100%; border-collapse: collapse; }
.table th, .table td {
  text-align: left; padding: 6px 8px;
  border-bottom: 1px solid var(--color-border);
  font-size: 13px; vertical-align: middle;
}
.row-actions { white-space: nowrap; }
.row-actions .btn + .btn { margin-left: 4px; }

.btn {
  padding: 4px 10px; border-radius: var(--radius); cursor: pointer;
  border: 1px solid var(--color-border); background: var(--color-bg-elevated); color: var(--color-text);
  font-size: 13px; text-decoration: none; display: inline-block;
}
.btn:hover:not(:disabled) { background: var(--color-bg); }
.btn-sm { padding: 3px 8px; font-size: 12px; }
.btn-danger { color: var(--color-danger, #c33); }
</style>
