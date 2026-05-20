<script setup lang="ts">
import { onMounted, ref, computed } from 'vue';
import { adminApi, type ApiError } from '../../shared/api';

const username = ref<string | null>(null);
const currentPassword = ref('');
const newPassword     = ref('');
const confirmPassword = ref('');
const submitting = ref(false);
const error   = ref<string | null>(null);
const success = ref<string | null>(null);

onMounted(async () => {
  try {
    const me = await adminApi.me();
    username.value = me.principal?.username ?? null;
  } catch {
    /* AppView already redirects to login when /me fails */
  }
});

const strengthIssues = computed<string[]>(() => {
  const pw = newPassword.value;
  const issues: string[] = [];
  if (pw.length < 12) issues.push('At least 12 characters');
  if (!/[A-Z]/.test(pw)) issues.push('At least one uppercase letter');
  if (!/[a-z]/.test(pw)) issues.push('At least one lowercase letter');
  if (!/[0-9]/.test(pw)) issues.push('At least one digit');
  if (!/[^A-Za-z0-9]/.test(pw)) issues.push('At least one symbol');
  return issues;
});

const passwordsMatch = computed(() =>
  newPassword.value.length > 0 && newPassword.value === confirmPassword.value,
);

const canSubmit = computed(() =>
  !submitting.value
  && currentPassword.value.length > 0
  && newPassword.value.length >= 8
  && passwordsMatch.value,
);

async function submit() {
  error.value = null;
  success.value = null;
  if (!passwordsMatch.value) {
    error.value = 'New passwords do not match';
    return;
  }
  submitting.value = true;
  try {
    await adminApi.changePassword(currentPassword.value, newPassword.value);
    success.value = 'Password updated successfully.';
    currentPassword.value = '';
    newPassword.value = '';
    confirmPassword.value = '';
  } catch (err) {
    error.value = (err as ApiError).message ?? 'change failed';
  } finally {
    submitting.value = false;
  }
}
</script>

<template>
  <h1>Profile</h1>

  <div class="card">
    <div class="row">
      <div class="muted">Signed in as</div>
      <div class="value">{{ username ?? '—' }}</div>
    </div>
  </div>

  <h2>Change password</h2>
  <form class="card" @submit.prevent="submit" autocomplete="off">
    <label>Current password
      <input v-model="currentPassword"
             type="password"
             autocomplete="current-password"
             required />
    </label>

    <label>New password
      <input v-model="newPassword"
             type="password"
             autocomplete="new-password"
             minlength="8"
             required />
      <ul v-if="newPassword && strengthIssues.length" class="hints">
        <li v-for="issue in strengthIssues" :key="issue">{{ issue }}</li>
      </ul>
      <div v-else-if="newPassword" class="hint-good">Strong enough.</div>
    </label>

    <label>Confirm new password
      <input v-model="confirmPassword"
             type="password"
             autocomplete="new-password"
             required />
      <div v-if="confirmPassword && !passwordsMatch" class="hint-bad">
        Passwords do not match.
      </div>
    </label>

    <div v-if="error"   class="error-box">{{ error }}</div>
    <div v-if="success" class="success-box">{{ success }}</div>

    <button class="primary" type="submit" :disabled="!canSubmit">
      {{ submitting ? 'Updating…' : 'Update password' }}
    </button>
  </form>
</template>

<style scoped>
h1 { font-size: 22px; margin: 0 0 16px; }
h2 { font-size: 16px; margin: 24px 0 12px; }
.card + .card { margin-top: 12px; }
.row { display: flex; justify-content: space-between; align-items: baseline; }
.value { font-weight: 600; }
form { display: flex; flex-direction: column; gap: 12px; max-width: 420px; }
label { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--color-text-muted); }
.hints {
  margin: 6px 0 0; padding-left: 18px; font-size: 11px;
  color: var(--color-warning, var(--color-text-muted));
}
.hint-good { font-size: 11px; color: var(--color-success, #2e7d32); margin-top: 4px; }
.hint-bad  { font-size: 11px; color: var(--color-danger,  #c62828); margin-top: 4px; }
.success-box {
  background: var(--color-success-bg, #e8f5e9);
  color: var(--color-success, #2e7d32);
  padding: 8px 10px; border-radius: var(--radius); font-size: 12px;
}
</style>
