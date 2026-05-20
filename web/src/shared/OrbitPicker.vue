<script setup lang="ts">
/**
 * <OrbitPicker> — combobox-style selector for a project + model.
 *
 * Loads projects from /api/orbit/projects when the parent supplies a target
 * (`prod` | `dev`); when a project is chosen, loads its models. If the
 * stored ORBIT credentials are missing or rejected, gracefully falls back
 * to free-text Project ID / Model ID inputs so the convert flow still
 * works (matching the legacy 3DConvert behaviour).
 *
 * Emits the selected IDs via v-model:projectId / v-model:modelId / v-model:modelName.
 */
import { computed, ref, watch } from 'vue';
import { orbitApi, type ApiError, type OrbitModel, type OrbitProject } from './api';

const props = defineProps<{
  target:        'prod' | 'dev';
  projectId:     string;
  modelId:       string;
  modelName?:    string;
}>();

const emit = defineEmits<{
  (e: 'update:projectId', v: string): void;
  (e: 'update:modelId',   v: string): void;
  (e: 'update:modelName', v: string): void;
}>();

const projects = ref<OrbitProject[]>([]);
const models   = ref<OrbitModel[]>([]);
const loadingProjects = ref(false);
const loadingModels   = ref(false);
const projectsError   = ref<string | null>(null);
const modelsError     = ref<string | null>(null);

const manual         = ref(false);
const projectFilter  = ref('');
const modelFilter    = ref('');
const projectOpen    = ref(false);
const modelOpen      = ref(false);

const selectedProject = computed(() => projects.value.find((p) => p.id === props.projectId));
const selectedModel   = computed(() => models.value.find((m) => m.id === props.modelId));

const filteredProjects = computed(() => {
  const q = projectFilter.value.trim().toLowerCase();
  if (!q) return projects.value;
  return projects.value.filter((p) =>
    p.name.toLowerCase().includes(q) || p.id.toLowerCase().includes(q));
});

const filteredModels = computed(() => {
  const q = modelFilter.value.trim().toLowerCase();
  if (!q) return models.value;
  return models.value.filter((m) =>
    m.name.toLowerCase().includes(q) || m.id.toLowerCase().includes(q));
});

async function loadProjects() {
  loadingProjects.value = true;
  projectsError.value = null;
  projects.value = [];
  try {
    const r = await orbitApi.projects(props.target);
    projects.value = r.items;
    if (!r.items.length) projectsError.value = 'no projects visible to this token';
  } catch (err) {
    const e = err as ApiError;
    projectsError.value = e.message ?? 'failed to load projects';
    if (e.status === 412) projectsError.value = `ORBIT ${props.target} not configured — set URL + token in admin Settings.`;
  } finally {
    loadingProjects.value = false;
  }
}

async function loadModels(projectId: string) {
  if (!projectId) { models.value = []; return; }
  loadingModels.value = true;
  modelsError.value = null;
  models.value = [];
  try {
    const r = await orbitApi.models(props.target, projectId);
    models.value = r.items;
  } catch (err) {
    modelsError.value = (err as ApiError).message ?? 'failed to load models';
  } finally {
    loadingModels.value = false;
  }
}

function pickProject(p: OrbitProject) {
  emit('update:projectId', p.id);
  emit('update:modelId',   '');
  emit('update:modelName', '');
  projectFilter.value = '';
  projectOpen.value   = false;
}

function pickModel(m: OrbitModel) {
  emit('update:modelId',   m.id);
  emit('update:modelName', m.name);
  modelFilter.value = '';
  modelOpen.value   = false;
}

function openProject() {
  projectOpen.value = true;
  projectFilter.value = '';
}
function closeProjectSoon() {
  window.setTimeout(() => { projectOpen.value = false; }, 200);
}
function onProjectInput(e: Event) {
  projectFilter.value = (e.target as HTMLInputElement).value;
  projectOpen.value = true;
}

function openModel() {
  if (!props.projectId) return;
  modelOpen.value = true;
  modelFilter.value = '';
}
function closeModelSoon() {
  window.setTimeout(() => { modelOpen.value = false; }, 200);
}
function onModelInput(e: Event) {
  modelFilter.value = (e.target as HTMLInputElement).value;
  modelOpen.value = true;
}

// Reload projects whenever target flips (or on mount).
watch(() => props.target, () => { void loadProjects(); }, { immediate: true });

// Reload models whenever the selected project changes.
watch(() => props.projectId, (id) => { void loadModels(id); });

const fallbackAvailable = computed(() => !!projectsError.value && projects.value.length === 0);

// What the input shows: filter text while typing, otherwise selected name (or empty).
const projectInputValue = computed(() => projectOpen.value
  ? projectFilter.value
  : (selectedProject.value?.name ?? ''));
const modelInputValue = computed(() => modelOpen.value
  ? modelFilter.value
  : (selectedModel.value?.name ?? ''));
</script>

<template>
  <div class="picker">
    <!-- ----- Project ----- -->
    <div class="field">
      <div class="lbl-row">
        <label class="lbl">Project</label>
        <button v-if="!manual && projects.length"
                type="button" class="link" @click="manual = true">
          enter ID manually
        </button>
        <button v-else-if="manual" type="button" class="link" @click="manual = false">
          back to project list
        </button>
      </div>

      <!-- Combobox mode -->
      <template v-if="!manual">
        <div class="combo">
          <input
            type="text"
            :placeholder="loadingProjects ? 'loading projects…' : (selectedProject ? selectedProject.name : 'search projects…')"
            :value="projectInputValue"
            :disabled="loadingProjects"
            @focus="openProject"
            @blur="closeProjectSoon"
            @input="onProjectInput"
          />
          <div v-if="projectOpen" class="dropdown">
            <div v-if="!filteredProjects.length" class="empty">
              <template v-if="loadingProjects">loading…</template>
              <template v-else-if="projectFilter">no match for "{{ projectFilter }}"</template>
              <template v-else>no projects available</template>
            </div>
            <div
              v-for="p in filteredProjects.slice(0, 50)"
              :key="p.id"
              class="item"
              @mousedown.prevent="pickProject(p)"
            >
              <div class="item-main">{{ p.name }}</div>
              <div class="item-sub">
                <code>{{ p.id }}</code>
                <span v-if="p.role" class="muted-role">· {{ p.role }}</span>
              </div>
            </div>
          </div>
        </div>
        <div v-if="projectsError" class="hint-bad">
          {{ projectsError }}
          <button v-if="fallbackAvailable" type="button" class="link" @click="manual = true">
            enter manually instead
          </button>
        </div>
      </template>

      <!-- Manual ID mode -->
      <template v-else>
        <input
          type="text"
          :value="projectId"
          placeholder="paste an ORBIT project id (e.g. cf900606f5)"
          @input="emit('update:projectId', ($event.target as HTMLInputElement).value)"
        />
      </template>
    </div>

    <!-- ----- Model ----- -->
    <div class="field">
      <div class="lbl-row">
        <label class="lbl">Model</label>
        <span v-if="!manual && selectedProject && !models.length && !loadingModels && !modelsError" class="muted-role">
          no models in this project yet
        </span>
      </div>

      <template v-if="!manual">
        <div class="combo">
          <input
            type="text"
            :placeholder="!projectId
              ? 'select a project first'
              : (loadingModels ? 'loading models…' : (selectedModel ? selectedModel.name : 'search models…'))"
            :value="modelInputValue"
            :disabled="!projectId || loadingModels"
            @focus="openModel"
            @blur="closeModelSoon"
            @input="onModelInput"
          />
          <div v-if="modelOpen" class="dropdown">
            <div v-if="!filteredModels.length" class="empty">
              <template v-if="loadingModels">loading…</template>
              <template v-else-if="modelFilter">no match for "{{ modelFilter }}"</template>
              <template v-else>no models available</template>
            </div>
            <div
              v-for="m in filteredModels.slice(0, 50)"
              :key="m.id"
              class="item"
              @mousedown.prevent="pickModel(m)"
            >
              <div class="item-main">{{ m.name }}</div>
              <div class="item-sub"><code>{{ m.id }}</code></div>
            </div>
          </div>
        </div>
        <div v-if="modelsError" class="hint-bad">{{ modelsError }}</div>
      </template>

      <template v-else>
        <input
          type="text"
          :value="modelId"
          placeholder="paste an ORBIT model id (e.g. be45d33eb1)"
          @input="emit('update:modelId', ($event.target as HTMLInputElement).value)"
        />
        <input
          type="text"
          :value="modelName ?? ''"
          placeholder="optional display name (e.g. main)"
          @input="emit('update:modelName', ($event.target as HTMLInputElement).value)"
        />
      </template>
    </div>
  </div>
</template>

<style scoped>
.picker { display: flex; flex-direction: column; gap: 12px; }
.field  { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--color-text-muted); }
.lbl    { font-size: 12px; color: var(--color-text-muted); }
.lbl-row { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; }

.combo { position: relative; }
.combo input { width: 100%; }
.dropdown {
  position: absolute; top: calc(100% + 2px); left: 0; right: 0; z-index: 100;
  background: var(--color-bg-elevated, #fafafa);
  border: 1px solid var(--color-border, #e5e7eb);
  border-radius: var(--radius, 8px);
  max-height: 280px; overflow: auto;
  box-shadow: var(--shadow-2, 0 4px 12px rgba(0,0,0,0.08));
}
.item  { padding: 8px 10px; cursor: pointer; }
.item:hover { background: var(--orbit-primary-fade, #fde9df); }
.item-main { font-weight: 500; color: var(--color-text, #111); font-size: 13px; }
.item-sub  { display: flex; gap: 6px; align-items: baseline; font-size: 11px; color: var(--color-text-muted, #6b7280); }
.item-sub code { font-family: var(--font-mono, ui-monospace, monospace); }
.empty { padding: 10px; color: var(--color-text-muted, #6b7280); font-size: 12px; font-style: italic; }

.link {
  background: none; border: none; padding: 0;
  color: var(--orbit-primary, #e06238); cursor: pointer;
  font-size: 11px; text-decoration: underline;
}
.muted-role { color: var(--color-text-muted, #6b7280); font-size: 11px; }
.hint-bad   { color: var(--color-error, #b91c1c); font-size: 11px; margin-top: 4px; }
</style>
